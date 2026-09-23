using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EveDeck.Services;

// Finds and fetches updates straight from GitHub Releases -- never from evedeck.space.
//
// The site is a single VPS; GitHub is a far harder target, and v1.53.3 already removed one hole of
// exactly this shape (a site JSON field naming a URL that got downloaded and executed unchecked).
// So nothing here trusts a free-form URL from any response. The release JSON is only used to learn
// a version number and a SHA-256; every URL is rebuilt from the pinned repo and must match what
// GitHub reported character for character, and the installer only ever runs after its bytes hash
// to GitHub's own digest. Anything unexpected -- a missing digest, a renamed asset, a draft or
// prerelease, a redirect to a strange host -- fails closed: no install, at most a download page.
public sealed class UpdateCheckService
{
    internal const string Repo = "EveDeck/EveDeck";
    private const string LatestReleaseUrl = "https://api.github.com/repos/" + Repo + "/releases/latest";

    // Hosts GitHub serves release downloads from after the github.com redirect. The final host is
    // checked, but it is defence in depth: the SHA-256 is what actually gates execution.
    private static readonly HashSet<string> DownloadHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com",
    };

    // The installer is ~100 MB; anything wildly larger is not ours, so stop reading rather than fill
    // the disk.
    private const long MaxInstallerBytes = 400L * 1024 * 1024;

    private static readonly Regex TagRegex = new(@"^v(\d+\.\d+\.\d+)$", RegexOptions.CultureInvariant);
    private static readonly Regex DigestRegex = new(@"^sha256:([0-9a-f]{64})$", RegexOptions.CultureInvariant);

    private static readonly HttpClient Http = CreateClient(TimeSpan.FromSeconds(10), api: true);
    private static readonly HttpClient DownloadHttp = CreateClient(TimeSpan.FromMinutes(10), api: false);

    private readonly LogService? _log;

    public UpdateCheckService(LogService? log = null)
    {
        _log = log;
    }

    // InstallerUrl/InstallerSha256 are null when the release has no verifiable installer asset --
    // the caller then falls back to opening ReleasePageUrl, never to an unverified install.
    public record UpdateInfo(string Version, string ReleasePageUrl, string? InstallerUrl, string? InstallerSha256)
    {
        public bool CanAutoInstall => InstallerUrl is not null && InstallerSha256 is not null;
    }

    public async Task<UpdateInfo?> CheckAsync(string currentVersion)
    {
        try
        {
            var json = await Http.GetStringAsync(LatestReleaseUrl);
            return ParseRelease(json, currentVersion);
        }
        catch (Exception ex)
        {
            _log?.Warn($"Update check failed: {ex.Message}");
            return null;
        }
    }

    // Pure: GitHub "latest release" JSON -> UpdateInfo, or null when there is nothing newer or the
    // release does not look exactly like one of ours.
    internal static UpdateInfo? ParseRelease(string json, string currentVersion)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (GetBool(root, "draft") || GetBool(root, "prerelease")) return null;

        var tagMatch = TagRegex.Match(GetString(root, "tag_name") ?? "");
        if (!tagMatch.Success) return null;
        var version = tagMatch.Groups[1].Value;
        if (!IsNewer(version, currentVersion)) return null;

        // Rebuilt, not read: the page we open is always this repo's tag page.
        var pageUrl = $"https://github.com/{Repo}/releases/tag/v{version}";

        string? installerUrl = null, sha256 = null;
        var expectedName = $"EveDeck-Setup-v{version}.exe";
        var expectedUrl = $"https://github.com/{Repo}/releases/download/v{version}/{expectedName}";
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                if (GetString(asset, "name") != expectedName) continue;
                var digest = DigestRegex.Match(GetString(asset, "digest") ?? "");
                if (GetString(asset, "browser_download_url") == expectedUrl && digest.Success)
                {
                    installerUrl = expectedUrl;
                    sha256 = digest.Groups[1].Value;
                }
                break;
            }
        }

        return new UpdateInfo(version, pageUrl, installerUrl, sha256);
    }

    // Downloads the installer to a private temp folder and verifies it against the SHA-256 GitHub
    // reported. Returns the file still OPEN with FileShare.Read, so nothing can swap the bytes
    // between the hash check and the launch; the caller disposes it once the installer has started.
    // Throws on any failure, after deleting the partial file.
    public async Task<FileStream> DownloadVerifiedInstallerAsync(UpdateInfo update, CancellationToken ct = default)
    {
        if (!update.CanAutoInstall) throw new InvalidOperationException("This release has no verifiable installer.");

        var dir = Path.Combine(Path.GetTempPath(), "EveDeck-update");
        Directory.CreateDirectory(dir);
        foreach (var old in Directory.EnumerateFiles(dir))
        {
            try { File.Delete(old); } catch { } // a locked leftover is harmless; the new name differs
        }
        var path = Path.Combine(dir, $"EveDeck-Setup-v{update.Version}-{Guid.NewGuid():N}.exe");

        FileStream? file = null;
        try
        {
            using var response = await DownloadHttp.GetAsync(update.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            var finalUri = response.RequestMessage?.RequestUri;
            if (finalUri is null || finalUri.Scheme != Uri.UriSchemeHttps || !DownloadHosts.Contains(finalUri.Host))
                throw new InvalidOperationException($"Update download was redirected to an unexpected host: {finalUri?.Host}");
            if (response.Content.Headers.ContentLength > MaxInstallerBytes)
                throw new InvalidOperationException("Update download is larger than any EveDeck installer.");

            file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                await using var body = await response.Content.ReadAsStreamAsync(ct);
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await body.ReadAsync(buffer, ct)) > 0)
                {
                    total += read;
                    if (total > MaxInstallerBytes)
                        throw new InvalidOperationException("Update download is larger than any EveDeck installer.");
                    hash.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                }

                var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
                if (!CryptographicOperations.FixedTimeEquals(
                        System.Text.Encoding.ASCII.GetBytes(actual),
                        System.Text.Encoding.ASCII.GetBytes(update.InstallerSha256!)))
                    throw new InvalidOperationException($"Update installer failed its SHA-256 check (expected {update.InstallerSha256}, got {actual}).");
            }
            await file.FlushAsync(ct);
            file.Dispose();

            // Reopen read-only, sharing read only: the installer can start, nothing can rewrite it.
            file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return file;
        }
        catch
        {
            file?.Dispose();
            try { File.Delete(path); } catch { }
            throw;
        }
    }

    internal static bool IsNewer(string remote, string current) =>
        Version.TryParse(remote, out var r) && Version.TryParse(current, out var c) && r > c;

    private static HttpClient CreateClient(TimeSpan timeout, bool api)
    {
        var client = new HttpClient { Timeout = timeout };
        // GitHub's API rejects requests without a User-Agent.
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("EveDeck", "1.0"));
        if (api) client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private static string? GetString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool GetBool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
