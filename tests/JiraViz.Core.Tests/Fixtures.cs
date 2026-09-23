using JiraViz.Core.Analysis;
using JiraViz.Core.Model;

namespace JiraViz.Core.Tests;

/// <summary>Terse builders so each test reads as the scenario it describes.</summary>
internal static class Fixtures
{
    public const string ToDo = "new";
    public const string InProgress = "indeterminate";
    public const string Done = "done";

    private static readonly DateTimeOffset DefaultUpdated = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);

    public static RawIssue Issue(
        string key,
        string categoryKey,
        double? points = null,
        bool isSubtask = false,
        DateTimeOffset? updated = null,
        DateTimeOffset? resolved = null,
        string? epicLink = null,
        string? parent = null,
        string? type = null) => new()
    {
        Key = key,
        Summary = key + " summary",
        StatusName = categoryKey switch
        {
            Done => "Done",
            InProgress => "In Progress",
            _ => "To Do",
        },
        StatusCategoryKey = categoryKey,
        IssueTypeName = type ?? (isSubtask ? "Sub-task" : "Story"),
        IsSubtask = isSubtask,
        StoryPoints = points,
        Updated = updated ?? DefaultUpdated,
        Resolved = resolved,
        EpicLinkKey = epicLink,
        ParentKey = parent,
    };

    public static StoryGroup Story(
        string key,
        string categoryKey,
        double? points = null,
        DateTimeOffset? updated = null,
        DateTimeOffset? resolved = null)
        => new() { Story = Issue(key, categoryKey, points, updated: updated, resolved: resolved) };

    /// <summary>A story that finished on a given date, sized in points.</summary>
    public static StoryGroup Finished(string key, DateTimeOffset resolved, double points)
        => Story(key, Done, points, resolved: resolved);

    public static EpicGroup Epic(string key, string summary, params StoryGroup[] stories)
    {
        var epic = new EpicGroup
        {
            Key = key,
            Summary = summary,
            StatusName = "In Progress",
            StatusCategoryKey = InProgress,
            IsSynthetic = false,
        };
        epic.Stories.AddRange(stories);
        return epic;
    }
}
