using JiraViz.Core.Model;

namespace JiraViz.Core.Jira;

/// <summary>The custom field ids resolved for this instance.</summary>
public sealed record ResolvedFields(string? StoryPointsFieldId, string? EpicLinkFieldId);

public interface IJiraClient
{
    /// <summary>Verifies credentials, returning the display name of the authenticated user.</summary>
    Task<string> WhoAmIAsync(CancellationToken ct = default);

    /// <summary>Locates the Story Points and Epic Link custom field ids for this instance.</summary>
    Task<ResolvedFields> ResolveFieldsAsync(CancellationToken ct = default);

    /// <summary>Fetches every issue matching the JQL, following pagination to the end.</summary>
    Task<IReadOnlyList<RawIssue>> SearchAsync(
        string jql, ResolvedFields fields, IProgress<int>? progress = null, CancellationToken ct = default);
}
