namespace CUModUpdater.Models;

/// <summary>
/// Mod 更新来源配置
/// </summary>
public class ModSource
{
    /// <summary>来源类型: "github" / "nexus" / "gamebanana"</summary>
    public string Type { get; set; } = "github";

    // === GitHub 相关 ===
    public string Owner { get; set; } = "";
    public string Repo { get; set; } = "";

    // === Nexus Mods 相关 ===
    public string ModId { get; set; } = "";

    // === 通用 ===
    /// <summary>匹配下载资源的名称模式（如 *.dll），留空则自动选择 .dll 文件</summary>
    public string AssetPattern { get; set; } = "";

    /// <summary>是否为 GitHub 来源</summary>
    public bool IsGitHub => Type.Equals("github", StringComparison.OrdinalIgnoreCase);

    /// <summary>是否为 Nexus 来源</summary>
    public bool IsNexus => Type.Equals("nexus", StringComparison.OrdinalIgnoreCase);

    /// <summary>是否为 GameBanana 来源（ModId 即 GameBanana modId）</summary>
    public bool IsGameBanana => Type.Equals("gamebanana", StringComparison.OrdinalIgnoreCase);

    /// <summary>来源是否有效</summary>
    public bool IsValid =>
        IsGitHub ? (!string.IsNullOrEmpty(Owner) && !string.IsNullOrEmpty(Repo))
                 : (!string.IsNullOrEmpty(ModId));
}
