using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using JiraViz.Core.Model;

namespace JiraViz.Core.Reporting;

/// <summary>
/// Renders the analysis into a single self-contained HTML file by injecting the model as JSON
/// into an embedded template. A plain marker replacement rather than a templating engine: the
/// output stays diffable and the template can be opened straight in a browser while developing.
/// </summary>
public static class ReportWriter
{
    private const string DataMarker = "/*__JIRAVIZ_DATA__*/";
    private const string TemplateResource = "JiraViz.Core.Assets.report.template.html";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Enum names read far better than ordinals in the browser devtools.
        Converters = { new JsonStringEnumConverter() },
        // The payload is embedded in a <script> block, so anything that could close that
        // element early has to be escaped. Serialize with the strictest built-in encoder.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Default,
    };

    public static string Render(ReportDocument document, string template)
    {
        if (!template.Contains(DataMarker, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"The report template is missing its '{DataMarker}' marker.");

        var json = JsonSerializer.Serialize(document, JsonOptions);

        // Belt and braces: the encoder above already escapes '<', but a literal "</script>"
        // inside the payload would end the block, so make that impossible.
        json = json.Replace("</", @"<\/", StringComparison.Ordinal);

        return template.Replace(DataMarker, json, StringComparison.Ordinal);
    }

    public static async Task WriteAsync(ReportDocument document, string outputPath, CancellationToken ct = default)
    {
        var html = Render(document, await LoadTemplateAsync(ct));

        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        await File.WriteAllTextAsync(outputPath, html, new System.Text.UTF8Encoding(false), ct);
    }

    /// <summary>
    /// Loads the template from the entry assembly's embedded resources, preferring a file on
    /// disk beside the binary when present so the HTML can be iterated on without a rebuild.
    /// </summary>
    public static async Task<string> LoadTemplateAsync(CancellationToken ct = default)
    {
        var onDisk = Path.Combine(AppContext.BaseDirectory, "Assets", "report.template.html");
        if (File.Exists(onDisk)) return await File.ReadAllTextAsync(onDisk, ct);

        foreach (var assembly in new[] { Assembly.GetEntryAssembly(), typeof(ReportWriter).Assembly })
        {
            var stream = assembly?.GetManifestResourceStream(TemplateResource);
            if (stream is null) continue;

            using (stream)
            using (var reader = new StreamReader(stream))
                return await reader.ReadToEndAsync(ct);
        }

        throw new FileNotFoundException(
            $"Could not find the report template, as '{onDisk}' or embedded resource '{TemplateResource}'.");
    }
}
