using System.Text;
using System.Text.Json;

namespace Steam2Browser;

public sealed class LabelStatus
{
    /// <summary>off | loading | ready | error</summary>
    public string State = "off";

    public string Message = "";
    public string? Error;
    public int Count;
    public DateTime? FetchedUtc;
    public string Source = "";
}

/// <summary>
/// Curated depot names from the steam2-winfsp project, which someone has clearly put real work
/// into: it covers all 10 876 depots in this archive with proper product names rather than the
/// directory names a manifest yields.
///
/// This is the first choice for a depot's name. The manifest and Steam passes stay as the fallback
/// for anything it does not cover.
/// </summary>
public sealed class LabelSource(HttpClient http)
{
    private const string Owner = "dr3murr";
    private const string Repo = "steam2-winfsp";
    private const string PathInRepo = "data/depot_labels.tsv";
    private const string CacheFile = "depot_labels.tsv";

    /// <summary>
    /// raw.githubusercontent.com is unreachable on some networks while the API is fine, so the API
    /// is a genuine fallback rather than a nicety.
    /// </summary>
    private static readonly string RawUrl = $"https://raw.githubusercontent.com/{Owner}/{Repo}/main/{PathInRepo}";
    private static readonly string ApiUrl = $"https://api.github.com/repos/{Owner}/{Repo}/contents/{PathInRepo}";

    /// <summary>
    /// Per-request cap. The shared HttpClient has no timeout, and raw.githubusercontent.com is
    /// blocked on some networks — without this the startup fetch stalls before ever trying the API.
    /// </summary>
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(20);

    private Dictionary<int, string> _labels = new();

    public LabelStatus Status { get; } = new();

    public string? Get(int depot) => _labels.GetValueOrDefault(depot);
    public bool Has(int depot) => _labels.ContainsKey(depot);
    public int Count => _labels.Count;

    /// <summary>
    /// Reads the cached copy and nothing else. Local file access only, so it cannot hold up
    /// startup — the GitHub refresh is a separate call precisely because it can.
    /// </summary>
    public async Task LoadCachedAsync(string dataDir, CancellationToken ct = default)
    {
        string cachePath = Path.Combine(dataDir, CacheFile);
        if (!File.Exists(cachePath))
        {
            // The table is fetched from a repository that is nothing to do with this one, so a
            // build carries its own copy: it names 10 870 of the 10 876 depots, and without it a
            // first run shows a list of numbers.
            using var stream = typeof(LabelSource).Assembly
                .GetManifestResourceStream("Steam2Browser.depot_labels.tsv");
            if (stream is null) return;

            using var reader = new StreamReader(stream);
            Apply(await reader.ReadToEndAsync(ct), "built in");
            return;
        }

        try
        {
            Apply(await File.ReadAllTextAsync(cachePath, ct), "cache");
        }
        catch
        {
            // A damaged cache just means fetching again.
        }
    }

    /// <summary>Cached copy first, then a refresh from GitHub. Only for callers that are not on
    /// the startup path: the refresh can block for the whole fetch timeout.</summary>
    public async Task LoadAsync(string dataDir, CancellationToken ct = default)
    {
        await LoadCachedAsync(dataDir, ct);
        await RefreshAsync(dataDir, ct);
    }

    public async Task RefreshAsync(string dataDir, CancellationToken ct = default)
    {
        Status.State = _labels.Count > 0 ? "ready" : "loading";
        if (_labels.Count == 0) Status.Message = "fetching curated depot names";

        try
        {
            // The API endpoint answers in about a second, while raw.githubusercontent.com is blocked
            // outright on some networks and only fails once the timeout runs out. Ask the fast one
            // first and keep raw as the fallback for where the API is the blocked one.
            string? tsv = await FetchViaApiAsync(ct) ?? await FetchRawAsync(ct);
            if (tsv is null)
            {
                if (_labels.Count == 0)
                {
                    Status.State = "error";
                    Status.Error = "could not reach GitHub for the curated names";
                    Status.Message = Status.Error;
                }
                return;
            }

            Apply(tsv, Status.Source);
            Status.FetchedUtc = DateTime.UtcNow;

            Directory.CreateDirectory(dataDir);
            await File.WriteAllTextAsync(Path.Combine(dataDir, CacheFile), tsv, new UTF8Encoding(false), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Leave whatever the cache gave us.
        }
        catch (Exception ex)
        {
            if (_labels.Count == 0)
            {
                Status.State = "error";
                Status.Error = ex.Message;
                Status.Message = ex.Message;
            }
        }
    }

    private async Task<string?> FetchRawAsync(CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(FetchTimeout);

            using var resp = await http.GetAsync(RawUrl, timeout.Token);
            if (!resp.IsSuccessStatusCode) return null;
            Status.Source = "raw.githubusercontent.com";
            return await resp.Content.ReadAsStringAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    private async Task<string?> FetchViaApiAsync(CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, ApiUrl);
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(FetchTimeout);

            using var resp = await http.SendAsync(req, timeout.Token);
            if (!resp.IsSuccessStatusCode) return null;

            await using var body = await resp.Content.ReadAsStreamAsync(timeout.Token);
            using var doc = await JsonDocument.ParseAsync(body, cancellationToken: timeout.Token);

            if (!doc.RootElement.TryGetProperty("content", out var content)) return null;
            var encoded = content.GetString();
            if (string.IsNullOrEmpty(encoded)) return null;

            Status.Source = "api.github.com";
            return Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    /// <summary>Parses "depot &lt;tab&gt; label" lines.</summary>
    private void Apply(string tsv, string source)
    {
        var parsed = new Dictionary<int, string>(11000);

        foreach (var line in tsv.Split('\n'))
        {
            var row = line.AsSpan().TrimEnd('\r');
            if (row.IsEmpty) continue;

            int tab = row.IndexOf('\t');
            if (tab <= 0) continue;

            if (!int.TryParse(row[..tab], out int depot)) continue;

            var label = row[(tab + 1)..].Trim();
            if (label.IsEmpty) continue;

            var text = new string(label);
            if (IsPlaceholder(text)) continue;

            parsed[depot] = text;
        }

        if (parsed.Count == 0) return;

        _labels = parsed;
        Status.Count = parsed.Count;
        Status.State = "ready";
        Status.Source = source;
        Status.Message = $"{parsed.Count} curated names from {source}";
    }

    /// <summary>
    /// Whether a label says nothing at all. "Unknown / No Depot" covers 999 depots in the list and
    /// is worth less than what the manifest pass finds, so those are treated as uncovered and left
    /// to the fallback passes.
    ///
    /// Deliberately exact rather than a prefix test: 46 other labels read "Unknown (lag.exe)",
    /// "Unknown (redist)" and so on, and naming the executable is more than the manifest would give.
    /// </summary>
    public static bool IsPlaceholder(string label)
    {
        var text = label.Trim();

        return text.Equals("Unknown / No Depot", StringComparison.OrdinalIgnoreCase)
               || text.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
               || text.Equals("No Depot", StringComparison.OrdinalIgnoreCase)
               || text.Equals("N/A", StringComparison.OrdinalIgnoreCase)
               || text.Equals("None", StringComparison.OrdinalIgnoreCase)
               || text is "-" or "?" or "--";
    }

    /// <summary>
    /// Trims a label for display. Some depots are shared by hundreds of products and their label
    /// runs to tens of kilobytes, which no list can show.
    /// </summary>
    public static string Short(string label, int maxTitles = 3, int maxChars = 90)
    {
        var titles = label.Split(" / ", StringSplitOptions.RemoveEmptyEntries);

        string text = string.Join(" / ", titles.Take(maxTitles));
        if (text.Length > maxChars) text = text[..maxChars].TrimEnd() + "…";

        int extra = titles.Length - maxTitles;
        return extra > 0 ? $"{text} (+{extra})" : text;
    }
}
