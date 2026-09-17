using System.Text;
using System.Text.RegularExpressions;

namespace CUModUpdater.Services;

/// <summary>
/// N 网 / GameBanana 的富文本清理。
///
/// **N 网的长描述是 BBCode + HTML 混排**，原文长这样：
///   &lt;br /&gt;[center][size=6][b][color=#ff0000]NOTE:[/color][/b]
///   [url=https://x][b]https://x[/b][/url][/center]
///   [size=4][b]安装说明[/b][/size]
///   [list][*]把 dll 放进 BepInEx/Plugins[/list]
///
/// 直接显示或丢给翻译接口都会出问题：用户看到一堆 [size=4][b]，
/// 翻译质量也被标签污染（标签会被当成正文翻译）。
/// 所以显示和翻译之前都必须先过这里。
/// </summary>
public static class Markup
{
    /// <summary>清洗富文本 → 纯文本（保留段落与列表结构）</summary>
    public static string Strip(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "";

        var s = input.Replace("\r\n", "\n").Replace("\r", "\n");

        // ── 1. HTML 换行/段落/列表先转成文本结构，其余 HTML 标签直接删 ──
        s = Regex.Replace(s, @"<\s*br\s*/?\s*>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<\s*/\s*p\s*>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<\s*li\s*>", "\n• ", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<[^>]+>", "");

        // ── 2. BBCode 里带内容的标签：只留内容 ──
        s = Regex.Replace(s, @"\[url=[^\]]*\](.*?)\[/url\]", "$1",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        // 嵌入图片整段丢掉（纯文本框里显示图片 URL 只是噪声）
        s = Regex.Replace(s, @"\[img\b[^\]]*\].*?\[/img\]", "",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        s = Regex.Replace(s, @"\[(b|i|u|s|center|left|right|quote|code|spoiler|size|color|colour|font|list|\*)\b[^\]]*\]",
            "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\[/(b|i|u|s|center|left|right|quote|code|spoiler|size|color|colour|font|list|\*)\]",
            "", RegexOptions.IgnoreCase);

        // 列表项 [*] → 项目符号
        s = Regex.Replace(s, @"\[\*\]", "\n• ");

        // ── 3. 兜底：清掉剩余的任何 [xxx] 标签（避免漏网的方括号噪声）──
        // 注意要兼容带属性的写法：[img width=383]、[font size=4]、[th] 等，
        // 所以标签名后面允许「空格+属性」或「=值」
        s = Regex.Replace(s, @"\[/?(?:[a-z]+)(?:\s[^\]]*|=[^\]]*)?\]", "", RegexOptions.IgnoreCase);

        // ── 4. HTML 实体 ──
        s = s.Replace("&nbsp;", " ").Replace("&amp;", "&").Replace("&lt;", "<")
             .Replace("&gt;", ">").Replace("&quot;", "\"").Replace("&#39;", "'");

        // ── 5. 收尾：多余空行压平 ──
        s = Regex.Replace(s, @"[ \t]+\n", "\n");
        s = Regex.Replace(s, @"\n{3,}", "\n\n");
        return s.Trim();
    }

    /// <summary>判断是否已经是中文内容（≥30% 中日韩字符）——已是中文就没必要再翻译</summary>
    public static bool IsMostlyChinese(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        int cjk = 0, letters = 0;
        foreach (var c in text)
        {
            if (c >= 0x4E00 && c <= 0x9FFF) cjk++;
            else if (char.IsLetter(c)) letters++;
        }
        var total = cjk + letters;
        return total > 0 && cjk * 10 >= total * 3;
    }
}
