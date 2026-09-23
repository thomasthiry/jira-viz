using JiraViz.Core.Model;

namespace JiraViz.Core.Analysis;

/// <summary>
/// A piece of finished work, worth <paramref name="Amount"/> of the portfolio's size, that
/// landed on <paramref name="Resolved"/>.
/// </summary>
/// <param name="Resolved">When it finished; null when Jira carries no date for it.</param>
/// <param name="Amount">Its share of the portfolio size.</param>
/// <param name="Completed">
/// False for the partial credit an in-progress story earns by being in progress. That credit is
/// real progress but it never finished anything, so it is reported and then left out of both the
/// curve and the remaining work.
/// </param>
public readonly record struct CreditEvent(DateTimeOffset? Resolved, double Amount, bool Completed);

/// <summary>
/// Projects when the work in scope reaches 100%.
///
/// History comes from resolution dates, which every issue already carries, so the first run of
/// the report has a full burn-up behind it rather than a single point. That curve is survivor
/// biased: it can only see the work that is in scope today, so scope added last month looks as
/// though it was always there and the total line is flat. It answers "how fast is this team
/// finishing things" honestly, which is what the projection needs; it is not a record of what
/// the plan looked like at the time.
///
/// The projection itself resamples whole past weeks rather than extrapolating their average.
/// Throughput is lumpy - a fortnight of nothing, then four stories closing at once - and the
/// question being asked is a stopping time, "how many weeks until the cumulative sum covers the
/// remainder", which has no closed form for an arbitrary empirical distribution. Simulating it
/// costs microseconds and gets the shape of the uncertainty right, including that good and bad
/// weeks cancel out over a long horizon, so the band narrows in relative terms the further out
/// the finish is.
/// </summary>
public sealed class ForecastCalculator(ForecastSettings settings)
{
    private readonly ForecastSettings _settings = settings;

    /// <summary>
    /// Ten years. A run that gets this far is reported as beyond the horizon rather than being
    /// allowed to spin: with a slow enough sampled week it would otherwise run for a very long
    /// time to produce a date nobody would act on.
    /// </summary>
    private const int HorizonWeeks = 520;

    /// <summary>
    /// Fewer complete weeks than this and the sample is too thin to resample honestly - the
    /// percentiles would be reporting the shape of three data points as though it were a
    /// distribution.
    /// </summary>
    public const int MinimumSampleWeeks = 8;

    /// <summary>
    /// A fixed seed, so regenerating an unchanged report produces an unchanged date. The
    /// generator is written out here rather than taken from System.Random because the framework
    /// does not guarantee that implementation across versions, and a projection that moved when
    /// the build machine's runtime changed would be indistinguishable from real news.
    /// </summary>
    private const ulong Seed = 0x6A09E667F3BCC908UL;

    public ForecastView Build(
        IReadOnlyList<CreditEvent> credits,
        double totalSize,
        bool hasImputed,
        DateTimeOffset now)
    {
        // Undated finished work still happened, so it is credited to the whole curve rather than
        // dropped; in-progress credit is only ever reported.
        var baseline = credits.Where(c => c.Completed && c.Resolved is null).Sum(c => c.Amount);
        var undatedCredit = credits.Where(c => !c.Completed).Sum(c => c.Amount);

        var dated = credits
            .Where(c => c.Completed && c.Resolved is not null && c.Resolved.Value <= now)
            .OrderBy(c => c.Resolved!.Value)
            .ToList();

        var completed = baseline + dated.Sum(c => c.Amount);
        var remaining = Math.Max(0, totalSize - completed);

        if (!_settings.Enabled)
            return Unavailable("Projection is switched off for this report.", suppressed: true);

        if (totalSize <= 0)
            return Unavailable("Nothing is in scope to project.");

        if (dated.Count == 0)
            return Unavailable(
                "No finished work carries a resolution date, so there is no history to project from. " +
                "Check that the query includes completed issues.");

        var burnup = BuildBurnup(dated, baseline, now);
        var samples = SampleWeeks(burnup);

        if (remaining <= 0)
            return new ForecastView
            {
                Available = false,
                Suppressed = false,
                UnavailableReason = "Everything in scope is finished.",
                Burnup = burnup,
                BaselineSize = baseline,
                CompletedSize = completed,
                UndatedCreditSize = undatedCredit,
                TotalSize = totalSize,
                RemainingSize = 0,
                Samples = samples,
                WindowWeeks = _settings.WindowWeeks,
                MeanWeeklyThroughput = samples.Count > 0 ? samples.Average() : 0,
                Simulations = 0,
                BeyondHorizonShare = 0,
                HasImputed = hasImputed,
            };

        if (samples.Count < MinimumSampleWeeks)
            return Unavailable(
                $"Only {samples.Count} complete week(s) of history, which is too few to project from; " +
                $"{MinimumSampleWeeks} are needed.", burnup, samples);

        if (samples.Sum() <= 0)
            return Unavailable(
                $"Nothing was finished in the last {samples.Count} weeks, so there is no rate to project.",
                burnup, samples);

        var (p50, p85, beyond) = Simulate(samples, remaining);

        return new ForecastView
        {
            Available = true,
            Suppressed = false,
            Burnup = burnup,
            BaselineSize = baseline,
            CompletedSize = completed,
            UndatedCreditSize = undatedCredit,
            TotalSize = totalSize,
            RemainingSize = remaining,
            Samples = samples,
            WindowWeeks = _settings.WindowWeeks,
            MeanWeeklyThroughput = samples.Average(),
            Simulations = _settings.Simulations,
            P50Weeks = p50 <= HorizonWeeks ? p50 : null,
            P85Weeks = p85 <= HorizonWeeks ? p85 : null,
            P50Date = p50 <= HorizonWeeks ? now.AddDays(p50 * 7) : null,
            P85Date = p85 <= HorizonWeeks ? now.AddDays(p85 * 7) : null,
            BeyondHorizonShare = beyond,
            HasImputed = hasImputed,
        };

        ForecastView Unavailable(
            string reason,
            IReadOnlyList<BurnupPoint>? points = null,
            IReadOnlyList<double>? drawn = null,
            bool suppressed = false) => new()
        {
            Available = false,
            UnavailableReason = reason,
            Suppressed = suppressed,
            Burnup = points ?? Array.Empty<BurnupPoint>(),
            BaselineSize = baseline,
            CompletedSize = completed,
            UndatedCreditSize = undatedCredit,
            TotalSize = totalSize,
            RemainingSize = remaining,
            Samples = drawn ?? Array.Empty<double>(),
            WindowWeeks = _settings.WindowWeeks,
            MeanWeeklyThroughput = drawn is { Count: > 0 } ? drawn.Average() : 0,
            Simulations = 0,
            BeyondHorizonShare = 0,
            HasImputed = hasImputed,
        };
    }

    /// <summary>
    /// Buckets the dated work into calendar weeks, from the first week that finished anything
    /// through to the week in progress. Quiet weeks in the middle are kept: a fortnight where
    /// nothing landed is exactly the kind of week the simulation needs to be able to draw.
    /// </summary>
    private static List<BurnupPoint> BuildBurnup(
        IReadOnlyList<CreditEvent> dated, double baseline, DateTimeOffset now)
    {
        var offset = now.Offset;
        var currentWeek = WeekStart(now, offset);
        var firstWeek = WeekStart(dated[0].Resolved!.Value, offset);

        var weekly = new Dictionary<DateTimeOffset, double>();
        foreach (var credit in dated)
        {
            var week = WeekStart(credit.Resolved!.Value, offset);
            weekly[week] = weekly.GetValueOrDefault(week) + credit.Amount;
        }

        var points = new List<BurnupPoint>();
        var cumulative = baseline;

        for (var week = firstWeek; week <= currentWeek; week = week.AddDays(7))
        {
            var throughput = weekly.GetValueOrDefault(week);
            cumulative += throughput;

            points.Add(new BurnupPoint
            {
                WeekStart = week,
                DoneSize = cumulative,
                Throughput = throughput,
                Complete = week < currentWeek,
            });
        }

        return points;
    }

    /// <summary>
    /// The weekly figures the simulation draws from: the trailing window of complete weeks. The
    /// week in progress is excluded because it is only part of a week, and counting it whole
    /// would bias every projection later.
    /// </summary>
    private List<double> SampleWeeks(IReadOnlyList<BurnupPoint> burnup)
        => burnup
            .Where(p => p.Complete)
            .TakeLast(Math.Max(1, _settings.WindowWeeks))
            .Select(p => p.Throughput)
            .ToList();

    /// <summary>
    /// Draws whole weeks at random, with replacement, until the remaining work is covered, and
    /// reports the 50th and 85th percentiles of how long that took.
    /// </summary>
    private (int P50, int P85, double BeyondHorizon) Simulate(IReadOnlyList<double> samples, double remaining)
    {
        var runs = _settings.Simulations;
        var weeks = new int[runs];
        var beyond = 0;
        var rng = new SplitMix64(Seed);
        var draw = samples.ToArray();

        for (var run = 0; run < runs; run++)
        {
            double left = remaining;
            var elapsed = 0;

            while (left > 0 && elapsed < HorizonWeeks)
            {
                left -= draw[rng.NextIndex(draw.Length)];
                elapsed++;
            }

            // A run that never covered the remainder is ranked past the horizon rather than at
            // it, so a percentile landing there reads as "further out than this report will say"
            // instead of as a date ten years out.
            if (left > 0) beyond++;
            weeks[run] = left > 0 ? HorizonWeeks + 1 : elapsed;
        }

        Array.Sort(weeks);
        return (Percentile(weeks, 0.50), Percentile(weeks, 0.85), (double)beyond / runs);
    }

    /// <summary>Nearest-rank percentile over an ascending array.</summary>
    public static int Percentile(IReadOnlyList<int> ascending, double p)
    {
        if (ascending.Count == 0) return 0;
        var rank = (int)Math.Ceiling(p * ascending.Count);
        return ascending[Math.Clamp(rank - 1, 0, ascending.Count - 1)];
    }

    /// <summary>
    /// Midnight on the Monday of the week a moment falls in, read in the report's own offset so
    /// that a resolution timestamp from another time zone lands in the week the team would say
    /// it landed in.
    /// </summary>
    public static DateTimeOffset WeekStart(DateTimeOffset moment, TimeSpan offset)
    {
        var local = moment.ToOffset(offset);
        var sinceMonday = ((int)local.DayOfWeek + 6) % 7;
        return new DateTimeOffset(local.Date, offset).AddDays(-sinceMonday);
    }

    /// <summary>
    /// SplitMix64: a few lines, no state to manage, and identical output on every platform and
    /// runtime, which is what makes the projection reproducible.
    /// </summary>
    private struct SplitMix64(ulong seed)
    {
        private ulong _state = seed;

        private ulong Next()
        {
            var z = _state += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        /// <summary>Multiply-shift rather than modulo, which would favour the low indices.</summary>
        public int NextIndex(int bound) => (int)(((UInt128)Next() * (ulong)bound) >> 64);
    }
}
