namespace JiraViz.Core.Model;

/// <summary>The complete analysis result, serialized straight into the HTML report.</summary>
public sealed class ReportModel
{
    public required DateTimeOffset GeneratedAt { get; init; }
    public required string JiraBaseUrl { get; init; }
    public required string Jql { get; init; }
    public required int StalledDays { get; init; }


    /// <summary>True when no story points were found anywhere, so sizing is count-based throughout.</summary>
    public required bool CountBasedSizing { get; init; }

    /// <summary>
    /// The stand-in size given to stories with no estimate: the rounded mean of the stories that
    /// do carry points. Null when nothing was estimated, in which case no imputation is possible
    /// and sizing falls back to counting issues.
    /// </summary>
    public double? ImputedPoints { get; init; }

    public required PortfolioTotals Totals { get; init; }
    public required IReadOnlyList<EpicView> Epics { get; init; }
    public required IReadOnlyList<StalledIssue> Stalled { get; init; }

    /// <summary>Non-fatal problems worth showing the reader (missing fields, orphaned issues).</summary>
    public required IReadOnlyList<string> Warnings { get; init; }
}

public sealed class PortfolioTotals
{
    public required double Size { get; init; }
    public required double Completion { get; init; }
    public required double DoneSize { get; init; }
    public required double InProgressSize { get; init; }
    public required double NotStartedSize { get; init; }
    public required int EpicCount { get; init; }
    public required int EpicsNotStarted { get; init; }
    public required int EpicsDone { get; init; }
    public required int IssueCount { get; init; }
    public required int StalledCount { get; init; }

    /// <summary>True when any size in this total came from an imputed estimate rather than Jira.</summary>
    public required bool HasImputed { get; init; }
}

public sealed class EpicView
{
    public required string Key { get; init; }
    public required string Summary { get; init; }
    public required string Status { get; init; }
    public required StatusBucket Bucket { get; init; }

    public required double Size { get; init; }
    public required double Completion { get; init; }

    /// <summary>Share of <see cref="Size"/> sitting in each bucket — drives the stacked bar.</summary>
    public required double DoneSize { get; init; }
    public required double InProgressSize { get; init; }
    public required double NotStartedSize { get; init; }

    /// <summary>True when no story under this epic carried a real estimate.</summary>
    public required bool Unestimated { get; init; }

    /// <summary>True when any story under this epic was sized by imputation.</summary>
    public required bool HasImputed { get; init; }

    /// <summary>True for the synthetic bucket holding stories with no epic.</summary>
    public required bool IsSynthetic { get; init; }

    public required bool NotStarted { get; init; }
    public required bool AtRisk { get; init; }
    public required int StalledCount { get; init; }

    public required IReadOnlyList<StoryView> Stories { get; init; }
}

public sealed class StoryView
{
    public required string Key { get; init; }
    public required string Summary { get; init; }
    public required string Status { get; init; }
    public required StatusBucket Bucket { get; init; }
    public required string IssueType { get; init; }
    public required double Size { get; init; }
    public required double Completion { get; init; }
    /// <summary>The estimate from Jira, null when the story carries none.</summary>
    public double? Points { get; init; }

    /// <summary>True when <see cref="Size"/> is a stand-in rather than a real estimate.</summary>
    public required bool Imputed { get; init; }
    public string? Assignee { get; init; }
    public DateTimeOffset? Updated { get; init; }
    public required bool Stalled { get; init; }
    public required IReadOnlyList<SubtaskView> Subtasks { get; init; }
}

public sealed class SubtaskView
{
    public required string Key { get; init; }
    public required string Summary { get; init; }
    public required string Status { get; init; }
    public required StatusBucket Bucket { get; init; }
    public string? Assignee { get; init; }
    public DateTimeOffset? Updated { get; init; }
    public required bool Stalled { get; init; }
}

public sealed class StalledIssue
{
    public required string Key { get; init; }
    public required string Summary { get; init; }
    public required string Status { get; init; }
    public required string IssueType { get; init; }
    public string? Assignee { get; init; }
    public required int DaysSinceUpdate { get; init; }
    public string? EpicKey { get; init; }
    public string? EpicSummary { get; init; }
}
