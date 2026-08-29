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
    public DateTime? LatestCommitUtc;
    public string? CommitSha;
    public string? CommitShort;
    public string? CommitMessage;
    public string? CommitAuthor;
    public string? CommitUrl;

    public DateTime? CheckedUtc;
}

/// <summary>
/// Answers "is there a newer build than mine" against the upstream GitHub repo.
///
/// That repo carries no tags and no releases, so there is no version number to compare. The
/// comparison is by time instead: the newest commit on the default branch versus the moment this
/// binary was built, which the build stamps into assembly metadata.
///
/// An empty repository is a normal state here, not a failure — GitHub answers the commits endpoint
/// with 409 "Git Repository is empty" until the first push, and that is reported as such.
/// </summary>
public sealed class UpdateChecker(HttpClient http)
{
    public UpdateStatus Status { get; } = new();

    private readonly SemaphoreSlim _gate = new(1, 1);

    public void Initialise()
    {
        Status.Repo = Meta("UpdateRepo") ?? "extremebleem/steam2_downloader";
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
                $"https://api.github.com/repos/{Status.Repo}/commits?per_page=1");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

            using var resp = await http.SendAsync(req, ct);

            // The repository exists but nothing has been pushed to it yet.
            if (resp.StatusCode == HttpStatusCode.Conflict)
            {
                Status.State = "empty";
                Status.Message = "nothing published upstream yet — the repository has no commits";
                Status.LatestCommitUtc = null;
                Status.CommitSha = null;
                return;
            }

            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                Status.State = "error";
                Status.Message = $"repository {Status.Repo} not found (or private)";
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

            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
            {
                Status.State = "empty";
                Status.Message = "nothing published upstream yet — no commits on the default branch";
                return;
            }

            var head = doc.RootElement[0];
            var commit = head.GetProperty("commit");

            Status.CommitSha = head.TryGetProperty("sha", out var sha) ? sha.GetString() : null;
            Status.CommitShort = Status.CommitSha?[..Math.Min(7, Status.CommitSha.Length)];
            Status.CommitUrl = head.TryGetProperty("html_url", out var url) ? url.GetString() : Status.RepoUrl;

            Status.CommitMessage = commit.TryGetProperty("message", out var msg)
                ? FirstLine(msg.GetString())
                : null;

            Status.CommitAuthor = commit.TryGetProperty("author", out var author) &&
                                  author.TryGetProperty("name", out var name)
                ? name.GetString()
                : null;

            // Prefer the committer date: that is when the commit landed on the branch.
            DateTime? when = null;
            if (commit.TryGetProperty("committer", out var committer) &&
                committer.TryGetProperty("date", out var cdate) &&
                DateTime.TryParse(cdate.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsedC))
                when = parsedC;
            else if (commit.TryGetProperty("author", out var a2) &&
                     a2.TryGetProperty("date", out var adate) &&
                     DateTime.TryParse(adate.GetString(), CultureInfo.InvariantCulture,
                         DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsedA))
                when = parsedA;

            Status.LatestCommitUtc = when;

            if (when is null)
            {
                Status.State = "error";
                Status.Message = "the newest commit carries no usable date";
                return;
            }

            if (Status.BuiltUtc is not DateTime built)
            {
                Status.State = "available";
                Status.Message = $"upstream commit {Status.CommitShort} from {Ago(when.Value)}; " +
                                 "this build carries no timestamp to compare against";
                return;
            }

            if (when.Value > built)
            {
                Status.State = "available";
                Status.Message = $"update available — commit {Status.CommitShort} is {Ago(when.Value)}, " +
                                 $"newer than this build from {Ago(built)}";
            }
            else
            {
                Status.State = "current";
                Status.Message = $"up to date — newest commit {Status.CommitShort} predates this build";
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

    private static string FirstLine(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        int nl = s.IndexOf('\n');
        var line = nl >= 0 ? s[..nl] : s;
        return line.Length > 160 ? line[..160] + "…" : line.Trim();
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
