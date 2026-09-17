using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using CUModUpdater.Models;

namespace CUModUpdater.Services;

/// <summary>
/// Nexus Mods 浏览器 - 查询游戏可用 Mod 列表
/// API 文档: https://app.nexusmods.com/mm/wiki/Docs/publicapi.html
/// </summary>
public class NexusBrowser : IDisposable
{
    /// <summary>Nexus 的浏览排序方式（API 没有通用的 mods.json，只有这三个榜单）</summary>
    public enum NexusSort
    {
        /// <summary>最近更新（mods/latest_updated.json）——更新器最常用</summary>
        LatestUpdated,
        /// <summary>最新发布（mods/latest_added.json）</summary>
        LatestAdded,
        /// <summary>热门（mods/trending.json）</summary>
        Trending,
    }

    private readonly HttpClient _client;
    private readonly string _gameDomain;
    private readonly bool _hasKey;

    public Action<string>? OnLog { get; set; }
    public bool HasApiKey => _hasKey;

    /// <summary>当前使用的游戏域名（构造时已做过兜底）</summary>
    public string GameDomain => _gameDomain;

    /// <summary>
    /// ⚠️ 游戏域名必须是 **scavprototype**（N 网 URL 里的那一段）。
    /// 这个游戏的名字叫 Casualties Unknown，但域名不是它的名字——
    /// 曾误用 "casualtiesunknown"，导致所有 N 网 API 请求 404。
    /// </summary>
    public const string DefaultGameDomain = "scavprototype";

    public NexusBrowser(string apiKey, string gameDomain = DefaultGameDomain)
    {
        _gameDomain = string.IsNullOrWhiteSpace(gameDomain) ? DefaultGameDomain : gameDomain;
        _hasKey = !string.IsNullOrEmpty(apiKey);

        _client = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
        });
        _client.Timeout = TimeSpan.FromSeconds(30);
        _client.DefaultRequestHeaders.Add("apikey", apiKey);
        _client.DefaultRequestHeaders.Add("User-Agent", "CU-ModUpdater/1.0.0");
        _client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>
    /// 浏览游戏 Mod 列表。
    ///
    /// ⚠️ Nexus v1 API **没有** `games/{game}/mods.json` 这个端点（原本用的是它，
    /// 两个域名都返回 404——所以「N 网 mod 访问不了」的真因是端点写错了，
    /// 不是没配 API Key）。可用的浏览端点是这三个榜单：
    ///   mods/latest_updated.json  最近更新
    ///   mods/latest_added.json    最新发布
    ///   mods/trending.json        热门
    /// </summary>
    public async Task<List<ModListing>> BrowseAsync(NexusSort sort = NexusSort.LatestUpdated, int count = 50)
    {
        if (!_hasKey)
            throw new InvalidOperationException("未配置 Nexus API Key，无法浏览 N 网 Mod。");

        var seg = sort switch
        {
            NexusSort.LatestAdded => "latest_added",
            NexusSort.Trending => "trending",
            _ => "latest_updated",
        };
        var url = $"https://api.nexusmods.com/v1/games/{_gameDomain}/mods/{seg}.json?count={count}";

        OnLog?.Invoke($"正在从 Nexus Mods 获取 Mod 列表（{seg}）...");

        using var resp = await _client.GetAsync(url);
        if (!resp.IsSuccessStatusCode)
        {
            if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                throw new InvalidOperationException("Nexus API Key 无效，请检查设置。");
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                throw new InvalidOperationException(
                    $"Nexus 找不到游戏域名“{_gameDomain}”。请在设置里把 Nexus 游戏域名改成 scavprototype。");
            throw new HttpRequestException($"Nexus API 请求失败: {resp.StatusCode}");
        }

        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        var mods = new List<ModListing>();
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return mods;

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var mod = ParseModListing(item);
            mods.Add(mod);
        }

        OnLog?.Invoke($"获取到 {mods.Count} 个 Mod");
        return mods;
    }

    /// <summary>
    /// 获取 Mod 详情
    /// GET /v1/games/{game_domain}/mods/{mod_id}.json
    /// </summary>
    public async Task<ModListing?> GetModDetailAsync(string modId)
    {
        var url = $"https://api.nexusmods.com/v1/games/{_gameDomain}/mods/{modId}.json";

        using var resp = await _client.GetAsync(url);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        return ParseModListing(doc.RootElement);
    }

    /// <summary>
    /// 获取 Mod 文件列表（用于下载）
    /// </summary>
    public async Task<string?> GetDownloadUrlAsync(string modId)
    {
        // 1. 获取文件列表
        var filesUrl = $"https://api.nexusmods.com/v1/games/{_gameDomain}/mods/{modId}/files.json";
        using var filesResp = await _client.GetAsync(filesUrl);
        if (!filesResp.IsSuccessStatusCode) return null;

        var filesJson = await filesResp.Content.ReadAsStringAsync();
        using var filesDoc = JsonDocument.Parse(filesJson);

        if (!filesDoc.RootElement.TryGetProperty("files", out var filesEl) ||
            filesEl.ValueKind != JsonValueKind.Array)
            return null;

        string? fileId = null;
        DateTime latest = DateTime.MinValue;

        foreach (var file in filesEl.EnumerateArray())
        {
            var name = file.TryGetProperty("file_name", out var fn) ? fn.GetString() ?? "" : "";
            if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                !name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                continue;

            var uploaded = DateTime.MinValue;
            if (file.TryGetProperty("uploaded_time", out var ut) && ut.ValueKind == JsonValueKind.Number)
            {
                var ts = ut.GetInt64();
                uploaded = DateTimeOffset.FromUnixTimeSeconds(ts).DateTime;
            }

            if (uploaded > latest || latest == DateTime.MinValue)
            {
                latest = uploaded;
                fileId = file.TryGetProperty("file_id", out var fid)
                    ? (fid.ValueKind == JsonValueKind.Number ? fid.GetInt64().ToString() : fid.GetString())
                    : "";
            }
        }

        if (string.IsNullOrEmpty(fileId)) return null;

        // 2. 获取下载链接
        var dlUrl = $"https://api.nexusmods.com/v1/games/{_gameDomain}/mods/{modId}/files/{fileId}/download_link.json";
        using var dlResp = await _client.GetAsync(dlUrl);
        if (!dlResp.IsSuccessStatusCode) return null;

        var dlJson = await dlResp.Content.ReadAsStringAsync();
        using var dlDoc = JsonDocument.Parse(dlJson);

        if (dlDoc.RootElement.ValueKind == JsonValueKind.Array && dlDoc.RootElement.GetArrayLength() > 0)
        {
            var first = dlDoc.RootElement[0];
            if (first.TryGetProperty("URI", out var uri) && uri.ValueKind == JsonValueKind.String)
                return uri.GetString();
        }

        return null;
    }

    private ModListing ParseModListing(JsonElement item)
    {
        long modId = 0;
        if (item.TryGetProperty("mod_id", out var midEl))
        {
            if (midEl.ValueKind == JsonValueKind.Number)
                modId = midEl.GetInt64();
        }

        // ⚠️ 字段名按 Nexus v1 实际返回对齐（此前多处写错导致整列空白）：
        //   下载量 = mod_downloads（不是 downloads）
        //   更新时间 = updated_time 是字符串 "MM/dd/yyyy HH:mm:ss"（不是 Unix 秒）
        //   页面 URL = nexusmods.com/{domain}/mods/{id}（**没有** /games/ 段）
        var author = GetString(item, "author");
        if (string.IsNullOrEmpty(author)) author = GetString(item, "uploaded_by");

        var downloads = GetLong(item, "mod_downloads");
        if (downloads == 0) downloads = GetLong(item, "downloads");

        return new ModListing
        {
            Name = GetString(item, "name"),
            Author = author,
            Summary = GetString(item, "summary"),
            Description = GetString(item, "description"),
            Version = GetString(item, "version"),
            Category = "N网",
            DownloadCount = downloads,
            EndorsementCount = GetLong(item, "endorsement_count"),
            ThumbnailUrl = GetString(item, "picture_url"),
            PreviewUrl = GetString(item, "picture_url"),
            Source = "Nexus",
            ModId = modId.ToString(),
            PageUrl = $"https://www.nexusmods.com/{_gameDomain}/mods/{modId}",
            PublishedAt = ParseNexusTime(item),
        };
    }

    /// <summary>解析更新时间：优先 Unix 秒（updated_timestamp），否则解析 "MM/dd/yyyy HH:mm:ss"</summary>
    private static DateTime ParseNexusTime(JsonElement el)
    {
        var ts = GetLong(el, "updated_timestamp");
        if (ts > 0) return DateTimeOffset.FromUnixTimeSeconds(ts).LocalDateTime;

        var s = GetString(el, "updated_time");
        if (DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var dt))
            return dt;
        return default;
    }

    private static string GetString(JsonElement el, string name)
    {
        if (el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String)
            return p.GetString() ?? "";
        return "";
    }

    private static long GetLong(JsonElement el, string name)
    {
        if (el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number)
            return p.GetInt64();
        return 0;
    }

    private static DateTime GetUnixTimestamp(JsonElement el, string name)
    {
        if (el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number)
            return DateTimeOffset.FromUnixTimeSeconds(p.GetInt64()).DateTime;
        return default;
    }

    public void Dispose() => _client.Dispose();
}
