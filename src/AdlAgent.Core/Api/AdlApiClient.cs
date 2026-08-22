using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AdlAgent.Core.Serialization;
using AdlAgent.Core.Update;
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
            Content = Body(new { pairing_code = pairingCode }),
        };

        return SendAsync<PairResponse>(request, cancellationToken);
    }

    public Task<SyncResponse> SyncAsync(string token, CancellationToken cancellationToken = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "sync/");

        Authorize(request, token);

        return SendAsync<SyncResponse>(request, cancellationToken);
    }

    public Task<ConfigWriteResponse> UpdateStationLinkConfigAsync(
        string token,
        long stationLinkId,
        JsonObject changes,
        CancellationToken cancellationToken = default)
    {
        var path = string.Create(
            CultureInfo.InvariantCulture, $"station-links/{stationLinkId}/config/");

        var request = new HttpRequestMessage(HttpMethod.Patch, path)
        {
            Content = Body(changes),
        };

        Authorize(request, token);

        return SendAsync<ConfigWriteResponse>(request, cancellationToken);
    }

    public Task<HeartbeatResponse> HeartbeatAsync(
        string token, HeartbeatRequest heartbeat, CancellationToken cancellationToken = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "heartbeat/")
        {
            Content = Body(heartbeat),
        };

        Authorize(request, token);

        return SendAsync<HeartbeatResponse>(request, cancellationToken);
    }

    public Task<ManifestResponse> ManifestAsync(
        string token, IReadOnlyList<ManifestEntry> files, CancellationToken cancellationToken = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "manifest/")
        {
            Content = Body(new ManifestRequest { Files = files }),
        };

        Authorize(request, token);

        return SendAsync<ManifestResponse>(request, cancellationToken);
    }

    public async Task<UploadResponse> UploadFileAsync(
        string token, ManifestEntry entry, string path, CancellationToken cancellationToken = default)
    {
        // Opened here rather than by the caller so that a file which vanished
        // between the manifest and its turn to be sent fails as an I/O error
        // on one file, with the stream closed on the way out either way.
        await using var file = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 64 * 1024,
            useAsync: true);

        using var body = new MultipartFormDataContent
        {
            { new StringContent(entry.StationLinkId.ToString(CultureInfo.InvariantCulture)), "station_link_id" },
            { new StringContent(entry.Name), "name" },
            { new StringContent(entry.Size.ToString(CultureInfo.InvariantCulture)), "size" },
            { new StringContent(entry.Mtime.ToString("O", CultureInfo.InvariantCulture)), "mtime" },
            { new StringContent(entry.Hash), "hash" },
            { new StreamContent(file), "file", entry.Name },
        };

        // Every part has a length it can state -- the strings trivially, the
        // file because it is seekable -- so the request goes out with a
        // Content-Length. That is not a nicety: ADL is a Django application
        // behind WSGI, where a chunked request body reaches the view as
        // nothing at all.
        var request = new HttpRequestMessage(HttpMethod.Post, "files/") { Content = body };

        Authorize(request, token);

        return await SendAsync<UploadResponse>(request, cancellationToken).ConfigureAwait(false);
    }

    public Task<UpdateOffer> UpdateOfferAsync(
        string token, string tier, CancellationToken cancellationToken = default)
    {
        var path = string.Create(
            CultureInfo.InvariantCulture, $"update/?tier={Uri.EscapeDataString(tier)}");

        var request = new HttpRequestMessage(HttpMethod.Get, path);

        Authorize(request, token);

        return SendAsync<UpdateOffer>(request, cancellationToken);
    }

    public async Task DownloadUpdateAsync(
        string token,
        string path,
        string destinationPath,
        long maxBytes,
        CancellationToken cancellationToken = default)
    {
        // The one answer in this API that is not JSON, so it does not go
        // through SendAsync: what comes back is tens of megabytes of
        // installer, and it must reach a file without ever being a string.
        var request = new HttpRequestMessage(HttpMethod.Get, Relative(path));

        Authorize(request, token);
        request.Headers.TryAddWithoutValidation(VersionHeader, AgentVersion.Current);

        using var http = _clients.CreateClient(HttpClientName);

        HttpResponseMessage response;

        try
        {
            response = await http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new AdlUnreachableException(
                $"Could not reach ADL at {http.BaseAddress}: {exception.Message}", exception);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AdlUnreachableException(
                $"ADL at {http.BaseAddress} did not answer within {http.Timeout}.", exception);
        }

        using (request)
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw await ReadFailureAsync(response, cancellationToken).ConfigureAwait(false);
            }

            await using var source = await response.Content
                .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

            await using var file = new FileStream(
                destinationPath, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 64 * 1024, useAsync: true);

            var buffer = new byte[64 * 1024];
            long written = 0;

            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

                if (read == 0)
                {
                    break;
                }

                written += read;

                if (written > maxBytes)
                {
                    // Stopped here rather than after the hash, because the
                    // hash cannot be checked until the whole thing has landed
                    // and the whole thing is what would not fit.
                    throw new AdlRequestException(
                        response.StatusCode,
                        "package_too_large",
                        $"The package at {path} is longer than the {maxBytes} bytes ADL said it would be.");
                }

                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// An artifact path from the feed, as something that can only address
    /// this instance's own API.
    /// </summary>
    /// <remarks>
    /// The feed states a relative path and this refuses anything else. What
    /// is being fetched is an executable that will replace a service running
    /// as LocalSystem, so "which host does this come from" is not a question
    /// the body of a response gets to answer -- not even the body of a
    /// response from ADL, which is behind whatever reverse proxy a country's
    /// IT department has put in front of it.
    /// </remarks>
    private static string Relative(string path)
    {
        if (path.Length == 0 ||
            path.StartsWith('/') ||
            path.StartsWith("\\", StringComparison.Ordinal) ||
            Uri.TryCreate(path, UriKind.Absolute, out _))
        {
            throw new AdlRequestException(
                HttpStatusCode.BadRequest,
                "invalid_package_path",
                $"ADL offered a package at '{path}', which is not a path on this instance's agent API.");
        }

        return path;
    }

    /// <summary>
    /// One JSON body, measured before it is sent.
    /// </summary>
    /// <remarks>
    /// Buffered into a string rather than streamed, purely so the request
    /// carries a <c>Content-Length</c>. A streamed body goes out chunked, and
    /// ADL is a Django application behind WSGI, where a chunked request body
    /// is not merely awkward -- it never reaches the view at all. The call
    /// then fails as though the agent had sent nothing, which is exactly what
    /// it did, and nothing in the answer says so.
    /// <para>
    /// The bodies here are a pairing code, a heartbeat and a manifest page.
    /// Buffering them costs nothing. A file upload cannot be buffered at all,
    /// and states its own length instead -- see
    /// <see cref="UploadFileAsync"/>.
    /// </para>
    /// </remarks>
    private static StringContent Body<T>(T value) =>
        new(JsonSerializer.Serialize(value, AgentJson.Options), Encoding.UTF8, "application/json");

    private static void Authorize(HttpRequestMessage request, string token)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private async Task<T> SendAsync<T>(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var pending = request;

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
        var (code, detail, rejected) = await ReadErrorEnvelopeAsync(response, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            _logger.LogWarning(
                "ADL refused this device's token ({Code}). Re-pairing is needed.", code);

            return new DeviceRevokedException(code, detail);
        }

        return new AdlRequestException(response.StatusCode, code, detail, rejected);
    }

    /// <summary>
    /// The plugin's <c>{"code", "detail"}</c> envelope, or a stand-in.
    /// </summary>
    /// <remarks>
    /// A stand-in is needed more often than the envelope suggests: a 502 from
    /// a reverse proxy in front of ADL is an HTML page, and the agent still
    /// has to say something a technician can act on.
    /// </remarks>
    private static async Task<(string Code, string Detail, IReadOnlyList<RejectedEntry> Rejected)>
        ReadErrorEnvelopeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var fallback = ($"http_{(int)response.StatusCode}",
            $"ADL answered {(int)response.StatusCode} {response.ReasonPhrase}.",
            (IReadOnlyList<RejectedEntry>)[]);

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
                envelope.Detail,
                Rejected(envelope.Errors));
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return fallback;
        }
    }

    /// <summary>
    /// The entries of a refused batch, when <c>errors</c> is that.
    /// </summary>
    /// <remarks>
    /// <c>errors</c> does not have one shape across this API, and reading it
    /// as though it did costs more than the field is worth. A refused
    /// manifest names its offending entries as a list; a refused
    /// configuration names its offending fields as an object keyed by field
    /// name. Deserialising the envelope straight into a list therefore fails
    /// on the second, and -- because the whole envelope is read at once --
    /// takes the code and the sentence down with it: a technician who typed
    /// a folder ADL will not store would be shown "ADL answered 400 Bad
    /// Request" instead of what ADL actually said about it.
    /// <para>
    /// So the field is read as whatever it is, and turned into entries only
    /// when it is a list of them. What the other shapes carry is already in
    /// the sentence.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<RejectedEntry> Rejected(JsonNode? errors)
    {
        if (errors is not JsonArray entries)
        {
            return [];
        }

        try
        {
            return entries.Deserialize<IReadOnlyList<RejectedEntry>>(AgentJson.Options) ?? [];
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return [];
        }
    }

    private sealed record ErrorEnvelope
    {
        public string Code { get; init; } = "";
        public string Detail { get; init; } = "";

        /// <summary>
        /// What ADL could not accept, in whatever shape this endpoint says
        /// it. See <see cref="Rejected"/>.
        /// </summary>
        public JsonNode? Errors { get; init; }
    }
}
