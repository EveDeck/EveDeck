using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace EveDeck.Services;

// Fetches recent release notes straight from the GitHub Releases API rather than bundling/caching
// anything locally -- by the time a build ships this feature, GitHub already holds the full release
// history, so "last 3 releases" is always real, current content with no separate data-seeding step.
public sealed class ChangelogService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private const string ReleasesUrl = "https://api.github.com/repos/EveDeck/EveDeck/releases?per_page=";

    static ChangelogService()
    {
        // GitHub's REST API rejects requests with no User-Agent.
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("EveDeck-App");
        Http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    private readonly LogService? _log;

    public ChangelogService(LogService? log = null)
    {
        _log = log;
    }

    // Summary is what a collapsed tile shows; Full is the whole changelog for that release with the
    // boilerplate stripped, so the dialog can expand in place instead of sending the user to a
    // browser. IsTruncated says whether the two actually differ, i.e. whether there is anything to
    // expand -- a short release should not offer a "Show more" that reveals nothing.
    public sealed record ReleaseNote(string Version, string Summary, string Full, bool IsTruncated,
                                     string HtmlUrl, DateTimeOffset? PublishedAt);

    public async Task<IReadOnlyList<ReleaseNote>> FetchRecentReleasesAsync(int count = 3)
    {
        try
        {
            var json = await Http.GetStringAsync(ReleasesUrl + count);
            using var doc = JsonDocument.Parse(json);
            var notes = new List<ReleaseNote>();
            foreach (var release in doc.RootElement.EnumerateArray())
            {
                if (release.TryGetProperty("draft", out var draftProp) && draftProp.GetBoolean()) continue;
                if (release.TryGetProperty("prerelease", out var preProp) && preProp.GetBoolean()) continue;

                var tag = release.GetProperty("tag_name").GetString() ?? "";
                var htmlUrl = release.GetProperty("html_url").GetString() ?? "";
                var body = release.TryGetProperty("body", out var bodyProp) ? bodyProp.GetString() ?? "" : "";
                DateTimeOffset? published = null;
                if (release.TryGetProperty("published_at", out var pubProp) && pubProp.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(pubProp.GetString(), out var dt))
                    published = dt;

                var full = CleanBody(body);
                var summary = Summarize(full);
                notes.Add(new ReleaseNote(tag.TrimStart('v'), summary, full,
                                          !string.Equals(summary, full, StringComparison.Ordinal), htmlUrl, published));
            }
            _log?.Info($"Changelog: fetched {notes.Count} recent release(s).");
            return notes;
        }
        catch (Exception ex)
        {
            _log?.Warn($"Changelog fetch failed: {ex.Message}");
            return Array.Empty<ReleaseNote>();
        }
    }

    // release.yml appends install instructions + a VirusTotal section below the actual changelog
    // bullets in every release body -- cut those off, leaving just the human-written notes. This is
    // what an EXPANDED tile shows in full.
    internal static string CleanBody(string body)
    {
        var cut = body;
        foreach (var marker in new[] { "**Two ways to install", "\n---", "**VirusTotal**" })
        {
            var idx = cut.IndexOf(marker, StringComparison.Ordinal);
            if (idx >= 0) cut = cut[..idx];
        }
        return cut.Trim();
    }

    // Cap the length for the COLLAPSED tile so one unusually long release can't blow out the dialog.
    // Returns the input unchanged when it already fits, which is what tells the UI there is nothing
    // to expand.
    internal static string Summarize(string cleanedBody)
    {
        const int maxLen = 480;
        if (cleanedBody.Length <= maxLen) return cleanedBody;

        var clipped = cleanedBody[..maxLen];
        var lastBreak = clipped.LastIndexOf('\n');
        if (lastBreak > 150) clipped = clipped[..lastBreak];
        return clipped.TrimEnd() + "\n...";
    }
}
