using System.ComponentModel;
using System.Diagnostics;
using CUModUpdater.Models;
using CUModUpdater.Services;

namespace CUModUpdater.Forms;

/// <summary>
/// 深色主题颜色定义
/// </summary>
internal static class Theme
{
    public static readonly Color Bg = Color.FromArgb(30, 30, 30);
    public static readonly Color PanelBg = Color.FromArgb(45, 45, 48);
    public static readonly Color GridBg = Color.FromArgb(37, 37, 38);
    public static readonly Color GridAlt = Color.FromArgb(43, 43, 46);
    public static readonly Color GridHead = Color.FromArgb(51, 51, 56);
    public static readonly Color Text = Color.FromArgb(224, 224, 224);
    public static readonly Color TextDim = Color.FromArgb(153, 153, 153);
    public static readonly Color Accent = Color.FromArgb(0, 122, 204);
    public static readonly Color AccentH = Color.FromArgb(28, 120, 180);
    public static readonly Color Success = Color.FromArgb(76, 175, 80);
    public static readonly Color Warning = Color.FromArgb(255, 152, 0);
    public static readonly Color Error = Color.FromArgb(244, 67, 54);
    public static readonly Color Border = Color.FromArgb(63, 63, 70);
    public static readonly Color LogBg = Color.FromArgb(22, 22, 22);
}

/// <summary>
/// 主窗口 - Mod 列表、版本检查、一键更新
/// </summary>
public class MainForm : Form
{
    // === 数据 ===
    private ModUpdaterConfig _config = null!;
    private List<ModInfo> _mods = new();

    /// <summary>当前网格可见的条目（分组后：组长 + 已展开的成员）</summary>
    private List<ModInfo> _visible = new();

    /// <summary>已展开的组（键为 GroupKey）；折叠时不入网格</summary>
    private readonly HashSet<string> _expandedGroups = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>分组结果缓存（_modsVersion 变化时失效）</summary>
    private List<ModInfo>? _groupCache;
    private int _groupCacheVersion = -1;

    /// <summary>_mods 内容版本号，每次扫描/结构变化时自增</summary>
    private int _modsVersion;

    /// <summary>名称/GUID 筛选关键字（空 = 不筛选）</summary>
    private string _searchText = "";

    private PluginScanner? _scanner;
    private UpdateManager? _updater;

    // === UI 控件 ===
    private readonly Label _gamePathLabel = new();
    private readonly Button _browseBtn = new();
    private readonly Button _scanBtn = new();
    private readonly Button _checkBtn = new();
    private readonly Button _updateAllBtn = new();
    private readonly Button _browseModBtn = new();
    private readonly Button _gbMatchBtn = new();
    private readonly Button _settingsBtn = new();
    private readonly Button _aboutBtn = new();
    private DataGridView _grid = null!;
    private readonly TextBox _searchBox = new();
    private readonly Button _clearSearchBtn = new();
    private readonly ComboBox _statusFilterBox = new();
    private readonly ComboBox _viewBox = new();
    private readonly RichTextBox _logBox = new();
    private readonly SplitContainer _mainSplit = new();
    private readonly StatusStrip _statusStrip = new();
    private ToolStripStatusLabel _toolStripStatus = null!;
    private ToolStripProgressBar _toolStripProgress = null!;
    private ToolStripSeparator _toolStripProgressSeparator = null!;
    private ToolStripStatusLabel _toolStripEnabled = null!;

    // === 列索引常量 ===
    private const int ColEnabled = 0;   // 复选框：直接开关这个 mod
    private const int ColName = 1;
    private const int ColGuid = 2;
    private const int ColCurVer = 3;
    private const int ColNewVer = 4;
    private const int ColSource = 5;
    private const int ColStatus = 6;
    private const int ColAction = 7;

    /// <summary>状态筛选（空串 = 全部）</summary>
    private string _statusFilter = "";

    // === 文件夹视图 ===
    private List<Services.ModGrouper.FolderGroup>? _folderCache;
    private long _folderCacheVersion = -1;
    /// <summary>目录合成节点缓存（按路径复用同一实例，滚动恢复靠对象身份）</summary>
    private readonly Dictionary<string, ModInfo> _folderNodes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>取（或创建）一个目录的合成节点</summary>
    private ModInfo GetFolderNode(Services.ModGrouper.FolderGroup g)
    {
        if (_folderNodes.TryGetValue(g.RelPath, out var node)) return node;

        node = new ModInfo
        {
            Name = g.DisplayName,
            IsFolderNode = true,
            FolderRelPath = g.RelPath,
            IsGroupHead = true,
            GroupKey = g.Key,
            GroupName = g.DisplayName,
            IsEnabled = false,
            Status = UpdateStatus.NotChecked,
        };
        _folderNodes[g.RelPath] = node;
        return node;
    }

    /// <summary>文件夹模式下文件行的可见性（关键字 + 状态筛选）</summary>
    private bool FolderFileVisible(ModInfo f, bool searching)
    {
        if (_statusFilter.Length > 0)
        {
            var ok = _statusFilter switch
            {
                "enabled" => f.IsEnabled,
                "disabled" => !f.IsEnabled,
                "update" => f.Status == UpdateStatus.UpdateAvailable,
                "nosource" => f.Status == UpdateStatus.NoSource,
                "local" => f.Status == UpdateStatus.LocalMod,
                "unknown" => f.Status == UpdateStatus.Unknown,
                _ => true,
            };
            if (!ok) return false;
        }
        if (!searching) return true;

        return ContainsAny(f.Name) || ContainsAny(f.FileName) || ContainsAny(f.PluginGuid) ||
               ContainsAny(f.DataModCreator);
    }

    private bool ContainsAny(string? s) =>
        !string.IsNullOrEmpty(s) &&
        s.Contains(_searchText, StringComparison.OrdinalIgnoreCase);

    private static bool FolderNameMatch(Services.ModGrouper.FolderGroup g, string text) =>
        g.DisplayName.Contains(text, StringComparison.OrdinalIgnoreCase);

    public MainForm()
    {
        Program.Trace("ctor begin");
        // 加载配置
        _config = ConfigManager.Load();

        Text = "CU Mod 更新器 - Casualties Unknown Demo";
        Size = new Size(1100, 700);
        MinimumSize = new Size(900, 550);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = new Font("Microsoft YaHei UI", 9F);
        AutoScaleMode = AutoScaleMode.Dpi;

        Program.Trace("BuildUI begin");
        BuildUI();
        Program.Trace("BuildUI end");

        // 初始化扫描器和更新管理器
        InitServices();
        Program.Trace("InitServices end");

        // 自动扫描 + 自动检查。
        // ⚠️ 之前 AutoCheckOnStart 只存在于设置界面和配置文件里，
        // 启动流程从来没读过它 —— 用户勾了「启动时自动检查」却毫无作用。
        if (_config.AutoScanOnStart && _scanner != null)
        {
            _ = StartupFlowAsync();
        }

        // 预热 CasualtiesManageable 官方元数据（后台拉取/刷新 1 小时缓存，
        // 扫描后的自动配置依赖它做 GUID 强匹配，避免手填 N 网 ID）
        _ = Task.Run(async () =>
        {
            try
            {
                using var cm = new Services.CasualtiesManageableService();
                await cm.EnsureLoadedAsync();
            }
            catch { /* 后台预热失败不影响主流程 */ }
        });
    }

    /// <summary>启动自动流程：扫描 →（按设置）检查更新</summary>
    private async Task StartupFlowAsync()
    {
        await ScanAsync();
        if (_config.AutoCheckOnStart && _updater != null && _mods.Count > 0)
            await CheckUpdatesAsync();
    }

    // ==================== UI 构建 ====================

    private void BuildUI()
    {
        // 顶部工具栏 - 单一 FlowLayoutPanel 直接平铺窗体（Dock=Top + AutoSize 自测量，最可靠）
        // 路径标签 SetFlowBreak 独占一行；不再嵌套任何 AutoSize 面板（嵌套测量会算矮导致按钮盖住表格）
        var topFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Theme.PanelBg,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            Margin = new Padding(0),
            Padding = new Padding(12, 8, 12, 8),
        };

        _gamePathLabel.Text = "游戏路径: " + _config.GamePath;
        _gamePathLabel.ForeColor = Theme.TextDim;
        _gamePathLabel.AutoSize = true;
        _gamePathLabel.Margin = new Padding(4, 6, 4, 2);
        topFlow.Controls.Add(_gamePathLabel);

        // 筛选框放在「路径行」——这一行本来就大片空白，
        // 放在按钮行的话会被挤到第三行，按钮一多整个顶栏跟着乱。
        // 宽度用 LogicalToDeviceUnits 换算，不同 DPI 下视觉宽度一致
        // （直接写像素会被 AutoScaleMode.Dpi 再放大一次，高 DPI 下变得过宽）。
        _searchBox.PlaceholderText = "🔍 筛选名称 / GUID";
        _searchBox.Width = LogicalToDeviceUnits(170);
        _searchBox.Margin = new Padding(16, 2, 0, 2);
        topFlow.Controls.Add(_searchBox);

        // 清除按钮做成小方块紧贴输入框 —— 之前是个全尺寸按钮孤零零在旁边，
        // 看起来像另一个功能入口，很怪
        _clearSearchBtn.Text = "✕";
        _clearSearchBtn.AutoSize = false;
        _clearSearchBtn.Size = LogicalToDeviceUnits(new Size(26, 24));
        _clearSearchBtn.FlatStyle = FlatStyle.Flat;
        _clearSearchBtn.FlatAppearance.BorderColor = Theme.Border;
        _clearSearchBtn.BackColor = Theme.GridBg;
        _clearSearchBtn.ForeColor = Theme.TextDim;
        _clearSearchBtn.Margin = new Padding(2, 2, 4, 2);
        _clearSearchBtn.Cursor = Cursors.Hand;
        ToolTipHelper(_clearSearchBtn, "清空筛选");
        topFlow.Controls.Add(_clearSearchBtn);

        // 状态筛选：找「启用的」「禁用的」「有更新的」不用再肉眼扫
        // （注意：先设 SelectedIndex 再挂事件，否则初始化会触发一次 PopulateGrid）
        var folderView = string.Equals(_config.GroupingView, "folder", StringComparison.OrdinalIgnoreCase);
        _statusFilterBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _statusFilterBox.Items.AddRange(new object[] { "全部状态", "已启用", "已禁用", "可更新", "未配置", "自有", "未知" });
        _statusFilterBox.Width = LogicalToDeviceUnits(92);
        _statusFilterBox.BackColor = Theme.GridBg;
        _statusFilterBox.ForeColor = Theme.Text;
        _statusFilterBox.FlatStyle = FlatStyle.Flat;
        _statusFilterBox.Margin = new Padding(0, 2, 4, 2);
        _statusFilterBox.SelectedIndex = 0;
        _statusFilterBox.SelectedIndexChanged += (_, _) =>
        {
            _statusFilter = _statusFilterBox.SelectedIndex switch
            {
                1 => "enabled",
                2 => "disabled",
                3 => "update",
                4 => "nosource",
                5 => "local",
                6 => "unknown",
                _ => "",
            };
            PopulateGrid();
        };
        topFlow.Controls.Add(_statusFilterBox);

        // 视图切换：来源分组（更新视角）/ 文件夹分组（管理视角，目录树）
        _viewBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _viewBox.Items.AddRange(new object[] { "视图：来源分组", "视图：文件夹分组" });
        _viewBox.Width = LogicalToDeviceUnits(120);
        _viewBox.BackColor = Theme.GridBg;
        _viewBox.ForeColor = Theme.Text;
        _viewBox.FlatStyle = FlatStyle.Flat;
        _viewBox.Margin = new Padding(0, 2, 4, 2);
        _viewBox.SelectedIndex = folderView ? 1 : 0;
        _viewBox.SelectedIndexChanged += (_, _) =>
        {
            var v = _viewBox.SelectedIndex == 1 ? "folder" : "source";
            if (v == _config.GroupingView) return;
            _config.GroupingView = v;
            ConfigManager.Save(_config);
            _expandedGroups.Clear();     // 两套分组键不通用
            PopulateGrid();
        };
        topFlow.Controls.Add(_viewBox);

        topFlow.SetFlowBreak(_viewBox, true);   // 路径行到此换行，按钮从下一行排起

        _browseBtn.Text = "浏览...";
        StyleButton(_browseBtn);
        _browseBtn.Click += OnBrowseClick;
        topFlow.Controls.Add(_browseBtn);

        _scanBtn.Text = "🔄 扫描插件";
        StyleButton(_scanBtn);
        _scanBtn.Click += async (_, _) => await ScanAsync();
        topFlow.Controls.Add(_scanBtn);

        _checkBtn.Text = "📡 检查更新";
        StyleButton(_checkBtn, Theme.Accent);
        _checkBtn.Click += async (_, _) => await CheckUpdatesAsync();
        topFlow.Controls.Add(_checkBtn);

        _updateAllBtn.Text = "⬆ 一键更新全部";
        StyleButton(_updateAllBtn, Color.FromArgb(33, 115, 81));
        _updateAllBtn.Click += async (_, _) => await UpdateAllAsync();
        topFlow.Controls.Add(_updateAllBtn);

        _browseModBtn.Text = "🛒 浏览 Mod";
        StyleButton(_browseModBtn, Color.FromArgb(123, 31, 162));
        _browseModBtn.Click += (_, _) => OpenModBrowser();
        topFlow.Controls.Add(_browseModBtn);

        // GameBanana 匹配按钮（社区 mod 主源在 GameBanana，作者信息权威）
        _gbMatchBtn.Text = "🍌 GB 匹配";
        StyleButton(_gbMatchBtn, Color.FromArgb(200, 130, 30));
        _gbMatchBtn.Click += async (_, _) => await GameBananaMatchAsync();
        topFlow.Controls.Add(_gbMatchBtn);

        _settingsBtn.Text = "⚙ 设置";
        StyleButton(_settingsBtn);
        _settingsBtn.Click += (_, _) => OpenSettings();
        topFlow.Controls.Add(_settingsBtn);

        _aboutBtn.Text = "ℹ 关于";
        StyleButton(_aboutBtn);
        _aboutBtn.Click += (_, _) => ShowAbout();
        topFlow.Controls.Add(_aboutBtn);

        // 筛选框的 TextChanged 只刷新显示列表，不会重排 _mods
        _searchBox.TextChanged += (_, _) =>
        {
            var t = _searchBox.Text.Trim();
            if (t == _searchText) return;
            _searchText = t;
            PopulateGrid();
        };

        _clearSearchBtn.Click += (_, _) =>
        {
            if (_searchText.Length == 0) return;
            _searchBox.Text = "";   // TextChanged 会负责刷新
        };

        // 主区域: DataGridView + LogBox
        _mainSplit.Dock = DockStyle.Fill;
        _mainSplit.Orientation = Orientation.Horizontal;
        _mainSplit.SplitterDistance = 380;
        _mainSplit.BackColor = Theme.Bg;
        _mainSplit.Panel1.BackColor = Theme.Bg;
        _mainSplit.Panel2.BackColor = Theme.Bg;

        // DataGridView
        BuildGrid();
        _mainSplit.Panel1.Controls.Add(_grid);

        // LogBox
        _logBox.Multiline = true;
        _logBox.Dock = DockStyle.Fill;
        _logBox.ReadOnly = true;
        _logBox.BackColor = Theme.LogBg;
        _logBox.ForeColor = Color.FromArgb(190, 190, 190);
        _logBox.Font = new Font("Consolas", 9F);
        _logBox.ScrollBars = RichTextBoxScrollBars.Vertical;
        _logBox.BorderStyle = BorderStyle.None;
        _logBox.Text = "";
        var logLabel = new Label
        {
            Dock = DockStyle.Top,
            Text = "  操作日志",
            Height = 22,
            BackColor = Theme.PanelBg,
            ForeColor = Theme.TextDim,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        _mainSplit.Panel2.Controls.Add(_logBox);
        _mainSplit.Panel2.Controls.Add(logLabel);

        Controls.Add(_mainSplit);

        // 状态栏
        BuildStatusStrip();
        Controls.Add(_statusStrip);

        // 顶部栏最后 Add → z-order 最前、贴在窗体最上方（其他控件已先 Add，不会被覆盖）
        Controls.Add(topFlow);
    }

    private void BuildGrid()
    {
        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            BackgroundColor = Theme.GridBg,
            BorderStyle = BorderStyle.None,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            // 整表只读会连复选框也锁死，所以按列控制（下面逐列设置）
            ReadOnly = false,
            MultiSelect = true,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            ColumnHeadersDefaultCellStyle =
            {
                BackColor = Theme.GridHead,
                ForeColor = Theme.Text,
                Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
                Alignment = DataGridViewContentAlignment.MiddleLeft,
                Padding = new Padding(8, 0, 0, 0),
            },
            EnableHeadersVisualStyles = false,
            RowTemplate =
            {
                Height = 30,
            },
            GridColor = Theme.Border,
        };

        _grid.RowHeadersVisible = false;
        _grid.SizeChanged += (_, _) => AlignGridHeight();   // 尺寸变化后对齐行高（防最后一行半截）
        _grid.DefaultCellStyle.BackColor = Theme.GridBg;
        _grid.DefaultCellStyle.ForeColor = Theme.Text;
        _grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(50, 50, 55);
        _grid.DefaultCellStyle.SelectionForeColor = Theme.Text;
        _grid.AlternatingRowsDefaultCellStyle.BackColor = Theme.GridAlt;
        _grid.CellFormatting += OnGridCellFormatting;
        _grid.CellContentClick += OnGridCellContentClick;
        _grid.CellDoubleClick += OnGridCellDoubleClick;

        // 添加列。
        // 「启用」用复选框单独一列 —— 之前塞在操作列里，同一列混着
        // 「可不可更新 / 是不是自有 / 有没有禁用」四种语义，按钮又同色难区分。
        // 现在开关归复选框（可批量多选后右键批处理），操作列只留真正的动作。
        _grid.Columns.Add(new DataGridViewCheckBoxColumn
        {
            Name = "启用",
            HeaderText = "启用",
            FillWeight = 34,
            ThreeState = true,           // 目录节点用中间态表示「混合」
            TrueValue = true,
            FalseValue = false,
            IndeterminateValue = null,
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Mod名称",
            HeaderText = "Mod 名称",
            FillWeight = 180,
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "GUID",
            HeaderText = "GUID",
            FillWeight = 120,
            DefaultCellStyle = { ForeColor = Theme.TextDim },
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "当前版本",
            HeaderText = "当前版本",
            FillWeight = 80,
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "最新版本",
            HeaderText = "最新版本",
            FillWeight = 80,
            DefaultCellStyle = { ForeColor = Theme.TextDim },
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "来源",
            HeaderText = "来源",
            FillWeight = 120,
            DefaultCellStyle = { ForeColor = Theme.TextDim },
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "状态",
            HeaderText = "状态",
            FillWeight = 70,
        });
        _grid.Columns.Add(new DataGridViewButtonColumn
        {
            Name = "操作",
            HeaderText = "操作",
            FillWeight = 60,
            Text = "—",
            UseColumnTextForButtonValue = false,
            DefaultCellStyle =
            {
                BackColor = Theme.GridBg,
                SelectionBackColor = Color.FromArgb(50, 50, 55),
            },
        });

        // 关闭表头点击排序。
        // DataGridView 的排序只重排「视觉行」，而 _visible（行号 → Mod 的映射）
        // 不会跟着重排 → 行里显示的内容与实际 Mod 错位。
        // 典型症状：点名称列排序后，私有 mod 那行冒出「可更新」，
        // 或者「◈ 自有」的状态配着一条 Nexus 来源。
        // 列表顺序由 ModGrouper 决定（有更新的 / 启用优先，再按名称）。
        foreach (DataGridViewColumn col in _grid.Columns)
        {
            col.SortMode = DataGridViewColumnSortMode.NotSortable;
            // 只有「启用」复选框可编辑，其余列全部只读
            col.ReadOnly = col.Index != ColEnabled;
        }

        // 右键菜单
        var ctxMenu = new ContextMenuStrip();
        ctxMenu.BackColor = Theme.PanelBg;
        ctxMenu.ForeColor = Theme.Text;
        AddMenuItem(ctxMenu, "🔄 更新此 Mod", (_, _) => _ = UpdateSelectedAsync());
        AddMenuItem(ctxMenu, "⏻ 启用 / 禁用此 Mod", (_, _) => ToggleSelectedMod());
        AddMenuItem(ctxMenu, "🚫 禁用所选（可多选）", (_, _) => _ = ToggleSelectedBatchAsync(false));
        AddMenuItem(ctxMenu, "✅ 启用所选（可多选）", (_, _) => _ = ToggleSelectedBatchAsync(true));
        AddMenuItem(ctxMenu, "🔧 配置更新来源...", (_, _) => ConfigureSource());
        AddMenuItem(ctxMenu, "🔍 GitHub 仓库解析...", (_, _) => _ = ResolveFromGithubAsync());
        AddMenuItem(ctxMenu, "📄 查看更新日志 / 依赖", (_, _) => ShowChangelog());
        AddMenuItem(ctxMenu, "---");
        AddMenuItem(ctxMenu, "📁 查看备份", (_, _) => ShowBackups());
        AddMenuItem(ctxMenu, "↩ 从备份回滚...", (_, _) => RollbackFromBackup());
        AddMenuItem(ctxMenu, "---");
        AddMenuItem(ctxMenu, "🌐 在浏览器中打开", (_, _) => OpenInBrowser());
        AddMenuItem(ctxMenu, "🔍 GitHub 搜索此 Mod", (_, _) => SearchGitHub());
        AddMenuItem(ctxMenu, "📋 复制 GUID", (_, _) => CopyGuid());
        AddMenuItem(ctxMenu, "📂 打开文件位置", (_, _) => OpenFileLocation());
        _grid.ContextMenuStrip = ctxMenu;
    }

    private void AddMenuItem(ContextMenuStrip menu, string text, EventHandler? handler = null)
    {
        if (text == "---")
        {
            menu.Items.Add(new ToolStripSeparator { BackColor = Theme.PanelBg });
            return;
        }
        var item = new ToolStripMenuItem(text);
        if (handler != null) item.Click += handler;
        menu.Items.Add(item);
    }

    private void BuildStatusStrip()
    {
        _statusStrip.BackColor = Theme.PanelBg;
        _statusStrip.ForeColor = Theme.TextDim;
        _statusStrip.SizingGrip = false;

        _toolStripStatus = new ToolStripStatusLabel("就绪")
        {
            Margin = new Padding(8, 0, 0, 0),
            Spring = true,               // 占满剩余宽度，把进度条推到最右
            TextAlign = ContentAlignment.MiddleLeft,
        };
        _toolStripProgress = new ToolStripProgressBar
        {
            Width = LogicalToDeviceUnits(180),
            Visible = false,
            Style = ProgressBarStyle.Continuous,
        };
        _toolStripProgressSeparator = new ToolStripSeparator { Visible = false };
        _toolStripEnabled = new ToolStripStatusLabel("0/0 已启用")
        {
            Margin = new Padding(4, 0, 8, 0),
        };

        _statusStrip.Items.Add(_toolStripEnabled);
        _statusStrip.Items.Add(_toolStripStatus);
        _statusStrip.Items.Add(_toolStripProgressSeparator);
        _statusStrip.Items.Add(_toolStripProgress);
    }

    // ==================== 样式辅助 ====================

    private static void StyleButton(Button btn, Color? accent = null)
    {
        var c = accent ?? Color.FromArgb(60, 60, 65);
        btn.FlatStyle = FlatStyle.Flat;
        btn.FlatAppearance.BorderColor = Theme.Border;
        btn.FlatAppearance.MouseOverBackColor = Theme.AccentH;
        btn.BackColor = c;
        btn.ForeColor = Theme.Text;
        btn.AutoSize = true;
        btn.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        btn.Padding = new Padding(16, 7, 16, 7);
        btn.Margin = new Padding(4, 3, 4, 3);
        btn.Font = new Font("Microsoft YaHei UI", 9F);
        btn.Cursor = Cursors.Hand;
        btn.TextAlign = ContentAlignment.MiddleCenter;
    }

    private static void ToolTipHelper(Control c, string text)
    {
        var tip = new ToolTip { BackColor = Theme.PanelBg, ForeColor = Theme.Text };
        tip.SetToolTip(c, text);
    }

    // ==================== 初始化 ====================

    private void InitServices()
    {
        if (!PluginScanner.IsValidGamePath(_config.GamePath))
        {
            var auto = PluginScanner.AutoDetectGamePath();
            if (auto != null)
            {
                _config.GamePath = auto;
                ConfigManager.Save(_config);
            }
        }

        if (PluginScanner.IsValidGamePath(_config.GamePath))
        {
            _scanner = new PluginScanner(_config.GamePath);
            _gamePathLabel.Text = "游戏路径: " + _config.GamePath;

            var github = new GitHubChecker(
                string.IsNullOrEmpty(_config.GitHubToken) ? null : _config.GitHubToken);
            NexusChecker? nexus = null;
            if (!string.IsNullOrEmpty(_config.NexusApiKey))
                nexus = new NexusChecker(_config.NexusApiKey, _config.NexusGameDomain);
            var gamebanana = new GameBananaBrowser();

            _updater = new UpdateManager(
                github, nexus, gamebanana, _config.GamePath,
                _config.BackupRetention, _config.EnableBackup,
                _config.NexusCookie, _config.NexusApiKey);
            _updater.OnLog = Log;
            _updater.OnDownloadProgress = OnDownloadProgress;
        }
        else
        {
            _gamePathLabel.Text = "⚠ 游戏路径无效，请点击设置配置正确路径";
            _gamePathLabel.ForeColor = Theme.Warning;
        }
    }

    // ==================== 扫描 ====================

    private async Task ScanAsync()
    {
        Program.Trace("ScanAsync begin");
        if (_scanner == null)
        {
            MessageBox.Show("请先在设置中配置正确的游戏路径。",
                "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _scanBtn.Enabled = false;
        _toolStripStatus.Text = "正在扫描插件...";
        ShowProgress(true);
        SetProgress(0, 1);

        Log("=== 开始扫描 BepInEx 插件目录 ===");

        var mods = await Task.Run(() =>
        {
            var scanner = new PluginScanner(_config.GamePath);

            // 进度上报做了两件事：
            //  ① 节流 —— 逐文件回调会往 UI 线程塞上百条消息，把状态栏刷得乱跳；
            //     限制到 ~25 次/秒，扫得快时表现为平滑前进而不是抽搐。
            //  ② 句柄保护 —— 启动时的自动扫描发生在窗体句柄创建**之前**，
            //     此时 BeginInvoke 直接抛异常，回调一炸整个 Task 就 fault 了，
            //     表现是「进度条卡在 0 不动」。必须判 IsHandleCreated。
            var sw = System.Diagnostics.Stopwatch.StartNew();
            scanner.OnProgress = (msg, cur, total) =>
            {
                var final = cur >= total;
                if (!final && sw.ElapsedMilliseconds < 40) return;
                sw.Restart();

                if (!IsHandleCreated) return;
                try
                {
                    BeginInvoke(() =>
                    {
                        _toolStripStatus.Text = msg;
                        SetProgress(cur, total);
                    });
                }
                catch { /* 窗体正在关闭时 BeginInvoke 会抛，忽略 */ }
            };

            return scanner.Scan();
        });

        _mods = mods;
        _modsVersion++; // 使分组缓存失效

        // 自动配置：根据精选数据库匹配 GUID，免去手动填写
        var autoConfigured = ConfigManager.AutoConfigureAll(_config, _mods);
        if (autoConfigured > 0)
            Log($"✓ 自动识别了 {autoConfigured} 个 Mod 的更新源（来自精选数据库）");

        // 第二轮：从 BepInEx 配置文件自动发现（如 i18nAutoUpdater 的 Repo Path）
        var discovered = 0;
        foreach (var mod in _mods.Where(m => m.Source == null))
        {
            var oldSource = mod.Source;
            DiscoverFromBepInExConfig(mod);
            if (mod.Source != null && oldSource == null) discovered++;
        }
        if (discovered > 0)
            Log($"✓ 从 BepInEx 配置额外发现 {discovered} 个 Mod 的更新源");

        // 关联配置中的来源
        foreach (var mod in _mods)
        {
            mod.Source = ConfigManager.GetSource(_config, mod);
            if (mod.Source == null)
            {
                // 自有/本地 mod（元数据未收录的自制插件）标为 LocalMod，
                // 不显示为「待配置」——它们本来就没有更新源，不是漏配
                if (Services.CasualtiesManageableService.IsLocalMod(mod.FileName, mod.PluginGuid))
                    mod.Status = UpdateStatus.LocalMod;
                else
                    mod.Status = mod.IsEnabled ? UpdateStatus.NoSource : UpdateStatus.Disabled;
            }

            // 标记归属歧义：同名 dll 被多个 mod 声明，无法自动判断来源
            mod.HasAmbiguousDll = mod.Source == null &&
                Services.CasualtiesManageableService.IsAmbiguousDllName(mod.FileName);
        }

        PopulateGrid();

        var modCount = _mods.Count(m => m.IsBepInExPlugin);
        var depCount = _mods.Count(m => !m.IsBepInExPlugin);
        var disabledCount = _mods.Count(m => !m.IsEnabled);
        var withSource = _mods.Count(m => m.Source != null && m.Source.IsValid);
        var localCount = _mods.Count(m => m.Status == UpdateStatus.LocalMod);
        var noSource = _mods.Count(m => m.Status == UpdateStatus.NoSource);
        var dupCount = _mods.Count(m => m.IsDuplicateFile);
        var conflictCount = _mods.Count(m => m.HasNameConflict);
        var groupCount = _visible.Count(m => m.GroupSize > 1 && m.IsGroupHead);

        _toolStripStatus.Text =
            $"扫描完成: {modCount} 个 Mod | {withSource} 已配置 | {localCount} 自有 | " +
            $"{noSource} 待配置 | {disabledCount} 禁用" +
            (dupCount > 0 ? $" | ⚠ {dupCount} 重复副本" : "") +
            (conflictCount > 0 ? $" | ⚠ {conflictCount} 同名冲突" : "");
        ShowProgress(false);

        Log($"扫描完成: {modCount} Mod, {withSource} 已自动识别, {localCount} 自有, {noSource} 待手动配置");
        if (groupCount > 0)
            Log($"  已按来源归组: {groupCount} 个多文件组（点击名称列可展开查看附属文件）");
        if (dupCount > 0)
        {
            Log($"  ⚠ {dupCount} 个重复副本（同一插件存在多份，BepInEx 会重复加载）:");
            foreach (var d in _mods.Where(m => m.IsDuplicateFile))
                Log($"      {d.FilePath.Replace(_config.GamePath, "")}");
        }
        if (conflictCount > 0)
        {
            Log($"  ⚠ {conflictCount} 个同名冲突（不同插件用了同一个文件名，不是重复安装）:");
            foreach (var d in _mods.Where(m => m.HasNameConflict))
                Log($"      {d.FilePath.Replace(_config.GamePath, "")}");
            Log("  有些 mod 会刻意自带改过的同名 dll（如 Gunsaw Genetics 的 body_sprite_replacer），按 mod 说明处理即可。");
        }

        LogDirectoryLayout();

        _config.LastScanTime = DateTime.Now;
        ConfigManager.Save(_config);

        _scanBtn.Enabled = true;
        Program.Trace("ScanAsync end");
    }

    /// <summary>显示/隐藏进度条（连同它前面的分隔符一起，避免留下一段孤零零的竖线）</summary>
    private void ShowProgress(bool show)
    {
        _toolStripProgress.Visible = show;
        _toolStripProgressSeparator.Visible = show;
        if (!show)
        {
            // 归零再藏，避免下一次扫描一开始就带着上一轮的残留填充
            try { _toolStripProgress.Value = 0; } catch { }
        }
    }

    /// <summary>设置进度（total ≤ 0 时按不确定进度处理）</summary>
    private void SetProgress(int cur, int total)
    {
        if (total <= 0)
        {
            _toolStripProgress.Maximum = 100;
            _toolStripProgress.Value = 0;
            return;
        }
        _toolStripProgress.Maximum = total;
        _toolStripProgress.Value = Math.Clamp(cur, 0, total);
    }

    // ==================== 检查更新 ====================

    private async Task CheckUpdatesAsync()
    {
        Program.Trace("CheckUpdatesAsync begin");
        if (_updater == null)
        {
            MessageBox.Show("请先扫描插件并确保游戏路径正确。", "提示");
            return;
        }

        if (_mods.Count == 0)
        {
            MessageBox.Show("没有扫描到任何 Mod，请先点击「扫描插件」。", "提示");
            return;
        }

        _checkBtn.Enabled = false;
        _updateAllBtn.Enabled = false;
        _toolStripStatus.Text = "正在检查更新...";
        ShowProgress(true);

        Log("=== 开始检查更新 ===");

        // 先确保元数据可用 —— 否则所有 N 网 mod 都会查不到、全部变「? 未知」。
        // 就算拿不到也继续，让每个 mod 各自报错，至少错误信息是真实的。
        using (var cm = new Services.CasualtiesManageableService())
        {
            var metaOk = await cm.EnsureLoadedAsync();
            Program.Trace($"CheckUpdates: meta={metaOk}");
            Log(metaOk ? "✓ 官方元数据已就绪" : "⚠ 元数据不可用（缓存缺失且下载失败），N 网来源的检查结果会不准");
        }

        Program.Trace("CheckUpdates: CheckAllAsync begin");
        await _updater.CheckAllAsync(_mods, _config.ConcurrentChecks);
        Program.Trace("CheckUpdates: CheckAllAsync done");

        PopulateGrid();
        Program.Trace("CheckUpdates: repopulated");

        var updates = _mods.Count(m => m.Status == UpdateStatus.UpdateAvailable);
        var upToDate = _mods.Count(m => m.Status == UpdateStatus.UpToDate);
        var errors = _mods.Count(m => m.Status == UpdateStatus.Error);
        _toolStripStatus.Text = $"检查完成: {updates} 个可更新, {upToDate} 个已最新, {errors} 个错误";
        ShowProgress(false);

        Log($"检查完成: {updates} 可更新 / {upToDate} 已最新 / {errors} 错误");

        _checkBtn.Enabled = true;
        _updateAllBtn.Enabled = updates > 0;
    }

    // ==================== 一键更新 ====================

    private async Task UpdateAllAsync()
    {
        if (_updater == null) return;

        var toUpdate = _mods
            .Where(m => m.Status == UpdateStatus.UpdateAvailable)
            // 按 GUID/文件名去重（防止备份目录等造成的重复条目）
            .GroupBy(m => !string.IsNullOrEmpty(m.PluginGuid)
                ? "guid:" + m.PluginGuid
                : "file:" + Path.GetFileName(m.FilePath))
            .Select(g => g.First())
            .ToList();

        if (toUpdate.Count == 0)
        {
            MessageBox.Show("没有需要更新的 Mod。", "提示");
            return;
        }

        var result = MessageBox.Show(
            $"即将更新 {toUpdate.Count} 个 Mod：\n\n" +
            string.Join("\n", toUpdate.Select(m =>
                $"  • {m.Name}  {m.CurrentVersion} → {m.LatestVersion}")) +
            "\n\n旧版本将被自动备份。确认继续？",
            "确认更新", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

        if (result != DialogResult.Yes) return;

        _updateAllBtn.Enabled = false;
        _checkBtn.Enabled = false;
        ShowProgress(true);
        _toolStripProgress.Style = ProgressBarStyle.Continuous;

        Log("=== 开始一键更新 ===");

        var (success, failed) = await _updater.InstallAllUpdatesAsync(_mods);

        PopulateGrid();

        _toolStripStatus.Text = $"更新完成: {success} 成功, {failed} 失败";
        ShowProgress(false);
        Log($"更新完成: {success} 成功, {failed} 失败");

        _checkBtn.Enabled = true;
        _updateAllBtn.Enabled = false;

        if (success > 0)
        {
            MessageBox.Show(
                $"成功更新 {success} 个 Mod！" +
                (failed > 0 ? $"\n{failed} 个失败，请查看日志。" : ""),
                "更新完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    // ==================== GameBanana 匹配 ====================

    /// <summary>
    /// 从 GameBanana 自动匹配：拉取游戏全部 Mod（免费免 key），按名称匹配已安装 Mod 并配置更新源。
    /// 作者/版本/下载链接来自 GameBanana（社区权威源），比 N 网/GitHub 用户名可靠。
    /// </summary>
    private async Task GameBananaMatchAsync()
    {
        if (_mods.Count == 0)
        {
            MessageBox.Show("请先扫描插件。", "提示");
            return;
        }

        _gbMatchBtn.Enabled = false;
        _toolStripStatus.Text = "正在从 GameBanana 拉取 Mod 列表...";
        ShowProgress(true);
        _toolStripProgress.Style = ProgressBarStyle.Marquee;

        try
        {
            using var gb = new GameBananaBrowser();

            // 拉取 GameBanana 上全部 CU Mod（Subfeed + 详情）
            var allGbMods = await gb.GetModsAsync();
            Log($"从 GameBanana 拉取到 {allGbMods.Count} 个 Mod");

            // 按规范化名称匹配
            int matched = 0;
            foreach (var mod in _mods.Where(m => m.Source == null || !m.Source.IsValid))
            {
                if (string.IsNullOrEmpty(mod.Name)) continue;

                var modName = NormalizeForMatch(mod.Name);

                var hit = allGbMods.FirstOrDefault(g =>
                {
                    var gName = NormalizeForMatch(g.Name);
                    return !string.IsNullOrEmpty(gName) &&
                           (gName.Contains(modName) || modName.Contains(gName));
                });

                if (hit != null && long.TryParse(hit.ModId, out _))
                {
                    var source = new ModSource
                    {
                        Type = "gamebanana",
                        ModId = hit.ModId,
                    };
                    ConfigManager.SetSource(_config, mod, source);
                    mod.Source = source;
                    Log($"  ✓ {mod.Name} → GameBanana #{hit.ModId} ({hit.Name} / {hit.Author})");
                    matched++;
                }
            }

            ConfigManager.Save(_config);
            PopulateGrid();

            _toolStripStatus.Text = $"GB 匹配完成: {matched} 个新匹配 / GameBanana 共 {allGbMods.Count} 个 Mod";
            Log($"GB 匹配完成: 新匹配 {matched} 个");

            if (matched == 0)
            {
                MessageBox.Show(
                    $"未能自动匹配。GameBanana 上共有 {allGbMods.Count} 个 Mod。\n\n" +
                    "提示: Mod 名称与 GameBanana 不一致时无法匹配，可以右键点击 Mod 选择「GitHub 搜索」找仓库。",
                    "匹配结果", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        catch (Exception ex)
        {
            Log($"GameBanana 匹配失败: {ex.Message}");
            MessageBox.Show($"GameBanana 匹配失败:\n{ex.Message}\n\n请检查网络。",
                "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _gbMatchBtn.Enabled = true;
            ShowProgress(false);
        }
    }

    /// <summary>
    /// 名称规范化：小写 + 只保留字母数字
    /// ("QoL: Unknown" / "QoL-Unknown" / "QoL Unknown" → "qolunknown")
    /// </summary>
    private static string NormalizeForMatch(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var c in name.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
        }
        return sb.ToString();
    }

    private async Task UpdateSelectedAsync()
    {
        if (_updater == null) return;
        if (_grid.CurrentRow == null) return;
        var mod = ModAtRow(_grid.CurrentRow?.Index ?? -1);
        if (mod == null) return;
        if (mod.Status != UpdateStatus.UpdateAvailable) return;

        _toolStripStatus.Text = $"正在更新 {mod.Name}...";
        ShowProgress(true);
        _toolStripProgress.Style = ProgressBarStyle.Continuous;

        Log($"--- 更新 {mod.Name} ---");
        await _updater.InstallUpdateAsync(mod);

        PopulateGrid();
        ShowProgress(false);
        _toolStripStatus.Text = mod.Status == UpdateStatus.Updated
            ? $"{mod.Name} 更新成功" : $"{mod.Name} 更新失败";
    }

    // ==================== 填充表格 ====================

    private void PopulateGrid() => PopulateGrid(null);

    /// <summary>
    /// 重建表格。
    ///
    /// focusMod 非空时把该行滚入视野并选中（用于启用/禁用后「停在原地」），
    /// 否则恢复重建前的顶部行——展开/收起、筛选、检查更新都会重建全部行，
    /// 不恢复的话表格每次都跳回开头。
    /// </summary>
    private void PopulateGrid(ModInfo? focusMod)
    {
        // 控件的事件（状态下拉、筛选框）可能在 BuildUI 早期触发，
        // 那时表格还没创建，直接退出
        if (_grid == null) return;

        // 1. 记住重建前的顶部条目（用「Mod 对象」而不是行号，
        //    因为展开/收起会增减行，行号会变）
        ModInfo? topMod = null;
        try
        {
            var idx = _grid.FirstDisplayedScrollingRowIndex;
            if (idx >= 0 && idx < _visible.Count) topMod = _visible[idx];
        }
        catch { /* 无行可显示时会抛，忽略 */ }

        _grid.Rows.Clear();

        // 分组结果缓存：_mods 未变化时复用（检查更新会频繁调用本方法，
        // 每次都全量重算分组代价不小）
        if (_groupCache == null || _groupCacheVersion != _modsVersion)
        {
            _groupCache = Services.ModGrouper.Group(_mods);
            _groupCacheVersion = _modsVersion;
        }

        var searching = _searchText.Length > 0;
        var folderMode = string.Equals(_config.GroupingView, "folder", StringComparison.OrdinalIgnoreCase);

        // 2. 组装候选列表
        var candidates = new List<ModInfo>();
        if (folderMode)
        {
            // ── 文件夹视图（管理视角，目录树一目了然）──
            // 每个目录一个可折叠节点；文件行归属自己的目录，不再按来源折叠。
            if (_folderCache == null || _folderCacheVersion != _modsVersion)
            {
                _folderCache = Services.ModGrouper.BuildFolderGroups(
                    _mods, Path.Combine(_config.GamePath, "BepInEx", "plugins"));
                _folderCacheVersion = _modsVersion;
            }

            foreach (var g in _folderCache)
            {
                var node = GetFolderNode(g);

                // 关键字/状态筛选作用于文件行；目录节点在有可见成员（或自身命中）时保留
                var visibleFiles = g.Files.Where(f => FolderFileVisible(f, searching)).ToList();
                if (searching && visibleFiles.Count == 0 && !FolderNameMatch(g, _searchText))
                    continue;
                if (_statusFilter.Length > 0 && visibleFiles.Count == 0)
                    continue;

                node.GroupMembers = visibleFiles;      // 展开时显示的直接文件
                node.FolderAllFiles = g.AllFiles;      // 计数与批量开关按递归算
                node.GroupSize = g.AllFiles.Count;     // 显示递归总数（与 Alexx 管理器一致）
                candidates.Add(node);

                var expanded = _expandedGroups.Contains(g.Key) || searching;
                if (!expanded) continue;

                foreach (var f in visibleFiles)
                    candidates.Add(f);
            }
        }
        else
        {
            // ── 来源视图（更新视角）：组长 + 成员 ──
            // 筛选状态下强制展开所有组，否则组内成员匹配不到就永远看不见。
            foreach (var head in _groupCache)
            {
                candidates.Add(head);

                var expanded = head.GroupSize > 1 && head.GroupKey != null &&
                               (_expandedGroups.Contains(head.GroupKey) || searching);
                if (!expanded) continue;

                foreach (var member in head.GroupMembers
                    .Where(m => !ReferenceEquals(m, head))
                    .OrderBy(m => m.IsDuplicateFile)          // 正式文件在前，重复副本在后
                    .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
                {
                    candidates.Add(member);
                }
            }
        }

        // 3. 按关键字筛选（只过滤显示列表，_visible 仍与行一一对应）
        if (searching)
        {
            bool Match(ModInfo m) =>
                Contains(m.Name) || Contains(m.FileName) || Contains(m.PluginGuid) ||
                Contains(m.DataModCreator) || Contains(m.GroupName);

            bool Contains(string? s) =>
                !string.IsNullOrEmpty(s) &&
                s.Contains(_searchText, StringComparison.OrdinalIgnoreCase);

            var keep = new HashSet<ModInfo>(candidates.Where(Match));
            // 命中组内成员时把组长也留下，方便看出它属于哪个组
            foreach (var m in candidates.Where(Match)
                         .Where(m => !m.IsGroupHead && m.GroupOwner != null))
            {
                keep.Add(m.GroupOwner!);
            }
            // 文件夹视图：成员命中 → 目录节点保留
            foreach (var f in candidates.Where(Match)
                         .Where(m => !m.IsFolderNode && m.GroupOwner is { IsFolderNode: true }))
            {
                keep.Add(f.GroupOwner!);
            }
            candidates = candidates.Where(m => keep.Contains(m)).ToList();
        }

        // 3.5 状态筛选（找「启用的」「禁用的」「有更新的」不用肉眼扫）
        if (_statusFilter.Length > 0)
        {
            bool MatchState(ModInfo m)
            {
                // 目录节点的可见性已经在组装阶段随成员算过了，这里不再过滤
                if (m.IsFolderNode) return true;
                return _statusFilter switch
                {
                    "enabled" => m.IsEnabled,
                    "disabled" => !m.IsEnabled,
                    // 组员沿用组长结论：组长有更新，整组都算「待更新」
                    "update" => m.Status == UpdateStatus.UpdateAvailable ||
                                (m.GroupOwner?.Status == UpdateStatus.UpdateAvailable),
                    "nosource" => m.Status == UpdateStatus.NoSource,
                    "local" => m.Status == UpdateStatus.LocalMod,
                    "unknown" => m.Status == UpdateStatus.Unknown ||
                                 (m.GroupOwner?.Status == UpdateStatus.Unknown),
                    _ => true,
                };
            }
            candidates = candidates.Where(MatchState).ToList();
        }

        _visible = candidates;

        // 启用计数（与 Alexx 的管理器同款「X/Y 已启用」，放状态栏常驻）
        _toolStripEnabled.Text = $"{_mods.Count(m => m.IsEnabled)}/{_mods.Count} 已启用";

        // 4. 建行
        foreach (var mod in _visible)
        {
            var row = new DataGridViewRow();

            // ── 文件夹视图的目录节点：特殊渲染 ──
            if (mod.IsFolderNode)
            {
                var rel = mod.FolderRelPath;
                var depth = rel.Length == 0 ? 0 : rel.Count(c => c == '\\') + 1;
                var indent = new string(' ', depth * 3);
                var mark = _expandedGroups.Contains("dir:" + rel.ToLowerInvariant()) || searching
                    ? "▾" : "▸";
                var all = mod.FolderAllFiles;
                var en = all.Count(f => f.IsEnabled);

                var folderName = $"{indent}{mark} {mod.Name}  ({mod.GroupSize} 个文件, {en} 启用)";
                object triState = all.Count == 0 ? false
                    : en == all.Count ? true
                    : en == 0 ? false
                    : (object)null;   // 混合状态

                row.CreateCells(_grid,
                    triState,
                    folderName,
                    "（目录）",
                    "", "",
                    "📁 目录",
                    $"{en}/{all.Count} 启用",
                    "—");
                row.Tag = mod;
                _grid.Rows.Add(row);
                continue;
            }

            var isMember = !mod.IsGroupHead && mod.GroupOwner != null;
            // 组内成员不单独检查更新（与主条目同源、共享版本号），
            // 显示上沿用组长结论，避免一排「未检查」让人以为没查到
            var statusOwner = !folderMode && isMember && mod.IsEnabled ? (mod.GroupOwner ?? mod) : mod;

            // 名称列：组长带折叠标记，成员缩进
            string nameText;
            if (folderMode)
            {
                // 文件夹视图：一切文件平铺在自己目录下，**不嵌套来源分组**——
                // 否则目录树里又长出一棵来源树，两套层级叠在一起没人看得懂。
                // 来源的批量开关回「来源分组」视图去做。
                nameText = "└ " + mod.Name;
            }
            else if (mod.GroupSize > 1 && mod.IsGroupHead && mod.GroupKey != null)
            {
                var mark = (_expandedGroups.Contains(mod.GroupKey) || searching) ? "▾" : "▸";
                nameText = $"{mark} {mod.Name}  ({mod.GroupSize} 个文件)";
            }
            else if (isMember)
            {
                nameText = "    └ " + mod.Name;
            }
            else
            {
                nameText = mod.Name;
            }

            // 名称列尾标：数据 Mod / 同名文件（重复副本 vs 同名冲突是两回事）
            var tags = new List<string>();
            if (mod.IsDataMod) tags.Add("数据");
            if (mod.IsDuplicateFile) tags.Add("重复副本");
            if (mod.HasNameConflict) tags.Add("同名冲突");
            if (tags.Count > 0) nameText += "  〔" + string.Join("·", tags) + "〕";

            // 数据 Mod 没有 GUID，GUID 列改显示作者，信息量更大
            string guidText;
            if (mod.IsDataMod)
            {
                guidText = string.IsNullOrWhiteSpace(mod.DataModCreator)
                    ? "（数据 Mod）"
                    : "by " + mod.DataModCreator;
            }
            else
            {
                guidText = mod.PluginGuid.Length > 30
                    ? mod.PluginGuid[..27] + "..."
                    : mod.PluginGuid;
            }

            // 来源列：组内成员标出所属组名（同组标记）
            string sourceText;
            if (isMember)
                sourceText = mod.GroupName is { Length: > 0 } gn ? "↳ " + gn : "↳ 同组";
            else
                sourceText = mod.SourceText;

            // 当前版本列：重复副本之间要比出新旧（新→突出，旧→淡）
            var curVerText = mod.DisplayVersion;
            if (mod.IsDuplicateFile && mod.DuplicateOf != null)
            {
                var cmp = VersionHelper.ParseVersion(mod.DisplayVersion)
                          .CompareTo(VersionHelper.ParseVersion(mod.DuplicateOf.DisplayVersion));
                if (cmp < 0) curVerText += "（旧）";
                else if (cmp > 0) curVerText += "（新）";
            }

            row.CreateCells(_grid,
                mod.IsEnabled,
                nameText,
                guidText,
                curVerText,
                statusOwner.LatestVersion,
                sourceText,
                statusOwner.StatusText,
                GetActionText(mod));
            row.Tag = mod;
            _grid.Rows.Add(row);
        }

        // 5. 先排版、再恢复滚动位置，最后才允许重绘。
        //    顺序不能反：如果先 Refresh() 再设滚动索引，控件会先在「顶部」
        //    画一帧再跳回去 —— 用户看到的就是展开时往上闪一下。
        AlignGridHeight();

        if (focusMod != null)
        {
            var i = _visible.IndexOf(focusMod);
            if (i >= 0 && i < _grid.RowCount)
            {
                try
                {
                    _grid.FirstDisplayedScrollingRowIndex = i;
                    _grid.CurrentCell = _grid.Rows[i].Cells[ColName];
                }
                catch { }
            }
            return;
        }

        if (topMod != null && _grid.RowCount > 0)
        {
            var i = _visible.IndexOf(topMod);
            // 顶部条目被收进折叠组时，退回它所属的组长
            if (i < 0 && topMod.GroupOwner != null) i = _visible.IndexOf(topMod.GroupOwner);
            if (i >= 0)
            {
                try { _grid.FirstDisplayedScrollingRowIndex = i; } catch { }
            }
        }
    }

    /// <summary>当前行对应的 Mod（_visible 与网格行一一对应）</summary>
    private ModInfo? ModAtRow(int rowIndex) =>
        rowIndex >= 0 && rowIndex < _visible.Count ? _visible[rowIndex] : null;

    /// <summary>
    /// 根治「最后一行显示不全」：把表格可视区高度对齐到行高的整数倍。
    /// DataGridView 滚动按行单位，可视区不是整行倍数时最后一行只能显示一半且无法再滚。
    /// </summary>
    private void AlignGridHeight()
    {
        if (_grid.RowCount == 0) return;
        var rowH = _grid.Rows[0].Height;
        if (rowH <= 0) return;
        var usable = _grid.ClientSize.Height - _grid.ColumnHeadersHeight;
        var remainder = usable % rowH;
        if (remainder > 0 && _grid.Height - remainder >= 100)
            _grid.Height -= remainder;   // 裁掉不足一行的像素（SizeChanged 递归一次即收敛）
    }

    /// <summary>
    /// 操作列按钮文字。启用/禁用已经移到复选框列，
    /// 这一列只放真正的动作，避免一列里混四种语义。
    /// </summary>
    private static string GetActionText(ModInfo mod)
    {
        switch (mod.Status)
        {
            case UpdateStatus.UpdateAvailable: return "更新";
            case UpdateStatus.NoSource: return "配置";
            case UpdateStatus.Error:
            case UpdateStatus.UpdateFailed: return "重试";
            case UpdateStatus.Checking:
            case UpdateStatus.Updating: return "…";
        }
        return "—";
    }
    // ==================== 表格事件 ====================

    private void OnGridCellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _visible.Count) return;
        var mod = ModAtRow(e.RowIndex);
        if (mod == null) return;

        // 已禁用的行整行压暗 —— 状态列只负责更新状态，启用与否由复选框和行亮度表达
        if (!mod.IsEnabled && e.ColumnIndex != ColAction)
            e.CellStyle!.ForeColor = Color.FromArgb(105, 105, 110);

        switch (e.ColumnIndex)
        {
            case ColName:
                // 组内成员缩进显示为暗色；组长高亮；重复副本/同名冲突用警示色
                if (mod.IsFolderNode)
                    e.CellStyle!.ForeColor = Color.FromArgb(120, 205, 160);   // 目录节点：绿松色
                else if (mod.IsDuplicateFile)
                    e.CellStyle!.ForeColor = Theme.Warning;
                else if (mod.HasNameConflict)
                    e.CellStyle!.ForeColor = Color.FromArgb(190, 150, 255);
                else if (!folderModeActive && mod.GroupOwner != null && !mod.IsGroupHead)
                    e.CellStyle!.ForeColor = Theme.TextDim;
                else if (!folderModeActive && mod.GroupSize > 1 && mod.IsGroupHead)
                    e.CellStyle!.ForeColor = Color.FromArgb(150, 200, 255);
                break;

            case ColCurVer:
                // 重复副本之间比版本：旧版淡、比保留那份还新的突出
                if (mod.IsDuplicateFile && mod.DuplicateOf != null)
                {
                    var cmp = VersionHelper.ParseVersion(mod.DisplayVersion)
                              .CompareTo(VersionHelper.ParseVersion(mod.DuplicateOf.DisplayVersion));
                    e.CellStyle!.ForeColor = cmp < 0 ? Theme.TextDim
                                           : cmp > 0 ? Theme.Warning
                                           : Theme.Text;
                }
                break;

            case ColStatus:
            {
                // 目录节点显示「X/Y 启用」
                if (mod.IsFolderNode)
                {
                    e.CellStyle!.ForeColor = Theme.TextDim;
                    e.FormattingApplied = true;
                    break;
                }

                // 组内成员沿用组长的检查结论（它们共享同一来源与版本）
                var owner = mod.GroupOwner != null && !mod.IsGroupHead && mod.IsEnabled
                    ? mod.GroupOwner : mod;
                e.Value = owner.StatusText;
                e.CellStyle!.ForeColor = owner.Status switch
                {
                    UpdateStatus.UpToDate => Theme.Success,
                    UpdateStatus.UpdateAvailable => Theme.Warning,
                    UpdateStatus.Error or UpdateStatus.UpdateFailed => Theme.Error,
                    UpdateStatus.Disabled => Theme.TextDim,
                    UpdateStatus.LocalMod => Color.FromArgb(140, 120, 190),
                    UpdateStatus.Updated => Theme.Success,
                    _ => Theme.TextDim,
                };
                e.FormattingApplied = true;
                break;
            }

            case ColNewVer:
            {
                var owner = mod.GroupOwner != null && !mod.IsGroupHead && mod.IsEnabled
                    ? mod.GroupOwner : mod;
                if (!string.IsNullOrEmpty(owner.LatestVersion))
                {
                    e.CellStyle!.ForeColor = owner.HasUpdate ? Theme.Warning : Theme.TextDim;
                }
                break;
            }

            case ColSource:
                if (mod.IsFolderNode)
                    e.CellStyle!.ForeColor = Theme.TextDim;   // 目录节点来源列显示「📁 目录」
                else if (mod.IsDuplicateFile || mod.HasNameConflict)
                    e.CellStyle!.ForeColor = Theme.Warning;   // 重复副本/同名冲突提示
                break;

            case ColAction:
                e.Value = GetActionText(mod);
                // 三种动作三种颜色，一眼能分清要点的是什么
                e.CellStyle!.ForeColor = mod.Status switch
                {
                    UpdateStatus.UpdateAvailable => Theme.Accent,
                    UpdateStatus.NoSource => Theme.Warning,
                    UpdateStatus.Error or UpdateStatus.UpdateFailed => Theme.Error,
                    _ => Theme.TextDim,
                };
                break;
        }
    }

    private void OnGridCellContentClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _visible.Count) return;

        // 名称列：点击组长/目录节点切换折叠/展开
        if (e.ColumnIndex == ColName)
        {
            var m = _visible[e.RowIndex];
            var key = m.IsFolderNode ? "dir:" + m.FolderRelPath.ToLowerInvariant() : m.GroupKey;
            if (m.GroupSize > 0 && m.IsGroupHead && key != null)
            {
                if (!_expandedGroups.Add(key))
                    _expandedGroups.Remove(key);
                PopulateGrid();
            }
            return;
        }

        if (e.ColumnIndex == ColEnabled)
        {
            var m = ModAtRow(e.RowIndex);
            if (m == null) return;

            // 目录节点的复选框 = 递归开关整个目录（含所有子目录）
            if (m.IsFolderNode)
            {
                var all = m.FolderAllFiles.Where(f => !f.IsFolderNode).ToList();
                if (all.Count == 0) return;
                // 全启用 → 全禁用；否则 → 全启用（混合状态点一下也是全启用）
                ToggleMany(all, all.All(f => f.IsEnabled) ? false : true,
                    $"目录 {m.Name}");
                return;
            }

            // 来源组长的复选框 = 批量开关整组（含全部附属/副本文件）
            if (!folderModeActive && m.IsGroupHead && m.GroupSize > 1 &&
                m.GroupMembers is { Count: > 1 })
            {
                var all = m.GroupMembers.Where(f => !f.IsFolderNode).ToList();
                ToggleMany(all, !m.IsEnabled, $"组 {m.Name}");
                return;
            }

            // 复选框即开关。CellContentClick 触发时勾选还没提交，
            // 先 EndEdit 让 Value 反映点击后的状态，再据此切换
            _grid.EndEdit();
            var nowChecked = _grid.Rows[e.RowIndex].Cells[ColEnabled].Value as bool? ?? m.IsEnabled;
            if (nowChecked != m.IsEnabled)
                ToggleModEnabled(m, nowChecked);
            return;
        }

        if (e.ColumnIndex != ColAction) return;

        var mod = ModAtRow(e.RowIndex);
        if (mod == null) return;
        switch (mod.Status)
        {
            case UpdateStatus.UpdateAvailable:
                _ = UpdateSelectedAsync();
                return;
            case UpdateStatus.NoSource:
                // 无源优先引导配置
                ConfigureSource(e.RowIndex);
                return;
            case UpdateStatus.Error:
            case UpdateStatus.UpdateFailed:
                _ = CheckSingleAsync(e.RowIndex);
                return;
        }
        // 其余情况按钮显示「—」，点击不做任何事。
        // 启用/禁用已经移到最左边的复选框列，别让「—」也偷偷干别的。
    }

    /// <summary>右键菜单：切换选中行的启用状态</summary>
    private void ToggleSelectedMod()
    {
        var mod = ModAtRow(_grid.CurrentRow?.Index ?? -1);
        if (mod != null) ToggleModEnabled(mod);
    }

    /// <summary>批量启用/禁用选中的行（含组内成员，按 Mod 去重）</summary>
    private async Task ToggleSelectedBatchAsync(bool enable)
    {
        if (_grid.SelectedRows.Count == 0)
        {
            Log("没有选中任何行（按住 Ctrl 可多选）");
            return;
        }

        var targets = _grid.SelectedRows.Cast<DataGridViewRow>()
            .Select(r => r.Tag as ModInfo)
            .Where(m => m != null && m!.IsEnabled != enable)
            .Select(m => m!)
            .Distinct()
            .ToList();

        if (targets.Count == 0)
        {
            Log($"所选 {(_grid.SelectedRows.Count)} 行都已经是{(_grid.SelectedRows.Count > 0 ? (enable ? "启用" : "禁用") : "")}状态，无需切换");
            return;
        }

        // 就地刷新所有受影响的行 —— 不重建表格，排序和滚动位置保持不动
        ToggleMany(targets, enable, "所选 Mod");
        await Task.CompletedTask;
    }

    private void ToggleModEnabled(ModInfo mod) =>
        ToggleModEnabled(mod, !mod.IsEnabled);

    /// <summary>
    /// 启用 / 禁用单个 Mod —— BepInEx 的约定是「禁用 = 名字加 _disabled 后缀」。
    ///   · 普通插件：xxx.dll  ↔  xxx.dll_disabled
    ///   · 数据 Mod：整个文件夹改名（{Name} ↔ {Name}_disabled）
    /// 只改名字、不动内容，改完同步内存状态并就地刷新行，不必全盘重扫。
    /// 返回是否成功。
    /// </summary>
    private bool ToggleModEnabled(ModInfo mod, bool enable, bool quiet = false)
    {
        try
        {
            string oldPath, newPath;

            if (mod.IsDataMod)
            {
                var dir = Path.GetDirectoryName(mod.FlagFilePath);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                {
                    Log($"✗ {mod.Name}: 找不到数据 Mod 文件夹，无法切换");
                    UpdateModRow(mod);   // 让复选框弹回真实状态
                    return false;
                }
                oldPath = dir;
                newPath = AltDisabledName(dir);   // 自动翻转 _disabled 后缀
                if (Directory.Exists(newPath))
                {
                    // 目标被同名的禁用副本占位 → 尝试移入隔离区让路
                    if (!TryClearBlockedTarget(mod, newPath, enable, quiet)) return false;
                }
                Directory.Move(oldPath, newPath);
                mod.FolderPath = newPath;
                mod.FlagFilePath = Path.Combine(newPath, Path.GetFileName(mod.FlagFilePath));
                mod.FilePath = mod.FlagFilePath;
            }
            else
            {
                oldPath = mod.FilePath;
                if (string.IsNullOrEmpty(oldPath) || !File.Exists(oldPath))
                {
                    Log($"✗ {mod.Name}: 找不到文件，无法切换");
                    UpdateModRow(mod);
                    return false;
                }
                newPath = AltDisabledName(oldPath);
                if (File.Exists(newPath))
                {
                    // 目标被同名的禁用副本占位 → 尝试移入隔离区让路
                    if (!TryClearBlockedTarget(mod, newPath, enable, quiet)) return false;
                }
                File.Move(oldPath, newPath);
                mod.FilePath = newPath;
            }

            mod.FileName = Path.GetFileName(newPath);
            mod.IsEnabled = enable;

            // 状态跟着启用与否走。
            // ⚠️ 重新启用时恢复「禁用前的检查结论」—— 否则会出现
            // 「最新版本列有 2.2.10、状态却写未检查」的矛盾显示，
            // 用户明明刚查过，还得再点一次检查更新。
            if (!enable)
            {
                if (mod.Status != UpdateStatus.Disabled)
                    mod.StatusBeforeDisable = mod.Status;
                mod.Status = UpdateStatus.Disabled;
            }
            else
            {
                var restore = mod.StatusBeforeDisable;
                if (restore is UpdateStatus.UpToDate or UpdateStatus.UpdateAvailable
                    or UpdateStatus.Error or UpdateStatus.UpdateFailed or UpdateStatus.Unknown
                    or UpdateStatus.Updated)
                {
                    mod.Status = restore;   // 检查结论还有效，直接恢复
                }
                else if (mod.Source != null && mod.Source.IsValid)
                {
                    mod.Status = UpdateStatus.NotChecked;
                }
                else if (Services.CasualtiesManageableService.IsLocalMod(mod.FileName, mod.PluginGuid))
                {
                    mod.Status = UpdateStatus.LocalMod;
                }
                else
                {
                    mod.Status = UpdateStatus.NoSource;
                }
            }

            // ⚠️ 不要全量重建表格 —— 重建会重排序（启用优先）并把视口拉走，
            // 用户在展开的组里单独开关一个文件时，整个列表会跳。
            // 只就地更新这一行的复选框 / 状态 / 颜色。
            // _modsVersion 照常自增：下次全量刷新（检查更新、扫描）时分组会重算。
            _modsVersion++;
            UpdateModRow(mod);
            if (folderModeActive)
            {
                // 刷新所在目录节点（可能多层嵌套，逐层刷新）
                var dir = Path.GetDirectoryName(mod.IsDataMod ? mod.FlagFilePath : mod.FilePath);
                var root = Path.Combine(_config.GamePath, "BepInEx", "plugins");
                var rel = Path.GetRelativePath(root, dir ?? root).Replace('/', '\\');
                if (rel == ".") rel = "";
                var prefix = rel.Length == 0 ? "" : rel + "\\";
                foreach (var n in _folderNodes.Values.Where(n =>
                    _visible.Contains(n) &&
                    (n.FolderRelPath == rel ||
                     (n.FolderRelPath.Length > 0 && rel.StartsWith(n.FolderRelPath + "\\", StringComparison.OrdinalIgnoreCase)))))
                {
                    UpdateModRow(n);
                }
            }
            UpdateEnabledCounter();
            if (!quiet)
                Log($"{(mod.IsEnabled ? "✓ 已启用" : "⊘ 已禁用")} {mod.Name}  ({mod.FileName})");
            return true;
        }
        catch (Exception ex)
        {
            Log($"✗ {mod.Name} 切换失败: {ex.Message}");
            try { UpdateModRow(mod); } catch { }   // 复选框弹回真实状态
            return false;
        }
    }

    /// <summary>刷新状态栏的「X/Y 已启用」计数（开关后立即更新，不等下次重建）</summary>
    private void UpdateEnabledCounter() =>
        _toolStripEnabled.Text = $"{_mods.Count(m => m.IsEnabled)}/{_mods.Count} 已启用";

    /// <summary>
    /// 就地刷新某个 Mod 所在的行（复选框、状态、最新版本），不重排、不滚动。
    /// 颜色由 CellFormatting 在重绘时按最新状态重算。
    /// 目录节点则重算三态复选框与「X/Y 启用」。
    /// </summary>
    private void UpdateModRow(ModInfo mod)
    {
        var i = _visible.IndexOf(mod);
        if (i < 0 || i >= _grid.RowCount) return;

        // 目录节点：三态复选框 + 启用计数（按递归文件集算）
        if (mod.IsFolderNode)
        {
            var all = mod.FolderAllFiles.Where(f => !f.IsFolderNode).ToList();
            var en = all.Count(f => f.IsEnabled);
            object v = all.Count == 0 ? false
                     : en == all.Count ? true
                     : en == 0 ? false
                     : (object)null;
            _grid[ColEnabled, i].Value = v;
            _grid[ColStatus, i].Value = $"{en}/{all.Count} 启用";
            _grid.InvalidateRow(i);
            return;
        }

        _grid[ColEnabled, i].Value = mod.IsEnabled;

        // 来源视图里组内成员沿用组长结论（与 PopulateGrid 的口径一致）
        var owner = !folderModeActive && !mod.IsGroupHead && mod.GroupOwner != null && mod.IsEnabled
            ? mod.GroupOwner : mod;
        _grid[ColStatus, i].Value = owner.StatusText;
        _grid[ColNewVer, i].Value = owner.LatestVersion;
        _grid[ColAction, i].Value = GetActionText(mod);

        _grid.InvalidateRow(i);
    }

    private bool folderModeActive =>
        string.Equals(_config.GroupingView, "folder", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 批量开关一组文件（目录节点递归开关 / 来源组开关 / 右键多选共用）。
    /// 就地刷新受影响行 + 所属目录节点 + 计数器，只写汇总日志。
    /// </summary>
    private void ToggleMany(List<ModInfo> files, bool enable, string label)
    {
        var targets = files.Where(f => f.IsEnabled != enable).ToList();
        if (targets.Count == 0)
        {
            Log($"{label}: 全部已是{(enable ? "启用" : "禁用")}状态，无需切换");
            return;
        }

        // 禁用前预检：目标被旧的禁用副本占用时，一次征询、整批放行
        _quarantineAllowed = false;
        if (!enable)
        {
            var blockers = new List<string>();
            foreach (var t in targets)
            {
                var p = t.IsDataMod ? Path.GetDirectoryName(t.FlagFilePath) : t.FilePath;
                if (string.IsNullOrEmpty(p)) continue;
                var np = AltDisabledName(p);
                var occupied = t.IsDataMod ? Directory.Exists(np) : File.Exists(np);
                if (occupied) blockers.Add(np);
            }
            blockers = blockers.Distinct().ToList();
            if (blockers.Count > 0)
            {
                if (!ConfirmQuarantine(blockers))
                {
                    Log($"已取消：{blockers.Count} 个旧副本挡路（未移动任何文件）。" +
                        "提示：这些副本标着〔重复副本〕/〔同名冲突〕，删掉或手动移走后就能正常开关");
                    return;
                }
                _quarantineAllowed = true;
            }
        }

        int ok = 0;
        var failed = new List<ModInfo>();
        try
        {
            foreach (var m in targets)
            {
                if (ToggleModEnabled(m, enable, quiet: true))
                {
                    ok++;
                    UpdateModRow(m);
                }
                else
                {
                    failed.Add(m);
                }
            }
        }
        finally
        {
            _quarantineAllowed = false;
        }

        // 刷新受影响的目录节点（文件可能分属多个目录）
        if (folderModeActive)
        {
            foreach (var n in _folderNodes.Values.Where(n => _visible.Contains(n)))
                UpdateModRow(n);
        }
        UpdateEnabledCounter();

        var failNote = failed.Count > 0
            ? $"，{failed.Count} 个跳过——目标文件已存在（同目录有同名的启用/禁用残留副本挡路，先在「重复副本」标记里清理旧副本）"
            : "";
        Log($"✓ {label}: {ok} 个已{(enable ? "启用" : "禁用")}{failNote}");
        if (failed.Count > 0)
            foreach (var f in failed.Take(5))
                Log($"    ↳ {f.Name} ({Path.GetFileName(f.FilePath)})");
    }

    /// <summary>在路径末尾加上/去掉 _disabled 后缀</summary>
    private static string AltDisabledName(string path) =>
        path.EndsWith("_disabled", StringComparison.OrdinalIgnoreCase)
            ? path[..^"_disabled".Length]
            : path + "_disabled";

    /// <summary>批量开关前经用户确认后放行隔离（避免每个文件弹一次窗）</summary>
    private bool _quarantineAllowed;

    /// <summary>禁用目标被旧副本占用时，征询是否把旧副本移入隔离区</summary>
    private bool ConfirmQuarantine(IEnumerable<string> blockers)
    {
        var list = string.Join("\n", blockers.Select(b => "  " + Path.GetFileName(b)));
        return MessageBox.Show(
            $"以下旧的禁用副本挡住了开关操作（它们本来就不会被游戏加载）：\n\n{list}\n\n" +
            $"把它们移到隔离区 BepInEx\\.cleanup\\ 吗？\n（不删除，随时可手动移回）",
            "清理挡路的旧副本", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
    }

    /// <summary>
    /// 把挡路的旧副本移出插件目录（BepInEx 不扫描 .cleanup）。
    /// 只移动「已禁用」的文件/文件夹——绝不碰启用中的文件。
    /// </summary>
    private void QuarantinePath(string path)
    {
        var dir = Path.Combine(_config.GamePath, "BepInEx", ".cleanup",
            DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, Path.GetFileName(path));
        var i = 1;
        while (Directory.Exists(dest) || File.Exists(dest))
            dest = Path.Combine(dir,
                $"{Path.GetFileNameWithoutExtension(path)}_{i++}{Path.GetExtension(path)}");

        if (Directory.Exists(path)) Directory.Move(path, dest);
        else File.Move(path, dest);

        // 内存状态同步（下次扫描后该条目自然消失）
        var owner = _mods.FirstOrDefault(x => !x.IsFolderNode &&
            (string.Equals(x.FilePath, path, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(x.FlagFilePath, path, StringComparison.OrdinalIgnoreCase)));
        if (owner != null)
        {
            owner.FilePath = dest;
            owner.FlagFilePath = dest;
            owner.FileName = Path.GetFileName(dest);
            owner.IsEnabled = false;
            owner.Status = UpdateStatus.Disabled;
        }

        Log($"♻ 旧副本 {Path.GetFileName(path)} → BepInEx\\.cleanup\\（不删除，可随时移回）");
    }

    /// <summary>禁用目标被旧副本占用时尝试清障；返回 false = 用户取消或不能动</summary>
    private bool TryClearBlockedTarget(ModInfo mod, string newPath, bool enable, bool quiet)
    {
        if (enable)
        {
            // 启用目标被「启用中」的同名文件占用 —— 绝不自动动启用中的文件
            Log($"✗ {mod.Name}: 启用目标已存在（{Path.GetFileName(newPath)}）——同名副本正在占用，先禁用或清理它");
            UpdateModRow(mod);
            return false;
        }

        // 禁用时目标被「禁用副本」占位 → 移入隔离区让路
        if (_quarantineAllowed)
            return true;

        if (quiet)
        {
            Log($"✗ {mod.Name}: 目标文件已存在（{Path.GetFileName(newPath)}）——未获隔离授权，跳过");
            UpdateModRow(mod);
            return false;
        }

        if (!ConfirmQuarantine(new[] { newPath }))
        {
            Log($"✗ {mod.Name}: 已取消（目标 {Path.GetFileName(newPath)} 被旧副本占用）");
            UpdateModRow(mod);
            return false;
        }
        QuarantinePath(newPath);
        return true;
    }

    private async Task CheckSingleAsync(int rowIndex)
    {
        if (_updater == null) return;
        var mod = ModAtRow(rowIndex);
        if (mod == null) return;
        if (mod.Source == null || !mod.Source.IsValid)
        {
            ConfigureSource(rowIndex);
            return;
        }
        await _updater.CheckUpdateAsync(mod, mod.Source);
        PopulateGrid();
    }

    private void OnGridCellDoubleClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _visible.Count) return;
        var mod = ModAtRow(e.RowIndex);
        if (mod == null) return;

        // 双击显示详细信息
        var info = $"Mod 名称: {mod.Name}\n" +
                   $"GUID: {mod.PluginGuid}\n" +
                   $"当前版本: {mod.DisplayVersion}\n" +
                   $"最新版本: {mod.LatestVersion}\n" +
                   $"来源: {mod.SourceText}\n" +
                   $"状态: {mod.StatusText}\n" +
                   $"文件: {mod.FileName}\n" +
                   $"路径: {mod.FilePath}\n" +
                   $"大小: {mod.FileSize / 1024.0:F1} KB\n" +
                   $"修改时间: {mod.LastModified:yyyy-MM-dd HH:mm}\n" +
                   $"启用: {(mod.IsEnabled ? "是" : "否")}";
        if (mod.IsDataMod)
            info += $"\n类型: 数据 Mod（前置 ICEnecoCustomItemAPI 读取 JSON 生成物品）" +
                    $"\n作者: {mod.DataModCreator}" +
                    $"\n标识文件: {mod.FlagFilePath}";
        if (mod.GroupOwner != null && !mod.IsGroupHead)
            info += $"\n所属组: {mod.GroupName}（同组主条目: {mod.GroupOwner.Name}）";
        if (mod.IsDuplicateFile)
        {
            info += "\n\n⚠ 重复副本：同一个插件（GUID 相同）在插件目录里存在多份，" +
                    "BepInEx 会重复加载，可能造成 GUID 冲突。建议删掉其中一份。";
            if (mod.DuplicateOf != null)
                info += $"\n保留的那一份: {mod.DuplicateOf.FilePath}";
        }
        if (mod.HasNameConflict)
        {
            info += "\n\n⚠ 同名冲突：两个**不同的插件**（GUID 不同）用了同一个文件名。" +
                    "这不是重复安装——有些 mod 会刻意自带修改过的同名 dll，" +
                    "具体看 mod 说明（典型：Gunsaw Genetics 自带 body_sprite_replacer.dll，" +
                    "要求覆盖原版精灵替换器）。";
            if (mod.ConflictWith != null)
                info += $"\n另一个同名文件: {mod.ConflictWith.FilePath}" +
                        $"\n　（GUID: {mod.ConflictWith.PluginGuid}）";
        }
        if (mod.Source != null && mod.Source.IsValid)
            info += $"\n来源详情: {(mod.Source.IsGitHub ? $"github.com/{mod.Source.Owner}/{mod.Source.Repo}" : $"nexusmods.com/mods/{mod.Source.ModId}")}";
        if (!string.IsNullOrEmpty(mod.LastError))
            info += $"\n错误: {mod.LastError}";

        // 元数据里能拿到的「冲突相关」信息：最后更新时间 / 依赖声明 / 目标版本更新日志。
        // 介绍页面经常没人维护，这些才是判断「更新了会不会出事」的依据。
        var meta = ResolveMeta(mod);
        if (meta != null)
        {
            // 整合时最容易踩坑的信息：mod 到底是什么、谁写的、简介说的什么
            if (!string.IsNullOrWhiteSpace(meta.Author))
                info += $"\n作者: {meta.Author}";
            if (!string.IsNullOrWhiteSpace(meta.Summary))
                info += $"\n简介: {meta.Summary.Trim()}";
            if (DateTime.TryParse(meta.LastUpdated, out var lu))
                info += $"\nN 网最后更新: {lu:yyyy-MM-dd}（{(DateTime.Now - lu).TotalDays:F0} 天前）";
            var deps = Services.CasualtiesManageableService.DescribeDependencies(meta);
            if (deps.Length > 0)
                info += $"\n声明依赖: {deps}";
            var log = meta.ChangelogFor(mod.HasUpdate ? mod.LatestVersion : meta.Version);
            if (!string.IsNullOrWhiteSpace(log))
                info += $"\n\n=== 更新日志（{mod.LatestVersion}）===\n{log.Trim()}";
        }
        if (!string.IsNullOrEmpty(mod.ReleaseNotes))
            info += $"\n\n=== 发布说明 ===\n{mod.ReleaseNotes}";

        // ⚠️ WinForms 的 TextBox 只认 \r\n，孤一个 \n 显示时会被吞掉
        //（复制到剪贴板反而正常）—— 必须统一成 \r\n 再喂给它
        info = info.Replace("\r\n", "\n").Replace("\n", "\r\n");

        var dlg = new Form
        {
            Text = mod.Name + " - 详细信息",
            Size = new Size(500, 420),
            StartPosition = FormStartPosition.CenterParent,
            BackColor = Theme.Bg,
            ForeColor = Theme.Text,
        };
        var tb = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            BackColor = Theme.Bg,
            ForeColor = Theme.Text,
            Text = info,
            ScrollBars = ScrollBars.Vertical,
            BorderStyle = BorderStyle.None,
        };
        dlg.Controls.Add(tb);
        dlg.ShowDialog(this);
    }

    // ==================== 设置 ====================

    private void OpenSettings()
    {
        var dlg = new SettingsForm(_config);
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _config = dlg.Config;
            ConfigManager.Save(_config);
            InitServices();
            _gamePathLabel.Text = "游戏路径: " + _config.GamePath;
            _gamePathLabel.ForeColor = Theme.TextDim;
            if (_config.AutoScanOnStart && _scanner != null)
                _ = ScanAsync();
        }
    }

    // ==================== 配置来源 ====================

    private void ConfigureSource(int? rowIndex = null)
    {
        var idx = rowIndex ?? (_grid.CurrentRow?.Index ?? -1);
        if (idx < 0 || idx >= _visible.Count) return;
        var mod = ModAtRow(idx);
        if (mod == null) return;

        var dlg = new ModSourceDialog(mod, _config.NexusGameDomain);
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            ConfigManager.SetSource(_config, mod, dlg.Source);
            mod.Source = dlg.Source;
            ConfigManager.Save(_config);
            PopulateGrid();
            Log($"已配置 {mod.Name} 的来源: {dlg.Source.Type}");
        }
    }

    // ==================== GitHub 仓库一键解析 ====================

    /// <summary>
    /// 右键 → GitHub 仓库解析：输入 owner/repo（或完整链接），自动验证仓库并拉最新 Release，
    /// 直接配置为更新源（对齐 QoL Unknown 的 GitHub releases 更新模式）
    /// </summary>
    private async Task ResolveFromGithubAsync()
    {
        if (_grid.CurrentRow == null) return;
        var mod = ModAtRow(_grid.CurrentRow?.Index ?? -1);
        if (mod == null) return;

        using var dlg = new GitHubResolveDialog(mod);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        var (owner, repo) = ParseGitHubRepo(dlg.RepoText);
        if (owner == null || repo == null)
        {
            MessageBox.Show("请输入 owner/repo 或完整的 GitHub 链接。\n例如: jimmyking9999999/QoL-Unknown",
                "格式错误", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _toolStripStatus.Text = $"正在解析 github.com/{owner}/{repo}...";
        ShowProgress(true);
        _toolStripProgress.Style = ProgressBarStyle.Marquee;

        try
        {
            using var github = new GitHubBrowser(
                string.IsNullOrEmpty(_config.GitHubToken) ? null : _config.GitHubToken);
            var (exists, release) = await github.ResolveRepoAsync(owner, repo);

            if (!exists)
            {
                _toolStripStatus.Text = "仓库解析失败";
                MessageBox.Show($"仓库 github.com/{owner}/{repo} 不存在或无权访问。",
                    "解析失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // 配置 GitHub 来源（release 有 asset 时自动填资源匹配模式）
            var source = new ModSource { Type = "github", Owner = owner, Repo = repo };
            if (release != null && !string.IsNullOrEmpty(release.AssetName))
                source.AssetPattern = release.AssetName;

            ConfigManager.SetSource(_config, mod, source);
            mod.Source = source;
            if (release != null && !string.IsNullOrEmpty(release.Version))
            {
                mod.LatestVersion = release.Version;
                mod.DownloadUrl = release.DownloadUrl;
                mod.AssetName = release.AssetName;
            }
            ConfigManager.Save(_config);
            PopulateGrid();

            var verInfo = release != null && !string.IsNullOrEmpty(release.Version)
                ? $"（最新 {release.Version}）" : "（仓库无 Release）";
            Log($"✓ {mod.Name} → github.com/{owner}/{repo} {verInfo}");
            _toolStripStatus.Text = $"已配置 {mod.Name} 的 GitHub 来源 {verInfo}";
        }
        catch (Exception ex)
        {
            Log($"GitHub 解析失败: {ex.Message}");
            _toolStripStatus.Text = "解析失败";
            MessageBox.Show($"解析失败:\n{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            ShowProgress(false);
        }
    }

    /// <summary>
    /// 解析用户输入为 owner/repo（支持 owner/repo、完整 URL、带 /releases 后缀等）
    /// </summary>
    private static (string? owner, string? repo) ParseGitHubRepo(string input)
    {
        var s = input.Trim();
        if (string.IsNullOrEmpty(s)) return (null, null);

        // 剥离 github.com/ 前缀（支持完整链接）
        var idx = s.IndexOf("github.com/", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0) s = s[(idx + "github.com/".Length)..];
        s = s.TrimEnd('/');
        var parts = s.Split('/');
        if (parts.Length < 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
            return (null, null);
        return (parts[0], parts[1]);
    }

    /// <summary>
    /// GitHub 仓库解析输入框（深色主题小对话框）
    /// </summary>
    private sealed class GitHubResolveDialog : Form
    {
        public string RepoText => _box.Text.Trim();
        private readonly TextBox _box = new();

        public GitHubResolveDialog(ModInfo mod)
        {
            Text = $"解析 GitHub 仓库 - {mod.Name}";
            Size = new Size(480, 190);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = new Font("Microsoft YaHei UI", 9F);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;

            var label = new Label
            {
                Text = "GitHub 仓库（owner/repo 或完整链接）:",
                Location = new Point(16, 14),
                AutoSize = true,
                ForeColor = Theme.Text,
            };

            _box.Location = new Point(16, 40);
            _box.Size = new Size(440, 25);
            _box.BackColor = Theme.GridBg;
            _box.ForeColor = Theme.Text;
            _box.BorderStyle = BorderStyle.FixedSingle;
            if (mod.Source?.IsGitHub == true)
                _box.Text = $"{mod.Source.Owner}/{mod.Source.Repo}";

            var hint = new Label
            {
                Text = "例如: jimmyking9999999/QoL-Unknown 或 https://github.com/jimmyking9999999/QoL-Unknown",
                Location = new Point(16, 72),
                AutoSize = true,
                ForeColor = Theme.TextDim,
            };

            var ok = new Button { Text = "解析并配置", Location = new Point(250, 110), Size = new Size(100, 32) };
            ok.FlatStyle = FlatStyle.Flat;
            ok.FlatAppearance.BorderColor = Theme.Border;
            ok.BackColor = Theme.Accent;
            ok.ForeColor = Theme.Text;
            ok.Cursor = Cursors.Hand;
            ok.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };

            var cancel = new Button { Text = "取消", Location = new Point(360, 110), Size = new Size(90, 32) };
            cancel.FlatStyle = FlatStyle.Flat;
            cancel.BackColor = Theme.PanelBg;
            cancel.ForeColor = Theme.Text;
            cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };

            Controls.Add(label);
            Controls.Add(_box);
            Controls.Add(hint);
            Controls.Add(ok);
            Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;
        }
    }

    // ==================== 备份与回滚 ====================

    private void ShowBackups()
    {
        if (_grid.CurrentRow == null) return;
        var mod = ModAtRow(_grid.CurrentRow?.Index ?? -1);
        if (mod == null) return;
        if (_updater == null) return;

        var backups = _updater.GetBackups(mod.FileName);
        if (backups.Count == 0)
        {
            MessageBox.Show($"{mod.Name} 没有备份文件。", "提示");
            return;
        }

        var dlg = new Form
        {
            Text = $"{mod.Name} - 备份列表",
            Size = new Size(450, 350),
            StartPosition = FormStartPosition.CenterParent,
            BackColor = Theme.Bg,
            ForeColor = Theme.Text,
        };
        var lv = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            BackColor = Theme.GridBg,
            ForeColor = Theme.Text,
        };
        lv.Columns.Add("版本", 100);
        lv.Columns.Add("备份时间", 150);
        lv.Columns.Add("大小", 80);
        foreach (var b in backups)
            lv.Items.Add(new ListViewItem(new[] { b.Version, b.DisplayDate, b.DisplaySize }));

        var btnRestore = new Button
        {
            Text = "回滚到选中版本",
            Dock = DockStyle.Bottom,
            Height = 35,
        };
        StyleButton(btnRestore, Theme.Warning);
        btnRestore.Click += (_, _) =>
        {
            if (lv.SelectedIndices.Count == 0) return;
            var sel = backups[lv.SelectedIndices[0]];
            if (MessageBox.Show($"确定回滚 {mod.Name} 到版本 {sel.Version}？",
                "确认", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                if (_updater.Rollback(mod, sel))
                {
                    PopulateGrid();
                    Log($"已回滚 {mod.Name} 到 {sel.Version}");
                    dlg.Close();
                }
            }
        };
        dlg.Controls.Add(lv);
        dlg.Controls.Add(btnRestore);
        dlg.ShowDialog(this);
    }

    private void RollbackFromBackup()
    {
        ShowBackups();
    }

    // ==================== 其他操作 ====================

    private void OnBrowseClick(object? sender, EventArgs e)
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "选择 Casualties Unknown Demo 游戏目录",
            SelectedPath = _config.GamePath,
        };
        if (dlg.ShowDialog() == DialogResult.OK)
        {
            if (PluginScanner.IsValidGamePath(dlg.SelectedPath))
            {
                _config.GamePath = dlg.SelectedPath;
                ConfigManager.Save(_config);
                InitServices();
                _gamePathLabel.Text = "游戏路径: " + _config.GamePath;
                _gamePathLabel.ForeColor = Theme.TextDim;
                _ = ScanAsync();
            }
            else
            {
                MessageBox.Show(
                    "该路径未找到 CasualtiesUnknown.exe 或 BepInEx 目录。\n" +
                    "请确认选择了正确的游戏安装目录。",
                    "路径无效", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }

    private void OpenInBrowser()
    {
        if (_grid.CurrentRow == null) return;
        var mod = ModAtRow(_grid.CurrentRow.Index);
        if (mod == null) return;
        // 组内成员跳到组长（同一个来源页面）
        if (mod.GroupOwner != null) mod = mod.GroupOwner;
        if (mod.Source == null) return;

        string? url = mod.Source switch
        {
            { IsGitHub: true } => $"https://github.com/{mod.Source.Owner}/{mod.Source.Repo}/releases",
            { IsNexus: true } => $"https://www.nexusmods.com/games/{_config.NexusGameDomain}/mods/{mod.Source.ModId}",
            { IsGameBanana: true } => $"https://gamebanana.com/mods/{mod.Source.ModId}",
            _ => null
        };

        if (url != null)
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    /// <summary>
    /// 在 GitHub 上搜索该 Mod（用名称 + GUID）
    /// </summary>
    private void SearchGitHub()
    {
        if (_grid.CurrentRow == null) return;
        var mod = ModAtRow(_grid.CurrentRow?.Index ?? -1);
        if (mod == null) return;

        // 优先用作者+仓库名（如果已知），否则用 Mod 名称搜索
        string query;
        if (!string.IsNullOrEmpty(mod.Source?.Owner) && !string.IsNullOrEmpty(mod.Source?.Repo))
        {
            query = $"{mod.Source.Owner}/{mod.Source.Repo}";
        }
        else if (!string.IsNullOrEmpty(mod.PluginGuid))
        {
            query = $"{mod.PluginGuid} casualties unknown bepinex";
        }
        else
        {
            query = $"{mod.Name} casualties unknown mod bepinex";
        }

        var url = $"https://github.com/search?q={Uri.EscapeDataString(query)}&type=repositories";
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        Log($"已在浏览器打开 GitHub 搜索: {query}");
    }

    /// <summary>
    /// 从 BepInEx 配置文件自动发现仓库地址
    /// 某些 Mod 会在 cfg 里写 Repo Path 等字段
    /// </summary>
    private void DiscoverFromBepInExConfig(ModInfo mod)
    {
        if (string.IsNullOrEmpty(mod.PluginGuid)) return;

        var configPath = Path.Combine(_config.GamePath, "BepInEx", "config", $"{mod.PluginGuid}.cfg");
        if (!File.Exists(configPath)) return;

        try
        {
            var content = File.ReadAllText(configPath);
            // 查找 Repo Path = xxx/yyy 格式
            var match = System.Text.RegularExpressions.Regex.Match(
                content, @"(?im)^\s*Repo\s*Path\s*=\s*([\w\-./]+)\s*$");
            if (match.Success)
            {
                var path = match.Groups[1].Value.Trim();
                var parts = path.Split('/', 2);
                if (parts.Length == 2)
                {
                    var source = new ModSource
                    {
                        Type = "github",
                        Owner = parts[0],
                        Repo = parts[1],
                    };
                    ConfigManager.SetSource(_config, mod, source);
                    mod.Source = source;
                    Log($"✓ 从 BepInEx 配置发现: {mod.Name} → {path}");
                }
            }
        }
        catch { }
    }

    private void CopyGuid()
    {
        if (_grid.CurrentRow == null) return;
        var mod = ModAtRow(_grid.CurrentRow?.Index ?? -1);
        if (mod == null) return;
        if (!string.IsNullOrEmpty(mod.PluginGuid))
        {
            Clipboard.SetText(mod.PluginGuid);
            _toolStripStatus.Text = $"已复制 GUID: {mod.PluginGuid}";
        }
    }

    private void OpenFileLocation()
    {
        if (_grid.CurrentRow == null) return;
        var mod = ModAtRow(_grid.CurrentRow?.Index ?? -1);
        if (mod == null) return;
        if (File.Exists(mod.FilePath))
        {
            Process.Start(new ProcessStartInfo("explorer.exe",
                $"/select,\"{mod.FilePath}\"") { UseShellExecute = true });
        }
    }

    /// <summary>按来源解析出这条 mod 对应的元数据条目（拿不到返回 null）</summary>
    private static Services.CasualtiesManageableService.CmModEntry? ResolveMeta(ModInfo mod)
    {
        var modId = mod.Source?.IsNexus == true ? mod.Source.ModId : null;
        return Services.CasualtiesManageableService.FindByModId(modId)
            ?? Services.CasualtiesManageableService.FindByGuidCached(mod.PluginGuid);
    }

    /// <summary>
    /// 查看更新日志。
    /// mod 之间的兼容/冲突说明基本只写在更新日志里，介绍页面经常没人维护
    /// （例：#321 的 1.0.1 写着「兼容 KrokMP V4.0.1」——这种信息不在日志里根本看不到）。
    /// </summary>
    private void ShowChangelog()
    {
        var mod = ModAtRow(_grid.CurrentRow?.Index ?? -1);
        if (mod == null) return;

        var meta = ResolveMeta(mod);
        if (meta == null || meta.Changelogs.Count == 0)
        {
            MessageBox.Show(
                mod.Source?.IsGitHub == true
                    ? "GitHub 来源的 mod 请用「🌐 在浏览器中打开」查看 Releases 页面。"
                    : "元数据里没有这个 mod 的更新日志。\n可以右键「🌐 在浏览器中打开」去 N 网页面看。",
                "没有更新日志", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"【{meta.Name}】  by {meta.Author}   N 网 #{meta.NexusModId}");
        if (DateTime.TryParse(meta.LastUpdated, out var lu))
            sb.AppendLine($"最后更新: {lu:yyyy-MM-dd HH:mm}");
        var deps = Services.CasualtiesManageableService.DescribeDependencies(meta);
        if (deps.Length > 0)
            sb.AppendLine($"声明依赖: {deps}");
        sb.AppendLine();

        foreach (var c in meta.Changelogs)
        {
            sb.AppendLine($"── 版本 {c.Version} ".PadRight(60, '─'));
            sb.AppendLine(c.Text?.Trim());
            sb.AppendLine();
        }

        var dlg = new Form
        {
            Text = $"{mod.Name} - 更新日志",
            Size = new Size(640, 560),
            StartPosition = FormStartPosition.CenterParent,
            BackColor = Theme.Bg,
            ForeColor = Theme.Text,
            Font = new Font("Microsoft YaHei UI", 9F),
        };
        var tb = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            BackColor = Theme.Bg,
            ForeColor = Theme.Text,
            Font = new Font("Consolas", 9F),
            // 元数据里的日志文本带 \n，TextBox 只认 \r\n，不转的话整段挤成一行
            Text = sb.ToString().Replace("\r\n", "\n").Replace("\n", "\r\n"),
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            BorderStyle = BorderStyle.None,
        };
        dlg.Controls.Add(tb);
        dlg.ShowDialog(this);
    }

    private void OnDownloadProgress(ModInfo mod, long received, long total)
    {
        this.BeginInvoke(() =>
        {
            if (total > 0)
            {
                ShowProgress(true);
                SetProgress((int)(received * 100 / total), 100);
                _toolStripStatus.Text = $"下载 {mod.Name}: {received / 1024.0 / 1024.0:F1}MB / {total / 1024.0 / 1024.0:F1}MB";
            }
        });
    }

    // ==================== 目录结构体检 ====================

    /// <summary>
    /// 在日志里给出插件目录的结构分布。
    ///
    /// 背景：整包压缩包解压进 plugins 后，常会出现一个顶层文件夹塞着
    /// 十几个 mod 文件夹的情况（例：Gameplay\ 下 22 个子文件夹）。
    /// BepInEx 5 会**递归**扫描 plugins，所以这些 mod 是会被加载的——
    /// 「读不到」不用担心；真正要留意的是同名副本与覆盖关系。
    /// 这里只把结构摆出来给用户看，**绝不自动移动或删除任何文件**。
    /// </summary>
    private void LogDirectoryLayout()
    {
        try
        {
            var pluginsRoot = Path.Combine(_config.GamePath, "BepInEx", "plugins");
            if (!Directory.Exists(pluginsRoot)) return;

            // 顶层子文件夹 → 其中的 dll 数量、二级子文件夹数量
            var stats = new List<(string Name, int Dlls, int SubDirs)>();
            int rootDlls = 0;

            foreach (var dir in Directory.EnumerateDirectories(pluginsRoot))
            {
                var name = Path.GetFileName(dir);
                if (name.Equals(".backup", StringComparison.OrdinalIgnoreCase)) continue;

                var dlls = Directory.EnumerateFiles(dir, "*.dll*", SearchOption.AllDirectories)
                    .Count(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                                f.EndsWith(".dll_disabled", StringComparison.OrdinalIgnoreCase));
                if (dlls == 0) continue;

                var subDirs = Directory.EnumerateDirectories(dir).Count();
                stats.Add((name, dlls, subDirs));
            }

            rootDlls = Directory.EnumerateFiles(pluginsRoot, "*.dll*", SearchOption.TopDirectoryOnly)
                .Count(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".dll_disabled", StringComparison.OrdinalIgnoreCase));

            var nested = stats.Sum(s => s.Dlls);
            if (nested + rootDlls == 0) return;

            Log($"📁 目录结构: 根目录 {rootDlls} 个 dll | 子目录 {nested} 个（BepInEx 递归扫描，子目录里的也会加载）");

            // 「一个顶层文件夹里塞了一堆 mod 文件夹」= 整包解压的典型痕迹。
            // 阈值取 5：手动分类（Libraries\、CHP\）到不了这个量级。
            foreach (var s in stats.OrderByDescending(s => s.SubDirs).Take(3))
            {
                if (s.SubDirs < 5) break;
                Log($"   ⚠ {s.Name}\\ 下有 {s.SubDirs} 个子文件夹、共 {s.Dlls} 个 dll " +
                    "—— 像是整包解压进来的。能用就别动，想整理时注意同名副本提示。");
            }
        }
        catch { /* 结构体检失败不影响主流程 */ }
    }

    // ==================== 日志 ====================

    /// <summary>
    /// 追加一条日志，按类型着色 —— 之前清一色绿字，
    /// 冲突/重复/结构提醒这种「需要你看一眼」的信息完全淹没在里面。
    /// </summary>
    private void Log(string msg)
    {
        if (InvokeRequired)
        {
            this.BeginInvoke(() => Log(msg));
            return;
        }

        var color = LogColorOf(msg);
        var line = $"[{DateTime.Now:HH:mm:ss}] {msg}\r\n";

        _logBox.SelectionStart = _logBox.TextLength;
        _logBox.SelectionLength = 0;
        _logBox.SelectionColor = color;
        _logBox.AppendText(line);
        _logBox.SelectionColor = _logBox.ForeColor;
        _logBox.ScrollToCaret();
    }

    /// <summary>按内容前缀给日志选颜色</summary>
    private static Color LogColorOf(string msg)
    {
        if (msg.StartsWith("✗")) return Theme.Error;                    // 失败
        if (msg.Contains("⚠") || msg.StartsWith("⊘")) return Theme.Warning; // 需要注意 / 禁用
        if (msg.StartsWith("📁")) return Color.FromArgb(90, 170, 255);  // 结构信息
        if (msg.StartsWith("✓")) return Theme.Success;                  // 成功
        return Color.FromArgb(190, 190, 190);                           // 普通流水
    }

    // ==================== Mod 浏览器 ====================

    private void OpenModBrowser()
    {
        var browser = new ModBrowserForm(
            _config.GamePath,
            string.IsNullOrEmpty(_config.GitHubToken) ? null : _config.GitHubToken,
            _config.NexusApiKey,
            _config.NexusGameDomain,
            _mods,
            _pendingBrowserSource);

        _pendingBrowserSource = null;
        browser.ShowDialog(this);

        // 关闭后重新扫描，以反映新安装的 Mod
        if (_scanner != null)
            _ = ScanAsync();
    }

    /// <summary>`--browser [来源]` 指定的初始标签页（只消费一次）</summary>
    private string? _pendingBrowserSource;

    /// <summary>
    /// `--browser [来源]` 启动参数：等启动扫描跑完再打开浏览器 ——
    /// 否则 `_mods` 还是空的，列表里所有 mod 都会显示「未安装」。
    /// </summary>
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (Program.AutoOpenBrowserSource == null) return;

        _pendingBrowserSource = Program.AutoOpenBrowserSource;
        Program.AutoOpenBrowserSource = null;
        _ = OpenBrowserWhenReadyAsync();
    }

    private async Task OpenBrowserWhenReadyAsync()
    {
        // 最多等 30 秒，扫描一结束就打开
        for (var i = 0; i < 100 && _mods.Count == 0 && _scanner != null; i++)
            await Task.Delay(300);
        OpenModBrowser();
    }

    // ==================== 关于 ====================

    private void ShowAbout()
    {
        MessageBox.Show(
            "CU Mod 更新器 v1.0.0\n" +
            "Casualties Unknown Demo BepInEx Mod 更新工具\n\n" +
            "功能:\n" +
            "  • 扫描 BepInEx 插件目录，解析 BepInPlugin 版本信息\n" +
            "  • 连接 GitHub Releases 检查最新版本\n" +
            "  • 连接 Nexus Mods API 检查最新版本\n" +
            "  • 一键下载并安装更新，自动备份旧版本\n" +
            "  • 从备份回滚到任意旧版本\n\n" +
            "游戏路径: " + _config.GamePath + "\n" +
            "插件目录: " + (_scanner?.PluginsPath ?? "未配置") + "\n\n" +
            "配置文件: " + ConfigManager.ConfigFilePath + "\n" +
            "备份目录: " + Path.Combine(_config.GamePath, "BepInEx", "plugins", ".backup"),
            "关于", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        Program.Trace($"OnFormClosing reason={e.CloseReason}");
        ConfigManager.Save(_config);
        _updater?.Dispose();
        base.OnFormClosing(e);
    }
}
