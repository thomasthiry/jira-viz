using JiraViz.Core.Jira;
using JiraViz.Core.Model;
using Xunit;

namespace JiraViz.Core.Tests;

public class HierarchyCompleterTests
{
    private static readonly ResolvedFields Fields = new("customfield_1", "customfield_2");

    /// <summary>A stand-in Jira holding a whole project, answering only "key in (...)" lookups.</summary>
    private sealed class FakeJira(params RawIssue[] universe) : IJiraClient
    {
        private readonly Dictionary<string, RawIssue> _byKey =
            universe.ToDictionary(i => i.Key, StringComparer.OrdinalIgnoreCase);

        public List<string> Queries { get; } = new();

        public Task<string> WhoAmIAsync(CancellationToken ct = default) => Task.FromResult("fake");
        public Task<ResolvedFields> ResolveFieldsAsync(CancellationToken ct = default) => Task.FromResult(Fields);

        public Task<IReadOnlyList<RawIssue>> SearchAsync(
            string jql, ResolvedFields fields, IProgress<int>? progress = null, CancellationToken ct = default)
        {
            Queries.Add(jql);

            var inside = jql[(jql.IndexOf('(') + 1)..jql.LastIndexOf(')')];
            var keys = inside.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

            IReadOnlyList<RawIssue> found = keys
                .Where(_byKey.ContainsKey)
                .Select(k => _byKey[k])
                .ToList();

            return Task.FromResult(found);
        }
    }

    [Fact]
    public async Task An_epic_missing_from_a_filtered_result_is_pulled_in()
    {
        // The motivating case: a milestone query returns the story, but the fixVersion lives on
        // the story and not on its epic, so the epic is absent and the story would be orphaned.
        var epic = Fixtures.Issue("E-1", Fixtures.InProgress, type: "Epic");
        var story = Fixtures.Issue("S-1", Fixtures.ToDo, epicLink: "E-1");

        var jira = new FakeJira(epic, story);
        var completed = await HierarchyCompleter.CompleteAsync(jira, Fields, new[] { story });

        Assert.Equal(new[] { "S-1", "E-1" }, completed.Select(i => i.Key));
        Assert.Single(jira.Queries);
    }

    [Fact]
    public async Task A_subtask_whose_parent_is_absent_pulls_in_the_parent_and_then_its_epic()
    {
        var epic = Fixtures.Issue("E-1", Fixtures.InProgress, type: "Epic");
        var story = Fixtures.Issue("S-1", Fixtures.ToDo, epicLink: "E-1");
        var subtask = Fixtures.Issue("T-1", Fixtures.InProgress, isSubtask: true, parent: "S-1");

        var completed = await HierarchyCompleter.CompleteAsync(
            new FakeJira(epic, story, subtask), Fields, new[] { subtask });

        // Two rounds: the parent story first, then the epic that story references.
        Assert.Equal(new[] { "T-1", "S-1", "E-1" }, completed.Select(i => i.Key));
    }

    [Fact]
    public async Task Nothing_is_fetched_when_the_hierarchy_is_already_whole()
    {
        var epic = Fixtures.Issue("E-1", Fixtures.InProgress, type: "Epic");
        var story = Fixtures.Issue("S-1", Fixtures.ToDo, epicLink: "E-1");

        var jira = new FakeJira(epic, story);
        var completed = await HierarchyCompleter.CompleteAsync(jira, Fields, new[] { story, epic });

        Assert.Empty(jira.Queries);
        Assert.Equal(2, completed.Count);
    }

    [Fact]
    public async Task An_ancestor_that_cannot_be_read_does_not_loop_or_throw()
    {
        // The epic exists in Jira but permissions hide it, so the lookup comes back empty.
        var story = Fixtures.Issue("S-1", Fixtures.ToDo, epicLink: "E-SECRET");

        var jira = new FakeJira(story);
        var completed = await HierarchyCompleter.CompleteAsync(jira, Fields, new[] { story });

        Assert.Single(completed);
        Assert.Single(jira.Queries);
    }

    [Fact]
    public async Task Duplicates_are_not_added_twice_when_stories_share_an_epic()
    {
        var epic = Fixtures.Issue("E-1", Fixtures.InProgress, type: "Epic");
        var a = Fixtures.Issue("S-1", Fixtures.ToDo, epicLink: "E-1");
        var b = Fixtures.Issue("S-2", Fixtures.ToDo, epicLink: "E-1");

        var jira = new FakeJira(epic, a, b);
        var completed = await HierarchyCompleter.CompleteAsync(jira, Fields, new[] { a, b });

        Assert.Equal(3, completed.Count);
        Assert.Single(jira.Queries);
    }
}
