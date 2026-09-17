using System.Net.Http;
using System.Text.Json;
using CUModUpdater.Models;

namespace CUModUpdater.Services;

/// <summary>
/// CasualtiesManageable 官方元数据服务
/// 数据源: github.com/jimmyking9999999/Metadata-generator（每小时自动抓 N 网）
/// 就是游戏内置模组管理器（设置→模组 标签页）用的同一份数据。
/// 提供 GUID → N 网 Mod 的权威映射，彻底免去手动填写 mod id。
/// </summary>
public class CasualtiesManageableService : IDisposable
{
    private static readonly string DataUrl =
        "https://raw.githubusercontent.com/jimmyking9999999/Metadata-generator/main/nexusmods.json";

    private static readonly string CacheFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CU-ModUpdater", "casualties_manageable.json");

    /// <summary>缓存有效期（元数据每小时更新，1 小时足够新鲜）</summary>
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(1);

    private readonly HttpClient _client;

    /// <summary>一条 N 网 mod 元数据（只解析我们需要的字段）</summary>
    public class CmModEntry
    {
        public string Name { get; set; } = "";
        public string Version { get; set; } = "";
        public string NexusModId { get; set; } = "";
        public string PageUrl { get; set; } = "";
        /// <summary>该 mod 所属的 N 网游戏域名（元数据里通常就是 scavprototype）</summary>
        public string NexusGameDomain { get; set; } = "";
        /// <summary>作者名（N 网 Author 字段，用于纯数据 Mod 的名称+作者匹配）</summary>
        public string Author { get; set; } = "";
        /// <summary>N 网页面简介（英文）。介绍页面经常滞后，但至少能看出 mod 是干嘛的</summary>
        public string Summary { get; set; } = "";
        /// <summary>原始下载地址（nexus://域/modId/fileId 格式，cookie 下载流程需要其中的 fileId）</summary>
        public string DownloadNexusUrl { get; set; } = "";
        public Dictionary<string, string> BepInExPlugins { get; set; } = new();
        /// <summary>该 mod 包含的 dll 文件名（含附属库，元数据权威字段）</summary>
        public List<string> DllNames { get; set; } = new();

        /// <summary>N 网长描述 —— **BBCode + HTML 混排**，用之前必须 Markup.Strip() 洗一遍</summary>
        public string Description { get; set; } = "";

        /// <summary>N 网图库（可作缩略图/预览图；435/436 条都有）</summary>
        public List<string> Images { get; set; } = new();

        /// <summary>N 网统计（下载量/好评数）</summary>
        public CmStatistics? Statistics { get; set; }

        /// <summary>N 网最后更新时间（ISO 8601）。介绍页面经常没人维护，
        /// 这个时间戳才是「作者最近还管不管这个 mod」的可靠信号。</summary>
        public string LastUpdated { get; set; } = "";

        /// <summary>
        /// 更新日志（新→旧）。**mod 之间的兼容/冲突说明基本只写在这里**，
        /// 介绍页面经常偷懒不更新（例：#321 的 1.0.1 写着「兼容 KrokMP V4.0.1」）。
        /// </summary>
        public List<CmChangelogEntry> Changelogs { get; set; } = new();

        /// <summary>声明的依赖（形如 "nexus-341"，可解析成 mod 名）</summary>
        public List<string> Dependencies { get; set; } = new();

        /// <summary>取某个版本的更新日志；找不到精确版本时回退到最新一条</summary>
        public string? ChangelogFor(string? version)
        {
            if (Changelogs.Count == 0) return null;
            if (!string.IsNullOrWhiteSpace(version))
            {
                var hit = Changelogs.FirstOrDefault(c =>
                    string.Equals(c.Version?.Trim(), version.Trim(), StringComparison.OrdinalIgnoreCase));
                if (hit?.Text != null) return hit.Text;
            }
            return Changelogs[0].Text;
        }
    }

    /// <summary>N 网统计信息（元数据 Statistics 字段）</summary>
    public class CmStatistics
    {
        public long Endorsements { get; set; }
        public long UniqueDownloads { get; set; }
        public long TotalDownloads { get; set; }
    }

    /// <summary>一条更新日志</summary>
    public class CmChangelogEntry
    {        public string? Version { get; set; }
        public string? Text { get; set; }
    }

    public CasualtiesManageableService()
    {
        _client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("CU-ModUpdater/1.1.0");
    }

    /// <summary>
    /// 确保元数据可用。
    ///
    /// ⚠️ 关键语义：**过期 ≠ 不可用**。
    /// TTL 只用来决定「要不要刷新」，绝不能决定「能不能查」。
    /// 之前把两者混在一起：缓存写完 1 小时后 TryReadCache 开始返回 null，
    /// 于是**所有**元数据查询失败 → 一按检查更新全部变「? 未知」
    /// （错误信息还是误导性的「未找到任何发布版本」）。
    /// 陈旧的数据远比查不到强——宁可给出旧版本号，也不能让检查全军覆没。
    /// </summary>
    public async Task<bool> EnsureLoadedAsync()
    {
        // 1. 内存里有 → 直接用（静态缓存跨实例共享）
        if (_staticEntries != null)
        {
            // 过期就在后台刷新，不阻塞调用方
            if (DateTime.Now - CacheFileTime() > Ttl)
                _ = RefreshInBackgroundAsync();
            return true;
        }

        // 2. 磁盘缓存（无视年龄——有就能用）
        _staticEntries = TryReadCache(ignoreAge: true);

        // 3. 缓存缺失或过期 → 下载；失败则退回磁盘上的旧缓存
        if (_staticEntries == null || DateTime.Now - CacheFileTime() > Ttl)
        {
            try
            {
                var json = await _client.GetStringAsync(DataUrl);
                var parsed = Parse(json);
                if (parsed.Count == 0) throw new InvalidOperationException("元数据为空");
                _staticEntries = parsed;
                WriteCacheRaw(json);
            }
            catch
            {
                if (_staticEntries == null)
                    return false;   // 缓存和网都没有，真的不可用
                // 有旧缓存 → 继续用，别让检查挂掉
            }
        }

        return _staticEntries != null;
    }

    private async Task RefreshInBackgroundAsync()
    {
        try
        {
            var json = await _client.GetStringAsync(DataUrl);
            var parsed = Parse(json);
            if (parsed.Count == 0) return;
            _staticEntries = parsed;
            WriteCacheRaw(json);
        }
        catch { /* 后台刷新失败，继续用现有数据 */ }
    }

    /// <summary>缓存文件时间（取磁盘 mtime，比解析 FetchedAt 稳）</summary>
    private static DateTime CacheFileTime() =>
        File.Exists(CacheFile) ? File.GetLastWriteTime(CacheFile) : DateTime.MinValue;

    /// <summary>
    /// 内存缓存（静态：静态查询方法与实例共用同一份）。
    /// 一旦加载就常驻 —— 元数据只有 433 条，内存占用可忽略；
    /// 换来的好处是查询路径永远不会因为「忘了加载」而全军覆没。
    /// </summary>
    private static List<CmModEntry>? _staticEntries;

    /// <summary>元数据是否可用（供调用方在检查前判断并给出明确的错误提示）</summary>
    public static bool IsAvailable => _staticEntries != null || TryReadCache(ignoreAge: true) != null;

    /// <summary>
    /// 查询路径统一入口：优先内存缓存，没有就读磁盘（**无视年龄**）。
    /// 年龄只该影响「要不要刷新」，绝不该影响「能不能查」。
    /// </summary>
    private static List<CmModEntry>? GetEntries()
    {
        if (_staticEntries != null) return _staticEntries;
        _staticEntries = TryReadCache(ignoreAge: true);
        return _staticEntries;
    }

    /// <summary>确保已加载（同步版：只读磁盘，不下载。检查更新前调用可避免「元数据还没加载」的空窗）</summary>
    public static void EnsureLoadedSync()
    {
        if (_staticEntries != null) return;
        _staticEntries = TryReadCache(ignoreAge: true);
    }

    /// <summary>
    /// 取全部元数据条目（436 条，含作者/版本/图片/描述/下载量）。
    /// 这是**唯一一份覆盖全游戏的 mod 清单**——N 网 API 没有「列出全部 mod」
    /// 的端点，只能用这些榜单（最新/热门各十几条），所以浏览器的主列表应该
    /// 建立在这份元数据上，而不是 API 榜单上。
    /// </summary>
    public static List<CmModEntry> GetAllEntries()
    {
        EnsureLoadedSync();
        return _staticEntries ?? new List<CmModEntry>();
    }

    /// <summary>
    /// 人工补录表：元数据仓库尚未收录、但已核实过 N 网来源的 Mod。
    ///
    /// 社区元数据仓库（Metadata-generator）有抓取滞后——新发布的 mod 要等
    /// 下一次定时抓取才会进 `nexusmods.json`。这类 mod 会一直显示「未配置 / 无源」，
    /// 用户以为程序坏了。核实后补录在这里，即可自动配置来源。
    ///
    /// 键 = BepInPlugin GUID（忽略大小写）；值 = (N 网 ModId, 已知最新版本)。
    /// 已知版本可为空 —— 留空时仍能配置来源，但版本检查会走 Nexus API。
    /// </summary>
    private sealed record ManualNexusEntry(string ModId, string? LatestVersion = null);

    private static readonly Dictionary<string, ManualNexusEntry> ManualNexusFallbacks =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // GraphicMods by Staili（N 网 #597）
            // 2026-09-11 核对元数据 433 条无此项（该仓库对 #597 抓取滞后）。
            // 已知最新文件版本取自 N 网下载文件名：
            //   "GraphicMods 597 0.7.8 2026-08-24T18-20Z CcKAzeo9E.7z"
            ["staili.casualties.graphicmods"] = new("597", "0.7.8"),
        };

    /// <summary>
    /// 同步版：只读磁盘缓存做 GUID 匹配（不做网络 IO，供扫描线程调用）。
    /// 缓存不存在/过期时返回 null——预热请先调用实例的 EnsureLoadedAsync。
    /// 元数据未收录时回退到人工补录表（见 ManualNexusFallbacks）。
    /// </summary>
    public static ModSource? TryGetSourceCached(string? guid)
    {
        var entry = FindByGuidCached(guid);
        if (entry != null)
            return new ModSource { Type = "nexus", ModId = entry.NexusModId };

        // 人工补录兜底：元数据滞后时这些 mod 也能自动配置来源
        if (!string.IsNullOrEmpty(guid) &&
            ManualNexusFallbacks.TryGetValue(guid, out var manual))
        {
            return new ModSource { Type = "nexus", ModId = manual.ModId };
        }

        return null;
    }

    /// <summary>
    /// 同步版：按 dll 文件名匹配。仅「独占命中」可靠——
    /// 共享依赖库（LiteNetLib/CUCoreLib 等）与同名工具 dll 会命中多个条目，
    /// 这种情况返回 null，由调用方保留「待配置」状态而不是给出错误来源。
    /// </summary>
    public static ModSource? TryGetSourceByDllNameCached(string? fileName)
    {
        var entry = FindByDllNameCached(fileName);
        if (entry == null) return null;
        return new ModSource { Type = "nexus", ModId = entry.NexusModId };
    }

    /// <summary>元数据条目 → 更新器来源（数据 Mod 匹配链路用）</summary>
    public static ModSource? ToModSource(CmModEntry? entry)
    {
        if (entry == null || string.IsNullOrWhiteSpace(entry.NexusModId)) return null;
        return new ModSource { Type = "nexus", ModId = entry.NexusModId };
    }

    /// <summary>
    /// 按 dll 文件名查找元数据条目。
    ///
    /// 关键约束：**独占性判定**。同一个 dll 往往被多个 mod 条目登记，
    /// 典型如共享依赖库（CUCoreLib.dll 出现在 10+ 个 mod 的 dllNames 里）、
    /// 作者复用的工具名（body_sprite_replacer.dll 属于 4 个不同 mod）。
    /// 这种情况无法可靠推断归属，强行取「最专一」条目就是硬误报——
    /// 宁可不配（留给 GUID 通道或手动配置），也不能配错。
    /// </summary>
    public static CmModEntry? FindByDllNameCached(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return null;
        var entries = GetEntries();
        if (entries == null) return null;

        var baseName = Path.GetFileName(fileName).ToLowerInvariant();
        if (baseName.EndsWith("_disabled")) baseName = baseName[..^"_disabled".Length];

        // 1. 精确匹配（原文件名）
        var hits = CollectDllHits(entries, baseName);
        if (hits.Count > 0) return Decide(hits, baseName);

        // 2. 归一化匹配：本地文件名常被玩家加了中文/符号前缀
        //    （如「[合成拓展]CasualtiesCraft.dll」对应元数据的 CasualtiesCraft.dll）。
        //    剥掉非 ASCII 修饰、取英文词干后重试。
        foreach (var variant in NormalizeVariants(baseName))
        {
            var vhits = CollectDllHits(entries, variant);
            if (vhits.Count > 0) return Decide(vhits, variant);
        }

        return null;
    }

    /// <summary>收集所有声明了该 dll 名的非整合条目</summary>
    private static List<CmModEntry> CollectDllHits(List<CmModEntry> entries, string baseName)
    {
        var hits = new List<CmModEntry>();
        foreach (var m in entries)
        {
            // 跳过整合包/合集类（它们的 dllNames 覆盖上百个 dll，会把无关 mod 都吸过来）
            if (IsPackLike(m)) continue;
            foreach (var dn in m.DllNames)
            {
                if (Path.GetFileName(dn).ToLowerInvariant() == baseName)
                {
                    hits.Add(m);
                    break;
                }
            }
        }
        return hits;
    }

    /// <summary>
    /// 从「被修饰过的」文件名里生成候选变体。
    /// 例：「[合成拓展]casualtiescraft.dll」→ ["casualtiescraft.dll"]
    /// 规则：提取所有长度 ≥3 的 ASCII 字母数字词，逐个作为变体。
    /// </summary>
    private static IEnumerable<string> NormalizeVariants(string baseName)
    {
        var stem = Path.GetFileNameWithoutExtension(baseName);
        // 若整串已是纯 ASCII，无需变体（避免无意义的重扫）
        var matches = System.Text.RegularExpressions.Regex.Matches(
            stem, @"[a-z][a-z0-9_\.]{2,}", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        foreach (System.Text.RegularExpressions.Match m in matches)
        {
            var word = m.Value.ToLowerInvariant();
            if (word.Length >= 3)
                yield return word + ".dll";
        }
    }

    /// <summary>
    /// 从多个命中里决策归属（独占/同名/精度/拒绝猜测）。
    /// </summary>
    private static CmModEntry? Decide(List<CmModEntry> hits, string matchedDllName)
    {
        // 独占命中：唯一一个非整合包的条目声明了这个 dll —— 可以放心归它
        if (hits.Count == 1) return hits[0];

        // 多重命中：先看有没有「条目名与 dll 名同名」的（如 CUCoreLib.dll ↔ mod "CUCoreLib"），
        // 这是最强的归属信号，比 dllNames 数量启发式可靠得多
        var stem = Path.GetFileNameWithoutExtension(matchedDllName).ToLowerInvariant();
        foreach (var m in hits)
        {
            var modStem = System.Text.RegularExpressions.Regex
                .Replace(m.Name.ToLowerInvariant(), @"[^a-z0-9]", "");
            var dllStem = System.Text.RegularExpressions.Regex
                .Replace(stem, @"[^a-z0-9]", "");
            if (!string.IsNullOrEmpty(dllStem) && modStem == dllStem)
                return m;
        }

        // 其次：仅在「有一个条目明显更专一且其余都是宽泛条目」时才敢选。
        // 判定标准：最少 dllNames 数 < 次少者的一半，且不超过 3 个 dll。
        var sorted = hits.OrderBy(m => m.DllNames.Count).ToList();
        var first = sorted[0];
        if (first.DllNames.Count <= 3 && sorted[1].DllNames.Count >= first.DllNames.Count * 2)
            return first;

        return null; // 歧义 → 不猜
    }

    /// <summary>
    /// 该 dll 文件名是否在元数据里存在歧义（被多个非整合条目声明）。
    /// 供 UI 标黄提示「来源不确定」，避免用户看到错误来源却不知情。
    /// </summary>
    public static bool IsAmbiguousDllName(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return false;
        var entries = GetEntries();
        if (entries == null) return false;

        var baseName = Path.GetFileName(fileName).ToLowerInvariant();
        if (baseName.EndsWith("_disabled")) baseName = baseName[..^"_disabled".Length];

        int hits = 0;
        foreach (var m in entries)
        {
            if (IsPackLike(m)) continue;
            foreach (var dn in m.DllNames)
            {
                if (Path.GetFileName(dn).ToLowerInvariant() == baseName) { hits++; break; }
            }
            if (hits > 1) return true;
        }
        return false;
    }

    /// <summary>查 N 网 ModId 对应的官方名称（用于分组显示组名）</summary>
    public static string? GetModNameById(string? modId)
    {
        if (string.IsNullOrEmpty(modId)) return null;
        var entries = GetEntries();
        if (entries == null) return null;
        foreach (var m in entries)
        {
            if (m.NexusModId == modId) return m.Name;
        }
        return null;
    }

    /// <summary>
    /// 按「Mod 名称 + 作者」匹配元数据条目。
    /// 专供**纯数据 Mod**（无 dll、无 GUID）使用——它们的 dllNames 在元数据里是空数组，
    /// 只能靠名称/作者识别。例：
    ///   本地 Flag: { "modName": "Craftable Leg Pouch", "creator": "SillyLKH", "version": "1.0.0" }
    ///   元数据 #572: Name="Craftable Leg Pouch", Author="SillyLKH"
    /// </summary>
    public static CmModEntry? FindByNameAndAuthor(string? modName, string? author)
    {
        if (string.IsNullOrWhiteSpace(modName)) return null;
        var entries = GetEntries();
        if (entries == null) return null;

        var targetName = NormalizeName(modName);

        // 1. 名称 + 作者都匹配（最可靠）
        if (!string.IsNullOrWhiteSpace(author))
        {
            var targetAuthor = NormalizeName(author);
            foreach (var m in entries)
            {
                if (IsPackLike(m)) continue;
                if (NormalizeName(m.Name) == targetName &&
                    NormalizeName(m.Author) == targetAuthor)
                    return m;
            }
        }

        // 2. 仅名称匹配（要求名称有足够辨识度，避免 "Craftable X" 泛匹配）
        if (targetName.Length >= 8)
        {
            CmModEntry? hit = null;
            int count = 0;
            foreach (var m in entries)
            {
                if (IsPackLike(m)) continue;
                if (NormalizeName(m.Name) == targetName)
                {
                    hit = m;
                    count++;
                }
            }
            if (count == 1) return hit;   // 唯一命中才敢用
        }

        return null;
    }

    /// <summary>把名称归一化用于比较：小写、只留字母数字</summary>
    private static string NormalizeName(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s.ToLowerInvariant())
            if (char.IsLetterOrDigit(c)) sb.Append(c);
        return sb.ToString();
    }

    /// <summary>
    /// 按 N 网下载文件名里解析出的 ModId 直接查条目。
    /// N 网新版下载文件名格式：{Mod名} {ModId} {版本} {时间戳} {随机串}.zip
    /// </summary>
    public static CmModEntry? FindByModId(string? modId)
    {
        if (string.IsNullOrWhiteSpace(modId)) return null;
        var entries = GetEntries();
        if (entries == null) return null;
        foreach (var m in entries)
            if (m.NexusModId == modId) return m;
        return null;
    }

    /// <summary>
    /// 从 N 网下载文件名里解析 ModId。
    ///
    /// 文件名格式："{Mod名} {ModId} {版本} {yyyy-MM-ddTHH-mmZ} {随机串}.zip"
    /// 例：
    ///   "Craftable Leg Pouch 572 1 2026-08-17T09-00Z iw6VjMukO.zip"  → 572
    ///   "CuSaveManager 82 25 2026-08-05T02-57Z zGXMFWhKC.zip"        → 82
    ///   "KrokMPOptimization 599 5 2026-09-02T04-19Z H7iN12nv1.zip"  → 599
    ///
    /// 坑点：ModId 与「版本号」都可能是纯数字，而 Mod 名里也可能夹着数字
    /// （"Casualties Unknow V1 2 Solo 612 1.2 ..." 里的 "V1"、"2"）。
    /// 但 N 网的字段顺序是**固定**的：
    ///
    ///     {Mod名...}  {ModId}  {版本}  {时间戳}  {随机串}
    ///
    /// 所以只要时间戳在，紧邻它左边那一段就是「版本」，再往左一段就是「ModId」。
    /// 这条位置规则比任何猜测都可靠，唯一需要处理的是版本本身可能不含数字段
    /// （极少见），此时退化为「用元数据校验时间戳前所有候选」。
    /// </summary>
    public static string? ParseModIdFromDownloadFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (string.IsNullOrWhiteSpace(stem)) return null;

        var parts = stem.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) return null;

        // 定位 ISO 时间戳段
        int tsIndex = -1;
        for (int i = parts.Length - 1; i >= 0; i--)
        {
            if (IsIsoTimestamp(parts[i])) { tsIndex = i; break; }
        }

        // ── 主路径：时间戳存在 → 按固定位置取 ──
        // parts[tsIndex - 1] = 版本，parts[tsIndex - 2] = ModId
        if (tsIndex >= 2)
        {
            var versionSlot = parts[tsIndex - 1];
            var modIdSlot = parts[tsIndex - 2];

            // versionSlot 通常含数字（"1" / "1.2.1" / "0.7.8"）；modIdSlot 必须是纯数字
            if (RegexIsDigits(modIdSlot) && versionSlot.Any(char.IsDigit))
                return modIdSlot;
        }

        // ── 兜底：位置推断不成立时（老格式/字段缺失），用元数据校验 ──
        var searchEnd = tsIndex >= 0 ? tsIndex : parts.Length;
        var candidates = new List<string>();
        for (int i = 0; i < searchEnd; i++)
            if (RegexIsDigits(parts[i])) candidates.Add(parts[i]);

        if (candidates.Count == 0) return null;

        var entries = GetEntries();
        if (entries != null)
        {
            // 优先挑「不是常见版本号形态」的候选，再验证存在性
            for (int i = candidates.Count - 1; i >= 0; i--)
                foreach (var m in entries)
                    if (m.NexusModId == candidates[i]) return candidates[i];
        }

        // ── 兜底 2：老式文件名 "{Mod名}-{ModId}-{版本...}-{时间戳}.zip" ──
        // 例："Quick Pickup-175-1-0-2-1780784621.zip"（#175 Quick Pickup）
        // 连字符分隔且无 ISO 时间戳，用元数据直接校验各段数字。
        if (tsIndex < 0 && stem.Contains('-'))
        {
            var dashParts = stem.Split('-', StringSplitOptions.RemoveEmptyEntries);
            var numSegs = dashParts.Where(RegexIsDigits).ToList();
            var entries2 = GetEntries();
            if (entries2 != null)
            {
                // 从右往左（ModId 通常紧跟名称，在版本串之前）
                foreach (var seg in numSegs)
                    foreach (var m in entries2)
                        if (m.NexusModId == seg) return seg;
            }
        }

        // ── 兜底 3：没有任何依据时：右起第二个 = 跳过版本号 ──
        return candidates.Count >= 2 ? candidates[^2] : candidates[^1];
    }

    private static bool RegexIsDigits(string s)
        => s.Length > 0 && s.All(char.IsDigit);

    private static bool IsIsoTimestamp(string s)
        => System.Text.RegularExpressions.Regex.IsMatch(s, @"^\d{4}-\d{2}-\d{2}T\d{2}-\d{2}Z$");

    /// <summary>
    /// 查出该 dll 名对应的所有候选 mod（含歧义时多个结果）。
    /// 供 UI 在「来源不确定」时列出候选让用户选，而不是静默给一个猜测。
    /// </summary>
    public static List<(string ModId, string Name)> GetDllNameCandidates(string? fileName)
    {
        var result = new List<(string, string)>();
        if (string.IsNullOrEmpty(fileName)) return result;
        var entries = GetEntries();
        if (entries == null) return result;

        var baseName = Path.GetFileName(fileName).ToLowerInvariant();
        if (baseName.EndsWith("_disabled")) baseName = baseName[..^"_disabled".Length];

        foreach (var m in entries)
        {
            if (IsPackLike(m)) continue;
            foreach (var dn in m.DllNames)
            {
                if (Path.GetFileName(dn).ToLowerInvariant() == baseName)
                {
                    result.Add((m.NexusModId, m.Name));
                    break;
                }
            }
        }
        return result;
    }

    /// <summary>是否整合包/合集类（名字含 pack/collection 或 dllNames 过多）</summary>
    private static bool IsPackLike(CmModEntry m)
    {
        if (m.DllNames.Count > 20) return true;
        var n = m.Name.ToLowerInvariant();
        return n.Contains("modpack") || n.Contains("mod pack") || n.Contains("collection")
            || n.Contains("modded (") || n.Contains("bundle");
    }

    /// <summary>
    /// 判定是否是「自有/本地 mod」——元数据未收录，且是作者自用/自制的插件。
    /// 识别规则：文件名以 CHP- 开头（玩家自制工具组）、或 GUID 落在本地命名空间。
    ///
    /// ⚠️ 顺序很关键：**必须先反查元数据，查得到就一律不算自有**。
    /// 只看命名空间前缀会大面积误判——实测有 14 个公开发布的 mod 用了
    /// `local.` / `com.local.` 前缀，它们都是正经的 N 网作品：
    ///   com.local.krokmp.optimization2            → #599 Multiplayer Optimization Patch
    ///   local.casualtiesunknown.gracepickupbypass → #177 Multiplayer Inventory Load Fix
    ///   local.casualtiesunknown.randomshrapnelpieces → #75 Shrapnel Variants
    ///   com.local.duneworld → #643、com.local.armorbugfix → #642 …（共 14 个）
    /// 误判成自有后，这些 mod 会跳过来源匹配、版本检查也拿不到数据 → 显示「? 未知」。
    /// </summary>
    public static bool IsLocalMod(string? fileName, string? guid = null)
    {
        var name = Path.GetFileName(fileName ?? "").ToLowerInvariant();
        if (name.EndsWith("_disabled")) name = name[..^"_disabled".Length];

        // CHP-* 系列（自制工具组，元数据里 0 条收录）
        if (name.StartsWith("chp-", StringComparison.OrdinalIgnoreCase)) return true;

        var g = (guid ?? "").ToLowerInvariant();

        // 元数据登记过的 GUID → 一定是公开发布的 mod，不是自有
        if (g.Length > 0 && !IsPlaceholderGuid(g) && FindByGuidRaw(g) != null)
            return false;

        // 本地/私有命名空间
        if (g.StartsWith("local.") || g.StartsWith("com.local.") || g.StartsWith("private."))
            return true;

        return false;
    }

    /// <summary>
    /// 判定 GUID 是否为「模板占位符」——作者忘记改成自己的命名空间，
    /// 直接留了模板默认值。这类 GUID 没有任何匹配价值，且会被误写进配置。
    /// 常见形式：com.yourname.modname / com.yourName.modName / yourname.xxx /
    ///           com.example.* / mymod / ModName / plugin / com.mymod.*
    /// </summary>
    public static bool IsPlaceholderGuid(string? guid)
    {
        if (string.IsNullOrWhiteSpace(guid)) return true;
        var g = guid.Trim().ToLowerInvariant();

        // 模板里的占位词
        string[] markers = {
            "yourname", "your_name", "your-name", "yournamehere",
            "modname", "mod_name", "mymodname", "mymod", "my_mod",
            "example", "test", "todo", "changeme", "change_me",
            "namespace", "author", "company", "template",
        };
        foreach (var marker in markers)
            if (g.Contains(marker)) return true;

        // 纯占位词本身（整串就是一个通用词）
        if (g is "mod" or "plugin" or "mymod" or "test" or "sample" or "default") return true;

        // 没有任何点分段的短标识符（合法 GUID 一般含 "." 或足够有辨识度）
        if (!g.Contains('.') && g.Length <= 4) return true;

        return false;
    }

    /// <summary>
    /// 同步版 GUID 匹配 → ReleaseInfo（版本 + N 网页面链接）。
    /// 检查更新免 API Key 的核心：元数据自带每个 mod 的 N 网最新版本。
    /// 版本优先取 bepinexPlugins[guid]（BepInPlugin 权威版本），
    /// 元数据顶层 Version 字段是 N 网页面版本（常为脏数据，如 "25"、"1.0.0.0"）。
    /// </summary>
    public static ReleaseInfo? GetReleaseByGuidCached(string? guid, string? fileName = null)
    {
        // 自有 mod 不查元数据（元数据里没有，硬查可能被同名 dll 吸到别的条目）
        if (IsLocalMod(fileName, guid)) return null;

        // GUID 优先，dll 文件名兜底（兜底同样走独占判定，歧义即放弃）
        var entry = FindByGuidCached(guid) ?? FindByDllNameCached(fileName);
        if (entry == null)
        {
            // 人工补录兜底（元数据抓取滞后的 mod）
            if (!string.IsNullOrEmpty(guid) &&
                ManualNexusFallbacks.TryGetValue(guid, out var manual) &&
                !string.IsNullOrEmpty(manual.LatestVersion))
            {
                return new ReleaseInfo
                {
                    Version = manual.LatestVersion,
                    DownloadUrl = $"https://www.nexusmods.com/scavprototype/mods/{manual.ModId}",
                };
            }
            return null;
        }

        // 版本优先级：bepinexPlugins[guid] > bepinexPlugins 唯一值 > 顶层 Version
        string version = "";
        if (!string.IsNullOrEmpty(guid) &&
            entry.BepInExPlugins.TryGetValue(guid, out var pv) && !string.IsNullOrEmpty(pv))
        {
            version = pv;
        }
        else if (entry.BepInExPlugins.Count == 1)
        {
            version = entry.BepInExPlugins.Values.First();
        }
        else
        {
            version = entry.Version;
        }

        if (string.IsNullOrEmpty(version)) return null;

        return new ReleaseInfo
        {
            Version = version,
            // 保留 nexus:// 原始格式（cookie 下载流程解析 fileId 用）；
            // 无 cookie 时 InstallUpdateAsync 会打开 mod 页面，不会用这个 URL 下载
            DownloadUrl = string.IsNullOrEmpty(entry.DownloadNexusUrl) ? entry.PageUrl : entry.DownloadNexusUrl,
            AssetName = "",
            // 把目标版本的更新日志带上 —— mod 间的兼容/冲突说明基本只写在这里，
            // 介绍页面经常没人维护，用户不该为此手动开网页
            ReleaseNotes = FormatChangelog(entry, version),
        };
    }

    /// <summary>
    /// 按 ModId 直接取 ReleaseInfo。兜底通道，优先级低于 GUID 匹配。
    ///
    /// 两条来源都可能走到这里：
    ///   ① 纯数据 Mod —— 没有 GUID / dll，只有「名称 + 作者」定下来的 ModId；
    ///   ② GUID 匹配不上但来源已配置的（local.* 命名空间、作者忘改 GUID、
    ///      来源是历史遗留配置）—— 有 ModId 就别浪费，直接查。
    ///
    /// 版本取值：元数据 `bepinexPlugins` 只有一个键时用它的值
    /// （那是作者手维护的 BepInPlugin 版本，最权威），否则退回顶层 Version。
    /// 例：#55 顶层 Version="1"（脏数据）但插件版本是 1.7.0，取后者才不误报。
    /// </summary>
    public static ReleaseInfo? GetReleaseByModIdCached(string? modId)
    {
        if (string.IsNullOrWhiteSpace(modId)) return null;
        var entry = FindByModId(modId);
        if (entry == null) return null;

        var version = entry.BepInExPlugins.Count == 1
            ? entry.BepInExPlugins.Values.First()
            : entry.Version;
        if (string.IsNullOrEmpty(version) && entry.BepInExPlugins.Count > 0)
            version = entry.BepInExPlugins.Values.First();
        if (string.IsNullOrEmpty(version)) return null;

        return new ReleaseInfo
        {
            Version = version,
            DownloadUrl = string.IsNullOrEmpty(entry.DownloadNexusUrl) ? entry.PageUrl : entry.DownloadNexusUrl,
            AssetName = "",
            ReleaseNotes = FormatChangelog(entry, version),
        };
    }

    /// <summary>
    /// 把某版本的更新日志整理成可读文本（带版本号和最后更新时间）。
    /// 精确版本没写日志时回退到最新一条 —— 作者经常只在最新那条里提兼容性变化。
    /// </summary>
    public static string FormatChangelog(CmModEntry entry, string? version)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(entry.LastUpdated) &&
            DateTime.TryParse(entry.LastUpdated, out var lu))
        {
            parts.Add($"最后更新: {lu:yyyy-MM-dd}");
        }

        var log = entry.ChangelogFor(version);
        if (!string.IsNullOrWhiteSpace(log))
        {
            parts.Add($"—— {version} 更新日志 ——");
            parts.Add(log.Trim());
        }

        return string.Join(Environment.NewLine, parts);
    }

    /// <summary>
    /// 解析依赖声明（"nexus-341"）成可读文本。
    /// 依赖是冲突信息的另一半：mod 说它需要什么前置，装错版本就出问题。
    /// </summary>
    public static string DescribeDependencies(CmModEntry entry)
    {
        if (entry.Dependencies.Count == 0) return "";
        var names = new List<string>();
        foreach (var dep in entry.Dependencies)
        {
            var id = dep.Replace("nexus-", "", StringComparison.OrdinalIgnoreCase);
            var target = FindByModId(id);
            names.Add(target == null ? $"#{id}" : $"{target.Name} (#{target.NexusModId})");
        }
        return string.Join("、", names);
    }

    /// <summary>
    /// GUID 强匹配（占位符 GUID 无匹配价值，直接跳过走 dllName 通道）。
    /// 自有 mod 的 GUID 不参与匹配 —— 但「自有」的判定本身会反查元数据，
    /// 所以真正的匹配逻辑抽在 <see cref="FindByGuidRaw"/>，这里只做过滤。
    /// </summary>
    public static CmModEntry? FindByGuidCached(string? guid)
    {
        if (string.IsNullOrEmpty(guid)) return null;
        // 占位符 GUID（作者没改的模板默认值）不参与匹配
        if (IsPlaceholderGuid(guid)) return null;
        // 自有 mod 的 GUID 不参与元数据匹配
        if (IsLocalMod(null, guid)) return null;
        return FindByGuidRaw(guid);
    }

    /// <summary>
    /// GUID 匹配本体（**不含**「自有 mod」过滤）。
    /// 单独抽出来是因为 <see cref="IsLocalMod"/> 要先反查元数据才能判定是不是自有
    /// ——若它直接调本方法会 infinite recursion。
    /// </summary>
    public static CmModEntry? FindByGuidRaw(string? guid)
    {
        if (string.IsNullOrEmpty(guid)) return null;
        if (IsPlaceholderGuid(guid)) return null;
        var entries = GetEntries();
        if (entries == null) return null;

        CmModEntry? best = null;
        foreach (var m in entries)
        {
            if (!m.BepInExPlugins.ContainsKey(guid)) continue;
            // 整合包/合集会登记大量 GUID，不能让它吸走单个 mod
            if (IsPackLike(m)) continue;
            // 优先最专一的条目
            if (best == null || m.BepInExPlugins.Count < best.BepInExPlugins.Count)
                best = m;
        }
        return best;
    }

    /// <summary>
    /// 按 BepInPlugin GUID 精确匹配 N 网 Mod（强匹配，无猜测）
    /// </summary>
    public CmModEntry? TryMatchByGuid(string? guid)
    {
        if (_staticEntries == null || string.IsNullOrEmpty(guid)) return null;
        foreach (var m in _staticEntries)
        {
            if (m.BepInExPlugins.ContainsKey(guid))
                return m;
        }
        return null;
    }

    /// <summary>
    /// GUID 匹配并转换为 ModSource（N 网），匹配不上返回 null
    /// </summary>
    public ModSource? TryGetSource(string? guid)
    {
        var m = TryMatchByGuid(guid);
        if (m == null) return null;
        return new ModSource
        {
            Type = "nexus",
            ModId = m.NexusModId,
        };
    }

    // ==================== 解析与缓存 ====================

    private static List<CmModEntry> Parse(string json)
    {
        var result = new List<CmModEntry>();
        using var doc = JsonDocument.Parse(json);
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var entry = new CmModEntry
            {
                Name = GetString(el, "Name"),
                Version = GetString(el, "Version"),
                Author = GetString(el, "Author"),
                Summary = GetString(el, "Summary"),
            };
            if (el.TryGetProperty("NexusModId", out var nid))
                entry.NexusModId = nid.ValueKind == JsonValueKind.Number
                    ? nid.GetInt64().ToString() : nid.GetString() ?? "";
            if (string.IsNullOrEmpty(entry.NexusModId) && el.TryGetProperty("Id", out var idEl))
                entry.NexusModId = GetString(el, "Id").Replace("nexus-", "");

            if (el.TryGetProperty("Links", out var links) &&
                links.TryGetProperty("NexusMods", out var nm) && nm.ValueKind == JsonValueKind.String)
                entry.PageUrl = nm.GetString() ?? "";

            if (el.TryGetProperty("downloadUrl", out var dl2) && dl2.ValueKind == JsonValueKind.String)
                entry.DownloadNexusUrl = dl2.GetString() ?? "";
            else if (el.TryGetProperty("DownloadUrl", out var dl) && dl.ValueKind == JsonValueKind.String)
                entry.DownloadNexusUrl = dl.GetString() ?? "";

            if (el.TryGetProperty("dllNames", out var dns) && dns.ValueKind == JsonValueKind.Array)
            {
                foreach (var dn in dns.EnumerateArray())
                {
                    if (dn.ValueKind == JsonValueKind.String)
                    {
                        var s = dn.GetString();
                        if (!string.IsNullOrEmpty(s)) entry.DllNames.Add(s);
                    }
                }
            }

            if (el.TryGetProperty("bepinexPlugins", out var plugins) &&
                plugins.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in plugins.EnumerateObject())
                    entry.BepInExPlugins[p.Name] = p.Value.ValueKind == JsonValueKind.String
                        ? p.Value.GetString() ?? "" : p.Value.ToString();
            }

            // 最后更新时间（介绍页面没人维护时，这是判断作者是否还在管的唯一信号）
            entry.LastUpdated = GetString(el, "LastUpdated");

            // 长描述（BBCode + HTML 混排）、图库、下载统计 —— 浏览器的详情面板要用
            entry.Description = GetString(el, "Description");
            entry.NexusGameDomain = GetString(el, "NexusGameDomain");

            if (el.TryGetProperty("Images", out var imgs) && imgs.ValueKind == JsonValueKind.Array)
            {
                foreach (var im in imgs.EnumerateArray())
                {
                    if (im.ValueKind == JsonValueKind.String)
                    {
                        var s = im.GetString();
                        if (!string.IsNullOrEmpty(s)) entry.Images.Add(s);
                    }
                }
            }

            if (el.TryGetProperty("Statistics", out var st) && st.ValueKind == JsonValueKind.Object)
            {
                entry.Statistics = new CmStatistics
                {
                    Endorsements = GetLong(st, "Endorsements"),
                    UniqueDownloads = GetLong(st, "UniqueDownloads"),
                    TotalDownloads = GetLong(st, "TotalDownloads"),
                };
            }

            // 声明的依赖（"nexus-341" → 可解析成 mod 名）
            if (el.TryGetProperty("Dependencies", out var deps) && deps.ValueKind == JsonValueKind.Array)
            {
                foreach (var d in deps.EnumerateArray())
                {
                    if (d.ValueKind == JsonValueKind.String)
                    {
                        var s = d.GetString();
                        if (!string.IsNullOrEmpty(s)) entry.Dependencies.Add(s);
                    }
                }
            }

            // 更新日志（mod 间兼容/冲突说明基本只写在这里）
            if (el.TryGetProperty("Changelogs", out var cls) && cls.ValueKind == JsonValueKind.Array)
            {
                foreach (var cl in cls.EnumerateArray())
                {
                    var ce = new CmChangelogEntry
                    {
                        Version = cl.TryGetProperty("Version", out var cv) && cv.ValueKind == JsonValueKind.String
                            ? cv.GetString() : null,
                        Text = cl.TryGetProperty("Changelog", out var ct) && ct.ValueKind == JsonValueKind.String
                            ? ct.GetString() : null,
                    };
                    if (!string.IsNullOrWhiteSpace(ce.Text)) entry.Changelogs.Add(ce);
                }
            }

            if (!string.IsNullOrEmpty(entry.NexusModId))
                result.Add(entry);
        }
        return result;
    }

    private sealed class CacheFile_
    {
        public DateTime FetchedAt { get; set; }
        public List<CmModEntry> Entries { get; set; } = new();
    }

    /// <summary>
    /// 读缓存：缓存存的是「原始元数据 JSON + FetchedAt 包装」，
    /// 统一用 Parse() 解析（自动处理数字型 NexusModId、dllNames、bepinexPlugins 等异构字段，
    /// 避免直接反序列化 CmModEntry 时因类型不匹配整批失败）。
    /// </summary>
    private static List<CmModEntry>? TryReadCache(bool ignoreAge = false)
    {
        try
        {
            if (!File.Exists(CacheFile)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(CacheFile));
            var root = doc.RootElement;

            if (root.TryGetProperty("FetchedAt", out var fa))
            {
                DateTime fetchedAt = fa.ValueKind == JsonValueKind.String
                    ? (fa.TryGetDateTime(out var dt) ? dt : DateTime.MinValue)
                    : DateTime.MinValue;
                if (!ignoreAge && DateTime.Now - fetchedAt > Ttl) return null;
            }

            if (!root.TryGetProperty("Entries", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return null;

            var entries = Parse(arr.GetRawText());
            return entries.Count > 0 ? entries : null;
        }
        catch { return null; }
    }

    /// <summary>写缓存：保留原始元数据 JSON 结构（解析统一走 Parse）</summary>
    private static void WriteCacheRaw(string rawJson)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CacheFile)!);
            var wrapper = $"{{\"FetchedAt\":\"{DateTime.Now:O}\",\"Entries\":{rawJson}}}";
            File.WriteAllText(CacheFile, wrapper);
        }
        catch { }
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

    public void Dispose() => _client.Dispose();
}
