namespace Steam2Browser;

public sealed record PlanFile(Entry Entry, long Size, bool SizeExact);

/// <summary>A blob that could be the chain head when a depot version is ambiguous.</summary>
public sealed record BlobChoice(string Crc, string Date, long ApproxSize);

public sealed class ChainPlan
{
    public int Depot;
    public int TargetVersion;
    public string? BlobCrc;

    /// <summary>direct = no fork below the target; smart = fork resolved through blob parent links;
    /// superset = fork present but unresolved, so every candidate is downloaded.</summary>
    public string Mode = "direct";

    public List<PlanFile> Files = new();
    public long TotalBytes;
    public bool TotalExact;
    public List<string> Warnings = new();
    public List<BlobChoice> Choices = new();
    public bool NeedsChoice;
    public string? Error;
    public string ExtractArgs = "";

    /// <summary>How many dats were left out because nothing in the target version reads them, and
    /// what that saved. Null when the question could not be answered — see ChangeIndex.Prune.</summary>
    public int? SkippedDats;

    public long SkippedBytes;

    /// <summary>Dats in the chain before any were skipped, so the UI can say "2 of 57".</summary>
    public int ChainDats;

    /// <summary>
    /// Fetch the whole chain, skipping nothing.
    ///
    /// The optimiser works out which dats the target version actually reads and leaves the rest
    /// alone, which is usually most of them. Someone archiving a depot wants the ones it discards
    /// too — they are the depot's history, not waste — so this turns it off for that download.
    /// </summary>
    public bool FullChain;
}

public static class ChainResolver
{
    /// <summary>
    /// Works out every file needed to extract depot/version. Data is stored as deltas, so the whole
    /// chain down to version 0 is required. Where Valve reset a depot the same version number exists
    /// twice; the true parent is recorded inside each blob, so the chain is walked through those links.
    /// </summary>
    public static async Task<ChainPlan> ResolveAsync(
        Catalog catalog,
        ArchiveClient client,
        string dataDir,
        int depotId,
        int targetVersion,
        string? blobCrc,
        CancellationToken ct = default)
    {
        var plan = new ChainPlan { Depot = depotId, TargetVersion = targetVersion, BlobCrc = blobCrc };

        if (!catalog.Depots.TryGetValue(depotId, out var depot))
        {
            plan.Error = $"depot {depotId} is not in the archive";
            return plan;
        }
        if (targetVersion < 0 || targetVersion > depot.MaxVersion)
        {
            plan.Error = $"depot {depotId} has no version {targetVersion} (max is {depot.MaxVersion})";
            return plan;
        }

        ReportGaps(depot, targetVersion, plan);

        bool forkBelowTarget = depot.ForkedVersions.Any(v => v <= targetVersion);
        if (!forkBelowTarget)
        {
            BuildDirect(depot, targetVersion, plan);
            plan.ExtractArgs = $"{depotId} {targetVersion}";
            return plan;
        }

        // The depot was reset at or below the target, so the head blob has to be pinned.
        var heads = depot.Blobs.Where(b => b.Version == targetVersion).ToList();
        if (heads.Count == 0)
        {
            plan.Error = $"depot {depotId} has no blob for version {targetVersion}";
            return plan;
        }

        Entry? head;
        if (!string.IsNullOrWhiteSpace(blobCrc))
        {
            head = heads.FirstOrDefault(b => b.CrcHex.Equals(blobCrc.Trim(), StringComparison.OrdinalIgnoreCase));
            if (head is null)
            {
                plan.Error = $"no blob with crc {blobCrc} at depot {depotId} version {targetVersion}";
                plan.Choices = heads.Select(ToChoice).ToList();
                plan.NeedsChoice = true;
                return plan;
            }
        }
        else if (heads.Count == 1)
        {
            head = heads[0];
        }
        else
        {
            plan.Mode = "smart";
            plan.NeedsChoice = true;
            plan.Choices = heads.Select(ToChoice).ToList();
            plan.Warnings.Add(
                $"version {targetVersion} exists {heads.Count} times because of a depot reset — pick which blob you want");
            return plan;
        }

        plan.BlobCrc = head.CrcHex;

        try
        {
            await WalkAsync(depot, head, client, dataDir, plan, ct);
            plan.Mode = "smart";
            plan.ExtractArgs = $"{depotId} {targetVersion} --blobcrc {head.CrcHex}";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Could not follow the links, so fall back to pulling every candidate and let the
            // extractor pick with --blobcrc. Costs extra bandwidth but always has what it needs.
            plan.Files.Clear();
            BuildDirect(depot, targetVersion, plan);
            plan.Mode = "superset";
            plan.ExtractArgs = $"{depotId} {targetVersion} --blobcrc {head.CrcHex}";
            plan.Warnings.Add($"could not follow the blob chain ({ex.Message}); downloading every candidate instead");
        }

        return plan;
    }

    private static BlobChoice ToChoice(Entry b) =>
        new(b.CrcHex, b.Date == default ? "" : b.Date.ToString("yyyy-MM-dd HH:mm:ss"), b.ApproxSize);

    private static void ReportGaps(Depot depot, int targetVersion, ChainPlan plan)
    {
        var missingDats = depot.MissingDats.Where(v => v <= targetVersion).ToList();
        var missingBlobs = depot.MissingBlobs.Where(v => v <= targetVersion).ToList();

        if (missingDats.Count > 0)
            plan.Warnings.Add($"no dat for version(s) {Join(missingDats)} — the chain is incomplete and extraction will fail");
        if (missingBlobs.Count > 0)
            plan.Warnings.Add($"no blob for version(s) {Join(missingBlobs)} — the chain is incomplete and extraction will fail");

        var present = new HashSet<int>(depot.Dats.Select(e => e.Version));
        present.UnionWith(depot.Blobs.Select(e => e.Version));
        var absent = Enumerable.Range(0, targetVersion + 1).Where(v => !present.Contains(v)).ToList();
        if (absent.Count > 0)
            plan.Warnings.Add($"version(s) {Join(absent)} are absent from the archive entirely");
    }

    private static string Join(List<int> xs) =>
        xs.Count <= 12 ? string.Join(", ", xs) : string.Join(", ", xs.Take(12)) + $", … (+{xs.Count - 12})";

    /// <summary>Every dat and blob at or below the target version.</summary>
    private static void BuildDirect(Depot depot, int targetVersion, ChainPlan plan)
    {
        foreach (var e in depot.Blobs.Where(b => b.Version <= targetVersion))
            plan.Files.Add(new PlanFile(e, e.ApproxSize, false));
        foreach (var e in depot.Dats.Where(d => d.Version <= targetVersion))
            plan.Files.Add(new PlanFile(e, e.ApproxSize, false));

        plan.TotalBytes = plan.Files.Sum(f => Math.Max(0, f.Size));
        plan.TotalExact = false;
    }

    /// <summary>
    /// Follows the delta chain from the head blob down to version 0. Each blob names its parent's CRC
    /// (key 12) and the exact size of its own dat (key 13), which is also how the right dat is picked
    /// when several share a version number.
    /// </summary>
    private static async Task WalkAsync(
        Depot depot, Entry head, ArchiveClient client, string dataDir, ChainPlan plan, CancellationToken ct)
    {
        var blobsByVersion = depot.Blobs
            .GroupBy(b => b.Version)
            .ToDictionary(g => g.Key, g => g.ToList());
        var datsByVersion = depot.Dats
            .GroupBy(d => d.Version)
            .ToDictionary(g => g.Key, g => g.ToList());

        var chainBlobs = new List<Entry>();
        var chainDats = new List<PlanFile>();

        var current = head;
        int version = head.Version;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            chainBlobs.Add(current);
            var info = BlobFormat.Parse(await ReadBlobAsync(current, client, dataDir, ct));

            if (!datsByVersion.TryGetValue(version, out var datCandidates) || datCandidates.Count == 0)
                throw new InvalidDataException($"no dat for version {version}");

            Entry dat;
            long datSize;
            bool exact;

            if (datCandidates.Count == 1)
            {
                dat = datCandidates[0];
                datSize = info.DatSize is ulong s ? (long)s : dat.ApproxSize;
                exact = info.DatSize is not null;
            }
            else
            {
                if (info.DatSize is not ulong want)
                    throw new InvalidDataException($"blob at version {version} does not record a dat size");

                dat = await PickBySizeAsync(datCandidates, (long)want, client, ct)
                      ?? throw new InvalidDataException($"no dat of size {want} at version {version}");
                datSize = (long)want;
                exact = true;
            }

            chainDats.Add(new PlanFile(dat, datSize, exact));

            if (version == 0) break;

            if (info.ParentCrc is not uint parentCrc)
                throw new InvalidDataException($"blob at version {version} does not record a parent crc");

            if (!blobsByVersion.TryGetValue(version - 1, out var parents))
                throw new InvalidDataException($"no blob for version {version - 1}");

            var parent = parents.FirstOrDefault(b => b.Crc == parentCrc)
                         ?? throw new InvalidDataException(
                             $"no blob with crc {parentCrc:x8} at version {version - 1}");

            current = parent;
            version--;
        }

        chainBlobs.Reverse();
        chainDats.Reverse();

        foreach (var b in chainBlobs) plan.Files.Add(new PlanFile(b, b.ApproxSize, false));
        plan.Files.AddRange(chainDats);

        plan.TotalBytes = plan.Files.Sum(f => Math.Max(0, f.Size));
        plan.TotalExact = chainDats.All(f => f.SizeExact);
    }

    private static async Task<Entry?> PickBySizeAsync(
        List<Entry> candidates, long wantedSize, ArchiveClient client, CancellationToken ct)
    {
        foreach (var c in candidates)
        {
            long len = await client.GetLengthAsync(c, ct);
            if (len == wantedSize) return c;
        }
        return null;
    }

    /// <summary>Uses an already-downloaded blob when present, otherwise fetches it. Blobs are only kilobytes.</summary>
    private static async Task<byte[]> ReadBlobAsync(Entry blob, ArchiveClient client, string dataDir, CancellationToken ct)
    {
        string local = Path.Combine(dataDir, "blobs", blob.FileName);
        if (File.Exists(local))
        {
            try { return await File.ReadAllBytesAsync(local, ct); }
            catch (IOException) { /* fall through to the network */ }
        }
        return await client.GetBytesAsync(blob, ct);
    }
}
