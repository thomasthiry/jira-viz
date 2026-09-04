namespace JiraViz.Core;

/// <summary>Everything the app needs to know, resolved from CLI args, env vars and appsettings.json.</summary>
public sealed class JiraVizOptions
{
    public string BaseUrl { get; set; } = "";
    public string Jql { get; set; } = "";
    public string OutputPath { get; set; } = "report.html";

    /// <summary>Personal Access Token. Bearer auth when <see cref="Username"/> is empty, Basic otherwise.</summary>
    public string Token { get; set; } = "";

    /// <summary>Set only for instances too old for PATs, which need Basic auth.</summary>
    public string? Username { get; set; }

    /// <summary>Days without an update before an in-progress issue counts as stalled.</summary>
    public int StalledDays { get; set; } = 14;

    /// <summary>Name of the epic issue type; renamed on some instances.</summary>
    public string EpicIssueTypeName { get; set; } = "Epic";

    /// <summary>Field names to look for during discovery. Overridden by the explicit ids below.</summary>
    public string StoryPointsFieldName { get; set; } = "Story Points";
    public string EpicLinkFieldName { get; set; } = "Epic Link";

    /// <summary>Explicit customfield_XXXXX ids, skipping discovery when set.</summary>
    public string? StoryPointsFieldId { get; set; }
    public string? EpicLinkFieldId { get; set; }

    /// <summary>Maps a status name to a bucket, for workflows whose statusCategory is unhelpful.</summary>
    public Dictionary<string, string> StatusOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public int PageSize { get; set; } = 100;
    public bool OpenWhenDone { get; set; }

    /// <summary>Skips TLS validation, for instances behind corporate interception. Opt-in only.</summary>
    public bool InsecureTls { get; set; }

    public void Validate()
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(BaseUrl)) problems.Add("--url is required (e.g. https://jira.example.com)");
        else if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out _)) problems.Add($"--url is not a valid absolute URL: {BaseUrl}");
        if (string.IsNullOrWhiteSpace(Jql)) problems.Add("--jql is required (e.g. \"project = ABC\")");
        if (StalledDays < 1) problems.Add("--stalled-days must be at least 1");
        if (PageSize is < 1 or > 1000) problems.Add("--page-size must be between 1 and 1000");

        if (problems.Count > 0)
            throw new JiraVizConfigurationException(string.Join(Environment.NewLine, problems.Select(p => "  - " + p)));
    }
}

public sealed class JiraVizConfigurationException(string message) : Exception(message);
