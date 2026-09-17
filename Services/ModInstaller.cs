using System.Net.Http;
using System.IO.Compression;
using CUModUpdater.Models;

namespace CUModUpdater.Services;

/// <summary>
/// Mod 安装器 - 下载并安装新 Mod 到 BepInEx/plugins 目录
/// 支持 .dll 直装和 .zip 解压安装
/// </summary>
public class ModInstaller : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _pluginsPath;
    private readonly NexusBrowser? _nexusBrowser;

    public Action<string>? OnLog { get; set; }
    public Action<long, long>? OnDownloadProgress { get; set; }

    public ModInstaller(string gamePath, NexusBrowser? nexusBrowser = null, string? githubToken = null)
    {
        _pluginsPath = Path.Combine(gamePath, "BepInEx", "plugins");
        _nexusBrowser = nexusBrowser;

        _httpClient = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
        });
        _httpClient.Timeout = TimeSpan.FromMinutes(5);
        // UA 版本号与 GameBananaBrowser 保持一致（GameBanana CDN 会校验 UA）
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("CU-ModUpdater/1.1.0 (+https://github.com)");
        // GameBanana 下载链接（gamebanana.com/dl/xxx）302 重定向后可能校验 Referer
        _httpClient.DefaultRequestHeaders.Referrer = new Uri("https://gamebanana.com/");
    }

    /// <summary>
    /// 安装一个 Mod
    /// </summary>
    public async Task<bool> InstallAsync(ModListing mod)
    {
        var downloadUrl = mod.DownloadUrl;

        // Nexus 来源需要先获取下载链接
        if (mod.Source == "Nexus" && _nexusBrowser != null && string.IsNullOrEmpty(downloadUrl))
        {
            OnLog?.Invoke("正在获取 Nexus 下载链接...");
            downloadUrl = await _nexusBrowser.GetDownloadUrlAsync(mod.ModId);
        }

        if (string.IsNullOrEmpty(downloadUrl))
        {
            OnLog?.Invoke($"错误: {mod.Name} 没有可用的下载链接");
            return false;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "CU-ModInstall-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            // 1. 下载
            var downloadExt = Path.GetExtension(downloadUrl);
            if (string.IsNullOrEmpty(downloadExt))
                downloadExt = ".zip";

            var tempPath = Path.Combine(tempDir, "download" + downloadExt);
            OnLog?.Invoke($"正在下载 {mod.Name}...");

            await DownloadFileAsync(downloadUrl, tempPath);

            // 2. 安装
            if (downloadExt.Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                OnLog?.Invoke("解压并安装...");
                var extractDir = Path.Combine(tempDir, "extracted");
                ZipFile.ExtractToDirectory(tempPath, extractDir);

                // 查找所有 .dll 文件
                var dlls = Directory.GetFiles(extractDir, "*.dll", SearchOption.AllDirectories)
                    .Where(f => !Path.GetFileName(f).StartsWith("0Harmony") &&
                                !Path.GetFileName(f).StartsWith("BepInEx") &&
                                !Path.GetFileName(f).StartsWith("UnityEngine") &&
                                !Path.GetFileName(f).StartsWith("Mono."))
                    .ToList();

                if (dlls.Count == 0)
                {
                    // 没找到 .dll，可能是纯资源 Mod，整个解压目录安装
                    InstallFolder(extractDir, mod.Name);
                }
                else
                {
                    // 安装找到的 .dll 文件
                    foreach (var dll in dlls)
                    {
                        var destPath = Path.Combine(_pluginsPath, Path.GetFileName(dll));
                        Directory.CreateDirectory(_pluginsPath);
                        BackupIfExists(destPath);
                        File.Copy(dll, destPath, overwrite: true);
                        OnLog?.Invoke($"  已安装: {Path.GetFileName(dll)}");
                    }

                    // 安装随附的非 DLL 文件（如配置、资源等）
                    var otherFiles = Directory.GetFiles(extractDir, "*", SearchOption.AllDirectories)
                        .Where(f => !f.EndsWith(".dll") &&
                                    !f.EndsWith(".exe") &&
                                    !f.EndsWith(".bat") &&
                                    !Path.GetFileName(f).Equals("README", StringComparison.OrdinalIgnoreCase) &&
                                    !Path.GetFileName(f).StartsWith("LICENSE", StringComparison.OrdinalIgnoreCase) &&
                                    !Path.GetFileName(f).EndsWith(".md") &&
                                    !Path.GetFileName(f).EndsWith(".txt"))
                        .ToList();

                    // 如果有非 DLL 文件且 DLL 在子目录中，安装整个子目录
                    if (otherFiles.Count > 0 && dlls.Count > 0)
                    {
                        var dllDir = Path.GetDirectoryName(dlls[0]) ?? "";
                        if (dllDir != extractDir)
                        {
                            // DLL 在子目录中，安装整个子目录
                            var subDirName = new DirectoryInfo(dllDir).Name;
                            var destDir = Path.Combine(_pluginsPath, subDirName);
                            CopyDirectory(dllDir, destDir);
                            OnLog?.Invoke($"  已安装目录: {subDirName}/");
                        }
                    }
                }
            }
            else if (downloadExt.Equals(".dll", StringComparison.OrdinalIgnoreCase))
            {
                // 直接是 DLL 文件
                Directory.CreateDirectory(_pluginsPath);
                var destPath = Path.Combine(_pluginsPath, Path.GetFileName(tempPath));
                if (destPath == tempPath)
                    destPath = Path.Combine(_pluginsPath, mod.Name + ".dll");
                BackupIfExists(destPath);
                File.Copy(tempPath, destPath, overwrite: true);
                OnLog?.Invoke($"  已安装: {Path.GetFileName(destPath)}");
            }
            else
            {
                // 其他文件类型，直接复制到 plugins
                Directory.CreateDirectory(_pluginsPath);
                var destPath2 = Path.Combine(_pluginsPath, Path.GetFileName(tempPath));
                BackupIfExists(destPath2);
                File.Copy(tempPath, destPath2, overwrite: true);
                OnLog?.Invoke($"  已安装: {Path.GetFileName(tempPath)}");
            }

            mod.InstallStatus = "已安装";
            OnLog?.Invoke($"✓ {mod.Name} 安装成功！");
            return true;
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"✗ 安装失败: {ex.Message}");
            return false;
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }
    }

    /// <summary>
    /// 覆盖前备份旧文件到 .backup（同名不同版本更新时，旧版可回滚）
    /// </summary>
    private void BackupIfExists(string destPath)
    {
        try
        {
            if (!File.Exists(destPath)) return;
            var backupDir = Path.Combine(_pluginsPath, ".backup");
            Directory.CreateDirectory(backupDir);
            var backupName = $"{Path.GetFileNameWithoutExtension(destPath)}_prev_{DateTime.Now:yyyyMMdd_HHmmss}{Path.GetExtension(destPath)}";
            File.Copy(destPath, Path.Combine(backupDir, backupName), overwrite: true);
            OnLog?.Invoke($"  已备份旧版本: {backupName}");
        }
        catch { }
    }

    /// <summary>
    /// 卸载 Mod（删除文件）
    /// </summary>
    public bool Uninstall(string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) return false;

            // 先备份
            var backupDir = Path.Combine(_pluginsPath, ".backup");
            if (!Directory.Exists(backupDir))
                Directory.CreateDirectory(backupDir);

            var backupName = $"{Path.GetFileNameWithoutExtension(filePath)}_uninstalled_{DateTime.Now:yyyyMMdd_HHmmss}{Path.GetExtension(filePath)}";
            File.Copy(filePath, Path.Combine(backupDir, backupName), overwrite: true);

            // 删除文件
            File.Delete(filePath);
            OnLog?.Invoke($"已卸载并备份: {Path.GetFileName(filePath)}");
            return true;
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"卸载失败: {ex.Message}");
            return false;
        }
    }

    private async Task DownloadFileAsync(string url, string destPath)
    {
        using var resp = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();

        var totalBytes = resp.Content.Headers.ContentLength ?? -1;
        using var contentStream = await resp.Content.ReadAsStreamAsync();
        using var fileStream = new FileStream(destPath, FileMode.Create, FileAccess.Write,
            FileShare.None, 81920, useAsync: true);

        var buffer = new byte[81920];
        long received = 0;
        int bytesRead;

        while ((bytesRead = await contentStream.ReadAsync(buffer)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));
            received += bytesRead;
            OnDownloadProgress?.Invoke(received, totalBytes);
        }
    }

    private void InstallFolder(string sourceDir, string modName)
    {
        var destDir = Path.Combine(_pluginsPath, modName);
        if (Directory.Exists(destDir))
            Directory.Delete(destDir, true);
        CopyDirectory(sourceDir, destDir);
        OnLog?.Invoke($"  已安装目录: {modName}/");
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(sourceDir, file);
            var dest = Path.Combine(destDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
    }

    public void Dispose() => _httpClient.Dispose();
}
