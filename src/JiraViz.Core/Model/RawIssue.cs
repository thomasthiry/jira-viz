namespace JiraViz.Core.Model;

/// <summary>
/// A Jira issue flattened into the handful of facts the report actually needs.
/// This is the boundary between the wire format and the analysis code.
/// </summary>
public sealed class RawIssue
{
    public required string Key { get; init; }
    public required string Summary { get; init; }
    public required string StatusName { get; init; }

    /// <summary>Raw statusCategory key: "new", "indeterminate" or "done".</summary>
    public required string StatusCategoryKey { get; init; }

    public required string IssueTypeName { get; init; }
    public required bool IsSubtask { get; init; }

    /// <summary>Parent key, set for subtasks (and, on newer instances, for stories too).</summary>
    public string? ParentKey { get; init; }

    /// <summary>Value of the discovered Epic Link custom field, for stories.</summary>
    public string? EpicLinkKey { get; init; }

    public double? StoryPoints { get; init; }
    public string? Assignee { get; init; }
    public string? Priority { get; init; }
    public DateTimeOffset? Updated { get; init; }
    public DateTimeOffset? Created { get; init; }
    public DateTimeOffset? Resolved { get; init; }
    public IReadOnlyList<string> Labels { get; init; } = Array.Empty<string>();
}
