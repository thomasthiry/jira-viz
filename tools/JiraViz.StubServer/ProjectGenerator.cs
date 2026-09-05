using System.Globalization;
using System.Text.Json.Nodes;

namespace JiraViz.StubServer;

/// <summary>
/// Synthesizes a deterministic Jira project in the exact JSON shape Server/DC returns.
///
/// The generated portfolio deliberately covers the awkward cases the report has to survive:
/// a finished epic, one mid-flight, a large epic at zero percent, an epic with no story points
/// at all (which forces the count fallback), stories with no epic, an empty epic, and several
/// in-progress issues left stale enough to register as stalled.
/// </summary>
public sealed class ProjectGenerator
{
    // Deliberately non-obvious ids, so that field discovery by name is genuinely exercised
    // rather than accidentally passing against a guessable default.
    public const string StoryPointsFieldId = "customfield_10237";
    public const string EpicLinkFieldId = "customfield_11004";

    private const string ProjectKey = "DEMO";

    private readonly Random _random;
    private readonly DateTimeOffset _now;
    private int _nextId = 10000;
    private int _nextNumber = 1;

    public List<JsonObject> Issues { get; } = new();

    public ProjectGenerator(int seed = 20260904, DateTimeOffset? now = null)
    {
        _random = new Random(seed);
        _now = now ?? DateTimeOffset.UtcNow;
        Generate();
    }

    private void Generate()
    {
        // name, storyCount, completionBias (0..1), pointed, subtaskDensity
        var blueprints = new (string Name, int Stories, double Bias, bool Pointed)[]
        {
            ("Checkout rework", 9, 0.72, true),
            ("Search relevance v2", 7, 0.18, true),
            ("Reporting and exports", 11, 0.00, true),   // large and untouched: the headline risk
            ("Infrastructure hardening", 6, 0.90, true),
            ("Payments migration", 8, 0.45, true),
            ("Mobile onboarding", 5, 1.00, true),        // finished
            ("Accessibility audit", 7, 0.35, false),     // unpointed: forces the count fallback
            ("Data retention compliance", 4, 0.10, true),
        };

        foreach (var bp in blueprints)
        {
            var epicKey = NextKey();
            var epicBucket = bp.Bias >= 1.0 ? "done" : bp.Bias <= 0.0 ? "new" : "indeterminate";
            Issues.Add(MakeIssue(epicKey, "Epic", bp.Name, epicBucket, points: null, epicLink: null, parent: null));

            for (var i = 0; i < bp.Stories; i++)
            {
                var storyKey = NextKey();
                var bucket = PickBucket(bp.Bias);
                double? points = bp.Pointed ? Fibonacci() : null;
                var release = PickRelease();

                Issues.Add(MakeIssue(
                    storyKey, "Story", $"{bp.Name}: {StorySummary(i)}", bucket,
                    points, epicLink: epicKey, parent: null, fixVersion: release));

                // Roughly half the stories are broken down; those are what give the report its
                // partial-credit signal for work that is underway but not finished.
                if (_random.NextDouble() < 0.55)
                {
                    var subtaskCount = _random.Next(2, 6);
                    for (var s = 0; s < subtaskCount; s++)
                    {
                        var subBucket = bucket switch
                        {
                            "done" => "done",
                            "new" => bp.Bias > 0 && _random.NextDouble() < 0.15 ? "done" : "new",
                            _ => PickBucket(0.5),
                        };
                        // Subtasks inherit their story's release, as they do on a real instance.
                        Issues.Add(MakeIssue(
                            NextKey(), "Sub-task", $"{StorySummary(i)} - step {s + 1}", subBucket,
                            points: null, epicLink: null, parent: storyKey, fixVersion: release));
                    }
                }
            }
        }

        // An epic with nothing under it, to prove zero-size epics do not divide by zero.
        Issues.Add(MakeIssue(NextKey(), "Epic", "Future: billing overhaul", "new", null, null, null));

        // Stories with no epic at all, which must land in the synthetic bucket.
        foreach (var summary in new[] { "Fix flaky login test", "Bump logging library", "Rotate staging secrets" })
            Issues.Add(MakeIssue(NextKey(), "Story", summary, PickBucket(0.4), Fibonacci(), null, null, PickRelease()));
    }

    private JsonObject MakeIssue(
        string key, string type, string summary, string categoryKey,
        double? points, string? epicLink, string? parent, string? fixVersion = null)
    {
        var isSubtask = type == "Sub-task";
        var statusName = categoryKey switch
        {
            "done" => _random.NextDouble() < 0.2 ? "Closed" : "Done",
            "indeterminate" => _random.NextDouble() < 0.35 ? "In Review" : "In Progress",
            _ => _random.NextDouble() < 0.25 ? "Backlog" : "To Do",
        };

        // In-progress issues get a wide spread of update ages so a believable number of them
        // fall the far side of the stalled threshold.
        var ageDays = categoryKey == "indeterminate"
            ? _random.NextDouble() < 0.25 ? _random.Next(15, 70) : _random.Next(0, 12)
            : _random.Next(0, 60);

        var updated = _now.AddDays(-ageDays).AddHours(-_random.Next(0, 24));
        var created = updated.AddDays(-_random.Next(5, 180));

        var fields = new JsonObject
        {
            ["summary"] = summary,
            ["status"] = new JsonObject
            {
                ["name"] = statusName,
                ["statusCategory"] = new JsonObject
                {
                    ["key"] = categoryKey,
                    ["name"] = categoryKey switch
                    {
                        "done" => "Done",
                        "indeterminate" => "In Progress",
                        _ => "To Do",
                    },
                },
            },
            ["issuetype"] = new JsonObject { ["name"] = type, ["subtask"] = isSubtask },
            ["assignee"] = _random.NextDouble() < 0.85
                ? new JsonObject { ["displayName"] = PickName() }
                : null,
            ["priority"] = new JsonObject { ["name"] = PickPriority() },
            ["created"] = Format(created),
            ["updated"] = Format(updated),
            ["resolutiondate"] = categoryKey == "done" ? Format(updated) : null,
            ["labels"] = Labels(),
            ["fixVersions"] = fixVersion is null
                ? new JsonArray()
                : new JsonArray(new JsonObject { ["name"] = fixVersion, ["released"] = false }),
            [StoryPointsFieldId] = points,
            [EpicLinkFieldId] = epicLink,
        };

        if (parent is not null)
            fields["parent"] = new JsonObject { ["key"] = parent };

        return new JsonObject
        {
            ["id"] = (_nextId++).ToString(CultureInfo.InvariantCulture),
            ["key"] = key,
            ["fields"] = fields,
        };
    }


    /// <summary>
    /// Releases the demo project ships into. Spread unevenly and with some issues unassigned,
    /// so layered milestone views return genuinely different slices rather than the same set.
    /// </summary>
    public static readonly string[] Releases = { "24.2", "24.3", "24.4" };

    /// <summary>
    /// Picks the release a story is scheduled into. Deliberately assigned at story level only:
    /// on a real instance epics rarely carry a fixVersion and subtasks inherit their story, and
    /// that is exactly what makes a milestone query return a hierarchy full of holes.
    /// </summary>
    private string? PickRelease()
    {
        var roll = _random.NextDouble();
        if (roll < 0.18) return null;   // not yet scheduled
        return roll < 0.50 ? Releases[0] : roll < 0.80 ? Releases[1] : Releases[2];
    }

    private JsonArray Labels()
    {
        var labels = new JsonArray();
        if (_random.NextDouble() < 0.22) labels.Add("tech-debt");
        if (_random.NextDouble() < 0.15) labels.Add("customer-raised");
        return labels;
    }
    private string NextKey() => $"{ProjectKey}-{_nextNumber++}";

    /// <summary>Picks a status bucket so that, on average, a share equal to bias comes out done.</summary>
    private string PickBucket(double bias)
    {
        // The extremes are absolute, not probabilistic: the generated portfolio has to
        // contain a genuinely untouched epic and a genuinely finished one for the report
        // to be exercised against them.
        if (bias <= 0) return "new";
        if (bias >= 1) return "done";

        var roll = _random.NextDouble();
        if (roll < bias) return "done";
        // Of what remains, a modest slice is actively in flight and the rest untouched.
        return roll < bias + (1 - bias) * 0.35 ? "indeterminate" : "new";
    }

    private double Fibonacci()
    {
        double[] scale = { 1, 2, 3, 5, 8, 13 };
        return scale[_random.Next(scale.Length)];
    }

    private string PickName()
    {
        string[] names = { "Ana Duarte", "Ben Okafor", "Chloe Martin", "Dmitri Volkov", "Elif Yilmaz", "Femi Adeyemi" };
        return names[_random.Next(names.Length)];
    }

    private string PickPriority()
    {
        string[] priorities = { "Highest", "High", "Medium", "Medium", "Low" };
        return priorities[_random.Next(priorities.Length)];
    }

    private static string Format(DateTimeOffset value)
        => value.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture);

    /// <summary>The Jira /rest/api/2/field catalogue for this fake instance.</summary>
    public static JsonArray FieldCatalogue() => new(
        Field("summary", "Summary", false),
        Field("status", "Status", false),
        Field("issuetype", "Issue Type", false),
        Field("parent", "Parent", false),
        Field("assignee", "Assignee", false),
        Field("priority", "Priority", false),
        Field("created", "Created", false),
        Field("updated", "Updated", false),
        Field("resolutiondate", "Resolved", false),
        Field("labels", "Labels", false),
        Field("customfield_10101", "Sprint", true),
        Field("customfield_10199", "Story Point Estimate", true), // a near-miss name, to catch sloppy matching
        Field(StoryPointsFieldId, "Story Points", true),
        Field(EpicLinkFieldId, "Epic Link", true),
        Field("customfield_11005", "Epic Name", true));

    private static JsonObject Field(string id, string name, bool custom)
        => new() { ["id"] = id, ["name"] = name, ["custom"] = custom };

    private static string StorySummary(int index)
    {
        string[] verbs = { "Design", "Implement", "Migrate", "Refactor", "Document", "Instrument", "Harden", "Validate", "Roll out", "Benchmark", "Retire" };
        string[] nouns = { "the API contract", "the read path", "the admin screen", "the audit trail", "the batch job", "the cache layer", "the webhook handler", "the error states", "the feature flag", "the schema", "the client SDK" };
        return $"{verbs[index % verbs.Length]} {nouns[index % nouns.Length]}";
    }
}
