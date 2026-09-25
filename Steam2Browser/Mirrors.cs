using System.Diagnostics;

namespace Steam2Browser;

public sealed class Mirror
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string BaseUrl { get; init; }
    public required string Region { get; init; }

    /// <summary>Last measured throughput in bytes/sec, -1 if never tested.</summary>
    public double SpeedBps = -1;

    /// <summary>Last measured time to first byte in ms, -1 if never tested.</summary>
    public double TtfbMs = -1;

    public bool Reachable = true;
    public string? Error;
    public DateTime? TestedUtc;

    /// <summary>The BitTorrent swarm rather than an HTTP host; it has no base URL to speak of.</summary>
    public bool IsTorrent { get; init; }

    public string Url(string relPath) => $"{BaseUrl.TrimEnd('/')}/{relPath.TrimStart('/')}";
}

public static class Mirrors
{
    /// <summary>
    /// The swarm, and nothing else.
    ///
    /// There were three HTTP mirrors at de/ro/us.steam2.download serving byte-identical content.
    /// The site has closed and the domain no longer resolves at all, so every one of them is a
    /// connection failure now rather than a slow host worth racing. The archive survives only as the
    /// torrent, which carries the same 13.32 TB.
    ///
    /// The list is kept as a list, and the code that picks between entries is kept with it, because
    /// nothing says another host cannot appear later.
    /// </summary>
    public static readonly Mirror[] All =
    [
        new() { Id = "torrent", Name = "BitTorrent swarm", Region = "P2P", BaseUrl = "", IsTorrent = true },
    ];

    public static Mirror ById(string? id) =>
        All.FirstOrDefault(m => m.Id == id) ?? All[0];

    /// <summary>A small, known-present file used as the speed-test target.</summary>
    private const string ProbePath =
        "dats/0_0_65e371a6_c84cc42ee2cf40687201018166353dc6a841d1d337bfdef2f989ca0e79ead0cf.dat";

    private const int ProbeBytes = 4_000_000;

    /// <summary>
    /// The probe stops at whichever comes first, the byte cap or this. Without it a mirror running at
    /// 45 KB/s would hold the whole race — and everything queued behind it — for over a minute.
    /// </summary>
    private static readonly TimeSpan ProbeWindow = TimeSpan.FromSeconds(6);

    /// <summary>Absolute cap on one probe, connect and read together, so a stalled mirror cannot hang the race.</summary>
    private static readonly TimeSpan ProbeDeadline = TimeSpan.FromSeconds(12);

    /// <summary>Times a fixed-size ranged read from every mirror, in parallel.</summary>
    public static async Task TestAllAsync(HttpClient http, CancellationToken ct = default)
    {
        // The swarm has no URL to time, and its rate depends on peers rather than on a probe.
        await Task.WhenAll(All.Where(m => !m.IsTorrent).Select(m => TestAsync(http, m, ct)));
    }

    public static async Task TestAsync(HttpClient http, Mirror m, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        long total = 0;

        // A hard deadline on the whole probe. The shared HttpClient has no timeout, and checking the
        // window between reads does nothing if a read itself never returns — which is exactly how a
        // stalled mirror used to hang the startup sequence that waits on this race.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(ProbeDeadline);
        var probeCt = deadline.Token;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, m.Url(ProbePath));
            req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, ProbeBytes - 1);

            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, probeCt);
            resp.EnsureSuccessStatusCode();

            m.TtfbMs = sw.Elapsed.TotalMilliseconds;

            await using var stream = await resp.Content.ReadAsStreamAsync(probeCt);
            var buf = new byte[128 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buf, probeCt)) > 0)
            {
                total += read;
                if (sw.Elapsed >= ProbeWindow) break;
            }

            sw.Stop();
            m.SpeedBps = sw.Elapsed.TotalSeconds > 0 ? total / sw.Elapsed.TotalSeconds : 0;
            m.Reachable = true;
            m.Error = null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // Hit the deadline. Whatever arrived still measures the mirror; nothing at all means unusable.
            sw.Stop();
            m.SpeedBps = total > 0 && sw.Elapsed.TotalSeconds > 0 ? total / sw.Elapsed.TotalSeconds : -1;
            m.Reachable = total > 0;
            m.Error = total > 0 ? null : $"no data within {ProbeDeadline.TotalSeconds:0}s";
        }
        catch (Exception ex)
        {
            m.Reachable = false;
            m.SpeedBps = -1;
            m.Error = ex.Message;
        }
        finally
        {
            m.TestedUtc = DateTime.UtcNow;
        }
    }
}
