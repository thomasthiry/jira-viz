using JiraViz.Core.Model;

namespace JiraViz.Core.Analysis;

/// <summary>
/// Turns the assembled hierarchy into the numbers the report renders.
///
/// Sizing: a story's size is its story points, falling back to 1 when unpointed, so an epic
/// where nobody estimated still gets weighted by issue count rather than vanishing. Epics where
/// that fallback was used throughout are flagged Unestimated, so a reader can tell a real
/// weighting from a synthesized one.
///
/// Completion: a story earns partial credit from its subtasks, which is what makes "almost done"
/// distinguishable from "just started" without anyone having to move the story itself.
/// </summary>
public sealed class ProgressCalculator(
    StatusBucketer bucketer,
    int stalledDays,
    double? sharedImputedPoints = null)
{
    private readonly StatusBucketer _bucketer = bucketer;
    private readonly int _stalledDays = stalledDays;

    /// <summary>
    /// A stand-in size decided elsewhere, so that every view in a report sizes unestimated work
    /// identically. Without it each view would average only its own slice and the same story
    /// could be worth a different amount depending on which milestone you were looking at.
    /// </summary>
    private readonly double? _sharedImputedPoints = sharedImputedPoints;

    /// <summary>Stand-in size for unestimated stories; null when nothing in scope was estimated.</summary>
    private double? _imputedPoints;

    /// <summary>Credit given to a story that is in progress but has no subtasks to measure.</summary>
    private const double InProgressCredit = 0.5;

    public ReportModel Build(
        IReadOnlyList<EpicGroup> groups,
        IReadOnlyList<string> warnings,
        string baseUrl,
        string jql,
        DateTimeOffset now)
    {
        var allStories = groups.SelectMany(g => g.Stories).ToList();

        // Stories with no estimate are sized by the rounded mean of the ones that have one. The
        // shared value wins when supplied, so a filtered view keeps the project's scale rather
        // than re-averaging its own narrower slice.
        _imputedPoints = _sharedImputedPoints
            ?? (allStories.Any(s => HasPoints(s.Story)) ? ImputedSize(allStories) : null);

        // Nothing estimated and nothing to borrow: fall back to counting issues.
        var countBased = _imputedPoints is null;

        var epics = groups.Select(g => BuildEpic(g, now)).ToList();

        // "At risk" is relative: an epic counts as large only next to the others in this report.
        var sizes = epics.Where(e => e.Size > 0).Select(e => e.Size).OrderBy(s => s).ToList();
        var medianSize = Median(sizes);
        epics = epics.Select(e => Reflag(e, medianSize)).ToList();

        // Least finished first, so the work needing attention sits at the top of the page.
        epics = epics
            .OrderByDescending(e => e.IsSynthetic ? 0 : 1)
            .ThenBy(e => e.Completion)
            .ThenByDescending(e => e.Size)
            .ToList();

        var stalled = CollectStalled(groups, now);
        var totalSize = epics.Sum(e => e.Size);

        var totals = new PortfolioTotals
        {
            Size = totalSize,
            Completion = totalSize > 0 ? epics.Sum(e => e.DoneSize) / totalSize : 0,
            DoneSize = epics.Sum(e => e.DoneSize),
            InProgressSize = epics.Sum(e => e.InProgressSize),
            NotStartedSize = epics.Sum(e => e.NotStartedSize),
            EpicCount = epics.Count(e => !e.IsSynthetic),
            EpicsNotStarted = epics.Count(e => !e.IsSynthetic && e.NotStarted && e.Size > 0),
            EpicsDone = epics.Count(e => !e.IsSynthetic && e.Completion >= 0.999 && e.Size > 0),
            IssueCount = groups.Sum(g => g.Stories.Count + g.Stories.Sum(s => s.Subtasks.Count))
                         + groups.Count(g => !g.IsSynthetic),
            StalledCount = stalled.Count,
            HasImputed = epics.Any(e => e.HasImputed),
        };

        return new ReportModel
        {
            GeneratedAt = now,
            JiraBaseUrl = baseUrl.TrimEnd('/'),
            Jql = jql,
            StalledDays = _stalledDays,
            CountBasedSizing = countBased,
            ImputedPoints = _imputedPoints,
            Totals = totals,
            Epics = epics,
            Stalled = stalled,
            Warnings = warnings,
        };
    }

    private EpicView BuildEpic(EpicGroup group, DateTimeOffset now)
    {
        var anyPointed = group.Stories.Any(s => HasPoints(s.Story));
        var stories = group.Stories.Select(s => BuildStory(s, now)).ToList();

        double size = 0, done = 0, inProgress = 0, notStarted = 0;
        foreach (var story in stories)
        {
            size += story.Size;
            var credited = story.Size * story.Completion;
            done += credited;

            // The uncredited remainder is coloured by where the story itself sits, so a story
            // that is in progress with nothing finished still shows as in-progress on the bar.
            var remainder = story.Size - credited;
            if (story.Bucket == StatusBucket.InProgress) inProgress += remainder;
            else notStarted += remainder;
        }

        var completion = size > 0 ? done / size : 0;
        stories = stories.OrderBy(s => s.Completion).ThenByDescending(s => s.Size).ToList();

        return new EpicView
        {
            Key = group.Key,
            Summary = group.Summary,
            Status = group.StatusName,
            Bucket = group.IsSynthetic
                ? StatusBucket.NotStarted
                : _bucketer.Bucket(group.StatusName, group.StatusCategoryKey),
            Size = size,
            Completion = completion,
            DoneSize = done,
            InProgressSize = inProgress,
            NotStartedSize = notStarted,
            Unestimated = !anyPointed && group.Stories.Count > 0,
            HasImputed = stories.Any(s => s.Imputed),
            IsSynthetic = group.IsSynthetic,
            NotStarted = completion <= 0,
            AtRisk = false, // filled in by Reflag, once the median epic size is known
            StalledCount = stories.Count(s => s.Stalled) + stories.Sum(s => s.Subtasks.Count(t => t.Stalled)),
            Stories = stories,
        };
    }

    private StoryView BuildStory(StoryGroup group, DateTimeOffset now)
    {
        var story = group.Story;
        var bucket = _bucketer.Bucket(story.StatusName, story.StatusCategoryKey);
        var subtasks = group.Subtasks
            .Select(t => BuildSubtask(t, now))
            .OrderBy(t => t.Bucket)
            .ThenBy(t => t.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        double completion;
        if (bucket == StatusBucket.Done)
            completion = 1.0;
        else if (subtasks.Count > 0)
            completion = (double)subtasks.Count(t => t.Bucket == StatusBucket.Done) / subtasks.Count;
        else
            completion = bucket == StatusBucket.InProgress ? InProgressCredit : 0.0;

        return new StoryView
        {
            Key = story.Key,
            Summary = story.Summary,
            Status = story.StatusName,
            Bucket = bucket,
            IssueType = story.IssueTypeName,
            Size = SizeOf(story),
            Imputed = !HasPoints(story) && _imputedPoints is not null,
            Completion = completion,
            Points = story.StoryPoints,
            Assignee = story.Assignee,
            Updated = story.Updated,
            Stalled = IsStalled(bucket, story.Updated, now),
            Subtasks = subtasks,
        };
    }

    private SubtaskView BuildSubtask(RawIssue subtask, DateTimeOffset now)
    {
        var bucket = _bucketer.Bucket(subtask.StatusName, subtask.StatusCategoryKey);
        return new SubtaskView
        {
            Key = subtask.Key,
            Summary = subtask.Summary,
            Status = subtask.StatusName,
            Bucket = bucket,
            Assignee = subtask.Assignee,
            Updated = subtask.Updated,
            Stalled = IsStalled(bucket, subtask.Updated, now),
        };
    }

    private List<StalledIssue> CollectStalled(IReadOnlyList<EpicGroup> groups, DateTimeOffset now)
    {
        var result = new List<StalledIssue>();

        foreach (var group in groups)
        foreach (var story in group.Stories)
        {
            Consider(story.Story, group);
            foreach (var subtask in story.Subtasks) Consider(subtask, group);
        }

        return result.OrderByDescending(s => s.DaysSinceUpdate).ToList();

        void Consider(RawIssue issue, EpicGroup group)
        {
            var bucket = _bucketer.Bucket(issue.StatusName, issue.StatusCategoryKey);
            if (!IsStalled(bucket, issue.Updated, now)) return;

            result.Add(new StalledIssue
            {
                Key = issue.Key,
                Summary = issue.Summary,
                Status = issue.StatusName,
                IssueType = issue.IssueTypeName,
                Assignee = issue.Assignee,
                DaysSinceUpdate = (int)(now - issue.Updated!.Value).TotalDays,
                EpicKey = group.IsSynthetic ? null : group.Key,
                EpicSummary = group.IsSynthetic ? null : group.Summary,
            });
        }
    }

    private bool IsStalled(StatusBucket bucket, DateTimeOffset? updated, DateTimeOffset now)
        => bucket == StatusBucket.InProgress
           && updated.HasValue
           && (now - updated.Value).TotalDays >= _stalledDays;

    private static bool HasPoints(RawIssue issue) => issue.StoryPoints is > 0;

    /// <summary>
    /// The rounded mean of the estimated stories, floored at 1 so a portfolio of half-point
    /// stories cannot impute a size of zero and make unestimated work vanish from the totals.
    /// </summary>
    private static double ImputedSize(IEnumerable<StoryGroup> stories)
    {
        var pointed = stories.Where(s => HasPoints(s.Story)).Select(s => s.Story.StoryPoints!.Value).ToList();
        if (pointed.Count == 0) return 1.0;

        return Math.Max(1.0, Math.Round(pointed.Average(), MidpointRounding.AwayFromZero));
    }

    private double SizeOf(RawIssue issue)
        => HasPoints(issue) ? issue.StoryPoints!.Value : _imputedPoints ?? 1.0;

    /// <summary>Re-stamps the risk flag now that the portfolio-wide median size is known.</summary>
    private static EpicView Reflag(EpicView epic, double medianSize) => new()
    {
        Key = epic.Key,
        Summary = epic.Summary,
        Status = epic.Status,
        Bucket = epic.Bucket,
        Size = epic.Size,
        Completion = epic.Completion,
        DoneSize = epic.DoneSize,
        InProgressSize = epic.InProgressSize,
        NotStartedSize = epic.NotStartedSize,
        Unestimated = epic.Unestimated,
        HasImputed = epic.HasImputed,
        IsSynthetic = epic.IsSynthetic,
        NotStarted = epic.NotStarted,
        AtRisk = !epic.IsSynthetic && epic.Size > 0 && epic.Size >= medianSize && epic.Completion < 0.25,
        StalledCount = epic.StalledCount,
        Stories = epic.Stories,
    };

    internal static double Median(IReadOnlyList<double> sorted)
    {
        if (sorted.Count == 0) return 0;
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }
}
