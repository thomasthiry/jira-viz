using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using JiraViz.StubServer;

// A local stand-in for an enterprise Jira Server instance, so the console app's real HTTP,
// auth, field-discovery and pagination paths can be exercised without access to production.
// Only the endpoints JiraViz actually calls are implemented.

var prefix = args.FirstOrDefault(a => a.StartsWith("--url=", StringComparison.Ordinal))?["--url=".Length..]
             ?? "http://localhost:5252/";
if (!prefix.EndsWith('/')) prefix += "/";

var seedArg = args.FirstOrDefault(a => a.StartsWith("--seed=", StringComparison.Ordinal))?["--seed=".Length..];
var seed = int.TryParse(seedArg, out var parsedSeed) ? parsedSeed : 20260904;

var generator = new ProjectGenerator(seed);
var allIssues = generator.Issues;

Console.WriteLine($"Stub Jira listening on {prefix}");
Console.WriteLine($"  seed={seed}  issues={allIssues.Count}");
Console.WriteLine($"  Story Points -> {ProjectGenerator.StoryPointsFieldId}");
Console.WriteLine($"  Epic Link    -> {ProjectGenerator.EpicLinkFieldId}");
Console.WriteLine("Press Ctrl+C to stop.");

using var listener = new HttpListener();
listener.Prefixes.Add(prefix);

try
{
    listener.Start();
}
catch (HttpListenerException ex)
{
    Console.Error.WriteLine($"Could not listen on {prefix}: {ex.Message}");
    Console.Error.WriteLine("On Windows a non-localhost prefix needs an netsh urlacl reservation.");
    return 1;
}

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); listener.Stop(); };

while (!shutdown.IsCancellationRequested)
{
    HttpListenerContext context;
    try
    {
        context = await listener.GetContextAsync();
    }
    catch (Exception) when (shutdown.IsCancellationRequested)
    {
        break;
    }

    _ = Task.Run(() => HandleAsync(context));
}

Console.WriteLine("Stopped.");
return 0;

async Task HandleAsync(HttpListenerContext context)
{
    var request = context.Request;
    var path = request.Url?.AbsolutePath.TrimEnd('/') ?? "";

    try
    {
        switch (path)
        {
            case "/rest/api/2/myself":
                await WriteJsonAsync(context, new JsonObject
                {
                    ["displayName"] = "Stub User",
                    ["name"] = "stub",
                    ["emailAddress"] = "stub@example.invalid",
                });
                break;

            case "/rest/api/2/field":
                await WriteJsonAsync(context, ProjectGenerator.FieldCatalogue());
                break;

            case "/rest/api/2/search":
                await HandleSearchAsync(context);
                break;

            default:
                context.Response.StatusCode = 404;
                await WriteJsonAsync(context, new JsonObject
                {
                    ["errorMessages"] = new JsonArray($"No stub route for {path}"),
                });
                break;
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"  ! {path}: {ex.Message}");
        try
        {
            context.Response.StatusCode = 500;
            await WriteJsonAsync(context, new JsonObject { ["errorMessages"] = new JsonArray(ex.Message) });
        }
        catch { /* client already gone */ }
    }
}

async Task HandleSearchAsync(HttpListenerContext context)
{
    var request = context.Request;
    var startAt = 0;
    var maxResults = 50;
    var jql = "";

    if (request.HttpMethod == "POST")
    {
        using var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8);
        var body = await reader.ReadToEndAsync();
        var parsed = JsonNode.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body)!.AsObject();

        startAt = (int?)parsed["startAt"] ?? 0;
        maxResults = (int?)parsed["maxResults"] ?? 50;
        jql = (string?)parsed["jql"] ?? "";
    }
    else
    {
        var query = request.QueryString;
        int.TryParse(query["startAt"], out startAt);
        if (!int.TryParse(query["maxResults"], out maxResults)) maxResults = 50;
        jql = query["jql"] ?? "";
    }

    // Jira caps the page size server-side; mirroring that is what makes a client's pagination
    // loop actually run more than once against this stub.
    maxResults = Math.Clamp(maxResults, 1, 100);
    startAt = Math.Max(0, startAt);

    var matched = Filter(allIssues, jql);
    var page = matched.Skip(startAt).Take(maxResults).ToList();

    Console.WriteLine($"  search jql='{Shorten(jql)}' startAt={startAt} max={maxResults} -> {page.Count}/{matched.Count}");

    await WriteJsonAsync(context, new JsonObject
    {
        ["startAt"] = startAt,
        ["maxResults"] = maxResults,
        ["total"] = matched.Count,
        // Each issue is deep-cloned because a JsonNode cannot be attached to two parents.
        ["issues"] = new JsonArray(page.Select(i => (JsonNode)i.DeepClone()).ToArray()),
    });
}

// Just enough JQL to exercise layered views locally: a project clause plus the two field kinds
// a milestone is most likely to be modelled with. Parentheses are ignored, so composed queries
// such as "(project = DEMO) AND (fixVersion = \"24.2\")" parse. Unrecognised clauses are
// ignored rather than rejected, which keeps the stub permissive while staying useful.
static List<JsonObject> Filter(List<JsonObject> issues, string jql)
{
    // "key in (A, B)" is a lookup by key and ignores every other clause, which is exactly what
    // the hierarchy completion pass needs.
    var keys = KeyList(jql);
    if (keys is not null)
        return issues.Where(i => keys.Contains((string?)i["key"] ?? "", StringComparer.OrdinalIgnoreCase)).ToList();

    var result = issues;

    var project = Clause(jql, "project");
    if (project is not null)
        result = result
            .Where(i => ((string?)i["key"])?.StartsWith(project + "-", StringComparison.OrdinalIgnoreCase) == true)
            .ToList();

    var version = Clause(jql, "fixVersion");
    if (version is not null)
        result = result.Where(i => ArrayOf(i, "fixVersions", "name").Contains(version, StringComparer.OrdinalIgnoreCase)).ToList();

    var label = Clause(jql, "labels");
    if (label is not null)
        result = result.Where(i => ArrayOf(i, "labels", null).Contains(label, StringComparer.OrdinalIgnoreCase)).ToList();

    return result;

    static HashSet<string>? KeyList(string jql)
    {
        var m = System.Text.RegularExpressions.Regex.Match(
            jql, @"key\s+in\s*\(([^)]*)\)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (!m.Success) return null;

        return m.Groups[1].Value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(k => k.Trim('"'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    static string? Clause(string jql, string field)
    {
        var m = System.Text.RegularExpressions.Regex.Match(
            jql, field + @"\s*=\s*(?:""([^""]*)""|([A-Za-z0-9_.\-]+))",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (!m.Success) return null;
        return m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
    }

    // Reads a field that is either an array of strings (labels) or of objects (fixVersions).
    static List<string> ArrayOf(JsonObject issue, string field, string? property)
    {
        var values = new List<string>();
        if (issue["fields"]?[field] is not JsonArray array) return values;

        foreach (var item in array)
        {
            var value = property is null ? (string?)item : (string?)item?[property];
            if (value is not null) values.Add(value);
        }
        return values;
    }
}

static string Shorten(string value) => value.Length <= 60 ? value : value[..60] + "...";

static async Task WriteJsonAsync(HttpListenerContext context, JsonNode payload)
{
    var bytes = Encoding.UTF8.GetBytes(payload.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
    context.Response.ContentType = "application/json;charset=UTF-8";
    context.Response.ContentLength64 = bytes.Length;
    await context.Response.OutputStream.WriteAsync(bytes);
    context.Response.Close();
}
