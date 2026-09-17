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
        _backupDir = Path.Combine(gamePath, "BepInEx", "plugins", ".backup");
    }

    private readonly string _nexusCookie = "";
    private readonly string _nexusApiKey = "";
    private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromMinutes(10) };

    /// <summary>
    /// 检查单个 Mod 的更新
    /// </summary>
    public async Task CheckUpdateAsync(ModInfo mod, ModSource source)
    {
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
            if (string.IsNullOrEmpty(release.Version))
            {
                mod.Status = UpdateStatus.Unknown;
            }
            else if (string.IsNullOrEmpty(mod.CurrentVersion))
            {
                mod.Status = UpdateStatus.UpdateAvailable;
            }
            else if (VersionHelper.IsNewer(release.Version, mod.CurrentVersion))
            {
                mod.Status = UpdateStatus.UpdateAvailable;
                OnLog?.Invoke($"  {mod.Name}: {mod.CurrentVersion} -> {release.Version} (有更新)");
            }
            else
            {
                mod.Status = UpdateStatus.UpToDate;
                OnLog?.Invoke($"  {mod.Name}: {mod.CurrentVersion} (已最新)");
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

            // 3-4. 解压（如为 zip）并替换文件
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

            // 替换旧文件
            OnLog?.Invoke("  正在替换文件...");
            var targetPath = mod.FilePath;

            // 如果旧文件是 _disabled 状态，新文件应该替换为启用状态
            if (targetPath.EndsWith("_disabled"))
            {
                targetPath = targetPath[..^"_disabled".Length];
            }

            // 确保目标目录存在
            var targetDir = Path.GetDirectoryName(targetPath);
            if (targetDir != null && !Directory.Exists(targetDir))
                Directory.CreateDirectory(targetDir);

            // 如果目标文件存在（正在使用），先尝试删除
            if (File.Exists(targetPath))
            {
                File.Copy(finalDllPath, targetPath, overwrite: true);
            }
            else
            {
                File.Copy(finalDllPath, targetPath);
            }

            // 如果旧文件是 _disabled 版本，删除它
            if (mod.FilePath != targetPath && File.Exists(mod.FilePath))
            {
                try { File.Delete(mod.FilePath); } catch { }
            }

            // 更新 Mod 信息
            mod.FilePath = targetPath;
            mod.FileName = Path.GetFileName(targetPath);
            mod.IsEnabled = true;
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

    /// <summary>
    /// 批量安装所有有更新的 Mod
    /// </summary>
    public async Task<(int success, int failed)> InstallAllUpdatesAsync(List<ModInfo> mods)
    {
        int success = 0, failed = 0;

        // 按 GUID/ModId 去重（防止同一 mod 多条目重复更新/重复开网页）
        var toUpdate = mods
            .Where(m => m.Status == UpdateStatus.UpdateAvailable)
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
