namespace JiraViz.Core.Model;

/// <summary>
/// When the work in scope is likely to be finished, reconstructed from resolution dates and
/// projected forward by simulation. Always present on a <see cref="ReportModel"/>: when there is
/// too little history to project, <see cref="Available"/> is false and
/// <see cref="UnavailableReason"/> says why, which is more use to a reader than a missing panel.
/// </summary>
public sealed class ForecastView
{
    /// <summary>False when the projection could not be made; the chart is then omitted.</summary>
    public required bool Available { get; init; }

    /// <summary>Plain-English reason, set only when <see cref="Available"/> is false.</summary>
    public string? UnavailableReason { get; init; }

    /// <summary>
    /// True when the projection was switched off rather than attempted and abandoned. The report
    /// then leaves the panel out altogether: someone who turned it off does not need to be told
    /// each time that it is off.
    /// </summary>
    public required bool Suppressed { get; init; }

    /// <summary>One point per calendar week, oldest first. Empty when nothing carries a date.</summary>
    public required IReadOnlyList<BurnupPoint> Burnup { get; init; }

    /// <summary>
    /// Finished work that carries no resolution date. It cannot be placed on the timeline, so it
    /// is credited to every point of the curve rather than dropped, and never counted as recent
    /// throughput.
    /// </summary>
    public required double BaselineSize { get; init; }

    /// <summary>Finished work the curve accounts for, including <see cref="BaselineSize"/>.</summary>
    public required double CompletedSize { get; init; }

    /// <summary>
    /// Partial credit held by in-progress stories with no subtasks to date it. The headline
    /// counts it as progress; the projection deliberately does not, so the curve ends where the
    /// forecast starts and finished work is the only thing driving the date.
    /// </summary>
    public required double UndatedCreditSize { get; init; }

    public required double TotalSize { get; init; }

    /// <summary>What the simulation has to get through: total less completed.</summary>
    public required double RemainingSize { get; init; }

    /// <summary>The weekly throughput figures the simulation draws from.</summary>
    public required IReadOnlyList<double> Samples { get; init; }

    /// <summary>How many weeks back the sample window reaches.</summary>
    public required int WindowWeeks { get; init; }

    public required double MeanWeeklyThroughput { get; init; }
    public required int Simulations { get; init; }

    /// <summary>Weeks from now at which half / 85 per cent of simulations had finished.</summary>
    public int? P50Weeks { get; init; }
    public int? P85Weeks { get; init; }

    public DateTimeOffset? P50Date { get; init; }
    public DateTimeOffset? P85Date { get; init; }

    /// <summary>Share of runs still unfinished at the simulation horizon, as a fraction.</summary>
    public required double BeyondHorizonShare { get; init; }

    /// <summary>
    /// The whole project's weekly rate, taken from the base view and reused here. Null on the
    /// base view itself, where it would only restate <see cref="MeanWeeklyThroughput"/>.
    /// </summary>
    public double? ProjectVelocity { get; init; }

    /// <summary>
    /// What is left expressed as weeks of the whole team's output rather than as a date: the
    /// remainder divided by the project's rate.
    ///
    /// It says how big this slice is, not when it lands, which is the only thing a project-wide
    /// rate can honestly say about one milestone. Handing every milestone the full team's
    /// velocity and reading dates off it would commit the same capacity several times over and
    /// produce a set of dates that cannot all be true; per-milestone rates, by contrast, sum
    /// back to the project's. Survives a view with too little history to project a date at all,
    /// which is where it earns its place.
    /// </summary>
    public double? TeamWeeksRemaining { get; init; }

    /// <summary>True when imputed estimates contributed to the sizes being projected.</summary>
    public required bool HasImputed { get; init; }
}

/// <summary>One week of the reconstructed burn-up.</summary>
public sealed class BurnupPoint
{
    /// <summary>Midnight on the Monday the week starts.</summary>
    public required DateTimeOffset WeekStart { get; init; }

    /// <summary>Cumulative finished work at the end of this week.</summary>
    public required double DoneSize { get; init; }

    /// <summary>Work finished during this week alone.</summary>
    public required double Throughput { get; init; }

    /// <summary>
    /// False for the week in progress right now, which is incomplete and would drag the
    /// sampled throughput down if it were treated as a finished week.
    /// </summary>
    public required bool Complete { get; init; }
}
