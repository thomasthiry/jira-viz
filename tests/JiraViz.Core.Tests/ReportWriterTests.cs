using JiraViz.Core.Analysis;
using JiraViz.Core.Model;
using JiraViz.Core.Reporting;
using Xunit;

namespace JiraViz.Core.Tests;

public class ReportWriterTests
{
    private static ReportDocument Doc(string summary = "Epic one", params ReportView[] extra)
    {
        var epic = Fixtures.Epic("E-1", summary, Fixtures.Story("S-1", Fixtures.Done, points: 3));
        var model = new ProgressCalculator(new StatusBucketer(), 14).Build(
            new[] { epic }, new[] { "a warning" },
            "https://jira.example.com/", "project = X",
            new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero));

        var views = new List<ReportView>
        {
            new() { Name = "All work", Jql = "project = X", Model = model },
        };
        views.AddRange(extra);

        return new ReportDocument
        {
            ProjectName = "Demo project",
            GeneratedAt = new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero),
            Views = views,
        };
    }

    [Fact]
    public void Replaces_the_marker_with_the_serialized_model()
    {
        var html = ReportWriter.Render(Doc(), "<html>/*__JIRAVIZ_DATA__*/</html>");

        Assert.DoesNotContain("__JIRAVIZ_DATA__", html);
        Assert.Contains("\"epics\"", html);
        Assert.Contains("Epic one", html);
    }

    [Fact]
    public void Enum_values_serialize_by_name()
    {
        var html = ReportWriter.Render(Doc(), "/*__JIRAVIZ_DATA__*/");
        Assert.Contains("\"Done\"", html);
    }

    [Fact]
    public void The_trailing_slash_is_trimmed_from_the_base_url()
    {
        var html = ReportWriter.Render(Doc(), "/*__JIRAVIZ_DATA__*/");
        Assert.Contains("https://jira.example.com\"", html);
    }

    [Fact]
    public void A_closing_script_tag_in_the_data_cannot_break_out_of_the_block()
    {
        // A summary can contain anything a user typed into Jira, including markup.
        var html = ReportWriter.Render(
            Doc("evil </script><script>alert(1)</script>"), "/*__JIRAVIZ_DATA__*/");

        Assert.DoesNotContain("</script>", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_template_without_the_marker_fails_loudly()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => ReportWriter.Render(Doc(), "<html>no marker here</html>"));

        Assert.Contains("__JIRAVIZ_DATA__", ex.Message);
    }

    [Fact]
    public async Task The_shipped_template_contains_the_marker()
    {
        // Guards against the template and the writer drifting apart.
        var template = await ReportWriter.LoadTemplateAsync();
        Assert.Contains("/*__JIRAVIZ_DATA__*/", template);
    }
}
