namespace CUModUpdater.Models;

/// <summary>
/// Mod 更新状态枚举
/// </summary>
public enum UpdateStatus
{
    NotChecked,       // 未检查
    UpToDate,         // 已是最新版本
    UpdateAvailable,  // 有可用更新
    Unknown,          // 无法确定（API 返回异常版本）
    Error,            // 检查出错
    NoSource,         // 未配置更新源
    Disabled,         // Mod 已禁用 (_disabled 后缀)
    Checking,         // 正在检查中
    Updating,         // 正在更新中
    Updated,          // 已更新
    UpdateFailed,     // 更新失败
    LocalMod,         // 自有/本地 Mod（元数据未收录，无需更新）
}

/// <summary>
/// 单个 Mod 的完整信息
/// </summary>
public class ModInfo
{
    // === BepInPlugin 属性信息 ===
    public string Name { get; set; } = "";
    public string PluginGuid { get; set; } = "";
    public string CurrentVersion { get; set; } = "";

    // === 文件信息 ===
    public string FilePath { get; set; } = "";
    public string FileName { get; set; } = "";
    public string FolderPath { get; set; } = "";
    public bool IsEnabled { get; set; } = true;
    public long FileSize { get; set; }
    public DateTime LastModified { get; set; }

    // === 程序集信息 ===
    public string AssemblyVersion { get; set; } = "";

    // === 纯数据 Mod（如 ICEneco Custom Item API 的物品包）===
    /// <summary>
    /// 是否为「纯数据 Mod」——没有 dll，靠前置 API 读取文件夹内的 JSON 生成内容。
    /// 典型形态：plugins/{ModName}/ICEnecoCustomItemFlag.json + Items/*.json + Langs/*.json
    /// </summary>
    public bool IsDataMod { get; set; }

    /// <summary>数据 Mod 的作者（取自 Flag 文件 creator 字段，用于元数据匹配）</summary>
    public string DataModCreator { get; set; } = "";

    /// <summary>数据 Mod 的标识文件路径（Flag 文件绝对路径）</summary>
    public string FlagFilePath { get; set; } = "";

    /// <summary>N 网下载文件名里解析出的 ModId（如 "Craftable Leg Pouch 572 1 2026-08-17T09-00Z xxx.zip" → 572）</summary>
    public string DownloadFileModId { get; set; } = "";

    // === 更新追踪 ===
    public string LatestVersion { get; set; } = "";
    public UpdateStatus Status { get; set; } = UpdateStatus.NotChecked;
    public ModSource? Source { get; set; }
    public string DownloadUrl { get; set; } = "";
    public string AssetName { get; set; } = "";
    public string ReleaseNotes { get; set; } = "";
    public DateTime? LastChecked { get; set; }
    public string LastError { get; set; } = "";

    // === 便捷属性 ===
    /// <summary>是否为有效的 BepInEx 插件（有 BepInPlugin 属性）</summary>
    public bool IsBepInExPlugin => !string.IsNullOrEmpty(PluginGuid);

    /// <summary>显示用的版本（优先使用 BepInPlugin 版本，回退到程序集版本）</summary>
    public string DisplayVersion =>
        !string.IsNullOrEmpty(CurrentVersion) ? CurrentVersion :
        !string.IsNullOrEmpty(AssemblyVersion) ? AssemblyVersion : "未知";

    /// <summary>状态文字</summary>
    public string StatusText => Status switch
    {
        UpdateStatus.NotChecked => "未检查",
        UpdateStatus.Checking => "检查中...",
        UpdateStatus.UpToDate => "✓ 最新",
        UpdateStatus.UpdateAvailable => "↑ 可更新",
        UpdateStatus.Unknown => "? 未知",
        UpdateStatus.Error => "✗ 错误",
        UpdateStatus.NoSource => "— 无源",
        UpdateStatus.Disabled => "⊘ 已禁用",
        UpdateStatus.Updating => "更新中...",
        UpdateStatus.Updated => "✓ 已更新",
        UpdateStatus.UpdateFailed => "✗ 失败",
        UpdateStatus.LocalMod => "◈ 自有",
        _ => ""
    };

    /// <summary>来源显示文字</summary>
    public string SourceText
    {
        get
        {
            if (Status == UpdateStatus.LocalMod) return "自有 Mod";
            if (Source == null) return "未配置";
            return Source.Type switch
            {
                "github" => $"GitHub: {Source.Owner}/{Source.Repo}",
                "nexus" => $"Nexus: #{Source.ModId}",
                "gamebanana" => $"GameBanana: #{Source.ModId}",
                _ => Source.Type ?? "—"
            };
        }
    }

    /// <summary>
    /// 该 Mod 的文件名是否在元数据里存在归属歧义（多个 mod 声明了同名 dll）。
    /// 用于 UI 提示「这类 mod 无法自动判断来源，需手动配置」。
    /// </summary>
    public bool HasAmbiguousDll { get; set; }

    // === 分组（同一来源 mod 的多个 dll 归为一组） ===
    /// <summary>组标识：同一来源的条目共享（形如 "nexus:67"），无来源时为 null</summary>
    public string? GroupKey { get; set; }

    /// <summary>组显示名（来源 mod 的官方名称，如 "Casualties Together"）</summary>
    public string? GroupName { get; set; }

    /// <summary>是否为组内主条目（折叠时显示的那一行）</summary>
    public bool IsGroupHead { get; set; } = true;

    /// <summary>组内成员数（含自身），仅 IsGroupHead 有意义</summary>
    public int GroupSize { get; set; } = 1;

    /// <summary>所属组长的引用（成员用它找到自己的组首行）</summary>
    public ModInfo? GroupOwner { get; set; }

    /// <summary>组内成员（仅 IsGroupHead 有值）</summary>
    public List<ModInfo> GroupMembers { get; set; } = new();

    // === 同名文件检测 ===
    /// <summary>
    /// 是否为重复文件：**同一个插件**（GUID 相同）在插件目录里存在多份副本
    /// （不同目录各一份，或启用态与 _disabled 态并存）。
    /// BepInEx 会尝试全部加载，同一插件重复加载会导致 GUID 冲突。
    /// </summary>
    public bool IsDuplicateFile { get; set; }

    /// <summary>被保留的那一份（重复文件的对照对象）</summary>
    public ModInfo? DuplicateOf { get; set; }

    /// <summary>
    /// 是否为「同名冲突」：**两个不同的插件**（GUID 不同）用了同一个文件名。
    /// 典型：Gunsaw Genetics 自带修改过的 body_sprite_replacer.dll
    /// （GUID spritereplacer），与原版 Player Sprite Replacer
    /// （GUID com.yourname.spritereplacer）同名但不是同一个 mod——
    /// 作者 README 明确要求覆盖原版。**不能**当成"重复副本"提示用户删除。
    /// </summary>
    public bool HasNameConflict { get; set; }

    /// <summary>与之抢文件名的另一个 mod</summary>
    public ModInfo? ConflictWith { get; set; }

    /// <summary>同名文件（无论重复还是冲突）—— UI 标记用</summary>
    public bool HasSameNameIssue => IsDuplicateFile || HasNameConflict;

    /// <summary>
    /// 禁用前的检查结论。重新启用时恢复它 —— 否则会出现
    /// 「最新版本列有值、状态却是未检查」的矛盾显示，用户还得再查一遍。
    /// </summary>
    public UpdateStatus StatusBeforeDisable { get; set; } = UpdateStatus.NotChecked;

    // === 文件夹视图 ===
    /// <summary>
    /// 文件夹视图的**合成节点**——它不是磁盘上的真实文件，
    /// 只代表一个目录（点名称折叠/展开，点复选框批量开关整个目录）。
    /// </summary>
    public bool IsFolderNode { get; set; }

    /// <summary>文件夹节点对应的目录（相对 plugins 根的路径；根目录为 ""）</summary>
    public string FolderRelPath { get; set; } = "";

    /// <summary>目录节点的递归文件集（含子目录）——批量开关与计数用；普通文件行不使用</summary>
    public List<ModInfo> FolderAllFiles { get; set; } = new();

    /// <summary>版本比较：当前版本是否小于最新版本</summary>
    public bool HasUpdate
    {
        get
        {
            if (string.IsNullOrEmpty(LatestVersion)) return false;
            if (string.IsNullOrEmpty(CurrentVersion)) return true;
            return VersionHelper.IsNewer(LatestVersion, CurrentVersion);
        }
    }
}

/// <summary>
/// 版本比较工具
/// </summary>
public static class VersionHelper
{
    /// <summary>
    /// 判断 versionA 是否比 versionB 更新
    /// 支持 "1.0.0"、"v2.3.1"、"1.2.3.4" 等格式
    /// </summary>
    public static bool IsNewer(string versionA, string versionB)
    {
        var a = ParseVersion(versionA);
        var b = ParseVersion(versionB);
        return a > b;
    }

    /// <summary>
    /// 解析版本字符串为可比较的 Version 对象
    /// 去除 'v'/'V' 前缀、去除预发布标签后缀
    ///
    /// ⚠️ 必须**统一补齐到 4 段**。若交给 new Version("1.0.0")，它的 Build=0、
    /// Revision=-1，而 "1" 走补齐分支变成 1.0.0.0 → 两者会比出大小差，
    /// 于是「元数据写 1、本地写 1.0.0」这种同一版本被误判成有更新。
    /// </summary>
    public static Version ParseVersion(string? versionStr)
    {
        if (string.IsNullOrWhiteSpace(versionStr))
            return new Version(0, 0, 0, 0);

        var s = versionStr.Trim().TrimStart('v', 'V');

        // 去除预发布标签 (-alpha, -beta, -rc 等)
        var dashIndex = s.IndexOf('-');
        if (dashIndex >= 0)
            s = s[..dashIndex];

        // 去除 build metadata (+xxx)
        var plusIndex = s.IndexOf('+');
        if (plusIndex >= 0)
            s = s[..plusIndex];

        // 去除非数字非点的字符
        var clean = new System.Text.StringBuilder();
        foreach (var c in s)
        {
            if (char.IsDigit(c) || c == '.')
                clean.Append(c);
            else if (c != ' ')
                break;
        }

        s = clean.ToString().Trim('.');

        if (string.IsNullOrEmpty(s))
            return new Version(0, 0, 0, 0);

        // 一律补零到 4 段，保证 "1" / "1.0" / "1.0.0" / "1.0.0.0" 相等
        var parts = s.Split('.');
        var nums = new int[4];
        for (int i = 0; i < Math.Min(parts.Length, 4); i++)
        {
            if (int.TryParse(parts[i], out var n))
                nums[i] = n;
        }
        return new Version(nums[0], nums[1], nums[2], nums[3]);
    }
}

/// <summary>
/// GitHub/Nexus 发布版本信息
/// </summary>
public class ReleaseInfo
{
    public string Version { get; set; } = "";
    public string DownloadUrl { get; set; } = "";
    public string AssetName { get; set; } = "";
    public string ReleaseNotes { get; set; } = "";
    public DateTime PublishedAt { get; set; }
}
