using System.Collections.Concurrent;

namespace Steam2Browser;

/// <summary>What one version of a depot did, as far as its blob and its predecessor's can tell.</summary>
public sealed class VersionChanges
{
    public int Version;
    public string Crc = "";
    public string? Date;

    /// <summary>Whether that version's blob is on disk. Without it nothing below is known.</summary>
    public bool Local;

    public int AddedCount;
    public int ChangedCount;
    public int RemovedCount;

    /// <summary>Bytes carried in this version's dat: the new files plus the rewritten ones.</summary>
    public long PayloadBytes;

    /// <summary>How much the depot grew or shrank, additions and removals included.</summary>
    public long DeltaBytes;

    public int FilesInVersion;

    /// <summary>Which line of descent this version belongs to. Resets create more than one.</summary>
    public int Branch;

    /// <summary>True when the predecessor's blob is missing, so new and changed cannot be told apart.</summary>
    public bool Unclassified;

    public string? Error;
}

public sealed class BlobFetchStatus
{
    public bool Running;
    public int Done;
    public int Total;
    public int Failed;
    public string Message = "";
}

/// <summary>
/// Reads per-version change lists out of blobs, and pulls a depot's blobs in bulk so the whole
/// history can be browsed at once.
///
/// A version's blob holds the manifest (every path and size at that point) and the file id table
/// (only the files whose data sits in that version's dat). Comparing a version's manifest with its
/// predecessor's turns that into a real diff: which files are new, which were rewritten, which
/// disappeared, and how each one's size moved — all without touching a single dat.
/// </summary>
public sealed class ChangeIndex(ArchiveClient client, Settings settings)
{
    /// <summary>
    /// One blob, decoded and keyed by path rather than by file id.
    ///
    /// A rewritten file gets a fresh file id in the next version, so comparing ids reports every
    /// edit as a removal plus an addition — 67681 v1 came out as 143 new and 155 removed with not
    /// one changed. The path is what actually persists across versions.
    /// </summary>
    private sealed record Decoded(
        Dictionary<string, long> SizeByPath,
        Dictionary<string, byte> ModeByPath,
        HashSet<string> CarriedHere,
        uint? ParentCrc,
        ulong? DatSize);

    private readonly ConcurrentDictionary<string, Decoded> _decoded = new();
    private readonly ConcurrentDictionary<string, VersionChanges> _summaries = new();
    private readonly ConcurrentDictionary<int, BlobFetchStatus> _fetches = new();

    public BlobFetchStatus StatusFor(int depot) =>
        _fetches.GetValueOrDefault(depot) ?? new BlobFetchStatus();

    private string PathOf(Entry blob) => Path.Combine(settings.DataDir, blob.DirName, blob.FileName);

    private Decoded? Decode(Entry blob)
    {
        if (_decoded.TryGetValue(blob.FileName, out var known)) return known;

        string blobPath = PathOf(blob);
        if (!File.Exists(blobPath)) return null;

        byte[] bytes = File.ReadAllBytes(blobPath);

        var tree = ManifestFormat.TreeFromBlob(bytes)
                   ?? throw new InvalidDataException("this blob carries no manifest");

        // The manifest records a size for every file at this version, not only the changed ones,
        // which is what makes a size delta against the previous version possible at all.
        var sizeByPath = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var pathById = new Dictionary<uint, string>();

        foreach (var node in tree.Nodes)
        {
            if (node.Flags == 0) continue;
            sizeByPath[node.Path] = node.Size;
            pathById[node.FileId] = node.Path;
        }

        var table = ChecksumTable.Parse(bytes, blob.Version);

        var modeByPath = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var carried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (id, loc) in table)
        {
            if (!pathById.TryGetValue(id, out var path)) continue;
            carried.Add(path);
            modeByPath[path] = loc.FileMode;
            sizeByPath[path] = (long)loc.FileSize;
        }

        var info = BlobFormat.Parse(bytes);

        var decoded = new Decoded(sizeByPath, modeByPath, carried, info.ParentCrc, info.DatSize);
        _decoded[blob.FileName] = decoded;
        return decoded;
    }

    /// <summary>
    /// The blob this one was actually built on, taken from the parent crc it records.
    ///
    /// Where a version exists more than once, guessing is not an option: 250 depots in this archive
    /// restart from v0 more than once, and picking the wrong candidate compares one branch against
    /// another — which is how a routine patch came out as tens of thousands of files added and
    /// removed at once. When the parent cannot be identified this returns null, and the caller says
    /// so rather than showing a made-up diff.
    /// </summary>
    private Entry? PreviousOf(Depot depot, Entry blob)
    {
        if (blob.Version == 0) return null;

        var below = depot.Blobs.Where(b => b.Version == blob.Version - 1).ToList();
        if (below.Count == 0) return null;
        if (below.Count == 1) return below[0];

        // Decode first: the crc lives inside the blob, and reading the cache before anything has
        // been decoded is what made this fall back to the wrong branch.
        var decoded = Decode(blob);
        if (decoded?.ParentCrc is uint crc) return below.FirstOrDefault(b => b.Crc == crc);

        return null;
    }

    public readonly record struct ChangedFile(string Path, long Size, long Delta, byte Mode, string Change);

    /// <summary>The per-file diff for one version, or null when its blob is not on disk.</summary>
    public (List<ChangedFile> Files, bool Unclassified)? Diff(Depot depot, Entry blob)
    {
        var now = Decode(blob);
        if (now is null) return null;

        var prevEntry = PreviousOf(depot, blob);
        var before = prevEntry is null ? null : Decode(prevEntry);

        // Version 0 has nothing before it, so everything in it is genuinely new.
        bool unclassified = blob.Version > 0 && before is null;

        var files = new List<ChangedFile>(now.CarriedHere.Count + 8);

        foreach (var path in now.CarriedHere)
        {
            long size = now.SizeByPath.GetValueOrDefault(path);
            byte mode = now.ModeByPath.GetValueOrDefault(path);

            string change;
            long delta;

            if (before is null)
            {
                change = blob.Version == 0 ? "new" : "changed";
                delta = blob.Version == 0 ? size : 0;
            }
            else if (before.SizeByPath.TryGetValue(path, out long was))
            {
                change = "changed";
                delta = size - was;
            }
            else
            {
                change = "new";
                delta = size;
            }

            files.Add(new ChangedFile(path, size, delta, mode, change));
        }

        // A file that was there and is not any more carries no data in this dat, so it would
        // otherwise be invisible — but it is exactly what a diff should show.
        if (before is not null)
        {
            foreach (var (path, was) in before.SizeByPath)
            {
                if (now.SizeByPath.ContainsKey(path)) continue;
                files.Add(new ChangedFile(path, 0, -was, 0, "removed"));
            }
        }

        files.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
        return (files, unclassified);
    }

    /// <summary>Every version of the depot, newest first, with its counts where the blobs allow.</summary>
    public List<VersionChanges> Summary(Depot depot)
    {
        var (_, branchOf) = Branches(depot);

        var rows = new List<VersionChanges>(depot.Blobs.Count);

        // Grouped by branch, then newest version first inside it, so two chains that both count
        // from v0 no longer interleave.
        var ordered = depot.Blobs
            .OrderBy(b => branchOf.GetValueOrDefault(b.FileName))
            .ThenByDescending(b => b.Version)
            .ThenByDescending(b => b.Date);

        foreach (var blob in ordered)
        {
            var row = Describe(depot, blob);
            row.Branch = branchOf.GetValueOrDefault(blob.FileName);
            rows.Add(row);
        }

        return rows;
    }

    public VersionChanges Describe(Depot depot, Entry blob)
    {
        // Keyed on the predecessor too: counts change once the previous blob arrives.
        var prev = PreviousOf(depot, blob);
        string key = blob.FileName + "|" + (prev is null ? "-" : (File.Exists(PathOf(prev)) ? prev.FileName : "?"));

        if (_summaries.TryGetValue(key, out var known)) return known;

        var row = new VersionChanges
        {
            Version = blob.Version,
            Crc = blob.CrcHex,
            Date = blob.Date == default ? null : blob.Date.ToString("yyyy-MM-dd HH:mm:ss"),
        };

        try
        {
            var result = Diff(depot, blob);
            if (result is null) return row;   // not cached: the blob may arrive later

            var (files, unclassified) = result.Value;

            row.Local = true;
            row.Unclassified = unclassified;
            row.AddedCount = files.Count(f => f.Change == "new");
            row.ChangedCount = files.Count(f => f.Change == "changed");
            row.RemovedCount = files.Count(f => f.Change == "removed");
            row.PayloadBytes = files.Where(f => f.Change != "removed").Sum(f => f.Size);
            row.DeltaBytes = files.Sum(f => f.Delta);
            row.FilesInVersion = Decode(blob)?.SizeByPath.Count ?? 0;
        }
        catch (Exception ex)
        {
            row.Local = true;
            row.Error = ex.Message;
        }

        _summaries[key] = row;
        return row;
    }


    /// <summary>
    /// The dat that goes with a blob. Where a version forked there are two, and the blob records
    /// the exact length of its own — matched here against the listing size, which is rounded, so a
    /// tolerance is needed but the candidates differ by megabytes.
    /// </summary>
    public Entry? DatFor(Depot depot, Entry blob)
    {
        var candidates = depot.Dats.Where(d => d.Version == blob.Version).ToList();
        if (candidates.Count == 0) return null;
        if (candidates.Count == 1) return candidates[0];

        if (Decode(blob)?.DatSize is not ulong want) return null;

        return candidates
            .Where(c => c.ApproxSize > 0)
            .OrderBy(c => Math.Abs(c.ApproxSize - (long)want))
            .FirstOrDefault();
    }

    // ---------------- branches ----------------

    /// <summary>One line of descent: a head blob and everything it was built on, down to a root.</summary>
    public sealed class BranchInfo
    {
        public int Index;
        public string HeadCrc = "";
        public int MinVersion;
        public int MaxVersion;
        public string? FirstDate;
        public string? LastDate;
        public int BlobCount;

        /// <summary>Set when this branch joins an older one instead of starting from its own root.</summary>
        public int? ForksFromVersion;
    }

    /// <summary>
    /// Splits a depot into lines of descent by following each blob's recorded parent.
    ///
    /// A reset restarts the version numbers, so without this the history interleaves two unrelated
    /// chains that both count from v0 and both claim to be the first release.
    /// </summary>
    public (List<BranchInfo> Branches, Dictionary<string, int> Of) Branches(Depot depot)
    {
        var parent = new Dictionary<string, Entry?>();
        foreach (var b in depot.Blobs) parent[b.FileName] = PreviousOf(depot, b);

        var isParent = new HashSet<string>();
        foreach (var p in parent.Values) if (p is not null) isParent.Add(p.FileName);

        // A head is a blob nothing else was built on. Newest first, so the live branch is branch 1.
        var heads = depot.Blobs
            .Where(b => !isParent.Contains(b.FileName))
            .OrderByDescending(b => b.Version)
            .ThenByDescending(b => b.Date)
            .ToList();

        var of = new Dictionary<string, int>();
        var list = new List<BranchInfo>();

        foreach (var head in heads)
        {
            var info = new BranchInfo
            {
                Index = list.Count,
                HeadCrc = head.CrcHex,
                MinVersion = head.Version,
                MaxVersion = head.Version,
            };

            var walk = head;
            int guard = 0;

            while (walk is not null && guard++ < 4096)
            {
                if (of.ContainsKey(walk.FileName))
                {
                    // Reached ground an earlier branch already covers: this one forked off here.
                    info.ForksFromVersion = walk.Version;
                    break;
                }

                of[walk.FileName] = info.Index;
                info.BlobCount++;
                info.MinVersion = Math.Min(info.MinVersion, walk.Version);

                var date = walk.Date == default ? null : walk.Date.ToString("yyyy-MM-dd");
                info.FirstDate = date ?? info.FirstDate;
                info.LastDate ??= date;

                walk = parent.GetValueOrDefault(walk.FileName);
            }

            list.Add(info);
        }

        // Anything unreachable — a blob whose parent could not be identified — still needs a home.
        foreach (var b in depot.Blobs)
        {
            if (of.ContainsKey(b.FileName)) continue;
            of[b.FileName] = list.Count;
            list.Add(new BranchInfo
            {
                Index = list.Count,
                HeadCrc = b.CrcHex,
                MinVersion = b.Version,
                MaxVersion = b.Version,
                BlobCount = 1,
                FirstDate = b.Date == default ? null : b.Date.ToString("yyyy-MM-dd"),
                LastDate = b.Date == default ? null : b.Date.ToString("yyyy-MM-dd"),
            });
        }

        return (list, of);
    }

    // ---------------- bulk fetch ----------------

    /// <summary>
    /// Which versions' dats actually hold bytes the target version needs.
    ///
    /// A dat carries only the files its version wrote, and the extractor resolves a file by walking
    /// the chain's file-id tables in order, letting later versions overwrite earlier entries. So a
    /// version whose every written file was overwritten again before the target contributes nothing
    /// to it, and its dat can be skipped — the blobs still have to be read, but they are kilobytes.
    ///
    /// Returns null when the answer cannot be established: a blob missing from disk, or a file id in
    /// the target manifest that no table in the chain claims. Pruning on a guess would produce an
    /// extraction that fails partway through, so an unknown answer means "download everything".
    /// </summary>
    public List<int>? NeededDatVersions(IReadOnlyList<Entry> chainBlobs, Entry targetBlob)
    {
        if (chainBlobs.Count == 0) return null;

        var owner = new Dictionary<uint, int>();

        foreach (var blob in chainBlobs.OrderBy(b => b.Version))
        {
            string path = PathOf(blob);
            if (!File.Exists(path)) return null;

            try
            {
                foreach (uint id in ChecksumTable.Parse(File.ReadAllBytes(path), blob.Version).Keys)
                    owner[id] = blob.Version;
            }
            catch
            {
                return null;
            }
        }

        string targetPath = PathOf(targetBlob);
        if (!File.Exists(targetPath)) return null;

        var tree = TryTree(targetPath);
        if (tree is null) return null;

        var needed = new HashSet<int>();

        foreach (var node in tree.Nodes)
        {
            if (node.Flags == 0) continue;
            if (!owner.TryGetValue(node.FileId, out int v)) return null;
            needed.Add(v);
        }

        return [.. needed.OrderBy(v => v)];

        static ManifestFormat.ManifestTree? TryTree(string path)
        {
            try { return ManifestFormat.TreeFromBlob(File.ReadAllBytes(path)); }
            catch { return null; }
        }
    }

    /// <summary>
    /// Removes from a plan the dats that hold nothing the target version reads, so the size quoted
    /// before the download is the size that will actually be fetched.
    ///
    /// Only possible once every blob in the chain is on disk, since the answer comes out of their
    /// file id tables. When they are not, the plan is left whole and <see cref="ChainPlan.SkippedDats"/>
    /// stays null — the download prunes again later, after its blob phase, where the answer exists.
    /// </summary>
    public void Prune(ChainPlan plan)
    {
        var blobs = plan.Files.Where(f => f.Entry.Kind == Kind.Blob).Select(f => f.Entry).ToList();
        var dats = plan.Files.Where(f => f.Entry.Kind == Kind.Dat).ToList();

        plan.ChainDats = dats.Count;
        if (blobs.Count == 0 || dats.Count == 0) return;

        var target = blobs
            .Where(b => b.Version == plan.TargetVersion)
            .FirstOrDefault(b => plan.BlobCrc is null
                                 || b.CrcHex.Equals(plan.BlobCrc, StringComparison.OrdinalIgnoreCase));
        if (target is null) return;

        var needed = NeededDatVersions(blobs, target);
        if (needed is null) return;

        var keep = needed.ToHashSet();
        var dropped = dats.Where(f => !keep.Contains(f.Entry.Version)).ToList();

        plan.SkippedDats = dropped.Count;
        plan.SkippedBytes = dropped.Sum(f => Math.Max(0, f.Size));

        if (dropped.Count == 0) return;

        foreach (var f in dropped) plan.Files.Remove(f);

        plan.TotalBytes = plan.Files.Sum(f => Math.Max(0, f.Size));
        plan.TotalExact = plan.Files.Where(f => f.Entry.Kind == Kind.Dat).All(f => f.SizeExact);
    }

    /// <summary>True when this blob is already on disk.</summary>
    public bool HasLocal(Entry blob) => File.Exists(PathOf(blob));

    /// <summary>Status key for the archive-wide range fetch, which is not tied to one depot.</summary>
    private const int RangeKey = -1;

    public BlobFetchStatus RangeStatus => _fetches.GetValueOrDefault(RangeKey) ?? new BlobFetchStatus();

    /// <summary>
    /// Pulls the blobs of every depot in an id range. Blobs are kilobytes, so a wide range is
    /// cheap in bytes but long in requests — which is why it reports progress and can be stopped.
    /// </summary>
    public void FetchRange(Catalog catalog, int fromDepot, int toDepot)
    {
        var status = _fetches.GetOrAdd(RangeKey, _ => new BlobFetchStatus());
        lock (status)
        {
            if (status.Running) return;
            status.Running = true;
            status.Done = 0;
            status.Failed = 0;
            status.Total = 0;
            status.Message = "";
        }

        int lo = Math.Min(fromDepot, toDepot);
        int hi = Math.Max(fromDepot, toDepot);

        _ = Task.Run(async () =>
        {
            try
            {
                var wanted = catalog.Ordered
                    .Where(d => d.Id >= lo && d.Id <= hi)
                    .SelectMany(d => d.Blobs)
                    .Where(b => !File.Exists(PathOf(b)))
                    .ToList();

                status.Total = wanted.Count;
                status.Message = wanted.Count == 0
                    ? $"depots {lo}–{hi}: every blob is already here"
                    : $"depots {lo}–{hi}: fetching {wanted.Count} blob(s)";

                using var gate = new SemaphoreSlim(16);

                await Task.WhenAll(wanted.Select(async blob =>
                {
                    await gate.WaitAsync();
                    try
                    {
                        string path = PathOf(blob);
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                        byte[] bytes = await client.GetBytesAsync(blob);

                        string temp = path + ".part";
                        await File.WriteAllBytesAsync(temp, bytes);
                        File.Move(temp, path, overwrite: true);

                        _decoded.TryRemove(blob.FileName, out _);
                        Interlocked.Increment(ref status.Done);
                    }
                    catch
                    {
                        Interlocked.Increment(ref status.Failed);
                    }
                    finally
                    {
                        gate.Release();
                    }
                }));

                _summaries.Clear();

                status.Message = status.Failed > 0
                    ? $"depots {lo}–{hi}: {status.Done} fetched, {status.Failed} failed"
                    : $"depots {lo}–{hi}: {status.Done} blob(s) fetched";
            }
            catch (Exception ex)
            {
                status.Message = ex.Message;
            }
            finally
            {
                status.Running = false;
            }
        });
    }

    /// <summary>
    /// Downloads every blob of the depot that is missing. Safe to call again; a second call while
    /// one is running is ignored.
    /// </summary>
    public void FetchAll(Depot depot)
    {
        var status = _fetches.GetOrAdd(depot.Id, _ => new BlobFetchStatus());
        lock (status)
        {
            if (status.Running) return;
            status.Running = true;
            status.Done = 0;
            status.Failed = 0;
            status.Total = 0;
            status.Message = "";
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var missing = depot.Blobs.Where(b => !File.Exists(PathOf(b))).ToList();
                status.Total = missing.Count;
                status.Message = missing.Count == 0
                    ? "every blob is already here"
                    : $"fetching {missing.Count} blob(s)";

                // Blobs are kilobytes, so latency dominates and many at once costs nothing.
                using var gate = new SemaphoreSlim(16);

                await Task.WhenAll(missing.Select(async blob =>
                {
                    await gate.WaitAsync();
                    try
                    {
                        string path = PathOf(blob);
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                        byte[] bytes = await client.GetBytesAsync(blob);

                        // Written aside and moved into place: a reader polling this depot must
                        // never see a half-written blob. One that did got decoded as a version
                        // with no files at all, and the empty result was then cached for good.
                        string temp = path + ".part";
                        await File.WriteAllBytesAsync(temp, bytes);
                        File.Move(temp, path, overwrite: true);

                        // Anything decoded before the file arrived is worthless now.
                        _decoded.TryRemove(blob.FileName, out _);

                        Interlocked.Increment(ref status.Done);
                    }
                    catch
                    {
                        Interlocked.Increment(ref status.Failed);
                    }
                    finally
                    {
                        gate.Release();
                    }
                }));

                // Counts were computed while blobs were missing, so drop them and let them rebuild.
                _summaries.Clear();
                _decoded.Clear();

                status.Message = status.Failed > 0
                    ? $"{status.Done} fetched, {status.Failed} failed"
                    : $"{status.Done} blob(s) fetched";
            }
            catch (Exception ex)
            {
                status.Message = ex.Message;
            }
            finally
            {
                status.Running = false;
            }
        });
    }
}
