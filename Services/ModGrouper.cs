using CUModUpdater.Models;

namespace CUModUpdater.Services;

/// <summary>
/// Mod 分组器：把「同一来源 mod 的多个 dll」归成一组。
///
/// 背景：N 网上很多 mod 是一个包带一串 dll——主插件 + 附属库（LiteNetLib、
/// OpusSharp.Core、Steamworks.NET 等）。它们在插件目录里各是一个文件，
/// 扫描后各占一行，看起来像 4 个独立 mod，实际同源。
/// 归组后只显示一行主条目，附属折叠在下面，也便于整组开关。
///
/// 组长的选取（按优先级）：
///   1. dll 名与来源 mod 名同名者（GunsawGenetics.dll ↔ "Gunsaw Genetics"）
///   2. 组内 GUID 命中来源元数据的条目（BepInPlugin 有登记，是真正的主插件）
///   3. 组内 dll 名与来源 mod 的 bepinexPlugins 键匹配者
///   4. 兜底：文件名字典序第一
/// </summary>
public static class ModGrouper
{
    /// <summary>文件夹视图的一个目录组（相对 plugins 根的路径 + 其下的文件）</summary>
    public sealed record FolderGroup(string RelPath, List<ModInfo> Files)
    {
        /// <summary>显示名（根目录给个人话名字）</summary>
        public string DisplayName => RelPath.Length == 0 ? "plugins 根目录" : RelPath;

        /// <summary>分组键（供折叠记忆）</summary>
        public string Key => "dir:" + RelPath.ToLowerInvariant();

        /// <summary>目录层级（根 = 0），用于缩进</summary>
        public int Depth => RelPath.Length == 0 ? 0 : RelPath.Count(c => c == '\\') + 1;

        /// <summary>递归范围内的全部文件（含所有子目录）——目录节点的计数与批量开关用它</summary>
        public List<ModInfo> AllFiles { get; set; } = Files;
    }

    /// <summary>
    /// 按物理文件夹建组（管理视角，与 Alexx 的管理器一致的目录结构）。
    /// 每个目录一组（非递归——每个文件恰好属于自己所在的目录），
    /// 排序时父目录天然排在子目录前面（"Gameplay" < "Gameplay\X"），树形一眼可读。
    /// </summary>
    public static List<FolderGroup> BuildFolderGroups(List<ModInfo> mods, string pluginsRoot)
    {
        var byDir = new Dictionary<string, List<ModInfo>>(StringComparer.OrdinalIgnoreCase);

        foreach (var m in mods)
        {
            var dir = Path.GetDirectoryName(m.IsDataMod ? m.FlagFilePath : m.FilePath) ?? "";
            if (string.IsNullOrEmpty(pluginsRoot)) continue;

            // 统一成相对路径（分隔符用 \，方便阅读）
            var rel = Path.GetRelativePath(pluginsRoot, dir);
            if (rel == ".") rel = "";
            rel = rel.Replace('/', '\\');

            if (!byDir.TryGetValue(rel, out var list))
                byDir[rel] = list = new List<ModInfo>();
            list.Add(m);
        }

        var groups = byDir
            .Select(kv => new FolderGroup(kv.Key,
                kv.Value.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList()))
            .OrderBy(g => g.RelPath.Length == 0 ? 0 : 1)
            .ThenBy(g => g.RelPath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // 递归文件集：目录节点的「X 个文件」计数与批量开关都按递归算
        // （与 Alexx 的管理器一致：Gameplay (28 个文件) = 含所有子目录）。
        // 父目录的键是子目录键的前缀（"Gameplay" < "Gameplay\X"）。
        foreach (var g in groups)
        {
            if (g.RelPath.Length == 0)
            {
                g.AllFiles = groups.SelectMany(x => x.Files).Distinct().ToList();
                continue;
            }
            var prefix = g.RelPath + "\\";
            g.AllFiles = groups
                .Where(x => x.RelPath == g.RelPath || x.RelPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .SelectMany(x => x.Files)
                .Distinct()
                .ToList();
        }

        return groups;
    }
    /// <summary>
    /// 就地给 mods 打分组标记。返回「顶层可见条目」（每个组只留组长）。
    /// </summary>
    public static List<ModInfo> Group(List<ModInfo> mods)
    {
        // 重置分组状态（重复扫描时避免残留）
        foreach (var m in mods)
        {
            m.GroupKey = null;
            m.GroupName = null;
            m.IsGroupHead = true;
            m.GroupSize = 1;
            m.GroupOwner = null;
            m.GroupMembers = new List<ModInfo>();
            m.IsDuplicateFile = false;
            m.DuplicateOf = null;
            m.HasNameConflict = false;
            m.ConflictWith = null;
        }

        // ── 同名文件检测 ──
        // 判据：归一化文件名相同（去 _disabled、去加载顺序前缀 0_、去目录、忽略大小写）。
        //
        // ⚠️ 同名 ≠ 同一个 mod，必须按 GUID 再分两类：
        //   · GUID 相同   → 「同一插件的重复副本」（升级残留 / 手滑装了两份），
        //                   BepInEx 会重复加载同一插件，可能 GUID 冲突 → 建议清理一份；
        //   · GUID 不同   → 「两个不同 mod 抢同一个文件名」。
        //                   典型：Gunsaw Genetics 自带**修改过的** body_sprite_replacer.dll
        //                   （GUID spritereplacer v1.1.0），而 plugins 根目录可能是
        //                   原版 Player Sprite Replacer（GUID com.yourname.spritereplacer
        //                   v1.7.0）—— 作者的 README 明确说要覆盖原版。这不是重复安装，
        //                   标成「重复副本」会误导用户去删错文件。
        // 保留规则：启用态优先 → 路径层级最浅（plugins 根 = 规范位置）→ 字典序。
        foreach (var bucket in mods
            .Where(m => !string.IsNullOrEmpty(m.FileName))
            .GroupBy(m => NormalizeDllName(m.FileName), StringComparer.OrdinalIgnoreCase))
        {
            var list = bucket.ToList();
            if (list.Count < 2) continue;

            var keep = list
                .OrderByDescending(m => m.IsEnabled)              // 启用态优先（别把启用的标成副本）
                .ThenBy(m => Depth(m.FilePath))                   // 其次最外层（plugins 根 = 规范安装位置）
                .ThenBy(m => m.FilePath, StringComparer.OrdinalIgnoreCase)
                .First();

            foreach (var m in list)
            {
                if (ReferenceEquals(m, keep)) continue;

                var samePlugin =
                    !string.IsNullOrEmpty(m.PluginGuid) &&
                    !string.IsNullOrEmpty(keep.PluginGuid) &&
                    string.Equals(m.PluginGuid, keep.PluginGuid, StringComparison.OrdinalIgnoreCase);

                if (samePlugin)
                {
                    m.IsDuplicateFile = true;
                    m.DuplicateOf = keep;
                }
                else
                {
                    m.HasNameConflict = true;
                    m.ConflictWith = keep;
                }
            }
        }

        // 仅对有有效来源的条目分组（无源/自有 mod 各自独立）
        var withSource = mods
            .Where(m => m.Source != null && m.Source.IsValid)
            .ToList();

        var groups = withSource
            .GroupBy(GroupKeyOf, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var visible = new List<ModInfo>();

        foreach (var g in groups)
        {
            // 正式文件（非重复副本）与重复副本分开：
            // 组内正式文件已由全局检测保证「每个 dll 名只有一份」。
            var primary = g.Where(m => !m.IsDuplicateFile)
                           .OrderBy(m => m.FileName, StringComparer.OrdinalIgnoreCase)
                           .ToList();
            var dups = g.Where(m => m.IsDuplicateFile)
                        .OrderBy(m => m.FileName, StringComparer.OrdinalIgnoreCase)
                        .ToList();

            // 组长始终从正式文件里挑
            var members = primary.Concat(dups).ToList();

            if (members.Count == 1)
            {
                // 单条目组：不打组标记，直接可见
                members[0].GroupKey = null;
                members[0].GroupSize = 1;
                members[0].IsGroupHead = true;
                visible.Add(members[0]);
                continue;
            }

            // 多条目：选组长
            var head = PickHead(primary.Count > 0 ? primary : members, g.Key);
            var groupName = Services.CasualtiesManageableService.GetModNameById(head.Source?.ModId)
                            ?? head.Name;

            head.GroupKey = g.Key;
            head.GroupName = groupName;
            head.IsGroupHead = true;
            head.GroupSize = members.Count;
            head.GroupMembers = members;

            foreach (var m in members)
            {
                m.GroupKey = g.Key;
                m.GroupName = groupName;
                m.IsGroupHead = ReferenceEquals(m, head);
                m.GroupSize = members.Count;
                m.GroupOwner = head;
            }

            visible.Add(head);
        }

        // 无来源条目照常可见（保持原顺序靠后）
        visible.AddRange(mods.Where(m => m.Source == null || !m.Source.IsValid));

        // 稳定排序：有更新的 / 启用优先，再看名称
        return visible
            .OrderByDescending(m => m.IsEnabled)
            .ThenBy(m => m.Status == UpdateStatus.UpdateAvailable ? 0 : 1)
            .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// 组键必须能唯一标识「同一个上游 mod」。
    ///
    /// ⚠️ 不能写成 `$"{Type}:{ModId}"`：**GitHub 来源的 ModId 是空字符串**，
    /// 于是所有 GitHub 来源的 mod 共用 `"github:"` 这一个键，被并成一个巨型组
    /// （实测 CUCoreLib / QoL Unknown / i18n Auto Updater 三个不相干的 mod
    ///   全挤进了同一个组）。GitHub 必须用 Owner/Repo 作键。
    /// </summary>
    private static string GroupKeyOf(ModInfo m)
    {
        var s = m.Source!;
        return s.Type?.ToLowerInvariant() switch
        {
            "github" => $"github:{s.Owner}/{s.Repo}".ToLowerInvariant(),
            _ => $"{s.Type}:{s.ModId}".ToLowerInvariant(),
        };
    }

    /// <summary>从组内挑出主条目</summary>
    private static ModInfo PickHead(List<ModInfo> members, string groupKey)
    {
        // 1. dll 名与来源 mod 名同名
        var modName = Services.CasualtiesManageableService.GetModNameById(
            members[0].Source?.ModId);
        if (!string.IsNullOrEmpty(modName))
        {
            var normMod = Norm(modName);
            foreach (var m in members)
            {
                var stem = Norm(Path.GetFileNameWithoutExtension(
                    m.FileName.Replace("_disabled", "")));
                if (!string.IsNullOrEmpty(stem) && stem == normMod)
                    return m;
            }
        }

        // 2. 组内有 GUID 且能命中元数据的（说明是登记过的主插件）
        var byGuid = members.FirstOrDefault(m =>
            !string.IsNullOrEmpty(m.PluginGuid) &&
            Services.CasualtiesManageableService.FindByGuidCached(m.PluginGuid) != null);
        if (byGuid != null) return byGuid;

        // 3. 兜底：字典序第一（members 进来前已排序）
        return members[0];
    }

    private static string Norm(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s.ToLowerInvariant())
            if (char.IsLetterOrDigit(c)) sb.Append(c);
        return sb.ToString();
    }

    /// <summary>路径层级深度（分隔符个数）——用来判断哪一份更靠近 plugins 根目录</summary>
    private static int Depth(string path) => path.Count(c => c == '\\' || c == '/');

    /// <summary>
    /// 归一化 dll 文件名用于「同名」比较。
    /// 去 _disabled 后缀、去目录，并**剥掉 BepInEx 的加载顺序前缀**（0_ / 00_ / 1_ …）。
    /// 实例：Chekushka Mod 同时带 `0_RshLib.dll` 和根目录的 `RshLib.dll`、
    /// `0_CUCoreLib.dll` 和 `CUCoreLib.dll` —— 不剥前缀的话同名检测完全失灵。
    /// </summary>
    private static string NormalizeDllName(string fileName)
    {
        var n = Path.GetFileName(fileName);
        if (n.EndsWith("_disabled", StringComparison.OrdinalIgnoreCase))
            n = n[..^"_disabled".Length];
        var m = System.Text.RegularExpressions.Regex.Match(n, @"^\d{1,3}_(?<rest>.+)$");
        if (m.Success) n = m.Groups["rest"].Value;
        return n;
    }
}
