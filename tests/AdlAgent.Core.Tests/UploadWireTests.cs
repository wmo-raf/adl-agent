using System.Globalization;
using System.Text.Json;
using AdlAgent.Core.Api;
using AdlAgent.Core.Serialization;
using AdlAgent.TestSupport;

namespace AdlAgent.Core.Tests;

/// <summary>
/// What the two new calls actually look like on the wire.
/// </summary>
/// <remarks>
/// Asserted against a real socket rather than a substituted handler because
/// what has to be right here is protocol, and protocol is what a substituted
/// handler papers over. One of these assertions is scar tissue: ADL is a
/// Django application behind WSGI, where a request body arriving without a
/// declared length reaches the view as nothing at all -- an upload that looks
/// like it worked and delivered no bytes.
/// </remarks>
public class UploadWireTests
{
    private const string Folder = "C:\\VendorData\\Garissa";

    [Fact]
    public async Task A_manifest_is_a_measured_JSON_body_of_candidate_files()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, Folder, "*.dat"));

        var written = agent.Time.GetUtcNow() - TimeSpan.FromMinutes(5);

        agent.Files.Add(Folder, "GARISSA_20260821.dat", written, "09:00,21.4\n");

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        var call = Assert.Single(agent.Server.RequestsFor("manifest/"));

        Assert.Equal("POST", call.Method);
        Assert.Equal(agent.Server.IssuedToken, call.BearerToken);
        Assert.Equal(AgentVersion.Current, call.Header(AdlApiClient.VersionHeader));
        Assert.True(call.ContentLength > 0, "The manifest went out without a declared length.");

        var body = JsonSerializer.Deserialize<ManifestRequest>(call.Body, AgentJson.Options)!;
        var entry = Assert.Single(body.Files);

        Assert.Equal(11, entry.StationLinkId);
        Assert.Equal("GARISSA_20260821.dat", entry.Name);
        Assert.Equal(11, entry.Size);
        Assert.Equal(written, entry.Mtime);
        Assert.Equal(Sha256Of("09:00,21.4\n"), entry.Hash);
    }

    [Fact]
    public async Task An_upload_carries_the_file_and_the_entry_that_promised_it()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, Folder, "*.dat"));

        var written = agent.Time.GetUtcNow() - TimeSpan.FromMinutes(5);

        agent.Files.Add(Folder, "GARISSA_20260821.dat", written, "09:00,21.4\n");

        await agent.PairAsync();
        await agent.Cycle.RunAsync();

        var call = Assert.Single(agent.Server.RequestsFor("files/"));

        Assert.Equal(agent.Server.IssuedToken, call.BearerToken);
        Assert.Equal(AgentVersion.Current, call.Header(AdlApiClient.VersionHeader));
        Assert.True(call.ContentLength > 0, "The upload went out without a declared length.");

        var form = call.Form!;

        Assert.Equal("11", form.Field("station_link_id"));
        Assert.Equal("GARISSA_20260821.dat", form.Field("name"));
        Assert.Equal("11", form.Field("size"));
        Assert.Equal(Sha256Of("09:00,21.4\n"), form.Field("hash"));
        Assert.Equal("09:00,21.4\n"u8.ToArray(), form.File);

        // Django reads this with parse_datetime, which wants ISO 8601 with an
        // offset. A naive timestamp would be read as UTC and quietly file the
        // observation under the wrong hour.
        Assert.Equal(
            written,
            DateTimeOffset.Parse(form.Field("mtime")!, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind));
    }

    [Fact]
    public async Task The_loop_runs_a_cycle_on_the_check_interval_ADL_set()
    {
        await using var agent = new AgentHarness();

        agent.Server.Config = SyncConfigs.With(SyncConfigs.Link(11, Folder, "*.dat"));
        agent.Files.Add(Folder, "one.dat", agent.Time.GetUtcNow() - TimeSpan.FromMinutes(5), "1\n");

        await agent.StartAsync();
        await agent.PairAsync();

        // Pairing wakes both loops, so the technician watching the fleet view
        // sees data move rather than waiting out ten minutes.
        Assert.True(await agent.Server.WaitForRequestsAsync("manifest/", 1));

        agent.Files.Add(Folder, "two.dat", agent.Time.GetUtcNow() - TimeSpan.FromMinutes(5), "2\n");

        await agent.AdvanceAsync(TimeSpan.FromMinutes(10));

        Assert.True(await agent.Server.WaitForRequestsAsync("manifest/", 2));
        Assert.True(await agent.Server.WaitForRequestsAsync("files/", 2));
    }

    private static string Sha256Of(string contents) =>
        Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(contents)));
}
