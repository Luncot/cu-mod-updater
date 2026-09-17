using System.Net.Http;
using System.Text.RegularExpressions;

namespace CUModUpdater.Services;

/// <summary>
/// N 网 Cookie 下载器 —— 免费账号批量下载的正解。
/// 原理：用用户浏览器里已登录的 Cookie 模拟网页下载流程
///   1. GET mod 文件页 → 提取内部 game_id
///   2. POST Core/Libs/Common/Widgets/DownloadPopUp.php?id={fileId}&game_id={gameId}
///   3. 从响应提取 CDN 直链（免费号走 slow download，也走这个端点）
///   4. GET CDN 直链（带 Cookie + Referer）→ 保存
/// Cookie 获取方法：浏览器登录 N 网 → F12 → Network → 任意请求的 Request Headers → 复制整行 Cookie
/// </summary>
public static class NexusCookieDownloader
{
    /// <summary>
    /// 解析元数据的 nexus:// 域/modId/fileId 格式
    /// </summary>
    public static (string domain, string modId, string fileId)? ParseNexusUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        var m = Regex.Match(url, @"^nexus://([\w-]+)/(\d+)/(\d+)$");
        if (!m.Success) return null;
        return (m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value);
    }

    /// <summary>
    /// 获取文件直链。两条路径：
    ///  1) 有 API Key：Nexus API 拿 file_name + md5 → 拼 cf-files CDN 直链（最可靠）
    ///  2) 无 API Key：解析页面 &lt;mod-file-download&gt; 组件的 nxm 链接（免费账号也有），
    ///     但缺少 md5/文件名，直链不可用 → 返回 null 由上层提示
    /// 2026 版 N 网已废弃 DownloadPopUp.php（404），本方法基于新版机制实现。
    /// </summary>
    public static async Task<string?> GetDirectUrlAsync(
        HttpClient client, string gameDomain, string modId, string fileId, string cookie,
        string? apiKey = null, Action<string>? log = null)
    {
        // === 1. 页面：拿内部 gameId + nxm key ===
        var pageUrl = $"https://www.nexusmods.com/{gameDomain}/mods/{modId}";
        using var req1 = new HttpRequestMessage(HttpMethod.Get, $"{pageUrl}?tab=files&file_id={fileId}&nmm=1");
        req1.Headers.TryAddWithoutValidation("Cookie", cookie);
        req1.Headers.TryAddWithoutValidation("User-Agent", BrowserUA);
        string html;
        try
        {
            using var r1 = await client.SendAsync(req1);
            if (!r1.IsSuccessStatusCode)
            {
                log?.Invoke($"  N 网页面请求失败: HTTP {(int)r1.StatusCode}（Cookie 可能过期）");
                return null;
            }
            html = await r1.Content.ReadAsStringAsync();
        }
        catch (Exception ex)
        {
            log?.Invoke($"  N 网页面请求异常: {ex.Message}");
            return null;
        }

        // 登录/免费状态自检
        var loggedIn = Regex.Match(html, @"user-is-logged-in=""(\w+)""");
        if (loggedIn.Success && loggedIn.Groups[1].Value.Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            log?.Invoke("  Cookie 已失效（未登录状态），请在设置里重新复制 Cookie");
            return null;
        }

        // 内部 gameId（如 8391）
        var gameIdMatch = Regex.Match(html, @"data-game-id=""(\d+)""");
        if (!gameIdMatch.Success)
            gameIdMatch = Regex.Match(html, @"game-id=""(\d+)""");
        var gameId = gameIdMatch.Success ? gameIdMatch.Groups[1].Value : "";

        // nxm 链接（免费账号也有，含 key/expires/user_id）
        var nxmMatch = Regex.Match(html,
            @"download-url=""(nxm://[^""]+)""", RegexOptions.IgnoreCase);
        var nxm = nxmMatch.Success ? nxmMatch.Groups[1].Value.Replace("&amp;", "&") : "";
        var keyM = Regex.Match(nxm, @"key=([^&]+)");
        var expM = Regex.Match(nxm, @"expires=(\d+)");
        var uidM = Regex.Match(nxm, @"user_id=(\d+)");
        var nxmKey = keyM.Success ? keyM.Groups[1].Value : "";
        var expires = expM.Success ? expM.Groups[1].Value : "";
        var userId = uidM.Success ? uidM.Groups[1].Value : "";

        if (string.IsNullOrEmpty(gameId))
        {
            log?.Invoke("  未能从页面解析 gameId，N 网页面结构可能又变了");
            return null;
        }

        // === 2. 有 API Key：拿 file_name + md5，拼 CDN 直链 ===
        if (!string.IsNullOrEmpty(apiKey))
        {
            try
            {
                var apiUrl = $"https://api.nexusmods.com/v1/games/{gameDomain}/mods/{modId}/files/{fileId}.json";
                using var req = new HttpRequestMessage(HttpMethod.Get, apiUrl);
                req.Headers.TryAddWithoutValidation("apikey", apiKey);
                req.Headers.TryAddWithoutValidation("User-Agent", BrowserUA);
                using var resp = await client.SendAsync(req);
                if (resp.IsSuccessStatusCode)
                {
                    var json = await resp.Content.ReadAsStringAsync();
                    var fileName = Regex.Match(json, @"""file_name""\s*:\s*""([^""]+)""").Groups[1].Value;
                    var md5 = Regex.Match(json, @"""md5""\s*:\s*""([^""]+)""").Groups[1].Value;
                    if (!string.IsNullOrEmpty(fileName) && !string.IsNullOrEmpty(md5))
                    {
                        var direct =
                            $"https://cf-files.nexusmods.com/cdn/{gameId}/{modId}/{fileId}/{Uri.EscapeDataString(fileName)}" +
                            $"?md5={md5}&expires={expires}&user_id={userId}&key={nxmKey}";
                        log?.Invoke($"  API 取到文件名 {fileName}（md5 {md5[..8]}...），已构造 CDN 直链");
                        return direct;
                    }
                    log?.Invoke("  API 响应缺少 file_name/md5");
                }
                else
                {
                    log?.Invoke($"  Nexus API 返回 HTTP {(int)resp.StatusCode}（Key 无效或过期？）");
                }
            }
            catch (Exception ex)
            {
                log?.Invoke($"  Nexus API 调用异常: {ex.Message}");
            }
        }
        else
        {
            log?.Invoke("  未配置 Nexus API Key —— 无法取得文件名+md5，无法构造直链");
        }

        return null;
    }

    /// <summary>浏览器 UA（cf_clearance 与 UA 绑定，必须固定一致）</summary>
    public const string BrowserUA =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/150.0.0.0 Safari/537.36 Edg/150.0.0.0";

    /// <summary>
    /// Cookie 流程完整下载：直链 → 文件流保存
    /// </summary>
    public static async Task<bool> DownloadFileAsync(
        HttpClient client, string gameDomain, string modId, string fileId,
        string cookie, string destPath, Action<long, long>? progress, Action<string>? log,
        string? apiKey = null)
    {
        var direct = await GetDirectUrlAsync(client, gameDomain, modId, fileId, cookie, apiKey, log);
        if (string.IsNullOrEmpty(direct))
        {
            log?.Invoke("  未能获取 N 网直链（Cookie 过期 / 未配 API Key / 页面结构变更）");
            return false;
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, direct);
        req.Headers.TryAddWithoutValidation("Cookie", cookie);
        req.Headers.TryAddWithoutValidation("Referer", $"https://www.nexusmods.com/{gameDomain}/mods/{modId}?tab=files");
        req.Headers.TryAddWithoutValidation("User-Agent", BrowserUA);

        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        if (!resp.IsSuccessStatusCode)
        {
            log?.Invoke($"  直链下载失败: HTTP {(int)resp.StatusCode}");
            return false;
        }

        var total = resp.Content.Headers.ContentLength ?? -1;
        await using var content = await resp.Content.ReadAsStreamAsync();
        await using var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
        var buffer = new byte[81920];
        long received = 0;
        int n;
        while ((n = await content.ReadAsync(buffer)) > 0)
        {
            await fs.WriteAsync(buffer.AsMemory(0, n));
            received += n;
            progress?.Invoke(received, total);
        }
        return true;
    }
}
