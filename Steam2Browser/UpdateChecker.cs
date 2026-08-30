using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text.Json;

namespace Steam2Browser;

public sealed class UpdateStatus
{
    /// <summary>unknown | checking | empty | current | available | error</summary>
    public string State = "unknown";

    public string Message = "";
    public string Repo = "";
    public string RepoUrl = "";

    public DateTime? BuiltUtc;
    public string? ReleaseTag;
    public DateTime? ReleasePublishedUtc;
    public string? ReleaseUrl;

    public DateTime? CheckedUtc;
}

/// <summary>
/// Answers "is there a newer build than mine" against this fork's own GitHub releases.
///
/// Unlike upstream (no tags, no releases — compared by commit time instead), this fork publishes
/// a tagged release on every push to main, so the releases endpoint is the right thing to compare
/// against: the release's publish time versus the moment this binary was built, which the build
/// stamps into assembly metadata.
/// </summary>
public sealed class UpdateChecker(HttpClient http)
{
    public UpdateStatus Status { get; } = new();

    private readonly SemaphoreSlim _gate = new(1, 1);

    public void Initialise()
    {
        Status.Repo = Meta("UpdateRepo") ?? "Hmmnd0/steam2_downloader-Mac";
        Status.RepoUrl = $"https://github.com/{Status.Repo}";
        Status.BuiltUtc = BuildTime();
        Status.Message = "not checked yet";
    }

    /// <summary>Reads a value the build wrote into AssemblyMetadata.</summary>
    private static string? Meta(string key) =>
        Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == key)?.Value;

    /// <summary>
    /// When this binary was built. The stamp comes from the build; if it is somehow missing, the
    /// assembly file's own timestamp is close enough for a "newer than me" comparison.
    /// </summary>
    private static DateTime? BuildTime()
    {
        var stamped = Meta("BuildTimeUtc");
        if (!string.IsNullOrWhiteSpace(stamped) &&
            DateTime.TryParse(stamped, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
            return parsed;

        try
        {
            // Assembly.Location is empty in a single-file build, so ask the OS for the running image.
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
                return File.GetLastWriteTimeUtc(path);
        }
        catch
        {
            // Not worth failing the check over.
        }

        return null;
    }

    public async Task CheckAsync(CancellationToken ct = default)
    {
        if (!await _gate.WaitAsync(0, ct)) return;

        try
        {
            Status.State = "checking";
            Status.Message = "checking GitHub…";

            using var req = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://api.github.com/repos/{Status.Repo}/releases/latest");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

            using var resp = await http.SendAsync(req, ct);

            // No release published yet is a normal state here, not a failure.
            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                Status.State = "empty";
                Status.Message = "no release published yet";
                Status.ReleaseTag = null;
                Status.ReleasePublishedUtc = null;
                return;
            }

            if (resp.StatusCode == HttpStatusCode.Forbidden || (int)resp.StatusCode == 429)
            {
                string reset = resp.Headers.TryGetValues("x-ratelimit-reset", out var v)
                    ? ResetHint(v.FirstOrDefault())
                    : "";
                Status.State = "error";
                Status.Message = $"GitHub rate limit reached{reset}";
                return;
            }

            resp.EnsureSuccessStatusCode();

            await using var body = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(body, cancellationToken: ct);
            var release = doc.RootElement;

            Status.ReleaseTag = release.TryGetProperty("tag_name", out var tag) ? tag.GetString() : null;
            Status.ReleaseUrl = release.TryGetProperty("html_url", out var url) ? url.GetString() : Status.RepoUrl;

            DateTime? when = release.TryGetProperty("published_at", out var pub) &&
                              DateTime.TryParse(pub.GetString(), CultureInfo.InvariantCulture,
                                  DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed
                : null;

            Status.ReleasePublishedUtc = when;

            if (when is null)
            {
                Status.State = "error";
                Status.Message = "the latest release carries no usable publish date";
                return;
            }

            if (Status.BuiltUtc is not DateTime built)
            {
                Status.State = "available";
                Status.Message = $"release {Status.ReleaseTag} published {Ago(when.Value)}; " +
                                 "this build carries no timestamp to compare against";
                return;
            }

            if (when.Value > built)
            {
                Status.State = "available";
                Status.Message = $"update available — release {Status.ReleaseTag} is {Ago(when.Value)}, " +
                                 $"newer than this build from {Ago(built)}";
            }
            else
            {
                Status.State = "current";
                Status.Message = $"up to date — latest release {Status.ReleaseTag} predates this build";
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Status.State = "unknown";
            Status.Message = "check cancelled";
        }
        catch (Exception ex)
        {
            Status.State = "error";
            Status.Message = ex.Message;
        }
        finally
        {
            Status.CheckedUtc = DateTime.UtcNow;
            _gate.Release();
        }
    }

    private static string Ago(DateTime utc)
    {
        var span = DateTime.UtcNow - utc;
        if (span < TimeSpan.Zero) return "in the future";
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes} min ago";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours} h ago";
        if (span.TotalDays < 30) return $"{(int)span.TotalDays} d ago";
        return utc.ToString("yyyy-MM-dd");
    }

    private static string ResetHint(string? epochSeconds)
    {
        if (!long.TryParse(epochSeconds, out long seconds)) return "";
        var at = DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime();
        return $", resets at {at:HH:mm}";
    }
}
