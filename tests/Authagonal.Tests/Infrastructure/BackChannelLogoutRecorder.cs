using System.Net;

namespace Authagonal.Tests.Infrastructure;

/// <summary>
/// Stands in for the "BackChannelLogout" named client's transport and records every logout-token POST
/// the server makes. Requests to <c>unreachable.example</c> throw, standing in for a transport failure.
/// </summary>
/// <remarks>
/// The suite used to observe the fan-out with a loopback TCP listener. It cannot any more: the sender
/// runs each registered URI through the outbound SSRF guard, and a loopback address is exactly what that
/// guard exists to refuse. Recording here also makes the refusal itself observable — the assertion that
/// matters for an internal URI is that NO request was made.
/// </remarks>
public sealed class BackChannelLogoutRecorder : HttpMessageHandler
{
    private readonly List<(string Uri, string Body)> _requests = [];

    public IReadOnlyList<(string Uri, string Body)> Requests
    {
        get { lock (_requests) return [.. _requests]; }
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken);
        lock (_requests) _requests.Add((request.RequestUri!.ToString(), body));

        if (request.RequestUri!.Host == "unreachable.example")
            throw new HttpRequestException("connection refused");

        return new HttpResponseMessage(HttpStatusCode.OK);
    }
}
