using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LacertMCP.Cli.Update
{
    /// <summary>One downloadable file attached to a release.</summary>
    public sealed class ReleaseAsset
    {
        public ReleaseAsset(string name, string downloadUrl, long size, string? sha256)
        {
            Name = name;
            DownloadUrl = downloadUrl;
            Size = size;
            Sha256 = sha256;
        }

        public string Name { get; }
        public string DownloadUrl { get; }
        public long Size { get; }

        /// <summary>Lower-case hex SHA-256 when the host publishes one (GitHub's <c>digest</c>), else null.</summary>
        public string? Sha256 { get; }
    }

    public sealed class ReleaseInfo
    {
        public ReleaseInfo(string tag, IReadOnlyList<ReleaseAsset> assets, string? htmlUrl = null)
        {
            Tag = tag;
            Assets = assets;
            HtmlUrl = htmlUrl;
        }

        public string Tag { get; }
        public IReadOnlyList<ReleaseAsset> Assets { get; }
        public string? HtmlUrl { get; }
    }

    /// <summary>Where releases come from. Seam so tests never touch the network.</summary>
    public interface IReleaseSource
    {
        /// <summary><paramref name="tag"/> null = the latest non-prerelease, non-draft release.</summary>
        Task<ReleaseInfo> GetReleaseAsync(string repo, string? tag, CancellationToken ct);

        Task DownloadAsync(ReleaseAsset asset, string destinationPath, CancellationToken ct);
    }

    /// <summary>Raised for an expected, operator-explainable update failure (exit 1, no stack).</summary>
    public class UpdateException : Exception
    {
        public UpdateException(string message) : base(message) { }
        public UpdateException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>
    /// The always-on launcher or its server can't be stopped by this user (it was
    /// started elevated). Raised before the swap; the update then changes nothing
    /// and starts no second launcher.
    /// </summary>
    public sealed class ServiceStopException : UpdateException
    {
        public ServiceStopException(string message) : base(message) { }
        public ServiceStopException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>
    /// GitHub Releases over the public REST API. Unauthenticated by default
    /// (the repo is public; 60 requests/hour is plenty for an operator CLI).
    /// When <c>GITHUB_TOKEN</c> or <c>GH_TOKEN</c> is set it is sent to
    /// <c>api.github.com</c> only — never to the asset download, which
    /// redirects to a different host.
    /// </summary>
    public sealed class GitHubReleaseSource : IReleaseSource, IDisposable
    {
        private readonly HttpClient _http;

        public GitHubReleaseSource(string userAgent)
        {
            _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        }

        public void Dispose() => _http.Dispose();

        public async Task<ReleaseInfo> GetReleaseAsync(string repo, string? tag, CancellationToken ct)
        {
            var url = tag == null
                ? $"https://api.github.com/repos/{repo}/releases/latest"
                : $"https://api.github.com/repos/{repo}/releases/tags/{Uri.EscapeDataString(tag)}";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
            if (string.IsNullOrWhiteSpace(token)) token = Environment.GetEnvironmentVariable("GH_TOKEN");
            if (!string.IsNullOrWhiteSpace(token))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token!.Trim());
            }

            HttpResponseMessage resp;
            try
            {
                resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new UpdateException($"Could not reach GitHub ({ex.GetBaseException().Message}). Check the network and retry.", ex);
            }

            using (resp)
            {
                if (resp.StatusCode == HttpStatusCode.NotFound)
                {
                    throw new UpdateException(tag == null
                        ? $"No published release found for {repo}."
                        : $"Release '{tag}' not found in {repo}.");
                }
                if ((int)resp.StatusCode == 403 || (int)resp.StatusCode == 429)
                {
                    throw new UpdateException(
                        $"GitHub refused the request (HTTP {(int)resp.StatusCode}) — likely the unauthenticated rate limit. " +
                        "Wait an hour or set GITHUB_TOKEN, then retry.");
                }
                if (!resp.IsSuccessStatusCode)
                {
                    throw new UpdateException($"GitHub returned HTTP {(int)resp.StatusCode} for {url}.");
                }
                var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                return Parse(body);
            }
        }

        /// <summary>Parses a GitHub <c>release</c> JSON object. Internal-visible for tests.</summary>
        public static ReleaseInfo Parse(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var tag = root.TryGetProperty("tag_name", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString()!
                : throw new UpdateException("GitHub release JSON has no tag_name.");
            var html = root.TryGetProperty("html_url", out var h) && h.ValueKind == JsonValueKind.String ? h.GetString() : null;

            var assets = new List<ReleaseAsset>();
            if (root.TryGetProperty("assets", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in arr.EnumerateArray())
                {
                    var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    var url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    var size = a.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : -1;
                    string? sha = null;
                    if (a.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String)
                    {
                        var digest = d.GetString() ?? string.Empty;
                        if (digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                        {
                            sha = digest.Substring("sha256:".Length).Trim().ToLowerInvariant();
                        }
                    }
                    if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(url))
                    {
                        assets.Add(new ReleaseAsset(name!, url!, size, sha));
                    }
                }
            }
            return new ReleaseInfo(tag, assets, html);
        }

        public async Task DownloadAsync(ReleaseAsset asset, string destinationPath, CancellationToken ct)
        {
            var uri = new Uri(asset.DownloadUrl);
            if (uri.Scheme != Uri.UriSchemeHttps)
            {
                throw new UpdateException($"Refusing non-HTTPS download URL: {asset.DownloadUrl}");
            }
            try
            {
                using var resp = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    throw new UpdateException($"Download of {asset.Name} failed: HTTP {(int)resp.StatusCode}.");
                }
                using var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
                using var dst = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await src.CopyToAsync(dst, 81920, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new UpdateException($"Download of {asset.Name} failed ({ex.GetBaseException().Message}).", ex);
            }
        }
    }
}
