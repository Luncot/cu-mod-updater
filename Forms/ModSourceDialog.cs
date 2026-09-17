using CUModUpdater.Models;

namespace CUModUpdater.Forms;

/// <summary>
/// Mod 来源配置对话框 - 为单个 Mod 配置 GitHub/Nexus 更新来源
/// </summary>
public class ModSourceDialog : Form
{
    public ModSource Source { get; private set; }

    private readonly RadioButton _githubRadio = new();
    private readonly RadioButton _nexusRadio = new();
    private readonly RadioButton _gbRadio = new();
    private readonly TextBox _ownerBox = new();
    private readonly TextBox _repoBox = new();
    private readonly TextBox _modIdBox = new();
    private readonly TextBox _patternBox = new();
    private readonly Label _ownerLabel = new();
    private readonly Label _repoLabel = new();
    private readonly Label _modIdLabel = new();
    private readonly Label _patternLabel = new();

    private readonly ModInfo _mod;

    public ModSourceDialog(ModInfo mod, string nexusDomain)
    {
        _mod = mod;

        // 如果已有配置，复制；否则创建新
        Source = mod.Source != null
            ? new ModSource
            {
                Type = mod.Source.Type,
                Owner = mod.Source.Owner,
                Repo = mod.Source.Repo,
                ModId = mod.Source.ModId,
                AssetPattern = mod.Source.AssetPattern,
            }
            : new ModSource();

        Text = $"配置来源 - {mod.Name}";
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(30, 30, 30);
        ForeColor = Color.FromArgb(224, 224, 224);
        Font = new Font("Microsoft YaHei UI", 9F);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        BuildUI();
        LoadValues();
        UpdateVisibility();

        // 高度在 BuildUI 之后确定（候选列表会撑高内容），保证按钮不被裁掉
        Size = new Size(480, Math.Max(380, _contentBottom + 60));
    }

    /// <summary>BuildUI 结束时的内容底部 y（用于自适应窗体高度）</summary>
    private int _contentBottom;

    private void BuildUI()
    {
        int y = 15;

        // Mod 信息
        var infoLabel = new Label
        {
            Text = $"Mod: {_mod.Name}\nGUID: {_mod.PluginGuid}\n当前版本: {_mod.DisplayVersion}",
            Location = new Point(20, y),
            Size = new Size(440, 55),
            ForeColor = Color.FromArgb(180, 180, 180),
        };
        Controls.Add(infoLabel);

        y += 65;

        // 分隔线
        var sep1 = new Panel
        {
            Location = new Point(20, y),
            Size = new Size(440, 1),
            BackColor = Color.FromArgb(63, 63, 70),
        };
        Controls.Add(sep1);

        y += 15;

        // 来源类型
        _githubRadio.Text = "GitHub Releases";
        _githubRadio.Location = new Point(30, y);
        _githubRadio.AutoSize = true;
        _githubRadio.ForeColor = Color.FromArgb(224, 224, 224);
        _githubRadio.Checked = true;
        _githubRadio.CheckedChanged += (_, _) => UpdateVisibility();
        Controls.Add(_githubRadio);

        _nexusRadio.Text = "Nexus Mods";
        _nexusRadio.Location = new Point(180, y);
        _nexusRadio.AutoSize = true;
        _nexusRadio.ForeColor = Color.FromArgb(224, 224, 224);
        _nexusRadio.CheckedChanged += (_, _) => UpdateVisibility();
        Controls.Add(_nexusRadio);

        _gbRadio.Text = "GameBanana";
        _gbRadio.Location = new Point(320, y);
        _gbRadio.AutoSize = true;
        _gbRadio.ForeColor = Color.FromArgb(224, 224, 224);
        _gbRadio.CheckedChanged += (_, _) => UpdateVisibility();
        Controls.Add(_gbRadio);

        y += 30;

        // GitHub 字段
        _ownerLabel.Text = "Owner (用户/组织):";
        _ownerLabel.Location = new Point(30, y);
        _ownerLabel.AutoSize = true;
        _ownerLabel.ForeColor = Color.FromArgb(200, 200, 200);
        Controls.Add(_ownerLabel);

        _ownerBox.Location = new Point(180, y - 3);
        _ownerBox.Size = new Size(250, 25);
        StyleControl(_ownerBox);
        Controls.Add(_ownerBox);

        y += 30;

        _repoLabel.Text = "Repo (仓库名):";
        _repoLabel.Location = new Point(30, y);
        _repoLabel.AutoSize = true;
        _repoLabel.ForeColor = Color.FromArgb(200, 200, 200);
        Controls.Add(_repoLabel);

        _repoBox.Location = new Point(180, y - 3);
        _repoBox.Size = new Size(250, 25);
        StyleControl(_repoBox);
        Controls.Add(_repoBox);

        // Nexus 字段
        _modIdLabel.Text = "Mod ID:";
        _modIdLabel.Location = new Point(30, y - 30);
        _modIdLabel.AutoSize = true;
        _modIdLabel.ForeColor = Color.FromArgb(200, 200, 200);
        Controls.Add(_modIdLabel);

        _modIdBox.Location = new Point(180, y - 33);
        _modIdBox.Size = new Size(250, 25);
        StyleControl(_modIdBox);
        Controls.Add(_modIdBox);

        y += 30;

        // 资源匹配模式
        _patternLabel.Text = "资源匹配模式:";
        _patternLabel.Location = new Point(30, y);
        _patternLabel.AutoSize = true;
        _patternLabel.ForeColor = Color.FromArgb(200, 200, 200);
        Controls.Add(_patternLabel);

        _patternBox.Location = new Point(180, y - 3);
        _patternBox.Size = new Size(250, 25);
        StyleControl(_patternBox);
        Controls.Add(_patternBox);

        var hint = new Label
        {
            Text = "留空自动选择 .dll 文件；可用通配符如 *.dll",
            Location = new Point(180, y + 25),
            AutoSize = true,
            ForeColor = Color.FromArgb(153, 153, 153),
        };
        Controls.Add(hint);

        y += 55;

        // 候选来源提示（该 dll 在元数据里对应多个 mod 时列出，供用户选择）
        var candidates = Services.CasualtiesManageableService.GetDllNameCandidates(_mod.FileName);
        if (candidates.Count > 1 && _mod.Source == null)
        {
            var candText = "⚠ 该文件名在官方元数据中对应多个 Mod，无法自动判断：\n" +
                string.Join("\n", candidates.Take(6).Select(c => $"    • #{c.ModId}  {c.Name}")) +
                (candidates.Count > 6 ? $"\n    …共 {candidates.Count} 个候选" : "") +
                "\n请在 N 网上核对后填写正确的 Mod ID。";
            var candLabel = new Label
            {
                Text = candText,
                Location = new Point(20, y),
                Size = new Size(440, candidates.Count > 6 ? 105 : 15 + candidates.Count * 16 + 32),
                ForeColor = Color.FromArgb(255, 180, 90),
                AutoSize = false,
            };
            Controls.Add(candLabel);
            y += candLabel.Height + 6;
        }

        // 预填建议
        var suggestLabel = new Label
        {
            Text = "提示: GitHub 链接格式为 github.com/{Owner}/{Repo}",
            Location = new Point(20, y),
            AutoSize = true,
            ForeColor = Color.FromArgb(100, 150, 200),
        };
        Controls.Add(suggestLabel);

        y += 30;

        // 按钮
        var saveBtn = new Button
        {
            Text = "保存",
            Location = new Point(260, y),
            Size = new Size(90, 32),
        };
        StyleButton(saveBtn, Color.FromArgb(0, 122, 204));
        saveBtn.Click += (_, _) =>
        {
            SaveValues();
            DialogResult = DialogResult.OK;
            Close();
        };
        Controls.Add(saveBtn);

        var cancelBtn = new Button
        {
            Text = "取消",
            Location = new Point(360, y),
            Size = new Size(90, 32),
        };
        StyleButton(cancelBtn);
        cancelBtn.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        Controls.Add(cancelBtn);

        _contentBottom = y + 32;
    }

    private void LoadValues()
    {
        if (Source.IsGitHub)
        {
            _githubRadio.Checked = true;
            _ownerBox.Text = Source.Owner;
            _repoBox.Text = Source.Repo;
        }
        else if (Source.IsNexus)
        {
            _nexusRadio.Checked = true;
            _modIdBox.Text = Source.ModId;
        }
        else if (Source.IsGameBanana)
        {
            _gbRadio.Checked = true;
            _modIdBox.Text = Source.ModId;
        }
        _patternBox.Text = Source.AssetPattern;
    }

    private void UpdateVisibility()
    {
        var isGithub = _githubRadio.Checked;
        _ownerLabel.Visible = isGithub;
        _ownerBox.Visible = isGithub;
        _repoLabel.Visible = isGithub;
        _repoBox.Visible = isGithub;
        var needsModId = !isGithub;
        _modIdLabel.Visible = needsModId;
        _modIdBox.Visible = needsModId;
        if (needsModId)
            _modIdLabel.Text = _gbRadio.Checked ? "GameBanana Mod ID:" : "Mod ID:";
    }

    private void SaveValues()
    {
        if (_githubRadio.Checked)
        {
            Source.Type = "github";
            Source.Owner = _ownerBox.Text.Trim();
            Source.Repo = _repoBox.Text.Trim();
        }
        else if (_gbRadio.Checked)
        {
            Source.Type = "gamebanana";
            Source.ModId = _modIdBox.Text.Trim();
        }
        else
        {
            Source.Type = "nexus";
            Source.ModId = _modIdBox.Text.Trim();
        }
        Source.AssetPattern = _patternBox.Text.Trim();
    }

    private static void StyleControl(Control ctrl)
    {
        ctrl.BackColor = Color.FromArgb(51, 51, 56);
        ctrl.ForeColor = Color.FromArgb(224, 224, 224);
        if (ctrl is TextBoxBase tb)
            tb.BorderStyle = BorderStyle.FixedSingle;
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
