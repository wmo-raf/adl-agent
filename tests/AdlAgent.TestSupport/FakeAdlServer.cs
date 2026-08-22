using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    private readonly Dictionary<(long StationLink, string Name), StagedFile> _ledger = [];
    private readonly List<IReadOnlyList<ManifestEntry>> _manifestPages = [];
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

    /// <summary>
    /// Every manifest page that arrived, in order, already parsed.
    /// </summary>
    /// <remarks>
    /// Pages rather than entries, because how the agent divided its
    /// candidates is itself behaviour under test: a cycle that offered nine
    /// hundred files in one call would be refused by a real instance.
    /// </remarks>
    public IReadOnlyList<IReadOnlyList<ManifestEntry>> ManifestPages
    {
        get
        {
            lock (_gate)
            {
                return _manifestPages.ToList();
            }
        }
    }

    /// <summary>The file ledger: what this instance holds, per station link.</summary>
    public IReadOnlyDictionary<(long StationLink, string Name), StagedFile> Ledger
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<(long, string), StagedFile>(_ledger);
            }
        }
    }

    /// <summary>
    /// Names this instance refuses to accept, however good the bytes are.
    /// </summary>
    /// <remarks>
    /// A refusal an agent has to survive rather than a bug to reproduce: ADL
    /// turns away a file whose hash no longer matches -- a vendor process
    /// appended to it mid-upload -- and the contract is that the next cycle
    /// simply offers it again. Removing a name here is that next cycle
    /// succeeding.
    /// </remarks>
    public HashSet<string> RefusedUploads { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Station links this instance no longer recognises as the device's, and
    /// station links an administrator has since switched off.
    /// </summary>
    /// <remarks>
    /// Set independently of <see cref="Config"/> because the situation they
    /// describe is precisely the one where the two disagree: the machine is
    /// offering files from a configuration it cached before HQ moved the
    /// station somewhere else.
    /// </remarks>
    public HashSet<long> StationLinksUnknownToAdl { get; } = [];

    public HashSet<long> StationLinksDisabledInAdl { get; } = [];

    /// <summary>
    /// Filenames this instance will not read as a manifest entry.
    /// </summary>
    /// <remarks>
    /// The reason is left unsaid on purpose: what matters is the shape of the
    /// refusal, not which rule was broken. ADL refuses such a manifest whole
    /// -- "an agent that had half its manifest accepted would believe the
    /// other half was already held" -- and names the offending entries by
    /// position, which is the only thing that lets an agent get the rest of
    /// the page through.
    /// </remarks>
    public HashSet<string> UnreadableNames { get; } = new(StringComparer.Ordinal);

    /// <summary>Entries this instance takes in one manifest, whatever it advertises.</summary>
    /// <remarks>
    /// Set below <see cref="AgentLimits.ManifestEntries"/> to be an instance
    /// whose stated limit is a lie -- the case where an agent that believed
    /// the number would be refused on every page of every cycle for ever.
    /// </remarks>
    public int? ManifestEntriesActuallyAccepted { get; set; }

    /// <summary>What ADL holds for one station link and name, if anything.</summary>
    public StagedFile? Held(long stationLinkId, string name) =>
        Ledger.TryGetValue((stationLinkId, name), out var staged) ? staged : null;

    /// <summary>Put a file in the ledger, as an earlier cycle would have left it.</summary>
    public void Stage(long stationLinkId, string name, byte[] bytes)
    {
        lock (_gate)
        {
            _ledger[(stationLinkId, name)] = new StagedFile
            {
                StationLinkId = stationLinkId,
                Name = name,
                Bytes = bytes,
                Hash = Sha256(bytes),
            };
        }
    }

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
        "manifest/" => Authenticated(request, Manifest),
        "files/" => Authenticated(request, Upload),
        _ when request.Path.StartsWith("station-links/", StringComparison.Ordinal) =>
            Authenticated(request, StationLinkConfig),
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

    private (int Status, object Body) Manifest(RecordedRequest request)
    {
        ManifestRequest? offered;

        try
        {
            offered = JsonSerializer.Deserialize<ManifestRequest>(request.Body, AgentJson.Options);
        }
        catch (JsonException)
        {
            offered = null;
        }

        if (offered is null)
        {
            return (400, new { code = "invalid_body", detail = "Send an object with a \"files\" list." });
        }

        var unreadable = offered.Files
            .Select((entry, index) => (entry, index))
            .Where(offer => UnreadableNames.Contains(offer.entry.Name))
            .Select(offer => new
            {
                index = offer.index,
                detail = $"{offer.entry.Name} could not be read as a manifest entry.",
            })
            .ToList();

        if (unreadable.Count > 0)
        {
            // All or nothing, and by position -- exactly as the plugin's
            // parse_entries does it.
            return (400, new
            {
                code = "invalid_entry",
                detail = $"{unreadable.Count} of the files offered could not be read.",
                errors = unreadable,
            });
        }

        if (offered.Files.Count > (ManifestEntriesActuallyAccepted ?? Config.Limits.ManifestEntries))
        {
            // Refused, never truncated: an agent told about the first five
            // hundred of its files would take the silence about the rest for
            // "already held" and never offer them again.
            return (400, new
            {
                code = "manifest_too_large",
                detail = "Offer at most "
                    + (ManifestEntriesActuallyAccepted ?? Config.Limits.ManifestEntries)
                    + " files per manifest, in pages.",
                limit = ManifestEntriesActuallyAccepted ?? Config.Limits.ManifestEntries,
            });
        }

        lock (_gate)
        {
            _manifestPages.Add(offered.Files.ToList());
        }

        var links = Config.Connections
            .SelectMany(connection => connection.StationLinks)
            .ToDictionary(link => link.Id);

        var requested = new List<RequestedFile>();
        var unknown = new List<long>();
        var disabled = new List<long>();

        foreach (var entry in offered.Files)
        {
            if (StationLinksUnknownToAdl.Contains(entry.StationLinkId) ||
                !links.TryGetValue(entry.StationLinkId, out var link))
            {
                Remember(unknown, entry.StationLinkId);
            }
            else if (!link.Admin.Enabled || StationLinksDisabledInAdl.Contains(entry.StationLinkId))
            {
                Remember(disabled, entry.StationLinkId);
            }
            else if (Held(entry.StationLinkId, entry.Name)?.Hash != entry.Hash)
            {
                requested.Add(new RequestedFile
                {
                    StationLinkId = entry.StationLinkId,
                    Name = entry.Name,
                    Hash = entry.Hash,
                });
            }
        }

        return (200, new ManifestResponse
        {
            ConfigVersion = Config.ConfigVersion,
            Limits = Config.Limits,
            Requested = requested,
            UnknownStationLinks = unknown,
            DisabledStationLinks = disabled,
        });
    }

    private (int Status, object Body) Upload(RecordedRequest request)
    {
        var form = request.Form;

        if (form?.File is null)
        {
            return (400, new { code = "file_missing", detail = "Attach the file itself as \"file\"." });
        }

        var name = form.Field("name") ?? "";
        var declaredHash = form.Field("hash") ?? "";
        var declaredSize = long.TryParse(form.Field("size"), out var size) ? size : -1;
        var stationLinkId = long.TryParse(form.Field("station_link_id"), out var id) ? id : 0;

        if (form.Field("mtime") is null or "")
        {
            return (400, new { code = "invalid_entry", detail = "Missing: mtime." });
        }

        if (form.File.LongLength > Config.Limits.FileBytes)
        {
            return (413, new
            {
                code = "file_too_large",
                detail = $"Files must be at most {Config.Limits.FileBytes} bytes.",
                limit = Config.Limits.FileBytes,
            });
        }

        if (declaredSize != form.File.LongLength)
        {
            return (400, new
            {
                code = "size_mismatch",
                detail = $"The file is {form.File.LongLength} bytes, not the {declaredSize} it was offered as.",
            });
        }

        // The check that makes this a seam worth testing against: ADL keeps
        // nothing it cannot describe truthfully, so the bytes are hashed here
        // rather than taken on the agent's word.
        var actualHash = Sha256(form.File);

        if (actualHash != declaredHash || RefusedUploads.Contains(name))
        {
            return (400, new
            {
                code = "hash_mismatch",
                detail = "The file does not hash to what it was offered as. Offer it again next cycle.",
            });
        }

        StagedFile staged;

        lock (_gate)
        {
            staged = new StagedFile
            {
                StationLinkId = stationLinkId,
                Name = name,
                Bytes = form.File,
                Hash = actualHash,
            };

            // In place: a file that grew keeps one ledger row, which is what
            // makes the next manifest say "unchanged" rather than offering
            // both versions forever.
            _ledger[(stationLinkId, name)] = staged;
        }

        return (201, new UploadResponse
        {
            StationLinkId = stationLinkId,
            Name = name,
            Size = staged.Bytes.LongLength,
            Hash = actualHash,
            Status = "received",
            ReceivedAt = ServerTime,
            ConfigVersion = Config.ConfigVersion,
        });
    }

    /// <summary>
    /// The tier a machine may write, and nothing else.
    /// </summary>
    /// <remarks>
    /// Mirrors the plugin's <c>AgentStationLink.APP_EDITABLE_FIELDS</c>
    /// exactly, because refusing the admin tier is the behaviour under test:
    /// an agent that could write a decoder choice or a collection start date
    /// would put the meaning of a country's data on the machine that happens
    /// to hold the files, which is the one thing decision #260 rules out.
    /// It is also, by design, the wire form of
    /// <see cref="StationLinkAppConfig"/> -- if the two ever disagree, one
    /// of them is wrong.
    /// </remarks>
    public static IReadOnlySet<string> AppEditableFields { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "local_folder_path",
        "file_pattern",
        "dir_structured_by_date",
        "date_granularity",
        "month_dir_format",
        "listing_strategy",
        "direct_fetch_prefix",
        "direct_fetch_interval_minutes",
        "direct_fetch_datetime_format",
        "direct_fetch_datetime_timezone",
        "direct_fetch_file_extension",
        "stability_window_seconds",
    };

    /// <summary>
    /// <c>PATCH station-links/{id}/config/</c> -- the app's tier, written
    /// from the machine.
    /// </summary>
    /// <remarks>
    /// Last-write-wins and never 409, exactly as decision #266 has it: the
    /// answer carries the configuration that now stands and the version it
    /// stands at, and the version moves so that an agent can see its own
    /// write landed.
    /// </remarks>
    private (int Status, object Body) StationLinkConfig(RecordedRequest request)
    {
        var parts = request.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length != 3 || parts[2] != "config" || !long.TryParse(parts[1], out var stationLinkId))
        {
            return (404, new { code = "not_found", detail = $"No endpoint at {request.Path}." });
        }

        if (request.Method != "PATCH")
        {
            return (405, new
            {
                code = "method_not_allowed",
                detail = "Station link configuration is written with PATCH.",
            });
        }

        JsonObject? changes;

        try
        {
            changes = JsonNode.Parse(string.IsNullOrEmpty(request.Body) ? "null" : request.Body) as JsonObject;
        }
        catch (JsonException)
        {
            changes = null;
        }

        if (changes is null)
        {
            return (400, new
            {
                code = "invalid_body",
                detail = "Send an object of station link settings to change.",
            });
        }

        var refused = changes.Select(change => change.Key)
            .Where(name => !AppEditableFields.Contains(name))
            .ToList();

        if (refused.Count > 0)
        {
            return (400, new
            {
                code = "read_only_fields",
                detail = string.Join(", ", refused)
                    + " is managed in the ADL admin and cannot be set from the app",
                fields = refused,
            });
        }

        lock (_gate)
        {
            var link = Config.Connections
                .SelectMany(connection => connection.StationLinks)
                .FirstOrDefault(candidate => candidate.Id == stationLinkId);

            if (link is null || StationLinksUnknownToAdl.Contains(stationLinkId))
            {
                return (404, new
                {
                    code = "unknown_station_link",
                    detail = "This device has no station link with that id.",
                });
            }

            var written = Merge(link.Config, changes);

            Config = Config with
            {
                ConfigVersion = Config.ConfigVersion + 1,
                Connections = Config.Connections
                    .Select(connection => connection with
                    {
                        StationLinks = connection.StationLinks
                            .Select(candidate => candidate.Id == stationLinkId
                                ? candidate with { Config = written }
                                : candidate)
                            .ToList(),
                    })
                    .ToList(),
            };

            return (200, new ConfigWriteResponse
            {
                StationLinkId = stationLinkId,
                ConfigVersion = Config.ConfigVersion,
                Config = written,
            });
        }
    }

    /// <summary>The stored settings with the named ones changed.</summary>
    private static StationLinkAppConfig Merge(StationLinkAppConfig stored, JsonObject changes)
    {
        var merged = JsonSerializer.SerializeToNode(stored, AgentJson.Options)!.AsObject();

        foreach (var change in changes)
        {
            merged[change.Key] = change.Value?.DeepClone();
        }

        return JsonSerializer.Deserialize<StationLinkAppConfig>(merged, AgentJson.Options)!;
    }

    private static void Remember(List<long> ids, long stationLinkId)
    {
        if (!ids.Contains(stationLinkId))
        {
            ids.Add(stationLinkId);
        }
    }

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));

    private static async Task<RecordedRequest> ReadAsync(HttpListenerRequest request)
    {
        var headers = request.Headers.AllKeys
            .Where(key => key is not null)
            .ToDictionary(key => key!, key => request.Headers[key] ?? "", StringComparer.OrdinalIgnoreCase);

        using var buffer = new MemoryStream();

        await request.InputStream.CopyToAsync(buffer).ConfigureAwait(false);

        var raw = buffer.ToArray();

        // ADL is a Django application behind WSGI, and WSGI has no way to
        // read a request body that arrives without a Content-Length: a
        // chunked body reaches the view as nothing at all. HttpListener is
        // more forgiving than that, and being more forgiving here would mean
        // the tests pass while every POST the agent makes silently arrives
        // empty at a real instance -- which is exactly what happened once.
        if (request.ContentLength64 < 0)
        {
            raw = [];
        }

        var form = MultipartForm.Parse(request.ContentType, raw);

        return new RecordedRequest
        {
            Method = request.HttpMethod,
            Path = (request.Url?.AbsolutePath ?? "").Replace(ApiPath, "", StringComparison.Ordinal).TrimStart('/'),
            Headers = headers,
            Body = form is null ? Encoding.UTF8.GetString(raw) : "",
            Form = form,
            ContentLength = request.ContentLength64,
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

/// <summary>One file ADL holds, as the ledger and the staging store see it.</summary>
public sealed record StagedFile
{
    public required long StationLinkId { get; init; }
    public required string Name { get; init; }
    public required byte[] Bytes { get; init; }
    public required string Hash { get; init; }

    public string Text => System.Text.Encoding.UTF8.GetString(Bytes);
}
