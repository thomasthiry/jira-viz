using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using JiraViz.Core.Model;

namespace JiraViz.Core.Jira;

/// <summary>
/// Talks to Jira Server / Data Center over REST API v2.
///
/// Deliberately not the Cloud API: Cloud removed /rest/api/3/search in favour of
/// /rest/api/3/search/jql with nextPageToken cursors, whereas Server/DC still uses
/// startAt/maxResults offsets against /rest/api/2/search.
/// </summary>
public sealed class JiraServerClient : IJiraClient, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly JiraVizOptions _options;
    private readonly bool _ownsHttp;

    public JiraServerClient(JiraVizOptions options, HttpClient? http = null)
    {
        _options = options;
        _ownsHttp = http is null;

        if (http is null)
        {
            var handler = new HttpClientHandler();
            if (options.InsecureTls)
                handler.ServerCertificateCustomValidationCallback =
                    HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;

            http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(2) };
        }

        _http = http;
        _http.BaseAddress ??= new Uri(options.BaseUrl.TrimEnd('/') + "/");
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (!string.IsNullOrWhiteSpace(options.Token))
        {
            _http.DefaultRequestHeaders.Authorization = string.IsNullOrWhiteSpace(options.Username)
                // Personal Access Token, the norm on Jira Server 8.14+.
                ? new AuthenticationHeaderValue("Bearer", options.Token)
                // Basic, for older instances that predate PATs.
                : new AuthenticationHeaderValue("Basic", Convert.ToBase64String(
                    Encoding.UTF8.GetBytes($"{options.Username}:{options.Token}")));
        }
    }

    public async Task<string> WhoAmIAsync(CancellationToken ct = default)
    {
        using var response = await SendWithRetryAsync(
            () => new HttpRequestMessage(HttpMethod.Get, "rest/api/2/myself"), ct);

        var user = await response.Content.ReadFromJsonAsync<UserDto>(JsonOptions, ct);
        return user?.DisplayName ?? "(unknown user)";
    }

    /// <summary>
    /// Finds the Story Points and Epic Link custom field ids by name. Their numeric ids differ
    /// between instances, so they cannot be hardcoded; explicit config overrides skip the lookup.
    /// </summary>
    public async Task<ResolvedFields> ResolveFieldsAsync(CancellationToken ct = default)
    {
        if (_options.StoryPointsFieldId is not null && _options.EpicLinkFieldId is not null)
            return new ResolvedFields(_options.StoryPointsFieldId, _options.EpicLinkFieldId);

        using var response = await SendWithRetryAsync(
            () => new HttpRequestMessage(HttpMethod.Get, "rest/api/2/field"), ct);

        var all = await response.Content.ReadFromJsonAsync<List<JiraFieldDto>>(JsonOptions, ct)
                  ?? new List<JiraFieldDto>();

        return new ResolvedFields(
            _options.StoryPointsFieldId ?? FindByName(all, _options.StoryPointsFieldName),
            _options.EpicLinkFieldId ?? FindByName(all, _options.EpicLinkFieldName));
    }

    private static string? FindByName(List<JiraFieldDto> fields, string name)
        => fields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))?.Id;

    public async Task<IReadOnlyList<RawIssue>> SearchAsync(
        string jql, ResolvedFields fields, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        // Ask only for the fields the report uses. Jira returns every field otherwise, which on a
        // large project is the difference between a fast fetch and a very slow one.
        var requested = new List<string>
        {
            "summary", "status", "issuetype", "parent", "assignee",
            "updated", "created", "resolutiondate", "priority", "labels",
        };
        if (fields.StoryPointsFieldId is not null) requested.Add(fields.StoryPointsFieldId);
        if (fields.EpicLinkFieldId is not null) requested.Add(fields.EpicLinkFieldId);

        var issues = new List<RawIssue>();
        var startAt = 0;
        var total = int.MaxValue;
        var guard = 0;

        while (startAt < total)
        {
            var body = JsonSerializer.Serialize(new
            {
                jql,
                startAt,
                maxResults = _options.PageSize,
                fields = requested,
            }, JsonOptions);

            using var response = await SendWithRetryAsync(() => new HttpRequestMessage(
                HttpMethod.Post, "rest/api/2/search")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            }, ct);

            var page = await response.Content.ReadFromJsonAsync<SearchResultDto>(JsonOptions, ct)
                       ?? throw new JiraException("Search returned an empty body.");

            total = page.Total;
            foreach (var dto in page.Issues) issues.Add(IssueMapper.Map(dto, fields));

            progress?.Report(issues.Count);

            // A page of zero with work still outstanding would spin forever; stop instead.
            if (page.Issues.Count == 0) break;

            startAt += page.Issues.Count;

            if (++guard > 10_000)
                throw new JiraException("Pagination exceeded 10,000 requests; aborting as a runaway guard.");
        }

        return issues;
    }

    /// <summary>
    /// Sends a request, retrying on 429 and 5xx with exponential backoff. The request is rebuilt
    /// each attempt because an HttpRequestMessage cannot be sent twice.
    /// </summary>
    private async Task<HttpResponseMessage> SendWithRetryAsync(
        Func<HttpRequestMessage> makeRequest, CancellationToken ct)
    {
        const int maxAttempts = 5;

        for (var attempt = 1; ; attempt++)
        {
            HttpResponseMessage response;
            try
            {
                using var request = makeRequest();
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                // Transient network fault (DNS, connection reset, timeout): back off and retry.
                if (attempt >= maxAttempts) throw Unreachable(ex);
                await Task.Delay(Backoff(attempt), ct);
                continue;
            }

            if (response.IsSuccessStatusCode) return response;

            var retryable = response.StatusCode is HttpStatusCode.TooManyRequests
                            or HttpStatusCode.BadGateway
                            or HttpStatusCode.ServiceUnavailable
                            or HttpStatusCode.GatewayTimeout;

            if (retryable && attempt < maxAttempts)
            {
                var wait = response.Headers.RetryAfter?.Delta ?? Backoff(attempt);
                response.Dispose();
                await Task.Delay(wait, ct);
                continue;
            }

            var status = response.StatusCode;
            var body = await SafeReadAsync(response, ct);
            response.Dispose();
            throw new JiraException(Describe(status, body), status, body);
        }

        static TimeSpan Backoff(int attempt) => TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt - 1));
    }

    /// <summary>
    /// Wraps a network-level failure, which is the likeliest first-run problem, in something a
    /// reader can act on rather than a bare stack trace.
    /// </summary>
    private JiraException Unreachable(Exception ex) => new(
        $"Could not reach Jira at {_http.BaseAddress}.{Environment.NewLine}" +
        $"  {ex.Message}{Environment.NewLine}" +
        "  Check the URL is right, that you are on the network or VPN that can see it, " +
        "and that any proxy or TLS interception is accounted for (--insecure skips certificate checks).");

    private static string Describe(HttpStatusCode status, string? body)
    {
        var hint = status switch
        {
            HttpStatusCode.Unauthorized => "Check JIRAVIZ_TOKEN; on Jira Server a Personal Access Token is sent as a bearer token.",
            HttpStatusCode.Forbidden => "The token is valid but lacks permission, or the instance requires a CAPTCHA re-login.",
            HttpStatusCode.NotFound => "Check --url points at the Jira root (the REST path is appended automatically).",
            HttpStatusCode.BadRequest => "Jira rejected the JQL. Check --jql, including quoting of field names.",
            _ => null,
        };

        var message = $"Jira request failed with {(int)status} {status}.";
        if (hint is not null) message += " " + hint;
        if (!string.IsNullOrWhiteSpace(body)) message += Environment.NewLine + Truncate(body, 600);
        return message;
    }

    private static async Task<string?> SafeReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try { return await response.Content.ReadAsStringAsync(ct); }
        catch { return null; }
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max] + "...";

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
