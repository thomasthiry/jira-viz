using System.Net;

namespace JiraViz.Core.Jira;

public sealed class JiraException(string message, HttpStatusCode? status = null, string? body = null)
    : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
    public string? Body { get; } = body;
}
