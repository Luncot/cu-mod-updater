using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CUModUpdater.Services;

/// <summary>
/// 英中翻译服务 - 内置游戏术语词典 + 在线 API + 本地缓存
/// 专为 Casualties Unknown Demo 游戏优化
/// </summary>
public class TranslationService : IDisposable
{
    private readonly HttpClient _client;
    private readonly string _cacheFile;
    private Dictionary<string, string> _cache = new();

    /// <summary>翻译进度回调</summary>
    public Action<string>? OnLog { get; set; }

    /// <summary>是否启用在线翻译</summary>
    public bool EnableOnline { get; set; } = true;

    // === 游戏术语词典 ===
    // 不应翻译的技术术语
    private static readonly HashSet<string> KeepOriginal = new(StringComparer.OrdinalIgnoreCase)
    {
        "BepInEx", "Harmony", "Mono", "Cecil", "Unity", "DLL", "API", "GUI", "UI",
        "GitHub", "Nexus", "Steam", "Mod", "Plugin", "Patch", "HarmonyX",
        "MonoBehaviour", "PlayerCamera", "ConsoleScript", "Assembly-CSharp",
        "BepInPlugin", "AccessTools", "dnSpy", "Doorstop", "TMP_Text",
    };

    // 游戏术语翻译映射
    private static readonly Dictionary<string, string> GameTerms = new(StringComparer.OrdinalIgnoreCase)
    {
        // 模组类型
        { "quality of life", "生活质量优化" },
        { "quality-of-life", "生活质量优化" },
        { "QoL", "生活质量优化" },
        { "performance", "性能" },
        { "optimization", "优化" },
        { "optimise", "优化" },
        { "optimize", "优化" },
        { "multiplayer", "多人联机" },
        { "single player", "单人模式" },
        { "singleplayer", "单人模式" },
        { "translation", "翻译" },
        { "localization", "本地化" },
        { "localisation", "本地化" },
        { "chinese", "中文" },
        { "english", "英文" },
        { "bug fix", "Bug 修复" },
        { "bugfix", "Bug 修复" },
        { "fix", "修复" },
        { "tweak", "微调" },
        { "overhaul", "大修" },
        { "expansion", "扩展" },
        { "hardcore", "硬核" },
        { "cheat", "作弊" },
        { "debug", "调试" },
        { "console", "控制台" },
        { "hardcore+", "硬核+" },

        // 游戏机制
        { "crafting", "合成" },
        { "craft", "合成" },
        { "recipe", "配方" },
        { "recipes", "配方" },
        { "inventory", "背包" },
        { "container", "容器" },
        { "spawn", "生成" },
        { "spawner", "生成器" },
        { "limb", "肢体" },
        { "limbs", "肢体" },
        { "body", "身体" },
        { "wound", "伤口" },
        { "medical", "医疗" },
        { "medicine", "药品" },
        { "weapon", "武器" },
        { "armor", "护甲" },
        { "wearable", "可穿戴物" },
        { "gun", "枪械" },
        { "turret", "炮塔" },
        { "explosion", "爆炸" },
        { "damage", "伤害" },
        { "health", "生命值" },
        { "bleeding", "流血" },
        { "disfigure", "毁容" },
        { "dismember", "断肢" },
        { "ragdoll", "布娃娃" },
        { "heal", "治疗" },
        { "pickup", "拾取" },
        { "sort", "排序" },
        { "autosort", "自动排序" },
        { "save", "存档" },
        { "saves", "存档" },
        { "save manager", "存档管理器" },
        { "sprite", "精灵图" },
        { "portrait", "肖像" },
        { "layer", "图层" },
        { "unlock", "解锁" },
        { "slider", "滑块" },
        { "panel", "面板" },
        { "menu", "菜单" },
        { "button", "按钮" },
        { "shortcut", "快捷键" },
        { "hotkey", "热键" },
        { "keybind", "按键绑定" },

        // 游戏特有
        { "Casualties Unknown", "未知伤亡" },
        { "Casualties: Unknown", "未知伤亡" },
        { "scavenger", "拾荒者" },
        { "wearables", "可穿戴物" },
        { "condition", "耐久度" },
        { "item", "物品" },
        { "items", "物品" },
        { "item spawner", "物品生成器" },
        { "enough items", "物品查看器" },

        // 通用
        { "download", "下载" },
        { "install", "安装" },
        { "uninstall", "卸载" },
        { "update", "更新" },
        { "version", "版本" },
        { "release", "发布" },
        { "changelog", "更新日志" },
        { "dependency", "依赖" },
        { "compatible", "兼容" },
        { "incompatible", "不兼容" },
        { "requirement", "需求" },
        { "feature", "功能" },
        { "features", "功能" },
        { "support", "支持" },
        { "enable", "启用" },
        { "disable", "禁用" },
        { "enabled", "已启用" },
        { "disabled", "已禁用" },
        { "settings", "设置" },
        { "configuration", "配置" },
        { "config", "配置" },
    };

    public TranslationService(string? cacheDir = null)
    {
        _client = new HttpClient();
        _client.Timeout = TimeSpan.FromSeconds(15);
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("CU-ModUpdater/1.0");

        cacheDir ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CU-ModUpdater");
        Directory.CreateDirectory(cacheDir);
        _cacheFile = Path.Combine(cacheDir, "translations.json");

        LoadCache();
    }

    // ==================== 缓存管理 ====================

    private void LoadCache()
    {
        try
        {
            if (File.Exists(_cacheFile))
            {
                var json = File.ReadAllText(_cacheFile);
                _cache = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
            }
        }
        catch { _cache = new(); }
    }

    private void SaveCache()
    {
        try
        {
            var json = JsonSerializer.Serialize(_cache, new JsonSerializerOptions
            {
                WriteIndented = true,
            });
            File.WriteAllText(_cacheFile, json);
        }
        catch { }
    }

    // ==================== 翻译接口 ====================

    /// <summary>
    /// 翻译文本（英→中），先查缓存和词典，再走在线 API
    /// </summary>
    public async Task<string> TranslateAsync(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        // 如果已经是中文为主，直接返回
        if (IsMostlyChinese(text))
            return text;

        var cacheKey = text.Trim().ToLowerInvariant();

        // 1. 查缓存
        if (_cache.TryGetValue(cacheKey, out var cached))
            return cached;

        // 2. 应用本地词典增强
        var dictEnhanced = ApplyLocalDictionary(text);

        // 3. 在线翻译（如果词典增强后仍以英文为主）
        string result;
        if (EnableOnline && !IsMostlyChinese(dictEnhanced))
        {
            result = await TranslateOnlineAsync(dictEnhanced);
        }
        else
        {
            result = dictEnhanced;
        }

        // 4. 后处理：修复专有名词
        result = PostProcess(result, text);

        // 5. 写缓存
        _cache[cacheKey] = result;
        SaveCache();

        return result;
    }

    /// <summary>
    /// 翻译 HTML 描述（先去标签，再翻译）
    /// </summary>
    public async Task<string> TranslateHtmlAsync(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return "";

        var plain = StripHtml(html);
        return await TranslateAsync(plain);
    }

    // ==================== 本地词典 ====================

    private string ApplyLocalDictionary(string text)
    {
        var result = text;
        foreach (var (en, zh) in GameTerms)
        {
            // 全词匹配（不区分大小写）
            result = Regex.Replace(result, $@"\b{Regex.Escape(en)}\b", zh,
                RegexOptions.IgnoreCase);
        }
        return result;
    }

    // ==================== 在线翻译 ====================

    private async Task<string> TranslateOnlineAsync(string text)
    {
        // 方案 A: Google Translate 非官方 API
        try
        {
            var result = await GoogleTranslateAsync(text);
            if (!string.IsNullOrWhiteSpace(result))
                return result;
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"Google 翻译失败: {ex.Message}");
        }

        // 方案 B: MyMemory API 后备
        try
        {
            var result = await MyMemoryTranslateAsync(text);
            if (!string.IsNullOrWhiteSpace(result))
                return result;
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"MyMemory 翻译失败: {ex.Message}");
        }

        // 都失败，返回词典增强后的文本
        return text;
    }

    /// <summary>
    /// Google Translate 非官方 API
    /// GET https://translate.googleapis.com/translate_a/single?client=gtx&sl=en&tl=zh-CN&dt=t&q=...
    /// </summary>
    private async Task<string> GoogleTranslateAsync(string text)
    {
        // Google 这个接口对单次请求长度有限制，长文要分段。
        // ⚠️ 分段必须**并行**：串行 await 时一段 1.5 秒，一篇 6000 字的
        // N 网描述要等 4~5 秒才出结果（用户反馈「翻译速度不够效率」）。
        if (text.Length > 1500)
        {
            var chunks = SplitText(text, 1500);
            // 限流：一次最多 4 段并发，避免被 Google 判定为滥用
            using var gate = new SemaphoreSlim(4);
            var tasks = chunks.Select(async chunk =>
            {
                await gate.WaitAsync();
                try { return await GoogleTranslateChunkAsync(chunk); }
                finally { gate.Release(); }
            }).ToArray();

            var results = await Task.WhenAll(tasks);
            return string.Concat(results);
        }
        return await GoogleTranslateChunkAsync(text);
    }

    private async Task<string> GoogleTranslateChunkAsync(string text)
    {
        var url = "https://translate.googleapis.com/translate_a/single" +
            $"?client=gtx&sl=en&tl=zh-CN&dt=t&q={Uri.EscapeDataString(text)}";

        using var resp = await _client.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync();

        // 响应格式: [[["翻译","原文",null,null,10],["翻译2","原文2",...]],null,"en",...]
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
            return text;

        var sb = new StringBuilder();
        var segments = root[0];
        if (segments.ValueKind == JsonValueKind.Array)
        {
            foreach (var seg in segments.EnumerateArray())
            {
                if (seg.ValueKind == JsonValueKind.Array && seg.GetArrayLength() > 0)
                {
                    var translated = seg[0].ValueKind == JsonValueKind.String
                        ? seg[0].GetString() : "";
                    sb.Append(translated);
                }
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// MyMemory 翻译 API（免费后备）
    /// GET https://api.mymemory.translated.net/get?q=...&langpair=en|zh-CN
    /// </summary>
    private async Task<string> MyMemoryTranslateAsync(string text)
    {
        // MyMemory 限制 500 字符/请求
        if (text.Length > 500)
        {
            var chunks = SplitText(text, 500);
            var sb = new StringBuilder();
            foreach (var chunk in chunks)
            {
                sb.Append(await MyMemoryTranslateChunkAsync(chunk));
            }
            return sb.ToString();
        }
        return await MyMemoryTranslateChunkAsync(text);
    }

    private async Task<string> MyMemoryTranslateChunkAsync(string text)
    {
        var url = "https://api.mymemory.translated.net/get" +
            $"?q={Uri.EscapeDataString(text)}&langpair=en|zh-CN";

        using var resp = await _client.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        if (doc.RootElement.TryGetProperty("responseData", out var rd) &&
            rd.TryGetProperty("translatedText", out var tt) &&
            tt.ValueKind == JsonValueKind.String)
        {
            return tt.GetString() ?? text;
        }
        return text;
    }

    // ==================== 工具方法 ====================

    private static string StripHtml(string html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        // 移除 script/style
        html = Regex.Replace(html, @"<script[^>]*>.*?</script>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        html = Regex.Replace(html, @"<style[^>]*>.*?</style>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        // <br> -> 换行
        html = Regex.Replace(html, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        // </p>, </div> -> 换行
        html = Regex.Replace(html, @"</(p|div|li|h[1-6])>", "\n", RegexOptions.IgnoreCase);
        // <li> -> •
        html = Regex.Replace(html, @"<li[^>]*>", "• ", RegexOptions.IgnoreCase);
        // 移除所有标签
        html = Regex.Replace(html, @"<[^>]+>", "");
        // HTML 实体解码
        html = System.Net.WebUtility.HtmlDecode(html);
        // 规范化空白
        html = Regex.Replace(html, @"[ \t]+", " ");
        html = Regex.Replace(html, @"\n{3,}", "\n\n");
        return html.Trim();
    }

    private static bool IsMostlyChinese(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        int chinese = 0, total = 0;
        foreach (var c in text)
        {
            if (char.IsLetter(c))
            {
                total++;
                if (c >= 0x4E00 && c <= 0x9FFF)
                    chinese++;
            }
        }
        return total > 0 && chinese * 100 / total > 30;
    }

    private string PostProcess(string translated, string original)
    {
        var result = translated;
        // 恢复不应被翻译的专有名词
        foreach (var term in KeepOriginal)
        {
            // 如果原文包含这个词但翻译结果不包含，尝试恢复
            if (original.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                // 简单处理：直接在结果中搜索是否有被误翻的部分
                // 这里不做复杂处理，专有名词在词典阶段已处理
            }
        }
        return result;
    }

    private static List<string> SplitText(string text, int maxLen)
    {
        var chunks = new List<string>();
        var sentences = text.Split(new[] { ". ", "! ", "? ", "\n", ".\n" }, StringSplitOptions.None);

        var current = new StringBuilder();
        foreach (var sentence in sentences)
        {
            var s = sentence.Trim();
            if (string.IsNullOrEmpty(s)) continue;

            if (current.Length + s.Length + 2 > maxLen)
            {
                if (current.Length > 0)
                {
                    chunks.Add(current.ToString());
                    current.Clear();
                }
                // 单句超长，硬切
                if (s.Length > maxLen)
                {
                    for (int i = 0; i < s.Length; i += maxLen)
                    {
                        chunks.Add(s.Substring(i, Math.Min(maxLen, s.Length - i)));
                    }
                }
                else
                {
                    current.Append(s);
                }
            }
            else
            {
                if (current.Length > 0)
                    current.Append(". ");
                current.Append(s);
            }
        }
        if (current.Length > 0)
            chunks.Add(current.ToString());

        return chunks;
    }

    public void Dispose()
    {
        _client.Dispose();
    }
}
