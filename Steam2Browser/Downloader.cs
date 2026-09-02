using System.Collections.Concurrent;
using System.Diagnostics;

namespace Steam2Browser;

public sealed class FileProgress
{
    public required string Name { get; init; }
    public long Done;
    public long Total;
    public string State = "running"; // running | done | failed | skipped
    public string? Error;
}

public sealed class DownloadJob
{
    public required string Id { get; init; }
    public int Depot;
    public int Version;
    public string? BlobCrc;
    public string Mode = "direct";
    public string ExtractArgs = "";

    /// <summary>Set when the download was asked for the whole chain, optimiser off.</summary>
    public bool FullChain;

    public string Status = "queued"; // queued | running | done | failed | cancelled
    public string? Error;

    public int TotalFiles;
    public int DoneFiles;
    public int SkippedFiles;
    public int FailedFiles;

    /// <summary>What the swarm contributed while the mirror worked the other end of the list.</summary>
    public int SwarmFiles;

    public long SwarmBytes;
    public double SwarmSeconds;
    public int SwarmSkipped;

    /// <summary>Files the swarm has already tried, so it never picks the same one twice.</summary>
    public readonly HashSet<string> SwarmDone = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Stops the swarm helper without stopping the job it is helping.</summary>
    public CancellationTokenSource SwarmStop = new();

    public long TotalBytes;
    public long DoneBytes;
    public double SpeedBps;

    public DateTime StartedUtc = DateTime.UtcNow;
    public DateTime? FinishedUtc;

    public readonly ConcurrentDictionary<string, FileProgress> Active = new();
    public readonly ConcurrentQueue<string> Log = new();

    internal CancellationTokenSource Cts = new();
    internal List<PlanFile> Files = new();

    public void Say(string message)
    {
        Log.Enqueue($"{DateTime.Now:HH:mm:ss}  {message}");
        while (Log.Count > 400) Log.TryDequeue(out _);
    }
}

public sealed class DownloadManager(ArchiveClient client, Settings settings, TorrentSource torrent, ChangeIndex changes)
{
    private readonly ConcurrentDictionary<string, DownloadJob> _jobs = new();
    private int _seq;

    public IReadOnlyCollection<DownloadJob> Jobs => _jobs.Values.ToArray();

    public DownloadJob? Get(string id) => _jobs.GetValueOrDefault(id);

    public DownloadJob Start(ChainPlan plan)
    {
        var job = new DownloadJob
        {
            Id = $"job{Interlocked.Increment(ref _seq)}",
            Depot = plan.Depot,
            Version = plan.TargetVersion,
            BlobCrc = plan.BlobCrc,
            Mode = plan.Mode,
            ExtractArgs = plan.ExtractArgs,
            FullChain = plan.FullChain,
            Files = plan.Files,
            TotalFiles = plan.Files.Count,
            TotalBytes = plan.TotalBytes,
        };
        _jobs[job.Id] = job;

        _ = Task.Run(() => RunAsync(job));
        return job;
    }

    public void Cancel(string id)
    {
        if (_jobs.TryGetValue(id, out var job)) job.Cts.Cancel();
    }

    public void Clear()
    {
        foreach (var kv in _jobs)
            if (kv.Value.Status is "done" or "failed" or "cancelled")
                _jobs.TryRemove(kv.Key, out _);
    }

    private async Task RunAsync(DownloadJob job)
    {
        job.Status = "running";
        job.Say($"depot {job.Depot} version {job.Version} — {job.TotalFiles} files, mode {job.Mode}");

        // The last line of defence on free space, and the only one that covers every caller: depot
        // downloads, app packs, and anything that reaches the manager without passing the endpoint
        // that checks first. A pack runs its depots one after another over hours, so the disk can
        // be fine when it starts and full by the third — each job asks again for itself.
        //
        // A drive that cannot be measured is treated as having room. Refusing to download because
        // the free space could not be read would turn a reporting failure into a broken app.
        long needed = job.Files
            .Where(f => !File.Exists(Path.Combine(settings.DataDir, f.Entry.DirName, f.Entry.FileName)))
            .Sum(f => f.Size);

        if (!Disk.Fits(settings.DataDir, needed, out var space))
        {
            job.Status = "failed";
            job.Error = $"not enough free space on {space.Root}";
            job.Say($"failed: {job.Error} — {Mb(needed)} to download plus {Mb(Disk.Headroom)} kept "
                  + $"free, but only {Mb(space.FreeBytes)} is available");
            job.FinishedUtc = DateTime.UtcNow;
            return;
        }

        var ct = job.Cts.Token;
        using var sampler = StartSpeedSampler(job, ct);
        using var gate = new SemaphoreSlim(Math.Max(1, settings.Concurrency));

        // Blobs first: the extractor reads them to resolve the chain, and they are tiny.
        var ordered = job.Files
            .OrderBy(f => f.Entry.Kind == Kind.Blob ? 0 : 1)
            .ThenBy(f => f.Entry.Version)
            .ToList();

        // The swarm works alongside the mirror rather than instead of it: the mirror takes the list
        // from the front, the swarm from the back, and they meet in the middle. Every file the swarm
        // supplies is a file the mirror is not asked for, which is the point — one person pays for
        // those three servers and asked people to stop hammering them.
        //
        // It cannot start here, though. Which dats are needed at all is only known once the blobs
        // are on disk, and starting the swarm on the unpruned list had it fetching versions the
        // planner had already ruled out: on depot 407 that was 15 MB pulled and filed away that
        // nothing reads. Blobs are left to HTTP regardless — they are kilobytes, and selecting one
        // in the torrent costs more than fetching it.
        Task? swarm = null;

        try
        {
            // Picking the swarm as the mirror means the whole selection comes from it, and only what
            // it cannot supply falls back to HTTP.
            //
            // Inside the try, because it is the one part of a download that can fail before any
            // request is made: with the engine switched off it threw, the throw missed the handler
            // that marks a job failed, and the job sat at "waiting for the torrent file list" for
            // as long as the app stayed open.
            if (client.Primary.IsTorrent)
            {
                // The engine can be off while the swarm is still the chosen mirror — the two are
                // separate settings. HTTP is what that choice degrades to; failing every download
                // until they notice the mirror box is not a reasonable answer.
                if (!settings.TorrentEnabled)
                {
                    job.Say("the torrent engine is off in Settings — downloading from the mirror instead");
                }
                else
                {
                    ordered = await ViaTorrentAsync(job, ordered, ct);
                    if (ordered.Count == 0)
                    {
                        Finish(job);
                        return;
                    }
                    job.Say($"{ordered.Count} file(s) are not in the torrent — fetching those over HTTP");
                }
            }

            if (settings.PhasedDownloads)
            {
                // Two phases, each with its own stream count. Blobs are kilobytes, so latency
                // dominates and many at once is free. Dats are large and these mirrors speed a
                // connection up the longer it keeps asking, so only a couple of streams are used —
                // more of them, or one file split into ranges, all sit at the cold starting rate.
                var blobs = ordered.Where(f => f.Entry.Kind == Kind.Blob).ToList();
                var dats = ordered.Where(f => f.Entry.Kind == Kind.Dat).ToList();

                int blobStreams = Math.Max(1, settings.BlobConcurrency);
                int datStreams = Math.Max(1, settings.DatConcurrency);

                job.Say($"phased: {blobs.Count} blob(s) at {blobStreams} stream(s), " +
                        $"then {dats.Count} dat(s) at {datStreams} stream(s)");

                // Only the dat phase warms ahead. Blobs are kilobytes and already run 32 at a
                // time, so there is nothing to hide the latency of.
                await OnePhaseAsync(job, blobs, "blobs", blobStreams, warmAhead: false, ct);

                // Every blob is on disk now, which is the first moment the question can be answered:
                // a dat whose every written file was overwritten again further up the chain holds
                // nothing this version reads. For a depot where one binary churns and the rest sits
                // still, that is almost the entire chain.
                dats = PruneDats(job, blobs, dats);

                // Now the list is the real one, the swarm can take its end of it.
                swarm = StartSwarmTail(job, dats, ct);

                // Large dats go last and alone. Two concurrent long sequential reads make
                // disk-backed storage seek between them; small files finish before that bites,
                // so they keep the configured parallelism.
                long bigFrom = settings.BigFileBytes;
                if (bigFrom > 0)
                {
                    // An unknown size is treated as large: better a slower download than a
                    // multi-gigabyte file competing with another one.
                    var small = dats.Where(f => f.Size >= 0 && f.Size < bigFrom).ToList();
                    var big = dats.Where(f => f.Size < 0 || f.Size >= bigFrom).ToList();

                    if (big.Count > 0)
                        job.Say($"{big.Count} dat(s) at or above {bigFrom / 1_000_000} MB " +
                                $"will be fetched one at a time");

                    await OnePhaseAsync(job, small, "small dats", datStreams, warmAhead: true, ct);
                    await OnePhaseAsync(job, big, "large dats", 1, warmAhead: true, ct);
                }
                else
                {
                    await OnePhaseAsync(job, dats, "dats", datStreams, warmAhead: true, ct);
                }
            }
            else
            {
                await Task.WhenAll(ordered.Select(async pf =>
                {
                    await gate.WaitAsync(ct);
                    try { await OneFileAsync(job, pf, ct); }
                    finally { gate.Release(); }
                }));
            }

            if (swarm is not null)
            {
                // It is a helper, never a dependency: whatever it has not finished by now, the
                // mirror has already fetched from the front.
                job.SwarmStop.Cancel();
                try { await swarm; } catch { /* its own log said what happened */ }
            }

            Finish(job);

            // Whoever just downloaded this is now a source for it. Offering it back straight away is
            // the difference between a swarm that grows with its users and one that does not.
            //
            // The conditions are checked here as well as inside RefreshSharingAsync, which would
            // return on its own: without that the log announced files being offered to the swarm on
            // installs where sharing is switched off entirely.
            if (job.DoneFiles > 0 && settings.TorrentEnabled && settings.SeedDownloaded)
            {
                job.Say("offering the new files back to the swarm");
                _ = torrent.RefreshSharingAsync(CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
            job.Status = "cancelled";
            job.Say("cancelled");
        }
        catch (Exception ex)
        {
            job.Status = "failed";
            job.Error = ex.Message;
            job.Say($"failed: {ex.Message}");
        }
        finally
        {
            job.FinishedUtc = DateTime.UtcNow;
            job.SpeedBps = 0;
            job.Active.Clear();
        }
    }

    /// <summary>Runs one kind of file to completion before the caller moves on to the next phase.</summary>
    /// <summary>
    /// Drops the dats no file in the target version resolves to. Answered from the blobs just
    /// downloaded, so it costs nothing; returns the list untouched whenever the answer cannot be
    /// established, because skipping a dat that is genuinely needed breaks the extraction.
    /// </summary>
    private List<PlanFile> PruneDats(DownloadJob job, List<PlanFile> blobs, List<PlanFile> dats)
    {
        if (job.FullChain)
        {
            job.Say($"optimiser off — fetching all {dats.Count} dat(s) in the chain");
            return dats;
        }

        var chainBlobs = blobs.Select(f => f.Entry).ToList();

        var target = chainBlobs
            .Where(b => b.Version == job.Version)
            .FirstOrDefault(b => job.BlobCrc is null
                                 || b.CrcHex.Equals(job.BlobCrc, StringComparison.OrdinalIgnoreCase));

        if (target is null) return dats;

        var needed = changes.NeededDatVersions(chainBlobs, target);
        if (needed is null) return dats;

        var keep = needed.ToHashSet();
        var kept = dats.Where(f => keep.Contains(f.Entry.Version)).ToList();
        int dropped = dats.Count - kept.Count;

        if (dropped == 0) return dats;

        long saved = dats.Where(f => !keep.Contains(f.Entry.Version)).Sum(f => Math.Max(0, f.Size));
        job.Say($"{dropped} of {dats.Count} dat(s) hold nothing version {job.Version} reads — "
                + $"skipping them saves {saved / 1_000_000} MB");

        job.TotalFiles -= dropped;
        return kept;
    }

    /// <summary>
    /// Pulls dats from the end of the list out of the swarm while the mirror works the front.
    ///
    /// Nothing here is allowed to slow the download down. The two never coordinate over who owns a
    /// file: the mirror simply downloads whatever it reaches, and if that is a file the swarm is
    /// still working on, the mirror's copy lands and the swarm's is thrown away when it notices the
    /// file is already there. A swarm too slow to finish first therefore costs nothing but its own
    /// wasted traffic, which is the right trade when it has three seeders and the mirrors have
    /// megabytes a second.
    ///
    /// The reverse also holds. If the swarm turns out to be the faster side, it is given the front
    /// of the list and the mirror the back — see <see cref="SwarmIsFaster"/>.
    /// </summary>
    private Task? StartSwarmTail(DownloadJob job, List<PlanFile> ordered, CancellationToken ct)
    {
        if (!settings.TorrentEnabled || !settings.SwarmAssist) return null;
        if (client.Primary.IsTorrent) return null;

        var dats = ordered.Where(f => f.Entry.Kind == Kind.Dat).ToList();
        if (dats.Count < 2) return null;

        job.SwarmStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var stop = job.SwarmStop.Token;

        return Task.Run(async () =>
        {
            try
            {
                if (!await torrent.EnsureStartedAsync(stop)) return;

                job.Say($"swarm helping from the end of {dats.Count} dat(s)");

                while (!stop.IsCancellationRequested)
                {
                    var next = NextForSwarm(job, dats);
                    if (next is null) break;

                    long before = job.SwarmBytes;
                    var clock = System.Diagnostics.Stopwatch.StartNew();

                    var missing = await torrent.DownloadAsync(
                        [next.Entry],
                        (done, _, _) => { },
                        stop);

                    // Judged by the file on disk, not by what the call returned. The swarm reports
                    // as "missing" anything it did not itself deliver, including a file that turned
                    // up because the mirror reached it first — so trusting that return value left
                    // the tally at zero while files were plainly arriving.
                    string path = Path.Combine(settings.DataDir, next.Entry.DirName, next.Entry.FileName);
                    bool arrived = File.Exists(path)
                                   && (next.Size < 0 || new FileInfo(path).Length == next.Size);

                    if (arrived)
                    {
                        job.SwarmFiles++;
                        job.SwarmBytes += Math.Max(0, next.Size);
                        job.SwarmSeconds += clock.Elapsed.TotalSeconds;
                    }
                    else
                    {
                        // Not in the swarm at all, or it could not finish. Leave it to the mirror.
                        job.SwarmSkipped++;
                    }

                    job.SwarmDone.Add(next.Entry.RelPath);
                }

                job.Say(job.SwarmFiles > 0
                    ? $"swarm supplied {job.SwarmFiles} dat(s), {job.SwarmBytes / 1_000_000} MB"
                    : $"swarm supplied nothing ({job.SwarmSkipped} attempt(s) went to the mirror)");
            }
            catch (OperationCanceledException)
            {
                // Expected: the mirror finished and stopped us.
            }
            catch (Exception ex)
            {
                job.Say($"swarm helper stopped: {ex.Message}");
            }
        }, CancellationToken.None);
    }

    /// <summary>
    /// The next file for the swarm: from the back of the list normally, from the front once the
    /// swarm has shown itself to be the faster of the two. Files already on disk are skipped, which
    /// is how the mirror's progress is noticed without either side reporting to the other.
    /// </summary>
    private PlanFile? NextForSwarm(DownloadJob job, List<PlanFile> dats)
    {
        bool fromFront = SwarmIsFaster(job);

        var order = fromFront ? Enumerable.Range(0, dats.Count) : Enumerable.Range(0, dats.Count).Reverse();

        foreach (int i in order)
        {
            var candidate = dats[i];
            if (job.SwarmDone.Contains(candidate.Entry.RelPath)) continue;

            string path = Path.Combine(settings.DataDir, candidate.Entry.DirName, candidate.Entry.FileName);
            if (File.Exists(path)) continue;

            return candidate;
        }

        return null;
    }

    /// <summary>
    /// Whether the swarm is out-running the mirror, measured on what each has actually delivered
    /// during this job rather than on any assumption about which ought to be quicker.
    ///
    /// It takes a few files on each side before the comparison means anything, so until then the
    /// swarm stays at the back where being slow costs nothing.
    /// </summary>
    private static bool SwarmIsFaster(DownloadJob job)
    {
        if (job.SwarmFiles < 2 || job.SwarmSeconds <= 0) return false;
        if (job.DoneFiles < 2 || job.SpeedBps <= 0) return false;

        double swarmRate = job.SwarmBytes / job.SwarmSeconds;
        return swarmRate > job.SpeedBps;
    }

    private async Task OnePhaseAsync(
        DownloadJob job, List<PlanFile> files, string what, int streams, bool warmAhead, CancellationToken ct)
    {
        if (files.Count == 0) return;

        int lookahead = warmAhead ? Math.Max(0, settings.WarmupLookahead) : 0;

        job.Say($"{what}: {files.Count} file(s), {streams} stream(s)"
                + (lookahead > 0 ? $", warming {lookahead} ahead" : ""));

        using var gate = new SemaphoreSlim(streams);

        await Task.WhenAll(files.Select(async (pf, index) =>
        {
            await gate.WaitAsync(ct);
            try
            {
                if (lookahead > 0) WarmAhead(files, index + lookahead, ct);
                await OneFileAsync(job, pf, ct);
            }
            finally { gate.Release(); }
        }));
    }

    /// <summary>
    /// Touches an upcoming file so the mirror can have it ready. Deliberately not awaited: the point
    /// is only that the request reaches the mirror, and its outcome never affects this download.
    /// </summary>
    private void WarmAhead(List<PlanFile> files, int index, CancellationToken ct)
    {
        if (index < 0 || index >= files.Count) return;

        var entry = files[index].Entry;
        if (File.Exists(Path.Combine(settings.DataDir, entry.DirName, entry.FileName))) return;

        _ = client.WarmAsync(entry, ct);
    }

    /// <summary>Sizes for the log, in the same MB/GB the rest of the app shows.</summary>
    private static string Mb(long b) => b >= 1_000_000_000L
        ? $"{b / 1_000_000_000d:0.##} GB"
        : $"{b / 1_000_000d:0.##} MB";

    private static void Finish(DownloadJob job)
    {
        job.Status = job.FailedFiles > 0 ? "failed" : "done";
        if (job.FailedFiles > 0) job.Error = $"{job.FailedFiles} file(s) failed";
        job.Say(job.FailedFiles > 0
            ? $"finished with {job.FailedFiles} failure(s)"
            : $"finished — {job.DoneFiles} downloaded, {job.SkippedFiles} already present");
    }

    /// <summary>
    /// Pulls what the swarm has. Returns the files it could not supply, for the HTTP path to pick up.
    /// </summary>
    private async Task<List<PlanFile>> ViaTorrentAsync(DownloadJob job, List<PlanFile> files, CancellationToken ct)
    {
        // Files already on disk need neither source.
        var needed = files
            .Where(f => !File.Exists(Path.Combine(settings.DataDir, f.Entry.DirName, f.Entry.FileName)))
            .ToList();

        job.SkippedFiles += files.Count - needed.Count;
        if (needed.Count == 0) return [];

        job.Say("waiting for the torrent file list");

        var sw = Stopwatch.StartNew();
        long lastLogged = -1;

        var missing = await torrent.DownloadAsync(
            needed.Select(f => f.Entry).ToList(),
            (done, total, rateBps) =>
            {
                Interlocked.Exchange(ref job.DoneBytes, done);

                // The swarm pulls pieces from many files in the selection at once, so there is no
                // single "current file" to show progress for the way the mirror path can — this keeps the
                // console showing that something is actually happening instead of sitting on the
                // one-off "waiting for the torrent file list" line for the whole download.
                if (done != lastLogged && sw.Elapsed.TotalSeconds >= 5)
                {
                    job.Say($"torrent: {Mb(done)} / {Mb(total)}" + (rateBps > 0 ? $" at {Mb((long)rateBps)}/s" : ""));
                    lastLogged = done;
                    sw.Restart();
                }
            },
            ct);

        var missingNames = missing.Select(e => e.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        job.DoneFiles += needed.Count - missingNames.Count;

        return needed.Where(f => missingNames.Contains(f.Entry.FileName)).ToList();
    }

    /// <summary>Samples DoneBytes once a second so the UI has a throughput figure to show.</summary>
    private static Timer StartSpeedSampler(DownloadJob job, CancellationToken ct)
    {
        long previous = Interlocked.Read(ref job.DoneBytes);
        var clock = Stopwatch.StartNew();
        var lastAt = TimeSpan.Zero;

        return new Timer(_ =>
        {
            if (ct.IsCancellationRequested) return;

            long now = Interlocked.Read(ref job.DoneBytes);
            var at = clock.Elapsed;
            double secs = (at - lastAt).TotalSeconds;
            if (secs > 0) job.SpeedBps = Math.Max(0, (now - previous) / secs);

            previous = now;
            lastAt = at;
        }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    private async Task OneFileAsync(DownloadJob job, PlanFile pf, CancellationToken ct)
    {
        var entry = pf.Entry;
        string dest = Path.Combine(settings.DataDir, entry.DirName, entry.FileName);

        var fp = new FileProgress { Name = entry.FileName, Total = Math.Max(0, pf.Size) };
        job.Active[entry.FileName] = fp;

        long counted = 0;

        try
        {
            bool existed = File.Exists(dest);

            await client.DownloadFileAsync(entry, dest, settings.VerifyHashes, (done, total) =>
            {
                if (total > 0)
                {
                    long previousTotal = Interlocked.Exchange(ref fp.Total, total);
                    if (previousTotal != total)
                        Interlocked.Add(ref job.TotalBytes, total - previousTotal);
                }
                fp.Done = done;

                long delta = done - counted;
                if (delta != 0)
                {
                    counted = done;
                    Interlocked.Add(ref job.DoneBytes, delta);
                }
            }, ct);

            fp.State = existed ? "skipped" : "done";
            if (existed) Interlocked.Increment(ref job.SkippedFiles);
            else Interlocked.Increment(ref job.DoneFiles);

            job.Active.TryRemove(entry.FileName, out _);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            job.Active.TryRemove(entry.FileName, out _);
            throw;
        }
        catch (Exception ex)
        {
            fp.State = "failed";
            fp.Error = ex.Message;
            Interlocked.Increment(ref job.FailedFiles);
            job.Say($"FAILED {entry.FileName}: {ex.Message}");
        }
    }
}
