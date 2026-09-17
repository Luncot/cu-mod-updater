using CUModUpdater.Models;

namespace CUModUpdater.Services;

/// <summary>
/// Mod 清单：把社区元数据（436 条）转成浏览器列表项。
///
/// 为什么不用 Nexus API 当主列表：**N 网 v1 没有「列出全部 mod」的端点**，
/// 只有 latest_updated / latest_added / trending 三个榜单，每个十来条。
/// 而社区元数据（Metadata-generator 定时抓取）是**唯一一份覆盖全游戏的清单**，
/// 且每条都带作者(436/436)、图片(435/436)、长描述(434/436)、下载量、更新时间。
///
/// 所以浏览器的主列表建立在元数据上，榜单只用来补充「刚发布、元数据还没收录」的新 mod。
/// </summary>
public static class ModCatalog
{
    /// <summary>把元数据条目转成列表项（含富文本清洗）</summary>
    public static List<ModListing> FromMetadata(IEnumerable<CasualtiesManageableService.CmModEntry> entries,
        string nexusGameDomain = "scavprototype")
    {
        var list = new List<ModListing>();

        foreach (var e in entries)
        {
            if (string.IsNullOrWhiteSpace(e.Name)) continue;

            var image = e.Images.FirstOrDefault(i => !string.IsNullOrWhiteSpace(i)) ?? "";
            var modId = e.NexusModId;

            var mod = new ModListing
            {
                Name = e.Name,
                Author = e.Author,
                Version = e.Version,
                // 描述/摘要都过一遍富文本清洗（原文是 BBCode + HTML 混排）
                Summary = Markup.Strip(e.Summary),
                Description = Markup.Strip(e.Description),
                Category = CategoryClassifier.Classify(e),
                Source = string.IsNullOrEmpty(modId) ? "Metadata" : "Nexus",
                ModId = modId,
                FileName = e.DllNames.FirstOrDefault() ?? "",
                PluginGuid = e.BepInExPlugins.Keys.FirstOrDefault() ?? "",
                PageUrl = string.IsNullOrEmpty(modId) ? "" :
                    $"https://www.nexusmods.com/{nexusGameDomain}/mods/{modId}",
                ThumbnailUrl = image,
                PreviewUrl = image,
                DownloadCount = e.Statistics?.TotalDownloads ?? 0,
                EndorsementCount = e.Statistics?.Endorsements ?? 0,
                PublishedAt = ParseTime(e.LastUpdated),
            };

            // 摘要为空时用描述首行兜底，别让列表里整列空着
            if (string.IsNullOrEmpty(mod.Summary) && mod.Description.Length > 0)
                mod.Summary = mod.Description.Split('\n').FirstOrDefault(l => l.Trim().Length > 8)?.Trim() ?? "";

            list.Add(mod);
        }

        return list
            .OrderByDescending(m => m.PublishedAt)
            .ToList();
    }

    private static DateTime ParseTime(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return default;
        if (DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var dt))
            return dt;
        return default;
    }
}

/// <summary>
/// 分类归类。
///
/// ⚠️ 现实情况：**这游戏在 N 网上只有两个分类**（Scav Prototype / Miscellaneous），
/// 直接照搬等于整列只有两个值、毫无信息量；社区元数据里也没有分类字段。
/// 所以这里按 mod 名称 + 描述里的关键词做一次轻量归类，
/// 给用户一个「这是本地化 / 性能 / 界面 / 联机…」的可读标签。
/// 归不出来的一律「其他」，宁可空着也不瞎猜。
/// </summary>
public static class CategoryClassifier
{
    private static readonly (string Category, string[] Keywords)[] Rules =
    {
        ("本地化", new[] { "translat", "chinese", "locale", "i18n", "汉化", "中文", "language", "localiz" }),
        ("性能",   new[] { "optimiz", "performance", "fps", "profiler", "lag", "stutter", "cull" }),
        ("联机",   new[] { "multiplayer", "multi-player", "co-op", "coop", "mp ", "network", "联机", "session" }),
        ("界面UI", new[] { "ui", "hud", "menu", "inventory", "sort", "tooltip", "interface", "hotbar" }),
        ("音效",   new[] { "audio", "sound", "music", "voice", "animalese", "sfx" }),
        ("图形",   new[] { "sprite", "texture", "graphic", "shader", "visual", "skin", "model", "render" }),
        ("玩法",   new[] { "gameplay", "weapon", "gun", "item", "loot", "craft", "trap", "survival", "food", "hydrat" }),
        ("工具",   new[] { "manager", "tool", "console", "command", "spawner", "patch", "lib", "api", "framework", "core" }),
    };

    public static string Classify(CasualtiesManageableService.CmModEntry e)
    {
        var hay = (e.Name + " " + e.Summary + " " + string.Join(" ", e.DllNames)).ToLowerInvariant();
        foreach (var (cat, keys) in Rules)
            if (keys.Any(k => hay.Contains(k)))
                return cat;
        return "其他";
    }

    /// <summary>GameBanana 的条目若只给到平台名当分类，同样按关键词兜底</summary>
    public static string ClassifyLoose(string name, string summary)
    {
        var hay = (name + " " + summary).ToLowerInvariant();
        foreach (var (cat, keys) in Rules)
            if (keys.Any(k => hay.Contains(k)))
                return cat;
        return "其他";
    }
}
