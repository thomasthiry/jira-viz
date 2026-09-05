using JiraViz.Core.Model;

namespace JiraViz.Core.Jira;

/// <summary>
/// Pulls in the ancestors a filtered query left behind.
///
/// A milestone query is the motivating case: on a real instance a fixVersion sits on the story,
/// not on its epic, so "fixVersion = 24.2" returns stories whose epics are absent. Left alone
/// every one of those stories falls into the synthetic "(no epic)" bucket and the view is
/// useless. The same applies to a subtask whose parent story did not match.
///
/// Fetching the missing ancestors by key restores the hierarchy without widening the view: an
/// epic pulled in this way still shows only the stories the query actually matched, which is
/// what "what is left for this milestone" should mean.
/// </summary>
public static class HierarchyCompleter
{
    /// <summary>Keys per follow-up query. Requests are POSTed, so this is about Jira's own limits.</summary>
    private const int ChunkSize = 100;

    /// <summary>Guards against a pathological chain of missing ancestors.</summary>
    private const int MaxRounds = 3;

    public static async Task<IReadOnlyList<RawIssue>> CompleteAsync(
        IJiraClient client,
        ResolvedFields fields,
        IReadOnlyList<RawIssue> issues,
        CancellationToken ct = default)
    {
        var all = issues.ToList();
        var present = new HashSet<string>(all.Select(i => i.Key), StringComparer.OrdinalIgnoreCase);

        for (var round = 0; round < MaxRounds; round++)
        {
            var missing = MissingAncestors(all, present);
            if (missing.Count == 0) break;

            var fetched = new List<RawIssue>();
            foreach (var chunk in Chunk(missing, ChunkSize))
            {
                var jql = "key in (" + string.Join(", ", chunk) + ")";
                fetched.AddRange(await client.SearchAsync(jql, fields, null, ct));
            }

            // Nothing came back - the keys are unreadable or deleted. Stop rather than loop.
            if (fetched.Count == 0) break;

            foreach (var issue in fetched)
                if (present.Add(issue.Key))
                    all.Add(issue);
        }

        return all;
    }

    /// <summary>Keys referenced as an epic or a parent by something present, but absent themselves.</summary>
    private static List<string> MissingAncestors(List<RawIssue> issues, HashSet<string> present)
    {
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var issue in issues)
        {
            if (issue.EpicLinkKey is { Length: > 0 } epic && !present.Contains(epic)) wanted.Add(epic);
            if (issue.ParentKey is { Length: > 0 } parent && !present.Contains(parent)) wanted.Add(parent);
        }

        return wanted.ToList();
    }

    private static IEnumerable<List<string>> Chunk(List<string> keys, int size)
    {
        for (var i = 0; i < keys.Count; i += size)
            yield return keys.GetRange(i, Math.Min(size, keys.Count - i));
    }
}
