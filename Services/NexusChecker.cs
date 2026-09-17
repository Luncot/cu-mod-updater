using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using CUModUpdater.Models;

namespace CUModUpdater.Services;

/// <summary>
/// Nexus Mods API 检查器
/// API 文档: https://app.nexusmods.com/mm/wiki/Docs/publicapi.html
/// 需要 API Key（在 nexusmods.com -> 个人设置 -> API 中生成）
/// </summary>
public class NexusChecker : IDisposable
{
    private readonly HttpClient _client;
    private readonly string _gameDomain;
    private readonly bool _hasApiKey;

    public NexusChecker(string apiKey, string gameDomain = "casualtiesunknown")
    {
        _gameDomain = gameDomain;
        _hasApiKey = !string.IsNullOrEmpty(apiKey);

        _client = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
        });

        _client.Timeout = TimeSpan.FromSeconds(30);
        _client.DefaultRequestHeaders.Add("apikey", apiKey);
        _client.DefaultRequestHeaders.Add("User-Agent", "CU-ModUpdater/1.0.0 (Casualties Unknown Mod Updater)");
        _client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>是否配置了 API Key</summary>
    public bool HasApiKey => _hasApiKey;

    /// <summary>
    /// 获取 Mod 的最新版本信息
    /// GET /v1/games/{game_domain}/mods/{mod_id}.json
    /// </summary>
    public async Task<ReleaseInfo?> GetModInfoAsync(string modId)
    {
        var url = $"https://api.nexusmods.com/v1/games/{_gameDomain}/mods/{modId}.json";

        using var response = await _client.GetAsync(url);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;

        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new HttpRequestException("Nexus Mods API Key 无效或未配置。请在设置中填写有效的 API Key。");

        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            throw new HttpRequestException("Nexus Mods API 访问被拒绝。请检查 API Key 权限和游戏域名。");

        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var version = root.TryGetProperty("version", out var verEl)
            ? (verEl.ValueKind == JsonValueKind.String ? verEl.GetString() ?? "" : "")
            : "";

        string modName = root.TryGetProperty("name", out var nameEl)
            ? nameEl.GetString() ?? "" : "";

        string description = "";
        if (root.TryGetProperty("summary", out var sumEl) && sumEl.ValueKind == JsonValueKind.String)
            description = sumEl.GetString() ?? "";
        if (root.TryGetProperty("description", out var descEl) && descEl.ValueKind == JsonValueKind.String)
            description += "\n\n" + (descEl.GetString() ?? "");

        DateTime publishedAt = default;
        if (root.TryGetProperty("updated_at", out var updEl) && updEl.ValueKind == JsonValueKind.Number)
        {
            var ts = updEl.GetInt64();
            publishedAt = DateTimeOffset.FromUnixTimeSeconds(ts).DateTime;
        }

        // 获取文件列表来确定下载链接
        var downloadInfo = await GetLatestFileAsync(modId);

        return new ReleaseInfo
        {
            Version = version,
            DownloadUrl = downloadInfo?.DownloadUrl ?? "",
            AssetName = downloadInfo?.AssetName ?? "",
            ReleaseNotes = description.Trim(),
            PublishedAt = publishedAt,
        };
    }

    /// <summary>
    /// 获取 Mod 的最新文件信息和下载链接
    /// GET /v1/games/{game_domain}/mods/{mod_id}/files.json
    /// </summary>
    private async Task<ReleaseInfo?> GetLatestFileAsync(string modId)
    {
        var url = $"https://api.nexusmods.com/v1/games/{_gameDomain}/mods/{modId}/files.json";

        try
        {
            using var response = await _client.GetAsync(url);
            if (!response.IsSuccessStatusCode)
                return null;

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("files", out var filesEl) || filesEl.ValueKind != JsonValueKind.Array)
                return null;

            string? latestFileId = null;
            string? fileName = null;
            DateTime latestDate = DateTime.MinValue;

            foreach (var file in filesEl.EnumerateArray())
            {
                var name = file.TryGetProperty("file_name", out var fnEl)
                    ? fnEl.GetString() ?? "" : "";

                // 优先选择 .dll 文件
                if (!name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
                    !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    continue;

                var uploadedTime = DateTime.MinValue;
                if (file.TryGetProperty("uploaded_time", out var utEl))
                {
                    var timeStr = utEl.ValueKind == JsonValueKind.String
                        ? utEl.GetString() ?? "" : "";
                    if (!string.IsNullOrEmpty(timeStr) && timeStr.Contains('/'))
                        DateTime.TryParse(timeStr, out uploadedTime); // Nexus returns epoch-like
                }

                if (uploadedTime > latestDate || latestDate == DateTime.MinValue)
                {
                    latestDate = uploadedTime;
                    latestFileId = file.TryGetProperty("file_id", out var fidEl)
                        ? (fidEl.ValueKind == JsonValueKind.Number
                            ? fidEl.GetInt64().ToString()
                            : fidEl.GetString() ?? "") : "";
                    fileName = name;
                }
            }

            if (string.IsNullOrEmpty(latestFileId))
                return null;

            // 获取下载链接
            // GET /v1/games/{game_domain}/mods/{mod_id}/files/{file_id}/download_link.json
            var dlUrl = $"https://api.nexusmods.com/v1/games/{_gameDomain}/mods/{modId}/files/{latestFileId}/download_link.json";
            using var dlResponse = await _client.GetAsync(dlUrl);

            if (!dlResponse.IsSuccessStatusCode)
                return new ReleaseInfo { AssetName = fileName ?? "" };

            var dlJson = await dlResponse.Content.ReadAsStringAsync();
            using var dlDoc = JsonDocument.Parse(dlJson);
            var dlRoot = dlDoc.RootElement;

            // 返回的是一个数组，取第一个的 URI
            if (dlRoot.ValueKind == JsonValueKind.Array && dlRoot.GetArrayLength() > 0)
            {
                var first = dlRoot[0];
                var uri = first.TryGetProperty("URI", out var uriEl)
                    ? uriEl.GetString() ?? "" : "";
                if (!string.IsNullOrEmpty(uri))
                    return new ReleaseInfo { DownloadUrl = uri, AssetName = fileName ?? "" };
            }

            return new ReleaseInfo { AssetName = fileName ?? "" };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 验证 API Key 是否有效
    /// </summary>
    public async Task<bool> ValidateApiKeyAsync()
    {
        try
        {
            using var response = await _client
                .GetAsync("https://api.nexusmods.com/v1/users/validate.json");

            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        _client.Dispose();
    }
}
