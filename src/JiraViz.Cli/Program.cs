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

    Console.WriteLine($"Fetching issues for: {options.Jql}");
    // A plain synchronous IProgress, so the last callback cannot land after the summary line.
    var progress = new SynchronousProgress<int>(count => Console.Write($"\r  fetched {count} issues ..."));
    var issues = await client.SearchAsync(options.Jql, fields, progress, ct);
    Console.WriteLine($"\r  fetched {issues.Count} issues.   ");

    if (issues.Count == 0)
    {
        Console.Error.WriteLine("The JQL matched no issues, so there is nothing to report.");
        return 2;
    }

    var (groups, warnings) = new HierarchyBuilder(options.EpicIssueTypeName).Build(issues);
    var bucketer = new StatusBucketer(options.StatusOverrides);

    var model = new ProgressCalculator(bucketer, options.StalledDays)
        .Build(groups, warnings, options.BaseUrl, options.Jql, DateTimeOffset.Now);

    await ReportWriter.WriteAsync(model, options.OutputPath, ct);
    var fullPath = Path.GetFullPath(options.OutputPath);

    stopwatch.Stop();
    Console.WriteLine();
    Console.WriteLine($"  {model.Totals.EpicCount} epics, {model.Totals.IssueCount} issues");
    Console.WriteLine($"  {model.Totals.Completion * 100:0}% complete by {(model.CountBasedSizing ? "issue count" : "story points")}"
                      + $" ({model.Totals.DoneSize:0.#} of {model.Totals.Size:0.#})");
    Console.WriteLine($"  {model.Totals.EpicsNotStarted} epic(s) not started, {model.Totals.StalledCount} stalled issue(s)");

    foreach (var warning in model.Warnings) Console.WriteLine($"  ! {warning}");

    Console.WriteLine();
    Console.WriteLine($"Wrote {fullPath} in {stopwatch.Elapsed.TotalSeconds:0.0}s");

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

