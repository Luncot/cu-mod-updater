using System.Text.Json.Serialization;

namespace CUModUpdater.Models;

/// <summary>
/// Mod 更新器全局配置
/// </summary>
public class ModUpdaterConfig
{
    /// <summary>游戏安装路径</summary>
    public string GamePath { get; set; } = @"D:\Steam\steamapps\common\Casualties Unknown Demo";

    /// <summary>Nexus Mods API Key（在 nexusmods.com -> 个人设置 -> API 中生成）</summary>
    public string NexusApiKey { get; set; } = "";

    /// <summary>
    /// Nexus Mods 游戏域名 —— 必须用 **scavprototype**（N 网 URL 里那一段）。
    /// 这游戏叫 Casualties Unknown，但域名不是游戏名；曾误填 casualtiesunknown，
    /// 导致所有 N 网 API 请求 404（表现为「N 网 mod 访问不了」）。
    /// </summary>
    public string NexusGameDomain { get; set; } = "scavprototype";

    /// <summary>GitHub Personal Access Token（可选，提高 API 速率限制从 60/h 到 5000/h）</summary>
    public string GitHubToken { get; set; } = "";

    /// <summary>N 网浏览器 Cookie（免费账号批量下载用，从浏览器 F12 复制整行 Cookie 头）</summary>
    public string NexusCookie { get; set; } = "";

    /// <summary>备份保留数量（最多保留多少个旧版本备份）</summary>
    public int BackupRetention { get; set; } = 10;

    /// <summary>Mod 到更新来源的映射（键为 BepInPlugin GUID 或 DLL 文件名）</summary>
    public Dictionary<string, ModSource> ModSources { get; set; } = new();

    /// <summary>最后一次扫描的时间</summary>
    public DateTime? LastScanTime { get; set; }

    /// <summary>是否在启动时自动扫描</summary>
    public bool AutoScanOnStart { get; set; } = true;

    /// <summary>是否在启动时自动检查更新</summary>
    public bool AutoCheckOnStart { get; set; } = false;

    /// <summary>是否启用备份</summary>
    public bool EnableBackup { get; set; } = true;

    /// <summary>检查更新并发数</summary>
    public int ConcurrentChecks { get; set; } = 5;

    /// <summary>
    /// 分组视图："source" = 按更新来源分组（默认，更新视角）；
    /// "folder" = 按物理文件夹分组（管理视角，与 Alexx 的管理器一致的目录树）。
    /// </summary>
    public string GroupingView { get; set; } = "source";
}
