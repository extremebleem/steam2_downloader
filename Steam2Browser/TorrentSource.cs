using MonoTorrent;
using MonoTorrent.Client;
using System.Net;

namespace Steam2Browser;

public sealed class TorrentStatus
{
    /// <summary>off | starting | metadata | ready | downloading | error</summary>
    public string State = "off";

    /// <summary>Seeding runs on its own manager, so it reports its own state.</summary>
    public string SeedState = "off";

    public string SeedMessage = "";
    public int SeedFiles;
    public long SeedBytes;
    public double SeedUploadRate;
    public int SeedPeers;
    public long SeedUploaded;

    public string Message = "";
    public string? Error;

    public bool HasMetadata;
    public int TotalFiles;
    public int SelectedFiles;
    public long SelectedBytes;

    public int Trackers;
    public int Peers;
    public int Seeds;
    public double DownloadRate;
    public double UploadRate;
    public double SelectedProgress;
    public string TorrentState = "";
}

/// <summary>
/// The archive as a BitTorrent swarm, used as a fourth source alongside the three HTTP mirrors.
///
/// The torrent holds all 116 339 files — 13.32 TB, matching the archive exactly — so it is only
/// usable because BitTorrent can fetch selected files: the piece picker is handed the files a chain
/// actually needs and never asks the swarm for anything else.
///
/// Metadata is fetched from the swarm once (the magnet carries no file list) and cached on disk,
/// so later runs skip that wait.
/// </summary>
public sealed class TorrentSource(Settings settings)
{
    /// <summary>
    /// The published magnet for the archive. Its first tracker is spelled "dp://", which is not a
    /// scheme MonoTorrent (or anything else) understands, so trackers are filtered before parsing.
    /// </summary>
    public const string Magnet =
        "magnet:?xt=urn:btih:0f3e7a75c0f885dde481054d4bcd8cd14eab51c8&dn=steam2" +
        "&xl=13316620144984" +
        "&tr=udp%3A%2F%2Ftracker.publictracker.xyz%3A6969%2Fannounce" +
        "&tr=udp%3A%2F%2Ftracker.opentrackr.org%3A1337%2Fannounce";

    /// <summary>
    /// How long to wait for a peer to hand over the file list before giving up and saying so.
    /// Reaching it usually means the trackers are unreachable — all three in this magnet time out
    /// on some networks — and DHT alone found nobody.
    /// </summary>
    private static readonly TimeSpan MetadataTimeout = TimeSpan.FromMinutes(5);


    public TorrentStatus Status { get; } = new();

    private ClientEngine? _engine;
    private TorrentManager? _manager;

    /// <summary>Archive-relative path ("dats/x.dat") to the file inside the torrent.</summary>
    private readonly Dictionary<string, ITorrentManagerFile> _byArchivePath =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _seedGate = new(1, 1);

    private TorrentManager? _seedManager;

    /// <summary>
    /// What keeps the download to the files a chain asked for. It replaces the engine's own picker
    /// on the downloading manager and is the only thing standing between a start and all 13.32 TB,
    /// so it goes on before the manager is ever started.
    /// </summary>
    private SelectionPieceRequester? _requester;

    /// <summary>
    /// The files the running download selected, kept for the progress readings: the manager's own
    /// PartialProgress counts everything above DoNotDownload, which is now every file.
    /// </summary>
    private IReadOnlyList<ITorrentManagerFile> _selection = Array.Empty<ITorrentManagerFile>();

    /// <summary>
    /// Set when sharing wanted to take stock of the disk but a download was using the manager, so
    /// the work is done once that download is out of the way instead of on top of it.
    /// </summary>
    private volatile bool _seedRefreshPending;

    /// <summary>
    /// Whether sharing is still wanted by the time each stage of starting it finishes.
    ///
    /// Starting takes a while — reading the file list, linking, then hashing what was linked — and
    /// the switch can be thrown at any point during it, which is exactly what happens when someone
    /// is shown the notice on first run and says no. Stopping could not reach a start that had not
    /// finished, because the manager it looks for is only published at the very end, so the start
    /// ran to completion and announced itself as sharing over the top of the refusal. Sharing after
    /// being told not to is not a glitch to tidy up later, so every stage checks this before going
    /// on and the last one checks it before committing.
    /// </summary>
    private volatile bool _seedWanted;

    /// <summary>
    /// Sharing was asked for and has not since been called off.
    /// </summary>
    private bool SeedStillWanted => _seedWanted && settings.TorrentEnabled && settings.SeedDownloaded;

    /// <summary>Leaves the display saying what is true: sharing was asked for and then was not.</summary>
    private void CallOff()
    {
        _seedManager = null;
        Status.SeedState = "off";
        Status.SeedMessage = "";
        Status.SeedFiles = 0;
        Status.SeedBytes = 0;
        Status.SeedUploadRate = 0;
        Status.SeedPeers = 0;
    }

    /// <summary>
    /// Whether a download currently owns the manager.
    ///
    /// Downloading and sharing are the same manager and the same picker, and sharing's half of that
    /// begins by clearing the selection and stopping the manager. Doing so while a download is in
    /// flight takes away both the files it asked for and the engine fetching them, and the download
    /// then sits at zero forever because nothing is left to tell it otherwise. That is what made
    /// downloading over the torrent look broken to anyone who left sharing on — which is everyone,
    /// since it is on by default.
    /// </summary>
    private bool DownloadInFlight => _selection.Count > 0;

    /// <summary>
    /// Ready means the file list is known and the selection picker is on: without the picker a
    /// download would select files the manager knows nothing about and start on all 13.32 TB.
    /// </summary>
    /// <summary>
    /// Where the engine is allowed to write.
    ///
    /// Inside the download directory rather than beside the index, because sharing hard-links
    /// archive files into it and a hard link cannot cross a volume. Anyone keeping the archive on a
    /// second drive — which is most of the reason to change that setting at all — would otherwise
    /// share nothing, and silently, because every link would simply fail.
    ///
    /// Inside it rather than next to it, so it can never land at the root of someone's drive. Its
    /// own dats/ and blobs/ sit a level below the archive's, so the two never meet.
    /// </summary>
    private string EngineDirectory => Path.Combine(settings.DataDir, "torrent-data");

    public bool Ready => _manager is { HasMetadata: true } && _requester is not null;

    // ---------------- startup ----------------

    /// <summary>
    /// Brings the engine up and waits for the file list. Safe to call repeatedly; only the first
    /// call does the work.
    /// </summary>
    /// <summary>
    /// Shares the archive files already on disk back to the swarm.
    ///
    /// This runs on its own manager rooted at the archive folder, which is the one thing the
    /// downloading manager is deliberately kept away from: pointed there, it allocates a file for
    /// everything it might want and once left 35 166 empty placeholders that looked like completed
    /// downloads. The protection here is that every file is parked at DoNotDownload before the
    /// manager is ever started, and only files that already exist on disk are lifted off it — so
    /// there is nothing it could decide to create.
    ///
    /// Nothing is downloaded by this manager. It hash-checks what is there and serves it.
    /// </summary>
    /// <summary>
    /// Shares the archive files already on disk back to the swarm.
    ///
    /// There is one manager, not two: MonoTorrent refuses a second manager for the same infohash
    /// ("A manager for this torrent has already been registered"), so sharing cannot have an engine
    /// of its own pointed at the archive.
    ///
    /// So the download manager does both, and the files reach it as hard links. Its own directory
    /// stays the one place the engine may write — the archive is never handed to it, which is what
    /// keeps the empty files MonoTorrent creates at startup out of the depot data the rest of the
    /// app reads. A link costs no space and no copy, and removing one leaves the original alone.
    ///
    /// With the picker selecting nothing, a running manager asks the swarm for no piece at all
    /// while still serving every piece it holds. That is exactly a seeder, and it is why sharing
    /// and downloading can share a manager: a download selects its chain, and puts the selection
    /// back to nothing when it is done.
    /// </summary>
    public async Task StartSeedingAsync(CancellationToken ct = default)
    {
        if (!settings.TorrentEnabled)
        {
            Status.SeedState = "off";
            Status.SeedMessage = "the torrent engine is switched off";
            return;
        }

        await _seedGate.WaitAsync(ct);
        try
        {
            Status.SeedState = "starting";
            Status.SeedMessage = "reading the file list";
            _seedWanted = true;

            if (!await EnsureStartedAsync(ct) || _manager is null)
            {
                Status.SeedState = "error";
                Status.SeedMessage = "could not get the torrent file list";
                return;
            }

            if (!SeedStillWanted) { CallOff(); return; }

            Status.SeedMessage = "linking what is already downloaded";
            var (linked, bytes) = LinkArchiveIntoTorrentData();

            if (!SeedStillWanted) { CallOff(); return; }

            Status.SeedFiles = linked;
            Status.SeedBytes = bytes;

            if (linked == 0)
            {
                Status.SeedState = "idle";
                // The sweep's own account of what it rejected is left in place: at zero that is the
                // only thing worth reading, and a friendlier sentence would hide it.
                return;
            }

            if (DownloadInFlight)
            {
                Status.SeedMessage = "waiting for the download to finish before sharing";
                _seedRefreshPending = true;
                return;
            }

            // The manager has to look at the links to know it holds those pieces. Nothing is
            // selected, so this cannot turn into a download.
            Status.SeedMessage = $"checking {linked} file(s) before sharing them";
            _requester?.SelectNone();

            if (_manager.State != TorrentState.Stopped)
                await _manager.StopAsync(TimeSpan.FromSeconds(10));

            await _manager.HashCheckAsync(autoStart: true);

            // The hash check cannot be interrupted part way, so a refusal that arrived during it is
            // honoured here instead: the manager is put back down and nothing is ever offered.
            if (!SeedStillWanted)
            {
                try { await _manager.StopAsync(TimeSpan.FromSeconds(10)); } catch { }
                CallOff();
                return;
            }

            _seedManager = _manager;
            Status.SeedState = "sharing";
            Status.SeedMessage = $"sharing {linked} file(s)";
        }
        catch (Exception ex)
        {
            Status.SeedState = "error";
            Status.SeedMessage = ex.Message;
        }
        finally
        {
            _seedGate.Release();
        }
    }

    /// <summary>
    /// Clears out the engine directory an earlier build kept beside the index.
    ///
    /// Everything in it is reconstructible — hard links to archive files, and the empty files
    /// MonoTorrent lays down at startup — so this removes rather than moves. Left alone it would
    /// strand tens of thousands of stray files next to the index for good.
    /// </summary>
    private void MigrateEngineDirectory(string current)
    {
        try
        {
            string old = Path.Combine(settings.IndexDir, "torrent-data");
            if (!Directory.Exists(old)) return;
            if (Path.GetFullPath(old).Equals(Path.GetFullPath(current), StringComparison.OrdinalIgnoreCase))
                return;

            int files = Directory.EnumerateFiles(old, "*", SearchOption.AllDirectories).Count();
            Directory.Delete(old, recursive: true);
            Status.Message = $"cleared {files} file(s) from the old torrent working directory";
        }
        catch
        {
            // Cosmetic. A leftover directory costs disk space, never correctness.
        }
    }

    /// <summary>
    /// Makes <paramref name="link"/> a second name for the bytes of <paramref name="existing"/>.
    ///
    /// .NET has no hard-link API — it offers symbolic links, which on Windows need elevation unless
    /// developer mode is on — so this calls the platform. A hard link is the right shape here: no
    /// second copy of the data, and deleting one name never touches the other, which is what keeps
    /// the engine's directory from being able to harm the archive.
    /// </summary>
    private static bool TryHardLink(string existing, string link)
    {
        try
        {
            if (OperatingSystem.IsWindows()) return CreateHardLinkW(link, existing, IntPtr.Zero);
            return LinkUnix(existing, link) == 0;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    // DllImport rather than the source-generated LibraryImport: the generator emits unsafe code and
    // would mean turning AllowUnsafeBlocks on for the whole project to declare two functions. These
    // are called once per file while sharing starts, so the older marshalling costs nothing here.
    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW",
        CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(
        System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName,
                                               IntPtr lpSecurityAttributes);

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "link",
        CharSet = System.Runtime.InteropServices.CharSet.Ansi, SetLastError = true)]
    private static extern int LinkUnix(string oldpath, string newpath);

    /// <summary>
    /// Hard-links every complete archive file into the engine's own directory, and reports how many
    /// and how much. Files already linked are left alone, so this is cheap to call again.
    ///
    /// A part-written file is skipped: its length will not match, and offering a peer bytes that
    /// have not been verified is worse than offering nothing.
    /// </summary>
    private (int Linked, long Bytes) LinkArchiveIntoTorrentData()
    {
        string archive = settings.DataDir;
        string engineDir = EngineDirectory;

        int linked = 0;
        long bytes = 0;

        // Counted per rejection reason. A sweep that links nothing has to be able to say why, and
        // guessing at it from the outside cost a great deal of time.
        int absent = 0, wrongSize = 0, failed = 0;

        foreach (var (relPath, file) in _byArchivePath)
        {
            string source = Path.Combine(archive, relPath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(source)) { absent++; continue; }

            var info = new FileInfo(source);
            if (info.Length != file.Length) { wrongSize++; continue; }

            string target = Path.Combine(engineDir, relPath.Replace('/', Path.DirectorySeparatorChar));

            try
            {
                // Anything already there is replaced rather than trusted. A file of the right length
                // is not proof of the right file, and treating it as one hid a real fault: with the
                // archive on another drive every link failed, while leftovers from an earlier run
                // made the sweep report 77 files shared when it had linked none of them.
                if (File.Exists(target)) File.Delete(target);

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (!TryHardLink(source, target)) { failed++; continue; }

                linked++;
                bytes += info.Length;
            }
            catch
            {
                // One file that cannot be linked costs its own sharing, not the whole sweep.
                failed++;
            }
        }

        // Written for whoever is reading the button, not for whoever was debugging the sweep. The
        // normal result is "most of the archive is not on this disk", which is not news and should
        // not be reported as though it were: absent is only interesting when nothing linked at all.
        // The counts that mean something went wrong are still said, and only then.
        var trouble = new List<string>();
        if (wrongSize > 0) trouble.Add($"{wrongSize} the wrong size");
        if (failed > 0) trouble.Add($"{failed} could not be linked");
        if (linked == 0 && absent > 0) trouble.Add($"none of the {_byArchivePath.Count} are on this disk");

        Status.SeedMessage = trouble.Count > 0
            ? $"linked {linked} file(s) — {string.Join(", ", trouble)}"
            : $"linked {linked} file(s)";

        return (linked, bytes);
    }

    /// <summary>
    /// Takes files downloaded since sharing started into the share, without a restart.
    ///
    /// This is the whole point of sharing being on by default: someone who has just pulled a depot
    /// through the mirrors is, at that moment, the only new source of it in the swarm. Waiting for
    /// them to restart the app before they can pass it on wastes exactly the moment when they are
    /// most useful, and the swarm is small enough that every additional source counts.
    ///
    /// Cheap when nothing is new: linking skips what is already linked, and the hash check — the
    /// expensive part — only runs when the share actually grew.
    /// </summary>
    public async Task RefreshSharingAsync(CancellationToken ct = default)
    {
        if (!settings.TorrentEnabled || !settings.SeedDownloaded) return;
        if (_manager is null) return;

        await _seedGate.WaitAsync(ct);
        try
        {
            int before = Status.SeedFiles;
            var (linked, bytes) = LinkArchiveIntoTorrentData();

            Status.SeedFiles = linked;
            Status.SeedBytes = bytes;

            if (linked <= before)
            {
                Status.SeedMessage = $"sharing {linked} file(s)";
                return;
            }

            if (DownloadInFlight)
            {
                // The new files are not going anywhere. Taking them in costs a stop and a hash
                // check, and doing that now would kill the download that is still running.
                _seedRefreshPending = true;
                return;
            }

            Status.SeedMessage = $"taking {linked - before} new file(s) into the share";

            // Nothing may be requested while this happens; the check is only there to notice what
            // arrived.
            _requester?.SelectNone();

            if (_manager.State != TorrentState.Stopped)
                await _manager.StopAsync(TimeSpan.FromSeconds(10));

            await _manager.HashCheckAsync(autoStart: true);

            _seedManager = _manager;
            Status.SeedState = "sharing";
            Status.SeedMessage = $"sharing {linked} file(s)";
        }
        catch (Exception ex)
        {
            Status.SeedMessage = $"could not take new files into the share: {ex.Message}";
        }
        finally
        {
            _seedGate.Release();
        }
    }

    public async Task StopSeedingAsync()
    {
        // Said first, so that a start still working its way through the stages sees it and gives
        // up. Without it the refusal reached only a start that had already finished — the manager
        // below is not published until the last line of one — and a start still running carried on
        // to the end and announced itself as sharing over the top of the refusal.
        _seedWanted = false;
        _seedRefreshPending = false;

        // Status is cleared first and unconditionally. There may be no manager yet — sharing spends
        // its first stretch inside EnsureStartedAsync — and returning early there used to leave the
        // display insisting it was still starting long after it had been switched off.
        // The same manager the download path uses, so it is released rather than stopped: pulling
        // it down here would take the downloader with it.
        var manager = _seedManager;
        _seedManager = null;

        Status.SeedState = "off";
        Status.SeedMessage = "";
        Status.SeedFiles = 0;
        Status.SeedBytes = 0;
        Status.SeedUploadRate = 0;
        Status.SeedPeers = 0;

        if (manager is null) return;

        try { await manager.StopAsync(); } catch { /* going away regardless */ }
    }

    /// <summary>
    /// Pushes the current speed caps into a running engine.
    ///
    /// Rate limits are the one setting people reach for while something is happening — the upload
    /// is in the way of a call, or a download is taking the whole line — so requiring a restart to
    /// apply them would miss the moment they are needed. Nothing to do if the engine is not up:
    /// the caps are read again when it starts.
    /// </summary>
    public async Task ApplyRateLimitsAsync()
    {
        var engine = _engine;
        if (engine is null) return;

        try
        {
            var builder = new EngineSettingsBuilder(engine.Settings)
            {
                MaximumUploadRate = settings.TorrentUploadKbps * 1000,
                MaximumDownloadRate = settings.TorrentDownloadKbps * 1000,
            };

            await engine.UpdateSettingsAsync(builder.ToSettings());
        }
        catch
        {
            // The caps are advisory. Failing to tighten one is not a reason to disturb whatever the
            // engine is doing, and the new value still takes effect on the next start.
        }
    }

    public void SampleSeed()
    {
        var m = _seedManager;
        if (m is null) return;

        Status.SeedUploadRate = m.Monitor.UploadRate;
        Status.SeedUploaded = m.Monitor.DataBytesSent;
        Status.SeedPeers = m.Peers.Seeds + m.Peers.Leechs;
    }

    public async Task<bool> EnsureStartedAsync(CancellationToken ct = default)
    {
        // The single place the engine can come up, so the switch belongs here rather than at each
        // call site where one could be missed.
        if (!settings.TorrentEnabled)
        {
            Status.State = "off";
            Status.Message = "the torrent engine is switched off in Settings";
            return false;
        }

        if (Ready) return true;

        await _gate.WaitAsync(ct);
        try
        {
            if (Ready) return true;

            Status.State = "starting";
            Status.Error = null;
            Status.Message = "starting the torrent engine";

            string cacheDir = Path.Combine(settings.IndexDir, "torrent");
            Directory.CreateDirectory(cacheDir);

            if (_engine is null)
            {
                var builder = new EngineSettingsBuilder
                {
                    CacheDirectory = cacheDir,
                    AllowPortForwarding = true,
                    AllowLocalPeerDiscovery = true,

                    // The file list is several megabytes; caching it turns later starts instant.
                    AutoSaveLoadMagnetLinkMetadata = true,
                    AutoSaveLoadFastResume = true,
                    AutoSaveLoadDhtCache = true,
                    MaximumConnections = 200,

                    // Zero is MonoTorrent's own "no limit", which is also this app's default, so
                    // the unlimited case needs no special handling here.
                    MaximumUploadRate = settings.TorrentUploadKbps * 1000,
                    MaximumDownloadRate = settings.TorrentDownloadKbps * 1000,
                };

                if (settings.TorrentPort > 0)
                {
                    builder.ListenEndPoints = new()
                    {
                        ["ipv4"] = new IPEndPoint(IPAddress.Any, settings.TorrentPort),
                        ["ipv6"] = new IPEndPoint(IPAddress.IPv6Any, settings.TorrentPort),
                    };
                    builder.DhtEndPoint = new IPEndPoint(IPAddress.Any, settings.TorrentPort);
                }

                // The disk layer is wrapped so that reading a file this machine does not have stops
                // at the wrapper instead of creating an empty one on the way down. See
                // ArchivePieceWriter: without it the startup hash check left about thirty thousand
                // placeholders behind, one for every file in the torrent it looked for and missed.
                var factories = Factories.Default
                    .WithPieceWriterCreator(maxOpenFiles =>
                        new ArchivePieceWriter(Factories.Default.CreatePieceWriter(maxOpenFiles)));

                _engine = new ClientEngine(builder.ToSettings(), factories);
            }

            if (_manager is null)
            {
                string? torrentPath = FindTorrentFile();

                // A directory of its own, never the archive itself: the engine allocates files for
                // whatever it is given, and the archive must only ever hold files this app has
                // verified — mixing the two once made 35 166 empty placeholders look like
                // completed downloads.
                string dataDir = EngineDirectory;
                Directory.CreateDirectory(dataDir);
                MigrateEngineDirectory(dataDir);

                var torrentSettings = new TorrentSettingsBuilder
                {
                    CreateContainingDirectory = false,
                    AllowDht = true,
                    AllowPeerExchange = true,
                }.ToSettings();

                if (torrentPath is not null)
                {
                    // Straight from the file: no metadata round trip, and its 88 trackers come with
                    // it. The infohash is the torrent's own, so it joins exactly the same swarm.
                    var loaded = await Torrent.LoadAsync(torrentPath);
                    _manager = await _engine.AddAsync(loaded, dataDir, torrentSettings);
                    Status.Message = $"file list read from {Path.GetFileName(torrentPath)}";

                    // The file list came with the file, so the picker can be in place before the
                    // manager has ever run and there is no window in which it could ask for
                    // anything. The magnet path has to wait for metadata to know the files at all.
                    await AttachRequesterAsync();
                }
                else
                {
                    _manager = await _engine.AddAsync(BuildLink(), dataDir, torrentSettings);
                }

                // Deliberately not awaited. Adding ninety trackers one at a time means ninety DNS
                // lookups and announces, most of them to hosts that will never answer, and doing
                // that before the manager starts held the whole engine at "starting" indefinitely.
                // Extra trackers are an improvement to reach for, never a precondition.
                var manager = _manager;
                _ = Task.Run(async () =>
                {
                    foreach (var url in settings.TrackersToUse)
                    {
                        try { await manager.TrackerManager.AddTrackerAsync(new Uri(url)); }
                        catch { /* one unusable url is not worth the rest of the list */ }
                    }
                });

                _manager.PeersFound += (_, _) => Sample();
                _manager.TorrentStateChanged += (_, _) => Sample();
            }

            await _manager.StartAsync();

            if (!_manager.HasMetadata)
            {
                Status.State = "metadata";
                Status.Message = "asking the swarm for the file list — this can take a few minutes";

                // Without a deadline this waits forever when no peer ever answers, and the only
                // symptom the user sees is a spinner. Bounded, it can say what actually happened.
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(MetadataTimeout);

                try
                {
                    await _manager.WaitForMetadataAsync(deadline.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    Status.State = "error";
                    Status.Error =
                        $"no peer supplied the file list within {MetadataTimeout.TotalMinutes:0} minutes " +
                        $"({_manager.OpenConnections} connections, {_manager.Peers.Available} peers known). " +
                        "The trackers in the magnet may be down or blocked; an HTTP mirror still works.";
                    Status.Message = Status.Error;
                    return false;
                }
            }

            // Stop before doing anything else. A running manager with everything at default
            // priority downloads the entire 13.32 TB — it had already pulled 38 GB before this
            // was caught. Nothing may transfer until a chain explicitly selects files.
            await _manager.StopAsync(TimeSpan.FromSeconds(10));

            MapFiles();

            // Only reached without a picker on the magnet path, where the file list did not exist
            // until now.
            if (_requester is null) await AttachRequesterAsync();

            Status.HasMetadata = true;
            Status.State = "ready";
            Status.Message = $"{Status.TotalFiles} files in the torrent, none selected";
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Status.State = "off";
            Status.Message = "cancelled";
            return false;
        }
        catch (Exception ex)
        {
            Status.State = "error";
            Status.Error = ex.Message;
            Status.Message = ex.Message;
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The published magnet plus any extra trackers from settings. The magnet's own three all
    /// resolve to a single address, so on a network that blocks it there is nothing to announce to
    /// and only DHT is left; extra trackers give the swarm another way in.
    /// </summary>
    /// <summary>
    /// The metadata file, if it is anywhere we can see it.
    ///
    /// Fetching 30 MB of file list from a swarm with three seeders takes minutes and often never
    /// finishes at all, which left sharing stuck at "reading the file list". Having the file on
    /// disk removes that entirely: the torrent is known the moment the app starts, and its own
    /// announce-list carries far more trackers than any list kept by hand.
    /// </summary>
    private string? FindTorrentFile()
    {
        string exeDir = AppContext.BaseDirectory;

        var candidates = new List<string>
        {
            Path.Combine(exeDir, TorrentFileName),
            Path.Combine(settings.IndexDir, TorrentFileName),
            Path.Combine(Directory.GetCurrentDirectory(), TorrentFileName),
        };

        // Running from a build output during development, the repository root is several levels up.
        var dir = new DirectoryInfo(exeDir);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
            candidates.Add(Path.Combine(dir.FullName, TorrentFileName));

        return candidates.FirstOrDefault(File.Exists);
    }

    public const string TorrentFileName = "steam2.torrent";

    private MagnetLink BuildLink()
    {
        var baseLink = MagnetLink.Parse(Magnet);

        var announce = new List<string>(baseLink.AnnounceUrls);
        foreach (var extra in settings.TrackersToUse)
        {
            var url = extra?.Trim();
            if (string.IsNullOrEmpty(url)) continue;
            if (!announce.Contains(url, StringComparer.OrdinalIgnoreCase)) announce.Add(url);
        }

        Status.Trackers = announce.Count;

        return new MagnetLink(
            baseLink.InfoHashes,
            baseLink.Name,
            announce,
            baseLink.Webseeds,
            baseLink.Size);
    }

    /// <summary>
    /// Indexes the torrent by archive-relative path, which is how a chain names the files it is
    /// after.
    /// </summary>
    private void MapFiles()
    {
        var manager = _manager!;
        _byArchivePath.Clear();

        foreach (var file in manager.Files)
        {
            // Torrent paths may or may not carry a leading "steam2/" container; the tail is what
            // matters, and only dats/ and blobs/ entries are of interest.
            var parts = file.Path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;

            string folder = parts[^2];
            string name = parts[^1];
            if (!folder.Equals("dats", StringComparison.OrdinalIgnoreCase) &&
                !folder.Equals("blobs", StringComparison.OrdinalIgnoreCase)) continue;

            _byArchivePath[$"{folder.ToLowerInvariant()}/{name}"] = file;
        }

        Status.TotalFiles = _byArchivePath.Count;
    }

    /// <summary>
    /// Hands the manager the selection picker, selecting nothing. Can only be done once the file
    /// list is known and while the manager is stopped, which MonoTorrent enforces.
    /// </summary>
    private async Task AttachRequesterAsync()
    {
        _requester = new SelectionPieceRequester(_manager!.Files);
        await _manager.ChangePickerAsync(_requester);
    }

    // ---------------- downloading ----------------

    /// <summary>
    /// Fetches exactly the given files from the swarm. Returns the entries it could not find in the
    /// torrent, which the caller should fall back to HTTP for.
    /// </summary>
    /// <summary>
    /// A file's length straight out of the torrent metadata, or null if the torrent has no such
    /// file or has not been read yet. No request is made; the file list carries every length.
    /// </summary>
    public long? TryGetLength(string relPath)
        => _byArchivePath.TryGetValue(relPath, out var f) ? f.Length : null;

    /// <summary>
    /// One file's bytes, from the archive if it is already there and from the swarm if not.
    ///
    /// This is what the planner, the version history and the depot namer used to get over HTTP.
    /// With the mirrors gone the swarm is the only place left to ask, and they ask for blobs, which
    /// are tens of kilobytes — small enough that fetching one is a reasonable thing to wait for.
    ///
    /// A file already on disk is read from disk. That matters more than it looks: resolving a chain
    /// walks a depot's whole blob history, and after the first pass almost all of it is local.
    /// </summary>
    public async Task<byte[]> GetBytesAsync(Entry entry, CancellationToken ct = default)
    {
        string local = Path.Combine(settings.DataDir, entry.DirName, entry.FileName);
        if (File.Exists(local)) return await File.ReadAllBytesAsync(local, ct);

        var missing = await DownloadAsync([entry], null, ct);
        if (missing.Count > 0 || !File.Exists(local))
            throw new IOException($"the swarm did not supply {entry.FileName}");

        return await File.ReadAllBytesAsync(local, ct);
    }

    public async Task<List<Entry>> DownloadAsync(
        IReadOnlyList<Entry> wanted,
        Action<long, long, double>? onProgress,
        CancellationToken ct)
    {
        if (!await EnsureStartedAsync(ct))
            throw new InvalidOperationException(Status.Error ?? "the torrent source is not available");

        var manager = _manager!;
        var requester = _requester!;
        var missing = new List<Entry>();
        var selected = new List<(Entry Entry, ITorrentManagerFile File)>();

        foreach (var entry in wanted)
        {
            if (_byArchivePath.TryGetValue(entry.RelPath, out var file))
                selected.Add((entry, file));
            else
                missing.Add(entry);
        }

        Status.SelectedFiles = selected.Count;
        Status.SelectedBytes = selected.Sum(x => x.File.Length);

        if (selected.Count == 0) return missing;

        _selection = selected.Select(x => x.File).ToList();
        requester.Select(_selection);

        Status.State = "downloading";
        Status.Message = $"{selected.Count} files selected from the swarm";

        try
        {
            await manager.StartAsync();

            // Every piece of every selected file, which is all the picker will ever ask for.
            while (!SelectionComplete())
            {
                ct.ThrowIfCancellationRequested();
                Sample();

                long done = (long)(Status.SelectedBytes * Status.SelectedProgress / 100.0);
                onProgress?.Invoke(done, Status.SelectedBytes, manager.Monitor.DownloadRate);

                await Task.Delay(1000, ct);
            }

            Sample();
            onProgress?.Invoke(Status.SelectedBytes, Status.SelectedBytes, 0);
        }
        finally
        {
            // Stop as soon as the selection is in; leaving it running would start on everything else.
            await manager.StopAsync(TimeSpan.FromSeconds(10));
            requester.SelectNone();
            _selection = Array.Empty<ITorrentManagerFile>();
        }

        // Hand the results to the archive, where the rest of the app expects to find them.
        foreach (var (entry, file) in selected)
        {
            if (!await PublishAsync(entry, file, ct)) missing.Add(entry);
        }

        Status.State = "ready";
        Status.Message = $"finished {selected.Count - missing.Count} of {selected.Count} files";

        // The manager is free again, so anything sharing put off while it was busy runs now.
        _ = RunPendingSeedRefreshAsync();

        return missing;
    }

    /// <summary>
    /// Does the sharing work that was deferred while a download held the manager.
    ///
    /// Which of the two it is depends on how far sharing had got: one that never finished starting
    /// has to start, and one already sharing only needs to notice what arrived.
    /// </summary>
    private async Task RunPendingSeedRefreshAsync()
    {
        if (!_seedRefreshPending) return;
        _seedRefreshPending = false;

        if (!settings.TorrentEnabled || !settings.SeedDownloaded) return;

        try
        {
            if (Status.SeedState == "sharing") await RefreshSharingAsync(CancellationToken.None);
            else await StartSeedingAsync();
        }
        catch
        {
            // Sharing failing to catch up is not the download's problem, and the next completed
            // download tries again.
        }
    }

    /// <summary>
    /// Moves one finished file out of the engine's directory into the archive, checking it against
    /// the sha256 carried in its own name first. Returns false when it is not usable, so the caller
    /// can fall back to HTTP for it.
    /// </summary>
    private async Task<bool> PublishAsync(Entry entry, ITorrentManagerFile file, CancellationToken ct)
    {
        try
        {
            string source = file.FullPath;
            if (!File.Exists(source) || new FileInfo(source).Length != file.Length) return false;

            // The sha256 is the fourth part of the file's own name, so the swarm's copy gets the
            // same check an HTTP download would get.
            if (!await ArchiveClient.VerifyAsync(source, entry.Sha, ct)) return false;

            string dest = Path.Combine(settings.DataDir, entry.DirName, entry.FileName);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

            // Moved rather than copied: keeping both would double the disk cost of every chain.
            File.Move(source, dest, overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void Sample()
    {
        var manager = _manager;
        if (manager is null) return;

        Status.Peers = manager.OpenConnections;
        Status.Seeds = manager.Peers.Seeds;
        Status.DownloadRate = manager.Monitor.DownloadRate;
        Status.UploadRate = manager.Monitor.UploadRate;
        Status.SelectedProgress = SelectionProgress();
        Status.TorrentState = manager.State.ToString();

        // Counted from the manager rather than set once while building a magnet link, which left it
        // reading zero for every start that loaded the torrent from a file — the usual path.
        try { Status.Trackers = manager.TrackerManager.Tiers.Sum(t => t.Trackers.Count); }
        catch { /* a count is not worth disturbing a sample for */ }
    }

    /// <summary>
    /// How far the selection has got, weighted by file size. Each file carries its own bitfield of
    /// the pieces it spans, which is where the manager records what has arrived.
    /// </summary>
    private double SelectionProgress()
    {
        long total = 0;
        double done = 0;

        foreach (var file in _selection)
        {
            var pieces = file.BitField;
            total += file.Length;
            if (pieces.Length > 0) done += file.Length * ((double)pieces.TrueCount / pieces.Length);
        }

        return total == 0 ? 0 : done * 100.0 / total;
    }

    /// <summary>
    /// True once every piece of every selected file is in. Read from the bitfields rather than the
    /// percentage, which would leave the loop at the mercy of a rounding error.
    /// </summary>
    private bool SelectionComplete()
    {
        foreach (var file in _selection)
            if (!file.BitField.AllTrue) return false;

        return true;
    }

    public async Task StopAsync()
    {
        try
        {
            if (_manager is not null) await _manager.StopAsync(TimeSpan.FromSeconds(5));
            Status.State = "off";
            Status.Message = "stopped";
        }
        catch (Exception ex)
        {
            Status.Error = ex.Message;
        }
    }

    public async Task ResetAsync()
    {
        // Deliberately not an unbounded wait. The gate is held for the whole of EnsureStartedAsync,
        // and the case this has to handle is precisely that a start has hung inside it — waiting
        // politely would mean the shutdown could never run. Disposing the engine underneath a stuck
        // start is what releases it.
        bool held = await _gate.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            if (_manager is not null)
                await _manager.StopAsync(TimeSpan.FromSeconds(5));

            _engine?.Dispose();
            _engine = null;
            _manager = null;
            _requester = null;
            _selection = Array.Empty<ITorrentManagerFile>();
            _byArchivePath.Clear();

            Status.State = "off";
            Status.Message = settings.TorrentPort > 0
                ? $"torrent engine reset; next start will listen on port {settings.TorrentPort}"
                : "torrent engine reset; next start will choose a random port";
            Status.Error = null;
            Status.HasMetadata = false;
            Status.TotalFiles = 0;
            Status.SelectedFiles = 0;
            Status.SelectedBytes = 0;
            Status.SelectedProgress = 0;
            Status.Peers = 0;
            Status.Seeds = 0;
            Status.DownloadRate = 0;
            Status.UploadRate = 0;
            Status.TorrentState = "";
        }
        finally
        {
            if (held) _gate.Release();
        }
    }
}
