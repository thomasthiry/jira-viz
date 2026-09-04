using System.Text.Json.Serialization;

namespace JiraViz.Core.Jira;

/// <summary>Wire-format DTOs mirroring the Jira Server/DC REST API v2 responses.</summary>
public sealed class JiraFieldDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("custom")] public bool Custom { get; set; }
}

public sealed class SearchResultDto
{
    [JsonPropertyName("startAt")] public int StartAt { get; set; }
    [JsonPropertyName("maxResults")] public int MaxResults { get; set; }
    [JsonPropertyName("total")] public int Total { get; set; }
    [JsonPropertyName("issues")] public List<IssueDto> Issues { get; set; } = new();
}

public sealed class IssueDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("key")] public string Key { get; set; } = "";

    /// <summary>
    /// Left as a raw element bag because the Story Points and Epic Link fields are custom
    /// ids discovered at runtime and so cannot be named at compile time.
    /// </summary>
    [JsonPropertyName("fields")] public System.Text.Json.JsonElement Fields { get; set; }
}

public sealed class StatusDto
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("statusCategory")] public StatusCategoryDto? StatusCategory { get; set; }
}

public sealed class StatusCategoryDto
{
    /// <summary>One of "new", "indeterminate", "done".</summary>
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
}

public sealed class IssueTypeDto
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("subtask")] public bool Subtask { get; set; }
}

public sealed class UserDto
{
    [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
}

public sealed class ParentDto
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
}

public sealed class PriorityDto
{
    [JsonPropertyName("name")] public string? Name { get; set; }
}
