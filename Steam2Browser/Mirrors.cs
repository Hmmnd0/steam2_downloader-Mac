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

    public string Url(string relPath) => $"{BaseUrl.TrimEnd('/')}/{relPath.TrimStart('/')}";
}

public static class Mirrors
{
    /// <summary>
    /// All three serve byte-identical content (same ETag / Content-Length / Last-Modified).
    /// Schemes differ and are not interchangeable: de answers only on https, ro and us only on http.
    /// </summary>
    public static readonly Mirror[] All =
    [
        new() { Id = "de", Name = "Germany", Region = "EU", BaseUrl = "https://de.steam2.download" },
        new() { Id = "ro", Name = "Romania", Region = "EU", BaseUrl = "http://ro.steam2.download" },
        new() { Id = "us", Name = "United States", Region = "NA", BaseUrl = "http://us.steam2.download" },
    ];

    public static Mirror ById(string? id) =>
        All.FirstOrDefault(m => m.Id == id) ?? All[0];

    /// <summary>A small, known-present file used as the speed-test target.</summary>
    private const string ProbePath =
        "dats/0_0_65e371a6_c84cc42ee2cf40687201018166353dc6a841d1d337bfdef2f989ca0e79ead0cf.dat";

    private const int ProbeBytes = 4 * 1024 * 1024;

    /// <summary>Times a fixed-size ranged read from every mirror, in parallel.</summary>
    public static async Task TestAllAsync(HttpClient http, CancellationToken ct = default)
    {
        await Task.WhenAll(All.Select(m => TestAsync(http, m, ct)));
    }

    public static async Task TestAsync(HttpClient http, Mirror m, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, m.Url(ProbePath));
            req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, ProbeBytes - 1);

            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            m.TtfbMs = sw.Elapsed.TotalMilliseconds;

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            var buf = new byte[128 * 1024];
            long total = 0;
            int read;
            while ((read = await stream.ReadAsync(buf, ct)) > 0) total += read;

            sw.Stop();
            m.SpeedBps = sw.Elapsed.TotalSeconds > 0 ? total / sw.Elapsed.TotalSeconds : 0;
            m.Reachable = true;
            m.Error = null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
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
