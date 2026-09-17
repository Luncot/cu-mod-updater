using System.Diagnostics;
using System.Drawing.Imaging;
using CUModUpdater.Models;
using CUModUpdater.Services;

namespace CUModUpdater.Forms;

/// <summary>
/// Mod 浏览器 - 浏览、预览、翻译、下载安装 Mod
/// 支持精选列表、GitHub 搜索、Nexus Mods 浏览
/// </summary>
public class ModBrowserForm : Form
{
    // === 服务 ===
    private readonly GitHubBrowser _github;
    private readonly GameBananaBrowser _gamebanana;
    private readonly NexusBrowser? _nexus;
    private readonly TranslationService _translator;
    private readonly ModInstaller _installer;
    private readonly List<ModInfo> _installedMods;

    // === 数据 ===
    private List<ModListing> _allMods = new();
    private ModListing? _selectedMod;
    private bool _showingTranslated = true;

    // === UI 控件 ===
    private readonly Button _tabCurated = new();
    private readonly Button _tabGitHub = new();
    private readonly Button _tabNexus = new();
    private readonly TextBox _searchBox = new();
    private readonly Button _searchBtn = new();
    private DataGridView _grid = null!;
    private readonly PictureBox _previewBox = new();
    private readonly Label _nameLabel = new();
    private readonly Label _metaLabel = new();
    private readonly Label _installStatusLabel = new();
    private readonly TextBox _descBox = new();
    private readonly Button _downloadBtn = new();
    private readonly Button _uninstallBtn = new();
    private readonly Button _openBrowserBtn = new();
    private readonly Button _translateBtn = new();
    private readonly SplitContainer _split = new();
    private ToolStripStatusLabel _statusLabel = null!;
    private ToolStripProgressBar _progressBar = null!;

    private string _activeSource = "Curated";

    // 列索引
    private const int ColName = 0;
    private const int ColAuthor = 1;
    private const int ColCat = 2;
    private const int ColVer = 3;
    private const int ColDl = 4;
    private const int ColSrc = 5;
    private const int ColStatus = 6;

    public ModBrowserForm(
        string gamePath,
        string? githubToken,
        string? nexusApiKey,
        string nexusDomain,
        List<ModInfo> installedMods,
        string? initialSource = null)
    {
        _github = new GitHubBrowser(githubToken);
        _gamebanana = new GameBananaBrowser();
        _nexus = !string.IsNullOrEmpty(nexusApiKey)
            ? new NexusBrowser(nexusApiKey, nexusDomain) : null;
        _translator = new TranslationService();
        _installer = new ModInstaller(gamePath, _nexus, githubToken);
        _installer.OnLog = Log;
        _installer.OnDownloadProgress = (r, t) =>
        {
            this.BeginInvoke(() =>
            {
                if (t > 0)
                {
                    _progressBar.Visible = true;
                    _progressBar.Value = (int)(r * 100 / t);
                    _statusLabel.Text = $"下载中: {r / 1024.0 / 1024.0:F1}MB / {t / 1024.0 / 1024.0:F1}MB";
                }
            });
        };
        _installedMods = installedMods;

        Text = "Mod 浏览器 - Casualties Unknown Demo";
        Size = new Size(1100, 750);
        MinimumSize = new Size(950, 600);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(30, 30, 30);
        ForeColor = Color.FromArgb(224, 224, 224);
        Font = new Font("Microsoft YaHei UI", 9F);
        AutoScaleMode = AutoScaleMode.Dpi;

        BuildUI();

        // 启动参数指定的标签页（--browser nexus 之类）
        var start = string.IsNullOrEmpty(initialSource) ? "Curated" : initialSource switch
        {
            "nexus" or "n网" or "n" => "Nexus",
            "github" or "gh" => "GitHub",
            _ => "Curated",
        };
        SelectTab(start);
        _ = LoadModsAsync(start);
    }

    // ==================== UI 构建 ====================

    private void BuildUI()
    {
        // 顶部栏 - 单一 FlowLayoutPanel 直接平铺窗体（Dock=Top + AutoSize 自测量，最可靠，不遮挡表格）
        var topFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Color.FromArgb(45, 45, 48),
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            Margin = new Padding(0),
            Padding = new Padding(10, 6, 10, 6),
        };

        _tabCurated.Text = "★ 精选";
        StyleTabButton(_tabCurated, true);
        _tabCurated.Click += (_, _) => SwitchTab("Curated");
        topFlow.Controls.Add(_tabCurated);

        _tabGitHub.Text = "GitHub";
        StyleTabButton(_tabGitHub, false);
        _tabGitHub.Click += (_, _) => SwitchTab("GitHub");
        topFlow.Controls.Add(_tabGitHub);

        _tabNexus.Text = "N网";
        StyleTabButton(_tabNexus, false);
        _tabNexus.Click += (_, _) => SwitchTab("Nexus");
        topFlow.Controls.Add(_tabNexus);

        var searchLabel = new Label
        {
            Text = "搜索:",
            AutoSize = true,
            ForeColor = Color.FromArgb(153, 153, 153),
            Margin = new Padding(12, 6, 4, 0),
        };
        topFlow.Controls.Add(searchLabel);

        _searchBox.Size = new Size(200, 25);
        StyleTextBox(_searchBox);
        _searchBox.Margin = new Padding(2, 3, 2, 3);
        _searchBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                _ = SearchAsync();
            }
        };
        topFlow.Controls.Add(_searchBox);

        _searchBtn.Text = "🔍 搜索";
        StyleButton(_searchBtn);
        _searchBtn.Click += (_, _) => _ = SearchAsync();
        topFlow.Controls.Add(_searchBtn);

        // 安装 / 卸载按钮（放在搜索栏右侧，选中 Mod 后可用）
        _downloadBtn.Text = "⬇ 下载并安装";
        StyleButton(_downloadBtn, Color.FromArgb(0, 122, 204));
        _downloadBtn.Click += async (_, _) => await DownloadAndInstallAsync();
        _downloadBtn.Enabled = false;
        topFlow.Controls.Add(_downloadBtn);

        _uninstallBtn.Text = "🗑 卸载";
        StyleButton(_uninstallBtn, Color.FromArgb(200, 60, 50));
        _uninstallBtn.Click += async (_, _) => await UninstallAsync();
        _uninstallBtn.Enabled = false;
        topFlow.Controls.Add(_uninstallBtn);

        // 主区域
        _split.Dock = DockStyle.Fill;
        _split.SplitterDistance = 480;
        _split.BackColor = Color.FromArgb(30, 30, 30);
        _split.Panel1.BackColor = Color.FromArgb(30, 30, 30);
        _split.Panel2.BackColor = Color.FromArgb(30, 30, 30);

        // 左侧: Mod 列表
        BuildGrid();
        _split.Panel1.Controls.Add(_grid);

        // 右侧: 详情面板
        BuildDetailPanel();
        _split.Panel2.Controls.Add(_detailPanel);

        Controls.Add(_split);

        // 状态栏
        var statusStrip = new StatusStrip
        {
            BackColor = Color.FromArgb(45, 45, 48),
            ForeColor = Color.FromArgb(153, 153, 153),
        };
        _statusLabel = new ToolStripStatusLabel("就绪") { Margin = new Padding(8, 0, 0, 0) };
        _progressBar = new ToolStripProgressBar { Width = 200, Visible = false };
        statusStrip.Items.Add(_statusLabel);
        statusStrip.Items.Add(new ToolStripSeparator());
        statusStrip.Items.Add(_progressBar);
        Controls.Add(statusStrip);

        // 顶部栏最后 Add → Dock 布局中先占顶部空间，表格不会被覆盖（与主窗口同规则）
        Controls.Add(topFlow);
    }

    private TableLayoutPanel _detailPanel = null!;
    private Label? _placeholder;

    /// <summary>在预览区显示居中的占位文字（"选择 Mod 查看预览"/"加载中..."/"无预览图"/"图片加载失败"）</summary>
    private void ShowPreviewPlaceholder(string text)
    {
        _previewBox.Controls.Clear();
        _placeholder = new Label
        {
            Text = text,
            AutoSize = true,
            ForeColor = Color.FromArgb(100, 100, 100),
            BackColor = Color.Transparent,
        };
        _previewBox.Controls.Add(_placeholder);
        CenterPreviewPlaceholder();
    }

    /// <summary>把占位文字居中（随预览区大小变化重新定位）</summary>
    private void CenterPreviewPlaceholder()
    {
        if (_placeholder == null) return;
        _placeholder.Location = new Point(
            Math.Max(0, (_previewBox.Width - _placeholder.Width) / 2),
            Math.Max(0, (_previewBox.Height - _placeholder.Height) / 2));
    }

    private void BuildDetailPanel()
    {
        // 使用 TableLayoutManager 自适应布局
        _detailPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 6,
            Padding = new Padding(10),
            BackColor = Color.FromArgb(30, 30, 30),
        };
        // 图片固定高度（Percent 与 AutoSize 行混合时 TableLayoutPanel 测量不可靠，会压缩/溢出）
        _detailPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _detailPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 260));  // 图片
        _detailPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));        // 名称
        _detailPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));        // 元数据
        _detailPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // 描述（占剩余）
        _detailPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));        // 按钮行（换行时自动撑高，按钮完整可见）
        _detailPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));   // 安装状态

        // 预览图片
        _previewBox.Dock = DockStyle.Fill;
        _previewBox.BackColor = Color.FromArgb(37, 37, 38);
        _previewBox.SizeMode = PictureBoxSizeMode.Zoom;
        _previewBox.BorderStyle = BorderStyle.FixedSingle;
        _previewBox.Margin = new Padding(0, 0, 0, 8);
        _previewBox.Resize += (_, _) => CenterPreviewPlaceholder();
        ShowPreviewPlaceholder("选择 Mod 查看预览");
        _detailPanel.Controls.Add(_previewBox, 0, 0);

        // Mod 名称
        _nameLabel.Text = "";
        _nameLabel.Dock = DockStyle.Fill;
        _nameLabel.AutoSize = false;
        _nameLabel.Height = 30;
        _nameLabel.Font = new Font("Microsoft YaHei UI", 13F, FontStyle.Bold);
        _nameLabel.ForeColor = Color.FromArgb(224, 224, 224);
        _nameLabel.Margin = new Padding(0, 4, 0, 0);
        _detailPanel.Controls.Add(_nameLabel, 0, 1);

        // 元数据
        _metaLabel.Text = "";
        _metaLabel.Dock = DockStyle.Fill;
        _metaLabel.AutoSize = false;
        _metaLabel.Height = 36;
        _metaLabel.ForeColor = Color.FromArgb(153, 153, 153);
        _detailPanel.Controls.Add(_metaLabel, 0, 2);

        // 安装状态（单独一行：已装版本信息挤在元数据里太难看）
        _installStatusLabel.Text = "";
        _installStatusLabel.Dock = DockStyle.Fill;
        _installStatusLabel.AutoSize = false;
        _installStatusLabel.Height = 22;
        _installStatusLabel.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
        _installStatusLabel.ForeColor = Color.FromArgb(150, 150, 150);
        _detailPanel.Controls.Add(_installStatusLabel, 0, 5);

        // 描述
        _descBox.Dock = DockStyle.Fill;
        _descBox.Multiline = true;
        _descBox.ReadOnly = true;
        _descBox.BackColor = Color.FromArgb(22, 22, 22);
        _descBox.ForeColor = Color.FromArgb(210, 220, 210);
        _descBox.Font = new Font("Microsoft YaHei UI", 9F);
        _descBox.ScrollBars = ScrollBars.Vertical;
        _descBox.BorderStyle = BorderStyle.FixedSingle;
        _descBox.Text = "选择左侧 Mod 查看详情";
        _descBox.Margin = new Padding(0, 4, 0, 4);
        _detailPanel.Controls.Add(_descBox, 0, 3);

        // 按钮行 - FlowLayoutPanel（允许换行，窄窗口下按钮也完整可见）
        var btnFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            Margin = new Padding(0, 4, 0, 0),
        };

        _translateBtn.Text = "译 翻译描述";
        StyleButton(_translateBtn);
        _translateBtn.Click += async (_, _) => await TranslateDescriptionAsync();
        _translateBtn.Enabled = false;
        btnFlow.Controls.Add(_translateBtn);

        _openBrowserBtn.Text = "🌐 打开网页";
        StyleButton(_openBrowserBtn);
        _openBrowserBtn.Click += (_, _) => OpenInBrowser();
        _openBrowserBtn.Enabled = false;
        btnFlow.Controls.Add(_openBrowserBtn);

        _detailPanel.Controls.Add(btnFlow, 0, 4);
    }

    private void BuildGrid()
    {
        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            BackgroundColor = Color.FromArgb(37, 37, 38),
            BorderStyle = BorderStyle.None,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            ReadOnly = true,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            EnableHeadersVisualStyles = false,
            GridColor = Color.FromArgb(63, 63, 70),
            RowTemplate = { Height = 28 },
        };
        _grid.RowHeadersVisible = false;
        _grid.DefaultCellStyle.BackColor = Color.FromArgb(37, 37, 38);
        _grid.DefaultCellStyle.ForeColor = Color.FromArgb(224, 224, 224);
        _grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(50, 50, 55);
        _grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(43, 43, 46);
        _grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(51, 51, 56);
        _grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(224, 224, 224);
        _grid.ColumnHeadersDefaultCellStyle.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);

        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "名称", HeaderText = "Mod 名称", FillWeight = 150 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "作者", HeaderText = "作者", FillWeight = 90, DefaultCellStyle = { ForeColor = Color.FromArgb(153, 153, 153) } });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "分类", HeaderText = "分类", FillWeight = 70 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "版本", HeaderText = "版本", FillWeight = 50 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "下载", HeaderText = "下载量", FillWeight = 50, DefaultCellStyle = { ForeColor = Color.FromArgb(153, 153, 153) } });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "来源", HeaderText = "来源", FillWeight = 40 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "状态", HeaderText = "状态", FillWeight = 50 });

        _grid.SelectionChanged += OnGridSelectionChanged;
        _grid.CellFormatting += OnGridCellFormatting;
    }

    // ==================== 数据加载 ====================

    private async Task LoadModsAsync(string source)
    {
        _statusLabel.Text = "正在加载 Mod 列表...";
        _progressBar.Visible = true;
        _progressBar.Style = ProgressBarStyle.Marquee;
        _grid.Rows.Clear();

        try
        {
            List<ModListing> mods;
            switch (source)
            {
                case "Curated":
                    _statusLabel.Text = "正在加载精选 Mod...";
                    mods = await LoadCuratedAsync();
                    break;
                case "GitHub":
                    _statusLabel.Text = "正在搜索 GitHub...";
                    // 合并多组查询 —— 单组查询覆盖不全（实测能匹配 176 个仓库，
                    // 而单查询只取到其中一部分）
                    mods = await _github.BrowseAsync();
                    if (mods.Count == 0)
                        Log("GitHub 没返回结果：可能是接口限额用尽（匿名 60 次/小时），或网络不可达 api.github.com");
                    break;
                case "Nexus":
                    if (_nexus == null || !_nexus.HasApiKey)
                    {
                        _statusLabel.Text = "未配置 Nexus API Key";
                        MessageBox.Show(
                            "浏览 N 网 Mod 需要配置 Nexus Mods API Key。\n\n" +
                            "获取方式:\n" +
                            "1. 登录 nexusmods.com\n" +
                            "2. 进入 Settings > API\n" +
                            "3. 生成 API Key\n" +
                            "4. 在本应用的设置中填入",
                            "需要 Nexus API Key",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                        _progressBar.Visible = false;
                        return;
                    }
                    _nexus.OnLog = Log;
                    _statusLabel.Text = "正在从 N 网加载...";

                    // 主列表 = 社区元数据（436 条全量清单，带作者/版本/图片/描述）。
                    // N 网 API 没有「列出全部 mod」的端点，三个榜单各只有十来条，
                    // 只用榜单会变成「N 网就 10 个 mod」——这是之前的毛病。
                    var catalog = Services.ModCatalog.FromMetadata(
                        Services.CasualtiesManageableService.GetAllEntries(),
                        _nexus.GameDomain);
                    Log($"社区元数据清单: {catalog.Count} 个 Mod");

                    // 榜单只用来补「刚发布、元数据还没收录」的新 mod
                    try
                    {
                        var latest = await _nexus.BrowseAsync(Services.NexusBrowser.NexusSort.LatestUpdated, 30);
                        var known = new HashSet<string>(catalog.Select(m => m.ModId), StringComparer.OrdinalIgnoreCase);
                        var added = latest.Where(m => !string.IsNullOrEmpty(m.ModId) && !known.Contains(m.ModId)).ToList();
                        if (added.Count > 0)
                        {
                            Log($"N 网榜单补充 {added.Count} 个新 Mod（元数据尚未收录）");
                            catalog.InsertRange(0, added);
                        }
                    }
                    catch (Exception ex) { Log($"N 网榜单加载失败（不影响主列表）: {ex.Message}"); }

                    mods = catalog;
                    break;
                default:
                    return;
            }

            // 标记已安装状态
            MarkInstalledMods(mods);

            _allMods = mods;
            PopulateGrid();
            _statusLabel.Text = $"加载完成: {mods.Count} 个 Mod ({mods.Count(m => m.IsInstalled)} 已安装)";
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "加载失败";
            MessageBox.Show($"加载 Mod 列表失败:\n{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Log($"加载失败: {ex.Message}");
        }
        finally
        {
            _progressBar.Visible = false;
        }
    }

    /// <summary>
    /// 精选列表 = GameBanana 实时数据优先 + 硬编码精选兜底。
    /// GameBanana 提供权威作者（Jimmy_king 等社区名，而非 N 网/GitHub 用户名）、版本、预览图、下载直链；
    /// 硬编码精选覆盖 GameBanana 没有的本地 mod（保留 GitHub release 下载能力）。
    /// </summary>
    private async Task<List<ModListing>> LoadCuratedAsync()
    {
        var result = new List<ModListing>();
        var gb = new List<ModListing>();
        try
        {
            gb = await _gamebanana.GetModsAsync();
            Log($"GameBanana 加载 {gb.Count} 个 Mod");
        }
        catch (Exception ex)
        {
            Log($"GameBanana 加载失败: {ex.Message}");
        }
        result.AddRange(gb);
        var gbNames = new HashSet<string>(
            gb.Select(m => NormalizeName(m.Name)).Where(n => n.Length > 0));

        try
        {
            var fallback = await _github.GetCuratedModsAsync();
            foreach (var m in fallback)
            {
                if (gbNames.Contains(NormalizeName(m.Name))) continue;
                result.Add(m);
            }
        }
        catch (Exception ex)
        {
            Log($"精选兜底加载失败: {ex.Message}");
        }

        EnrichFromMetadata(result);
        return result;
    }

    /// <summary>
    /// 用社区元数据补全列表项的空字段。
    ///
    /// 精选里的 26 个条目有 24 个**根本没有 GitHub 仓库**（Owner/Repo 为空），
    /// 于是没有作者、没有版本、没有配图、没有长描述——右侧面板一片空白。
    /// 但它们全都在 N 网、也全都在社区元数据里（436 条：作者 436/436、
    /// 图片 435/436、长描述 434/436）。按规范化名称查一次就能全部补齐。
    ///
    /// 只填空字段，不覆盖已有值（N 网/GB 的实时数据优先）。
    /// </summary>
    private static void EnrichFromMetadata(List<ModListing> mods)
    {
        var entries = Services.CasualtiesManageableService.GetAllEntries();
        if (entries.Count == 0) return;

        // 名称 → 元数据条目（规范化后建索引，避免逐条 O(n²) 扫）
        var byName = new Dictionary<string, Services.CasualtiesManageableService.CmModEntry>();
        foreach (var e in entries)
        {
            var k = NormalizeName(e.Name);
            if (k.Length > 0) byName.TryAdd(k, e);
        }

        foreach (var m in mods)
        {
            // GB / N 网条目如果只是把平台名当分类，按关键词给个可读分类
            if (m.Category is "GameBanana" or "GB" or "" or "N网")
                m.Category = Services.CategoryClassifier.ClassifyLoose(m.Name, m.Summary);

            if (!string.IsNullOrEmpty(m.Author) && !string.IsNullOrEmpty(m.Version) &&
                !string.IsNullOrEmpty(m.PreviewUrl) && m.Description.Length > 80)
                continue;   // 该有的都有，不必补

            var key = NormalizeName(m.Name);
            Services.CasualtiesManageableService.CmModEntry? e = null;
            if (key.Length > 0) byName.TryGetValue(key, out e);

            if (e == null && !string.IsNullOrEmpty(m.FileName))
                e = Services.CasualtiesManageableService.FindByDllNameCached(m.FileName);

            if (e == null) continue;

            if (string.IsNullOrEmpty(m.Author)) m.Author = e.Author;
            if (string.IsNullOrEmpty(m.Version)) m.Version = e.Version;
            if (string.IsNullOrEmpty(m.PreviewUrl))
            {
                var img = e.Images.FirstOrDefault(i => !string.IsNullOrWhiteSpace(i)) ?? "";
                m.PreviewUrl = img;
                m.ThumbnailUrl = img;
            }
            if (m.Description.Length <= 80)
            {
                var desc = Services.Markup.Strip(e.Description);
                if (desc.Length > m.Description.Length) m.Description = desc;
                if (string.IsNullOrEmpty(m.Summary)) m.Summary = Services.Markup.Strip(e.Summary);
            }
            if (string.IsNullOrEmpty(m.ModId) && !string.IsNullOrEmpty(e.NexusModId))
                m.ModId = e.NexusModId;
            if (string.IsNullOrEmpty(m.PageUrl) && !string.IsNullOrEmpty(e.NexusModId))
            {
                var domain = string.IsNullOrEmpty(e.NexusGameDomain) ? "scavprototype" : e.NexusGameDomain;
                m.PageUrl = $"https://www.nexusmods.com/{domain}/mods/{e.NexusModId}";
            }            if (string.IsNullOrEmpty(m.PluginGuid)) m.PluginGuid = e.BepInExPlugins.Keys.FirstOrDefault() ?? "";
            if (string.IsNullOrEmpty(m.FileName)) m.FileName = e.DllNames.FirstOrDefault() ?? "";
            if (m.DownloadCount == 0 && e.Statistics != null) m.DownloadCount = e.Statistics.TotalDownloads;
            if (m.EndorsementCount == 0 && e.Statistics != null) m.EndorsementCount = e.Statistics.Endorsements;
        }
    }

    /// <summary>
    /// 标记每个列表项的「已安装 / 可更新 / 未安装」。
    ///
    /// 匹配通道（按可靠性从高到低）：
    ///   1. **社区元数据反查**（最可靠）——N 网条目按 ModId 查
    ///      `casualties_manageable.json`，拿到它声明的全部 BepInPlugin GUID 和
    ///      dll 文件名，再跟本地 mod 比对。这是解决「很多 mod 匹配不上」的关键：
    ///      API / GitHub 列表本身几乎不带 GUID，光靠名字互相包含命中率很低。
    ///   2. GUID（列表里自带时）
    ///   3. dll 文件名
    ///   4. 规范化名称（互相包含）
    ///   5. 安装目录名（子目录装法的 mod，名字常来自文件夹）
    /// </summary>
    private void MarkInstalledMods(List<ModListing> mods)
    {
        Services.CasualtiesManageableService.EnsureLoadedSync();

        foreach (var mod in mods)
        {
            ModInfo? installed = null;
            var searched = new List<string>();   // 调试用：记录了哪些线索

            // ── 1. 元数据反查出的 GUID / dll 名 / 权威名称 ──
            var meta = ResolveMetaFor(mod);
            if (meta != null)
            {
                foreach (var g in meta.BepInExPlugins.Keys)
                {
                    installed = _installedMods.FirstOrDefault(m =>
                        !string.IsNullOrEmpty(m.PluginGuid) &&
                        m.PluginGuid.Equals(g, StringComparison.OrdinalIgnoreCase));
                    if (installed != null) break;
                }

                if (installed == null)
                    foreach (var dn in meta.DllNames)
                    {
                        var bare = Path.GetFileName(dn);
                        installed = _installedMods.FirstOrDefault(m =>
                            NormalizeFileName(m.FileName) == NormalizeFileName(bare));
                        if (installed != null) break;
                    }

                // 列表项本身没写 GUID 时用元数据的补上，后续「卸载」也能定位
                if (string.IsNullOrEmpty(mod.PluginGuid) && meta.BepInExPlugins.Count > 0)
                    mod.PluginGuid = meta.BepInExPlugins.Keys.First();
                if (string.IsNullOrEmpty(mod.FileName) && meta.DllNames.Count > 0)
                    mod.FileName = Path.GetFileName(meta.DllNames[0]);
            }

            // ── 2. 列表自带的 GUID ──
            installed ??= _installedMods.FirstOrDefault(m =>
                !string.IsNullOrEmpty(mod.PluginGuid) &&
                m.PluginGuid.Equals(mod.PluginGuid, StringComparison.OrdinalIgnoreCase));

            // ── 3. dll 文件名 ──
            installed ??= _installedMods.FirstOrDefault(m =>
                !string.IsNullOrEmpty(mod.FileName) &&
                NormalizeFileName(m.FileName) == NormalizeFileName(mod.FileName));

            // ── 4. 规范化名称（互相包含；GitHub 仓库名与本地 mod 名写法不同也能命中）──
            installed ??= _installedMods.FirstOrDefault(m =>
            {
                var a = NormalizeName(m.Name);
                var b = NormalizeName(mod.Name);
                return !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) &&
                       (a == b || a.Contains(b) || b.Contains(a));
            });

            // ── 5. 元数据权威名称（比列表名更接近本地名）──
            if (installed == null && meta != null && !string.IsNullOrEmpty(meta.Name))
            {
                var b = NormalizeName(meta.Name);
                installed = _installedMods.FirstOrDefault(m =>
                {
                    var a = NormalizeName(m.Name);
                    return !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) &&
                           (a == b || a.Contains(b) || b.Contains(a));
                });
            }

            // ── 6. 安装目录名（子目录安装时本地名字往往取自文件夹）──
            if (installed == null)
            {
                var b = NormalizeName(mod.Name);
                if (!string.IsNullOrEmpty(b))
                    installed = _installedMods.FirstOrDefault(m => DirectoryNameMatches(m, b));
            }

            if (installed != null)
            {
                mod.InstalledVersion = installed.CurrentVersion;
                mod.InstallStatus = !string.IsNullOrEmpty(mod.Version) &&
                    VersionHelper.IsNewer(mod.Version, installed.CurrentVersion)
                    ? "可更新" : "已安装";
            }
            else
            {
                mod.InstallStatus = "未安装";
            }

            // 元数据有版本而我们没有时补上（GitHub 列表常缺版本号）
            if (string.IsNullOrEmpty(mod.Version) && meta != null &&
                meta.BepInExPlugins.Count > 0)
                mod.Version = meta.BepInExPlugins.Values.First();
        }
    }

    /// <summary>按 listing 反查社区元数据条目（Nexus 用 ModId；其它尽力匹配名称）</summary>
    private static Services.CasualtiesManageableService.CmModEntry? ResolveMetaFor(ModListing mod)
    {
        if (mod.Source == "Nexus" && !string.IsNullOrEmpty(mod.ModId))
        {
            var e = Services.CasualtiesManageableService.FindByModId(mod.ModId);
            if (e != null) return e;
        }
        if (!string.IsNullOrEmpty(mod.PluginGuid))
            return Services.CasualtiesManageableService.FindByGuidRaw(mod.PluginGuid);
        return null;
    }

    /// <summary>安装目录名匹配：本地 mod 所在文件夹名与目标名规范化后相等</summary>
    private static bool DirectoryNameMatches(ModInfo m, string normalizedTarget)
    {
        try
        {
            var p = m.IsDataMod ? m.FlagFilePath : m.FilePath;
            if (string.IsNullOrEmpty(p)) return false;
            var dir = Path.GetDirectoryName(p);
            if (string.IsNullOrEmpty(dir)) return false;
            var dirName = NormalizeName(Path.GetFileName(dir));
            if (string.IsNullOrEmpty(dirName)) return false;
            return dirName == normalizedTarget ||
                   dirName.Contains(normalizedTarget) || normalizedTarget.Contains(dirName);
        }
        catch { return false; }
    }

    /// <summary>文件名规范化：去掉 _disabled 后缀后小写比较</summary>
    private static string NormalizeFileName(string? f)
    {
        if (string.IsNullOrEmpty(f)) return "";
        var n = Path.GetFileName(f);
        if (n.EndsWith("_disabled", StringComparison.OrdinalIgnoreCase))
            n = n[..^"_disabled".Length];
        return n.ToLowerInvariant();
    }

    /// <summary>
    /// 名称规范化：小写 + 只保留字母数字（去掉空格/连字符/下划线/点/冒号等）
    /// "QoL-Unknown" / "QoL Unknown" / "QoL_Unknown" → "qolunknown"
    /// </summary>
    private static string NormalizeName(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
        }
        return sb.ToString();
    }

    private async Task SearchAsync()
    {
        var query = _searchBox.Text.Trim();
        if (string.IsNullOrEmpty(query))
        {
            PopulateGrid();
            return;
        }

        _statusLabel.Text = $"搜索: {query}...";
        _progressBar.Visible = true;
        _progressBar.Style = ProgressBarStyle.Marquee;

        try
        {
            if (_activeSource == "Curated")
            {
                // 精选列表本地过滤
                PopulateGrid(_allMods.Where(m => MatchesQuery(m, query)).ToList());
            }
            else if (_activeSource == "GitHub")
            {
                // 先按用户原话搜；搜不到再补上游戏名重试
                // （用户可能输入中文、仓库名、作者名，硬拼游戏名反而搜不到东西）
                var results = await _github.SearchAsync(query, 2);
                if (results.Count == 0 && !Services.Markup.IsMostlyChinese(query))
                    results = await _github.SearchAsync($"{query} casualties unknown", 1);

                MarkInstalledMods(results);
                _allMods = results;
                PopulateGrid();
                _statusLabel.Text = results.Count > 0
                    ? $"搜索到 {results.Count} 个仓库"
                    : "没搜到结果（可能是 GitHub 限额用尽，等一分钟再试）";
            }
            else if (_activeSource == "Nexus" && _nexus != null)
            {
                // Nexus API 没有文本搜索，用本地过滤（支持中文）
                PopulateGrid(_allMods.Where(m => MatchesQuery(m, query)).ToList());
            }
            _statusLabel.Text = $"搜索完成";
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "搜索失败";
            Log($"搜索失败: {ex.Message}");
        }
        finally
        {
            _progressBar.Visible = false;
        }
    }

    // ==================== 表格填充 ====================

    private void PopulateGrid(List<ModListing>? filtered = null)
    {
        _grid.Rows.Clear();
        var list = filtered ?? _allMods;

        foreach (var mod in list)
        {
            _grid.Rows.Add(
                mod.Name,
                mod.Author,
                mod.Category,
                mod.Version,
                mod.DisplayDownloads,
                mod.SourceDisplay,
                mod.InstallStatus);
        }

        if (_grid.Rows.Count > 0)
            _grid.Rows[0].Selected = true;
    }

    // ==================== 表格事件 ====================

    private void OnGridSelectionChanged(object? sender, EventArgs e)
    {
        if (_grid.CurrentRow == null || _grid.CurrentRow.Index >= _allMods.Count)
        {
            // 过滤后索引可能不匹配
            var filtered = GetFilteredList();
            if (_grid.CurrentRow == null || _grid.CurrentRow.Index >= filtered.Count)
                return;
            _selectedMod = filtered[_grid.CurrentRow.Index];
        }
        else
        {
            _selectedMod = _allMods[_grid.CurrentRow.Index];
        }
        ShowDetail();
    }

    private List<ModListing> GetFilteredList()
    {
        // 如果有搜索文本，需要从过滤后的列表匹配
        var query = _searchBox.Text.Trim();
        if (string.IsNullOrEmpty(query))
            return _allMods;
        return _allMods.Where(m => MatchesQuery(m, query)).ToList();
    }

    /// <summary>
    /// 搜索匹配（**支持中文**）。
    ///
    /// 之前只匹配 Name/Summary/Category 三个英文字段，中文输入永远搜不到东西。
    /// 现在覆盖：原名、**中文译名**、作者、分类、摘要与描述的原文/译文、
    /// ModId、dll 文件名（找「这个 dll 是哪个 mod」很有用）。
    /// </summary>
    private static bool MatchesQuery(ModListing m, string query)
    {
        var q = query.Trim();
        if (q.Length == 0) return true;

        foreach (var field in new[]
        {
            m.Name, m.TranslatedName, m.Author, m.Category, m.Summary,
            m.TranslatedSummary, m.Description, m.TranslatedDescription,
            m.ModId, m.FileName, m.PluginGuid,
        })
        {
            if (!string.IsNullOrEmpty(field) &&
                field.Contains(q, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private void OnGridCellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0) return;
        var filtered = GetFilteredList();
        if (e.RowIndex >= filtered.Count) return;
        var mod = filtered[e.RowIndex];

        if (e.ColumnIndex == ColStatus)
        {
            e.CellStyle!.ForeColor = mod.InstallStatus switch
            {
                "已安装" => Color.FromArgb(76, 175, 80),
                "可更新" => Color.FromArgb(255, 152, 0),
                _ => Color.FromArgb(153, 153, 153),
            };
        }
        else if (e.ColumnIndex == ColName)
        {
            e.CellStyle!.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
        }
    }

    // ==================== 详情显示 ====================

    private void ShowDetail()
    {
        if (_selectedMod == null) return;
        var mod = _selectedMod;

        // 名称：有译名时「原名（译名）」，找中文 mod 一眼能看到
        _nameLabel.Text = !string.IsNullOrEmpty(mod.TranslatedName) &&
                          !mod.TranslatedName.Equals(mod.Name, StringComparison.OrdinalIgnoreCase)
            ? $"{mod.Name}（{mod.TranslatedName}）"
            : mod.Name;

        // 元数据：把能显示的字段都显示出来（之前只有寥寥几项）
        var metaParts = new List<string>();
        if (!string.IsNullOrEmpty(mod.Author)) metaParts.Add($"作者: {mod.Author}");
        if (!string.IsNullOrEmpty(mod.Version)) metaParts.Add($"版本: {mod.Version}");
        if (!string.IsNullOrEmpty(mod.Category)) metaParts.Add($"分类: {mod.Category}");
        if (mod.DownloadCount > 0) metaParts.Add($"下载: {mod.DisplayDownloads}");
        if (mod.EndorsementCount > 0) metaParts.Add($"好评: {mod.DisplayEndorsements}");
        if (mod.PublishedAt != default) metaParts.Add($"更新: {mod.DisplayDate}");
        if (!string.IsNullOrEmpty(mod.SourceDisplay)) metaParts.Add($"来源: {mod.SourceDisplay}");
        if (!string.IsNullOrEmpty(mod.ModId)) metaParts.Add($"ID: {mod.ModId}");
        _metaLabel.Text = string.Join("  |  ", metaParts);

        // 已安装状态单独一行（含已装版本），比挤在元数据里更容易看
        _installStatusLabel.Text = mod.IsInstalled
            ? $"● {mod.InstallStatus}" + (string.IsNullOrEmpty(mod.InstalledVersion)
                ? "" : $"（已装 {mod.InstalledVersion}）")
            : "○ 未安装";
        _installStatusLabel.ForeColor = mod.InstallStatus switch
        {
            "已安装" => Color.FromArgb(76, 175, 80),
            "可更新" => Color.FromArgb(255, 152, 0),
            _ => Color.FromArgb(150, 150, 150),
        };

        // 描述：**绝不能因为「当前显示翻译」而显示空白**——
        // 之前 _showingTranslated 默认偏向译文，而译文为空时就显示「暂无描述」，
        // 用户必须先点「显示原文」才看得到简介。
        var desc = _showingTranslated && !string.IsNullOrEmpty(mod.TranslatedDescription)
            ? mod.TranslatedDescription
            : mod.Description;
        if (string.IsNullOrEmpty(desc)) desc = mod.Summary;
        if (string.IsNullOrEmpty(desc) && !string.IsNullOrEmpty(mod.TranslatedSummary))
            desc = mod.TranslatedSummary;

        _descBox.Text = !string.IsNullOrEmpty(desc)
            ? Services.Markup.Strip(desc)          // 兜底再洗一次（N 网原文是 BBCode）
            : "（该 Mod 没有提供描述）";

        // 翻译按钮：已经是中文的内容没什么可译的，直接禁用
        var translatable = !string.IsNullOrEmpty(mod.Description) &&
                           !Services.Markup.IsMostlyChinese(mod.Description);
        _translateBtn.Enabled = translatable &&
            (string.IsNullOrEmpty(mod.TranslatedDescription) || string.IsNullOrEmpty(mod.TranslatedName));
        _translateBtn.Text = _showingTranslated ? "原文 显示原文" : "译 显示翻译";

        // 安装按钮（版本处理规则）：
        //   已安装（同版本）→ 禁用显示 "✓ 已安装"
        //   可更新（同名不同版本）→ "⬆ 更新"，点按备份旧版后覆盖
        //   未安装 → "⬇ 下载并安装"
        var canDownload = !string.IsNullOrEmpty(mod.DownloadUrl) || mod.Source == "Nexus";
        _downloadBtn.Enabled = canDownload && mod.InstallStatus != "已安装";
        _downloadBtn.Text = mod.InstallStatus switch
        {
            "已安装" => "✓ 已安装",
            "可更新" => "⬆ 更新",
            _ => canDownload ? "⬇ 下载并安装" : "⬇ 需手动下载"
        };

        // 卸载按钮：仅已安装（含可更新）时可卸载
        _uninstallBtn.Enabled = mod.InstallStatus == "已安装" || mod.InstallStatus == "可更新";

        // 打开浏览器按钮
        _openBrowserBtn.Enabled = !string.IsNullOrEmpty(mod.PageUrl);

        // 加载预览图
        _ = LoadPreviewImageAsync(mod);

        // GitHub 条目：搜索接口不给 Release 信息，选中时补查一次
        // （补到版本和下载直链，「需手动下载」就变成可点的「下载并安装」）
        if (mod.Source == "GitHub" && !mod.ReleaseChecked &&
            string.IsNullOrEmpty(mod.DownloadUrl) &&
            !string.IsNullOrEmpty(mod.Owner) && !string.IsNullOrEmpty(mod.Repo))
            _ = ResolveReleaseAsync(mod);

        // GitHub 来源且描述缺失时，异步拉 README 补简介
        if (mod.Source == "GitHub" && !string.IsNullOrEmpty(mod.Owner) && !string.IsNullOrEmpty(mod.Repo))
            _ = LoadReadmeAsync(mod);
    }

    /// <summary>
    /// 选中 GitHub 条目时补查 Release：拿到版本与下载直链后，
    /// 就地刷新这一行 + 详情面板，「需手动下载」升级成「下载并安装」。
    /// </summary>
    private async Task ResolveReleaseAsync(ModListing mod)
    {
        if (_releaseBusy) return;
        _releaseBusy = true;
        try
        {
            var ok = await _github.EnrichReleaseAsync(mod);
            if (_selectedMod != mod) return;      // 用户已经切走了

            if (ok)
            {
                Log($"✓ {mod.Name}: 找到 Release v{mod.Version}，可直接下载");
                PopulateGrid();                   // 版本列要重画
                var idx = GetFilteredList().IndexOf(mod);
                if (idx >= 0) _grid.Rows[idx].Selected = true;
                ShowDetail();
            }
            else
            {
                // ⚠️ 写日志而不是改状态栏：状态栏那行是「加载完成: N 个 Mod」的计数，
                // 被这种一次性提示顶掉之后用户就再也看不到列表总数了
                Log($"{mod.Name}: 该仓库没有 Release（只能手动下载或自行编译）");
            }
        }
        finally
        {
            _releaseBusy = false;
        }
    }

    private bool _releaseBusy;

    /// <summary>
    /// 异步拉取 GitHub 仓库 README 作为 Mod 简介（描述为空或太短时生效）。
    ///
    /// 两条通道：
    ///   ① API `/repos/x/y/readme`（返回 base64）—— 吃 60 次/小时匿名限额；
    ///   ② 限额打满 / 仓库无 README 时，退回 raw.githubusercontent 抓
    ///      README.md（**不吃 API 限额**）。GitHub 上大量 mod 根本没写
    ///      description，这条兜底决定了预览面板是空的还是有内容。
    /// </summary>
    private async Task LoadReadmeAsync(ModListing mod)
    {
        if (!string.IsNullOrEmpty(mod.Description) && mod.Description.Length > 80) return;

        var readme = await _github.GetReadmeTextAsync(mod.Owner, mod.Repo);
        if (string.IsNullOrWhiteSpace(readme))
            readme = await _github.FetchReadmeSummaryAsync(mod.Owner, mod.Repo);

        if (string.IsNullOrWhiteSpace(readme))
        {
            if (_selectedMod == mod)
            {
                // 说清原因，别让用户以为是这个 mod 的问题
                var why = _github.LastCallRateLimited
                    ? "GitHub 匿名限额 60 次/小时已用尽（在设置里填 GitHub Token 可提升到 5000 次/小时）"
                    : _github.LastRawUnreachable
                        ? "raw.githubusercontent.com 在当前网络下不可达（它常被墙，需要代理），而 API 也没有可读的 README"
                        : "该仓库确实没有 README";
                _descBox.Text = $"（获取不到简介：{why}）";
            }
            return;
        }

        // 用户可能已切到别的 Mod（防竞态：只更新还在选中的那个）
        if (_selectedMod != mod) return;

        mod.Description = readme;
        // 描述框要跟着刷新（无论当前是不是「显示翻译」状态）
        _descBox.Text = _showingTranslated && !string.IsNullOrEmpty(mod.TranslatedDescription)
            ? mod.TranslatedDescription
            : readme;
        _translateBtn.Enabled = !string.IsNullOrEmpty(mod.Description) &&
            string.IsNullOrEmpty(mod.TranslatedDescription);
    }

    private async Task LoadPreviewImageAsync(ModListing mod)
    {
        // 清除旧图
        if (_previewBox.Image != null)
        {
            _previewBox.Image.Dispose();
            _previewBox.Image = null;
        }
        ShowPreviewPlaceholder("加载中...");

        var url = !string.IsNullOrEmpty(mod.PreviewUrl) ? mod.PreviewUrl : mod.ThumbnailUrl;
        if (string.IsNullOrEmpty(url))
        {
            _placeholder!.Text = "无预览图";
            CenterPreviewPlaceholder();
            return;
        }

        try
        {
            using var http = new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(15);
            // 各平台 CDN 都会拒绝无 UserAgent 的请求，必须带上
            http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) CU-ModUpdater/1.1.0");
            // ⚠️ Referer 要跟着图源走：
            //   · GameBanana 的 CDN 校验 Referer，必须给 gamebanana.com
            //   · Nexus 的 CDN（staticdelivery.nexusmods.com）**会因为
            //     外来 Referer 直接拒图**（表现为「图片加载失败」），所以要给自己的
            //   · GitHub 的 OG 预览图不校验，随便给都行
            var host = new Uri(url).Host;
            if (host.Contains("gamebanana", StringComparison.OrdinalIgnoreCase))
                http.DefaultRequestHeaders.Referrer = new Uri("https://gamebanana.com/");
            else if (host.Contains("nexusmods", StringComparison.OrdinalIgnoreCase))
                http.DefaultRequestHeaders.Referrer = new Uri("https://www.nexusmods.com/");
            var bytes = await http.GetByteArrayAsync(url);
            var img = DecodeImage(bytes);
            if (img == null)
            {
                ShowPreviewPlaceholder("不支持的图片格式");
                return;
            }
            _previewBox.Image = img;
            _previewBox.Controls.Clear();
            _placeholder = null;
        }
        catch
        {
            ShowPreviewPlaceholder("图片加载失败");
        }
    }

    /// <summary>
    /// 解码图片字节。先用 GDI+（PNG/JPEG/GIF/BMP 都行），
    /// 失败的再用 WPF 的 WIC 解码器兜底 —— **主要是为了 WebP**：
    /// N 网的图片 CDN 不管 Accept 头怎么给都返回 WebP，
    /// 而 GDI+ 完全不认 WebP（Image.FromStream 直接抛异常）。
    /// </summary>
    private static Image? DecodeImage(byte[] bytes)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
            return Image.FromStream(ms);
        }
        catch
        {
            try
            {
                using var ms = new MemoryStream(bytes);
                var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(
                    ms,
                    System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
                    System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(decoder.Frames[0]));
                using var outMs = new MemoryStream();
                encoder.Save(outMs);
                outMs.Position = 0;
                return Image.FromStream(outMs);   // 转成 PNG 后再交给 GDI+ 显示
            }
            catch
            {
                return null;
            }
        }
    }

    // ==================== 翻译 ====================

    private async Task TranslateDescriptionAsync()
    {
        if (_selectedMod == null || string.IsNullOrEmpty(_selectedMod.Description))
            return;

        _translateBtn.Enabled = false;
        _translateBtn.Text = "翻译中...";
        _statusLabel.Text = "正在翻译（名称/摘要/描述）...";
        var mod = _selectedMod;

        try
        {
            // 富文本先洗一遍再翻译：BBCode 标签会被翻译接口当成正文，
            // 既拖慢速度又把译文弄脏（用户看到译文里混着 [size=4][b]）。
            var cleanDesc = Services.Markup.Strip(mod.Description);

            // 名称 + 摘要 + 描述**并行**翻译。
            // 之前是串行 await（描述 → 摘要 → 摘要二次），一次翻译要等三趟往返；
            // 描述本身还按 1500 字切块串行，长文要等好几秒。
            var nameTask = Services.Markup.IsMostlyChinese(mod.Name)
                ? Task.FromResult("")
                : _translator.TranslateAsync(mod.Name);
            var summaryTask = Services.Markup.IsMostlyChinese(mod.Summary)
                ? Task.FromResult("")
                : _translator.TranslateAsync(mod.Summary);
            var descTask = _translator.TranslateAsync(cleanDesc);

            await Task.WhenAll(nameTask, summaryTask, descTask);

            mod.TranslatedName = nameTask.Result;
            mod.TranslatedSummary = !string.IsNullOrEmpty(summaryTask.Result)
                ? summaryTask.Result : mod.TranslatedSummary;
            mod.TranslatedDescription = descTask.Result;

            // 译完立刻把界面切到译文（用户点了翻译就是想看中文）
            _showingTranslated = true;
            _descBox.Text = mod.TranslatedDescription;
            _nameLabel.Text = !string.IsNullOrEmpty(mod.TranslatedName)
                ? $"{mod.Name}（{mod.TranslatedName}）" : mod.Name;

            _translateBtn.Text = "原文 显示原文";
            _translateBtn.Enabled = true;
            var charCount = mod.TranslatedDescription.Length;
            _statusLabel.Text = $"翻译完成（{charCount} 字）";
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "翻译失败";
            Log($"翻译失败: {ex.Message}");
            _translateBtn.Text = "译 显示翻译";
            _translateBtn.Enabled = true;
        }
    }

    // ==================== 下载安装 ====================

    private async Task DownloadAndInstallAsync()
    {
        if (_selectedMod == null) return;

        var mod = _selectedMod;
        var confirm = MessageBox.Show(
            $"即将下载并安装:\n\n  {mod.Name}\n  版本: {mod.Version}\n  来源: {mod.SourceDisplay}\n\n" +
            (mod.InstallStatus == "可更新" ? "将替换当前已安装版本。" : "将安装到 BepInEx/plugins/ 目录。"),
            "确认安装", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

        if (confirm != DialogResult.Yes) return;

        _downloadBtn.Enabled = false;
        _statusLabel.Text = $"正在安装 {mod.Name}...";
        _progressBar.Visible = true;
        _progressBar.Style = ProgressBarStyle.Continuous;
        _progressBar.Value = 0;

        Log($"--- 安装 {mod.Name} ---");

        var success = await _installer.InstallAsync(mod);

        _progressBar.Visible = false;

        if (success)
        {
            _statusLabel.Text = $"{mod.Name} 安装成功";
            ShowDetail(); // 刷新按钮状态
            MessageBox.Show($"{mod.Name} 安装成功！\n请重启游戏使 Mod 生效。",
                "安装成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        else
        {
            _statusLabel.Text = "安装失败";
            _downloadBtn.Enabled = true;
        }
    }

    // ==================== 卸载 ====================

    /// <summary>
    /// 卸载已安装的 Mod：确认 → 备份到 .backup → 删除文件 → 刷新状态
    /// </summary>
    private async Task UninstallAsync()
    {
        if (_selectedMod == null) return;
        var mod = _selectedMod;

        // 在已安装列表中找到对应文件（GUID 优先，其次规范化名称）
        var installed = _installedMods.FirstOrDefault(m =>
            !string.IsNullOrEmpty(mod.PluginGuid) &&
            m.PluginGuid.Equals(mod.PluginGuid, StringComparison.OrdinalIgnoreCase));
        installed ??= _installedMods.FirstOrDefault(m =>
            NormalizeName(m.Name) == NormalizeName(mod.Name));

        if (installed == null)
        {
            MessageBox.Show($"未找到 {mod.Name} 的已安装文件。\n\n如果 Mod 以子目录形式安装，请手动删除。",
                "未找到", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var confirm = MessageBox.Show(
            $"确定卸载 {mod.Name}？\n\n将删除文件: {installed.FileName}\n" +
            "旧文件会备份到 BepInEx/plugins/.backup/ 目录，可从主窗口回滚。",
            "确认卸载", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

        if (confirm != DialogResult.Yes) return;

        _uninstallBtn.Enabled = false;
        _statusLabel.Text = $"正在卸载 {mod.Name}...";

        var ok = _installer.Uninstall(installed.FilePath);
        if (ok)
        {
            _installedMods.Remove(installed);
            mod.InstallStatus = "未安装";
            _statusLabel.Text = $"{mod.Name} 已卸载";
            ShowDetail();   // 刷新按钮状态（安装可用、卸载禁用）
            MessageBox.Show($"{mod.Name} 已卸载。\n\n如需恢复可在主窗口「从备份回滚」。",
                "卸载完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        else
        {
            _statusLabel.Text = "卸载失败";
            MessageBox.Show("卸载失败，请查看日志。", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ==================== 其他 ====================

    private void OpenInBrowser()
    {
        if (_selectedMod == null || string.IsNullOrEmpty(_selectedMod.PageUrl)) return;
        Process.Start(new ProcessStartInfo(_selectedMod.PageUrl) { UseShellExecute = true });
    }

    /// <summary>只切换标签页外观与当前来源（不触发加载，供启动参数用）</summary>
    private void SelectTab(string source)
    {
        _activeSource = source;
        StyleTabButton(_tabCurated, source == "Curated");
        StyleTabButton(_tabGitHub, source == "GitHub");
        StyleTabButton(_tabNexus, source == "Nexus");
    }

    private void SwitchTab(string source)
    {
        SelectTab(source);
        _ = LoadModsAsync(source);
    }

    // ==================== 样式 ====================

    private static void StyleTabButton(Button btn, bool active)
    {
        btn.FlatStyle = FlatStyle.Flat;
        btn.FlatAppearance.BorderSize = 0;
        btn.BackColor = active ? Color.FromArgb(0, 122, 204) : Color.FromArgb(60, 60, 65);
        btn.ForeColor = Color.FromArgb(224, 224, 224);
        btn.AutoSize = true;
        btn.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        btn.Padding = new Padding(14, 6, 14, 6);
        btn.Margin = new Padding(3, 3, 3, 3);
        btn.Font = new Font("Microsoft YaHei UI", 9F, active ? FontStyle.Bold : FontStyle.Regular);
        btn.Cursor = Cursors.Hand;
    }

    private static void StyleButton(Button btn, Color? accent = null)
    {
        btn.FlatStyle = FlatStyle.Flat;
        btn.FlatAppearance.BorderColor = Color.FromArgb(63, 63, 70);
        btn.BackColor = accent ?? Color.FromArgb(60, 60, 65);
        btn.ForeColor = Color.FromArgb(224, 224, 224);
        btn.AutoSize = true;
        btn.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        btn.Padding = new Padding(14, 6, 14, 6);
        btn.Margin = new Padding(3, 3, 3, 3);
        btn.Cursor = Cursors.Hand;
        btn.Font = new Font("Microsoft YaHei UI", 9F);
    }

    private static void StyleTextBox(TextBox tb)
    {
        tb.BackColor = Color.FromArgb(51, 51, 56);
        tb.ForeColor = Color.FromArgb(224, 224, 224);
        tb.BorderStyle = BorderStyle.FixedSingle;
    }

    private void Log(string msg)
    {
        // 可以扩展到外部日志
        System.Diagnostics.Debug.WriteLine($"[ModBrowser] {msg}");
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _github.Dispose();
        _gamebanana.Dispose();
        _nexus?.Dispose();
        _translator.Dispose();
        _installer.Dispose();
        if (_previewBox.Image != null)
            _previewBox.Image.Dispose();
        base.OnFormClosed(e);
    }
}
