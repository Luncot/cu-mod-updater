using System.Net.Http;
using System.IO.Compression;
using CUModUpdater.Models;

namespace CUModUpdater.Services;

/// <summary>
/// 更新管理器 - 负责下载、备份、安装、回滚 Mod 文件
/// </summary>
public class UpdateManager : IDisposable
{
    private readonly GitHubChecker _github;
    private readonly NexusChecker? _nexus;
    private readonly GameBananaBrowser? _gamebanana;
    private readonly string _backupDir;
    private readonly string _pluginsDir;
    private readonly string _downloadsDir;   // 下载到的压缩包归档目录（plugins\.downloads）
    private readonly string _cleanupDir;     // 改名/残留文件的隔离区（BepInEx\.cleanup）
    private readonly int _backupRetention;
    private readonly bool _enableBackup;

    /// <summary>进度回调</summary>
    public Action<string>? OnLog { get; set; }

    /// <summary>下载进度回调</summary>
    public Action<ModInfo, long, long>? OnDownloadProgress { get; set; }

    public UpdateManager(
        GitHubChecker github,
        NexusChecker? nexus,
        GameBananaBrowser? gamebanana,
        string gamePath,
        int backupRetention = 10,
        bool enableBackup = true,
        string nexusCookie = "",
        string nexusApiKey = "")
    {
        _github = github;
        _nexus = nexus;
        _gamebanana = gamebanana;
        _backupRetention = backupRetention;
        _enableBackup = enableBackup;
        _nexusCookie = nexusCookie;
        _nexusApiKey = nexusApiKey;
        _pluginsDir = Path.Combine(gamePath, "BepInEx", "plugins");
        _backupDir = Path.Combine(_pluginsDir, ".backup");
        _downloadsDir = Path.Combine(_pluginsDir, ".downloads");
        _cleanupDir = Path.Combine(gamePath, "BepInEx", ".cleanup");
    }

    /// <summary>下载归档目录（更新时把原始压缩包留一份，方便回滚或手动重装）</summary>
    public string DownloadsDir => _downloadsDir;

    private readonly string _nexusCookie = "";
    private readonly string _nexusApiKey = "";
    private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromMinutes(10) };

    /// <summary>
    /// 检查单个 Mod 的更新
    /// </summary>
    public async Task CheckUpdateAsync(ModInfo mod, ModSource source)
    {
        // ── 没有 [BepInPlugin] 的 dll：不是插件，是随宿主 Mod 附带的库 ──
        // BepInEx 只加载带该属性的程序集，所以 plugins 里的 LiteNetLib.dll /
        // 0Harmony.dll / Mono.Cecil.dll / BepInEx.dll 本体这些永远不会被当插件加载。
        // 它们只有程序集版本，拿它去比「宿主 Mod 的发行号」必然判成可更新
        // （如 LiteNetLib 1.3.5.0 → 4.0.1），而点更新装进来的是整个 Mod 而不是这个库。
        // 所以直接跳过比对，界面显示「▫ 库文件」。
        if (!mod.IsBepInExPlugin && !mod.IsDataMod)
        {
            mod.Status = UpdateStatus.Library;
            mod.LatestVersion = "";
            mod.DownloadUrl = "";
            mod.LastChecked = DateTime.Now;
            return;
        }

        mod.Status = UpdateStatus.Checking;
        mod.LastError = "";

        try
        {
            ReleaseInfo? release = null;

            if (source.IsGitHub)
            {
                OnLog?.Invoke($"正在检查 GitHub: {source.Owner}/{source.Repo}");
                release = await _github.GetLatestReleaseAsync(source.Owner, source.Repo);
            }
            else if (source.IsNexus)
            {
                // 纯数据 Mod（无 dll / 无 GUID）：按 ModId 直接取元数据版本。
                // 它们走不了 GUID 与 dllName 通道，若落到 Nexus API 上，
                // 常常因为拿不到「文件版本」而返回 Unknown（界面显示「? 未知」）。
                if (mod.IsDataMod)
                {
                    var dataRelease = Services.CasualtiesManageableService
                        .GetReleaseByModIdCached(source.ModId);
                    if (dataRelease != null)
                    {
                        OnLog?.Invoke($"正在检查 Nexus(元数据): 数据 Mod #{source.ModId}");
                        release = dataRelease;
                    }
                }

                // 优先走 CasualtiesManageable 官方元数据（免 API Key，GUID 强匹配）
                // 元数据自带 N 网最新版本，比 Nexus API 还全（含 bepinexPlugins 版本映射）
                var cmRelease = release ?? Services.CasualtiesManageableService.GetReleaseByGuidCached(mod.PluginGuid, mod.FileName);
                if (cmRelease != null)
                {
                    OnLog?.Invoke($"正在检查 Nexus(元数据): Mod #{source.ModId}");
                    release = cmRelease;
                }
                else
                {
                    // 兜底：GUID 匹配不上（local.* 命名空间、作者忘改 GUID、
                    // 数据 Mod），但来源里的 ModId 是确定的 —— 直接按 Id 查。
                    // 没有这一步，这些 mod 会一路落到「? 未知」。
                    release = Services.CasualtiesManageableService.GetReleaseByModIdCached(source.ModId);
                    if (release != null)
                        OnLog?.Invoke($"正在检查 Nexus(元数据/Id): Mod #{source.ModId}");
                }

                if (release == null && (_nexus == null || !_nexus.HasApiKey))
                {
                    mod.Status = UpdateStatus.Error;
                    mod.LastError = "元数据未收录且未配置 Nexus API Key。";
                    return;
                }

                if (release == null)
                {
                    OnLog?.Invoke($"正在检查 Nexus: Mod #{source.ModId}");
                    release = await _nexus!.GetModInfoAsync(source.ModId);
                }
            }
            else if (source.IsGameBanana)
            {
                if (_gamebanana == null)
                {
                    mod.Status = UpdateStatus.Error;
                    mod.LastError = "GameBanana 服务未初始化。";
                    return;
                }
                OnLog?.Invoke($"正在检查 GameBanana: Mod #{source.ModId}");
                release = await _gamebanana.GetLatestReleaseAsync(source.ModId);
            }

            mod.LastChecked = DateTime.Now;

            if (release == null)
            {
                mod.Status = UpdateStatus.Unknown;
                // 区分「真的没有发布版本」和「我们这边元数据都没拿到」，
                // 之前一律说「未找到任何发布版本」，用户会以为是 mod 的问题
                mod.LastError = Services.CasualtiesManageableService.IsAvailable
                    ? "未找到任何发布版本"
                    : "元数据不可用（缓存缺失且下载失败），无法比对版本。请检查网络后重试。";
                return;
            }

            mod.LatestVersion = release.Version;
            mod.DownloadUrl = release.DownloadUrl;
            mod.AssetName = release.AssetName;
            mod.ReleaseNotes = release.ReleaseNotes;

            // 版本比较
            // ⚠️ 基准用 mod.CompareVersion（= 界面「当前版本」列显示的值），
            // 不要用 mod.CurrentVersion：非插件 dll（BepInEx 本体、纯库）没有
            // BepInPlugin 版本，CurrentVersion 为空但列里显示的是回退的程序集版本，
            // 于是出现「当前 5.4.23.5 / 最新 5.4.23.5 / 状态却是可更新」的自相矛盾。
            if (string.IsNullOrEmpty(release.Version))
            {
                mod.Status = UpdateStatus.Unknown;
            }
            else if (string.IsNullOrEmpty(mod.CompareVersion))
            {
                // 本地完全读不到版本 → 保守当作有更新（界面上会显示「当前版本: 未知」）
                mod.Status = UpdateStatus.UpdateAvailable;
            }
            else if (VersionHelper.IsNewer(release.Version, mod.CompareVersion))
            {
                mod.Status = UpdateStatus.UpdateAvailable;
                OnLog?.Invoke($"  {mod.Name}: {mod.CompareVersion} -> {release.Version} (有更新)");
            }
            else
            {
                mod.Status = UpdateStatus.UpToDate;
                OnLog?.Invoke($"  {mod.Name}: {mod.CompareVersion} (已最新)");
            }
        }
        catch (Exception ex)
        {
            mod.Status = UpdateStatus.Error;
            mod.LastError = ex.Message;
            OnLog?.Invoke($"  {mod.Name}: 检查失败 - {ex.Message}");
        }
    }

    /// <summary>
    /// 批量检查更新（带并发控制）。
    /// 同组附属库（与主插件同源）跳过——它们跟主插件共享同一个版本号，
    /// 各自查一遍只是重复请求，还会在界面上产生一堆同源结果。
    /// </summary>
    public async Task CheckAllAsync(List<ModInfo> mods, int maxConcurrency = 5)
    {
        var toCheck = mods
            .Where(m => m.IsEnabled && m.Source != null && m.Source.IsValid)
            // 组内成员（非组长）不单独检查
            .Where(m => m.GroupOwner == null || m.IsGroupHead)
            .ToList();

        if (toCheck.Count == 0)
        {
            OnLog?.Invoke("没有配置更新源的 Mod，请点击「🍌 GB 匹配」或右键为 Mod 配置来源。");
            return;
        }

        OnLog?.Invoke($"开始检查 {toCheck.Count} 个 Mod 的更新...");

        using var semaphore = new SemaphoreSlim(maxConcurrency);
        var tasks = toCheck.Select(async mod =>
        {
            await semaphore.WaitAsync();
            try
            {
                await CheckUpdateAsync(mod, mod.Source!);
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);
        OnLog?.Invoke("检查完成。");
    }

    /// <summary>
    /// 下载并安装单个 Mod 更新
    /// </summary>
    public async Task<bool> InstallUpdateAsync(ModInfo mod)
    {
        if (mod.Source == null || !mod.Source.IsValid)
        {
            OnLog?.Invoke($"错误: {mod.Name} 未配置更新源");
            mod.Status = UpdateStatus.Error;
            mod.LastError = "未配置更新源";
            return false;
        }

        // N 网来源：免费账号的下载直链是 Premium 专属（N 网 API 明确拒绝），
        // 正规通道 = 网页 slow download。这里直接打开该文件的下载确认页，
        // 用户点 Manual download → 5 秒倒计时后自动开始（少一次导航）。
        if (mod.Source.IsNexus)
        {
            var parsed = NexusCookieDownloader.ParseNexusUrl(mod.DownloadUrl);
            var modId = parsed?.modId ?? mod.Source.ModId;
            var fileId = parsed?.fileId;
            var url = string.IsNullOrEmpty(fileId)
                ? $"https://www.nexusmods.com/scavprototype/mods/{modId}?tab=files"
                : $"https://www.nexusmods.com/scavprototype/mods/{modId}?tab=files&file_id={fileId}";

            OnLog?.Invoke($"🌐 {mod.Name} {mod.CurrentVersion} → {mod.LatestVersion}：已打开 N 网下载页" +
                          "（免费账号需网页下载，点 Manual download 等 5 秒即可）");
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch { }
            mod.Status = UpdateStatus.UpdateAvailable; // 保持待更新，手动替换后重新扫描即可
            return false;
        }

        if (string.IsNullOrEmpty(mod.DownloadUrl))
        {
            OnLog?.Invoke($"错误: {mod.Name} 没有可用的下载链接");
            mod.Status = UpdateStatus.Error;
            mod.LastError = "没有可用的下载链接";
            return false;
        }

        mod.Status = UpdateStatus.Updating;
        OnLog?.Invoke($"开始更新 {mod.Name} ({mod.CurrentVersion} -> {mod.LatestVersion})");

        // 临时下载目录
        var tempDir = Path.Combine(Path.GetTempPath(), "CU-ModUpdater-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            // 1. 备份旧文件
            if (_enableBackup && File.Exists(mod.FilePath))
            {
                OnLog?.Invoke("  正在备份旧版本...");
                BackupOldFile(mod);
            }

            // 2. 下载新版本
            var downloadExt = Path.GetExtension(
                !string.IsNullOrEmpty(mod.AssetName) ? mod.AssetName : mod.DownloadUrl);
            if (string.IsNullOrEmpty(downloadExt))
                downloadExt = ".dll";

            var tempDownloadPath = Path.Combine(tempDir, "download" + downloadExt);
            OnLog?.Invoke($"  正在下载 {mod.AssetName}...");

            // 创建进度报告
            var progress = new Progress<(long received, long total)>(p =>
            {
                OnDownloadProgress?.Invoke(mod, p.received, p.total);
            });

            // Nexus 下载链接是临时的，可能需要特殊头
            // GitHub 下载链接是直接的
            await _github.DownloadFileAsync(mod.DownloadUrl, tempDownloadPath,
                progress as IProgress<(long, long)>);

            // 3. 归档原始包（用户要求：下载到的压缩包统一放一个文件夹，方便留底/手动重装）
            ArchiveDownload(mod, tempDownloadPath, downloadExt);

            // 4. 解压（如为 zip）并替换文件
            return await InstallFromDownloadedFileAsync(mod, tempDownloadPath, downloadExt, tempDir);
        }
        catch (Exception ex)
        {
            mod.Status = UpdateStatus.UpdateFailed;
            mod.LastError = ex.Message;
            OnLog?.Invoke($"  ✗ {mod.Name} 更新失败: {ex.Message}");
            return false;
        }
        finally
        {
            // 清理临时文件
            try
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
            catch { }
        }
    }

    /// <summary>
    /// 把下载到的原始包复制进 plugins\.downloads，文件名带上「mod 名 + 版本」。
    /// 保留原始包的理由：N 网/GB 的旧版本常常下架，出事时只能靠本地这一份。
    /// </summary>
    private void ArchiveDownload(ModInfo mod, string tempPath, string ext)
    {
        try
        {
            Directory.CreateDirectory(_downloadsDir);
            var name = SanitizeFileName(
                $"{mod.Name}_{mod.LatestVersion}{(string.IsNullOrEmpty(mod.LatestVersion) ? "" : "_")}{DateTime.Now:yyyyMMdd_HHmm}{ext}");
            var dest = Path.Combine(_downloadsDir, name);
            int i = 1;
            while (File.Exists(dest))
                dest = Path.Combine(_downloadsDir,
                    $"{Path.GetFileNameWithoutExtension(name)}({i++}){Path.GetExtension(name)}");

            File.Copy(tempPath, dest, overwrite: false);
            OnLog?.Invoke($"  已归档: .downloads\\{Path.GetFileName(dest)}");
        }
        catch (Exception ex)
        {
            // 归档失败不该让更新失败
            OnLog?.Invoke($"  （归档失败，不影响更新: {ex.Message}）");
        }
    }

    /// <summary>
    /// 把文件移进 BepInEx/.cleanup（按日期分目录）。不删除 —— 改名/换版本时
    /// 用户可能还要把它移回来，尤其是旧版本已经下架的。
    /// </summary>
    private void MoveToCleanup(string filePath)
    {
        try
        {
            var sub = Path.Combine(_cleanupDir, $"{DateTime.Now:yyyyMMdd}_rename");
            Directory.CreateDirectory(sub);
            var dest = Path.Combine(sub, Path.GetFileName(filePath));
            if (File.Exists(dest))
                dest = Path.Combine(sub,
                    $"{Path.GetFileNameWithoutExtension(filePath)}_{DateTime.Now:HHmmss}{Path.GetExtension(filePath)}");

            File.Move(filePath, dest);
            OnLog?.Invoke($"  旧文件已移入隔离区: .cleanup\\{Path.GetFileName(sub)}\\{Path.GetFileName(dest)}");
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"  （旧文件移入隔离区失败: {ex.Message}）");
        }
    }

    /// <summary>文件名里不能出现的字符换成下划线</summary>
    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name.Trim();
    }

    /// <summary>
    /// 把新 dll 装到旧文件的位置（改名感知）。
    /// 抽出来是为了让「一键更新」和「待装包安装」共用同一套规则，
    /// 否则两条路径早晚会有一边忘了处理改名/禁用副本。
    /// </summary>
    private void ReplaceModFile(ModInfo mod, string finalDllPath)
    {
        // 替换旧文件
        OnLog?.Invoke("  正在替换文件...");

        var oldPath = mod.FilePath;
        var oldName = Path.GetFileName(oldPath);
        var newName = Path.GetFileName(finalDllPath);
        var targetDir = Path.GetDirectoryName(oldPath) ?? _pluginsDir;
        Directory.CreateDirectory(targetDir);

        // ── 作者改了 dll 文件名的情况 ──
        // 以前是「把新内容写进旧文件名」，结果磁盘上的名字和作者发布的对不上，
        // 下次更新匹配又会歪，而且用户看目录会以为没更新。
        // 现在按新名字装，旧文件移进隔离区（不删，可随时移回）。
        var oldKey = ConfigManager.GetFileKey(oldName);
        var newKey = ConfigManager.GetFileKey(newName);
        var renamed = !string.Equals(oldKey, newKey, StringComparison.OrdinalIgnoreCase);

        var targetPath = renamed
            ? Path.Combine(targetDir, newName)
            : Path.Combine(targetDir, oldName.EndsWith("_disabled", StringComparison.OrdinalIgnoreCase)
                ? oldName[..^"_disabled".Length]
                : oldName);

        File.Copy(finalDllPath, targetPath, overwrite: true);

        if (renamed)
        {
            OnLog?.Invoke($"  文件名已变更: {oldName} -> {newName}");
            if (File.Exists(oldPath) &&
                !string.Equals(Path.GetFullPath(oldPath), Path.GetFullPath(targetPath),
                StringComparison.OrdinalIgnoreCase))
                MoveToCleanup(oldPath);
        }
        else if (!string.Equals(oldPath, targetPath, StringComparison.OrdinalIgnoreCase) &&
                 File.Exists(oldPath))
        {
            // 原来是 _disabled：新版装成启用状态，禁用副本留着没意义
            try { File.Delete(oldPath); } catch { }
        }

        // 回写，让调用方（一键更新 / 待装包安装）看到的是磁盘真实状态
        mod.FilePath = targetPath;
        mod.FileName = Path.GetFileName(targetPath);
        mod.IsEnabled = true;
    }

    /// <summary>
    /// 从已下载的文件（zip 或 dll）安装：解压 → 定位 dll → 备份已做 → 替换
    /// </summary>
    private Task<bool> InstallFromDownloadedFileAsync(ModInfo mod, string tempDownloadPath, string downloadExt, string tempDir)
    {
        try
        {
            // 如果是 zip，解压找到 .dll
            string finalDllPath;
            if (downloadExt.Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                OnLog?.Invoke("  解压下载文件...");
                var extractDir = Path.Combine(tempDir, "extracted");
                ZipFile.ExtractToDirectory(tempDownloadPath, extractDir, overwriteFiles: true);

                // 找到 .dll 文件
                var dlls = Directory.GetFiles(extractDir, "*.dll", SearchOption.AllDirectories)
                    .Where(f => !f.EndsWith("_disabled"))
                    .ToList();

                if (dlls.Count == 0)
                {
                    throw new InvalidOperationException("下载的 zip 中未找到 .dll 文件");
                }

                // 如果只有一个 dll，直接用；如果有多个，尝试匹配文件名
                // （用 GetFileKey 归一化，本地文件可能带 _disabled 或中文前缀）
                var modKey = ConfigManager.GetFileKey(mod.FileName);
                var matchingDll = dlls.FirstOrDefault(d =>
                    Path.GetFileName(d).Equals(mod.FileName, StringComparison.OrdinalIgnoreCase) ||
                    ConfigManager.GetFileKey(d).Equals(modKey, StringComparison.OrdinalIgnoreCase));

                finalDllPath = matchingDll ?? dlls[0];
            }
            else
            {
                // 直接是 dll 文件
                finalDllPath = tempDownloadPath;
            }

            ReplaceModFile(mod, finalDllPath);

            // 更新 Mod 信息（文件路径/名字已由 ReplaceModFile 回写）
            mod.CurrentVersion = mod.LatestVersion;
            mod.Status = UpdateStatus.Updated;
            OnLog?.Invoke($"  ✓ {mod.Name} 更新成功！");
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            mod.Status = UpdateStatus.UpdateFailed;
            mod.LastError = ex.Message;
            OnLog?.Invoke($"  ✗ {mod.Name} 更新失败: {ex.Message}");
            return Task.FromResult(false);
        }
    }

    /// <summary>待装包目录：把 N 网手动下载的 zip/dll 丢进来，程序按 GUID 自动认领</summary>
    public string InboxDir => Path.Combine(_pluginsDir, ".inbox");

    /// <summary>一个待装包的扫描结果</summary>
    public sealed class InboxPackage
    {
        public string ArchivePath = "";
        public string TempDir = "";
        public List<(string DllPath, string Guid, string Name, string Version)> Dlls = new();
        public List<(string DllName, ModInfo? Target, string Reason)> Matches = new();
    }

    /// <summary>
    /// 扫描待装包目录，按 GUID→文件名 两级把它和已安装 mod 对上。
    /// 只做「认领」，不动文件；真正的安装在 InstallInboxAsync 里。
    /// </summary>
    public List<InboxPackage> ScanInbox(List<ModInfo> mods)
    {
        var result = new List<InboxPackage>();
        if (!Directory.Exists(InboxDir)) return result;

        var byGuid = mods.Where(m => !string.IsNullOrEmpty(m.PluginGuid))
            .GroupBy(m => m.PluginGuid, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var byKey = mods.Where(m => !m.IsFolderNode)
            .GroupBy(m => ConfigManager.GetFileKey(m.FileName), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var file in Directory.GetFiles(InboxDir)
                     .Where(f => f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                              || f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
        {
            var pkg = new InboxPackage { ArchivePath = file };
            try
            {
                var temp = Path.Combine(Path.GetTempPath(), "CU-Inbox-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(temp);
                pkg.TempDir = temp;
                var dlls = new List<string>();
                if (file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    var copy = Path.Combine(temp, Path.GetFileName(file));
                    File.Copy(file, copy, overwrite: true);
                    dlls.Add(copy);
                }
                else
                {
                    ZipFile.ExtractToDirectory(file, temp, overwriteFiles: true);
                    dlls.AddRange(Directory.GetFiles(temp, "*.dll", SearchOption.AllDirectories)
                        .Where(f => !f.EndsWith("_disabled"))
                        .Where(f => !IsFrameworkDll(Path.GetFileName(f))));
                }

                foreach (var dll in dlls)
                {
                    var id = PluginScanner.ReadPluginIdentity(dll);
                    if (id == null) continue;   // 非 BepInEx 插件（原生库/依赖），跳过
                    var (guid, name, version) = id.Value;
                    pkg.Dlls.Add((dll, guid, name, version));

                    ModInfo? target = null;
                    var reason = "";
                    if (!string.IsNullOrEmpty(guid) && byGuid.TryGetValue(guid, out var t1))
                    {
                        target = t1;
                        reason = $"GUID 匹配（{guid}）";
                    }
                    else if (byKey.TryGetValue(ConfigManager.GetFileKey(Path.GetFileName(dll)), out var t2))
                    {
                        target = t2;
                        reason = "文件名匹配";
                    }
                    else
                    {
                        reason = $"没有同 GUID 的已安装 mod（{guid}）";
                    }
                    pkg.Matches.Add((Path.GetFileName(dll), target, reason));
                }
            }
            catch (Exception ex)
            {
                pkg.Matches.Add(("(解压失败)", null, ex.Message));
            }

            result.Add(pkg);
        }

        return result;
    }

    /// <summary>
    /// 安装待装包：认领到的按「更新」处理（改名/禁用副本规则同 ReplaceModFile），
    /// 认不到的原包留在 .inbox 里不动 —— 不擅自往 plugins 里塞未知 dll。
    /// </summary>
    public async Task<(int installed, int skipped, int failed)> InstallInboxAsync(List<ModInfo> mods)
    {
        var packages = ScanInbox(mods);
        int installed = 0, skipped = 0, failed = 0;

        if (packages.Count == 0)
        {
            OnLog?.Invoke($"待装包目录是空的：{InboxDir}");
            return (0, 0, 0);
        }

        OnLog?.Invoke($"=== 待装包：发现 {packages.Count} 个文件 ===");

        foreach (var pkg in packages)
        {
            var name = Path.GetFileName(pkg.ArchivePath);
            var claimed = pkg.Matches.Where(m => m.Target != null).ToList();

            if (claimed.Count == 0)
            {
                skipped++;
                OnLog?.Invoke($"⏭ {name}：没有已安装的 mod 能对上（{pkg.Matches.FirstOrDefault().Reason}）");
                continue;
            }

            try
            {
                foreach (var (dllName, target, _) in claimed)
                {
                    var dllPath = pkg.Dlls.First(d => Path.GetFileName(d.DllPath) == dllName).DllPath;
                    var t = target!;

                    if (_enableBackup && File.Exists(t.FilePath))
                        BackupOldFile(t);

                    OnLog?.Invoke($"📦 {name} → {t.Name}（{t.DisplayVersion} → {pkg.Dlls.First(d => d.DllPath == dllPath).Version}）");
                    ReplaceModFile(t, dllPath);
                    t.CurrentVersion = pkg.Dlls.First(d => d.DllPath == dllPath).Version;
                    t.Status = UpdateStatus.Updated;
                }

                // 装完把原包移到 .downloads 留底（不再留在 .inbox 里重复触发）
                Directory.CreateDirectory(_downloadsDir);
                var dest = Path.Combine(_downloadsDir, name);
                int i = 1;
                while (File.Exists(dest))
                    dest = Path.Combine(_downloadsDir,
                        $"{Path.GetFileNameWithoutExtension(name)}({i++}){Path.GetExtension(name)}");
                File.Move(pkg.ArchivePath, dest);

                installed++;
                OnLog?.Invoke($"  ✓ {name} 安装完成，原包已归档到 .downloads");
            }
            catch (Exception ex)
            {
                failed++;
                OnLog?.Invoke($"  ✗ {name} 安装失败: {ex.Message}");
            }
            finally
            {
                TryDeleteDir(pkg.TempDir);
            }
        }

        OnLog?.Invoke($"待装包处理完成：{installed} 个已安装 / {skipped} 个没认领 / {failed} 个失败");
        await Task.CompletedTask;
        return (installed, skipped, failed);
    }

    /// <summary>清掉 ScanInbox 解出来的临时目录（预览模式用，避免留垃圾）</summary>
    public void DisposeInboxCache(IEnumerable<InboxPackage> packages)
    {
        foreach (var p in packages) TryDeleteDir(p.TempDir);
    }

    private static void TryDeleteDir(string dir)
    {
        try { if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) Directory.Delete(dir, true); }
        catch { }
    }

    /// <summary>压缩包里常见的框架 dll，不是 mod 本体</summary>
    private static bool IsFrameworkDll(string fileName) =>
        fileName.StartsWith("0Harmony", StringComparison.OrdinalIgnoreCase) ||
        fileName.StartsWith("BepInEx", StringComparison.OrdinalIgnoreCase) ||
        fileName.StartsWith("UnityEngine", StringComparison.OrdinalIgnoreCase) ||
        fileName.StartsWith("Unity.", StringComparison.OrdinalIgnoreCase) ||
        fileName.StartsWith("Mono.", StringComparison.OrdinalIgnoreCase) ||
        fileName.StartsWith("System.", StringComparison.OrdinalIgnoreCase) ||
        fileName.Equals("Mono.Cecil.dll", StringComparison.OrdinalIgnoreCase) ||
        fileName.Equals("LiteNetLib.dll", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 批量安装所有有更新的 Mod
    /// </summary>
    public async Task<(int success, int failed)> InstallAllUpdatesAsync(List<ModInfo> mods)
    {
        int success = 0, failed = 0;

        // 按 GUID/ModId 去重（防止同一 mod 多条目重复更新/重复开网页）
        var toUpdate = mods
            // 已禁用的不参与更新：更新会把文件换成启用状态，等于偷偷把用户关掉的 mod 又打开
            .Where(m => m.Status == UpdateStatus.UpdateAvailable && m.IsEnabled)
            .GroupBy(m => !string.IsNullOrEmpty(m.PluginGuid)
                ? "guid:" + m.PluginGuid
                : "file:" + Path.GetFileName(m.FilePath))
            .Select(g => g.First())
            .ToList();

        // N 网来源单独处理：免费账号不能直链下载，改为统一打开下载页（去重）
        var nexusMods = toUpdate.Where(m => m.Source != null && m.Source.IsNexus).ToList();
        var autoMods = toUpdate.Except(nexusMods).ToList();

        OnLog?.Invoke($"开始更新 {toUpdate.Count} 个 Mod（自动 {autoMods.Count}，N 网 {nexusMods.Count}）...");

        // N 网：按 ModId 去重后每个只开一个标签页
        if (nexusMods.Count > 0)
        {
            var opened = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var mod in nexusMods)
            {
                var id = mod.Source!.ModId;
                if (!opened.Add(id)) continue;
                var url = $"https://www.nexusmods.com/scavprototype/mods/{id}";
                OnLog?.Invoke($"🌐 {mod.Name}: 已在浏览器打开 N 网下载页（请手动下载后替换 {Path.GetFileName(mod.FilePath)}）");
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
                }
                catch { }
                mod.Status = UpdateStatus.UpdateAvailable; // 保持待更新，等手动替换后重新扫描
                await Task.Delay(300); // 避免浏览器弹窗过快丢标签
            }
            OnLog?.Invoke($"N 网 {nexusMods.Count} 个 mod 已打开 {opened.Count} 个下载页（免费账号无法直链下载）");
        }

        // 其余来源正常自动更新
        foreach (var mod in autoMods)
        {
            if (await InstallUpdateAsync(mod))
                success++;
            else
                failed++;
        }

        OnLog?.Invoke($"更新完成: {success} 成功, {failed} 失败, N 网 {nexusMods.Count} 个已打开下载页");
        return (success, failed);
    }

    /// <summary>
    /// 备份旧版本文件
    /// </summary>
    private void BackupOldFile(ModInfo mod)
    {
        if (!Directory.Exists(_backupDir))
            Directory.CreateDirectory(_backupDir);

        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        // 统一用 GetFileKey：去掉 _disabled 与 .dll，避免备份名出现
        // 「xxx.dll_1.0.0_20260911.dll_disabled」这种畸形组合
        var baseName = ConfigManager.GetFileKey(mod.FileName);
        var backupName = $"{baseName}_{mod.CurrentVersion}_{timestamp}.dll";
        var backupPath = Path.Combine(_backupDir, backupName);

        File.Copy(mod.FilePath, backupPath, overwrite: true);
        OnLog?.Invoke($"  已备份: {backupName}");

        // 清理过多的备份
        CleanOldBackups(baseName);
    }

    /// <summary>
    /// 清理旧的备份文件，只保留最近 N 个
    /// </summary>
    private void CleanOldBackups(string baseName)
    {
        try
        {
            var backups = Directory.GetFiles(_backupDir, $"{baseName}_*")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTime)
                .ToList();

            for (int i = _backupRetention; i < backups.Count; i++)
            {
                backups[i].Delete();
            }
        }
        catch { }
    }

    /// <summary>
    /// 获取某个 Mod 的备份列表
    /// </summary>
    public List<BackupEntry> GetBackups(string baseName)
    {
        var result = new List<BackupEntry>();
        if (!Directory.Exists(_backupDir))
            return result;

        var key = ConfigManager.GetFileKey(baseName);
        var pattern = $"{key}_*";
        foreach (var file in Directory.GetFiles(_backupDir, pattern)
            .OrderByDescending(f => f))
        {
            var fi = new FileInfo(file);
            // 备份名格式: {key}_{version}_{yyyyMMdd_HHmmss}.dll
            // 用正则从右侧锚定时间戳（时间戳自身含下划线，不能用简单 split）
            var name = Path.GetFileNameWithoutExtension(fi.Name);
            string version = "未知", date = "";
            if (name.StartsWith(key + "_", StringComparison.OrdinalIgnoreCase))
            {
                var rest = name[(key.Length + 1)..];
                var m = System.Text.RegularExpressions.Regex.Match(
                    rest, @"^(?<ver>.+)_(?<ts>\d{8}_\d{6})$");
                if (m.Success)
                {
                    version = m.Groups["ver"].Value;
                    date = m.Groups["ts"].Value;
                }
                else version = rest;
            }

            result.Add(new BackupEntry
            {
                FilePath = file,
                FileName = fi.Name,
                Version = version,
                BackupDate = fi.LastWriteTime,
                Size = fi.Length,
            });
        }

        return result;
    }

    /// <summary>
    /// 从备份回滚
    /// </summary>
    public bool Rollback(ModInfo mod, BackupEntry backup)
    {
        try
        {
            var targetPath = mod.FilePath;
            // 先备份当前版本（如果文件存在且与备份不同）
            if (File.Exists(targetPath) && targetPath != backup.FilePath)
            {
                if (_enableBackup)
                    BackupOldFile(mod);
            }

            File.Copy(backup.FilePath, targetPath, overwrite: true);
            mod.CurrentVersion = backup.Version;
            mod.Status = UpdateStatus.UpToDate;
            OnLog?.Invoke($"已回滚 {mod.Name} 到版本 {backup.Version}");
            return true;
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"回滚失败: {ex.Message}");
            return false;
        }
    }

    public void Dispose()
    {
        _github.Dispose();
        _nexus?.Dispose();
        _gamebanana?.Dispose();
    }
}

/// <summary>
/// 备份文件信息
/// </summary>
public class BackupEntry
{
    public string FilePath { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Version { get; set; } = "";
    public DateTime BackupDate { get; set; }
    public long Size { get; set; }

    public string DisplayDate => BackupDate.ToString("yyyy-MM-dd HH:mm");
    public string DisplaySize => Size switch
    {
        < 1024 => $"{Size} B",
        < 1024 * 1024 => $"{Size / 1024.0:F1} KB",
        _ => $"{Size / 1024.0 / 1024.0:F1} MB",
    };
}
