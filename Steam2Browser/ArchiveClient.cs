using System.Collections.Concurrent;
using System.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace Steam2Browser;

/// <summary>
/// HTTP access to one archive mirror, with optional failover to the others.
/// Every mirror serves byte-identical files, so a failed part can be retried elsewhere.
/// </summary>
public sealed class ArchiveClient(HttpClient http)
{
    private const long SegmentedThresholdBytes = 1L * 1024 * 1024;
    private const int MinSegmentSizeBytes = 1 * 1024 * 1024;
    private const int MaxSegmentSizeBytes = 8 * 1024 * 1024;
    private const int SegmentsPerFile = 32;
    private const int MaxConcurrentTransfers = 32;
    private const int MaxRangeFailuresWithoutProgress = 6;
    private static readonly TimeSpan ReadInactivityTimeout = TimeSpan.FromSeconds(20);

    // DownloadManager limits files, while this gate limits the actual HTTP streams. Without it,
    // several files with many ranges each can overload the mirror and leave every stream starved.
    private readonly SemaphoreSlim transferGate = new(MaxConcurrentTransfers, MaxConcurrentTransfers);

    public Mirror Primary { get; set; } = Mirrors.All[0];

    /// <summary>When true, a failed request is retried against the remaining mirrors.</summary>
    public bool Failover { get; set; } = true;

    /// <summary>
    /// Split large dats into parallel ranges. Off by default: these mirrors ramp a connection up
    /// over time, so every extra range is another cold stream competing with the one that warmed up.
    /// </summary>
    public bool UseSegments { get; set; }

    /// <summary>Cap on a warm-up touch. It must never hold anything up, so it gives up quickly.</summary>
    private static readonly TimeSpan WarmTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Asks the mirror for one byte of a file that will be wanted shortly, so the storage has it
    /// open and cached by the time the real request arrives. Costs a byte plus headers, targets the
    /// mirror actually in use (no failover), and swallows everything — nothing depends on it.
    /// </summary>
    public async Task WarmAsync(Entry entry, CancellationToken ct = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(WarmTimeout);

            using var req = new HttpRequestMessage(HttpMethod.Get, Primary.Url(entry.RelPath));
            req.Headers.Range = new RangeHeaderValue(0, 0);

            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            await resp.Content.CopyToAsync(Stream.Null, timeout.Token);
        }
        catch
        {
            // Best effort. A failed warm-up is not a failed download.
        }
    }

    /// <summary>
    /// Which mirrors honour a Range request, learned from the answers themselves — no probing.
    /// It varies by file kind and not only by host: de replies 206 for blobs but plain 200 with
    /// the whole body for dats, while us honours both.
    /// </summary>
    private readonly ConcurrentDictionary<(string Mirror, Kind Kind), bool> _honoursRange = new();

    private bool? HonoursRange(Mirror m, Kind kind) =>
        _honoursRange.TryGetValue((m.Id, kind), out bool v) ? v : null;

    /// <summary>
    /// Picks how to continue a partly-downloaded file: resume on a mirror that honours Range, or
    /// discard the partial and start over on the fastest one.
    ///
    /// Starting over sounds like the wasteful choice and often is not. Resuming only saves the
    /// bytes already on disk; it does nothing for throughput, since a 206 and a 200 stream at the
    /// same rate over the same connection. So when the fast mirror ignores Range, re-fetching the
    /// whole file there beats collecting the tail from a slow one — measured here, de runs about
    /// five times faster than us, which means a restart wins until roughly 80% is already down.
    /// Both speeds have to be known for that comparison; without them, resuming is the safe pick
    /// because it can never transfer more than starting over would.
    /// </summary>
    private (List<Mirror> Order, long ResumeFrom) PlanResume(Entry entry, long resumeFrom)
    {
        var all = Order().ToList();
        if (resumeFrom <= 0 || all.Count == 0) return (all, resumeFrom);

        // Untried mirrors count as usable: the only way to learn is to ask one.
        var resumable = all.Where(m => HonoursRange(m, entry.Kind) != false).ToList();
        if (resumable.Count == all.Count) return (all, resumeFrom);

        // The swarm has no URL to range against, so it takes no part in this comparison.
        long total = entry.ApproxSize;
        var fastest = all.Where(m => !m.IsTorrent).MaxBy(m => m.SpeedBps);
        var bestResume = resumable.Where(m => !m.IsTorrent).MaxBy(m => m.SpeedBps);

        // Nothing can resume, so the partial is worthless whatever happens.
        if (bestResume is null) return (all, 0);

        if (total <= 0 || fastest is null || fastest.SpeedBps <= 0 || bestResume.SpeedBps <= 0)
            return (Reordered(all, bestResume), resumeFrom);

        double restartSeconds = total / fastest.SpeedBps;
        double resumeSeconds = (total - resumeFrom) / bestResume.SpeedBps;

        return resumeSeconds <= restartSeconds
            ? (Reordered(all, bestResume), resumeFrom)
            : (Reordered(all, fastest), 0);
    }

    private static List<Mirror> Reordered(List<Mirror> all, Mirror first) =>
        [first, .. all.Where(m => m.Id != first.Id)];

    private IEnumerable<Mirror> Order()
    {
        yield return Primary;
        if (!Failover) yield break;
        foreach (var m in Mirrors.All)
            if (m.Id != Primary.Id)
                yield return m;
    }

    /// <summary>
    /// The swarm, once it exists. Set after construction because the torrent engine is built after
    /// this client and needs it.
    /// </summary>
    public TorrentSource? Torrent { get; set; }

    /// <summary>
    /// One archive file's bytes, from wherever the archive currently lives.
    ///
    /// Takes the entry rather than a path because the swarm needs to know which file of the torrent
    /// is meant and what its hash should be, neither of which a relative path carries. The HTTP
    /// route below is left in place and is what this falls back to if a host is ever configured
    /// again; today there is none, so every call goes to the swarm.
    /// </summary>
    public Task<byte[]> GetBytesAsync(Entry entry, CancellationToken ct = default)
        => Torrent is { } t && Primary.IsTorrent
            ? t.GetBytesAsync(entry, ct)
            : GetBytesAsync(entry.RelPath, ct);

    /// <summary>
    /// A file's exact length.
    ///
    /// The torrent states the length of all 116 346 files in its own metadata, so with the swarm as
    /// the source this is answered from memory: no request, no waiting, and exact rather than the
    /// approximation the directory listings used to give. It is what tells two dats of a forked
    /// version apart, so being exact is the whole point of it.
    /// </summary>
    public Task<long> GetLengthAsync(Entry entry, CancellationToken ct = default)
        => Torrent is { } t && Primary.IsTorrent && t.TryGetLength(entry.RelPath) is { } len
            ? Task.FromResult(len)
            : GetLengthAsync(entry.RelPath, ct);

    public async Task<byte[]> GetBytesAsync(string relPath, CancellationToken ct = default)
    {
        Exception? last = null;
        foreach (var m in Order())
        {
            try { return await http.GetByteArrayAsync(m.Url(relPath), ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { last = ex; }
        }
        throw last ?? new HttpRequestException($"could not fetch {relPath}");
    }

    /// <summary>
    /// Fetches a text resource, optionally reporting bytes as they arrive. The directory listings
    /// are around 20 MB each, which is long enough that a caller wants to show real progress
    /// rather than a spinner — so this streams instead of buffering the whole body first.
    /// The callback gets (bytes so far, total) with total -1 when the server sends no length.
    /// </summary>
    public async Task<string> GetStringAsync(string relPath, CancellationToken ct = default,
                                             Action<long, long>? progress = null)
    {
        Exception? last = null;
        foreach (var m in Order())
        {
            try
            {
                if (progress is null) return await http.GetStringAsync(m.Url(relPath), ct);

                using var resp = await http.GetAsync(m.Url(relPath),
                                                     HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();

                long total = resp.Content.Headers.ContentLength ?? -1;
                await using var body = await resp.Content.ReadAsStreamAsync(ct);

                var buffer = new byte[128 * 1024];
                var text = new StringBuilder(total > 0 ? (int)Math.Min(total, int.MaxValue) : 1 << 20);
                var decoder = Encoding.UTF8.GetDecoder();
                var chars = new char[buffer.Length + 8];
                long read = 0;

                while (true)
                {
                    int n = await body.ReadAsync(buffer, ct);
                    if (n == 0) break;

                    // Decoding as it streams keeps a multi-byte character split across two reads
                    // from turning into replacement characters.
                    int produced = decoder.GetChars(buffer, 0, n, chars, 0);
                    text.Append(chars, 0, produced);

                    read += n;
                    progress(read, total);
                }

                return text.ToString();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { last = ex; }
        }
        throw last ?? new HttpRequestException($"could not fetch {relPath}");
    }

    /// <summary>Exact byte length from Content-Length. -1 when the file is missing everywhere.</summary>
    public async Task<long> GetLengthAsync(string relPath, CancellationToken ct = default)
    {
        foreach (var m in Order())
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Head, m.Url(relPath));
                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                if (resp.StatusCode == HttpStatusCode.NotFound) continue;
                resp.EnsureSuccessStatusCode();
                if (resp.Content.Headers.ContentLength is long len) return len;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { /* try next mirror */ }
        }
        return -1;
    }

    /// <summary>
    /// Downloads one file to <paramref name="destPath"/>, resuming a partial .part file via Range,
    /// then verifies sha256 against the hash embedded in the file name.
    /// Returns the number of bytes pulled over the network this call.
    /// </summary>
    public async Task<long> DownloadFileAsync(
        Entry entry,
        string destPath,
        bool verify,
        Action<long, long>? onProgress,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

        if (File.Exists(destPath))
        {
            if (!verify) { onProgress?.Invoke(new FileInfo(destPath).Length, new FileInfo(destPath).Length); return 0; }
            if (await VerifyAsync(destPath, entry.Sha, ct))
            {
                long have = new FileInfo(destPath).Length;
                onProgress?.Invoke(have, have);
                return 0;
            }
            File.Delete(destPath);
        }

        string partPath = destPath + ".part";
        string segmentedMarkerPath = partPath + ".segmented";

        // Segmented downloads preallocate the target, so its file length is not resume progress.
        // A marker left by a killed process means the file must be restarted instead of issuing a
        // bogus Range request from EOF.
        if (File.Exists(segmentedMarkerPath))
        {
            TryDelete(partPath);
            TryDelete(segmentedMarkerPath);
        }

        long resumeFrom = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;

        // Builds before the marker was introduced can leave a partially-filled, full-size .part.
        // Recover a genuinely complete file, otherwise discard it so the ranged path can run.
        if (resumeFrom > 0 && entry.Kind == Kind.Dat)
        {
            long remoteLength = await GetLengthAsync(entry.RelPath, ct);
            if (remoteLength > 0 && resumeFrom >= remoteLength)
            {
                if (resumeFrom == remoteLength && await VerifyAsync(partPath, entry.Sha, ct))
                {
                    File.Move(partPath, destPath, overwrite: true);
                    onProgress?.Invoke(remoteLength, remoteLength);
                    return 0;
                }

                TryDelete(partPath);
                resumeFrom = 0;
            }
        }

        var (order, planned) = PlanResume(entry, resumeFrom);
        if (planned == 0 && resumeFrom > 0) TryDelete(partPath);
        resumeFrom = planned;

        Exception? last = null;
        foreach (var m in order)
        {
            try
            {
                long pulled = await PullAsync(m, entry, partPath, resumeFrom, onProgress, ct);

                if (verify && !await VerifyAsync(partPath, entry.Sha, ct))
                {
                    File.Delete(partPath);
                    resumeFrom = 0;
                    last = new InvalidDataException($"sha256 mismatch for {entry.FileName} from {m.Id}");
                    continue;
                }

                File.Move(partPath, destPath, overwrite: true);
                return pulled;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                last = ex;
                resumeFrom = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
            }
        }
        throw last ?? new HttpRequestException($"could not download {entry.FileName}");
    }

    private async Task<long> PullAsync(
        Mirror mirror, Entry entry, string partPath, long resumeFrom,
        Action<long, long>? onProgress, CancellationToken ct)
    {
        // Measured on one 53 MB dat: de refuses to serve ranges for dats and sustains 0.43 MB/s
        // on its single stream, while ro serves them and reaches 3.48 MB/s across sixteen. So the
        // question is not whether the user asked for segments, it is whether this mirror will
        // honour them — a mirror already known to ignore Range is left on a plain download.
        bool canSegment = entry.Kind == Kind.Dat
                          && HonoursRange(mirror, entry.Kind) != false
                          && (entry.ApproxSize < 0 || entry.ApproxSize >= SegmentedThresholdBytes);

        if (canSegment)
        {
            try
            {
                var segmented = await TryPullSegmentedAsync(mirror, entry, partPath, resumeFrom, onProgress, ct);
                if (segmented is not null) return segmented.Value;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Falling back to one stream is always possible, so a mirror that will not segment
                // costs this file its speed rather than the download itself.
                _honoursRange[(mirror.Id, entry.Kind)] = false;
            }
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, mirror.Url(entry.RelPath));
        if (resumeFrom > 0) req.Headers.Range = new RangeHeaderValue(resumeFrom, null);

        await transferGate.WaitAsync(ct);
        try
        {
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

            if (resumeFrom > 0)
            {
                bool honoured = resp.StatusCode == HttpStatusCode.PartialContent;
                _honoursRange[(mirror.Id, entry.Kind)] = honoured;

                // It sent the whole file instead of the tail, so the partial is now useless.
                if (!honoured)
                {
                    resumeFrom = 0;
                    if (File.Exists(partPath)) File.Delete(partPath);
                }
            }
            resp.EnsureSuccessStatusCode();

            long total = (resp.Content.Headers.ContentLength ?? -1) + (resumeFrom > 0 ? resumeFrom : 0);

            await using var netStream = await resp.Content.ReadAsStreamAsync(ct);
            await using var file = new FileStream(
                partPath, resumeFrom > 0 ? FileMode.Append : FileMode.Create,
                FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);

            var buffer = new byte[1 << 20];
            long done = resumeFrom;
            long pulled = 0;
            int read;

            while ((read = await ReadWithInactivityTimeoutAsync(netStream, buffer, ct)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                pulled += read;
                onProgress?.Invoke(done, total);
            }

            return pulled;
        }
        finally
        {
            transferGate.Release();
        }
    }

    private async Task<long?> TryPullSegmentedAsync(
        Mirror mirror, Entry entry, string partPath, long resumeFrom,
        Action<long, long>? onProgress, CancellationToken ct)
    {
        long length = await GetMirrorLengthAsync(mirror, entry.RelPath, ct);
        if (length < SegmentedThresholdBytes) return null;

        Directory.CreateDirectory(Path.GetDirectoryName(partPath)!);
        string markerPath = partPath + ".segmented";

        try
        {
            await File.WriteAllTextAsync(markerPath, length.ToString(), ct);
            await using (var fs = new FileStream(
                partPath, resumeFrom > 0 ? FileMode.Open : FileMode.Create,
                FileAccess.Write, FileShare.ReadWrite, 1, useAsync: true))
                fs.SetLength(length);

            var ranges = new Queue<(int Index, long From, long To)>();
            int index = 0;
            long segmentSize = Math.Clamp(
                (length + SegmentsPerFile - 1) / SegmentsPerFile,
                MinSegmentSizeBytes,
                MaxSegmentSizeBytes);
            for (long from = resumeFrom; from < length; from += segmentSize)
                ranges.Enqueue((index++, from, Math.Min(length - 1, from + segmentSize - 1)));

            var segmentProgress = new long[index];
            var progressLock = new object();
            long done = resumeFrom;
            onProgress?.Invoke(done, length);

            var workers = Enumerable.Range(0, Math.Min(SegmentsPerFile, index)).Select(async _ =>
            {
                while (true)
                {
                    (int Index, long From, long To) range;
                    lock (ranges)
                    {
                        if (ranges.Count == 0) return;
                        range = ranges.Dequeue();
                    }

                    await PullRangeAsync(mirror, entry, partPath, range.From, range.To, segmentDone =>
                    {
                        lock (progressLock)
                        {
                            long delta = segmentDone - segmentProgress[range.Index];
                            if (delta <= 0) return;

                            segmentProgress[range.Index] = segmentDone;
                            done += delta;
                            onProgress?.Invoke(done, length);
                        }
                    }, ct);
                }
            });

            await Task.WhenAll(workers);
            TryDelete(markerPath);
            onProgress?.Invoke(length, length);
            return length - resumeFrom;
        }
        catch
        {
            TryDelete(partPath);
            TryDelete(markerPath);
            throw;
        }
    }

    private async Task<long> GetMirrorLengthAsync(Mirror mirror, string relPath, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Head, mirror.Url(relPath));
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return -1;
        resp.EnsureSuccessStatusCode();
        return resp.Content.Headers.ContentLength ?? -1;
    }

    private async Task PullRangeAsync(
        Mirror mirror, Entry entry, string partPath, long from, long to,
        Action<long> onSegmentProgress, CancellationToken ct)
    {
        var buffer = new byte[1 << 20];
        long done = 0;
        int failuresWithoutProgress = 0;

        while (from + done <= to)
        {
            long attemptStart = done;
            try
            {
                await transferGate.WaitAsync(ct);
                try
                {
                    long requestFrom = from + done;
                    using var req = new HttpRequestMessage(HttpMethod.Get, mirror.Url(entry.RelPath))
                    {
                        // Independent HTTP/1.1 connections are intentional here. HTTP/2 would
                        // multiplex ranges over fewer TCP connections and lose the measured gain.
                        Version = HttpVersion.Version11,
                        VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                    };
                    req.Headers.Range = new RangeHeaderValue(requestFrom, to);

                    using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                    if (resp.StatusCode != HttpStatusCode.PartialContent)
                        throw new HttpRequestException($"range request was not honored by {mirror.Id}");
                    resp.EnsureSuccessStatusCode();

                    long? returnedFrom = resp.Content.Headers.ContentRange?.From;
                    if (returnedFrom != requestFrom)
                        throw new InvalidDataException($"range response started at {returnedFrom}, expected {requestFrom}");

                    await using var netStream = await resp.Content.ReadAsStreamAsync(ct);
                    await using var file = new FileStream(
                        partPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite,
                        1 << 20, useAsync: true);
                    file.Seek(requestFrom, SeekOrigin.Begin);

                    while (from + done <= to)
                    {
                        int wanted = (int)Math.Min(buffer.Length, to - (from + done) + 1);
                        int read = await ReadWithInactivityTimeoutAsync(netStream, buffer.AsMemory(0, wanted), ct);
                        if (read == 0) throw new EndOfStreamException($"range {requestFrom}-{to} ended early");

                        await file.WriteAsync(buffer.AsMemory(0, read), ct);
                        done += read;
                        onSegmentProgress(done);
                    }
                }
                finally
                {
                    transferGate.Release();
                }

                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception) when (failuresWithoutProgress < MaxRangeFailuresWithoutProgress)
            {
                failuresWithoutProgress = done > attemptStart ? 0 : failuresWithoutProgress + 1;
                if (failuresWithoutProgress >= MaxRangeFailuresWithoutProgress) throw;

                await Task.Delay(TimeSpan.FromMilliseconds(250 * failuresWithoutProgress), ct);
            }
        }
    }

    private static async ValueTask<int> ReadWithInactivityTimeoutAsync(
        Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        using var inactivity = CancellationTokenSource.CreateLinkedTokenSource(ct);
        inactivity.CancelAfter(ReadInactivityTimeout);
        try
        {
            return await stream.ReadAsync(buffer, inactivity.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"no download data received for {ReadInactivityTimeout.TotalSeconds:0} seconds");
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch { /* Preserve the original network or verification error. */ }
    }

    public static async Task<bool> VerifyAsync(string path, string expectedSha, CancellationToken ct = default)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        var hash = await SHA256.HashDataAsync(fs, ct);
        return Convert.ToHexStringLower(hash).Equals(expectedSha, StringComparison.OrdinalIgnoreCase);
    }
}
