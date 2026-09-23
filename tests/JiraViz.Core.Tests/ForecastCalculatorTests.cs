using JiraViz.Core.Analysis;
using JiraViz.Core.Model;
using Xunit;

namespace JiraViz.Core.Tests;

public class ForecastCalculatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 10, 0, 0, TimeSpan.Zero);

    private static ForecastSettings Settings(int windowWeeks = 12, int simulations = 10_000)
        => new() { WindowWeeks = windowWeeks, Simulations = simulations };

    /// <summary>
    /// A run of complete weeks ending with the one before the current week, each finishing its
    /// share of the work on the Wednesday.
    /// </summary>
    private static List<CreditEvent> WeeklyHistory(IEnumerable<double> perWeek)
    {
        var weeks = perWeek.ToList();
        var lastComplete = ForecastCalculator.WeekStart(Now, TimeSpan.Zero).AddDays(-7);

        var credits = new List<CreditEvent>();
        for (var i = 0; i < weeks.Count; i++)
        {
            var start = lastComplete.AddDays(-7 * (weeks.Count - 1 - i));
            if (weeks[i] > 0) credits.Add(new CreditEvent(start.AddDays(2), weeks[i], Completed: true));
        }
        return credits;
    }

    private static ForecastView Build(
        IEnumerable<double> weekly, double totalSize, ForecastSettings? settings = null)
        => new ForecastCalculator(settings ?? Settings())
            .Build(WeeklyHistory(weekly), totalSize, hasImputed: false, Now);

    private static double[] Steady(double perWeek, int weeks) => Enumerable.Repeat(perWeek, weeks).ToArray();

    [Fact]
    public void Steady_throughput_projects_the_arithmetic_answer()
    {
        // 10 a week done, 100 left: ten weeks, and with no variance every run agrees.
        var forecast = Build(Steady(10, 12), totalSize: 220);

        Assert.True(forecast.Available);
        Assert.Equal(120, forecast.CompletedSize);
        Assert.Equal(100, forecast.RemainingSize);
        Assert.Equal(10, forecast.P50Weeks);
        Assert.Equal(10, forecast.P85Weeks);
        Assert.Equal(Now.AddDays(70), forecast.P50Date);
    }

    [Fact]
    public void Variable_throughput_puts_the_cautious_date_after_the_even_one()
    {
        var forecast = Build(new double[] { 0, 25, 2, 18, 0, 30, 5, 12, 1, 22, 8, 3 }, totalSize: 226);

        Assert.True(forecast.Available);
        Assert.True(forecast.P85Weeks > forecast.P50Weeks);
    }

    [Fact]
    public void The_same_input_always_produces_the_same_dates()
    {
        var weekly = new double[] { 4, 19, 0, 7, 13, 2, 26, 1, 9, 0, 15, 6 };

        var first = Build(weekly, totalSize: 400);
        var second = Build(weekly, totalSize: 400);

        Assert.Equal(first.P50Weeks, second.P50Weeks);
        Assert.Equal(first.P85Weeks, second.P85Weeks);
    }

    [Fact]
    public void Quiet_weeks_count_against_the_rate_rather_than_being_skipped()
    {
        // Same work finished over the same window: one team every week, the other in bursts
        // with nothing in between. The bursty one is the riskier bet and has to read that way.
        var steady = Build(Steady(10, 12), totalSize: 220);
        var bursty = Build(new double[] { 20, 0, 20, 0, 20, 0, 20, 0, 20, 0, 20, 0 }, totalSize: 220);

        Assert.Equal(steady.CompletedSize, bursty.CompletedSize);
        Assert.Equal(steady.MeanWeeklyThroughput, bursty.MeanWeeklyThroughput);
        Assert.True(bursty.P85Weeks > steady.P85Weeks);
    }

    [Fact]
    public void Only_the_trailing_window_is_sampled()
    {
        // A fast start a long time ago must not flatter a slow present.
        var weekly = Steady(50, 20).Concat(Steady(5, 12)).ToArray();

        var forecast = Build(weekly, totalSize: 1160, Settings(windowWeeks: 12));

        Assert.Equal(12, forecast.Samples.Count);
        Assert.Equal(5, forecast.MeanWeeklyThroughput);
        Assert.Equal(20, forecast.P50Weeks);
    }

    [Fact]
    public void The_week_in_progress_is_left_out_of_the_sample()
    {
        // Counted as a whole week, a week that is two days old looks like a collapse in
        // throughput and drags every date out.
        var credits = WeeklyHistory(Steady(10, 12));
        credits.Add(new CreditEvent(Now.AddDays(-1), 1, Completed: true));

        var forecast = new ForecastCalculator(Settings()).Build(credits, 221, hasImputed: false, Now);

        Assert.All(forecast.Samples, sample => Assert.Equal(10, sample));
        Assert.False(forecast.Burnup[^1].Complete);
        Assert.Equal(121, forecast.Burnup[^1].DoneSize);
    }

    [Fact]
    public void Too_little_history_reports_why_rather_than_guessing()
    {
        var forecast = Build(Steady(10, 4), totalSize: 200);

        Assert.False(forecast.Available);
        Assert.False(forecast.Suppressed);
        Assert.Contains("4 complete week", forecast.UnavailableReason);
        Assert.Null(forecast.P50Date);
    }

    [Fact]
    public void A_window_with_no_throughput_has_no_rate_to_project()
    {
        var credits = WeeklyHistory(Steady(10, 8).Concat(Steady(0, 12)));

        var forecast = new ForecastCalculator(Settings()).Build(credits, 200, hasImputed: false, Now);

        Assert.False(forecast.Available);
        Assert.Contains("Nothing was finished", forecast.UnavailableReason);
    }

    [Fact]
    public void Work_with_no_resolution_date_is_credited_to_the_whole_curve()
    {
        // Typical of a workflow whose close transition never set a resolution date: the work is
        // finished, it just cannot be placed on the timeline.
        var credits = WeeklyHistory(Steady(10, 12));
        credits.Add(new CreditEvent(null, 40, Completed: true));

        var forecast = new ForecastCalculator(Settings()).Build(credits, 220, hasImputed: false, Now);

        Assert.Equal(40, forecast.BaselineSize);
        Assert.Equal(160, forecast.CompletedSize);
        Assert.Equal(60, forecast.RemainingSize);
        Assert.Equal(50, forecast.Burnup[0].DoneSize);
        Assert.Equal(10, forecast.MeanWeeklyThroughput);
    }

    [Fact]
    public void In_progress_credit_is_reported_but_never_projected_from()
    {
        var credits = WeeklyHistory(Steady(10, 12));
        credits.Add(new CreditEvent(null, 15, Completed: false));

        var forecast = new ForecastCalculator(Settings()).Build(credits, 220, hasImputed: false, Now);

        Assert.Equal(15, forecast.UndatedCreditSize);
        Assert.Equal(120, forecast.CompletedSize);
        Assert.Equal(100, forecast.RemainingSize);
        Assert.Equal(120, forecast.Burnup[^1].DoneSize);
    }

    [Fact]
    public void Nothing_left_to_do_is_reported_as_finished_not_as_a_date()
    {
        var forecast = Build(Steady(10, 12), totalSize: 120);

        Assert.False(forecast.Available);
        Assert.Equal("Everything in scope is finished.", forecast.UnavailableReason);
        Assert.NotEmpty(forecast.Burnup);
    }

    [Fact]
    public void A_scope_the_team_cannot_finish_is_reported_as_beyond_the_horizon()
    {
        var forecast = Build(Steady(1, 12), totalSize: 100_000);

        Assert.Equal(1, forecast.BeyondHorizonShare);
        Assert.Null(forecast.P50Date);
        Assert.Null(forecast.P85Date);
    }

    [Fact]
    public void Nothing_dated_leaves_the_projection_off_with_a_reason()
    {
        var forecast = new ForecastCalculator(Settings())
            .Build(new List<CreditEvent> { new(null, 30, true) }, 100, hasImputed: false, Now);

        Assert.False(forecast.Available);
        Assert.Contains("resolution date", forecast.UnavailableReason);
    }

    [Fact]
    public void Switching_the_forecast_off_leaves_the_rest_of_the_report_alone()
    {
        var settings = new ForecastSettings { Enabled = false };

        var forecast = new ForecastCalculator(settings)
            .Build(WeeklyHistory(Steady(10, 12)), 220, hasImputed: false, Now);

        Assert.False(forecast.Available);
        Assert.True(forecast.Suppressed);
        Assert.Empty(forecast.Burnup);
    }

    [Fact]
    public void Weeks_start_on_monday_in_the_reports_own_offset()
    {
        var sunday = new DateTimeOffset(2026, 9, 13, 23, 0, 0, TimeSpan.Zero);

        Assert.Equal(
            new DateTimeOffset(2026, 9, 7, 0, 0, 0, TimeSpan.Zero),
            ForecastCalculator.WeekStart(sunday, TimeSpan.Zero));

        // The same instant read two hours further east is already Monday, and belongs to the
        // week the team would file it under.
        Assert.Equal(
            new DateTimeOffset(2026, 9, 14, 0, 0, 0, TimeSpan.FromHours(2)),
            ForecastCalculator.WeekStart(sunday, TimeSpan.FromHours(2)));
    }

    [Fact]
    public void The_burnup_runs_week_by_week_with_no_gaps()
    {
        var forecast = Build(new double[] { 10, 0, 0, 0, 5, 0, 0, 12, 0, 3, 0, 7 }, totalSize: 200);

        // Twelve complete weeks plus the one in progress.
        Assert.Equal(13, forecast.Burnup.Count);

        for (var i = 1; i < forecast.Burnup.Count; i++)
        {
            Assert.Equal(forecast.Burnup[i - 1].WeekStart.AddDays(7), forecast.Burnup[i].WeekStart);
            Assert.True(forecast.Burnup[i].DoneSize >= forecast.Burnup[i - 1].DoneSize);
        }
    }

    [Fact]
    public void Percentiles_read_off_the_ranked_runs()
    {
        var runs = new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };

        Assert.Equal(5, ForecastCalculator.Percentile(runs, 0.50));
        Assert.Equal(9, ForecastCalculator.Percentile(runs, 0.85));
        Assert.Equal(0, ForecastCalculator.Percentile(Array.Empty<int>(), 0.5));
    }
}
