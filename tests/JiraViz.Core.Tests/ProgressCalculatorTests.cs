using JiraViz.Core.Analysis;
using JiraViz.Core.Model;
using Xunit;

namespace JiraViz.Core.Tests;

public class ProgressCalculatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);

    private static ReportModel Build(params EpicGroup[] groups)
        => new ProgressCalculator(new StatusBucketer(), stalledDays: 14)
            .Build(groups, Array.Empty<string>(), "https://jira.example.com", "project = X", Now);

    [Fact]
    public void Story_points_drive_epic_size_when_present()
    {
        var epic = Fixtures.Epic("E-1", "Pointed",
            Fixtures.Story("S-1", Fixtures.Done, points: 8),
            Fixtures.Story("S-2", Fixtures.ToDo, points: 2));

        var model = Build(epic);

        Assert.Equal(10, model.Epics[0].Size);
        Assert.Equal(0.8, model.Epics[0].Completion, 3);
        Assert.False(model.Epics[0].Unestimated);
        Assert.False(model.CountBasedSizing);
    }

    [Fact]
    public void Unpointed_stories_fall_back_to_counting_one_each()
    {
        var epic = Fixtures.Epic("E-1", "Unpointed",
            Fixtures.Story("S-1", Fixtures.Done),
            Fixtures.Story("S-2", Fixtures.ToDo),
            Fixtures.Story("S-3", Fixtures.ToDo),
            Fixtures.Story("S-4", Fixtures.ToDo));

        var model = Build(epic);

        Assert.Equal(4, model.Epics[0].Size);
        Assert.Equal(0.25, model.Epics[0].Completion, 3);
        Assert.True(model.Epics[0].Unestimated);
        Assert.True(model.CountBasedSizing);
    }

    [Fact]
    public void Story_earns_partial_credit_from_its_subtasks()
    {
        // The story itself is still To Do, but three of its four subtasks are finished, which is
        // exactly the "almost done" case a status-by-story-alone view would miss.
        var story = Fixtures.Story("S-1", Fixtures.ToDo, points: 8);
        story.Subtasks.Add(Fixtures.Issue("T-1", Fixtures.Done, isSubtask: true));
        story.Subtasks.Add(Fixtures.Issue("T-2", Fixtures.Done, isSubtask: true));
        story.Subtasks.Add(Fixtures.Issue("T-3", Fixtures.Done, isSubtask: true));
        story.Subtasks.Add(Fixtures.Issue("T-4", Fixtures.ToDo, isSubtask: true));

        var model = Build(Fixtures.Epic("E-1", "Partial", story));

        Assert.Equal(0.75, model.Epics[0].Completion, 3);
        Assert.Equal(6, model.Epics[0].DoneSize, 3);
        // The story is To Do, so the uncredited remainder must not be coloured as in progress.
        Assert.Equal(2, model.Epics[0].NotStartedSize, 3);
        Assert.Equal(0, model.Epics[0].InProgressSize, 3);
    }

    [Fact]
    public void A_done_story_counts_fully_even_if_a_subtask_lags()
    {
        var story = Fixtures.Story("S-1", Fixtures.Done, points: 5);
        story.Subtasks.Add(Fixtures.Issue("T-1", Fixtures.ToDo, isSubtask: true));

        var model = Build(Fixtures.Epic("E-1", "Closed", story));

        Assert.Equal(1.0, model.Epics[0].Completion, 3);
    }

    [Fact]
    public void An_in_progress_story_with_no_subtasks_gets_half_credit()
    {
        var model = Build(Fixtures.Epic("E-1", "Flight",
            Fixtures.Story("S-1", Fixtures.InProgress, points: 10)));

        Assert.Equal(0.5, model.Epics[0].Completion, 3);
        Assert.Equal(5, model.Epics[0].InProgressSize, 3);
    }

    [Fact]
    public void An_epic_with_no_stories_is_zero_sized_and_does_not_divide_by_zero()
    {
        var model = Build(Fixtures.Epic("E-1", "Empty"));

        Assert.Equal(0, model.Epics[0].Size);
        Assert.Equal(0, model.Epics[0].Completion);
        // A zero-size epic has no work to have "not started", so it must not inflate the count.
        Assert.Equal(0, model.Totals.EpicsNotStarted);
    }

    [Fact]
    public void A_large_untouched_epic_is_flagged_at_risk()
    {
        var big = Fixtures.Epic("E-BIG", "Untouched",
            Fixtures.Story("S-1", Fixtures.ToDo, points: 50));
        var small = Fixtures.Epic("E-SMALL", "Small",
            Fixtures.Story("S-2", Fixtures.Done, points: 1));

        var model = Build(big, small);

        var flagged = model.Epics.Single(e => e.Key == "E-BIG");
        Assert.True(flagged.AtRisk);
        Assert.True(flagged.NotStarted);
        Assert.False(model.Epics.Single(e => e.Key == "E-SMALL").AtRisk);
    }

    [Fact]
    public void In_progress_work_untouched_past_the_threshold_is_stalled()
    {
        var stale = Fixtures.Story("S-1", Fixtures.InProgress, points: 3, updated: Now.AddDays(-30));
        var fresh = Fixtures.Story("S-2", Fixtures.InProgress, points: 3, updated: Now.AddDays(-2));
        // Old but finished: done work is never "stalled", however long it has sat.
        var oldDone = Fixtures.Story("S-3", Fixtures.Done, points: 3, updated: Now.AddDays(-90));

        var model = Build(Fixtures.Epic("E-1", "Ageing", stale, fresh, oldDone));

        Assert.Equal(1, model.Totals.StalledCount);
        Assert.Equal("S-1", model.Stalled.Single().Key);
        Assert.Equal(30, model.Stalled.Single().DaysSinceUpdate);
    }

    [Fact]
    public void Epics_are_ordered_least_complete_first()
    {
        var model = Build(
            Fixtures.Epic("E-DONE", "Done", Fixtures.Story("S-1", Fixtures.Done, points: 5)),
            Fixtures.Epic("E-TODO", "Todo", Fixtures.Story("S-2", Fixtures.ToDo, points: 5)));

        Assert.Equal("E-TODO", model.Epics.First().Key);
    }
}
