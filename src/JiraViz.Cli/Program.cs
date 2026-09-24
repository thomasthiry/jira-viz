using System.Diagnostics;
using JiraViz.Cli;
using JiraViz.Core;
using JiraViz.Core.Analysis;
using JiraViz.Core.Jira;
using JiraViz.Core.Model;
using JiraViz.Core.Reporting;

if (args.Contains("--help") || args.Contains("-h") || args.Length == 0)
{
    Console.WriteLine(OptionsLoader.Usage);
    return args.Length == 0 ? 1 : 0;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
var ct = cancellation.Token;

try
{
    var options = OptionsLoader.Load(args);
    options.Validate();

    if (string.IsNullOrWhiteSpace(options.Token))
        Console.WriteLine($"! No token supplied; proceeding anonymously. Set {OptionsLoader.TokenEnvVar} if the instance requires auth.");

    using var client = new JiraServerClient(options);

    var stopwatch = Stopwatch.StartNew();

    Console.WriteLine($"Connecting to {options.BaseUrl} ...");
    var who = await client.WhoAmIAsync(ct);
    Console.WriteLine($"  authenticated as {who}");

    Console.WriteLine("Resolving custom fields ...");
    var fields = await client.ResolveFieldsAsync(ct);
    Report("Story Points", options.StoryPointsFieldName, fields.StoryPointsFieldId);
    Report("Epic Link", options.EpicLinkFieldName, fields.EpicLinkFieldId);

    // The base query is always the first view; the configured ones layer onto it.
    var requested = new List<(string Name, string Jql)> { (options.DefaultViewName.Trim(), options.Jql) };
    foreach (var v in options.Views)
        requested.Add((v.Name.Trim(), JqlComposer.Compose(options.Jql, v.Jql)));

    var bucketer = new StatusBucketer(options.StatusOverrides);
    var generatedAt = DateTimeOffset.Now;
    var views = new List<ReportView>();

    // Fixed by the first (base) view and reused by the rest, so unestimated work is worth the
    // same everywhere and the milestone views stay comparable with the whole project.
    double? sharedImputedPoints = null;

    // Taken from the base view only, and never from a milestone: a milestone's own rate is a
    // share of the team's output, not the team's output, and treating one as the other would
    // hand every milestone the whole team and commit that capacity several times over.
    double? sharedProjectVelocity = null;

    foreach (var (name, jql) in requested)
    {
        var isBaseView = views.Count == 0;

        Console.WriteLine();
        Console.WriteLine($"[{name}] {jql}");

        // A plain synchronous IProgress, so the last callback cannot land after the summary line.
        var progress = new SynchronousProgress<int>(c => Console.Write($"\r  fetched {c} issues ..."));
        var issues = (IReadOnlyList<RawIssue>)await client.SearchAsync(jql, fields, progress, ct);
        Console.WriteLine($"\r  fetched {issues.Count} issues.   ");

        // A filtered view returns stories without their epics, so the hierarchy is completed
        // before analysis; otherwise every story falls into the synthetic "(no epic)" bucket.
        var matched = issues.Count;
        issues = await HierarchyCompleter.CompleteAsync(client, fields, issues, ct);
        if (issues.Count > matched)
            Console.WriteLine($"  pulled in {issues.Count - matched} ancestor(s) to complete the hierarchy");

        var (groups, warnings) = new HierarchyBuilder(options.EpicIssueTypeName).Build(issues);
        var model = new ProgressCalculator(
                bucketer, options.StalledDays, sharedImputedPoints, options.Forecast, sharedProjectVelocity)
            .Build(groups, warnings, options.BaseUrl, jql, generatedAt);

        if (sharedImputedPoints is null && model.ImputedPoints is not null)
        {
            sharedImputedPoints = model.ImputedPoints;
            Console.WriteLine($"  unestimated stories counted as {model.ImputedPoints:0.#} pts"
                              + " (project-wide average of the estimated ones)");
        }

        if (isBaseView && model.Forecast.MeanWeeklyThroughput > 0)
            sharedProjectVelocity = model.Forecast.MeanWeeklyThroughput;

        views.Add(new ReportView { Name = name, Jql = jql, Model = model });

        // An empty view is reported, not fatal: a milestone with no open work left is a good
        // outcome, and it must not take the other views down with it.
        if (issues.Count == 0)
        {
            Console.WriteLine("  (nothing matches this view)");
            continue;
        }

        Console.WriteLine($"  {model.Totals.EpicCount} epics, {model.Totals.IssueCount} issues, "
                          + $"{model.Totals.Completion * 100:0}% complete "
                          + $"({model.Totals.DoneSize:0.#} of {model.Totals.Size:0.#}"
                          + $" {(model.CountBasedSizing ? "issues" : "pts")})");
        Console.WriteLine($"  {model.Totals.EpicsNotStarted} epic(s) not started, "
                          + $"{model.Totals.StalledCount} stalled issue(s)");
        ReportForecast(model);

        foreach (var warning in model.Warnings) Console.WriteLine($"  ! {warning}");
    }

    if (views.All(v => v.Model.Totals.IssueCount == 0))
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine("No view matched any issues, so there is nothing to report.");
        return 2;
    }

    var document = new ReportDocument
    {
        ProjectName = options.ProjectName,
        GeneratedAt = generatedAt,
        Views = views,
    };

    await ReportWriter.WriteAsync(document, options.OutputPath, ct);
    var fullPath = Path.GetFullPath(options.OutputPath);

    stopwatch.Stop();
    Console.WriteLine();
    Console.WriteLine($"Wrote {fullPath} ({views.Count} view{(views.Count == 1 ? "" : "s")})"
                      + $" in {stopwatch.Elapsed.TotalSeconds:0.0}s");

    if (options.OpenWhenDone) Open(fullPath);
    return 0;
}
catch (JiraVizConfigurationException ex)
{
    Console.Error.WriteLine("Configuration problem:");
    Console.Error.WriteLine(ex.Message);
    return 1;
}
catch (JiraException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 3;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Cancelled.");
    return 130;
}

static void ReportForecast(ReportModel model)
{
    var forecast = model.Forecast;

    // Someone who passed --no-forecast does not need telling once per view that it is off.
    if (forecast.Suppressed) return;

    var unit = model.CountBasedSizing ? "issues" : "pts";

    // Printed whether or not a date came out, since a view too thin to carry one is exactly
    // where knowing the size of the remainder is worth most.
    if (forecast.TeamWeeksRemaining is { } teamWeeks)
        Console.WriteLine($"  {teamWeeks:0.#} team-week(s) of work left at the project's "
                          + $"{forecast.ProjectVelocity:0.#} {unit}/week");

    if (!forecast.Available)
    {
        if (forecast.UnavailableReason is not null) Console.WriteLine($"  no projection: {forecast.UnavailableReason}");
        return;
    }

    Console.WriteLine($"  {forecast.MeanWeeklyThroughput:0.#} {unit}/week over the last "
                      + $"{forecast.Samples.Count} week(s); {forecast.RemainingSize:0.#} {unit} left");

    Console.WriteLine(forecast.P85Date is null
        ? $"  100% is further out than this report projects ({forecast.BeyondHorizonShare:P0} of runs unfinished)"
        : $"  100% by {forecast.P50Date:yyyy-MM-dd} at even odds, {forecast.P85Date:yyyy-MM-dd} to be safe");
}

static void Report(string label, string searchedName, string? resolvedId)
{
    Console.WriteLine(resolvedId is null
        ? $"  ! {label} ('{searchedName}') not found on this instance; continuing without it."
        : $"  {label} -> {resolvedId}");
}

static void Open(string path)
{
    try
    {
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Could not open the report automatically: {ex.Message}");
    }
}

/// <summary>
/// Reports progress on the calling thread. The built-in Progress&lt;T&gt; posts to the
/// synchronization context, which lets a final callback arrive after the summary has printed.
/// </summary>
internal sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}

