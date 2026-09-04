using JiraViz.Core;
using JiraViz.Core.Analysis;
using JiraViz.Core.Model;
using Xunit;

namespace JiraViz.Core.Tests;

public class StatusBucketerTests
{
    [Theory]
    [InlineData("done", StatusBucket.Done)]
    [InlineData("indeterminate", StatusBucket.InProgress)]
    [InlineData("new", StatusBucket.NotStarted)]
    [InlineData("DONE", StatusBucket.Done)]
    public void Maps_from_the_status_category(string categoryKey, StatusBucket expected)
        => Assert.Equal(expected, new StatusBucketer().Bucket("Anything", categoryKey));

    [Fact]
    public void An_unrecognised_category_is_treated_as_not_started()
    {
        // Over-reporting progress is the more damaging error in a status report.
        Assert.Equal(StatusBucket.NotStarted, new StatusBucketer().Bucket("Weird", "banana"));
    }

    [Fact]
    public void A_configured_override_beats_the_category()
    {
        var bucketer = new StatusBucketer(new Dictionary<string, string>
        {
            ["Awaiting Release"] = "Done",
        });

        Assert.Equal(StatusBucket.Done, bucketer.Bucket("Awaiting Release", "indeterminate"));
        Assert.Equal(StatusBucket.InProgress, bucketer.Bucket("In Review", "indeterminate"));
    }

    [Fact]
    public void Override_matching_ignores_case()
    {
        var bucketer = new StatusBucketer(new Dictionary<string, string> { ["uat"] = "InProgress" });
        Assert.Equal(StatusBucket.InProgress, bucketer.Bucket("UAT", "new"));
    }

    [Fact]
    public void A_nonsense_override_value_fails_loudly_at_construction()
    {
        var ex = Assert.Throws<JiraVizConfigurationException>(() =>
            new StatusBucketer(new Dictionary<string, string> { ["Blocked"] = "Purple" }));

        Assert.Contains("Blocked", ex.Message);
    }
}
