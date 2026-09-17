using System.ComponentModel;

namespace CUModUpdater.Models;

/// <summary>
/// 可浏览的 Mod 列表项
/// </summary>
public class ModListing
{
    [DisplayName("Mod 名称")]
    public string Name { get; set; } = "";

    [DisplayName("作者")]
    public string Author { get; set; } = "";

    /// <summary>简短摘要（英文原文）</summary>
    public string Summary { get; set; } = "";

    /// <summary>完整描述（英文原文）</summary>
    public string Description { get; set; } = "";

    /// <summary>翻译后的中文描述</summary>
    public string TranslatedDescription { get; set; } = "";

    /// <summary>翻译后的中文摘要</summary>
    public string TranslatedSummary { get; set; } = "";

    /// <summary>翻译后的中文名称（列表搜索也能命中中文名）</summary>
    public string TranslatedName { get; set; } = "";

    /// <summary>GitHub 条目的 Release 是否已查过（避免每次选中都重复请求）</summary>
    public bool ReleaseChecked { get; set; }

    [DisplayName("版本")]
    public string Version { get; set; } = "";

    [DisplayName("分类")]
    public string Category { get; set; } = "";

    [DisplayName("下载量")]
    public string DisplayDownloads => DownloadCount switch
    {
        >= 1_000_000 => $"{DownloadCount / 1_000_000.0:F1}M",
        >= 1_000 => $"{DownloadCount / 1_000.0:F1}K",
        > 0 => DownloadCount.ToString(),
        _ => "-"
    };

    public long DownloadCount { get; set; }

    [DisplayName("好评")]
    public string DisplayEndorsements => EndorsementCount > 0 ? EndorsementCount.ToString() : "-";

    public long EndorsementCount { get; set; }

    /// <summary>缩略图 URL</summary>
    public string ThumbnailUrl { get; set; } = "";

    /// <summary>大图预览 URL</summary>
    public string PreviewUrl { get; set; } = "";

    /// <summary>下载 URL</summary>
    public string DownloadUrl { get; set; } = "";

    /// <summary>Mod 页面 URL</summary>
    public string PageUrl { get; set; } = "";

    /// <summary>来源: "Nexus" / "GitHub" / "Curated"</summary>
    [DisplayName("来源")]
    public string Source { get; set; } = "";

    /// <summary>Nexus Mod ID 或 GitHub Owner/Repo</summary>
    public string ModId { get; set; } = "";

    /// <summary>BepInPlugin GUID（用于匹配已安装 Mod）</summary>
    public string PluginGuid { get; set; } = "";

    /// <summary>DLL 文件名（用于匹配已安装 Mod）</summary>
    public string FileName { get; set; } = "";

    /// <summary>GitHub Owner（GitHub 来源时）</summary>
    public string Owner { get; set; } = "";

    /// <summary>GitHub Repo（GitHub 来源时）</summary>
    public string Repo { get; set; } = "";

    /// <summary>是否已安装</summary>
    [DisplayName("状态")]
    public string InstallStatus { get; set; } = "未安装";

    /// <summary>已安装的版本（如已安装）</summary>
    public string InstalledVersion { get; set; } = "";

    /// <summary>发布日期</summary>
    public DateTime PublishedAt { get; set; }

    [DisplayName("更新日期")]
    public string DisplayDate => PublishedAt != default
        ? PublishedAt.ToString("yyyy-MM-dd")
        : "-";

    /// <summary>来源显示文本</summary>
    public string SourceDisplay => Source switch
    {
        "Nexus" => "N网",
        "GitHub" => "GitHub",
        "GameBanana" => "GB",
        "Curated" => "精选",
        _ => Source,
    };

    /// <summary>是否已安装</summary>
    public bool IsInstalled => InstallStatus == "已安装" || InstallStatus == "可更新";

    /// <summary>是否有更新</summary>
    public bool HasUpdate => InstallStatus == "可更新";
}

/// <summary>
/// 精选 Mod 条目（硬编码的已知 Mod）
/// </summary>
public class CuratedModEntry
{
    public string Name { get; set; } = "";
    public string Owner { get; set; } = "";
    public string Repo { get; set; } = "";
    public string Category { get; set; } = "";
    public string EnglishSummary { get; set; } = "";
    public string ChineseSummary { get; set; } = "";
    public string EnglishDescription { get; set; } = "";
    public string ChineseDescription { get; set; } = "";
    public string PluginGuid { get; set; } = "";
    public string FileName { get; set; } = "";
}
