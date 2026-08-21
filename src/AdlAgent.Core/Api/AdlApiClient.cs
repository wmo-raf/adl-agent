using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AdlAgent.Core.Serialization;
using Microsoft.Extensions.Logging;

namespace AdlAgent.Core.Api;

/// <summary>
/// <see cref="IAdlApiClient"/> over HTTPS -- the only network path this
/// product ever needs.
/// </summary>
/// <remarks>
/// This class holds no policy. It does not decide when to call, what to do
/// about a failure, or whether the agent is still paired; it turns one
/// request into one typed answer or one typed exception, and the loops above
/// decide what that means. That division is why a 401 arrives upstairs as
/// <see cref="DeviceRevokedException"/> rather than as a status code somebody
/// has to remember to check.
/// </remarks>
public sealed class AdlApiClient : IAdlApiClient
{
    /// <summary>
    /// The name the <see cref="HttpClient"/> is registered under. Named
    /// rather than typed so a head can add handlers (a proxy, a pinned
    /// certificate) without the core knowing.
    /// </summary>
    public const string HttpClientName = "adl";

    /// <summary>Where the agent's own version rides on every call.</summary>
    public const string VersionHeader = "X-Agent-Version";

    private readonly IHttpClientFactory _clients;
    private readonly ILogger<AdlApiClient> _logger;

    /// <remarks>
    /// A factory rather than a single <see cref="HttpClient"/>, because this
    /// class is a singleton inside a service that runs for months. A client
    /// held for that long pins the connection it first opened and never
    /// notices its ADL instance moving -- a DNS change nobody in-country can
    /// diagnose. Asking the factory per call keeps the handler pool's
    /// rotation working.
    /// </remarks>
    public AdlApiClient(IHttpClientFactory clients, ILogger<AdlApiClient> logger)
    {
        _clients = clients;
        _logger = logger;
    }

    public Task<PairResponse> PairAsync(string pairingCode, CancellationToken cancellationToken = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "pair/")
        {
            Content = JsonContent.Create(
                new { pairing_code = pairingCode }, options: AgentJson.Options),
        };

        return SendAsync<PairResponse>(request, cancellationToken);
    }

    public Task<SyncResponse> SyncAsync(string token, CancellationToken cancellationToken = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "sync/");

        Authorize(request, token);

        return SendAsync<SyncResponse>(request, cancellationToken);
    }

    public Task<HeartbeatResponse> HeartbeatAsync(
        string token, HeartbeatRequest heartbeat, CancellationToken cancellationToken = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "heartbeat/")
        {
            Content = JsonContent.Create(heartbeat, options: AgentJson.Options),
        };

        Authorize(request, token);

        return SendAsync<HeartbeatResponse>(request, cancellationToken);
    }

    private static void Authorize(HttpRequestMessage request, string token)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private async Task<T> SendAsync<T>(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.TryAddWithoutValidation(VersionHeader, AgentVersion.Current);

        using var http = _clients.CreateClient(HttpClientName);

        HttpResponseMessage response;

        try
        {
            response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new AdlUnreachableException(
                $"Could not reach ADL at {http.BaseAddress}: {exception.Message}", exception);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            // A cancellation nobody asked for is the request timeout, which is
            // an unreachable ADL by another name.
            throw new AdlUnreachableException(
                $"ADL at {http.BaseAddress} did not answer within {http.Timeout}.", exception);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw await ReadFailureAsync(response, cancellationToken).ConfigureAwait(false);
            }

            T? body;

            try
            {
                body = await response.Content
                    .ReadFromJsonAsync<T>(AgentJson.Options, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is JsonException or NotSupportedException)
            {
                // A success status carrying something that is not the agent
                // API's answer -- most often a captive portal or a reverse
                // proxy's error page in front of ADL. Reported as a refusal
                // rather than thrown raw, so the loops handle it like every
                // other bad answer.
                throw new AdlRequestException(
                    response.StatusCode,
                    "invalid_response",
                    $"ADL answered {(int)response.StatusCode} with something that is not an agent API response.");
            }

            if (body is null)
            {
                throw new AdlRequestException(
                    response.StatusCode,
                    "empty_response",
                    "ADL answered with an empty body where a result was expected.");
            }

            return body;
        }
    }

    private async Task<AdlRequestException> ReadFailureAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var (code, detail) = await ReadErrorEnvelopeAsync(response, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            _logger.LogWarning(
                "ADL refused this device's token ({Code}). Re-pairing is needed.", code);

            return new DeviceRevokedException(code, detail);
        }

        return new AdlRequestException(response.StatusCode, code, detail);
    }

    /// <summary>
    /// The plugin's <c>{"code", "detail"}</c> envelope, or a stand-in.
    /// </summary>
    /// <remarks>
    /// A stand-in is needed more often than the envelope suggests: a 502 from
    /// a reverse proxy in front of ADL is an HTML page, and the agent still
    /// has to say something a technician can act on.
    /// </remarks>
    private static async Task<(string Code, string Detail)> ReadErrorEnvelopeAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var fallback = ($"http_{(int)response.StatusCode}",
            $"ADL answered {(int)response.StatusCode} {response.ReasonPhrase}.");

        try
        {
            var envelope = await response.Content
                .ReadFromJsonAsync<ErrorEnvelope>(AgentJson.Options, cancellationToken)
                .ConfigureAwait(false);

            if (envelope is null || string.IsNullOrWhiteSpace(envelope.Detail))
            {
                return fallback;
            }

            return (
                string.IsNullOrWhiteSpace(envelope.Code) ? fallback.Item1 : envelope.Code,
                envelope.Detail);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return fallback;
        }
    }

    private sealed record ErrorEnvelope
    {
        public string Code { get; init; } = "";
        public string Detail { get; init; } = "";
    }
}
