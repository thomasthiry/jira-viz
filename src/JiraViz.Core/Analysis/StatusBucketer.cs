using JiraViz.Core.Model;

namespace JiraViz.Core.Analysis;

/// <summary>
/// Collapses arbitrary workflow statuses into the three reporting buckets. Uses Jira's own
/// statusCategory by default, since that is the one thing every instance agrees on, and lets
/// config override individual status names for workflows where the category is misleading.
/// </summary>
public sealed class StatusBucketer
{
    private readonly IReadOnlyDictionary<string, StatusBucket> _overrides;

    public StatusBucketer(IReadOnlyDictionary<string, string>? overrides = null)
    {
        var map = new Dictionary<string, StatusBucket>(StringComparer.OrdinalIgnoreCase);
        if (overrides is not null)
        {
            foreach (var (status, bucket) in overrides)
            {
                if (TryParseBucket(bucket, out var parsed))
                    map[status] = parsed;
                else
                    throw new JiraVizConfigurationException(
                        $"StatusOverrides['{status}'] is '{bucket}'; expected NotStarted, InProgress or Done.");
            }
        }
        _overrides = map;
    }

    public StatusBucket Bucket(string statusName, string statusCategoryKey)
    {
        if (_overrides.TryGetValue(statusName, out var overridden))
            return overridden;

        return statusCategoryKey?.ToLowerInvariant() switch
        {
            "done" => StatusBucket.Done,
            "indeterminate" => StatusBucket.InProgress,
            "new" => StatusBucket.NotStarted,
            // An unrecognised category is safer treated as unstarted than as finished:
            // over-reporting progress is the more damaging error in a status report.
            _ => StatusBucket.NotStarted,
        };
    }

    private static bool TryParseBucket(string value, out StatusBucket bucket)
    {
        switch (value?.Trim().Replace(" ", "").ToLowerInvariant())
        {
            case "notstarted": case "todo": case "new": bucket = StatusBucket.NotStarted; return true;
            case "inprogress": case "indeterminate": bucket = StatusBucket.InProgress; return true;
            case "done": case "complete": case "completed": bucket = StatusBucket.Done; return true;
            default: bucket = StatusBucket.NotStarted; return false;
        }
    }
}
