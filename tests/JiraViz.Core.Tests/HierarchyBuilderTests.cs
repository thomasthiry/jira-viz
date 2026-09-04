using JiraViz.Core.Analysis;
using Xunit;

namespace JiraViz.Core.Tests;

public class HierarchyBuilderTests
{
    [Fact]
    public void Links_stories_to_epics_and_subtasks_to_stories()
    {
        var (epics, warnings) = new HierarchyBuilder().Build(new[]
        {
            Fixtures.Issue("E-1", Fixtures.InProgress, type: "Epic"),
            Fixtures.Issue("S-1", Fixtures.ToDo, epicLink: "E-1"),
            Fixtures.Issue("T-1", Fixtures.ToDo, isSubtask: true, parent: "S-1"),
            Fixtures.Issue("T-2", Fixtures.Done, isSubtask: true, parent: "S-1"),
        });

        var epic = Assert.Single(epics);
        var story = Assert.Single(epic.Stories);
        Assert.Equal("S-1", story.Story.Key);
        Assert.Equal(2, story.Subtasks.Count);
        Assert.Empty(warnings);
    }

    [Fact]
    public void Stories_without_an_epic_go_to_the_synthetic_bucket()
    {
        var (epics, warnings) = new HierarchyBuilder().Build(new[]
        {
            Fixtures.Issue("E-1", Fixtures.InProgress, type: "Epic"),
            Fixtures.Issue("S-1", Fixtures.ToDo, epicLink: "E-1"),
            Fixtures.Issue("S-2", Fixtures.ToDo),
        });

        var synthetic = Assert.Single(epics, e => e.IsSynthetic);
        Assert.Equal(SyntheticKeys.NoEpic, synthetic.Key);
        Assert.Equal("S-2", Assert.Single(synthetic.Stories).Story.Key);
        Assert.Contains(warnings, w => w.Contains("not linked to an epic"));
    }

    [Fact]
    public void A_subtask_whose_parent_is_out_of_scope_is_reported_not_silently_dropped()
    {
        var (epics, warnings) = new HierarchyBuilder().Build(new[]
        {
            Fixtures.Issue("E-1", Fixtures.InProgress, type: "Epic"),
            Fixtures.Issue("T-9", Fixtures.Done, isSubtask: true, parent: "S-MISSING"),
        });

        Assert.Empty(epics.SelectMany(e => e.Stories));
        Assert.Contains(warnings, w => w.Contains("no parent in the result set"));
    }

    [Fact]
    public void Falls_back_to_the_parent_field_when_epic_link_is_absent()
    {
        // Instances migrated to the unified Parent field expose the epic there instead.
        var (epics, _) = new HierarchyBuilder().Build(new[]
        {
            Fixtures.Issue("E-1", Fixtures.InProgress, type: "Epic"),
            Fixtures.Issue("S-1", Fixtures.ToDo, parent: "E-1"),
        });

        Assert.Equal("S-1", Assert.Single(epics.Single().Stories).Story.Key);
    }

    [Fact]
    public void A_renamed_epic_issue_type_is_honoured()
    {
        var (epics, _) = new HierarchyBuilder("Initiative").Build(new[]
        {
            Fixtures.Issue("E-1", Fixtures.InProgress, type: "Initiative"),
            Fixtures.Issue("S-1", Fixtures.ToDo, epicLink: "E-1"),
        });

        var epic = Assert.Single(epics, e => !e.IsSynthetic);
        Assert.Equal("E-1", epic.Key);
        Assert.Single(epic.Stories);
    }

    [Fact]
    public void An_empty_epic_is_kept_and_reported()
    {
        var (epics, warnings) = new HierarchyBuilder().Build(new[]
        {
            Fixtures.Issue("E-1", Fixtures.ToDo, type: "Epic"),
        });

        Assert.Single(epics);
        Assert.Contains(warnings, w => w.Contains("no stories"));
    }
}
