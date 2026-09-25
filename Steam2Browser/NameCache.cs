using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Steam2Browser;

public sealed class NameRecord
{
    [JsonPropertyName("d")] public int Depot { get; set; }
    [JsonPropertyName("a")] public uint AppId { get; set; }
    [JsonPropertyName("v")] public uint VerId { get; set; }

    /// <summary>Name taken from the manifest inside the depot's blob.</summary>
    [JsonPropertyName("l")] public string Label { get; set; } = "";

    [JsonPropertyName("r")] public string[] Roots { get; set; } = [];
    [JsonPropertyName("f")] public uint Files { get; set; }
    [JsonPropertyName("e")] public string? Error { get; set; }

    /// <summary>Name from the Steam store, when the depot id happens to be a real app id.</summary>
    [JsonPropertyName("sn")] public string? SteamName { get; set; }

    [JsonPropertyName("sy")] public string? SteamType { get; set; }

    /// <summary>Set once the store has been asked about this depot, so misses are not retried every run.</summary>
    [JsonPropertyName("sc")] public bool SteamChecked { get; set; }

    /// <summary>
    /// Whether the depot's files are actually AES-encrypted. Null while unknown. Most depots missing
    /// from the key table are simply unencrypted and unpack fine without one.
    /// </summary>
    [JsonPropertyName("enc")] public bool? Encrypted { get; set; }

    /// <summary>What to show: the store name wins when there is one, otherwise the manifest name.</summary>
    [JsonIgnore]
    public string Display => string.IsNullOrEmpty(SteamName) ? Label : SteamName!;
}

public sealed class NameCacheStatus
{
    public bool Running;
    public int Total;
    public int Cached;
    public int Named;
    public int Failed;
    public int Current;
    public int Curated;
    public int Remaining;
    public string Message = "";
}

public sealed class SteamPassStatus
{
    public bool Running;
    public int Checked;
    public int Found;
    public int Remaining;
    public int Current;
    public string Message = "";
}

/// <summary>
/// Names depots in two passes.
///
/// Pass one reads the manifest embedded in each depot's blob. That is the reliable source: it needs
/// no key, no API and no guessing, and it yields the real top-level directory names.
///
/// Pass two then asks the Steam store about each depot id and overrides the name when it answers.
/// Expect few hits — these are Steam2 depot ids, which are not Steam3 app ids (a 41-depot sample
/// resolved exactly one). It runs slowly and sequentially to stay well inside the store's rate
/// limit, and records misses so they are asked about only once.
///
/// Every record is appended to names.jsonl as it lands, so either pass resumes where it stopped.
/// </summary>
public sealed class NameCache(ArchiveClient client, HttpClient http, LabelSource labels)
{
    private readonly ConcurrentDictionary<int, NameRecord> _byDepot = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private string _path = "";
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _steamCts;

    public NameCacheStatus Status { get; } = new();
    public SteamPassStatus Steam { get; } = new();

    public NameRecord? Get(int depot) => _byDepot.GetValueOrDefault(depot);
    public bool Has(int depot) => _byDepot.ContainsKey(depot);

    /// <summary>
    /// The name to show. Curated labels win: they are proper product names, where a manifest only
    /// yields directory names. Steam comes next, then the manifest.
    /// </summary>
    public string DisplayFor(int depot)
    {
        var curated = labels.Get(depot);
        if (!string.IsNullOrEmpty(curated)) return LabelSource.Short(curated);

        return Get(depot)?.Display ?? "";
    }

    /// <summary>Where the shown name came from: curated | steam | manifest, or null when unnamed.</summary>
    public string? SourceFor(int depot)
    {
        if (labels.Has(depot)) return "curated";

        var rec = Get(depot);
        if (rec is null) return null;
        if (!string.IsNullOrEmpty(rec.SteamName)) return "steam";
        return string.IsNullOrEmpty(rec.Label) ? null : "manifest";
    }

    /// <summary>True once the manifest pass has had its answer for this depot, hit or miss.</summary>
    private static bool HasManifest(NameRecord? r) =>
        r is not null && (!string.IsNullOrEmpty(r.Label) || r.Error is not null);

    /// <summary>
    /// Folds a manifest result into whatever is already stored, keeping any Steam name.
    /// The two passes run at the same time, so neither may overwrite the other's fields.
    /// </summary>
    private NameRecord MergeManifest(int depot, NameRecord fresh) =>
        _byDepot.AddOrUpdate(depot, fresh, (_, existing) =>
        {
            existing.AppId = fresh.AppId;
            existing.VerId = fresh.VerId;
            existing.Files = fresh.Files;
            existing.Roots = fresh.Roots;
            existing.Label = fresh.Label;
            existing.Error = fresh.Error;
            existing.Encrypted = fresh.Encrypted;
            return existing;
        });

    /// <summary>Folds a Steam answer into whatever is already stored, keeping the manifest name.</summary>
    private NameRecord MergeSteam(int depot, string? name, string? type) =>
        _byDepot.AddOrUpdate(
            depot,
            new NameRecord { Depot = depot, SteamChecked = true, SteamName = name, SteamType = type },
            (_, existing) =>
            {
                existing.SteamChecked = true;
                existing.SteamName = name;
                existing.SteamType = type;
                return existing;
            });

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>No BOM: Encoding.UTF8 would stamp one on the first append and break that line for any strict JSON reader.</summary>
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public void Load(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        _path = Path.Combine(dataDir, "names.jsonl");

        // Falls back to the copy built into the app. Working these names out means reading the
        // manifest inside each depot's blob, which needed the mirrors; with those gone a fresh
        // install would otherwise start with no names at all and no way to earn any.
        IEnumerable<string>? lines = File.Exists(_path) ? File.ReadLines(_path) : EmbeddedLines();
        if (lines is null) return;

        foreach (var raw in lines)
        {
            // Older caches were written with a BOM on the first line; drop it before parsing.
            var line = raw.TrimStart('﻿');
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                // Later lines for the same depot are newer, so they win.
                var rec = JsonSerializer.Deserialize<NameRecord>(line, Json);
                if (rec is not null) _byDepot[rec.Depot] = rec;
            }
            catch
            {
                // A half-written final line from a killed run is expected; skip it.
            }
        }

        Recount();
        Status.Message = $"{Status.Cached} depots in cache";
    }

    /// <summary>The built-in name cache, or null if this build carries none.</summary>
    private static IEnumerable<string>? EmbeddedLines()
    {
        var stream = typeof(NameCache).Assembly
            .GetManifestResourceStream("Steam2Browser.names.jsonl");
        if (stream is null) return null;

        return Read(stream);

        static IEnumerable<string> Read(Stream s)
        {
            using (s)
            using (var reader = new StreamReader(s))
            {
                while (reader.ReadLine() is { } line) yield return line;
            }
        }
    }

    private void Recount()
    {
        Status.Cached = _byDepot.Count;
        Status.Curated = labels.Count;

        // A depot is named whatever the source. Counting only our own cache understated it badly
        // once the curated list arrived: it covers most of the archive and never touches this cache.
        Status.Named = labels.Count
                       + _byDepot.Values.Count(r => !labels.Has(r.Depot) && !string.IsNullOrEmpty(r.Display));

        Status.Failed = _byDepot.Values.Count(r => r.Error is not null);

        Steam.Checked = _byDepot.Values.Count(r => r.SteamChecked);
        Steam.Found = _byDepot.Values.Count(r => !string.IsNullOrEmpty(r.SteamName));
    }

    /// <summary>Re-reads the derived counters, for when the curated list lands after startup.</summary>
    public void Refresh() => Recount();

    public void Stop() => _cts?.Cancel();
    public void StopSteam() => _steamCts?.Cancel();

    // ---------------- pass one: manifests from the mirrors ----------------

    /// <summary>
    /// Walks depots whose manifest has not been read yet. Safe to call again; a second call is
    /// ignored while one runs. This runs alongside the Steam pass rather than before it, so names
    /// start appearing from both sources straight away.
    /// </summary>
    public void Start(Catalog catalog, int concurrency = 12, bool retryFailed = false)
    {
        if (Status.Running) return;

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        Status.Running = true;

        _ = Task.Run(() => SweepAsync(catalog, concurrency, retryFailed, _cts.Token));
    }

    private async Task SweepAsync(Catalog catalog, int concurrency, bool retryFailed, CancellationToken ct)
    {
        try
        {
            // Keyed on whether the manifest itself was read, not on the depot merely being present:
            // the Steam pass may already have created a record for it.
            // Skip anything the curated list already names — that is the whole archive today, so
            // this normally does no network work at all.
            var todo = catalog.Ordered
                .Where(d => d.Blobs.Count > 0)
                .Where(d => !labels.Has(d.Id))
                .Where(d =>
                {
                    var r = _byDepot.GetValueOrDefault(d.Id);
                    return !HasManifest(r) || (retryFailed && r!.Error is not null);
                })
                .ToList();

            Status.Total = catalog.Ordered.Count;
            Status.Remaining = todo.Count;
            Status.Message = $"{todo.Count} depots left to name";

            using var gate = new SemaphoreSlim(Math.Max(1, concurrency));

            await Task.WhenAll(todo.Select(async depot =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    Status.Current = depot.Id;
                    var fresh = await ReadOneAsync(depot, ct);
                    var merged = MergeManifest(depot.Id, fresh);
                    await AppendAsync(merged, ct);
                    Interlocked.Decrement(ref Status.Remaining);
                    Recount();
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
                catch
                {
                    // One depot failing must not end the sweep.
                }
                finally
                {
                    gate.Release();
                }
            }));

            Status.Message = ct.IsCancellationRequested
                ? $"stopped — {Status.Named} named"
                : $"done — {Status.Named} named, {Status.Failed} without a manifest";
        }
        catch (OperationCanceledException)
        {
            Status.Message = $"stopped — {Status.Named} named";
        }
        catch (Exception ex)
        {
            Status.Message = $"sweep failed: {ex.Message}";
        }
        finally
        {
            Status.Running = false;
            Status.Current = 0;
            Status.Remaining = 0;
        }
    }

    private async Task<NameRecord> ReadOneAsync(Depot depot, CancellationToken ct)
    {
        // The newest blob has the most complete manifest.
        var blob = depot.Blobs[^1];

        try
        {
            byte[] bytes = await client.GetBytesAsync(blob, ct);

            // The same blob answers both questions, so read the encryption flag while it is here.
            bool? encrypted = ChecksumTable.AnyEncrypted(bytes);
            var info = ManifestFormat.FromBlob(bytes);

            if (info is null)
                return new NameRecord { Depot = depot.Id, Error = "no manifest in blob", Encrypted = encrypted };

            return new NameRecord
            {
                Depot = depot.Id,
                AppId = info.AppId,
                VerId = info.VerId,
                Files = info.FileCount,
                Roots = info.Roots.ToArray(),
                Label = ManifestFormat.Label(info.Roots),
                Encrypted = encrypted,
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new NameRecord { Depot = depot.Id, Error = ex.Message };
        }
    }

    // ---------------- pass two: the Steam store, overriding when it answers ----------------

    /// <summary>Gap between store requests. Steam allows roughly 200 per five minutes per address.</summary>
    private static readonly TimeSpan SteamDelay = TimeSpan.FromMilliseconds(1600);

    public void StartSteam(Catalog catalog, bool recheckMisses = false)
    {
        if (Steam.Running) return;

        _steamCts?.Dispose();
        _steamCts = new CancellationTokenSource();
        Steam.Running = true;

        _ = Task.Run(() => SteamPassAsync(catalog, recheckMisses, _steamCts.Token));
    }

    private async Task SteamPassAsync(Catalog catalog, bool recheckMisses, CancellationToken ct)
    {
        try
        {
            var todo = catalog.Ordered
                .Where(d => !labels.Has(d.Id))
                .Where(d => !_byDepot.TryGetValue(d.Id, out var r) || recheckMisses || !r.SteamChecked)
                .Select(d => d.Id)
                .ToList();

            Steam.Remaining = todo.Count;
            Steam.Message = $"{todo.Count} depots to ask Steam about";

            foreach (int depot in todo)
            {
                ct.ThrowIfCancellationRequested();
                Steam.Current = depot;

                var (name, type, ok) = await AskSteamAsync(depot, ct);

                if (ok)
                {
                    var merged = MergeSteam(depot, name, type);
                    await AppendAsync(merged, ct);
                    Recount();
                }

                Steam.Remaining--;
                await Task.Delay(SteamDelay, ct);
            }

            Steam.Message = $"done — Steam named {Steam.Found} of {Steam.Checked} checked";
        }
        catch (OperationCanceledException)
        {
            Steam.Message = $"stopped — Steam named {Steam.Found} of {Steam.Checked} checked";
        }
        catch (Exception ex)
        {
            Steam.Message = $"Steam pass failed: {ex.Message}";
        }
        finally
        {
            Steam.Running = false;
            Steam.Current = 0;
        }
    }

    /// <summary>
    /// Asks the store about one id. Returns ok=false when the answer was inconclusive (rate limit or
    /// network trouble) so the depot stays unchecked and is tried again on a later run.
    /// </summary>
    private async Task<(string? Name, string? Type, bool Ok)> AskSteamAsync(int depot, CancellationToken ct)
    {
        var url = $"https://store.steampowered.com/api/appdetails?appids={depot}&filters=basic";

        try
        {
            using var resp = await http.GetAsync(url, ct);

            if ((int)resp.StatusCode == 429 || resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                Steam.Message = "rate limited by Steam, backing off for a minute";
                await Task.Delay(TimeSpan.FromMinutes(1), ct);
                return (null, null, false);
            }

            if (!resp.IsSuccessStatusCode) return (null, null, false);

            await using var body = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(body, cancellationToken: ct);

            if (doc.RootElement.ValueKind != JsonValueKind.Object) return (null, null, false);

            foreach (var entry in doc.RootElement.EnumerateObject())
            {
                bool success = entry.Value.TryGetProperty("success", out var s) &&
                               (s.ValueKind == JsonValueKind.True ||
                                (s.ValueKind == JsonValueKind.Number && s.GetInt32() == 1));

                // A definite "no such app" still counts as checked, so it is not asked again.
                if (!success || !entry.Value.TryGetProperty("data", out var data))
                    return (null, null, true);

                string? name = data.TryGetProperty("name", out var n) ? n.GetString() : null;
                string? type = data.TryGetProperty("type", out var t) ? t.GetString() : null;
                return (string.IsNullOrWhiteSpace(name) ? null : name, type, true);
            }

            return (null, null, true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return (null, null, false);
        }
    }

    // ---------------- persistence ----------------

    private async Task AppendAsync(NameRecord rec, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_path)) return;

        string line = JsonSerializer.Serialize(rec, Json) + "\n";

        await _writeLock.WaitAsync(ct);
        try
        {
            await File.AppendAllTextAsync(_path, line, Utf8NoBom, ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
