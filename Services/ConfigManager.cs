using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CUModUpdater.Models;

namespace CUModUpdater.Services;

/// <summary>
/// 配置文件管理器 - 加载/保存 JSON 配置
/// </summary>
public class ConfigManager
{
    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CU-ModUpdater");

    private static readonly string ConfigPath = Path.Combine(ConfigDir, "modupdater_config.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>配置文件路径</summary>
    public static string ConfigFilePath => ConfigPath;

    /// <summary>配置目录路径</summary>
    public static string ConfigDirectory => ConfigDir;

    /// <summary>
    /// 加载配置，如果不存在则创建默认配置
    /// </summary>
    public static ModUpdaterConfig Load()
    {
        try
        {
            if (!Directory.Exists(ConfigDir))
                Directory.CreateDirectory(ConfigDir);

            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath, Encoding.UTF8);
                var config = JsonSerializer.Deserialize<ModUpdaterConfig>(json, JsonOpts);
                if (config != null)
                {
                    // 如果游戏路径无效，尝试自动检测
                    if (!PluginScanner.IsValidGamePath(config.GamePath))
                    {
                        var auto = PluginScanner.AutoDetectGamePath();
                        if (auto != null)
                            config.GamePath = auto;
                    }

                    // 域名迁移：早期版本存的是 "casualtiesunknown"（游戏名），
                    // 但 N 网域名是 scavprototype —— 用错域名所有 N 网 API 都 404。
                    if (string.IsNullOrWhiteSpace(config.NexusGameDomain) ||
                        config.NexusGameDomain.Equals("casualtiesunknown", StringComparison.OrdinalIgnoreCase))
                    {
                        config.NexusGameDomain = Services.NexusBrowser.DefaultGameDomain;
                        Save(config);
                    }

                    return config;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"加载配置失败: {ex.Message}");
        }

        // 创建默认配置
        var defaultConfig = CreateDefaultConfig();
        Save(defaultConfig);
        return defaultConfig;
    }

    /// <summary>
    /// 保存配置
    /// </summary>
    public static void Save(ModUpdaterConfig config)
    {
        try
        {
            if (!Directory.Exists(ConfigDir))
                Directory.CreateDirectory(ConfigDir);

            var json = JsonSerializer.Serialize(config, JsonOpts);
            File.WriteAllText(ConfigPath, json, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"保存配置失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 创建默认配置，包含已知 Mod 来源映射
    /// </summary>
    private static ModUpdaterConfig CreateDefaultConfig()
    {
        var gamePath = PluginScanner.AutoDetectGamePath()
            ?? @"D:\Steam\steamapps\common\Casualties Unknown Demo";

        var config = new ModUpdaterConfig
        {
            GamePath = gamePath,
        };

        // 预填已知 Mod 的 GitHub 来源（来自开发指南中提到的社区项目）
        var knownSources = new Dictionary<string, ModSource>
        {
            // CUCoreLib - jimmyking9999999
            ["com.casualtiesunknown.corelib"] = new ModSource
            {
                Type = "github",
                Owner = "jimmyking9999999",
                Repo = "CUCoreLib",
            },
            // QoL-Unknown - jimmyking9999999
            ["com.casualtiesunknown.qol"] = new ModSource
            {
                Type = "github",
                Owner = "jimmyking9999999",
                Repo = "QoL-Unknown",
            },
        };

        foreach (var kv in knownSources)
            config.ModSources[kv.Key] = kv.Value;

        return config;
    }

    /// <summary>
    /// 为指定 Mod 获取或创建来源配置。
    /// 查找顺序与 SetSource 的键选取保持一致：非占位符 GUID → 文件名。
    /// </summary>
    public static ModSource? GetSource(ModUpdaterConfig config, ModInfo mod)
    {
        // ① 文件名键优先 —— 它绑定的是「这一份文件」，最具体
        var key = GetFileKey(mod.FileName);
        if (!string.IsNullOrEmpty(key) &&
            config.ModSources.TryGetValue(key, out var byFile) && byFile.IsValid)
            return byFile;

        // ② GUID 键（非占位符）
        if (!string.IsNullOrEmpty(mod.PluginGuid) &&
            !Services.CasualtiesManageableService.IsPlaceholderGuid(mod.PluginGuid) &&
            config.ModSources.TryGetValue(mod.PluginGuid, out var src) && src.IsValid)
            return src;

        // ③ GUID 键即使是占位符也要读。
        //    占位符键只是「不该被自动配置写入」，不代表用户手动配的不算数。
        //    实例：body_sprite_replacer.dll 的 GUID 是 com.yourname.spritereplacer
        //    （作者忘改模板），但用户已手动把它配到 #55——跳过的话
        //    这条永远显示「未配置 / 无源」。
        if (!string.IsNullOrEmpty(mod.PluginGuid) &&
            config.ModSources.TryGetValue(mod.PluginGuid, out var ph) && ph.IsValid)
            return ph;

        return null;
    }

    /// <summary>
    /// 由文件名生成配置键：去掉 _disabled 后缀 + 去掉 .dll 扩展名。
    /// 例："[合成拓展]CasualtiesCraft.dll_disabled" → "[合成拓展]CasualtiesCraft"
    /// </summary>
    public static string GetFileKey(string fileName)
    {
        var n = Path.GetFileName(fileName);
        if (n.EndsWith("_disabled", StringComparison.OrdinalIgnoreCase))
            n = n[..^"_disabled".Length];
        if (n.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            n = n[..^".dll".Length];
        return n;
    }

    /// <summary>
    /// 保存单个 Mod 的来源配置。
    /// 键的选取：优先 GUID，但**占位符 GUID 不用**（作者忘改的模板默认值
    /// 如 com.yourName.modName 会让配置键失去辨识度，改走文件名键）。
    /// </summary>
    public static void SetSource(ModUpdaterConfig config, ModInfo mod, ModSource source)
    {
        var useGuid = !string.IsNullOrEmpty(mod.PluginGuid) &&
                      !Services.CasualtiesManageableService.IsPlaceholderGuid(mod.PluginGuid);
        var key = useGuid ? mod.PluginGuid
                          : GetFileKey(mod.FileName);
        if (!string.IsNullOrEmpty(key))
            config.ModSources[key] = source;
    }

    /// <summary>
    /// 自动配置：优先用 CasualtiesManageable 官方元数据按 GUID 强匹配（游戏内管理器同款数据），
    /// 其次用 dll 文件名独占匹配，最后用精选数据库；返回 true 表示找到并已配置。
    /// 自有 mod（CHP-* 等元数据未收录）不参与自动配置，由调用方标为 LocalMod。
    /// </summary>
    public static bool TryAutoConfigure(ModUpdaterConfig config, ModInfo mod)
    {
        // 如果已有有效配置，跳过
        if (GetSource(config, mod) != null)
            return false;

        // ── 纯数据 Mod（无 dll，靠 ICEneco Custom Item API 读 JSON 生成物品）──
        // 这类 Mod 没有 GUID、没有 dll，元数据里的 dllNames 是空数组，
        // 所以走单独的「名称 + 作者」双匹配通道。
        if (mod.IsDataMod)
        {
            var dataSource = Services.CasualtiesManageableService
                .ToModSource(Services.CasualtiesManageableService
                    .FindByNameAndAuthor(mod.Name, mod.DataModCreator));
            if (dataSource != null)
            {
                SetSource(config, mod, dataSource);
                return true;
            }
            // 名称匹配失败时，尝试用下载文件名里夹带的 ModId 直接查
            var dlId = Services.CasualtiesManageableService
                .ParseModIdFromDownloadFileName(mod.DownloadFileModId);
            var byId = Services.CasualtiesManageableService
                .ToModSource(Services.CasualtiesManageableService.FindByModId(dlId));
            if (byId != null)
            {
                SetSource(config, mod, byId);
                return true;
            }
            return false;
        }

        // 自有/本地 mod 不做来源匹配（避免被同名 dll 误吸到别的 mod）
        if (Services.CasualtiesManageableService.IsLocalMod(mod.FileName, mod.PluginGuid))
            return false;

        // 1. 官方元数据 GUID 强匹配（CasualtiesManageable，每小时更新）
        var cmSource = Services.CasualtiesManageableService.TryGetSourceCached(mod.PluginGuid);
        if (cmSource != null)
        {
            SetSource(config, mod, cmSource);
            return true;
        }

        // 2. 元数据 dll 文件名匹配（仅独占命中才算，歧义 dll 一律跳过——
        //    宁可留给手动配置，也不能配错来源）
        var dllSource = Services.CasualtiesManageableService.TryGetSourceByDllNameCached(mod.FileName);
        if (dllSource != null)
        {
            SetSource(config, mod, dllSource);
            return true;
        }

        // 3. 精选数据库兜底（仅收集过明确来源的）
        var curated = GitHubBrowser.FindCuratedByGuid(mod.PluginGuid, mod.FileName);
        if (curated == null) return false;

        var source = new ModSource
        {
            Type = "github",
            Owner = curated.Owner,
            Repo = curated.Repo,
            AssetPattern = curated.FileName,
        };

        // 只在 Owner/Repo 都有值时才算有效
        if (!source.IsValid) return false;

        SetSource(config, mod, source);
        return true;
    }

    /// <summary>
    /// 批量自动配置所有未配置的 Mod
    /// 返回成功配置的 Mod 数
    /// </summary>
    public static int AutoConfigureAll(ModUpdaterConfig config, IEnumerable<ModInfo> mods)
    {
        int count = 0;
        foreach (var mod in mods)
        {
            if (TryAutoConfigure(config, mod))
                count++;
        }
        return count;
    }
}
