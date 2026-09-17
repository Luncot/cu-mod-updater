using CUModUpdater.Models;
using CUModUpdater.Forms;
using CUModUpdater.Services;
using System.Text.Json;

namespace CUModUpdater;

/// <summary>
/// CU Mod 更新器入口
/// Casualties Unknown Demo BepInEx Mod 更新器
/// 一键连接 Nexus Mods 和 GitHub 检查并安装 Mod 更新
///
/// 命令行模式:
///   CU-ModUpdater.exe --scan [游戏路径]   导出本地 Mod 清单 JSON（GUID/版本/文件名）后退出
///   CU-ModUpdater.exe --check [游戏路径]  扫描 + 检查更新 + 打印结果（无 UI，冒烟测试/脚本比对用）
/// </summary>
internal static class Program
{
    /// <summary>
    /// 启动参数 `--browser [来源]` 指定的浏览器初始标签页
    /// （curated / github / nexus）。null = 不自动打开浏览器。
    /// </summary>
    internal static string? AutoOpenBrowserSource;

    /// <summary>
    /// 启动追踪：写 %APPDATA%\CU-ModUpdater\trace.log。
    /// 之前出现过「进程直接消失、无事件日志、无错误框」的静默退出，
    /// 没有这条轨迹就只能靠猜。稳定后可以移除。
    /// </summary>
    internal static void Trace(string msg)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "CU-ModUpdater");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "trace.log"),
                $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\r\n");
        }
        catch { }
    }

    [STAThread]
    static int Main(string[] args)
    {
        // --scan 命令行模式：扫描插件输出 JSON（供脚本批量比对用）
        if (args.Length >= 1 && args[0] == "--scan")
        {
            var gamePath = args.Length >= 2 ? args[1] : "";
            if (string.IsNullOrEmpty(gamePath) || !PluginScanner.IsValidGamePath(gamePath))
                gamePath = PluginScanner.AutoDetectGamePath() ?? gamePath;

            try
            {
                var scanner = new PluginScanner(gamePath);
                var mods = scanner.Scan();
                var list = mods.Select(m => new
                {
                    guid = m.PluginGuid,
                    name = m.Name,
                    version = m.CurrentVersion,
                    asmVersion = m.AssemblyVersion,
                    file = m.FileName,
                    enabled = m.IsEnabled,
                    isPlugin = m.IsBepInExPlugin,
                });
                var json = JsonSerializer.Serialize(list, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                });
                Console.WriteLine(json);
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("SCAN_ERROR: " + ex.Message);
                return 1;
            }
        }

        // --check 命令行模式：扫描 + 检查更新 + 打印结果（无 UI，冒烟测试/脚本比对用）
        // Main 必须保持 [STAThread] 同步方法（WinForms 要求），异步逻辑放独立方法里阻塞等待
        if (args.Length >= 1 && args[0] == "--check")
        {
            return RunCheckAsync(args).GetAwaiter().GetResult();
        }

        // --browser 命令行模式：启动后直接打开 Mod 浏览器（可指定标签页）
        //   CU-ModUpdater.exe --browser            默认标签页
        //   CU-ModUpdater.exe --browser nexus      N 网标签页
        if (args.Length >= 1 && args[0] == "--browser")
        {
            AutoOpenBrowserSource = args.Length >= 2 ? args[1].ToLowerInvariant() : "";
        }

        // --preview 诊断模式：打印某个 N 网 Mod 在浏览器里会怎么显示
        //   CU-ModUpdater.exe --preview 324
        // 用来核对富文本清洗（BBCode/HTML）与字段补全是否符合预期
        if (args.Length >= 2 && args[0] == "--preview")
        {
            var entry = Services.CasualtiesManageableService.FindByModId(args[1]);
            if (entry == null)
            {
                Console.WriteLine($"元数据里找不到 ModId={args[1]}");
                return 1;
            }
            Console.WriteLine($"[名称] {entry.Name}");
            Console.WriteLine($"[作者] {entry.Author}   [版本] {entry.Version}   [ID] {entry.NexusModId}");
            Console.WriteLine($"[分类] {Services.CategoryClassifier.Classify(entry)}");
            Console.WriteLine($"[图片] {(entry.Images.Count > 0 ? entry.Images[0] : "（无）")}");
            Console.WriteLine($"[下载] {entry.Statistics?.TotalDownloads ?? 0}   好评 {entry.Statistics?.Endorsements ?? 0}");
            Console.WriteLine($"[dll] {string.Join(", ", entry.DllNames)}");
            Console.WriteLine();
            Console.WriteLine("=== 原始描述（前 400 字）===");
            Console.WriteLine(entry.Description.Length > 400 ? entry.Description[..400] : entry.Description);
            var stripped = Services.Markup.Strip(entry.Description);
            Console.WriteLine();
            Console.WriteLine($"=== 清洗后（{stripped.Length} 字，原 {entry.Description.Length} 字）===");
            Console.WriteLine(stripped.Length > 500 ? stripped[..500] + "…" : stripped);
            Console.WriteLine();
            var leftover = System.Text.RegularExpressions.Regex.Matches(stripped, @"\[/?[a-z]+[^\]]*\]|<\w+[^>]*>");
            Console.WriteLine($"残留标签数: {leftover.Count}" +
                (leftover.Count > 0 ? "  → " + string.Join(" | ", leftover.Take(5).Select(m => m.Value)) : "  ✓ 干净"));
            return 0;
        }

        // .NET 8: ApplicationConfiguration.Initialize 会根据 csproj 中的
        // ApplicationHighDpiMode=PerMonitorV2 自动配置 DPI 感知
        ApplicationConfiguration.Initialize();

        // 全局异常兜底：之前出现过「进程直接消失、无事件日志、无错误框」的静默退出，
        // 抓下来至少能看见是什么错。
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            Trace("ThreadException: " + e.Exception);
            MessageBox.Show($"{e.Exception.GetType().Name}: {e.Exception.Message}\n\n{e.Exception.StackTrace}",
                "发生未处理的异常", MessageBoxButtons.OK, MessageBoxIcon.Error);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Trace("UnhandledException: " + e.ExceptionObject);
            MessageBox.Show($"{(e.ExceptionObject as Exception)?.GetType().Name}: {(e.ExceptionObject as Exception)?.Message}",
                "发生严重错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        };
        // 后台 Task 里没人 await 的异常（不会崩进程，但可能就是问题的线索）
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Trace("UnobservedTaskException: " + e.Exception);
            e.SetObserved();
        };
        // 进程正常退出路径（Environment.Exit / 主窗体关闭后 GC 终结时触发）
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Trace("ProcessExit");
        // 静默消失最常见的原因是栈溢出 —— 它会绕过所有异常处理器，
        // 只能靠这条日志确认「走到了哪一步之后才死的」
        Trace("handlers wired");

        Trace("=== main begin ===");
        Application.Run(new MainForm());
        Trace("=== Application.Run returned ===");
        return 0;
    }

    /// <summary>--check 无头模式：扫描 + 检查更新 + 打印结果（冒烟测试 / 脚本比对用）</summary>
    private static async Task<int> RunCheckAsync(string[] args)
    {
        var gamePath = args.Length >= 2 ? args[1] : "";
        if (string.IsNullOrEmpty(gamePath) || !PluginScanner.IsValidGamePath(gamePath))
            gamePath = PluginScanner.AutoDetectGamePath() ?? gamePath;

        try
        {
            var config = ConfigManager.Load();
            if (!string.IsNullOrEmpty(gamePath)) config.GamePath = gamePath;
            var scanner = new PluginScanner(config.GamePath);
            var mods = scanner.Scan();
            Console.WriteLine($"[scan] {mods.Count} mods");

            using var cm = new Services.CasualtiesManageableService();
            var metaOk = await cm.EnsureLoadedAsync();
            Console.WriteLine($"[meta] available={metaOk}");

            var github = new GitHubChecker(string.IsNullOrEmpty(config.GitHubToken) ? null : config.GitHubToken);
            NexusChecker? nexus = string.IsNullOrEmpty(config.NexusApiKey)
                ? null : new NexusChecker(config.NexusApiKey, config.NexusGameDomain);
            using var updater = new UpdateManager(
                github, nexus, new GameBananaBrowser(), config.GamePath,
                config.BackupRetention, enableBackup: false);
            updater.OnLog = _ => { };

            ConfigManager.AutoConfigureAll(config, mods);
            foreach (var m in mods)
                m.Source ??= ConfigManager.GetSource(config, m);

            await updater.CheckAllAsync(mods, 8);

            Console.WriteLine();
            Console.WriteLine("[updateable]");
            foreach (var m in mods.Where(m => m.Status == UpdateStatus.UpdateAvailable))
                Console.WriteLine($"  {m.Name,-40} {m.DisplayVersion} -> {m.LatestVersion}  file={m.FileName}");

            Console.WriteLine();
            Console.WriteLine("[summary]");
            foreach (var g in mods.GroupBy(m => m.Status).OrderByDescending(g => g.Count()))
                Console.WriteLine($"  {g.Key,-16} {g.Count()}");

            Console.WriteLine();
            Console.WriteLine("[unknown / error 明细]");
            foreach (var m in mods.Where(m => m.Status is UpdateStatus.Unknown or UpdateStatus.Error))
                Console.WriteLine($"  {m.Name,-40} #{m.Source?.ModId,-5} {m.LastError}");

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("CHECK_ERROR: " + ex);
            return 1;
        }
    }
}
