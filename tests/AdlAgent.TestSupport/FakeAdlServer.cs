using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AdlAgent.Core.Api;
using AdlAgent.Core.Serialization;

namespace AdlAgent.TestSupport;

/// <summary>
/// An ADL instance, small enough to keep in a test and real enough to be
/// worth testing against.
/// </summary>
/// <remarks>
/// Real HTTP on a loopback port, not a stubbed message handler. What the
/// agent has to get right at this seam is mostly protocol -- the bearer
/// header, the version header, the trailing slashes Django insists on, a 401
/// arriving as a 401 and a refused connection arriving as a refused
/// connection -- and every one of those is exactly what a substituted handler
/// would paper over.
/// <para>
/// It keeps state, because the behaviour under test is stateful: a pairing
/// code is single-use, a revoked token stays revoked, and a configuration
/// version moves. Tests arrange that state and read back what arrived.
/// </para>
/// </remarks>
public sealed class FakeAdlServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly List<RecordedRequest> _requests = [];
    private readonly List<HeartbeatRequest> _heartbeats = [];
    private readonly HashSet<string> _pairingCodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();
    private Task _serving;
    private bool _unreachable;

    public FakeAdlServer()
    {
        var port = FreePort();

        BaseAddress = new Uri($"http://127.0.0.1:{port}/");
        _listener.Prefixes.Add(BaseAddress.ToString());
        _listener.Start();

        _serving = Task.Run(ServeAsync);
    }

    /// <summary>Root of the fake instance, as it would go in the agent's settings.</summary>
    public Uri BaseAddress { get; }

    /// <summary>The token a successful pairing hands out.</summary>
    public string IssuedToken { get; set; } = "device-token-0123456789";

    /// <summary>What ADL says about the device that just paired.</summary>
    public DeviceSummary Device { get; set; } = new()
    {
        Id = 7,
        Name = "Nairobi vendor server",
        PairedAt = DateTimeOffset.Parse("2026-08-21T09:00:00Z"),
    };

    /// <summary>The world this device is told about on sync.</summary>
    public SyncResponse Config { get; set; } = SampleConfig();

    /// <summary>ADL's own clock, which the reported skew is measured against.</summary>
    public DateTimeOffset ServerTime { get; set; } = DateTimeOffset.Parse("2026-08-21T09:00:00Z");

    /// <summary>ADL's current verdict on this device.</summary>
    public string FleetStatus { get; set; } = "online";

    /// <summary>
    /// Set when the administrator has revoked or rotated the token: every
    /// authenticated call answers 401 from here on.
    /// </summary>
    public bool TokenRevoked { get; set; }

    /// <summary>
    /// Set when the link is down.
    /// </summary>
    /// <remarks>
    /// The port stops accepting, so the agent gets a refused connection --
    /// the same thing it gets when a country's uplink drops, and a very
    /// different thing from an ADL that answers badly. Reversible, because
    /// the interesting half of an outage is what happens when it ends.
    /// </remarks>
    public bool Unreachable
    {
        get => _unreachable;

        set
        {
            if (value == _unreachable)
            {
                return;
            }

            _unreachable = value;

            if (value)
            {
                _listener.Stop();
            }
            else
            {
                _listener.Start();
                _serving = Task.Run(ServeAsync);
            }
        }
    }

    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return _requests.ToList();
            }
        }
    }

    /// <summary>Every heartbeat that arrived, in order, already parsed.</summary>
    public IReadOnlyList<HeartbeatRequest> Heartbeats
    {
        get
        {
            lock (_gate)
            {
                return _heartbeats.ToList();
            }
        }
    }

    public IReadOnlyList<RecordedRequest> RequestsFor(string path) =>
        Requests.Where(request => request.Path == path).ToList();

    /// <summary>Issue a pairing code, as an administrator would in the admin.</summary>
    public void AddPairingCode(string code)
    {
        lock (_gate)
        {
            _pairingCodes.Add(code);
        }
    }

    /// <summary>Wait until at least <paramref name="count"/> heartbeats have arrived.</summary>
    public async Task<bool> WaitForHeartbeatsAsync(int count, TimeSpan? timeout = null)
    {
        return await WaitForAsync(() => Heartbeats.Count >= count, timeout).ConfigureAwait(false);
    }

    /// <summary>Wait until at least <paramref name="count"/> calls have arrived for a path.</summary>
    public async Task<bool> WaitForRequestsAsync(string path, int count, TimeSpan? timeout = null)
    {
        return await WaitForAsync(() => RequestsFor(path).Count >= count, timeout).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _unreachable = false;

        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            _serving.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
        }

        _stopping.Dispose();
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan? timeout)
    {
        // Real wall-clock waiting, on purpose: the agent's own clock is faked
        // in these tests, and the thing being waited for is a background task
        // reaching the socket.
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));

        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(10).ConfigureAwait(false);
        }

        return condition();
    }

    private static int FreePort()
    {
        // HttpListener will not bind port 0, so one is borrowed and handed
        // straight back.
        var probe = new TcpListener(IPAddress.Loopback, 0);

        probe.Start();

        var port = ((IPEndPoint)probe.LocalEndpoint).Port;

        probe.Stop();

        return port;
    }

    private async Task ServeAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            HttpListenerContext context;

            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            try
            {
                await HandleAsync(context).ConfigureAwait(false);
            }
            catch (HttpListenerException)
            {
                // The client hung up. Nothing to answer.
            }
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var request = await ReadAsync(context.Request).ConfigureAwait(false);

        lock (_gate)
        {
            _requests.Add(request);
        }

        var (status, body) = Answer(request);

        await WriteAsync(context.Response, status, body).ConfigureAwait(false);
    }

    private (int Status, object Body) Answer(RecordedRequest request) => request.Path switch
    {
        "pair/" => Pair(request),
        "sync/" => Authenticated(request, _ => (200, (object)Config)),
        "heartbeat/" => Authenticated(request, Heartbeat),
        _ => (404, new { code = "not_found", detail = $"No endpoint at {request.Path}." }),
    };

    private (int Status, object Body) Pair(RecordedRequest request)
    {
        if (string.IsNullOrEmpty(request.Body))
        {
            return (400, new
            {
                code = "invalid_pairing_code",
                detail = "That pairing code is not recognised. Ask your ADL administrator to issue a new one.",
            });
        }

        var code = JsonDocument.Parse(request.Body).RootElement
            .TryGetProperty("pairing_code", out var value)
            ? value.GetString()
            : null;

        bool redeemed;

        lock (_gate)
        {
            redeemed = code is not null && _pairingCodes.Remove(code);
        }

        if (!redeemed)
        {
            return (400, new
            {
                code = "invalid_pairing_code",
                detail = "That pairing code is not recognised. Ask your ADL administrator to issue a new one.",
            });
        }

        return (200, new PairResponse { Token = IssuedToken, Device = Device });
    }

    private (int Status, object Body) Authenticated(
        RecordedRequest request, Func<RecordedRequest, (int, object)> handle)
    {
        if (TokenRevoked || request.BearerToken != IssuedToken)
        {
            return (401, new { detail = "Invalid or revoked device token." });
        }

        return handle(request);
    }

    private (int Status, object Body) Heartbeat(RecordedRequest request)
    {
        if (string.IsNullOrEmpty(request.Body))
        {
            return (400, new
            {
                code = "invalid_heartbeat",
                detail = "Send an object describing the machine.",
            });
        }

        var beat = JsonSerializer.Deserialize<HeartbeatRequest>(request.Body, AgentJson.Options)
            ?? new HeartbeatRequest();

        lock (_gate)
        {
            _heartbeats.Add(beat);
        }

        return (200, new HeartbeatResponse
        {
            DeviceId = Device.Id,
            ServerTime = ServerTime,
            ClockSkewSeconds = beat.DeviceTime is null
                ? null
                : (int)(beat.DeviceTime.Value - ServerTime).TotalSeconds,
            Status = FleetStatus,
            HeartbeatIntervalMinutes = Config.Device.HeartbeatIntervalMinutes,
            CheckIntervalMinutes = Config.Device.CheckIntervalMinutes,
            ConfigVersion = Config.ConfigVersion,
        });
    }

    private async Task<RecordedRequest> ReadAsync(HttpListenerRequest request)
    {
        using var reader = new StreamReader(request.InputStream, Encoding.UTF8);

        var headers = request.Headers.AllKeys
            .Where(key => key is not null)
            .ToDictionary(key => key!, key => request.Headers[key] ?? "", StringComparer.OrdinalIgnoreCase);

        var body = await reader.ReadToEndAsync().ConfigureAwait(false);

        // ADL is a Django application behind WSGI, and WSGI has no way to
        // read a request body that arrives without a Content-Length: a
        // chunked body reaches the view as nothing at all. HttpListener is
        // more forgiving than that, and being more forgiving here would mean
        // the tests pass while every POST the agent makes silently arrives
        // empty at a real instance -- which is exactly what happened once.
        if (request.ContentLength64 < 0)
        {
            body = "";
        }

        return new RecordedRequest
        {
            Method = request.HttpMethod,
            Path = (request.Url?.AbsolutePath ?? "").Replace(ApiPath, "", StringComparison.Ordinal).TrimStart('/'),
            Headers = headers,
            Body = body,
        };
    }

    private static async Task WriteAsync(HttpListenerResponse response, int status, object body)
    {
        var json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body, AgentJson.Options));

        response.StatusCode = status;
        response.ContentType = "application/json";
        response.ContentLength64 = json.Length;

        // Every call gets a fresh connection. Keep-alive would leave the
        // agent holding a pooled socket to a server the test has since taken
        // away, and the outage it is trying to simulate would arrive as a
        // truncated answer instead of a refused connection -- a race, and one
        // that only shows up on a loaded machine.
        response.KeepAlive = false;

        await response.OutputStream.WriteAsync(json).ConfigureAwait(false);

        response.Close();
    }

    /// <summary>Where the plugin mounts its versioned surface under ADL.</summary>
    public const string ApiPath = "/plugins/api/agent/v1/";

    /// <summary>
    /// One device, one connection, one station -- the shape of a real sync
    /// response, small enough to read in a failure message.
    /// </summary>
    public static SyncResponse SampleConfig() => new()
    {
        ConfigVersion = 1,
        Limits = new AgentLimits { ManifestEntries = 500, FileBytes = 50 * 1024 * 1024 },
        Device = new DeviceConfig
        {
            Id = 7,
            Name = "Nairobi vendor server",
            CheckIntervalMinutes = 10,
            HeartbeatIntervalMinutes = 5,
        },
        Connections =
        [
            new ConnectionConfig
            {
                Id = 3,
                Name = "Vaisala AWS",
                Admin = new ConnectionAdminConfig { Enabled = true, Network = "Kenya AWS" },
                StationLinks =
                [
                    new StationLinkConfig
                    {
                        Id = 11,
                        Watermark = DateTimeOffset.Parse("2026-08-01T00:00:00Z"),
                        Config = new StationLinkAppConfig
                        {
                            LocalFolderPath = "C:\\VendorData\\Garissa",
                            FilePattern = "GARISSA_*.dat",
                            StabilityWindowSeconds = 60,
                        },
                        Admin = new StationLinkAdminConfig
                        {
                            Enabled = true,
                            Timezone = "Africa/Nairobi",
                            Station = new StationSummary
                            {
                                Id = 42,
                                Name = "Garissa",
                                StationId = "GARISSA",
                            },
                        },
                    },
                ],
            },
        ],
    };
}
