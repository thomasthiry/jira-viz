using System.Globalization;
using System.Text.Json;
using JiraViz.Core.Model;

namespace JiraViz.Core.Jira;

/// <summary>
/// Flattens a Jira issue's untyped "fields" bag into a <see cref="RawIssue"/>.
///
/// Everything here is defensive: field ids differ per instance, custom fields come back null on
/// issues that do not use them, and story points arrive variously as a number or a string. A
/// missing field must never take the whole report down.
/// </summary>
public static class IssueMapper
{
    public static RawIssue Map(IssueDto dto, ResolvedFields fields)
    {
        var f = dto.Fields;

        var status = GetObject(f, "status");
        var statusName = GetString(status, "name") ?? "Unknown";
        var categoryKey = "new";
        if (status is { ValueKind: JsonValueKind.Object }
            && status.Value.TryGetProperty("statusCategory", out var cat)
            && cat.ValueKind == JsonValueKind.Object)
        {
            categoryKey = GetString(cat, "key") ?? "new";
        }

        var issueType = GetObject(f, "issuetype");
        var typeName = GetString(issueType, "name") ?? "Unknown";
        var isSubtask = issueType is { ValueKind: JsonValueKind.Object }
                        && issueType.Value.TryGetProperty("subtask", out var st)
                        && st.ValueKind == JsonValueKind.True;

        return new RawIssue
        {
            Key = dto.Key,
            Summary = GetString(f, "summary") ?? "(no summary)",
            StatusName = statusName,
            StatusCategoryKey = categoryKey,
            IssueTypeName = typeName,
            IsSubtask = isSubtask,
            ParentKey = GetString(GetObject(f, "parent"), "key"),
            EpicLinkKey = fields.EpicLinkFieldId is null ? null : ReadEpicLink(f, fields.EpicLinkFieldId),
            StoryPoints = fields.StoryPointsFieldId is null ? null : ReadNumber(f, fields.StoryPointsFieldId),
            Assignee = GetString(GetObject(f, "assignee"), "displayName"),
            Priority = GetString(GetObject(f, "priority"), "name"),
            Updated = ReadDate(f, "updated"),
            Created = ReadDate(f, "created"),
            Resolved = ReadDate(f, "resolutiondate"),
            Labels = ReadLabels(f),
        };
    }

    /// <summary>
    /// Epic Link is normally a plain issue-key string, but instances that have migrated to the
    /// unified Parent field expose it as an object, so both shapes are accepted.
    /// </summary>
    private static string? ReadEpicLink(JsonElement fields, string fieldId)
    {
        if (!TryGet(fields, fieldId, out var el)) return null;
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Object => GetString(el, "key"),
            _ => null,
        };
    }

    /// <summary>Story points arrive as a JSON number on most instances and a string on some.</summary>
    private static double? ReadNumber(JsonElement fields, string fieldId)
    {
        if (!TryGet(fields, fieldId, out var el)) return null;
        return el.ValueKind switch
        {
            JsonValueKind.Number => el.TryGetDouble(out var d) ? d : null,
            JsonValueKind.String => double.TryParse(
                el.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var s) ? s : null,
            _ => null,
        };
    }

    private static DateTimeOffset? ReadDate(JsonElement fields, string name)
    {
        if (!TryGet(fields, name, out var el) || el.ValueKind != JsonValueKind.String) return null;
        return DateTimeOffset.TryParse(
            el.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;
    }

    private static IReadOnlyList<string> ReadLabels(JsonElement fields)
    {
        if (!TryGet(fields, "labels", out var el) || el.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        return el.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => x.GetString()!)
            .ToList();
    }

    private static JsonElement? GetObject(JsonElement parent, string name)
        => TryGet(parent, name, out var el) && el.ValueKind == JsonValueKind.Object ? el : null;

    private static string? GetString(JsonElement? parent, string name)
        => parent is null ? null : GetString(parent.Value, name);

    private static string? GetString(JsonElement parent, string name)
        => TryGet(parent, name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    private static bool TryGet(JsonElement parent, string name, out JsonElement value)
    {
        value = default;
        if (parent.ValueKind != JsonValueKind.Object) return false;
        if (!parent.TryGetProperty(name, out var found)) return false;
        if (found.ValueKind == JsonValueKind.Null) return false;
        value = found;
        return true;
    }
}
