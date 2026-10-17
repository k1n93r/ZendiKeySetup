using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ZendiKeySetup
{
    internal sealed class MainForm : Form
    {
        private static readonly Color Canvas = Color.FromArgb(245, 246, 246);
        private static readonly Color Surface = Color.White;
        private static readonly Color Ink = Color.FromArgb(32, 35, 37);
        private static readonly Color Muted = Color.FromArgb(101, 108, 112);
        private static readonly Color Line = Color.FromArgb(220, 223, 224);
        private static readonly Color Accent = Color.FromArgb(183, 35, 43);
        private static readonly Color AccentDark = Color.FromArgb(145, 24, 31);
        private static readonly Color Success = Color.FromArgb(33, 128, 83);
        private static readonly Color Warning = Color.FromArgb(181, 116, 20);
        private static readonly Color Neutral = Color.FromArgb(137, 143, 146);

        private TextBox _keyBox;
        private CheckBox _showKey;
        private CheckBox _setupClaude;
        private CheckBox _setupCodex;
        private CheckBox _installMissing;
        private ComboBox _claudeModel;
        private ComboBox _codexModel;
        private Button _testButton;
        private Button _setupButton;
        private Button _installButton;
        private Button _restoreButton;
        private Button _refreshButton;
        private Button _clearLogButton;
        private Label _keyState;
        private Label _overallState;
        private ProgressBar _progress;
        private RichTextBox _log;
        private StatusDisplay _claudeCodeStatus;
        private StatusDisplay _codexStatus;
        private StatusDisplay _claudeDesktopStatus;
        private StatusDisplay _chatGptStatus;
        private bool _busy;
        private readonly string _initialInput;

        internal MainForm()
            : this(null)
        {
        }

        internal MainForm(string initialInput)
        {
            _initialInput = string.IsNullOrWhiteSpace(initialInput) ? null : initialInput.Trim();
            Text = "ZendiKey Setup";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(860, 680);
            ClientSize = new Size(940, 720);
            BackColor = Canvas;
            Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            AutoScaleMode = AutoScaleMode.Dpi;
            KeyPreview = true;

            try
            {
                Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch
            {
            }

            BuildInterface();
            WireEvents();
            UpdateTargetControls();
        }

        private void BuildInterface()
        {
            SuspendLayout();

            Panel header = BuildHeader();
            header.Dock = DockStyle.Fill;

            TableLayoutPanel workspace = new TableLayoutPanel();
            workspace.Dock = DockStyle.Fill;
            workspace.BackColor = Canvas;
            workspace.Padding = new Padding(20, 18, 20, 20);
            workspace.ColumnCount = 2;
            workspace.RowCount = 2;
            workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 260F));
            workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            workspace.RowStyles.Add(new RowStyle(SizeType.Percent, 64F));
            workspace.RowStyles.Add(new RowStyle(SizeType.Percent, 36F));

            Panel statusPanel = BuildStatusPanel();
            statusPanel.Margin = new Padding(0, 0, 12, 12);
            workspace.Controls.Add(statusPanel, 0, 0);

            Panel setupPanel = BuildSetupPanel();
            setupPanel.Margin = new Padding(0, 0, 0, 12);
            workspace.Controls.Add(setupPanel, 1, 0);

            Panel logPanel = BuildLogPanel();
            logPanel.Margin = new Padding(0);
            workspace.Controls.Add(logPanel, 0, 1);
            workspace.SetColumnSpan(logPanel, 2);

            TableLayoutPanel shell = new TableLayoutPanel();
            shell.Dock = DockStyle.Fill;
            shell.BackColor = Canvas;
            shell.ColumnCount = 1;
            shell.RowCount = 2;
            shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 92F));
            shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            shell.Controls.Add(header, 0, 0);
            shell.Controls.Add(workspace, 0, 1);
            Controls.Add(shell);

            ResumeLayout(true);
        }

        private Panel BuildHeader()
        {
            Panel header = new Panel();
            header.BackColor = Ink;

            Panel accent = new Panel();
            accent.Dock = DockStyle.Left;
            accent.Width = 6;
            accent.BackColor = Accent;
            header.Controls.Add(accent);

            PictureBox logo = new PictureBox();
            logo.Location = new Point(26, 16);
            logo.Size = new Size(60, 60);
            logo.SizeMode = PictureBoxSizeMode.Zoom;
            logo.BackColor = Color.Transparent;
            logo.Image = LoadLogo();
            header.Controls.Add(logo);

            Label title = new Label();
            title.AutoSize = true;
            title.Location = new Point(103, 17);
            title.Font = new Font("Segoe UI Semibold", 19F, FontStyle.Bold, GraphicsUnit.Point);
            title.ForeColor = Color.White;
            title.Text = "ZendiKey Setup";
            header.Controls.Add(title);

            Label endpoint = new Label();
            endpoint.AutoSize = true;
            endpoint.Location = new Point(105, 56);
            endpoint.Font = new Font("Segoe UI", 9.5F, FontStyle.Regular, GraphicsUnit.Point);
            endpoint.ForeColor = Color.FromArgb(194, 199, 201);
            endpoint.Text = AppPaths.BaseUrl;
            header.Controls.Add(endpoint);

            _overallState = new Label();
            _overallState.Dock = DockStyle.Right;
            _overallState.AutoEllipsis = true;
            _overallState.Width = 300;
            _overallState.Padding = new Padding(0, 0, 24, 0);
            _overallState.TextAlign = ContentAlignment.MiddleRight;
            _overallState.Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold, GraphicsUnit.Point);
            _overallState.ForeColor = Color.FromArgb(210, 214, 216);
            _overallState.Text = "Loading status...";
            header.Controls.Add(_overallState);

            return header;
        }

        private Panel BuildStatusPanel()
        {
            Panel panel = SurfacePanel();

            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.Padding = new Padding(16, 12, 16, 14);
            layout.ColumnCount = 1;
            layout.RowCount = 7;
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 56F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 56F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 56F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 56F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40F));

            Label heading = SectionHeading("Clients");
            layout.Controls.Add(heading, 0, 0);

            _claudeCodeStatus = new StatusDisplay("Claude Code");
            _codexStatus = new StatusDisplay("Codex CLI");
            _claudeDesktopStatus = new StatusDisplay("Claude Desktop");
            _chatGptStatus = new StatusDisplay("ChatGPT - Codex mode");
            layout.Controls.Add(_claudeCodeStatus, 0, 1);
            layout.Controls.Add(_codexStatus, 0, 2);
            layout.Controls.Add(_claudeDesktopStatus, 0, 3);
            layout.Controls.Add(_chatGptStatus, 0, 4);

            _refreshButton = MakeButton("Refresh status", ButtonKind.Neutral);
            _refreshButton.Dock = DockStyle.Fill;
            _refreshButton.Margin = new Padding(0, 4, 0, 0);
            layout.Controls.Add(_refreshButton, 0, 6);

            panel.Controls.Add(layout);
            return panel;
        }

        private Panel BuildSetupPanel()
        {
            Panel panel = SurfacePanel();

            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.Padding = new Padding(20, 12, 20, 14);
            layout.ColumnCount = 1;
            layout.RowCount = 8;
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 67F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 72F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 43F));

            TableLayoutPanel headingRow = new TableLayoutPanel();
            headingRow.Dock = DockStyle.Fill;
            headingRow.ColumnCount = 2;
            headingRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55F));
            headingRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45F));
            headingRow.Margin = new Padding(0);
            headingRow.Controls.Add(SectionHeading("Gateway access"), 0, 0);

            _keyState = new Label();
            _keyState.Dock = DockStyle.Fill;
            _keyState.TextAlign = ContentAlignment.MiddleRight;
            _keyState.AutoEllipsis = true;
            _keyState.ForeColor = Muted;
            _keyState.Text = "Not tested";
            headingRow.Controls.Add(_keyState, 1, 0);
            layout.Controls.Add(headingRow, 0, 0);

            TableLayoutPanel keyArea = new TableLayoutPanel();
            keyArea.Dock = DockStyle.Fill;
            keyArea.ColumnCount = 2;
            keyArea.RowCount = 2;
            keyArea.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            keyArea.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82F));
            keyArea.RowStyles.Add(new RowStyle(SizeType.Absolute, 23F));
            keyArea.RowStyles.Add(new RowStyle(SizeType.Absolute, 36F));
            keyArea.Margin = new Padding(0);

            Label keyLabel = FieldLabel("API key or ZEN code (ZEN-XXX-XXX-XXX)");
            keyArea.Controls.Add(keyLabel, 0, 0);
            keyArea.SetColumnSpan(keyLabel, 2);

            _keyBox = new TextBox();
            _keyBox.Dock = DockStyle.Fill;
            _keyBox.Font = new Font("Consolas", 10F, FontStyle.Regular, GraphicsUnit.Point);
            _keyBox.UseSystemPasswordChar = true;
            _keyBox.Margin = new Padding(0, 2, 10, 1);
            _keyBox.BorderStyle = BorderStyle.FixedSingle;
            keyArea.Controls.Add(_keyBox, 0, 1);

            _showKey = new CheckBox();
            _showKey.Dock = DockStyle.Fill;
            _showKey.Text = "Show";
            _showKey.ForeColor = Ink;
            _showKey.Margin = new Padding(0, 1, 0, 0);
            keyArea.Controls.Add(_showKey, 1, 1);
            layout.Controls.Add(keyArea, 0, 1);

            Label modelHeading = FieldLabel("Models returned by gateway");
            modelHeading.TextAlign = ContentAlignment.BottomLeft;
            layout.Controls.Add(modelHeading, 0, 2);

            TableLayoutPanel models = new TableLayoutPanel();
            models.Dock = DockStyle.Fill;
            models.ColumnCount = 2;
            models.RowCount = 2;
            models.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            models.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            models.RowStyles.Add(new RowStyle(SizeType.Absolute, 24F));
            models.RowStyles.Add(new RowStyle(SizeType.Absolute, 38F));
            models.Margin = new Padding(0, 0, 0, 0);

            Label claudeLabel = FieldLabel("Claude");
            claudeLabel.Margin = new Padding(0, 0, 6, 0);
            models.Controls.Add(claudeLabel, 0, 0);
            Label codexLabel = FieldLabel("Codex");
            codexLabel.Margin = new Padding(6, 0, 0, 0);
            models.Controls.Add(codexLabel, 1, 0);

            _claudeModel = ModelCombo();
            _claudeModel.Margin = new Padding(0, 1, 6, 0);
            models.Controls.Add(_claudeModel, 0, 1);
            _codexModel = ModelCombo();
            _codexModel.Margin = new Padding(6, 1, 0, 0);
            models.Controls.Add(_codexModel, 1, 1);
            layout.Controls.Add(models, 0, 3);

            TableLayoutPanel targets = new TableLayoutPanel();
            targets.Dock = DockStyle.Fill;
            targets.ColumnCount = 3;
            targets.RowCount = 1;
            targets.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 62F));
            targets.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 105F));
            targets.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            targets.Margin = new Padding(0);

            Label targetLabel = FieldLabel("Set up");
            targets.Controls.Add(targetLabel, 0, 0);

            _setupClaude = new CheckBox();
            _setupClaude.Dock = DockStyle.Fill;
            _setupClaude.Text = "Claude";
            _setupClaude.Checked = true;
            _setupClaude.ForeColor = Ink;
            _setupClaude.Margin = new Padding(0, 3, 8, 0);
            targets.Controls.Add(_setupClaude, 1, 0);

            _setupCodex = new CheckBox();
            _setupCodex.Dock = DockStyle.Fill;
            _setupCodex.Text = "Codex / ChatGPT";
            _setupCodex.Checked = true;
            _setupCodex.ForeColor = Ink;
            _setupCodex.Margin = new Padding(0, 3, 0, 0);
            targets.Controls.Add(_setupCodex, 2, 0);
            layout.Controls.Add(targets, 0, 4);

            _installMissing = new CheckBox();
            _installMissing.Dock = DockStyle.Fill;
            _installMissing.Text = "Install missing apps and CLIs";
            _installMissing.Checked = true;
            _installMissing.ForeColor = Ink;
            _installMissing.Margin = new Padding(0, 5, 0, 0);
            layout.Controls.Add(_installMissing, 0, 5);

            TableLayoutPanel primaryActions = new TableLayoutPanel();
            primaryActions.Dock = DockStyle.Bottom;
            primaryActions.Height = 43;
            primaryActions.ColumnCount = 2;
            primaryActions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 36F));
            primaryActions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 64F));
            primaryActions.Margin = new Padding(0);

            _testButton = MakeButton("Test key", ButtonKind.Neutral);
            _testButton.Dock = DockStyle.Fill;
            _testButton.Margin = new Padding(0, 0, 8, 0);
            primaryActions.Controls.Add(_testButton, 0, 0);

            _setupButton = MakeButton("Set up selected", ButtonKind.Primary);
            _setupButton.Dock = DockStyle.Fill;
            _setupButton.Margin = new Padding(0);
            primaryActions.Controls.Add(_setupButton, 1, 0);
            layout.Controls.Add(primaryActions, 0, 7);

            panel.Controls.Add(layout);
            return panel;
        }

        private Panel BuildLogPanel()
        {
            Panel panel = SurfacePanel();

            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.Padding = new Padding(16, 10, 16, 12);
            layout.ColumnCount = 1;
            layout.RowCount = 3;
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 5F));

            TableLayoutPanel bar = new TableLayoutPanel();
            bar.Dock = DockStyle.Fill;
            bar.ColumnCount = 4;
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 126F));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 126F));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 62F));
            bar.Margin = new Padding(0);
            bar.Controls.Add(SectionHeading("Activity"), 0, 0);

            _installButton = MakeButton("Install selected", ButtonKind.Compact);
            _installButton.Dock = DockStyle.Fill;
            _installButton.Margin = new Padding(4, 0, 4, 5);
            bar.Controls.Add(_installButton, 1, 0);

            _restoreButton = MakeButton("Restore latest", ButtonKind.Compact);
            _restoreButton.Dock = DockStyle.Fill;
            _restoreButton.Margin = new Padding(4, 0, 4, 5);
            bar.Controls.Add(_restoreButton, 2, 0);

            _clearLogButton = MakeButton("Clear", ButtonKind.Compact);
            _clearLogButton.Dock = DockStyle.Fill;
            _clearLogButton.Margin = new Padding(4, 0, 0, 5);
            bar.Controls.Add(_clearLogButton, 3, 0);
            layout.Controls.Add(bar, 0, 0);

            _log = new RichTextBox();
            _log.Dock = DockStyle.Fill;
            _log.ReadOnly = true;
            _log.BorderStyle = BorderStyle.None;
            _log.BackColor = Color.FromArgb(250, 250, 249);
            _log.ForeColor = Color.FromArgb(57, 62, 65);
            _log.Font = new Font("Consolas", 8.7F, FontStyle.Regular, GraphicsUnit.Point);
            _log.DetectUrls = false;
            _log.Margin = new Padding(0, 2, 0, 6);
            layout.Controls.Add(_log, 0, 1);

            _progress = new ProgressBar();
            _progress.Dock = DockStyle.Fill;
            _progress.Style = ProgressBarStyle.Marquee;
            _progress.MarqueeAnimationSpeed = 28;
            _progress.Visible = false;
            _progress.Margin = new Padding(0);
            layout.Controls.Add(_progress, 0, 2);

            panel.Controls.Add(layout);
            return panel;
        }

        private void WireEvents()
        {
            Shown += async delegate
            {
                LoadExistingCredential();
                LoadConfiguredModels();
                Log("Ready. Gateway: " + AppPaths.BaseUrl);
                if (_initialInput != null)
                {
                    _keyBox.Text = _initialInput;
                    _keyBox.SelectionStart = _keyBox.TextLength;
                    SetKeyState(KeyCodeClient.IsZenCode(_initialInput) ? "Code from link" : "Key from link", Success);
                    Log("Opened from link with " + (KeyCodeClient.IsZenCode(_initialInput) ? "code " + KeyCodeClient.NormalizeCode(_initialInput) : "a key") + ".");
                }
                await RefreshStatusAsync();
                if (_initialInput != null)
                {
                    await SetupSelectedAsync();
                }
            };

            _showKey.CheckedChanged += delegate
            {
                _keyBox.UseSystemPasswordChar = !_showKey.Checked;
            };

            _keyBox.TextChanged += delegate
            {
                if (!_busy)
                {
                    SetKeyState("Not tested", Muted);
                }
            };

            _setupClaude.CheckedChanged += delegate { UpdateTargetControls(); };
            _setupCodex.CheckedChanged += delegate { UpdateTargetControls(); };
            _refreshButton.Click += async delegate { await RefreshStatusAsync(); };
            _testButton.Click += async delegate { await TestKeyAsync(); };
            _setupButton.Click += async delegate { await SetupSelectedAsync(); };
            _installButton.Click += async delegate { await InstallMissingAsync(); };
            _restoreButton.Click += async delegate { await RestoreLatestAsync(); };
            _clearLogButton.Click += delegate { _log.Clear(); };

            KeyDown += async delegate(object sender, KeyEventArgs args)
            {
                if (args.KeyCode == Keys.Enter && args.Control)
                {
                    args.SuppressKeyPress = true;
                    await SetupSelectedAsync();
                }
            };
        }

        private void LoadExistingCredential()
        {
            string key = ConfigManager.FindExistingKey();
            if (!string.IsNullOrWhiteSpace(key))
            {
                _keyBox.Text = key;
                _keyBox.SelectionStart = _keyBox.TextLength;
                SetKeyState(CredentialStore.Exists ? "Saved key loaded" : "Existing key found", Success);
            }
        }

        private void LoadConfiguredModels()
        {
            LoadConfiguredModel(_claudeModel, ConfigManager.GetConfiguredClaudeModel());
            LoadConfiguredModel(_codexModel, ConfigManager.GetConfiguredCodexModel());
        }

        private static void LoadConfiguredModel(ComboBox combo, string model)
        {
            if (string.IsNullOrWhiteSpace(model))
            {
                return;
            }
            combo.Items.Clear();
            combo.Items.Add(model);
            combo.SelectedIndex = 0;
        }

        private async Task<GatewayCheckResult> TestKeyAsync()
        {
            if (_busy)
            {
                return null;
            }

            bool setupClaude = _setupClaude.Checked;
            bool setupCodex = _setupCodex.Checked;
            if (!setupClaude && !setupCodex)
            {
                MessageBox.Show(this, "Select Claude, Codex, or both first.", "Nothing selected", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return null;
            }

            string key = _keyBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                MessageBox.Show(this, "Enter a ZendiKey API key or a ZEN code first.", "ZendiKey Setup", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _keyBox.Focus();
                return null;
            }

            SetBusy(true, "Testing gateway...");
            try
            {
                key = await ResolveInputAsync(key);
                Log("Checking available models...");
                GatewayCheckResult models = await GatewayClient.CheckAsync(key, null, null, false);
                if (!models.ModelsOk)
                {
                    SetKeyState("Connection failed", Accent);
                    Log("Gateway check failed: " + models.ModelsMessage);
                    MessageBox.Show(this, models.ModelsMessage, "Gateway check failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return models;
                }

                PopulateModels(models.Models);
                Log(models.ModelsMessage + ".");

                string claude = setupClaude
                    ? SelectedModel(_claudeModel, ModelSelector.PickClaude(models.Models))
                    : null;
                string codex = setupCodex
                    ? SelectedModel(_codexModel, ModelSelector.PickCodex(models.Models))
                    : null;
                Log(setupClaude && setupCodex
                    ? "Testing Messages and Responses endpoints..."
                    : setupClaude
                        ? "Testing the Messages endpoint..."
                        : "Testing the Responses endpoint...");
                models = await GatewayClient.FindCompatibleModelsAsync(
                    key,
                    models.Models,
                    claude,
                    codex,
                    setupClaude,
                    setupCodex);
                if (setupClaude)
                {
                    SelectComboValue(_claudeModel, models.ClaudeModel);
                }
                if (setupCodex)
                {
                    SelectComboValue(_codexModel, models.CodexModel);
                }

                if (models.AllOk)
                {
                    SetKeyState("Key verified", Success);
                    if (setupClaude)
                    {
                        Log("Claude API: " + models.ClaudeMessage + ".");
                    }
                    if (setupCodex)
                    {
                        Log("Responses API: " + models.CodexMessage + ".");
                    }
                    await ShowKeyStatusAsync(key);
                }
                else
                {
                    SetKeyState("Protocol test failed", Accent);
                    Log("Protocol test failed: " + models.Summary);
                    MessageBox.Show(this, models.Summary, "Gateway check failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }

                return models;
            }
            catch (Exception ex)
            {
                SetKeyState("Connection failed", Accent);
                Log("Gateway check failed: " + ex.Message);
                MessageBox.Show(this, ex.Message, "Gateway check failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return null;
            }
            finally
            {
                SetBusy(false, null);
            }
        }

        private async Task SetupSelectedAsync()
        {
            if (_busy)
            {
                return;
            }

            bool setupClaude = _setupClaude.Checked;
            bool setupCodex = _setupCodex.Checked;
            if (!setupClaude && !setupCodex)
            {
                MessageBox.Show(this, "Select Claude, Codex, or both first.", "Nothing selected", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string key = _keyBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                MessageBox.Show(this, "Enter a ZendiKey API key or a ZEN code first.", "ZendiKey Setup", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _keyBox.Focus();
                return;
            }

            SetBusy(true, "Setting up clients...");
            try
            {
                key = await ResolveInputAsync(key);
                Log("Reading models from gateway...");
                GatewayCheckResult first = await GatewayClient.CheckAsync(key, null, null, false);
                if (!first.ModelsOk)
                {
                    throw new InvalidOperationException(first.ModelsMessage);
                }
                PopulateModels(first.Models);

                string claude = setupClaude
                    ? SelectedModel(_claudeModel, ModelSelector.PickClaude(first.Models))
                    : null;
                string codex = setupCodex
                    ? SelectedModel(_codexModel, ModelSelector.PickCodex(first.Models))
                    : null;
                if ((setupClaude && string.IsNullOrWhiteSpace(claude)) ||
                    (setupCodex && string.IsNullOrWhiteSpace(codex)))
                {
                    throw new InvalidOperationException("The key does not expose a compatible model for the selected setup target.");
                }

                if (setupClaude)
                {
                    Log("Validating Claude model " + claude + "...");
                }
                if (setupCodex)
                {
                    Log("Validating Codex model " + codex + "...");
                }
                GatewayCheckResult check = await GatewayClient.FindCompatibleModelsAsync(
                    key,
                    first.Models,
                    claude,
                    codex,
                    setupClaude,
                    setupCodex);
                if (!check.AllOk)
                {
                    throw new InvalidOperationException(check.Summary);
                }
                claude = check.ClaudeModel;
                codex = check.CodexModel;
                if (setupClaude)
                {
                    SelectComboValue(_claudeModel, claude);
                }
                if (setupCodex)
                {
                    SelectComboValue(_codexModel, codex);
                }
                SetKeyState("Key verified", Success);

                bool installSuccess = true;
                if (_installMissing.Checked)
                {
                    InstallResult install = await InstallManager.InstallMissingAsync(Log, setupClaude, setupCodex);
                    installSuccess = install.Success;
                }

                Log("Writing encrypted credential and client configuration...");
                ApplyResult applied = await Task.Run(delegate
                {
                    return ConfigManager.Apply(
                        key,
                        claude,
                        codex,
                        check.Models,
                        setupClaude,
                        setupCodex);
                });
                if (setupClaude)
                {
                    Log("Claude Code configured.");
                    Log("Claude Desktop: " + applied.ClaudeDesktopMessage);
                }
                if (setupCodex)
                {
                    Log("Codex CLI and ChatGPT Codex mode configured.");
                }
                Log("Encrypted backup: " + applied.BackupDirectory);

                await RefreshStatusCoreAsync();
                await ShowKeyStatusAsync(key);
                SetOverall("Setup complete", installSuccess ? Success : Warning);
                string successMessage = setupClaude && setupCodex
                    ? "Setup completed. Restart Claude Desktop and ChatGPT before using the new configuration."
                    : setupClaude
                        ? "Claude setup completed. Restart Claude Desktop before using the new configuration."
                        : "Codex setup completed. Restart ChatGPT before using the new configuration.";
                MessageBox.Show(
                    this,
                    installSuccess
                        ? successMessage
                        : "Configuration completed, but at least one optional installation failed. See Activity for details.",
                    "ZendiKey Setup",
                    MessageBoxButtons.OK,
                    installSuccess ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                SetOverall("Setup failed", Accent);
                Log("Setup failed: " + ex.Message);
                MessageBox.Show(this, ex.Message, "Setup failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                SetBusy(false, null);
            }
        }

        /// <summary>
        /// Accepts either an sk- key or a ZEN-XXX-XXX-XXX code. A code is exchanged for its
        /// key through the gateway (recovering it if it was already claimed) and the key box
        /// is updated so the rest of the flow works with the real key.
        /// </summary>
        private async Task<string> ResolveInputAsync(string input)
        {
            if (!KeyCodeClient.IsZenCode(input))
            {
                return input;
            }

            string code = KeyCodeClient.NormalizeCode(input);
            Log("Exchanging code " + code + " for a key...");
            KeyCodeResult claimed = await KeyCodeClient.ClaimAsync(code);
            _keyBox.Text = claimed.Key;
            _keyBox.SelectionStart = _keyBox.TextLength;
            Log((claimed.Recovered ? "Key recovered: " : "Key received: ") + claimed.Summary + ".");
            SetKeyState(claimed.Summary, Success);
            return claimed.Key;
        }

        /// <summary>Shows package, remaining credit and expiry of the key from the public check endpoint.</summary>
        private async Task ShowKeyStatusAsync(string key)
        {
            try
            {
                KeyStatus status = await KeyCodeClient.CheckAsync(key);
                Log("Key status: " + status.ShortSummary + ".");
                SetKeyState(status.ShortSummary, status.Active ? Success : Warning);
            }
            catch (Exception ex)
            {
                Log("Key status unavailable: " + ex.Message);
            }
        }

        private async Task InstallMissingAsync()
        {
            if (_busy)
            {
                return;
            }

            bool installClaude = _setupClaude.Checked;
            bool installCodex = _setupCodex.Checked;
            if (!installClaude && !installCodex)
            {
                MessageBox.Show(this, "Select Claude, Codex, or both first.", "Nothing selected", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            SetBusy(true, "Installing missing clients...");
            try
            {
                InstallResult result = await InstallManager.InstallMissingAsync(Log, installClaude, installCodex);
                await RefreshStatusCoreAsync();
                SetOverall(result.Success ? "Clients ready" : "Installation incomplete", result.Success ? Success : Warning);
            }
            catch (Exception ex)
            {
                Log("Installation failed: " + ex.Message);
                MessageBox.Show(this, ex.Message, "Installation failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                SetBusy(false, null);
            }
        }

        private async Task RestoreLatestAsync()
        {
            if (_busy)
            {
                return;
            }

            DialogResult confirmation = MessageBox.Show(
                this,
                "Restore Claude, Codex, credential, and Claude Desktop policy from the latest backup?",
                "Restore latest backup",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (confirmation != DialogResult.Yes)
            {
                return;
            }

            SetBusy(true, "Restoring backup...");
            try
            {
                string restored = await Task.Run(delegate { return ConfigManager.RestoreLatestBackup(); });
                Log("Restored: " + restored);
                LoadExistingCredential();
                await RefreshStatusCoreAsync();
                SetOverall("Backup restored", Success);
            }
            catch (Exception ex)
            {
                Log("Restore failed: " + ex.Message);
                MessageBox.Show(this, ex.Message, "Restore failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                SetBusy(false, null);
            }
        }

        private async Task RefreshStatusAsync()
        {
            if (_busy)
            {
                return;
            }

            SetBusy(true, "Refreshing status...");
            try
            {
                await RefreshStatusCoreAsync();
                Log("Client status refreshed.");
            }
            finally
            {
                SetBusy(false, null);
            }
        }

        private async Task RefreshStatusCoreAsync()
        {
            ClientStatus status = await Task.Run(delegate { return SystemDetector.Detect(); });
            SetDetection(_claudeCodeStatus, status.ClaudeCode);
            SetDetection(_codexStatus, status.CodexCli);
            SetDetection(_claudeDesktopStatus, status.ClaudeDesktop);
            SetDetection(_chatGptStatus, status.ChatGptDesktop);

            bool claude = ConfigManager.IsClaudeConfigured();
            bool codex = ConfigManager.IsCodexConfigured();
            bool desktop = ConfigManager.IsClaudeDesktopConfigured();
            bool credential = CredentialStore.IsReadable;
            if (claude && codex && desktop && credential)
            {
                SetOverall("Gateway configuration active", Success);
            }
            else if (claude && desktop && !codex && credential)
            {
                SetOverall("Claude configuration active", Success);
            }
            else if (codex && !claude && !desktop && credential)
            {
                SetOverall("Codex configuration active", Success);
            }
            else if ((claude || codex || desktop) && credential)
            {
                SetOverall("Gateway configuration partial", Warning);
            }
            else
            {
                SetOverall("Configuration not applied", Neutral);
            }
        }

        private void PopulateModels(IEnumerable<string> models)
        {
            string oldClaude = Convert.ToString(_claudeModel.SelectedItem);
            string oldCodex = Convert.ToString(_codexModel.SelectedItem);
            List<string> source = models.ToList();

            FillCombo(_claudeModel, ModelSelector.ClaudeModels(source), oldClaude, ModelSelector.PickClaude(source));
            FillCombo(_codexModel, ModelSelector.CodexModels(source), oldCodex, ModelSelector.PickCodex(source));
        }

        private static void FillCombo(ComboBox combo, List<string> values, string oldValue, string preferred)
        {
            combo.BeginUpdate();
            try
            {
                combo.Items.Clear();
                foreach (string value in values)
                {
                    combo.Items.Add(value);
                }

                string selection = values.FirstOrDefault(delegate(string value)
                {
                    return string.Equals(value, oldValue, StringComparison.OrdinalIgnoreCase);
                }) ?? preferred;
                if (selection != null)
                {
                    combo.SelectedItem = values.First(delegate(string value)
                    {
                        return string.Equals(value, selection, StringComparison.OrdinalIgnoreCase);
                    });
                }
                else if (combo.Items.Count > 0)
                {
                    combo.SelectedIndex = 0;
                }
            }
            finally
            {
                combo.EndUpdate();
            }
        }

        private static string SelectedModel(ComboBox combo, string fallback)
        {
            string selected = Convert.ToString(combo.SelectedItem);
            return string.IsNullOrWhiteSpace(selected) ? fallback : selected;
        }

        private static void SelectComboValue(ComboBox combo, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }
            for (int i = 0; i < combo.Items.Count; i++)
            {
                if (string.Equals(Convert.ToString(combo.Items[i]), value, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedIndex = i;
                    return;
                }
            }
        }

        private void SetBusy(bool busy, string state)
        {
            _busy = busy;
            _progress.Visible = busy;
            _restoreButton.Enabled = !busy;
            _refreshButton.Enabled = !busy;
            _keyBox.Enabled = !busy;
            _showKey.Enabled = !busy;
            _setupClaude.Enabled = !busy;
            _setupCodex.Enabled = !busy;
            _installMissing.Enabled = !busy;
            UpdateTargetControls();
            UseWaitCursor = busy;
            if (busy && !string.IsNullOrWhiteSpace(state))
            {
                SetOverall(state, Warning);
            }
        }

        private void UpdateTargetControls()
        {
            bool hasTarget = _setupClaude.Checked || _setupCodex.Checked;
            _claudeModel.Enabled = !_busy && _setupClaude.Checked;
            _codexModel.Enabled = !_busy && _setupCodex.Checked;
            _setupButton.Enabled = !_busy && hasTarget;
            _testButton.Enabled = !_busy && hasTarget;
            _installButton.Enabled = !_busy && hasTarget;
        }

        private void SetKeyState(string text, Color color)
        {
            _keyState.Text = text;
            _keyState.ForeColor = color;
        }

        private void SetOverall(string text, Color color)
        {
            _overallState.Text = text;
            _overallState.ForeColor = color == Success
                ? Color.FromArgb(121, 220, 165)
                : color == Warning
                    ? Color.FromArgb(244, 194, 105)
                    : color == Accent
                        ? Color.FromArgb(255, 151, 157)
                        : Color.FromArgb(210, 214, 216);
        }

        private static void SetDetection(StatusDisplay display, DetectionItem item)
        {
            display.SetValue(item.DisplayValue, item.Installed ? Success : Neutral);
        }

        private void Log(string message)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string>(Log), message);
                return;
            }

            _log.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + message + Environment.NewLine);
            _log.SelectionStart = _log.TextLength;
            _log.ScrollToCaret();
        }

        private static Panel SurfacePanel()
        {
            return new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Surface,
                BorderStyle = BorderStyle.FixedSingle
            };
        }

        private static Label SectionHeading(string text)
        {
            return new Label
            {
                Dock = DockStyle.Fill,
                AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Segoe UI Semibold", 11F, FontStyle.Bold, GraphicsUnit.Point),
                ForeColor = Ink,
                Text = text,
                Margin = new Padding(0)
            };
        }

        private static Label FieldLabel(string text)
        {
            return new Label
            {
                Dock = DockStyle.Fill,
                AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Segoe UI Semibold", 8.5F, FontStyle.Bold, GraphicsUnit.Point),
                ForeColor = Muted,
                Text = text,
                Margin = new Padding(0)
            };
        }

        private static ComboBox ModelCombo()
        {
            return new ComboBox
            {
                Dock = DockStyle.Fill,
                DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.Standard,
                BackColor = Color.White,
                Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point),
                IntegralHeight = false,
                DropDownHeight = 210
            };
        }

        private static Button MakeButton(string text, ButtonKind kind)
        {
            Button button = new Button();
            button.Text = text;
            button.Cursor = Cursors.Hand;
            button.FlatStyle = FlatStyle.Flat;
            button.Font = new Font("Segoe UI Semibold", kind == ButtonKind.Compact ? 8.3F : 9F, FontStyle.Bold, GraphicsUnit.Point);
            button.UseVisualStyleBackColor = false;

            if (kind == ButtonKind.Primary)
            {
                button.BackColor = Accent;
                button.ForeColor = Color.White;
                button.FlatAppearance.BorderColor = AccentDark;
                button.FlatAppearance.MouseOverBackColor = AccentDark;
                button.FlatAppearance.MouseDownBackColor = Color.FromArgb(118, 18, 24);
            }
            else if (kind == ButtonKind.Compact)
            {
                button.BackColor = Color.FromArgb(249, 249, 248);
                button.ForeColor = Ink;
                button.FlatAppearance.BorderColor = Line;
                button.FlatAppearance.MouseOverBackColor = Color.FromArgb(238, 240, 240);
            }
            else
            {
                button.BackColor = Color.White;
                button.ForeColor = Ink;
                button.FlatAppearance.BorderColor = Color.FromArgb(177, 182, 184);
                button.FlatAppearance.MouseOverBackColor = Color.FromArgb(239, 241, 241);
            }

            button.FlatAppearance.BorderSize = 1;
            return button;
        }

        private static Image LoadLogo()
        {
            try
            {
                Stream resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("ZendiKeySetup.Crane.png");
                if (resource == null)
                {
                    return null;
                }
                using (resource)
                using (Image source = Image.FromStream(resource))
                {
                    return new Bitmap(source);
                }
            }
            catch
            {
                return null;
            }
        }

        private enum ButtonKind
        {
            Primary,
            Neutral,
            Compact
        }
    }

    internal sealed class StatusDisplay : Panel
    {
        private readonly Label _value;
        private readonly Panel _indicator;

        internal StatusDisplay(string name)
        {
            Dock = DockStyle.Fill;
            Margin = new Padding(0);
            BackColor = Color.White;

            _indicator = new Panel();
            _indicator.Size = new Size(8, 8);
            _indicator.Location = new Point(1, 23);
            _indicator.BackColor = Color.FromArgb(137, 143, 146);
            Controls.Add(_indicator);

            Label title = new Label();
            title.AutoEllipsis = true;
            title.Location = new Point(19, 7);
            title.Size = new Size(206, 19);
            title.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            title.Font = new Font("Segoe UI Semibold", 8.7F, FontStyle.Bold, GraphicsUnit.Point);
            title.ForeColor = Color.FromArgb(47, 51, 53);
            title.Text = name;
            Controls.Add(title);

            _value = new Label();
            _value.AutoEllipsis = true;
            _value.Location = new Point(19, 28);
            _value.Size = new Size(206, 18);
            _value.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _value.Font = new Font("Segoe UI", 8F, FontStyle.Regular, GraphicsUnit.Point);
            _value.ForeColor = Color.FromArgb(101, 108, 112);
            _value.Text = "Checking...";
            Controls.Add(_value);
        }

        internal void SetValue(string value, Color indicator)
        {
            _value.Text = value;
            _indicator.BackColor = indicator;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (Pen line = new Pen(Color.FromArgb(232, 234, 235)))
            {
                e.Graphics.DrawLine(line, 0, Height - 1, Width, Height - 1);
            }
        }
    }
}
