using JiraViz.Core.Analysis;
using JiraViz.Core.Model;
using Xunit;

namespace JiraViz.Core.Tests;

public class ImputedEstimateTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);

    private static ReportModel Build(params EpicGroup[] groups)
        => new ProgressCalculator(new StatusBucketer(), stalledDays: 14)
            .Build(groups, Array.Empty<string>(), "https://jira.example.com", "project = X", Now);

    [Fact]
    public void An_unestimated_story_takes_the_rounded_mean_of_the_estimated_ones()
    {
        // Mean of 2, 4 and 6 is 4, so the unpointed story is sized 4 rather than 1.
        var model = Build(Fixtures.Epic("E-1", "Mixed",
            Fixtures.Story("S-1", Fixtures.ToDo, points: 2),
            Fixtures.Story("S-2", Fixtures.ToDo, points: 4),
            Fixtures.Story("S-3", Fixtures.ToDo, points: 6),
            Fixtures.Story("S-4", Fixtures.ToDo)));

        Assert.Equal(4, model.ImputedPoints);
        Assert.Equal(16, model.Epics[0].Size);

        var imputed = model.Epics[0].Stories.Single(s => s.Key == "S-4");
        Assert.Equal(4, imputed.Size);
        Assert.True(imputed.Imputed);
        Assert.Null(imputed.Points);
    }

    [Fact]
    public void A_real_estimate_is_never_marked_imputed()
    {
        var model = Build(Fixtures.Epic("E-1", "Mixed",
            Fixtures.Story("S-1", Fixtures.ToDo, points: 3),
            Fixtures.Story("S-2", Fixtures.ToDo)));

        Assert.False(model.Epics[0].Stories.Single(s => s.Key == "S-1").Imputed);
        Assert.True(model.Epics[0].Stories.Single(s => s.Key == "S-2").Imputed);
    }

    [Fact]
    public void The_mean_is_rounded_away_from_zero()
    {
        // Mean of 2 and 5 is 3.5, which must land on 4 rather than banker's-rounding to 4 by luck.
        var model = Build(Fixtures.Epic("E-1", "Halves",
            Fixtures.Story("S-1", Fixtures.ToDo, points: 2),
            Fixtures.Story("S-2", Fixtures.ToDo, points: 5),
            Fixtures.Story("S-3", Fixtures.ToDo)));

        Assert.Equal(4, model.ImputedPoints);
    }

    [Fact]
    public void A_mean_below_one_is_floored_so_unestimated_work_cannot_vanish()
    {
        // Half-point stories average 0.5, which would round to 0 and erase the unestimated work.
        var model = Build(Fixtures.Epic("E-1", "Tiny",
            Fixtures.Story("S-1", Fixtures.ToDo, points: 0.5),
            Fixtures.Story("S-2", Fixtures.ToDo)));

        Assert.Equal(1, model.ImputedPoints);
        Assert.Equal(1, model.Epics[0].Stories.Single(s => s.Key == "S-2").Size);
    }

    [Fact]
    public void With_nothing_estimated_there_is_no_mean_and_sizing_stays_count_based()
    {
        var model = Build(Fixtures.Epic("E-1", "Unpointed",
            Fixtures.Story("S-1", Fixtures.ToDo),
            Fixtures.Story("S-2", Fixtures.Done)));

        Assert.True(model.CountBasedSizing);
        Assert.Null(model.ImputedPoints);
        Assert.False(model.Totals.HasImputed);
        Assert.All(model.Epics[0].Stories, s => Assert.False(s.Imputed));
        Assert.Equal(2, model.Epics[0].Size);
    }

    [Fact]
    public void The_mean_is_taken_across_the_whole_report_not_per_epic()
    {
        // The unestimated epic borrows the portfolio's average, which is the point: it becomes
        // comparable with the epics that were estimated.
        var model = Build(
            Fixtures.Epic("E-POINTED", "Estimated",
                Fixtures.Story("S-1", Fixtures.ToDo, points: 8),
                Fixtures.Story("S-2", Fixtures.ToDo, points: 8)),
            Fixtures.Epic("E-BLANK", "Unestimated",
                Fixtures.Story("S-3", Fixtures.ToDo),
                Fixtures.Story("S-4", Fixtures.ToDo)));

        Assert.Equal(8, model.ImputedPoints);

        var blank = model.Epics.Single(e => e.Key == "E-BLANK");
        Assert.Equal(16, blank.Size);
        Assert.True(blank.Unestimated);
        Assert.True(blank.HasImputed);
    }

    [Fact]
    public void An_epic_where_everything_is_estimated_is_not_flagged()
    {
        var model = Build(
            Fixtures.Epic("E-CLEAN", "Clean", Fixtures.Story("S-1", Fixtures.ToDo, points: 5)),
            Fixtures.Epic("E-MIXED", "Mixed", Fixtures.Story("S-2", Fixtures.ToDo)));

        Assert.False(model.Epics.Single(e => e.Key == "E-CLEAN").HasImputed);
        Assert.True(model.Epics.Single(e => e.Key == "E-MIXED").HasImputed);

        // One imputed story anywhere makes the portfolio total approximate.
        Assert.True(model.Totals.HasImputed);
    }

    [Fact]
    public void A_shared_average_overrides_what_the_view_would_have_computed()
    {
        // The view's own stories average 2, but the project-wide figure is 7, and that is what
        // must be used so this view stays on the same scale as every other.
        var model = new ProgressCalculator(new StatusBucketer(), 14, sharedImputedPoints: 7)
            .Build(
                new[]
                {
                    Fixtures.Epic("E-1", "Narrow slice",
                        Fixtures.Story("S-1", Fixtures.ToDo, points: 2),
                        Fixtures.Story("S-2", Fixtures.ToDo)),
                },
                Array.Empty<string>(), "https://jira.example.com", "project = X", Now);

        Assert.Equal(7, model.ImputedPoints);
        Assert.Equal(9, model.Epics[0].Size);
    }

    [Fact]
    public void A_view_with_no_estimates_of_its_own_still_uses_the_shared_average()
    {
        // Without the shared value this view would size by counting; with it the view remains
        // comparable with the rest of the report rather than switching units.
        var model = new ProgressCalculator(new StatusBucketer(), 14, sharedImputedPoints: 5)
            .Build(
                new[]
                {
                    Fixtures.Epic("E-1", "Nothing estimated",
                        Fixtures.Story("S-1", Fixtures.ToDo),
                        Fixtures.Story("S-2", Fixtures.ToDo)),
                },
                Array.Empty<string>(), "https://jira.example.com", "project = X", Now);

        Assert.False(model.CountBasedSizing);
        Assert.Equal(5, model.ImputedPoints);
        Assert.Equal(10, model.Epics[0].Size);
        Assert.True(model.Totals.HasImputed);
    }

    [Fact]
    public void Without_a_shared_value_a_view_falls_back_to_its_own_average()
    {
        var model = Build(Fixtures.Epic("E-1", "Alone",
            Fixtures.Story("S-1", Fixtures.ToDo, points: 3),
            Fixtures.Story("S-2", Fixtures.ToDo)));

        Assert.Equal(3, model.ImputedPoints);
    }
}
