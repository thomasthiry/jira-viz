using System.Text.Json;
using JiraViz.Core;

namespace JiraViz.Cli;

/// <summary>
/// Builds the options from three layers, lowest precedence first: appsettings.json, then
/// environment variables, then command-line arguments. The token is never read from the JSON
/// file, only from the environment or an explicit flag, so it cannot be committed by accident.
/// </summary>
public static class OptionsLoader
{
    public const string TokenEnvVar = "JIRAVIZ_TOKEN";

    private static readonly JsonSerializerOptions FileJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static JiraVizOptions Load(string[] args, string? settingsPath = null)
    {
        var options = LoadFile(settingsPath);
        ApplyEnvironment(options);
        ApplyArgs(options, args);
        return options;
    }

    private static JiraVizOptions LoadFile(string? settingsPath)
    {
        var path = settingsPath ?? Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (!File.Exists(path)) return new JiraVizOptions();

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<JiraVizOptions>(json, FileJsonOptions) ?? new JiraVizOptions();
        }
        catch (JsonException ex)
        {
            throw new JiraVizConfigurationException($"Could not parse {path}: {ex.Message}");
        }
    }

    private static void ApplyEnvironment(JiraVizOptions options)
    {
        var token = Environment.GetEnvironmentVariable(TokenEnvVar);
        if (!string.IsNullOrWhiteSpace(token)) options.Token = token;

        var url = Environment.GetEnvironmentVariable("JIRAVIZ_URL");
        if (!string.IsNullOrWhiteSpace(url)) options.BaseUrl = url;

        var jql = Environment.GetEnvironmentVariable("JIRAVIZ_JQL");
        if (!string.IsNullOrWhiteSpace(jql)) options.Jql = jql;

        var user = Environment.GetEnvironmentVariable("JIRAVIZ_USER");
        if (!string.IsNullOrWhiteSpace(user)) options.Username = user;
    }

    private static void ApplyArgs(JiraVizOptions options, string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--url": options.BaseUrl = Next(args, ref i, arg); break;
                case "--jql": options.Jql = Next(args, ref i, arg); break;
                case "--out": case "-o": options.OutputPath = Next(args, ref i, arg); break;
                case "--token": options.Token = Next(args, ref i, arg); break;
                case "--user": options.Username = Next(args, ref i, arg); break;
                case "--epic-type": options.EpicIssueTypeName = Next(args, ref i, arg); break;
                case "--points-field": options.StoryPointsFieldId = Next(args, ref i, arg); break;
                case "--epic-link-field": options.EpicLinkFieldId = Next(args, ref i, arg); break;
                case "--stalled-days": options.StalledDays = NextInt(args, ref i, arg); break;
                case "--page-size": options.PageSize = NextInt(args, ref i, arg); break;
                case "--open": options.OpenWhenDone = true; break;
                case "--insecure": options.InsecureTls = true; break;
                default:
                    if (arg.StartsWith('-'))
                        throw new JiraVizConfigurationException($"Unknown option '{arg}'. Run with --help for usage.");
                    break;
            }
        }
    }

    private static string Next(string[] args, ref int i, string flag)
    {
        if (i + 1 >= args.Length) throw new JiraVizConfigurationException($"{flag} needs a value.");
        return args[++i];
    }

    private static int NextInt(string[] args, ref int i, string flag)
    {
        var raw = Next(args, ref i, flag);
        if (!int.TryParse(raw, out var value))
            throw new JiraVizConfigurationException($"{flag} needs a whole number, got '{raw}'.");
        return value;
    }

    public const string Usage = """
        JiraViz - a one-glance HTML status report for a Jira epic portfolio.

        Usage:
          JiraViz.Cli --url <jira-base-url> --jql "<query>" [options]

        Required:
          --url <url>              Jira Server/DC base URL, e.g. https://jira.example.com
          --jql "<query>"          Scope of the report, e.g. "project = ABC"

        Options:
          -o, --out <path>         Output file (default: report.html)
              --open               Open the report in the default browser when done
              --token <pat>        Personal Access Token (prefer the JIRAVIZ_TOKEN env var)
              --user <name>        Switch to Basic auth, for instances predating PATs
              --stalled-days <n>   Days without an update before in-progress work is stalled (default: 14)
              --epic-type <name>   Epic issue type name, if renamed (default: Epic)
              --points-field <id>  Story Points customfield id, skipping discovery
              --epic-link-field <id>  Epic Link customfield id, skipping discovery
              --page-size <n>      Issues per request (default: 100)
              --insecure           Skip TLS validation, for corporate interception proxies
          -h, --help               Show this help

        Environment:
          JIRAVIZ_TOKEN            Personal Access Token
          JIRAVIZ_URL, JIRAVIZ_JQL, JIRAVIZ_USER
        """;
}
