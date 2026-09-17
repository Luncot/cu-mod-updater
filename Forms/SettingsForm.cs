using CUModUpdater.Models;
using CUModUpdater.Services;
using System.Windows.Forms;

namespace CUModUpdater.Forms;

/// <summary>
/// 设置对话框 - 配置游戏路径、API Key、各项开关
/// </summary>
public class SettingsForm : Form
{
    public ModUpdaterConfig Config { get; private set; }

    private readonly TextBox _gamePathBox = new();
    private readonly TextBox _githubTokenBox = new();
    private readonly TextBox _nexusKeyBox = new();
    private readonly TextBox _nexusCookieBox = new() { Multiline = true, ScrollBars = ScrollBars.Vertical };
    private readonly TextBox _nexusDomainBox = new();
    private readonly NumericUpDown _backupCount = new();
    private readonly CheckBox _autoScan = new();
    private readonly CheckBox _autoCheck = new();
    private readonly CheckBox _enableBackup = new();
    private readonly NumericUpDown _concurrency = new();

    public SettingsForm(ModUpdaterConfig config)
    {
        Config = config;

        Text = "设置";
        Size = new Size(600, 680);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(30, 30, 30);
        ForeColor = Color.FromArgb(224, 224, 224);
        Font = new Font("Microsoft YaHei UI", 9F);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        AutoScroll = true; // DPI 缩放下内容溢出时可滚动兜底

        BuildUI();
        LoadValues();
    }

    private void BuildUI()
    {
        int y = 15;
        const int labelX = 20;
        const int ctrlX = 160;
        const int ctrlW = 360;
        const int rowH = 30;

        // === 游戏路径 ===
        AddLabel("游戏安装路径:", labelX, y);
        _gamePathBox.Location = new Point(ctrlX, y);
        _gamePathBox.Size = new Size(ctrlW - 80, 25);
        StyleControl(_gamePathBox);
        Controls.Add(_gamePathBox);

        var browseBtn = new Button { Text = "浏览...", Location = new Point(ctrlX + ctrlW - 75, y - 1), Size = new Size(75, 27) };
        StyleButton(browseBtn);
        browseBtn.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog { SelectedPath = _gamePathBox.Text };
            if (dlg.ShowDialog() == DialogResult.OK)
                _gamePathBox.Text = dlg.SelectedPath;
        };
        Controls.Add(browseBtn);

        y += rowH + 12;

        // === GitHub Token ===
        AddLabel("GitHub Token:", labelX, y);
        AddHint("(可选) 提高 API 限额: 60 -> 5000/h", ctrlX, y - 2, ctrlW);
        _githubTokenBox.Location = new Point(ctrlX, y + 15);
        _githubTokenBox.Size = new Size(ctrlW, 25);
        _githubTokenBox.UseSystemPasswordChar = true;
        StyleControl(_githubTokenBox);
        Controls.Add(_githubTokenBox);

        y += rowH + 25;

        // === Nexus API Key ===
        AddLabel("Nexus Mods API Key:", labelX, y);
        AddHint("在 nexusmods.com -> Settings -> API 获取", ctrlX, y - 2, ctrlW);
        _nexusKeyBox.Location = new Point(ctrlX, y + 15);
        _nexusKeyBox.Size = new Size(ctrlW - 90, 25);
        _nexusKeyBox.UseSystemPasswordChar = true;
        StyleControl(_nexusKeyBox);
        Controls.Add(_nexusKeyBox);

        var validateBtn = new Button { Text = "验证", Location = new Point(ctrlX + ctrlW - 85, y + 14), Size = new Size(85, 27) };
        StyleButton(validateBtn);
        validateBtn.Click += async (_, _) => await ValidateNexusKey();
        Controls.Add(validateBtn);

        y += rowH + 25;

        // === Nexus 游戏域名 ===
        AddLabel("Nexus 游戏域名:", labelX, y);
        _nexusDomainBox.Location = new Point(ctrlX, y);
        _nexusDomainBox.Size = new Size(200, 25);
        StyleControl(_nexusDomainBox);
        Controls.Add(_nexusDomainBox);

        y += rowH + 12;

        // === Nexus Cookie（免费账号批量下载） ===
        AddLabel("Nexus Cookie:", labelX, y);
        AddHint("免费账号批量下载用：N 网登录后 F12 -> Network -> 复制 Cookie 请求头", ctrlX, y - 2, ctrlW);
        _nexusCookieBox.Location = new Point(ctrlX, y + 15);
        _nexusCookieBox.Size = new Size(ctrlW, 52);
        StyleControl(_nexusCookieBox);
        Controls.Add(_nexusCookieBox);

        y += 70;

        // === 选项 ===
        var optionsPanel = new Panel
        {
            Location = new Point(labelX, y),
            Size = new Size(510, 95),
            BackColor = Color.FromArgb(45, 45, 48),
        };

        _autoScan.Text = "启动时自动扫描插件";
        _autoScan.Location = new Point(10, 10);
        _autoScan.AutoSize = true;
        _autoScan.ForeColor = Color.FromArgb(224, 224, 224);
        optionsPanel.Controls.Add(_autoScan);

        _autoCheck.Text = "启动时自动检查更新";
        _autoCheck.Location = new Point(10, 32);
        _autoCheck.AutoSize = true;
        _autoCheck.ForeColor = Color.FromArgb(224, 224, 224);
        optionsPanel.Controls.Add(_autoCheck);

        _enableBackup.Text = "更新前备份旧版本 (支持回滚)";
        _enableBackup.Location = new Point(10, 54);
        _enableBackup.AutoSize = true;
        _enableBackup.ForeColor = Color.FromArgb(224, 224, 224);
        optionsPanel.Controls.Add(_enableBackup);

        Controls.Add(optionsPanel);

        y += 100;

        // === 备份保留数 ===
        AddLabel("备份保留数量:", labelX, y);
        _backupCount.Location = new Point(ctrlX, y);
        _backupCount.Size = new Size(80, 25);
        _backupCount.Minimum = 1;
        _backupCount.Maximum = 100;
        _backupCount.Value = 10;
        StyleControl(_backupCount);
        Controls.Add(_backupCount);

        y += rowH + 12;

        // === 并发数 ===
        AddLabel("检查更新并发数:", labelX, y);
        _concurrency.Location = new Point(ctrlX, y);
        _concurrency.Size = new Size(80, 25);
        _concurrency.Minimum = 1;
        _concurrency.Maximum = 20;
        _concurrency.Value = 5;
        StyleControl(_concurrency);
        Controls.Add(_concurrency);

        y += rowH + 20;

        // === 按钮（右下角对齐，窗宽 600 时客户区约 584） ===
        var saveBtn = new Button { Text = "保存", Location = new Point(330, y), Size = new Size(110, 34) };
        StyleButton(saveBtn, Color.FromArgb(0, 122, 204));
        saveBtn.Click += (_, _) =>
        {
            SaveValues();
            DialogResult = DialogResult.OK;
            Close();
        };
        Controls.Add(saveBtn);

        var cancelBtn = new Button { Text = "取消", Location = new Point(452, y), Size = new Size(110, 34) };
        StyleButton(cancelBtn);
        cancelBtn.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        Controls.Add(cancelBtn);
    }

    private void AddLabel(string text, int x, int y)
    {
        Controls.Add(new Label
        {
            Text = text,
            Location = new Point(x, y + 3),
            AutoSize = true,
            ForeColor = Color.FromArgb(200, 200, 200),
        });
    }

    private void AddHint(string text, int x, int y, int width)
    {
        Controls.Add(new Label
        {
            Text = text,
            Location = new Point(x, y),
            AutoSize = true,
            ForeColor = Color.FromArgb(153, 153, 153),
        });
    }

    private void LoadValues()
    {
        _gamePathBox.Text = Config.GamePath;
        _githubTokenBox.Text = Config.GitHubToken;
        _nexusKeyBox.Text = Config.NexusApiKey;
        _nexusDomainBox.Text = Config.NexusGameDomain;
        _nexusCookieBox.Text = Config.NexusCookie;
        _backupCount.Value = Math.Max(1, Config.BackupRetention);
        _concurrency.Value = Math.Max(1, Config.ConcurrentChecks);
        _autoScan.Checked = Config.AutoScanOnStart;
        _autoCheck.Checked = Config.AutoCheckOnStart;
        _enableBackup.Checked = Config.EnableBackup;
    }

    private void SaveValues()
    {
        Config.GamePath = _gamePathBox.Text.Trim();
        Config.GitHubToken = _githubTokenBox.Text.Trim();
        Config.NexusApiKey = _nexusKeyBox.Text.Trim();
        Config.NexusGameDomain = string.IsNullOrEmpty(_nexusDomainBox.Text.Trim())
            ? "casualtiesunknown" : _nexusDomainBox.Text.Trim();
        Config.NexusCookie = _nexusCookieBox.Text.Trim();
        Config.BackupRetention = (int)_backupCount.Value;
        Config.ConcurrentChecks = (int)_concurrency.Value;
        Config.AutoScanOnStart = _autoScan.Checked;
        Config.AutoCheckOnStart = _autoCheck.Checked;
        Config.EnableBackup = _enableBackup.Checked;
    }

    private async Task ValidateNexusKey()
    {
        var key = _nexusKeyBox.Text.Trim();
        if (string.IsNullOrEmpty(key))
        {
            MessageBox.Show("请先输入 API Key。", "提示");
            return;
        }

        var domain = string.IsNullOrEmpty(_nexusDomainBox.Text.Trim())
            ? "casualtiesunknown" : _nexusDomainBox.Text.Trim();

        ValidateBtnText("验证中...");
        var checker = new NexusChecker(key, domain);
        try
        {
            var valid = await checker.ValidateApiKeyAsync();
            MessageBox.Show(valid
                ? "✓ API Key 验证成功！"
                : "✗ API Key 无效，请检查。",
                "验证结果", MessageBoxButtons.OK,
                valid ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"验证出错: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            checker.Dispose();
            ValidateBtnText("验证");
        }
    }

    private Button? _validateBtnRef;
    private void ValidateBtnText(string text)
    {
        foreach (Control c in Controls)
            if (c is Button b && b.Text is "验证" or "验证中...")
            {
                b.Text = text;
                _validateBtnRef = b;
            }
    }

    private static void StyleControl(Control ctrl)
    {
        ctrl.BackColor = Color.FromArgb(51, 51, 56);
        ctrl.ForeColor = Color.FromArgb(224, 224, 224);
        if (ctrl is TextBoxBase tb)
            tb.BorderStyle = BorderStyle.FixedSingle;
        else if (ctrl is UpDownBase ud)
            ud.BorderStyle = BorderStyle.FixedSingle;
    }

    private static void StyleButton(Button btn, Color? accent = null)
    {
        btn.FlatStyle = FlatStyle.Flat;
        btn.FlatAppearance.BorderColor = Color.FromArgb(63, 63, 70);
        btn.BackColor = accent ?? Color.FromArgb(60, 60, 65);
        btn.ForeColor = Color.FromArgb(224, 224, 224);
        btn.Cursor = Cursors.Hand;
    }
}
