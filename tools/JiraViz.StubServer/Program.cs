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

// Just enough JQL to be useful locally: a project term is honoured and everything else ignored.
static List<JsonObject> Filter(List<JsonObject> issues, string jql)
{
    var match = System.Text.RegularExpressions.Regex.Match(
        jql, @"project\s*=\s*""?([A-Za-z][A-Za-z0-9_]*)""?",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    if (!match.Success) return issues;

    var project = match.Groups[1].Value;
    return issues
        .Where(i => ((string?)i["key"])?.StartsWith(project + "-", StringComparison.OrdinalIgnoreCase) == true)
        .ToList();
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
