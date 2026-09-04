using System.Text.Json;
using JiraViz.Core.Jira;
using Xunit;

namespace JiraViz.Core.Tests;

public class IssueMapperTests
{
    private static readonly ResolvedFields Fields = new("customfield_10237", "customfield_11004");

    private static IssueDto Dto(string fieldsJson) => new()
    {
        Key = "ABC-1",
        Fields = JsonDocument.Parse(fieldsJson).RootElement,
    };

    [Fact]
    public void Maps_a_fully_populated_issue()
    {
        var issue = IssueMapper.Map(Dto("""
        {
          "summary": "Do the thing",
          "status": { "name": "In Review", "statusCategory": { "key": "indeterminate" } },
          "issuetype": { "name": "Story", "subtask": false },
          "assignee": { "displayName": "Ana Duarte" },
          "priority": { "name": "High" },
          "updated": "2026-08-30T09:15:00.000+0200",
          "labels": ["backend", "q3"],
          "customfield_10237": 8,
          "customfield_11004": "ABC-100"
        }
        """), Fields);

        Assert.Equal("Do the thing", issue.Summary);
        Assert.Equal("In Review", issue.StatusName);
        Assert.Equal("indeterminate", issue.StatusCategoryKey);
        Assert.False(issue.IsSubtask);
        Assert.Equal("Ana Duarte", issue.Assignee);
        Assert.Equal(8, issue.StoryPoints);
        Assert.Equal("ABC-100", issue.EpicLinkKey);
        Assert.Equal(new[] { "backend", "q3" }, issue.Labels);
        Assert.NotNull(issue.Updated);
    }

    [Fact]
    public void Story_points_arriving_as_a_string_still_parse()
    {
        // Some instances serialize the field as text rather than a number.
        var issue = IssueMapper.Map(Dto("""{ "customfield_10237": "13.5" }"""), Fields);
        Assert.Equal(13.5, issue.StoryPoints);
    }

    [Fact]
    public void Epic_link_arriving_as_an_object_is_read_from_its_key()
    {
        var issue = IssueMapper.Map(Dto("""{ "customfield_11004": { "key": "ABC-100" } }"""), Fields);
        Assert.Equal("ABC-100", issue.EpicLinkKey);
    }

    [Fact]
    public void Null_and_missing_fields_do_not_throw()
    {
        var issue = IssueMapper.Map(Dto("""
        { "summary": null, "assignee": null, "customfield_10237": null }
        """), Fields);

        Assert.Equal("(no summary)", issue.Summary);
        Assert.Null(issue.Assignee);
        Assert.Null(issue.StoryPoints);
        Assert.Null(issue.Updated);
        Assert.Equal("Unknown", issue.StatusName);
        // An unknown category must default to something, and "new" keeps progress honest.
        Assert.Equal("new", issue.StatusCategoryKey);
    }

    [Fact]
    public void An_unresolved_custom_field_id_yields_no_value_rather_than_an_error()
    {
        var issue = IssueMapper.Map(
            Dto("""{ "customfield_10237": 5 }"""), new ResolvedFields(null, null));

        Assert.Null(issue.StoryPoints);
        Assert.Null(issue.EpicLinkKey);
    }

    [Fact]
    public void A_subtask_is_recognised_and_keeps_its_parent()
    {
        var issue = IssueMapper.Map(Dto("""
        {
          "issuetype": { "name": "Sub-task", "subtask": true },
          "parent": { "key": "ABC-50" }
        }
        """), Fields);

        Assert.True(issue.IsSubtask);
        Assert.Equal("ABC-50", issue.ParentKey);
    }

    [Fact]
    public void An_unparseable_date_is_ignored_rather_than_throwing()
    {
        var issue = IssueMapper.Map(Dto("""{ "updated": "not a date" }"""), Fields);
        Assert.Null(issue.Updated);
    }
}
