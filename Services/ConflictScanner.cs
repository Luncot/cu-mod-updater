using CUModUpdater.Models;

namespace CUModUpdater.Services;

/// <summary>
/// 冲突扫描 —— 只做「能确定判断的」那几类，不做猜测。
///
/// 为什么要单独做：BepInEx 对冲突几乎全是静默的。同一个插件被加载两次、
/// 两个 mod 抢同一个 dll 名、更新后旧文件没删干净 —— 表现都是「功能怪怪的」
/// 或「改了配置没反应」，日志里什么都不报。所以要在文件层面先把这些挑出来。
/// </summary>
public static class ConflictScanner
{
    public enum Severity { High, Medium, Info }

    public sealed class Finding
    {
        public Severity Level { get; init; }
        public string Category { get; init; } = "";
        public string Title { get; init; } = "";
        public string Detail { get; init; } = "";
        public List<string> Files { get; init; } = new();
        public string Advice { get; init; } = "";

        public string LevelText => Level switch
        {
            Severity.High => "⚠ 高",
            Severity.Medium => "· 中",
            _ => "  i",
        };
    }

    /// <summary>
    /// 分析已扫描到的插件列表，返回全部冲突项。
    /// </summary>
    /// <param name="mods">PluginScanner.Scan() 的结果</param>
    /// <param name="pluginsRoot">BepInEx\plugins，仅用于显示相对路径</param>
    public static List<Finding> Scan(IReadOnlyList<ModInfo> mods, string pluginsRoot)
    {
        var result = new List<Finding>();

        // 库文件（无 BepInPlugin）与数据 Mod 不参与插件级判断
        var plugins = mods
            .Where(m => m.IsBepInExPlugin && !m.IsFolderNode && !m.IsDuplicateFile)
            .ToList();

        string Rel(string p)
        {
            try { return Path.GetRelativePath(pluginsRoot, p); }
            catch { return p; }
        }

        // ── ① 同一个 GUID 出现多份 ──
        // BepInEx 会把两份都加载，Harmony 补丁叠加执行 → 数值翻倍、事件触发两次。
        foreach (var g in plugins.GroupBy(m => m.PluginGuid, StringComparer.OrdinalIgnoreCase)
                                 .Where(g => g.Count() > 1))
        {
            var list = g.OrderByDescending(m => m.IsEnabled).ThenBy(m => m.FileName).ToList();
            var enabled = list.Count(m => m.IsEnabled);
            var versions = list.Select(m => m.CompareVersion).Distinct().ToList();

            result.Add(new Finding
            {
                Level = enabled > 1 ? Severity.High : Severity.Medium,
                Category = "重复 GUID",
                Title = $"{list[0].Name}（{g.Key}）有 {list.Count} 份",
                Detail = (enabled > 1
                    ? $"其中 {enabled} 份处于启用状态，BepInEx 会重复加载同一个插件，补丁叠加执行。"
                    : "只有一份启用，其余是禁用副本；问题是它们占着同一个 GUID，容易在切换时留下两份启用的。")
                    + (versions.Count > 1 ? $"\n涉及版本：{string.Join(" / ", versions)}" : "")
                    + "\n" + string.Join("\n", list.Select(m =>
                        $"  {Rel(m.FilePath)}   版本={m.DisplayVersion}   启用={m.IsEnabled}")),
                Files = list.Select(m => Rel(m.FilePath)).ToList(),
                Advice = enabled > 1
                    ? "保留一份（版本最新、路径最靠近 plugins 根目录的那份），其余禁用或移入 .cleanup。"
                    : "确认保留哪份后，把多余的删掉或移到 BepInEx\\.cleanup。",
            });
        }

        // ── ② 同名 dll、不同 GUID ──
        // 两个不同的插件用了同一个文件名（通常是从不同 mod 包里各拿了一份）。
        foreach (var g in plugins.GroupBy(m => Path.GetFileNameWithoutExtension(
                     m.FileName.Replace("_disabled", "")), StringComparer.OrdinalIgnoreCase)
                 .Where(g => g.Select(m => m.PluginGuid).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1))
        {
            var list = g.ToList();
            result.Add(new Finding
            {
                Level = Severity.High,
                Category = "同名冲突",
                Title = $"{Path.GetFileNameWithoutExtension(list[0].FileName)}.dll 被 {list.Count} 个不同插件占用",
                Detail = string.Join("\n", list.Select(m =>
                    $"  {Rel(m.FilePath)}   GUID={m.PluginGuid}   版本={m.DisplayVersion}")),
                Files = list.Select(m => Rel(m.FilePath)).ToList(),
                Advice = "同名文件在同一个目录里只能存在一份，后加载的会覆盖前一个。留需要的那个，另一个改名或移走。",
            });
        }

        // ── ③ 同一 GUID 的多个版本并存（更新没删干净 / 作者改了 dll 名） ──
        // 这类最容易骗人：界面上看到一个 mod 只显示一份，磁盘上却躺着两个版本。
        foreach (var g in plugins.GroupBy(m => m.PluginGuid, StringComparer.OrdinalIgnoreCase))
        {
            var versions = g.Select(m => m.CompareVersion)
                            .Where(v => !string.IsNullOrEmpty(v))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();
            if (versions.Count < 2) continue;

            // 已被「重复 GUID」报过的同一组，不重复列（用户看两条会以为有两件事）
            if (result.Any(f => f.Category == "重复 GUID" && f.Title.Contains(g.Key))) continue;

            var list = g.ToList();
            result.Add(new Finding
            {
                Level = Severity.High,
                Category = "多版本并存",
                Title = $"{list[0].Name} 同时存在 {versions.Count} 个版本（{string.Join(" / ", versions)}）",
                Detail = string.Join("\n", list.Select(m =>
                    $"  {Rel(m.FilePath)}   版本={m.DisplayVersion}   启用={m.IsEnabled}")),
                Files = list.Select(m => Rel(m.FilePath)).ToList(),
                Advice = "多半是更新时作者改了 dll 文件名，旧文件没被覆盖。删掉旧版本（或移入 .cleanup），只留新版本。",
            });
        }

        // ── ④ 同一 mod 的插件被拆到多个目录（BepInEx 会各地加载，顺序不定） ──
        foreach (var g in plugins.GroupBy(m => m.PluginGuid, StringComparer.OrdinalIgnoreCase)
                                 .Where(g => g.Count() > 1))
        {
            var dirs = g.Select(m => Path.GetDirectoryName(Rel(m.FilePath)) ?? "").Distinct().ToList();
            if (dirs.Count < 2) continue;
            // 已被 ① / ③ 报过的不重复报
            if (result.Any(f => (f.Category is "重复 GUID" or "多版本并存") && f.Title.Contains(g.Key))) continue;

            result.Add(new Finding
            {
                Level = Severity.Medium,
                Category = "跨目录重复",
                Title = $"{g.First().Name}（{g.Key}）的 dll 分散在 {dirs.Count} 个目录",
                Detail = string.Join("\n", g.Select(m => $"  {Rel(m.FilePath)}")),
                Files = g.Select(m => Rel(m.FilePath)).ToList(),
                Advice = "合成一个目录，避免更新时只替换了其中一份。",
            });
        }

        // ── ⑤ 数据 Mod 没有前置 API ──
        // 数据 Mod（JSON 物品包）必须靠前置读取；缺前置时游戏里什么都不出现，且不报错。
        var dataMods = mods.Where(m => m.IsDataMod).ToList();
        if (dataMods.Count > 0)
        {
            var knownApi = plugins.Where(m => m.IsEnabled && m.IsBepInExPlugin)
                .Any(m => m.PluginGuid.Contains("ice", StringComparison.OrdinalIgnoreCase)
                          || m.Name.Contains("ICEneco", StringComparison.OrdinalIgnoreCase)
                          || m.Name.Contains("Item API", StringComparison.OrdinalIgnoreCase));
            if (!knownApi)
            {
                result.Add(new Finding
                {
                    Level = Severity.Medium,
                    Category = "缺前置",
                    Title = $"检测到 {dataMods.Count} 个数据 Mod，但没找到读取它们的 API 插件",
                    Detail = string.Join("\n", dataMods.Take(8).Select(m => $"  {Rel(m.FilePath)}")),
                    Files = dataMods.Select(m => Rel(m.FilePath)).ToList(),
                    Advice = "数据 Mod 需要前置 API（如 ICEneco Custom Item API）才会生效，缺了在游戏里不会报错、只是什么都不出现。",
                });
            }
        }

        // ── ⑥ 备份目录里的 dll 被误放到 plugins 根下 ──
        var strays = mods.Where(m => !m.IsFolderNode && m.IsEnabled
                                     && m.FileName.StartsWith(".")).ToList();
        if (strays.Count > 0)
        {
            result.Add(new Finding
            {
                Level = Severity.Info,
                Category = "可疑文件名",
                Title = $"{strays.Count} 个以点开头的 dll 处于启用状态",
                Detail = string.Join("\n", strays.Take(8).Select(m => $"  {Rel(m.FilePath)}")),
                Files = strays.Select(m => Rel(m.FilePath)).ToList(),
                Advice = "以 . 开头通常是备份/临时文件，BepInEx 仍会加载它。确认无用后删掉。",
            });
        }

        return result
            .OrderByDescending(f => f.Level)
            .ThenBy(f => f.Category)
            .ToList();
    }
}
