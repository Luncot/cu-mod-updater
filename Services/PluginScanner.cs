using Mono.Cecil;
using CUModUpdater.Models;

namespace CUModUpdater.Services;

/// <summary>
/// 使用 Mono.Cecil 扫描 BepInEx/plugins 目录下的所有 Mod
/// 不加载 DLL 到 AppDomain，仅读取元数据（BepInPlugin 属性、程序集版本）
/// </summary>
public class PluginScanner
{
    private readonly string _pluginsPath;
    private readonly string _corePath;

    /// <summary>扫描进度回调</summary>
    public Action<string, int, int>? OnProgress { get; set; }

    public PluginScanner(string gamePath)
    {
        _pluginsPath = Path.Combine(gamePath, "BepInEx", "plugins");
        _corePath = Path.Combine(gamePath, "BepInEx", "core");
    }

    /// <summary>插件目录路径</summary>
    public string PluginsPath => _pluginsPath;

    /// <summary>插件目录是否存在</summary>
    public bool PluginsDirExists => Directory.Exists(_pluginsPath);

    /// <summary>
    /// 扫描所有 .dll 文件，提取 BepInEx 插件信息
    /// </summary>
    public List<ModInfo> Scan()
    {
        var mods = new List<ModInfo>();

        if (!Directory.Exists(_pluginsPath))
            throw new DirectoryNotFoundException($"BepInEx 插件目录不存在: {_pluginsPath}\n请确认游戏路径正确且已安装 BepInEx。");

        // 递归查找所有 .dll 文件（包括子目录）
        // 排除 .backup（更新器自己的备份目录，里面的旧版 dll 会被扫成重复 mod）
        var dllFiles = Directory.GetFiles(_pluginsPath, "*.dll*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".dll_disabled", StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.Replace('/', '\\').Contains("\\.backup\\", StringComparison.OrdinalIgnoreCase))
            .Distinct()
            .OrderBy(f => f)
            .ToList();

        OnProgress?.Invoke("正在扫描插件目录...", 0, dllFiles.Count);

        for (int i = 0; i < dllFiles.Count; i++)
        {
            var dllPath = dllFiles[i];
            OnProgress?.Invoke($"解析 {Path.GetFileName(dllPath)}", i + 1, dllFiles.Count);

            var mod = ScanDll(dllPath);
            if (mod != null)
                mods.Add(mod);
        }

        // 扫描「纯数据 Mod」——没有 dll，靠前置 API 读取文件夹内的 JSON。
        // 典型：ICEneco Custom Item API 的物品包（Craftable Leg Pouch 等）
        var dataMods = ScanDataMods();
        mods.AddRange(dataMods);

        OnProgress?.Invoke($"扫描完成（{mods.Count - dataMods.Count} 个插件 + {dataMods.Count} 个数据 Mod）",
            dllFiles.Count, dllFiles.Count);

        // 按名称排序，启用的排在前面
        return mods
            .OrderByDescending(m => m.IsEnabled)
            .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// 扫描「纯数据 Mod」：插件目录下含 ICEnecoCustomItemFlag.json 的文件夹。
    /// 这类 Mod 没有 dll，只能靠标识文件识别。
    /// </summary>
    public List<ModInfo> ScanDataMods()
    {
        var result = new List<ModInfo>();
        if (!Directory.Exists(_pluginsPath)) return result;

        IEnumerable<string> flags;
        try
        {
            flags = Directory.GetFiles(_pluginsPath, DataModFlagFileName, SearchOption.AllDirectories)
                .Where(f => !f.Replace('/', '\\').Contains("\\.backup\\", StringComparison.OrdinalIgnoreCase));
        }
        catch { return result; }

        foreach (var flagPath in flags)
        {
            var mod = ParseDataMod(flagPath);
            if (mod != null) result.Add(mod);
        }
        return result;
    }

    /// <summary>数据 Mod 的标识文件名（前置 API 靠它识别物品包）</summary>
    private const string DataModFlagFileName = "ICEnecoCustomItemFlag.json";

    /// <summary>
    /// 判断一个标识文件是否只是「作者模板 / 前置本体自带的空白样板」，而不是真正的物品包。
    ///
    /// ICEneco 前置本体（ICEnecoCustomItemAPI 文件夹旁边）会自带一份模板 Flag：
    ///   { "modName": "yourmod", "creator": "yourname", "version": 1.0 }
    /// 它是给作者改写的样板，本身不产生任何物品。若不排除，扫描器会把它
    /// 当成一个叫 "yourmod" 的数据 Mod 显示出来。
    ///
    /// 排除条件（任一命中即视为模板）：
    ///   1. 直接躺在 plugins 根目录下的 Flag —— 前置本体模板的固定位置；
    ///   2. modName / creator 仍是模板占位词（yourmod / yourname / modname ...）。
    /// </summary>
    private static bool IsTemplateFlag(string flagPath, string modName, string creator)
    {
        // 1. 直接躺在 plugins 根目录下的 Flag = 前置自带的样板
        //    （真实物品包必定放在自己的文件夹里）
        var parent = Path.GetDirectoryName(flagPath);
        if (!string.IsNullOrEmpty(parent) &&
            string.Equals(Path.GetFileName(parent), "plugins", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 2. 占位词检测
        var n = (modName ?? "").Trim().ToLowerInvariant().Replace(" ", "");
        var c = (creator ?? "").Trim().ToLowerInvariant().Replace(" ", "");
        if (n is "yourmod" or "yourmodname" or "modname" or "mymod" or "mymodname" or "example" or "template")
            return true;
        if (c is "yourname" or "yournamehere" or "author" or "creator" or "me")
            return true;

        return false;
    }

    /// <summary>
    /// 解析数据 Mod 的标识文件，提取 modName / creator / version。
    /// 文件夹名加 _disabled 后缀视为已禁用（与 dll 的约定一致）。
    /// </summary>
    private ModInfo? ParseDataMod(string flagPath)
    {
        try
        {
            var folder = Path.GetDirectoryName(flagPath) ?? "";
            var folderName = Path.GetFileName(folder);

            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(flagPath));
            var root = doc.RootElement;

            string Get(string name) =>
                root.TryGetProperty(name, out var p)
                    ? p.ValueKind switch
                    {
                        System.Text.Json.JsonValueKind.String => p.GetString() ?? "",
                        // version 有时写数字（如模板里的 "version": 1.0），一并兼容
                        System.Text.Json.JsonValueKind.Number => p.GetRawText(),
                        _ => ""
                    }
                    : "";

            var modName = Get("modName");
            if (string.IsNullOrEmpty(modName)) modName = folderName;
            var creator = Get("creator");
            var version = Get("version");

            // 前置本体自带的空白样板：不是真正的物品包，跳过
            if (IsTemplateFlag(flagPath, modName, creator))
                return null;

            var isEnabled = !folderName.EndsWith("_disabled", StringComparison.OrdinalIgnoreCase);
            var dirInfo = new DirectoryInfo(folder);

            return new ModInfo
            {
                Name = modName,
                PluginGuid = "",                       // 数据 Mod 没有 GUID
                CurrentVersion = version,
                FilePath = flagPath,                   // 以 Flag 文件代表该 Mod
                FileName = folderName,                 // 文件夹名作为「文件名」
                FolderPath = folder,
                IsEnabled = isEnabled,
                FileSize = dirInfo.Exists
                    ? dirInfo.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0,
                LastModified = dirInfo.Exists ? dirInfo.LastWriteTime : DateTime.MinValue,
                IsDataMod = true,
                DataModCreator = creator,
                FlagFilePath = flagPath,
                Status = isEnabled ? UpdateStatus.NotChecked : UpdateStatus.Disabled,
            };
        }
        catch
        {
            // Flag 文件损坏/格式异常 → 跳过，不影响其他 Mod
            return null;
        }
    }

    /// <summary>
    /// 解析单个 DLL 文件
    /// </summary>
    private ModInfo? ScanDll(string dllPath)
    {
        var fileName = Path.GetFileName(dllPath);
        var folderPath = Path.GetDirectoryName(dllPath) ?? "";
        var isEnabled = !fileName.EndsWith("_disabled", StringComparison.OrdinalIgnoreCase);
        var fileInfo = new FileInfo(dllPath);

        string? pluginGuid = null;
        string? pluginName = null;
        string? pluginVersion = null;
        string? asmVersion = null;

        try
        {
            // 设置程序集解析器，让 Cecil 能找到依赖程序集
            var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(folderPath);
            if (Directory.Exists(_corePath))
                resolver.AddSearchDirectory(_corePath);
            // 也搜索 Managed 目录（UnityEngine 等依赖）
            var managedDir = Path.Combine(
                Path.GetDirectoryName(Path.GetDirectoryName(_pluginsPath)) ?? "",
                "CasualtiesUnknown_Data", "Managed");
            if (Directory.Exists(managedDir))
                resolver.AddSearchDirectory(managedDir);

            // 使用 Deferred 模式，不加载整个程序集到内存（更快、更安全）
            var parameters = new ReaderParameters
            {
                AssemblyResolver = resolver,
                ReadingMode = ReadingMode.Deferred,
                ReadWrite = false,
                InMemory = true,
            };

            using var asm = AssemblyDefinition.ReadAssembly(dllPath, parameters);

            // 获取程序集版本
            asmVersion = asm.Name.Version?.ToString();

            // 遍历所有类型，查找 BepInPlugin 特性
            foreach (var type in asm.MainModule.Types)
            {
                CustomAttribute? bepAttr = type.CustomAttributes.FirstOrDefault(
                    a => a.AttributeType.Name == "BepInPluginAttribute" ||
                         a.AttributeType.FullName == "BepInEx.BepInPlugin");

                if (bepAttr != null && bepAttr.ConstructorArguments.Count >= 3)
                {
                    pluginGuid = bepAttr.ConstructorArguments[0].Value?.ToString();
                    pluginName = bepAttr.ConstructorArguments[1].Value?.ToString();
                    pluginVersion = ExtractVersionString(bepAttr.ConstructorArguments[2].Value);
                    break; // 只取第一个 BepInPlugin
                }
            }
        }
        catch (BadImageFormatException)
        {
            // 非 .NET 程序集（如原生 C++ DLL: steam_api64.dll, opus.dll）
            // 跳过，不列为 Mod
            return null;
        }
        catch
        {
            // 其他解析错误，也跳过
            return null;
        }

        // 如果既没有 BepInPlugin 也不是有效 .NET 程序集，跳过
        // 但如果它是 .NET 程序集（有 asmVersion），即使没有 BepInPlugin 也保留（可能是依赖库）
        if (string.IsNullOrEmpty(pluginGuid) && string.IsNullOrEmpty(asmVersion))
            return null;

        // 确定显示名称
        var displayName = !string.IsNullOrEmpty(pluginName) ? pluginName
                         : Path.GetFileNameWithoutExtension(fileName);
        // 去除 _disabled 后缀用于显示
        if (displayName.EndsWith("_disabled", StringComparison.OrdinalIgnoreCase))
            displayName = displayName[..^"_disabled".Length];

        return new ModInfo
        {
            Name = displayName,
            PluginGuid = pluginGuid ?? "",
            CurrentVersion = pluginVersion ?? "",
            FilePath = dllPath,
            FileName = fileName,
            FolderPath = folderPath,
            IsEnabled = isEnabled,
            FileSize = fileInfo.Length,
            LastModified = fileInfo.LastWriteTime,
            AssemblyVersion = asmVersion ?? "",
            Status = isEnabled ? UpdateStatus.NotChecked : UpdateStatus.Disabled,
        };
    }

    /// <summary>
    /// 从 BepInPlugin 特性的版本参数中提取版本字符串
    /// BepInPlugin 的 Version 参数类型是 string，但有些 Mod 可能用了 Version 对象
    /// </summary>
    private static string ExtractVersionString(object? value)
    {
        if (value == null) return "";
        return value switch
        {
            string s => s,
            System.Version v => v.ToString(),
            _ => value.ToString() ?? ""
        };
    }

    /// <summary>正式版 appid（2026-11-09 上线）</summary>
    public const string OfficialAppId = "4576490";

    /// <summary>Demo appid（作者已声明不再更新）</summary>
    public const string DemoAppId = "4576510";

    /// <summary>
    /// 是否为游戏安装目录 —— 只要求 CasualtiesUnknown.exe 存在。
    ///
    /// 2026-11-09 正式版上线后，安装目录名从「Casualties Unknown Demo」变成
    /// 「Casualties Unknown」，原先那批写死的候选路径一个都命中不了；
    /// 而且正式版是干净安装（还没有 BepInEx），用「必须存在 BepInEx」当门槛
    /// 会把刚买游戏的人全部挡在门外。所以这里只认 exe，
    /// 「能不能用」交给 IsValidGamePath。
    /// </summary>
    public static bool IsGameInstallDir(string? gamePath)
    {
        if (string.IsNullOrEmpty(gamePath) || !Directory.Exists(gamePath))
            return false;
        return File.Exists(Path.Combine(gamePath, "CasualtiesUnknown.exe"));
    }

    /// <summary>
    /// 检测该路径是否「可直接使用」：游戏本体 + BepInEx 都在
    /// </summary>
    public static bool IsValidGamePath(string gamePath)
    {
        if (!IsGameInstallDir(gamePath)) return false;
        return Directory.Exists(Path.Combine(gamePath, "BepInEx"));
    }

    /// <summary>
    /// 判断某个安装目录是 Demo 还是正式版。
    /// 首选 appmanifest 的 appid（唯一可靠），拿不到再退回目录名。
    /// </summary>
    public static bool IsDemoInstall(string? gamePath)
    {
        if (string.IsNullOrEmpty(gamePath)) return false;

        try
        {
            // <common>\<installdir> → <steamapps>
            var steamApps = Path.GetDirectoryName(Path.GetDirectoryName(
                gamePath.TrimEnd(Path.DirectorySeparatorChar)));
            var installdir = Path.GetFileName(gamePath.TrimEnd(Path.DirectorySeparatorChar));
            if (!string.IsNullOrEmpty(steamApps) && Directory.Exists(steamApps))
            {
                var demoAcf = Path.Combine(steamApps, $"appmanifest_{DemoAppId}.acf");
                if (File.Exists(demoAcf) && AcfInstallDir(demoAcf) == installdir)
                    return true;
            }
        }
        catch { }

        return Path.GetFileName(gamePath.TrimEnd(Path.DirectorySeparatorChar))
                   .IndexOf("demo", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>给人看的版本标签：正式版 / Demo</summary>
    public static string GameEditionText(string? gamePath) =>
        IsDemoInstall(gamePath) ? "Demo" : "正式版";

    /// <summary>按路径去重加入列表（忽略大小写）</summary>
    private static void AddUnique(List<string> list, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        // 注册表里给的是 "d:/steam" 这种形式，必须归一化后比较，否则同一目录会被收两遍
        var norm = path!.TrimEnd('\\', '/').Replace('/', '\\');
        if (norm.Length < 3 || norm[1] != ':') return;   // 只接受带盘符的绝对路径
        if (!list.Exists(x => string.Equals(x, norm, StringComparison.OrdinalIgnoreCase)))
            list.Add(norm);
    }

    /// <summary>读取 appmanifest 里的 installdir</summary>
    private static string? AcfInstallDir(string acfPath)
    {
        try
        {
            var m = System.Text.RegularExpressions.Regex.Match(
                File.ReadAllText(acfPath), "\"installdir\"\\s+\"([^\"]+)\"");
            return m.Success ? m.Groups[1].Value : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// 收集本机所有 Steam 库根目录（即包含 steamapps 的目录）。
    /// 来源：注册表 SteamPath → 常见安装位置 → libraryfolders.vdf 里登记的库。
    /// </summary>
    private static List<string> CollectSteamLibraries()
    {
        var roots = new List<string>();

        void AddRoot(string? p)
        {
            if (string.IsNullOrWhiteSpace(p)) return;
            p = p.TrimEnd('\\', '/');
            // 传进来的可能是库根，也可能直接是 Steam 安装根
            foreach (var cand in new[] { p, Path.Combine(p, "Steam"), Path.Combine(p, "SteamLibrary") })
            {
                try { if (Directory.Exists(Path.Combine(cand, "steamapps"))) AddUnique(roots, cand); } catch { }
            }
        }

        // ① 注册表（用户级 + 机器级，64/32 位都试）
        foreach (var hive in new[] { Microsoft.Win32.Registry.CurrentUser, Microsoft.Win32.Registry.LocalMachine })
        {
            foreach (var sub in new[] { @"Software\Valve\Steam", @"SOFTWARE\WOW6432Node\Valve\Steam" })
            {
                try
                {
                    using var key = hive.OpenSubKey(sub);
                    AddRoot(key?.GetValue("SteamPath") as string ?? key?.GetValue("InstallPath") as string);
                }
                catch { }
            }
        }

        // ② 常见位置（不依赖注册表，覆盖便携/多盘装法）
        foreach (var drive in new[] { "C", "D", "E", "F", "G" })
        {
            AddRoot($@"{drive}:\Steam");
            AddRoot($@"{drive}:\SteamLibrary");
            AddRoot($@"{drive}:\Games\Steam");
        }
        AddRoot(@"C:\Program Files (x86)\Steam");
        AddRoot(@"C:\Program Files\Steam");

        // ③ libraryfolders.vdf 里登记的其他库（可能在其他盘）
        foreach (var root in roots.ToList())
        {
            var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;
            try
            {
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                             File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                {
                    var lib = m.Groups[1].Value.Replace(@"\\", @"\");
                    if (lib.Length > 2 && Directory.Exists(lib)) AddUnique(roots, lib.TrimEnd('\\'));
                }
            }
            catch { }
        }

        return roots.ToList();
    }

    /// <summary>
    /// 枚举本机所有 Casualties Unknown 安装（正式版 / Demo 都可能存在）。
    ///
    /// 两条通道：① 读 appmanifest_*.acf 的 installdir（目录名改了也能认出来）；
    /// ② 兜底扫 steamapps\common 下名字含 "casualties" 的目录。
    /// </summary>
    public static List<string> FindGameInstalls()
    {
        var found = new List<string>();

        foreach (var lib in CollectSteamLibraries())
        {
            var common = Path.Combine(lib, "steamapps", "common");
            if (!Directory.Exists(common)) continue;

            // ① appmanifest
            try
            {
                foreach (var acf in Directory.GetFiles(Path.Combine(lib, "steamapps"), "appmanifest_*.acf"))
                {
                    var dir = AcfInstallDir(acf);
                    if (string.IsNullOrEmpty(dir)) continue;
                    if (dir.IndexOf("casualt", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var full = Path.Combine(common, dir);
                    if (IsGameInstallDir(full)) AddUnique(found, full);
                }
            }
            catch { }

            // ② 目录名兜底
            try
            {
                foreach (var d in Directory.GetDirectories(common))
                {
                    if (Path.GetFileName(d).IndexOf("casualt", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (IsGameInstallDir(d)) AddUnique(found, d);
                }
            }
            catch { }
        }

        // 排序：正式版优先（Demo 已停止更新，作者明确不再维护）→ 装好 BepInEx 的优先 → 路径短的优先
        return found
            .OrderByDescending(p => (IsDemoInstall(p) ? 0 : 2)
                                    + (Directory.Exists(Path.Combine(p, "BepInEx")) ? 1 : 0))
            .ThenBy(p => p.Length)
            .ToList();
    }

    /// <summary>诊断用：暴露收集到的 Steam 库根目录（--detect 用）</summary>
    public static List<string> FindSteamLibrariesForDiagnostics() => CollectSteamLibraries();

    /// <summary>
    /// 尝试自动检测游戏安装路径。
    /// 找不到时返回 null，由调用方决定是否让用户手选。
    /// </summary>
    public static string? AutoDetectGamePath()
    {
        var installs = FindGameInstalls();
        return installs.Count > 0 ? installs[0] : null;
    }
}
