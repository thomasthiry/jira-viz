using JiraViz.Core;
using Xunit;

namespace JiraViz.Core.Tests;

public class ViewOptionsTests
{
    private static JiraVizOptions Options(params ViewOptions[] views)
    {
        var options = new JiraVizOptions
        {
            BaseUrl = "https://jira.example.com",
            Jql = "project = ABC",
        };
        options.Views.AddRange(views);
        return options;
    }

    private static ViewOptions View(string name, string jql) => new() { Name = name, Jql = jql };

    [Fact]
    public void No_views_is_valid_and_leaves_just_the_base()
        => Options().Validate();

    [Fact]
    public void Named_views_with_fragments_are_valid()
        => Options(View("Release 24.2", "fixVersion = \"24.2\""), View("Tech debt", "labels = td")).Validate();

    [Fact]
    public void Duplicate_view_names_are_rejected()
    {
        var ex = Assert.Throws<JiraVizConfigurationException>(
            () => Options(View("Release", "a = 1"), View("release", "b = 2")).Validate());

        Assert.Contains("unique", ex.Message);
    }

    [Fact]
    public void A_view_may_not_reuse_the_default_view_name()
    {
        var options = Options(View("All work", "labels = x"));

        var ex = Assert.Throws<JiraVizConfigurationException>(() => options.Validate());
        Assert.Contains("unique", ex.Message);
    }

    [Fact]
    public void A_blank_view_name_is_rejected()
    {
        var ex = Assert.Throws<JiraVizConfigurationException>(
            () => Options(View("  ", "labels = x")).Validate());

        Assert.Contains("needs a name", ex.Message);
    }

    [Fact]
    public void A_view_without_a_fragment_is_rejected()
    {
        // It would silently render a second, identical copy of the base view.
        var ex = Assert.Throws<JiraVizConfigurationException>(
            () => Options(View("Release 24.2", "")).Validate());

        Assert.Contains("needs a jql fragment", ex.Message);
    }
}
