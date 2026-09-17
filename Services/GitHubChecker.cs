using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using CUModUpdater.Models;

namespace CUModUpdater.Services;

/// <summary>
/// GitHub Releases API 检查器
/// API 文档: https://docs.github.com/en/rest/releases/releases
/// </summary>
public class GitHubChecker : IDisposable
{
    private readonly HttpClient _client;
    private readonly string? _token;

    public GitHubChecker(string? token = null)
    {
        _token = token;
        _client = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
        });

        _client.Timeout = TimeSpan.FromSeconds(30);
        _client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("CU-ModUpdater", "1.0.0"));
        _client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        if (!string.IsNullOrEmpty(token))
        {
            _client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token);
        }
    }

    /// <summary>
    /// 获取仓库的最新 Release
    /// </summary>
    public async Task<ReleaseInfo?> GetLatestReleaseAsync(string owner, string repo)
    {
        var url = $"https://api.github.com/repos/{owner}/{repo}/releases/latest";

        using var response = await _client.GetAsync(url);

        // 404 = 没有 releases。
        // 但**很多 mod 只打 tag 不发 Release**（实例：Orsoniks/scavgame-locale
        // 即 i18n Auto Updater 的更新源，只有 v7.7.9 这样的 tag，没有 Release），
        // 直接返回 null 会让这个 mod 永远显示「? 未知」。回退到 tags 接口。
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return await GetLatestTagAsync(owner, repo);

        // 401：没配 Token 也会出现 —— GitHub 对匿名限额打满 / IP 临时受限的
        // 请求有时回 401 有时回 403。别甩底层异常，说清楚是限额问题且会自愈。
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new HttpRequestException(
                "GitHub 拒绝了请求（401）。匿名请求限额为 60 次/小时，用尽后约 1 小时自动恢复；" +
                "经常遇到可在设置里配置 GitHub Token（5000 次/小时）。");

        // 403 = 速率限制
        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            var remaining = response.Headers.Contains("X-RateLimit-Remaining")
                ? response.Headers.GetValues("X-RateLimit-Remaining").FirstOrDefault()
                : "unknown";
            throw new HttpRequestException(
                $"GitHub API 速率限制。剩余请求: {remaining}\n" +
                "建议在设置中配置 GitHub Token 以提高限额 (60 -> 5000/h)。");
        }

        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // 提取版本号 (tag_name，如 "v1.0.0" -> "1.0.0")
        var tagName = root.GetProperty("tag_name").GetString() ?? "";
        var version = tagName.TrimStart('v', 'V');

        // 提取发布日期
        DateTime publishedAt = default;
        if (root.TryGetProperty("published_at", out var pubEl) &&
            pubEl.ValueKind == JsonValueKind.String &&
            DateTime.TryParse(pubEl.GetString(), out var dt))
            publishedAt = dt;

        // 提取 Release Notes
        string releaseNotes = "";
        if (root.TryGetProperty("body", out var bodyEl) && bodyEl.ValueKind == JsonValueKind.String)
            releaseNotes = bodyEl.GetString() ?? "";

        // 提取下载资源
        string? downloadUrl = null;
        string? assetName = null;

        if (root.TryGetProperty("assets", out var assetsEl) && assetsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assetsEl.EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                var url2 = asset.GetProperty("browser_download_url").GetString() ?? "";

                // 优先选择 .dll 文件
                if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    downloadUrl = url2;
                    assetName = name;
                    break;
                }
                // 回退到第一个资源
                downloadUrl ??= url2;
                assetName ??= name;
            }
        }

        // 如果没有 assets，可能 tag 本身就是下载点（用 zipball）
        downloadUrl ??= root.TryGetProperty("zipball_url", out var zbEl)
            ? zbEl.GetString() ?? "" : "";

        return new ReleaseInfo
        {
            Version = version,
            DownloadUrl = downloadUrl,
            AssetName = assetName ?? "",
            ReleaseNotes = releaseNotes,
            PublishedAt = publishedAt,
        };
    }

    /// <summary>
    /// 没有 Release 时的兜底：取仓库最新的 tag。
    /// tag 没有「发布日期/说明/附件」，版本号取 tag 名（去 v 前缀）。
    /// </summary>
    private async Task<ReleaseInfo?> GetLatestTagAsync(string owner, string repo)
    {
        try
        {
            var url = $"https://api.github.com/repos/{owner}/{repo}/tags";
            using var response = await _client.GetAsync(url);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
                response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                throw new HttpRequestException(
                    "GitHub 拒绝了请求。匿名请求限额为 60 次/小时，用尽后约 1 小时自动恢复；" +
                    "经常遇到可在设置里配置 GitHub Token（5000 次/小时）。");
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array ||
                doc.RootElement.GetArrayLength() == 0)
                return null;

            // tags 接口按创建时间倒序，第一个就是最新
            var first = doc.RootElement[0];
            var tagName = first.GetProperty("name").GetString() ?? "";
            if (string.IsNullOrEmpty(tagName)) return null;

            return new ReleaseInfo
            {
                Version = tagName.TrimStart('v', 'V'),
                DownloadUrl = first.TryGetProperty("zipball_url", out var zb)
                    ? zb.GetString() ?? "" : "",
                AssetName = "",
                ReleaseNotes = $"（此仓库以 tag 发布，无正式 Release 说明）最新 tag: {tagName}",
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 下载文件到指定路径（支持进度回调）
    /// </summary>
    public async Task DownloadFileAsync(string url, string destPath,
        IProgress<(long received, long total)>? progress = null)
    {
        using var response = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? -1;
        using var contentStream = await response.Content.ReadAsStreamAsync();
        using var fileStream = new FileStream(destPath, FileMode.Create, FileAccess.Write,
            FileShare.None, 8192, useAsync: true);

        var buffer = new byte[81920];
        long received = 0;
        int bytesRead;

        while ((bytesRead = await contentStream.ReadAsync(buffer)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));
            received += bytesRead;
            progress?.Report((received, totalBytes));
        }
    }

    public void Dispose()
    {
        _client.Dispose();
    }
}
