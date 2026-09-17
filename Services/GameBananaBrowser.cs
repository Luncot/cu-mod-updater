using System.Net.Http;
using System.Text.Json;
using CUModUpdater.Models;

namespace CUModUpdater.Services;

/// <summary>
/// GameBanana API v11 浏览器（免费、无需 API Key）
/// 游戏 id 24260 = Casualties: Unknown（Subfeed 的 id 是 game id 不是 mod id，填错静默返回空数组）
/// 端点与响应结构详见项目内 GameBanana_API参考.md
/// 作者信息以 _aSubmitter._sName 为准（社区权威来源，比 N 网/GitHub 用户名可靠）
/// </summary>
public class GameBananaBrowser : IDisposable
{
    private const string BaseUrl = "https://gamebanana.com/apiv11";
    private const int GameId = 24260; // Casualties: Unknown

    private static readonly string CacheFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CU-ModUpdater", "gamebanana_cache.json");

    private readonly HttpClient _client;

    public Action<string>? OnLog { get; set; }

    public GameBananaBrowser()
    {
        // GB 的 API 偶尔慢，8 秒会把能成功的请求掐死在半路
        _client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        // 图片 CDN / API 都会校验 UA 和 Referer，必须带上，否则被拒
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("CU-ModUpdater/1.1.0 (+https://github.com)");
        _client.DefaultRequestHeaders.Referrer = new Uri("https://gamebanana.com/");
    }

    /// <summary>
    /// 拉取游戏下全部 Mod（Subfeed 分页），并逐个补详情（版本/图片/下载链接）。
    /// 带 30 分钟磁盘缓存：免费层限速 + 单请求可能超时，无缓存时列表会卡很久。
    /// </summary>
    public async Task<List<ModListing>> GetModsAsync(bool forceRefresh = false)
    {
        // 1. 读缓存
        if (!forceRefresh)
        {
            var cached = TryReadCache();
            if (cached != null)
            {
                OnLog?.Invoke("GameBanana: 使用本地缓存（30 分钟内有效），如需刷新请重新打开浏览器窗口后点搜索");
                return cached;
            }
        }

        var result = new List<ModListing>();
        var page = 1;
        while (page <= 5)
        {
            List<JsonElement> records;
            try
            {
                records = await GetSubfeedPageAsync(page);
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"GameBanana 列表第 {page} 页失败: {ex.Message}");
                // 列表页失败：有部分结果就用部分，否则抛缓存兜底
                if (result.Count > 0) break;
                var stale = TryReadCache(ignoreAge: true);
                if (stale != null)
                {
                    OnLog?.Invoke("网络失败，回退到过期缓存");
                    return stale;
                }
                throw;
            }
            if (records.Count == 0) break;

            foreach (var record in records)
            {
                var mod = BuildListing(record);
                if (mod == null) continue;
                try { await EnrichAsync(mod); }
                catch (Exception ex)
                {
                    // 单条详情失败不影响列表（超时最多拖 8 秒而不是 30 秒）
                    OnLog?.Invoke($"  {mod.Name} 详情获取失败: {ex.Message}");
                }
                result.Add(mod);
                await Task.Delay(200); // 免费层限速 ~30 req/min，批量请求间留间隔
            }

            if (records.Count < 50) break; // 不到一页说明拉完了
            page++;
            await Task.Delay(300);
        }

        if (result.Count > 0)
            WriteCache(result);
        return result;
    }

    // ==================== 磁盘缓存 ====================

    private sealed class CacheEntry
    {
        public DateTime FetchedAt { get; set; }
        public List<ModListing> Mods { get; set; } = new();
    }

    private static List<ModListing>? TryReadCache(bool ignoreAge = false)
    {
        try
        {
            if (!File.Exists(CacheFile)) return null;
            var json = File.ReadAllText(CacheFile);
            var entry = JsonSerializer.Deserialize<CacheEntry>(json);
            if (entry?.Mods == null || entry.Mods.Count == 0) return null;
            if (!ignoreAge && DateTime.Now - entry.FetchedAt > TimeSpan.FromMinutes(30))
                return null;
            return entry.Mods;
        }
        catch { return null; }
    }

    private static void WriteCache(List<ModListing> mods)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CacheFile)!);
            var entry = new CacheEntry { FetchedAt = DateTime.Now, Mods = mods };
            var json = JsonSerializer.Serialize(entry, new JsonSerializerOptions { WriteIndented = false });
            File.WriteAllText(CacheFile, json);
        }
        catch { /* 缓存写失败不影响主流程 */ }
    }

    // ==================== API 调用 ====================

    private async Task<List<JsonElement>> GetSubfeedPageAsync(int page)
    {
        var records = new List<JsonElement>();
        using var doc = await GetJsonAsync($"{BaseUrl}/Game/{GameId}/Subfeed?_nPage={page}");
        if (doc.RootElement.TryGetProperty("_aRecords", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in arr.EnumerateArray())
                records.Add(r.Clone());
        }
        return records;
    }

    /// <summary>按 modId 补全：版本、预览图、下载链接（ProfilePage + Files）</summary>
    private async Task EnrichAsync(ModListing mod)
    {
        var modId = mod.ModId;
        if (string.IsNullOrEmpty(modId)) return;

        // 1. ProfilePage：版本 + 图片
        using (var profile = await GetJsonAsync($"{BaseUrl}/Mod/{modId}/ProfilePage"))
        {
            var root = profile.RootElement;
            if (root.TryGetProperty("_aVersion", out var ver) &&
                ver.TryGetProperty("_sVersionLabel", out var vl) && vl.ValueKind == JsonValueKind.String)
                mod.Version = vl.GetString() ?? "";
            if (string.IsNullOrEmpty(mod.Version))
                mod.Version = GetString(root, "_sVersion");

            // 描述（_sText 是 HTML，去标签后显示）
            var desc = GetString(root, "_sText");
            if (!string.IsNullOrEmpty(desc))
            {
                desc = System.Text.RegularExpressions.Regex.Replace(desc, "<[^>]+>", "\n");
                desc = System.Net.WebUtility.HtmlDecode(desc);
                desc = System.Text.RegularExpressions.Regex.Replace(desc, "[ \t]+\n", "\n");
                desc = System.Text.RegularExpressions.Regex.Replace(desc, "\n{3,}", "\n\n").Trim();
                mod.Description = desc;
            }

            if (root.TryGetProperty("_aPreviewMedia", out var media) &&
                media.TryGetProperty("_aImages", out var imgs) && imgs.ValueKind == JsonValueKind.Array)
            {
                foreach (var img in imgs.EnumerateArray())
                {
                    var baseUrl = GetString(img, "_sBaseUrl");
                    var file = GetString(img, "_sFile");
                    if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(file)) continue;
                    mod.PreviewUrl = baseUrl + "/" + file;

                    // 缩略图：_sFile220 的值若已是完整 URL 直接用，否则拼 BaseUrl
                    var t220 = GetString(img, "_sFile220");
                    mod.ThumbnailUrl = string.IsNullOrEmpty(t220)
                        ? mod.PreviewUrl
                        : (t220.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                            ? t220 : baseUrl + "/" + t220);
                    break;
                }
            }
        }

        // 2. Files：下载直链（302 重定向到真实文件，HttpClient 默认跟随）
        var files = await GetJsonArrayAsync($"{BaseUrl}/Mod/{modId}/Files");
        if (files.Count > 0)
        {
            var f = files[0];
            mod.DownloadUrl = GetString(f, "_sDownloadUrl");
            if (string.IsNullOrEmpty(mod.DownloadUrl))
                mod.DownloadUrl = $"https://gamebanana.com/dl/{modId}";
            if (mod.DownloadCount <= 0)
                mod.DownloadCount = GetLong(f, "_nDownloadCount");
        }
    }

    private async Task<JsonDocument> GetJsonAsync(string url)
    {
        using var resp = await _client.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync();
        return JsonDocument.Parse(json);
    }

    private async Task<List<JsonElement>> GetJsonArrayAsync(string url)
    {
        using var doc = await GetJsonAsync(url);
        var list = new List<JsonElement>();
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in doc.RootElement.EnumerateArray())
                list.Add(el.Clone());
        }
        return list;
    }

    /// <summary>
    /// 获取最新版本信息（供主窗口更新检查）：
    /// ProfilePage 的 _aVersion._sVersionLabel + Files 的第一个下载直链
    /// </summary>
    public async Task<ReleaseInfo?> GetLatestReleaseAsync(string modId)
    {
        if (string.IsNullOrEmpty(modId)) return null;

        try
        {
            var info = new ReleaseInfo();

            using (var profile = await GetJsonAsync($"{BaseUrl}/Mod/{modId}/ProfilePage"))
            {
                var root = profile.RootElement;
                if (root.TryGetProperty("_aVersion", out var ver) &&
                    ver.TryGetProperty("_sVersionLabel", out var vl) && vl.ValueKind == JsonValueKind.String)
                    info.Version = vl.GetString() ?? "";
                if (string.IsNullOrEmpty(info.Version))
                    info.Version = GetString(root, "_sVersion");

                var ts = GetLong(root, "_tsDateUpdated");
                if (ts > 0)
                    info.PublishedAt = DateTimeOffset.FromUnixTimeSeconds(ts).LocalDateTime;
            }

            var files = await GetJsonArrayAsync($"{BaseUrl}/Mod/{modId}/Files");
            if (files.Count > 0)
            {
                var f = files[0];
                info.DownloadUrl = GetString(f, "_sDownloadUrl");
                info.AssetName = GetString(f, "_sFile");
            }
            if (string.IsNullOrEmpty(info.DownloadUrl))
                info.DownloadUrl = $"https://gamebanana.com/dl/{modId}";

            return string.IsNullOrEmpty(info.Version) ? null : info;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // gamebanana.com 在国内直连经常超时 —— 别把底层异常甩给用户，
            // 说清楚是网络到不了 GB，不是 mod 出了问题
            throw new HttpRequestException(
                "GameBanana 连接失败（国内直连常超时，可能需要代理）：" + ex.Message, ex);
        }
    }

    // ==================== 数据映射 ====================

    private static ModListing? BuildListing(JsonElement record)
    {
        var name = GetString(record, "_sName");
        if (string.IsNullOrEmpty(name)) return null;

        var modId = GetLong(record, "_idRow");
        var submitter = record.TryGetProperty("_aSubmitter", out var sub) ? GetString(sub, "_sName") : "";

        return new ModListing
        {
            Name = name,
            Author = submitter,
            Category = "GameBanana",
            Source = "GameBanana",
            ModId = modId > 0 ? modId.ToString() : "",
            PageUrl = modId > 0 ? $"https://gamebanana.com/mods/{modId}" : "",
            DownloadCount = GetLong(record, "_nDownloadCount"),
            Summary = $"GameBanana 作者: {submitter}",
        };
    }

    private static string GetString(JsonElement el, string name)
    {
        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var p) &&
            p.ValueKind == JsonValueKind.String)
            return p.GetString() ?? "";
        return "";
    }

    private static long GetLong(JsonElement el, string name)
    {
        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var p) &&
            p.ValueKind == JsonValueKind.Number)
            return p.GetInt64();
        return 0;
    }

    public void Dispose() => _client.Dispose();
}
