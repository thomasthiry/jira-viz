namespace JiraViz.Core.Analysis;

/// <summary>
/// Layers a view's JQL fragment on top of the base query.
/// </summary>
public static class JqlComposer
{
    /// <summary>
    /// Combines the base scope with a view fragment. Both sides are parenthesised: a base of
    /// "a OR b" ANDed naively with "c" would bind as "a OR (b AND c)" and quietly report on the
    /// wrong issues, which is the kind of bug that produces a plausible-looking wrong answer.
    /// </summary>
    public static string Compose(string baseJql, string? viewJql)
    {
        var left = (baseJql ?? "").Trim();
        var right = (viewJql ?? "").Trim();

        if (right.Length == 0) return left;
        if (left.Length == 0) return right;

        return $"({left}) AND ({right})";
    }
}
