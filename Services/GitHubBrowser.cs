using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using CUModUpdater.Models;

namespace CUModUpdater.Services;

/// <summary>
/// GitHub Mod 浏览器 - 精选列表 + 搜索 API + 仓库详情
/// 专为 Casualties Unknown Demo 游戏对接
/// </summary>
public class GitHubBrowser : IDisposable
{
    private readonly HttpClient _client;
    private readonly string? _token;

    public Action<string>? OnLog { get; set; }

    // === Casualties Unknown 精选 Mod 列表 ===
    public static readonly CuratedModEntry[] CuratedMods = new[]
    {
        new CuratedModEntry
        {
            Name = "CUCoreLib",
            Owner = "jimmyking9999999",
            Repo = "CUCoreLib",
            Category = "核心库",
            ChineseSummary = "Casualties Unknown 社区核心库，为其他 Mod 提供 UI 组件和工具",
            ChineseDescription = "CUCoreLib 是 Casualties Unknown 模组生态的核心库。\n\n提供共享 UI 组件、通用工具方法，被 QoL-Unknown 等多个 Mod 依赖。\n\n依赖此库的 Mod 必须先安装 CUCoreLib。",
            PluginGuid = "com.casualtiesunknown.corelib",
            FileName = "CUCoreLib.dll",
        },
        new CuratedModEntry
        {
            Name = "QoL Unknown",
            Owner = "jimmyking9999999",
            Repo = "QoL-Unknown",
            Category = "体验优化",
            ChineseSummary = "生活质量改进：物品高亮、批量合成、容器转移等",
            ChineseDescription = "QoL-Unknown 为 Casualties Unknown 添加多项生活质量改进：\n\n• 物品高亮 (AltItemHighlight)\n• 批量合成 (BulkCrafting)\n• 容器快速转移 (ContainerTransfer)\n• 控制台命令增强\n\n依赖 CUCoreLib。",
            PluginGuid = "org.bepinex.plugins.qol_unknown",
            FileName = "QoL Unknown.dll",
        },
        // 其他社区 Mod
        new CuratedModEntry { Name = "i18n Auto Updater", Owner = "Orsoniks", Repo = "scavgame-locale", Category = "本地化", ChineseSummary = "中文本地化自动更新器（配置文件内置 Repo Path）", PluginGuid = "org.cncumc.i18nautoupdater", FileName = "i18nAutoUpdater.dll" },
        new CuratedModEntry { Name = "ScavLib API", Owner = "", Repo = "", Category = "API 库", ChineseSummary = "第三方 API 库，提供 UI 等基础设施", PluginGuid = "", FileName = "ScavLib API.dll" },
        new CuratedModEntry { Name = "AutoSort", Owner = "", Repo = "", Category = "体验优化", ChineseSummary = "背包自动排序", PluginGuid = "com.el739.autosort", FileName = "AutoSort.dll" },
        new CuratedModEntry { Name = "ContainerTweaks", Owner = "", Repo = "", Category = "体验优化", ChineseSummary = "容器交互优化", PluginGuid = "com.user.containertweaks", FileName = "ContainerTweaks.dll" },
        new CuratedModEntry { Name = "SellFromBags", Owner = "", Repo = "", Category = "体验优化", ChineseSummary = "从背包直接出售", PluginGuid = "angly.casualtiesunknown.sellfrombags", FileName = "SellFromBags.dll" },
        new CuratedModEntry { Name = "SaveManager", Owner = "", Repo = "", Category = "工具", ChineseSummary = "多存档管理", PluginGuid = "com.casualtiesUnknown.saveManager", FileName = "CuSaveManager.dll" },
        new CuratedModEntry { Name = "ConsoleChinese", Owner = "", Repo = "", Category = "本地化", ChineseSummary = "控制台中文翻译", PluginGuid = "", FileName = "ConsoleChinese.dll" },
        new CuratedModEntry { Name = "A.Multi_Mod Chinese", Owner = "", Repo = "", Category = "本地化", ChineseSummary = "Casualties Unknown 中文翻译整合", PluginGuid = "", FileName = "A.Multi_Mod.Chinese.Localization.dll" },
        new CuratedModEntry { Name = "ItemSpawnerMenuCN", Owner = "", Repo = "", Category = "工具", ChineseSummary = "中文物品生成菜单", PluginGuid = "com.kanisuko.menu", FileName = "ItemSpawnerMenuCN.dll" },
        new CuratedModEntry { Name = "CUQuickPickup", Owner = "", Repo = "", Category = "体验优化", ChineseSummary = "快速拾取优化", PluginGuid = "com.casualtiesunknown.quickpickup", FileName = "CUQuickPickup.dll" },
        new CuratedModEntry { Name = "GracePickupBypass", Owner = "", Repo = "", Category = "体验优化", ChineseSummary = "跳过拾取确认", PluginGuid = "local.casualtiesunknown.gracepickupbypass", FileName = "GracePickupBypass.dll" },
        new CuratedModEntry { Name = "WearableArray", Owner = "", Repo = "", Category = "工具", ChineseSummary = "可穿戴物数组扩展", PluginGuid = "", FileName = "WearableArray.dll" },
        new CuratedModEntry { Name = "ModNotifier", Owner = "", Repo = "", Category = "工具", ChineseSummary = "Mod 通知器", PluginGuid = "", FileName = "ModNotifier.dll" },
        new CuratedModEntry { Name = "AllWearableCanBeHeld", Owner = "", Repo = "", Category = "体验优化", ChineseSummary = "所有穿戴物可手持", PluginGuid = "", FileName = "AllWearableCanBeHeld.dll" },
        new CuratedModEntry { Name = "Alexx Mod Manager", Owner = "", Repo = "", Category = "工具", ChineseSummary = "Mod 管理器（运行时启用/禁用）", PluginGuid = "", FileName = "Alexx_'sModMgr.dll" },
        new CuratedModEntry { Name = "Animalese", Owner = "", Repo = "", Category = "音频", ChineseSummary = "角色动物语音效", PluginGuid = "", FileName = "Animalese.dll" },
        new CuratedModEntry { Name = "UnknownPerformance", Owner = "", Repo = "", Category = "性能", ChineseSummary = "性能优化", PluginGuid = "com.kanisuko.unknownperform", FileName = "UnknownPerformance.dll" },
        new CuratedModEntry { Name = "VProfiler", Owner = "", Repo = "", Category = "性能", ChineseSummary = "帧率/性能分析器", PluginGuid = "", FileName = "VProfiler.dll" },
        new CuratedModEntry { Name = "CatPatch", Owner = "", Repo = "", Category = "工具", ChineseSummary = "CatPatch 多功能补丁", PluginGuid = "meow.catpatch", FileName = "CatPatch.dll" },
        new CuratedModEntry { Name = "body_sprite_replacer", Owner = "", Repo = "", Category = "图形", ChineseSummary = "身体精灵替换", PluginGuid = "", FileName = "body_sprite_replacer.dll" },
        new CuratedModEntry { Name = "CommandLine", Owner = "", Repo = "", Category = "工具", ChineseSummary = "命令行 Mod", PluginGuid = "com.casualtiesunknown.commandline", FileName = "ChangeSkinMP/CommandLine.dll" },
        new CuratedModEntry { Name = "KrokMP 多人联机", Owner = "", Repo = "", Category = "联机", ChineseSummary = "多人在线联机 Mod", PluginGuid = "com.krokmp", FileName = "KrokoshaCasualtiesMP.dll" },
        new CuratedModEntry { Name = "ChangeSkinMP", Owner = "", Repo = "", Category = "联机", ChineseSummary = "联机换皮肤", PluginGuid = "05126619z.changeskin", FileName = "ChangeSkinMP.dll" },
        new CuratedModEntry { Name = "RemiyamuremodLayerUnlock", Owner = "", Repo = "", Category = "图形", ChineseSummary = "图层解锁工具", PluginGuid = "", FileName = "RemiyamuremodLayerUnlock.dll" },
    };

    public GitHubBrowser(string? token = null)
    {
        _token = token;
        _client = new HttpClient();
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
    /// 在精选列表中按 PluginGuid 精确查找 Mod（忽略大小写）
    /// </summary>
    public static CuratedModEntry? FindCuratedByGuid(string? guid, string? fileName = null)
    {
        if (!string.IsNullOrEmpty(guid))
        {
            var match = CuratedMods.FirstOrDefault(m =>
                !string.IsNullOrEmpty(m.PluginGuid) &&
                m.PluginGuid.Equals(guid, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;
        }

        // 回退：用文件名（去掉 .dll/.dll_disabled/_disabled）匹配
        if (!string.IsNullOrEmpty(fileName))
        {
            var baseName = ConfigManager.GetFileKey(fileName).Trim();
            if (!string.IsNullOrEmpty(baseName))
            {
                var match = CuratedMods.FirstOrDefault(m =>
                    !string.IsNullOrEmpty(m.FileName) &&
                    ConfigManager.GetFileKey(m.FileName)
                        .Trim()
                        .Equals(baseName, StringComparison.OrdinalIgnoreCase));
                if (match != null) return match;
            }
        }

        return null;
    }

    /// <summary>
    /// GitHub 仓库的预览卡图片（不需要 API、不需要 Token，任何公开仓库都有）。
    /// GitHub 的 API 里没有仓库配图，而 OG 卡片是 GitHub 自己渲染的社交预览图，
    /// 用它当缩略图比一片空白强得多。
    /// </summary>
    public static string OpenGraphImage(string owner, string repo) =>
        string.IsNullOrEmpty(owner) || string.IsNullOrEmpty(repo)
            ? ""
            : $"https://opengraph.githubassets.com/1/{owner}/{repo}";

    /// <summary>
    /// 用 raw.githubusercontent 抓 README 做简介兜底。
    /// 走 raw 域名**不消耗 GitHub API 限额**（API 匿名只有 60 次/小时，
    /// 而仓库没写 description 的情况很常见，全靠 API 会立刻打满）。
    /// </summary>
    public async Task<string> FetchReadmeSummaryAsync(string owner, string repo, int maxChars = 700)
    {
        if (string.IsNullOrEmpty(owner) || string.IsNullOrEmpty(repo)) return "";

        foreach (var branch in new[] { "HEAD", "main", "master" })
        {
            foreach (var file in new[] { "README.md", "readme.md", "README.MD" })
            {
                try
                {
                    var url = $"https://raw.githubusercontent.com/{owner}/{repo}/{branch}/{file}";
                    using var resp = await _client.GetAsync(url);
                    if (!resp.IsSuccessStatusCode) continue;
                    var text = await resp.Content.ReadAsStringAsync();
                    var clean = CleanMarkdown(text, maxChars);
                    if (clean.Length > 20) return clean;
                }
                catch { /* 换下一个组合 */ }
            }
        }
        return "";
    }

    /// <summary>把 README 的 markdown 洗成人能读的纯文本（去徽章/HTML/标题符号）</summary>
    private static string CleanMarkdown(string md, int maxChars)
    {
        var lines = md.Replace("\r\n", "\n").Split('\n');
        var kept = new List<string>();

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                if (kept.Count > 0 && kept[^1].Length > 0) kept.Add("");
                continue;
            }
            if (line.StartsWith("![") || line.StartsWith("[![")) continue;   // 徽章
            if (line.StartsWith("---") || line.StartsWith("<img") ||
                line.StartsWith("<p") || line.StartsWith("<div") ||
                line.StartsWith("<h") || line.StartsWith("</")) continue;   // HTML 块

            line = System.Text.RegularExpressions.Regex.Replace(line, @"!\[[^\]]*\]\([^)]*\)", "");  // 图片
            line = System.Text.RegularExpressions.Regex.Replace(line, @"\[([^\]]*)\]\([^)]*\)", "$1"); // 链接留文字
            line = line.TrimStart('#', '>', '*', '-', ' ').Trim();
            if (line.Length == 0) continue;

            kept.Add(line);
            if (kept.Sum(l => l.Length) > maxChars) break;
        }

        var text = string.Join("\n", kept).Trim();
        return text.Length > maxChars ? text[..maxChars] + "…" : text;
    }

    /// <summary>
    /// 获取精选 Mod 列表
    /// </summary>
    public async Task<List<ModListing>> GetCuratedModsAsync()
    {
        var list = new List<ModListing>();

        foreach (var entry in CuratedMods)
        {
            var mod = new ModListing
            {
                Name = entry.Name,
                Author = entry.Owner,
                Category = entry.Category,
                Summary = entry.ChineseSummary,
                // 精选条目大多只有中文摘要、没有长描述 —— 用摘要兜底，
                // 别让预览面板空着；真正需要长文时在选中后再抓 README
                Description = !string.IsNullOrEmpty(entry.ChineseDescription)
                    ? entry.ChineseDescription
                    : entry.ChineseSummary,
                TranslatedDescription = entry.ChineseDescription,
                TranslatedSummary = entry.ChineseSummary,
                Source = "Curated",
                Owner = entry.Owner,
                Repo = entry.Repo,
                PluginGuid = entry.PluginGuid,
                FileName = entry.FileName,
                // 页面地址无条件给出（此前只在拿到 release 时才设，
                // 导致没发 Release 的仓库连「打开页面」都点不动）
                PageUrl = !string.IsNullOrEmpty(entry.Owner) && !string.IsNullOrEmpty(entry.Repo)
                    ? $"https://github.com/{entry.Owner}/{entry.Repo}" : "",
                // GitHub 没有仓库配图 → 用 OG 预览卡当缩略图
                ThumbnailUrl = OpenGraphImage(entry.Owner, entry.Repo),
                PreviewUrl = OpenGraphImage(entry.Owner, entry.Repo),
            };

            // 如果有 GitHub 仓库，尝试获取最新版本
            if (!string.IsNullOrEmpty(entry.Owner) && !string.IsNullOrEmpty(entry.Repo))
            {
                try
                {
                    var release = await GetLatestReleaseInfo(entry.Owner, entry.Repo);
                    if (release != null)
                    {
                        mod.Version = release.Version;
                        mod.DownloadUrl = release.DownloadUrl;
                        mod.PublishedAt = release.PublishedAt;
                        mod.Source = "GitHub";
                    }
                }
                catch { /* 忽略，使用精选信息 */ }
            }

            list.Add(mod);
        }

        return list;
    }

    /// <summary>
    /// 搜索 GitHub 仓库
    /// </summary>
    /// <summary>
    /// GitHub 仓库搜索（query 原样使用，**不追加任何词**——
    /// 早前在这里硬追加 "casualties unknown"，调用方若已经带了游戏名就会叠词）。
    /// </summary>
    public async Task<List<ModListing>> SearchAsync(string query, int maxPages = 1)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new List<ModListing>();

        var mods = new List<ModListing>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ⚠️ GitHub 搜索**默认 per_page=30**，且最多允许 100。
        // 之前写死 30 条，而 "casualties unknown" 实测能匹配 176 个仓库 ——
        // 用户看到的就是「GitHub 的 mod 获取不全」。这里改成 100 并支持翻页。
        const int perPage = 100;
        for (var page = 1; page <= Math.Max(1, maxPages); page++)
        {
            var url = "https://api.github.com/search/repositories" +
                $"?q={Uri.EscapeDataString(query)}" +
                $"&sort=updated&order=desc&per_page={perPage}&page={page}";

            using var resp = await _client.GetAsync(url);

            if (resp.StatusCode == System.Net.HttpStatusCode.Forbidden ||
                resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                OnLog?.Invoke(mods.Count > 0
                    ? $"GitHub 搜索限额用尽，已取到前 {mods.Count} 个结果（配置 Token 可解除：60/h → 5000/h）"
                    : "GitHub 搜索速率限制，请稍后再试或配置 Token。");
                break;
            }
            if (!resp.IsSuccessStatusCode)
            {
                OnLog?.Invoke($"GitHub 搜索失败: {resp.StatusCode}");
                break;
            }

            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("items", out var items) ||
                items.ValueKind != JsonValueKind.Array)
                break;

            var countThisPage = 0;
            foreach (var item in items.EnumerateArray())
            {
                var fullName = GetString(item, "full_name");
                var parts = fullName.Split('/', 2);
                if (parts.Length != 2) continue;
                if (!seen.Add(fullName)) continue;      // 多次查询合并时去重
                countThisPage++;

                var repoDesc = GetString(item, "description");
                mods.Add(new ModListing
                {
                    Name = GetString(item, "name"),
                    Author = parts[0],
                    Summary = repoDesc,
                    // 仓库没写 description 时给个明确的占位，而不是留空让人以为没加载出来
                    Description = string.IsNullOrWhiteSpace(repoDesc)
                        ? "（该仓库未填写简介，选中后会尝试抓取 README）" : repoDesc,
                    Category = Services.CategoryClassifier.ClassifyLoose(GetString(item, "name"), repoDesc),
                    Source = "GitHub",
                    Owner = parts[0],
                    Repo = parts[1],
                    PageUrl = GetString(item, "html_url"),
                    EndorsementCount = GetLong(item, "stargazers_count"),
                    ThumbnailUrl = OpenGraphImage(parts[0], parts[1]),
                    PreviewUrl = OpenGraphImage(parts[0], parts[1]),
                    PublishedAt = ParseTime(GetString(item, "updated_at")),
                });
            }

            OnLog?.Invoke($"GitHub 搜索 “{query}” 第 {page} 页: {countThisPage} 个");
            if (countThisPage < perPage) break;         // 没有下一页了
            await Task.Delay(250);                      // 搜索接口 10 次/分钟，翻页别太急
        }

        OnLog?.Invoke($"GitHub 搜索 “{query}” 共 {mods.Count} 个结果");
        return mods;
    }

    /// <summary>
    /// GitHub 标签页的默认浏览：**合并多组查询**。
    /// 单个查询覆盖不全（搜 "casualties unknown mod" 只匹配 176 个仓库中的一部分，
    /// 而且要求三个词都出现），所以用几组互补的查询取并集再按更新时间排序。
    /// 搜索接口匿名限额是 10 次/分钟，所以每组查询只取 1 页。
    /// </summary>
    public async Task<List<ModListing>> BrowseAsync(int maxPagesPerQuery = 1)
    {
        // 几组互补的查询取并集：单组查询只覆盖到 176 个匹配仓库的一部分。
        // 搜索接口匿名限额 10 次/分钟，所以每组查询只取 1 页（4 组 = 4 次请求）。
        var queries = new[]
        {
            "casualties unknown",           // 覆盖面最广（实测 total_count=176）
            "casualties-unknown",           // 仓库名写法带连字符的
            "casualties unknown mod",       // 明确叫 mod 的
            "casualties unknown bepinex",   // 标了 BepInEx 的插件仓库
        };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var all = new List<ModListing>();

        foreach (var q in queries)
        {
            try
            {
                foreach (var m in await SearchAsync(q, maxPagesPerQuery))
                {
                    if (!string.IsNullOrEmpty(m.Owner) && seen.Add($"{m.Owner}/{m.Repo}"))
                        all.Add(m);
                }
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"查询「{q}」失败: {ex.Message}");
            }
            await Task.Delay(300);
        }

        OnLog?.Invoke($"GitHub 浏览合计 {all.Count} 个仓库（已去重）");
        return all.OrderByDescending(m => m.PublishedAt).ToList();
    }

    /// <summary>
    /// 给 GitHub 列表项补 Release 信息（版本 / 下载直链 / 发布日期）。
    ///
    /// 搜索接口**不返回 Release**，所以搜索列表里那一列全是「-」、按钮是
    /// 「需手动下载」—— 看着像硬限制，其实选中时补查一次接口就能直接下载。
    /// 每次查询 1 个 API 请求，结果缓存在内存里避免反复查同一个仓库。
    /// </summary>
    public async Task<bool> EnrichReleaseAsync(ModListing mod)
    {
        if (string.IsNullOrEmpty(mod.Owner) || string.IsNullOrEmpty(mod.Repo)) return false;
        var key = $"{mod.Owner}/{mod.Repo}";

        if (!_releaseCache.TryGetValue(key, out var release))
        {
            try
            {
                release = await GetLatestReleaseInfo(mod.Owner, mod.Repo);
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"获取 {key} 的 Release 失败: {ex.Message}");
                return false;
            }
            _releaseCache[key] = release;
        }

        if (release == null)
        {
            mod.ReleaseChecked = true;   // 没有 Release（源码仓库），别反复查
            return false;
        }

        mod.Version = string.IsNullOrEmpty(mod.Version) ? release.Version : mod.Version;
        mod.DownloadUrl = release.DownloadUrl ?? "";
        if (release.PublishedAt != default) mod.PublishedAt = release.PublishedAt;
        if (!string.IsNullOrEmpty(release.ReleaseNotes) && mod.Description.Length < 80)
            mod.Description = release.ReleaseNotes;
        mod.ReleaseChecked = true;
        return true;
    }

    private readonly Dictionary<string, ReleaseInfo?> _releaseCache =
        new(StringComparer.OrdinalIgnoreCase);

    private static DateTime ParseTime(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return default;
        return DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal, out var dt) ? dt : default;
    }

    /// <summary>
    /// 一键解析 GitHub 仓库：验证仓库存在 + 拉最新 Release（版本/下载/asset 名）
    /// 返回 (仓库是否存在, 最新 Release 信息)。仓库存在但没有 Release 时 release 为 null。
    /// </summary>
    public async Task<(bool repoExists, ReleaseInfo? release)> ResolveRepoAsync(string owner, string repo)
    {
        // 1. 验证仓库存在（404 = 不存在或私有）
        var repoUrl = $"https://api.github.com/repos/{owner}/{repo}";
        using (var repoResp = await _client.GetAsync(repoUrl))
        {
            if (!repoResp.IsSuccessStatusCode)
                return (false, null);
        }

        // 2. 最新 Release（可能没有 Release，属正常情况）
        ReleaseInfo? release = null;
        try
        {
            release = await GetLatestReleaseInfo(owner, repo);
        }
        catch { /* 无 release 不算失败 */ }

        return (true, release);
    }

    /// <summary>
    /// 获取 GitHub 仓库详情（README + 最新 Release）
    /// </summary>
    public async Task<ModListing?> GetRepoDetailAsync(string owner, string repo)
    {
        OnLog?.Invoke($"正在获取 GitHub 仓库: {owner}/{repo}");

        // 1. 获取仓库信息
        var repoUrl = $"https://api.github.com/repos/{owner}/{repo}";
        using var repoResp = await _client.GetAsync(repoUrl);
        if (repoResp.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        repoResp.EnsureSuccessStatusCode();

        var repoJson = await repoResp.Content.ReadAsStringAsync();
        using var repoDoc = JsonDocument.Parse(repoJson);
        var repoEl = repoDoc.RootElement;

        var repoDesc = GetString(repoEl, "description");
        var mod = new ModListing
        {
            Name = GetString(repoEl, "name"),
            Author = owner,
            Summary = repoDesc,
            Description = string.IsNullOrWhiteSpace(repoDesc)
                ? "（该仓库未填写简介，正在尝试读取 README）" : repoDesc,
            Category = "GitHub",
            Source = "GitHub",
            Owner = owner,
            Repo = repo,
            PageUrl = GetString(repoEl, "html_url"),
            EndorsementCount = GetLong(repoEl, "stargazers_count"),
            ThumbnailUrl = OpenGraphImage(owner, repo),
            PreviewUrl = OpenGraphImage(owner, repo),
        };

        // 2. 获取最新 Release
        try
        {
            var release = await GetLatestReleaseInfo(owner, repo);
            if (release != null)
            {
                mod.Version = release.Version;
                mod.DownloadUrl = release.DownloadUrl;
                mod.PublishedAt = release.PublishedAt;
                if (!string.IsNullOrEmpty(release.ReleaseNotes))
                {
                    mod.Description = release.ReleaseNotes;
                }
            }
        }
        catch { /* 没有 Release 也正常 */ }

        // 3. 获取 README
        try
        {
            var readme = await GetReadmeAsync(owner, repo);
            if (!string.IsNullOrEmpty(readme))
            {
                mod.Description = readme;
            }
        }
        catch { /* README 获取失败也正常 */ }

        return mod;
    }

    /// <summary>
    /// 获取最新 Release 信息
    /// </summary>
    private async Task<ReleaseInfo?> GetLatestReleaseInfo(string owner, string repo)
    {
        var url = $"https://api.github.com/repos/{owner}/{repo}/releases/latest";
        using var resp = await _client.GetAsync(url);

        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;

        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var tagName = GetString(root, "tag_name");
        var version = tagName.TrimStart('v', 'V');

        string? downloadUrl = null;
        string? assetName = null;

        if (root.TryGetProperty("assets", out var assetsEl) && assetsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assetsEl.EnumerateArray())
            {
                var name = GetString(asset, "name");
                var url2 = GetString(asset, "browser_download_url");
                if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    downloadUrl = url2;
                    assetName = name;
                    break;
                }
            }
        }

        DateTime publishedAt = default;
        if (root.TryGetProperty("published_at", out var pubEl) &&
            pubEl.ValueKind == JsonValueKind.String &&
            DateTime.TryParse(pubEl.GetString(), out var dt))
            publishedAt = dt;

        return new ReleaseInfo
        {
            Version = version,
            DownloadUrl = downloadUrl ?? "",
            AssetName = assetName ?? "",
            ReleaseNotes = GetString(root, "body"),
            PublishedAt = publishedAt,
        };
    }

    /// <summary>
    /// 获取仓库 README 文本（用于 Mod 浏览器显示简介）
    /// </summary>
    /// <summary>README 磁盘缓存目录（README 内容基本不变，缓存下来省匿名限额）</summary>
    private static readonly string ReadmeCacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CU-ModUpdater", "gh_readme");

    /// <summary>最近一次 API 调用是不是被限额挡了（用于给用户准确的原因）</summary>
    public bool LastCallRateLimited { get; private set; }

    /// <summary>最近一次 raw.githubusercontent 是不是连不上（国内无代理时常超时）</summary>
    public bool LastRawUnreachable { get; private set; }

    public async Task<string?> GetReadmeTextAsync(string owner, string repo)
    {
        LastCallRateLimited = false;
        LastRawUnreachable = false;

        // 0. 磁盘缓存
        var cacheFile = ReadmeCachePath(owner, repo);
        try
        {
            if (File.Exists(cacheFile))
            {
                var cached = await File.ReadAllTextAsync(cacheFile);
                if (!string.IsNullOrWhiteSpace(cached)) return cached;
            }
        }
        catch { /* 缓存读失败不影响主流程 */ }

        // 1. API /repos/{o}/{r}/readme（api.github.com 在国内一般能通）
        try
        {
            var text = await GetReadmeAsync(owner, repo);
            if (!string.IsNullOrWhiteSpace(text))
            {
                SaveReadmeCache(cacheFile, text);
                return text;
            }
            LastCallRateLimited = _lastApiForbidden;
        }
        catch { }

        // 2. raw.githubusercontent 兜底（**国内无代理时经常超时**）
        try
        {
            var raw = await FetchReadmeSummaryAsync(owner, repo);
            if (!string.IsNullOrWhiteSpace(raw))
            {
                SaveReadmeCache(cacheFile, raw);
                return raw;
            }
            LastRawUnreachable = true;
        }
        catch { LastRawUnreachable = true; }

        return null;
    }

    private static string ReadmeCachePath(string owner, string repo)
    {
        var name = $"{owner}__{repo}.txt";
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return Path.Combine(ReadmeCacheDir, name);
    }

    private static void SaveReadmeCache(string path, string text)
    {
        try
        {
            Directory.CreateDirectory(ReadmeCacheDir);
            File.WriteAllText(path, text);
        }
        catch { }
    }

    /// <summary>
    /// 获取 README 内容
    /// </summary>
    private async Task<string?> GetReadmeAsync(string owner, string repo)
    {
        var url = $"https://api.github.com/repos/{owner}/{repo}/readme";
        _client.DefaultRequestHeaders.Accept.Clear();
        _client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github.raw+json"));

        using var resp = await _client.GetAsync(url);

        // 恢复 Accept 头
        _client.DefaultRequestHeaders.Accept.Clear();
        _client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        _lastApiForbidden = resp.StatusCode == System.Net.HttpStatusCode.Forbidden ||
                            resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests;
        if (!resp.IsSuccessStatusCode)
            return null;

        return await resp.Content.ReadAsStringAsync();
    }

    private bool _lastApiForbidden;

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

    public void Dispose() => _client.Dispose();
}
