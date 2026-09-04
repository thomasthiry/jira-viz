using JiraViz.Core.Model;

namespace JiraViz.Core.Analysis;

/// <summary>An epic and everything hanging off it, before any metrics are computed.</summary>
public sealed class EpicGroup
{
    public required string Key { get; init; }
    public required string Summary { get; init; }
    public required string StatusName { get; init; }
    public required string StatusCategoryKey { get; init; }

    /// <summary>True for the synthetic bucket collecting stories that belong to no epic.</summary>
    public required bool IsSynthetic { get; init; }

    public List<StoryGroup> Stories { get; } = new();
}

public sealed class StoryGroup
{
    public required RawIssue Story { get; init; }
    public List<RawIssue> Subtasks { get; } = new();
}

/// <summary>Key used for the synthetic bucket of stories with no epic.</summary>
public static class SyntheticKeys
{
    public const string NoEpic = "(no epic)";
}

/// <summary>
/// Assembles a flat issue list into epics -> stories -> subtasks. Everything fetched ends up
/// somewhere: stories with no epic go to a synthetic bucket and subtasks whose parent was not
/// in the result set are reported as warnings rather than silently dropped.
/// </summary>
public sealed class HierarchyBuilder(string epicIssueTypeName = "Epic")
{
    private readonly string _epicTypeName = epicIssueTypeName;

    public (List<EpicGroup> Epics, List<string> Warnings) Build(IReadOnlyList<RawIssue> issues)
    {
        var warnings = new List<string>();

        var epics = new Dictionary<string, EpicGroup>(StringComparer.OrdinalIgnoreCase);
        var stories = new Dictionary<string, StoryGroup>(StringComparer.OrdinalIgnoreCase);
        var subtasks = new List<RawIssue>();

        foreach (var issue in issues)
        {
            if (issue.IsSubtask)
                subtasks.Add(issue);
            else if (string.Equals(issue.IssueTypeName, _epicTypeName, StringComparison.OrdinalIgnoreCase))
                epics[issue.Key] = new EpicGroup
                {
                    Key = issue.Key,
                    Summary = issue.Summary,
                    StatusName = issue.StatusName,
                    StatusCategoryKey = issue.StatusCategoryKey,
                    IsSynthetic = false,
                };
            else
                stories[issue.Key] = new StoryGroup { Story = issue };
        }

        // Attach subtasks to their parent story. A subtask whose parent was filtered out by the
        // JQL is counted in a warning, since it silently distorts the parent's completion.
        var orphanSubtasks = 0;
        foreach (var subtask in subtasks)
        {
            if (subtask.ParentKey is not null && stories.TryGetValue(subtask.ParentKey, out var parent))
                parent.Subtasks.Add(subtask);
            else
                orphanSubtasks++;
        }
        if (orphanSubtasks > 0)
            warnings.Add($"{orphanSubtasks} subtask(s) had no parent in the result set and were excluded. " +
                         "Widen the JQL to include their parents for accurate story progress.");

        // Attach stories to their epic, falling back to the parent field for instances that have
        // already migrated off Epic Link.
        EpicGroup? synthetic = null;
        foreach (var story in stories.Values.OrderBy(s => s.Story.Key, StringComparer.OrdinalIgnoreCase))
        {
            var epicKey = story.Story.EpicLinkKey ?? story.Story.ParentKey;

            if (epicKey is not null && epics.TryGetValue(epicKey, out var epic))
            {
                epic.Stories.Add(story);
                continue;
            }

            synthetic ??= new EpicGroup
            {
                Key = SyntheticKeys.NoEpic,
                Summary = "Stories not linked to any epic",
                StatusName = "",
                StatusCategoryKey = "new",
                IsSynthetic = true,
            };
            synthetic.Stories.Add(story);
        }

        var result = epics.Values.ToList();
        if (synthetic is not null)
        {
            result.Add(synthetic);
            warnings.Add($"{synthetic.Stories.Count} story/stories are not linked to an epic and are " +
                         "grouped under \"(no epic)\".");
        }

        var emptyEpics = result.Count(e => e.Stories.Count == 0);
        if (emptyEpics > 0)
            warnings.Add($"{emptyEpics} epic(s) contain no stories in the result set and are sized at zero.");

        return (result, warnings);
    }
}
