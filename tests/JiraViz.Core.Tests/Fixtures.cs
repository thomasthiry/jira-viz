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
        EpicLinkKey = epicLink,
        ParentKey = parent,
    };

    public static StoryGroup Story(
        string key, string categoryKey, double? points = null, DateTimeOffset? updated = null)
        => new() { Story = Issue(key, categoryKey, points, updated: updated) };

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
