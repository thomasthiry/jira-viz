namespace JiraViz.Core.Model;

/// <summary>
/// The whole report: one <see cref="ReportModel"/> per named view, all embedded in a single
/// page so switching between milestones is instant and needs no network.
/// </summary>
public sealed class ReportDocument
{
    /// <summary>Report title. Null falls back to a generic heading in the page.</summary>
    public string? ProjectName { get; init; }

    public required DateTimeOffset GeneratedAt { get; init; }

    /// <summary>Always at least one: the base query is emitted as the first view.</summary>
    public required IReadOnlyList<ReportView> Views { get; init; }
}

public sealed class ReportView
{
    public required string Name { get; init; }

    /// <summary>The effective query, base and fragment already composed.</summary>
    public required string Jql { get; init; }

    public required ReportModel Model { get; init; }
}
