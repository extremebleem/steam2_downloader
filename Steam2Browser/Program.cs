using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.FileProviders;
using Steam2Browser;

var baseDir = AppContext.BaseDirectory;
var settings = Settings.Load(baseDir);

int port = 5099;
foreach (var arg in args)
    if (arg.StartsWith("--port=", StringComparison.OrdinalIgnoreCase) && int.TryParse(arg[7..], out int p))
        port = p;
bool noBrowser = args.Contains("--no-browser", StringComparer.OrdinalIgnoreCase);

var handler = new SocketsHttpHandler
{
    MaxConnectionsPerServer = 64,
    PooledConnectionLifetime = TimeSpan.FromMinutes(10),
    AutomaticDecompression = System.Net.DecompressionMethods.All,
    ConnectTimeout = TimeSpan.FromSeconds(20),
};
var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
http.DefaultRequestHeaders.UserAgent.ParseAdd(
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
    "AppleWebKit/537.36 (KHTML, like Gecko) " +
    "Chrome/151.0.0.0 Safari/537.36");

var client = new ArchiveClient(http)
{
    Primary = Mirrors.ById(settings.MirrorId),
    Failover = settings.Failover,
    UseSegments = !settings.PhasedDownloads,
};
var loader = new IndexLoader(client, settings);
var torrent = new TorrentSource(settings);
// The swarm is the archive now, so the client fetches through it.
client.Torrent = torrent;
// Built before the download manager, which uses it to work out which dats a chain really needs.
var changes = new ChangeIndex(client, settings);
var downloads = new DownloadManager(client, settings, torrent, changes);
var extractor = new ExtractorRunner(settings);
var installs = new InstallManager(settings, client, changes, downloads, extractor);
var updates = new UpdateChecker(http);
updates.Initialise();
var labels = new LabelSource(http);
var apps = new AppCatalog(http);
var fileSearch = new FileSearch(settings);
var names = new NameCache(client, http, labels);
names.Load(Settings.RootFor(baseDir));

// Maintainer and CI tool: `--apps <folder>` reads the app definitions, reports anything wrong with
// them and exits non-zero if there is. With `--out <path>` it also writes the combined file the
// running app fetches. The archive catalog is loaded first so a definition pointing at a depot or
// version that does not exist is caught before it can be merged.
int appsAt = Array.FindIndex(args, a => a.Equals("--apps", StringComparison.OrdinalIgnoreCase));
if (appsAt >= 0)
{
    string folder = Path.GetFullPath(appsAt + 1 < args.Length ? args[appsAt + 1] : "apps");

    int outAt = Array.FindIndex(args, a => a.Equals("--out", StringComparison.OrdinalIgnoreCase));
    string? combinedPath = outAt >= 0 && outAt + 1 < args.Length
        ? Path.GetFullPath(args[outAt + 1])
        : null;

    Console.WriteLine($"checking app definitions in {folder}");

    // Without a catalog the structure is still checked; only the existence tests are skipped.
    await loader.LoadAsync(refreshIndex: false, withSizes: false);
    var catalogForApps = loader.Catalog;
    Console.WriteLine(catalogForApps is null
        ? "  no catalog available — depot and version existence will not be checked"
        : $"  catalog: {catalogForApps.Ordered.Count} depots");

    var problems = AppCatalog.Validate(folder, catalogForApps, out var defined);

    foreach (var line in problems) Console.Error.WriteLine($"  {line}");

    if (problems.Count > 0)
    {
        Console.Error.WriteLine($"  {problems.Count} problem(s) found");
        return 1;
    }

    int builds = defined.Sum(a => a.Builds.Count);
    int pins = defined.Sum(a => a.Builds.Sum(b => b.Depots.Count));
    Console.WriteLine($"  ok: {defined.Count} app(s), {builds} build(s), {pins} depot pin(s)");

    if (combinedPath is not null)
    {
        AppCatalog.WriteCombined(combinedPath, defined);
        Console.WriteLine($"  wrote {combinedPath} ({new FileInfo(combinedPath).Length / 1024.0:0.0} KB)");
    }

    return 0;
}

// Maintainer tool: `--build-index <path>` snapshots the whole catalog into one compact file, which
// the build then embeds so a release needs no network on first run. Always pulls fresh from a
// mirror rather than reusing local caches, since the point is to capture the archive as it is now.
int buildIndexAt = Array.FindIndex(args, a => a.Equals("--build-index", StringComparison.OrdinalIgnoreCase));
if (buildIndexAt >= 0)
{
    string outPath = Path.GetFullPath(
        buildIndexAt + 1 < args.Length ? args[buildIndexAt + 1] : "index.bin");

    // Sizes are the expensive part — two ~20 MB directory listings — so they are opt-in. Without
    // them the snapshot still carries every name and date, and sizes fill in later on demand.
    bool withSizes = args.Contains("--with-sizes", StringComparer.OrdinalIgnoreCase);

    Console.WriteLine("building a compact index snapshot");
    Console.WriteLine(withSizes
        ? "  sizes requested: fetching two directory listings (~40 MB)"
        : "  sizes skipped: pass --with-sizes to include them");

    // Local dats_dates.txt / blobs_dates.txt are used when present, anywhere up the tree.
    await loader.LoadAsync(refreshIndex: false, withSizes: false, ignoreEmbedded: true);
    if (loader.Catalog is null)
    {
        Console.Error.WriteLine($"  failed: {loader.Status.Error ?? loader.Status.Message}");
        return 1;
    }
    Console.WriteLine($"  index: {loader.Status.Message}");

    if (withSizes)
    {
        await loader.LoadSizesAsync(force: true);
        Console.WriteLine($"  sizes: {loader.Status.Message}");
    }

    var cat = loader.Catalog;
    Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
    CompactIndex.Write(
        outPath,
        cat.Ordered.SelectMany(d => d.Dats),
        cat.Ordered.SelectMany(d => d.Blobs));

    Console.WriteLine($"  wrote {outPath} ({new FileInfo(outPath).Length / 1_000_000.0:0.00} MB) " +
                      $"— {cat.Ordered.Count} depots, {cat.DatCount + cat.BlobCount} files" +
                      (cat.SizesLoaded ? ", with sizes" : ", no sizes"));
    return 0;
}

// ---------------- address ----------------

// The name the app calls itself in a browser, in place of a bare loopback address.
//
// Anything under .localhost is reserved by RFC 6761 and resolved to loopback by the browser itself,
// so this needs no DNS, no entry in the hosts file, and no administrator: nothing on the machine is
// changed to make it work, and nothing is left behind. The alternatives all cost more than a nicer
// address is worth — a hosts entry needs elevation and outlives the app, and mDNS would mean
// listening on the network rather than on loopback, which is not a trade to make for an app that
// downloads and writes files.
//
// A port still has to appear unless the app is on 80, which cannot be relied on: it is often taken,
// and an ordinary user on Linux is not allowed to bind it at all. Passing --port=80 drops it and
// leaves the bare name, wherever that does work.
const string LocalHostName = "steam2downloader.localhost";

static string AddressFor(int p) => p == 80
    ? $"http://{LocalHostName}/"
    : $"http://{LocalHostName}:{p}/";

// Kestrel only discovers a busy port deep inside app.Run(), where the failure surfaces as a wall
// of stack trace. Settle it here instead: hand the user over to an instance that is already
// running, or step aside to the next free port.
static bool PortIsFree(int candidate)
{
    try
    {
        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, candidate);
        probe.Start();
        probe.Stop();
        return true;
    }
    catch (System.Net.Sockets.SocketException)
    {
        return false;
    }
}

static async Task<bool> AnotherInstanceAsync(int candidate)
{
    try
    {
        using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var body = await probe.GetStringAsync($"http://127.0.0.1:{candidate}/api/state");
        return body.Contains("\"mirrors\"", StringComparison.Ordinal);
    }
    catch
    {
        return false;
    }
}

if (!PortIsFree(port))
{
    if (await AnotherInstanceAsync(port))
    {
        string running = AddressFor(port);
        Console.WriteLine($"steam2browser is already running at {running} — opening that one");

        if (!noBrowser)
        {
            try { OpenWithDefaultApplication(running); }
            catch { /* the URL is printed above */ }
        }
        return 0;
    }

    int free = Enumerable.Range(port + 1, 20).FirstOrDefault(PortIsFree, -1);
    if (free < 0)
    {
        Console.Error.WriteLine($"port {port} is taken and nothing is free through {port + 20}.");
        Console.Error.WriteLine("Pass --port=NNNN to choose another one.");
        return 1;
    }

    Console.WriteLine($"port {port} is taken by something else — using {free}");
    port = free;
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = baseDir });
builder.Logging.ClearProviders();
// "localhost" rather than 127.0.0.1, which binds both loopback addresses instead of only the IPv4
// one. A browser resolving steam2.localhost is free to pick ::1, and on an IPv4-only listener that
// arrives as a refused connection — the name would work on one machine and not the next for a
// reason nobody could see. Still loopback either way: nothing is reachable from the network.
builder.WebHost.UseUrls($"http://localhost:{port}");
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

var app = builder.Build();

// UI assets are embedded so the published exe stands alone; a physical wwwroot wins during development.
IFileProvider assets = new ManifestEmbeddedFileProvider(typeof(Program).Assembly, "wwwroot");
string devAssets = Path.Combine(baseDir, "wwwroot");
if (Directory.Exists(devAssets))
    assets = new CompositeFileProvider(new PhysicalFileProvider(devAssets), assets);

app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = assets });
app.UseStaticFiles(new StaticFileOptions { FileProvider = assets });

// ---------------- state ----------------

app.MapGet("/api/state", () => new
{
    status = new
    {
        loader.Status.Phase,
        loader.Status.Message,
        loader.Status.Percent,
        loader.Status.Ready,
        loader.Status.Error,
    },
    catalog = loader.Catalog is { } c
        ? new
        {
            depots = c.Ordered.Count,
            dats = c.DatCount,
            blobs = c.BlobCount,
            totalBytes = c.ApproxTotalBytes,
            sizesLoaded = c.SizesLoaded,
            resetDepots = c.Ordered.Count(d => d.HasReset),
            keyedDepots = c.Ordered.Count(d => DepotKeys.Has(d.Id)),
            incompleteDepots = c.Ordered.Count(d => !d.IsComplete),
        }
        : null,
    settings = new
    {
        settings.DataDir,
        settings.IndexDir,
        settings.MirrorId,
        settings.Failover,
        settings.Concurrency,
        settings.PhasedDownloads,
        settings.BlobConcurrency,
        settings.DatConcurrency,
        settings.WarmupLookahead,
        settings.BigFileBytes,
        settings.TorrentEnabled,
        settings.SeedDownloaded,
        settings.SwarmAssist,
        settings.SharingNoticeSeen,
        settings.VerifyHashes,
        settings.TorrentPort,
        settings.TorrentUploadKbps,
        settings.TorrentDownloadKbps,
        settings.ExtractOutDir,
        trackers = settings.TrackersToUse,
    },
    // Free space where downloads land. Nothing is asked of it here, so needed is zero — this is
    // the standing figure the settings dialog shows, not a verdict on any one download.
    disk = Dto.Space(settings.DataDir, 0),
    mirrors = Mirrors.All.Select(m => new
    {
        m.Id, m.Name, m.Region, m.BaseUrl, m.SpeedBps, m.TtfbMs, m.Reachable, m.Error,
        m.IsTorrent,
        tested = m.TestedUtc,
        active = m.Id == client.Primary.Id,
    }),
    update = new
    {
        updates.Status.State,
        updates.Status.Message,
        updates.Status.Repo,
        updates.Status.RepoUrl,
        updates.Status.BuiltUtc,
        updates.Status.LatestCommitUtc,
        updates.Status.CommitShort,
        updates.Status.CommitMessage,
        updates.Status.CommitAuthor,
        updates.Status.CommitUrl,
        updates.Status.CheckedUtc,
    },
    labels = new
    {
        labels.Status.State,
        labels.Status.Message,
        labels.Status.Error,
        labels.Status.Count,
        labels.Status.Source,
        labels.Status.FetchedUtc,
    },
    names = new
    {
        names.Status.Running,
        names.Status.Curated,
        names.Status.Cached,
        names.Status.Named,
        names.Status.Failed,
        names.Status.Current,
        names.Status.Remaining,
        names.Status.Message,
    },
    fileSearch = new
    {
        fileSearch.Status.Running,
        fileSearch.Status.DepotsIndexed,
        fileSearch.Status.DepotsToIndex,
        fileSearch.Status.PathCount,
        fileSearch.Status.Capped,
        fileSearch.Status.BuiltUtc,
        fileSearch.Status.Message,
    },
    torrent = new
    {
        torrent.Status.State,
        torrent.Status.Message,
        torrent.Status.Error,
        torrent.Status.HasMetadata,
        torrent.Status.TotalFiles,
        torrent.Status.SelectedFiles,
        torrent.Status.SelectedBytes,
        torrent.Status.SelectedProgress,
        torrent.Status.Trackers,
        torrent.Status.Peers,
        torrent.Status.Seeds,
        torrent.Status.DownloadRate,
        torrent.Status.UploadRate,
        torrent.Status.TorrentState,
        magnet = TorrentSource.Magnet,
    },
    steam = new
    {
        names.Steam.Running,
        names.Steam.Checked,
        names.Steam.Found,
        names.Steam.Remaining,
        names.Steam.Current,
        names.Steam.Message,
    },
});

app.MapPost("/api/settings", async (SettingsPatch patch) =>
{
    var resetTorrent = false;
    if (patch.MirrorId is { } mid) { settings.MirrorId = mid; client.Primary = Mirrors.ById(mid); }
    if (patch.Failover is { } fo) { settings.Failover = fo; client.Failover = fo; }
    if (patch.Concurrency is { } cc) settings.Concurrency = Math.Clamp(cc, 1, 64);
    if (patch.PhasedDownloads is { } phased)
    {
        settings.PhasedDownloads = phased;
        client.UseSegments = !phased;
    }
    if (patch.BlobConcurrency is { } bc) settings.BlobConcurrency = Math.Clamp(bc, 1, 128);
    if (patch.DatConcurrency is { } dc) settings.DatConcurrency = Math.Clamp(dc, 1, 64);
    if (patch.WarmupLookahead is { } wl) settings.WarmupLookahead = Math.Clamp(wl, 0, 16);
    if (patch.SwarmAssist is { } sa) settings.SwarmAssist = sa;
    if (patch.SharingNoticeSeen is { } sn)
    {
        // Answering the notice is what joins the swarm on a first run, because startup deliberately
        // does not: see where seeding is started at the bottom of this file. Only ever forward, so
        // a client that sends this again cannot restart sharing somebody has since switched off.
        bool firstAnswer = sn && !settings.SharingNoticeSeen;
        settings.SharingNoticeSeen = sn;

        if (firstAnswer && settings.TorrentEnabled && settings.SeedDownloaded)
            _ = Task.Run(() => torrent.StartSeedingAsync());
    }

    if (patch.TorrentEnabled is { } te)
    {
        settings.TorrentEnabled = te;

        if (!te)
        {
            // Off means off: sharing stops, the manager stops, and the engine is disposed. Merely
            // refusing to start again would leave a half-started engine running for the session.
            await torrent.StopSeedingAsync();
            await torrent.ResetAsync();
        }
        else if (settings.SeedDownloaded)
        {
            _ = Task.Run(() => torrent.StartSeedingAsync());
        }
    }

    if (patch.BigFileMb is { } bm) settings.BigFileBytes = Math.Max(0, bm) * 1_000_000L;
    if (patch.VerifyHashes is { } vh) settings.VerifyHashes = vh;
    if (patch.TorrentPort is { } tp)
    {
        int port = tp is < 0 or > 65535 ? 0 : tp;
        resetTorrent = port != settings.TorrentPort;
        settings.TorrentPort = port;
    }

    // Unlike the port, a speed cap does not need the engine rebuilt — it is pushed into the running
    // one, so a limit set while an upload is in the way takes effect on that upload.
    var rateChanged = false;
    if (patch.TorrentUploadKbps is { } uk)
    {
        int v = Math.Max(0, uk);
        rateChanged |= v != settings.TorrentUploadKbps;
        settings.TorrentUploadKbps = v;
    }
    if (patch.TorrentDownloadKbps is { } dk)
    {
        int v = Math.Max(0, dk);
        rateChanged |= v != settings.TorrentDownloadKbps;
        settings.TorrentDownloadKbps = v;
    }
    if (!string.IsNullOrWhiteSpace(patch.DataDir)) settings.DataDir = patch.DataDir!;
    if (!string.IsNullOrWhiteSpace(patch.ExtractOutDir)) settings.ExtractOutDir = patch.ExtractOutDir!;
    if (patch.ExtraTrackers is { } tr) settings.ExtraTrackers = tr;
    settings.Save();
    if (rateChanged && !resetTorrent) await torrent.ApplyRateLimitsAsync();
    if (resetTorrent) await torrent.ResetAsync();
    return Results.Ok(new { ok = true });
});

app.MapPost("/api/mirrors/test", async (CancellationToken ct) =>
{
    await Mirrors.TestAllAsync(http, ct);
    return Results.Ok(Mirrors.All.Select(m => new { m.Id, m.SpeedBps, m.TtfbMs, m.Reachable, m.Error }));
});

// Both of these fetched from the mirrors, and both are gone with them. There is nothing to refresh
// against either: the archive stopped changing when the site closed, so the snapshot shipped with
// the app is not a stale copy of the catalog, it is the catalog. Reloading from disk is still
// allowed, since that is local and costs nothing.
app.MapPost("/api/index/reload", (ReloadRequest req) =>
{
    if (req.Refresh)
        return Results.BadRequest(new { error = "the mirrors are gone — the built-in catalog is the only one there is" });

    _ = loader.LoadAsync(refreshIndex: false, req.Sizes);
    return Results.Ok(new { ok = true });
});

app.MapPost("/api/index/sizes", () =>
    Results.BadRequest(new { error = "sizes came from the mirrors' directory listings and are now built in" }));

app.MapPost("/api/names/start", (bool? retryFailed) =>
{
    if (loader.Catalog is null) return Results.BadRequest(new { error = "index not loaded yet" });
    names.Start(loader.Catalog, retryFailed: retryFailed ?? false);
    return Results.Ok(new { ok = true });
});

app.MapPost("/api/names/stop", () => { names.Stop(); return Results.Ok(new { ok = true }); });

app.MapPost("/api/names/labels/refresh", async (CancellationToken ct) =>
{
    await labels.RefreshAsync(Settings.RootFor(AppContext.BaseDirectory), ct);
    return Results.Ok(new { labels.Status.State, labels.Status.Message, labels.Status.Count });
});

app.MapPost("/api/names/steam/start", (bool? recheckMisses) =>
{
    if (loader.Catalog is null) return Results.BadRequest(new { error = "index not loaded yet" });
    names.StartSteam(loader.Catalog, recheckMisses ?? false);
    return Results.Ok(new { ok = true });
});

app.MapPost("/api/names/steam/stop", () => { names.StopSteam(); return Results.Ok(new { ok = true }); });

app.MapPost("/api/torrent/start", () =>
{
    _ = torrent.EnsureStartedAsync();
    return Results.Ok(new { ok = true });
});

app.MapPost("/api/torrent/stop", async () =>
{
    await torrent.StopAsync();
    return Results.Ok(new { ok = true });
});

app.MapPost("/api/update/check", async (CancellationToken ct) =>
{
    await updates.CheckAsync(ct);
    return Results.Ok(new { updates.Status.State, updates.Status.Message });
});

// ---------------- browsing ----------------

app.MapGet("/api/depots", (string? q, string? sort, string? dir, string? filter, int? skip, int? take) =>
{
    var cat = loader.Catalog;
    if (cat is null) return Results.Ok(new { total = 0, items = Array.Empty<object>() });

    IEnumerable<Depot> items = cat.Ordered;

    if (!string.IsNullOrWhiteSpace(q))
    {
        var needle = q.Trim();

        // Wrapping the term in quotes asks for an exact match: "440" is depot 440 alone,
        // where a bare 440 also brings back 4400, 14400 and every other id containing it.
        bool exact = needle.Length >= 2
                     && (needle[0] == '"' && needle[^1] == '"' || needle[0] == '\'' && needle[^1] == '\'');
        if (exact) needle = needle[1..^1].Trim();

        if (needle.Length == 0)
        {
            // A lone pair of quotes filters nothing.
        }
        else if (exact)
        {
            items = items.Where(d =>
                d.Id.ToString() == needle ||
                string.Equals(names.DisplayFor(d.Id), needle, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            items = items.Where(d =>
                d.Id.ToString().Contains(needle, StringComparison.Ordinal) ||
                names.DisplayFor(d.Id).Contains(needle, StringComparison.OrdinalIgnoreCase));
        }
    }

    items = filter switch
    {
        "reset" => items.Where(d => d.HasReset),
        "incomplete" => items.Where(d => !d.IsComplete),
        "single" => items.Where(d => d.DistinctVersions == 1),
        "big" => items.Where(d => d.ApproxDatBytes >= 1_000_000_000L),
        _ => items,
    };

    bool desc = dir == "desc";
    Func<Depot, object> key = sort switch
    {
        "versions" => d => d.DistinctVersions,
        "size" => d => d.ApproxDatBytes + d.ApproxBlobBytes,
        "date" => d => d.LastDate,
        "files" => d => d.Dats.Count + d.Blobs.Count,
        _ => d => d.Id,
    };

    var list = items.ToList();
    list.Sort((a, b) =>
    {
        int c = Comparer<object>.Default.Compare(key(a), key(b));
        if (c == 0) c = a.Id.CompareTo(b.Id);
        return desc ? -c : c;
    });

    int pageSize = Math.Clamp(take is null or <= 0 ? 200 : take.Value, 1, 2000);
    int offset = Math.Max(0, skip ?? 0);

    return Results.Ok(new
    {
        total = list.Count,
        items = list.Skip(offset).Take(pageSize)
            .Select(d => Dto.Summary(d, names.Get(d.Id), names.DisplayFor(d.Id), names.SourceFor(d.Id))),
    });
});

app.MapGet("/api/depots/{id:int}", (int id) =>
{
    var cat = loader.Catalog;
    if (cat is null || !cat.Depots.TryGetValue(id, out var d)) return Results.NotFound();

    string blobDir = Path.Combine(settings.DataDir, "blobs");
    string datDir = Path.Combine(settings.DataDir, "dats");

    // Which branch each blob sits on, so that where a version exists twice the card can ask which
    // branch is wanted instead of which checksum.
    var (branches, branchOf) = changes.Branches(d);

    return Results.Ok(new
    {
        summary = Dto.Summary(d, names.Get(d.Id), names.DisplayFor(d.Id), names.SourceFor(d.Id)),
        branches = branches.Select(b => new
        {
            b.Index, b.HeadCrc, b.MinVersion, b.MaxVersion,
            b.FirstDate, b.LastDate, b.BlobCount, b.ForksFromVersion,
        }),
        versions = Enumerable.Range(0, d.MaxVersion + 1).Select(v => new
        {
            version = v,
            dats = d.Dats.Where(e => e.Version == v).Select(e => Dto.File(e, datDir)),
            blobs = d.Blobs.Where(e => e.Version == v)
                .Select(e => Dto.File(e, blobDir, branchOf.TryGetValue(e.FileName, out int bi) ? bi : null)),
        }).Where(x => x.dats.Any() || x.blobs.Any()),
    });
});

// The whole version history of a depot, newest first. Counts appear for versions whose blob is
// already on disk; the rest need the bulk fetch below, which costs kilobytes per version.
app.MapGet("/api/depots/{id:int}/versions", (int id) =>
{
    var cat = loader.Catalog;
    if (cat is null || !cat.Depots.TryGetValue(id, out var depot)) return Results.NotFound();

    var fetch = changes.StatusFor(id);
    var (branches, _) = changes.Branches(depot);

    return Results.Ok(new
    {
        depot = id,
        fetch = new { fetch.Running, fetch.Done, fetch.Total, fetch.Failed, fetch.Message },
        branches = branches.Select(b => new
        {
            b.Index, b.HeadCrc, b.MinVersion, b.MaxVersion,
            b.FirstDate, b.LastDate, b.BlobCount, b.ForksFromVersion,
        }),
        versions = changes.Summary(depot).Select(v =>
        {
            // Direct links to the mirror so the browser can save a single file without going
            // through the download queue. The swarm has no URL, so those fall back to a real host.
            var host = client.Primary.IsTorrent ? Mirrors.All[0] : client.Primary;

            var blob = depot.Blobs.FirstOrDefault(b => b.Version == v.Version && b.CrcHex == v.Crc);
            var dat = blob is null ? null : changes.DatFor(depot, blob);

            return new
            {
                v.Version, v.Crc, v.Date, v.Local, v.Branch,
                v.AddedCount, v.ChangedCount, v.RemovedCount,
                v.PayloadBytes, v.DeltaBytes, v.FilesInVersion, v.Unclassified, v.Error,
                blobUrl = blob is null ? null : host.Url(blob.RelPath),
                blobBytes = blob?.ApproxSize ?? -1,
                datUrl = dat is null ? null : host.Url(dat.RelPath),
                datBytes = dat?.ApproxSize ?? -1,
            };
        }),
    });
});

// The files one version changed. Read straight from that version's blob, no dat involved.
app.MapGet("/api/depots/{id:int}/versions/{version:int}/files", (int id, int version, string? crc) =>
{
    var cat = loader.Catalog;
    if (cat is null || !cat.Depots.TryGetValue(id, out var depot)) return Results.NotFound();

    var candidates = depot.Blobs.Where(b => b.Version == version).ToList();
    var blob = !string.IsNullOrWhiteSpace(crc)
        ? candidates.FirstOrDefault(b => b.CrcHex.Equals(crc.Trim(), StringComparison.OrdinalIgnoreCase))
        : candidates.FirstOrDefault();

    if (blob is null) return Results.Ok(new { error = $"no blob for version {version}" });

    try
    {
        var result = changes.Diff(depot, blob);
        if (result is null) return Results.Ok(new { needsFetch = true, crc = blob.CrcHex });

        var (files, unclassified) = result.Value;

        return Results.Ok(new
        {
            version,
            crc = blob.CrcHex,
            count = files.Count,
            unclassified,
            files = files.Take(20000).Select(f => new
            {
                path = f.Path,
                size = f.Size,
                delta = f.Delta,
                mode = f.Mode,
                change = f.Change,
            }),
        });
    }
    catch (Exception ex)
    {
        return Results.Ok(new { error = ex.Message });
    }
});

// Blobs for a whole span of depot ids at once — enough to browse history and search files
// across a chunk of the archive without paying for any dat.
// Same-origin relay for the browser-side chain download. The mirrors send no
// Access-Control-Allow-Origin and answer OPTIONS with 405, so a page cannot read them across
// origins at all — it has to ask us, and we stream the bytes straight through. A copy already on
// disk is served from there instead of being pulled a second time.
app.MapGet("/api/file/{dir}/{name}", async (string dir, string name, CancellationToken ct) =>
{
    if (dir is not ("blobs" or "dats")) return Results.BadRequest(new { error = "unknown folder" });

    // The name comes back to us from our own plan, but it still ends up in a path.
    if (name.Length == 0 || name.Contains('/') || name.Contains('\\') || name.Contains(".."))
        return Results.BadRequest(new { error = "bad file name" });

    string local = Path.Combine(settings.DataDir, dir, name);
    if (System.IO.File.Exists(local))
        return Results.File(local, "application/octet-stream", name);

    var host = client.Primary.IsTorrent ? Mirrors.All[0] : client.Primary;
    var upstream = await http.GetAsync(host.Url($"{dir}/{name}"),
                                       HttpCompletionOption.ResponseHeadersRead, ct);

    if (!upstream.IsSuccessStatusCode)
        return Results.StatusCode((int)upstream.StatusCode);

    return Results.Stream(await upstream.Content.ReadAsStreamAsync(ct),
                          "application/octet-stream", name);
});

Dto.PlanHost = () => client.Primary;

// How much of a chain's dat traffic is actually dead weight. Answered from blobs alone, so it
// costs nothing but the blobs that browsing the depot already needs.
app.MapGet("/api/depots/{id:int}/needed", (int id, int version) =>
{
    if (loader.Catalog is null) return Results.BadRequest(new { error = "index not loaded yet" });

    var depot = loader.Catalog.Ordered.FirstOrDefault(d => d.Id == id);
    if (depot is null) return Results.NotFound(new { error = "no such depot" });

    var chainBlobs = depot.Blobs.Where(b => b.Version <= version)
                                .OrderBy(b => b.Version)
                                .ToList();

    var target = chainBlobs.LastOrDefault(b => b.Version == version);
    if (target is null) return Results.NotFound(new { error = "no blob at that version" });

    var needed = changes.NeededDatVersions(chainBlobs, target);
    if (needed is null)
        return Results.Ok(new { resolved = false, reason = "not every blob is on disk, or an id is unaccounted for" });

    var datsByVersion = depot.Dats.Where(d => d.Version <= version)
                                  .GroupBy(d => d.Version)
                                  .ToDictionary(g => g.Key, g => g.First());

    long full = datsByVersion.Values.Sum(d => Math.Max(0, d.ApproxSize));
    long slim = needed.Where(datsByVersion.ContainsKey)
                      .Sum(v => Math.Max(0, datsByVersion[v].ApproxSize));

    return Results.Ok(new
    {
        resolved = true,
        chainVersions = datsByVersion.Count,
        neededVersions = needed.Count,
        needed,
        fullBytes = full,
        neededBytes = slim,
    });
});

app.MapGet("/api/seed", () =>
{
    torrent.SampleSeed();
    var st = torrent.Status;

    return Results.Ok(new
    {
        enabled = settings.TorrentEnabled && settings.SeedDownloaded,
        engineEnabled = settings.TorrentEnabled,
        state = st.SeedState,
        message = st.SeedMessage,
        files = st.SeedFiles,
        bytes = st.SeedBytes,
        uploadRate = st.SeedUploadRate,
        uploaded = st.SeedUploaded,
        peers = st.SeedPeers,
    });
});

app.MapPost("/api/seed", async (SeedRequest req) =>
{
    settings.SeedDownloaded = req.Enabled;
    settings.Save();

    if (req.Enabled && settings.TorrentEnabled) _ = Task.Run(() => torrent.StartSeedingAsync());
    else await torrent.StopSeedingAsync();

    return Results.Ok(new { ok = true, enabled = settings.SeedDownloaded });
});

app.MapPost("/api/installs", (InstallRequest req) =>
{
    var cat = loader.Catalog;
    if (cat is null) return Results.BadRequest(new { error = "index not loaded yet" });

    var app0 = apps.Apps.FirstOrDefault(a => a.Appid == req.Appid);
    if (app0 is null) return Results.NotFound(new { error = $"no app {req.Appid}" });

    var build = app0.Builds.FirstOrDefault(b => b.Id == req.Build);
    if (build is null) return Results.NotFound(new { error = $"no build '{req.Build}'" });

    // The client sends which depots it wants, since optional ones are its to choose. Anything not
    // in the build is ignored rather than trusted.
    var wanted = req.Depots is { Count: > 0 }
        ? build.Depots.Where(d => req.Depots.Contains(d.Depot)).ToList()
        : build.Depots.Where(d => !d.Optional).ToList();

    if (wanted.Count == 0) return Results.BadRequest(new { error = "nothing selected" });

    var install = installs.Start(cat, app0, build, wanted, names.DisplayFor);
    return Results.Ok(new { installId = install.Id });
});

app.MapGet("/api/installs", () => Results.Ok(installs.All.Select(Dto.Install)));

app.MapPost("/api/installs/{id}/cancel", (string id) =>
    installs.Cancel(id) ? Results.Ok(new { ok = true }) : Results.NotFound());

app.MapGet("/api/apps", () =>
{
    var cat = loader.Catalog;

    return Results.Ok(new
    {
        status = new
        {
            apps.Status.State,
            apps.Status.Message,
            apps.Status.Source,
            apps.Status.Count,
            apps.Status.FetchedUtc,
        },
        items = apps.Apps.Select(a => new
        {
            a.Appid,
            a.Name,
            builds = a.Builds.Select(b => new
            {
                b.Id,
                b.Name,
                b.Date,
                b.Notes,
                depots = b.Depots.Select(d =>
                {
                    var depot = cat?.Ordered.FirstOrDefault(x => x.Id == d.Depot);

                    return new
                    {
                        d.Depot,
                        d.Version,
                        d.Role,
                        d.Optional,
                        // Resolved here so a definition never repeats what the catalog already says,
                        // and so a pin that no longer resolves is visible instead of silent.
                        name = depot is null ? null : names.DisplayFor(d.Depot),
                        known = depot is not null && depot.Blobs.Any(x => x.Version == d.Version),
                        maxVersion = depot?.MaxVersion ?? -1,
                    };
                }),
            }),
        }),
    });
});

app.MapPost("/api/blobs/range", (BlobRangeRequest req) =>
{
    if (loader.Catalog is null) return Results.BadRequest(new { error = "index not loaded yet" });

    changes.FetchRange(loader.Catalog, req.From, req.To);
    return Results.Ok(new { ok = true });
});

app.MapGet("/api/blobs/range", (int? from, int? to) =>
{
    var st = changes.RangeStatus;
    object? preview = null;

    if (from is { } f && to is { } t && loader.Catalog is { } cat)
    {
        int lo = Math.Min(f, t), hi = Math.Max(f, t);
        var span = cat.Ordered.Where(d => d.Id >= lo && d.Id <= hi).ToList();
        var blobs = span.SelectMany(d => d.Blobs).ToList();
        int missing = blobs.Count(b => !changes.HasLocal(b));

        preview = new
        {
            depots = span.Count,
            blobs = blobs.Count,
            missing,
            bytes = blobs.Where(b => b.ApproxSize > 0).Sum(b => b.ApproxSize),
        };
    }

    return Results.Ok(new { st.Running, st.Done, st.Total, st.Failed, st.Message, preview });
});

// Pulls every blob the depot has, so the full history can be expanded offline.
app.MapPost("/api/depots/{id:int}/blobs", (int id) =>
{
    var cat = loader.Catalog;
    if (cat is null || !cat.Depots.TryGetValue(id, out var depot)) return Results.NotFound();

    changes.FetchAll(depot);
    return Results.Ok(new { ok = true, blobs = depot.Blobs.Count });
});

// Builds the path index over blobs already on disk. Nothing is downloaded for it.
app.MapPost("/api/files/index", () =>
{
    if (loader.Catalog is null) return Results.BadRequest(new { error = "index not loaded yet" });
    fileSearch.Build(loader.Catalog);
    return Results.Ok(new { ok = true });
});

// Finds a file path anywhere in the depots whose blobs have been fetched.
app.MapGet("/api/files/search", (string? q, int? limit) =>
{
    if (string.IsNullOrWhiteSpace(q)) return Results.Ok(new { hits = Array.Empty<object>(), total = 0 });

    int cap = Math.Clamp(limit is null or <= 0 ? 300 : limit.Value, 1, 2000);
    var hits = fileSearch.Search(q, cap);

    return Results.Ok(new
    {
        indexed = fileSearch.Status.PathCount,
        depots = fileSearch.Status.DepotsIndexed,
        running = fileSearch.Status.Running,
        // Blobs downloaded since the index was built are invisible to a search until it is redone,
        // which is worth saying out loud rather than letting the results look complete.
        blobsIndexed = fileSearch.Status.BlobsIndexed,
        blobsOnDisk = fileSearch.BlobsOnDisk(),
        truncated = hits.Count >= cap,
        hits = hits.Select(h => new
        {
            depot = h.Depot,
            path = h.Path,
            name = names.DisplayFor(h.Depot),
        }),
    });
});

app.MapGet("/api/search", (string? q, int? take) =>
{
    var cat = loader.Catalog;
    if (cat is null || string.IsNullOrWhiteSpace(q)) return Results.Ok(Array.Empty<object>());

    var needle = q.Trim();
    int limit = Math.Clamp(take is null or <= 0 ? 100 : take.Value, 1, 500);

    var hits = new List<object>();
    foreach (var d in cat.Ordered)
    {
        foreach (var e in d.Dats.Concat(d.Blobs))
        {
            if (e.FileName.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                hits.Add(Dto.File(e, Path.Combine(settings.DataDir, e.DirName)));
                if (hits.Count >= limit) return Results.Ok(hits);
            }
        }
    }
    return Results.Ok(hits);
});

// ---------------- plan / download / extract ----------------

app.MapPost("/api/plan", async (PlanRequest req, CancellationToken ct) =>
{
    var cat = loader.Catalog;
    if (cat is null) return Results.BadRequest(new { error = "index not loaded yet" });

    var plan = await ChainResolver.ResolveAsync(cat, client, settings.DataDir, req.Depot, req.Version, req.BlobCrc, ct);
    plan.FullChain = req.FullChain is true;
    if (!plan.FullChain) changes.Prune(plan);
    return Results.Ok(Dto.Plan(plan, settings));
});

app.MapPost("/api/download", async (PlanRequest req, CancellationToken ct) =>
{
    var cat = loader.Catalog;
    if (cat is null) return Results.BadRequest(new { error = "index not loaded yet" });

    var plan = await ChainResolver.ResolveAsync(cat, client, settings.DataDir, req.Depot, req.Version, req.BlobCrc, ct);
    plan.FullChain = req.FullChain is true;
    if (!plan.FullChain) changes.Prune(plan);
    if (plan.Error is not null || plan.NeedsChoice) return Results.Ok(Dto.Plan(plan, settings));

    // Checked here and not only in the browser. The button that leads here is disabled when the
    // plan does not fit, but a disabled button is a courtesy, not a guarantee — this endpoint is
    // also reached by a stale page, a second window, and anything replaying a request — and the
    // failure it prevents is a full disk part-way through a download that has to start over.
    if (!Disk.Fits(settings.DataDir, Dto.RemainingBytes(plan, settings), out var space))
    {
        plan.Error = Dto.NotEnoughSpace(plan, settings, space);
        return Results.Ok(Dto.Plan(plan, settings));
    }

    var job = downloads.Start(plan);
    return Results.Ok(new { jobId = job.Id, plan = Dto.Plan(plan, settings) });
});

app.MapGet("/api/jobs", () => downloads.Jobs
    .OrderByDescending(j => j.StartedUtc)
    .Select(Dto.Job));

app.MapPost("/api/jobs/{id}/cancel", (string id) => { downloads.Cancel(id); return Results.Ok(new { ok = true }); });
app.MapPost("/api/jobs/clear", () => { downloads.Clear(); return Results.Ok(new { ok = true }); });

app.MapPost("/api/extract", (ExtractRequest req) =>
{
    // Extracted files are stamped with the date of the version that wrote them, which only the
    // catalog knows. Blob dates rather than dat dates: dat timestamps collapse in the dump, with
    // depot 205 showing 102 dats across 26 distinct values, while its blobs carry 102.
    var dates = loader.Catalog?.Ordered
        .FirstOrDefault(d => d.Id == req.Depot)?.Blobs
        .GroupBy(b => b.Version)
        .ToDictionary(g => g.Key, g => g.Max(b => b.Date));

    var run = extractor.Start(req.Depot, req.Version, req.BlobCrc, req.Filter, req.KeyHex, dates);
    return Results.Ok(new { runId = run.Id });
});

app.MapGet("/api/extract", () => extractor.Runs
    .OrderByDescending(r => r.StartedUtc)
    .Select(r => new
    {
        r.Id, r.Depot, r.Version, r.BlobCrc, r.OutDir, r.Status, r.Error,
        started = r.StartedUtc, finished = r.FinishedUtc,
        progress = new
        {
            r.Progress.TotalFiles,
            r.Progress.DoneFiles,
            r.Progress.FailedFiles,
            r.Progress.BytesWritten,
            r.Progress.Current,
        },
        log = r.Log.ToArray(),
    }));

app.MapPost("/api/extract/{id}/cancel", (string id) => { extractor.Cancel(id); return Results.Ok(new { ok = true }); });
app.MapPost("/api/extract/clear", () => { extractor.Clear(); return Results.Ok(new { ok = true }); });

app.MapPost("/api/reveal", (RevealRequest req) =>
{
    try
    {
        string target = req.Path;
        if (string.IsNullOrWhiteSpace(target)) return Results.BadRequest(new { error = "empty path" });
        if (!Directory.Exists(target) && !File.Exists(target)) Directory.CreateDirectory(target);
        OpenWithDefaultApplication(target);
        return Results.Ok(new { ok = true });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// ---------------- go ----------------

_ = updates.CheckAsync();

// Startup: load the index, race the mirrors, switch to the fastest, then start naming depots.
// Refreshing the curated names is deliberately off the startup path: raw.githubusercontent.com is
// blocked on some networks and does not fail fast, so awaiting it used to hold the whole UI for the
// length of the fetch timeout before anything could be drawn.
_ = Task.Run(async () =>
{
    await labels.RefreshAsync(Settings.RootFor(baseDir));
    names.Refresh();
});

_ = Task.Run(() => apps.RefreshAsync(Settings.RootFor(baseDir)));

// Both switches have to be on, and on a first run the notice has to have been answered first.
//
// Sharing announces to every tracker it has, which on a machine that has just unzipped the app
// means contacting well over a hundred hosts within seconds of launch, before the person running it
// has agreed to anything. That is bad manners on its own, and it is also what an unsigned new
// executable looks like to a reputation engine: a VirusTotal sandbox recorded 117 contacted domains
// on first run, and Kaspersky answered with UDS:Trojan.Win64.SBadur.gen — a cloud verdict on
// behaviour, not a signature on anything in the file.
//
// So the first run is quiet. The notice is shown, and the swarm is joined once somebody has said
// yes; from then on SharingNoticeSeen is set and startup shares as before.
if (settings.TorrentEnabled && settings.SeedDownloaded && settings.SharingNoticeSeen)
    _ = Task.Run(() => torrent.StartSeedingAsync());

_ = Task.Run(async () =>
{
    // Cached curated names first: they cover the whole archive today, which means both sweeps
    // below usually find nothing left to do and no blob or store request is made at all. This
    // reads a local file only — refreshing them from GitHub is a separate task started below,
    // because that fetch can sit on an unreachable host for the whole timeout and the index is
    // the one thing the UI cannot render without.
    await labels.LoadCachedAsync(Settings.RootFor(baseDir));
    await apps.LoadCachedAsync(Settings.RootFor(baseDir));
    names.Refresh();

    await loader.LoadAsync(refreshIndex: false, withSizes: true);
    if (loader.Catalog is null) return;

    try
    {
        await Mirrors.TestAllAsync(http);

        // Racing the HTTP mirrors says nothing about the swarm, so someone who chose the swarm is
        // left on it. Without this the race quietly moved them back to an HTTP mirror a few seconds
        // into every start, and the only symptom was that picking the torrent never seemed to do
        // anything — which is exactly what it looked like from the outside.
        if (!client.Primary.IsTorrent)
        {
            var best = Mirrors.All.Where(m => !m.IsTorrent && m.Reachable && m.SpeedBps > 0).MaxBy(m => m.SpeedBps);
            if (best is not null && best.Id != client.Primary.Id)
            {
                client.Primary = best;
                settings.MirrorId = best.Id;
                settings.Save();
            }
        }
    }
    catch
    {
        // Keep whatever mirror is configured if the race fails.
    }

    // Both passes run together: the mirror sweep is bandwidth-bound and the Steam pass is
    // rate-limited to well under one request a second, so neither holds the other up.
    names.Start(loader.Catalog);
    names.StartSteam(loader.Catalog);
});

string url = AddressFor(port);
Console.WriteLine($"steam2browser  ->  {url}");
// The numeric address is printed too, and deliberately. Browsers resolve .localhost themselves, but
// a curl on an unusual resolver, a script, or a machine with an aggressive DNS policy may not — and
// somebody staring at a name that will not open needs the address that always works on the line
// below, not in an issue thread.
Console.WriteLine($"               or  http://127.0.0.1{(port == 80 ? "" : $":{port}")}/");
Console.WriteLine($"data dir: {settings.DataDir}");
Console.WriteLine("press Ctrl+C to stop");

if (!noBrowser)
{
    try { OpenWithDefaultApplication(url); }
    catch { /* headless is fine, the URL is printed above */ }
}

try
{
    app.Run();
}
catch (IOException ex)
{
    // Something grabbed the port between the probe above and Kestrel binding it.
    Console.Error.WriteLine($"could not start on port {port}: {ex.Message}");
    Console.Error.WriteLine("Pass --port=NNNN to choose another one.");
    return 1;
}

return 0;

// ---------------- request bodies ----------------

static void OpenWithDefaultApplication(string target)
{
    if (OperatingSystem.IsLinux())
    {
        var startInfo = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
        startInfo.ArgumentList.Add(target);
        Process.Start(startInfo);
        return;
    }

    if (OperatingSystem.IsMacOS())
    {
        var startInfo = new ProcessStartInfo("open") { UseShellExecute = false };
        startInfo.ArgumentList.Add(target);
        Process.Start(startInfo);
        return;
    }

    // Shell execution lets Windows select Explorer for directories and the default browser
    // for URLs without hard-coding a Windows executable into the rest of the application.
    Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
}

internal sealed record SettingsPatch(
    string? MirrorId, bool? Failover, int? Concurrency, bool? VerifyHashes, bool? PhasedDownloads, bool? TorrentEnabled, bool? SwarmAssist, bool? SharingNoticeSeen, int? BlobConcurrency, int? DatConcurrency, int? WarmupLookahead, int? BigFileMb,
    int? TorrentPort, int? TorrentUploadKbps, int? TorrentDownloadKbps,
    string? DataDir, string? ExtractOutDir, string[]? ExtraTrackers);

internal sealed record ReloadRequest(bool Refresh, bool Sizes);
internal sealed record PlanRequest(int Depot, int Version, string? BlobCrc, bool? FullChain);
internal sealed record BlobRangeRequest(int From, int To);
internal sealed record InstallRequest(int Appid, string Build, List<int>? Depots);
internal sealed record SeedRequest(bool Enabled);
internal sealed record ExtractRequest(int Depot, int Version, string? BlobCrc, string? Filter, string? KeyHex);
internal sealed record RevealRequest(string Path);

internal static class Dto
{
    /// <summary>
    /// Bytes a plan still has to fetch. Files already on disk are not downloaded again, so they are
    /// not counted against the free space either.
    /// </summary>
    public static long RemainingBytes(ChainPlan p, Settings s) => p.Files
        .Where(f => !System.IO.File.Exists(Path.Combine(s.DataDir, f.Entry.DirName, f.Entry.FileName)))
        .Sum(f => f.Size);

    /// <summary>
    /// Free space where downloads land, and whether <paramref name="needed"/> bytes fit in it.
    /// </summary>
    public static object Space(string dir, long needed)
    {
        var d = Disk.For(dir);
        return new
        {
            root = d.Root,
            free = d.FreeBytes,
            total = d.TotalBytes,
            used = d.UsedBytes,
            headroom = Disk.Headroom,
            error = d.Error,
            // Null when the drive could not be measured, which the UI shows as "unknown" rather
            // than blocking on a number it does not have.
            fits = d.Error is not null ? (bool?)null : d.FreeBytes >= needed + Disk.Headroom,
            needed,
        };
    }

    public static string NotEnoughSpace(ChainPlan p, Settings s, DiskSpace space)
    {
        long needed = RemainingBytes(p, s);
        return $"not enough free space on {space.Root}: {Fmt(needed)} to download plus "
             + $"{Fmt(Disk.Headroom)} kept free, but {Fmt(space.FreeBytes)} is available";
    }

    private static string Fmt(long b) => b >= 1_000_000_000L
        ? $"{b / 1_000_000_000d:0.##} GB"
        : $"{b / 1_000_000d:0.##} MB";

    public static object Summary(Depot d, NameRecord? name = null, string? display = null, string? source = null) => new
    {
        id = d.Id,
        name = string.IsNullOrEmpty(display) ? null : display,
        nameSource = source,
        manifestName = string.IsNullOrEmpty(name?.Label) ? null : name!.Label,
        steamType = name?.SteamType,
        roots = name?.Roots,
        manifestAppId = name is { Error: null } ? name.AppId : (uint?)null,
        nameError = name?.Error,
        versions = d.DistinctVersions,
        maxVersion = d.MaxVersion,
        dats = d.Dats.Count,
        blobs = d.Blobs.Count,
        datBytes = d.ApproxDatBytes,
        blobBytes = d.ApproxBlobBytes,
        first = d.FirstDate == default ? null : d.FirstDate.ToString("yyyy-MM-dd"),
        last = d.LastDate == default ? null : d.LastDate.ToString("yyyy-MM-dd"),
        hasKey = DepotKeys.Has(d.Id),
        // Whether it is encrypted at all is the real question; the key table only matters if it is.
        encrypted = name?.Encrypted,
        needsKey = name?.Encrypted == true && !DepotKeys.Has(d.Id),
        hasReset = d.HasReset,
        forkedVersions = d.ForkedVersions,
        complete = d.IsComplete,
        missingDats = d.MissingDats,
        missingBlobs = d.MissingBlobs,
    };

    public static object File(Entry e, string localDir, int? branch = null) => new
    {
        name = e.FileName,
        depot = e.Depot,
        version = e.Version,
        crc = e.CrcHex,
        sha = e.Sha,
        kind = e.Kind == Kind.Dat ? "dat" : "blob",
        size = e.ApproxSize,
        date = e.Date == default ? null : e.Date.ToString("yyyy-MM-dd HH:mm:ss"),
        local = System.IO.File.Exists(Path.Combine(localDir, e.FileName)),
        // Which branch this blob belongs to, so a fork can be offered as a choice between branches
        // rather than between two checksums that mean nothing on their own.
        branch,
    };

    public static object Plan(ChainPlan p, Settings s) => new
    {
        depot = p.Depot,
        version = p.TargetVersion,
        blobCrc = p.BlobCrc,
        mode = p.Mode,
        error = p.Error,
        needsChoice = p.NeedsChoice,
        choices = p.Choices,
        warnings = p.Warnings,
        totalBytes = p.TotalBytes,
        totalExact = p.TotalExact,
        fileCount = p.Files.Count,
        datCount = p.Files.Count(f => f.Entry.Kind == Kind.Dat),
        blobCount = p.Files.Count(f => f.Entry.Kind == Kind.Blob),
        alreadyLocal = p.Files.Count(f =>
            System.IO.File.Exists(Path.Combine(s.DataDir, f.Entry.DirName, f.Entry.FileName))),
        remainingBytes = RemainingBytes(p, s),
        // Everything the button needs to explain itself: how much room there is, and whether this
        // particular download fits in it.
        disk = Space(s.DataDir, RemainingBytes(p, s)),
        extractArgs = p.ExtractArgs,
        // Null when it could not be worked out yet, which the UI reports rather than hiding.
        skippedDats = p.SkippedDats,
        skippedBytes = p.SkippedBytes,
        chainDats = p.ChainDats,
        fullChain = p.FullChain,
        files = p.Files.Take(2000).Select(f => new
        {
            name = f.Entry.FileName,
            kind = f.Entry.Kind == Kind.Dat ? "dat" : "blob",
            version = f.Entry.Version,
            crc = f.Entry.CrcHex,
            size = f.Size,
            exact = f.SizeExact,
            local = System.IO.File.Exists(Path.Combine(s.DataDir, f.Entry.DirName, f.Entry.FileName)),
            // Subfolder and absolute URL, so a browser-side download can lay the chain out the
            // same way the app does and fetch it without going through the queue. The swarm has
            // no URL of its own, so that mirror falls back to a real host.
            dir = f.Entry.DirName,
            url = (PlanHost().IsTorrent ? Mirrors.All[0] : PlanHost()).Url(f.Entry.RelPath),
        }),
    };

    /// <summary>Set once at startup; the DTO needs a mirror to build absolute file URLs.</summary>
    public static Func<Mirror> PlanHost = () => Mirrors.All[0];

    public static object Install(Install i) => new
    {
        id = i.Id,
        appid = i.Appid,
        name = i.AppName,
        build = i.BuildId,
        outDir = i.OutDir,
        status = i.Status,
        error = i.Error,
        started = i.StartedUtc,
        finished = i.FinishedUtc,
        steps = i.Steps.Select(s => new
        {
            s.Depot,
            s.Version,
            s.Role,
            s.Name,
            s.Status,
            s.Error,
            s.TotalBytes,
            s.DoneBytes,
            s.FilesWritten,
            s.JobId,
            s.RunId,
        }),
        // Summed here so the panel can draw one bar for the whole install.
        totalBytes = i.Steps.Sum(s => Math.Max(0, s.TotalBytes)),
        doneBytes = i.Steps.Sum(s => Math.Max(0, s.DoneBytes)),
        doneSteps = i.Steps.Count(s => s.Status == "done"),
        log = i.Log.ToArray(),
    };

    public static object Job(DownloadJob j) => new
    {
        id = j.Id,
        depot = j.Depot,
        version = j.Version,
        blobCrc = j.BlobCrc,
        mode = j.Mode,
        status = j.Status,
        error = j.Error,
        totalFiles = j.TotalFiles,
        doneFiles = j.DoneFiles,
        skippedFiles = j.SkippedFiles,
        failedFiles = j.FailedFiles,
        totalBytes = j.TotalBytes,
        doneBytes = Interlocked.Read(ref j.DoneBytes),
        speedBps = j.SpeedBps,
        extractArgs = j.ExtractArgs,
        started = j.StartedUtc,
        finished = j.FinishedUtc,
        active = j.Active.Values
            .Where(f => f.State == "running")
            .OrderByDescending(f => f.Done)
            .Take(12)
            .Select(f => new { f.Name, f.Done, f.Total }),
        log = j.Log.ToArray(),
    };
}
