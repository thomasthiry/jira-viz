using JiraViz.Core.Analysis;
using Xunit;

namespace JiraViz.Core.Tests;

public class JqlComposerTests
{
    [Fact]
    public void An_empty_fragment_leaves_the_base_untouched()
    {
        Assert.Equal("project = ABC", JqlComposer.Compose("project = ABC", null));
        Assert.Equal("project = ABC", JqlComposer.Compose("project = ABC", ""));
        Assert.Equal("project = ABC", JqlComposer.Compose("project = ABC", "   "));
    }

    [Fact]
    public void A_fragment_is_anded_onto_the_base()
    {
        Assert.Equal(
            "(project = ABC) AND (fixVersion = \"24.2\")",
            JqlComposer.Compose("project = ABC", "fixVersion = \"24.2\""));
    }

    [Fact]
    public void Both_sides_are_parenthesised_so_an_OR_in_the_base_keeps_its_grouping()
    {
        // Without the brackets this would bind as `a OR (b AND c)` and quietly report on a
        // different set of issues than the reader asked for.
        var composed = JqlComposer.Compose("project = A OR project = B", "fixVersion = \"1.0\"");

        Assert.Equal("(project = A OR project = B) AND (fixVersion = \"1.0\")", composed);
    }

    [Fact]
    public void An_OR_in_the_fragment_is_grouped_too()
    {
        var composed = JqlComposer.Compose("project = ABC", "labels = a OR labels = b");

        Assert.Equal("(project = ABC) AND (labels = a OR labels = b)", composed);
    }

    [Fact]
    public void Surrounding_whitespace_is_trimmed()
    {
        Assert.Equal("(project = ABC) AND (labels = x)", JqlComposer.Compose("  project = ABC  ", "  labels = x "));
    }

    [Fact]
    public void An_empty_base_yields_the_fragment_alone()
        => Assert.Equal("labels = x", JqlComposer.Compose("", "labels = x"));
}
