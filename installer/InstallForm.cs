namespace Soda.AssetQuickRead.Setup;

// Keep the scanner installer's brand, spacing and status hierarchy.
// Installation still requires a click because protocol files are downloaded.
internal sealed partial class InstallForm
{
    private readonly Label text = new();
    private readonly SodaProgressBar progress = new();
    private readonly Button action = new();
    private readonly Button cancel = new();
    private readonly Button uninstall = new();
    private readonly Panel card = new();
    private readonly LinkLabel detailsLink = new();
    private readonly TextBox details = new();
    private static readonly Color Purple = Color.FromArgb(107, 89, 148);

    private void InitializeInterface()
    {
        SuspendLayout();
        Text = "Soda Terminal · 资产快读"; ClientSize = new Size(520, 284);
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen; BackColor = Color.White;
        Font = new Font("Microsoft YaHei UI", 9f); Padding = new Padding(32);
        card.Location = new Point(32, 24); card.Size = new Size(456, 180); card.BackColor = Color.White;
        using var brandStream = typeof(InstallForm).Assembly.GetManifestResourceStream("soda-brand-icon.png");
        if (brandStream is not null)
        {
            var image = new Bitmap(brandStream);
            card.Controls.Add(new PictureBox { Location = new Point(0, 0), Size = new Size(44, 44),
                Image = image, SizeMode = PictureBoxSizeMode.Zoom, AccessibleName = "Soda Terminal" });
        }
        using var iconStream = typeof(InstallForm).Assembly.GetManifestResourceStream("soda-brand-icon.ico");
        if (iconStream is not null) Icon = new Icon(iconStream);
        card.Controls.Add(new Label { Location = new Point(58, 0), Size = new Size(398, 30),
            Text = "资产快读", Font = new Font("Microsoft YaHei UI", 17f, FontStyle.Bold), ForeColor = Color.FromArgb(34, 30, 43) });
        card.Controls.Add(new Label { Location = new Point(59, 32), Size = new Size(395, 22),
            Text = "安装版本 " + InstallEngine.Version, ForeColor = Color.FromArgb(108, 101, 120), Name = "version" });
        text.Location = new Point(0, 82); text.Size = new Size(454, 36);
        text.Font = new Font("Microsoft YaHei UI", 10.5f); text.ForeColor = Color.FromArgb(55, 48, 67);
        text.Text = "安装后，打开工具并返回网页连接。"; text.AccessibleRole = AccessibleRole.StaticText;
        progress.Location = new Point(0, 130); progress.Size = new Size(456, 6);
        progress.Visible = false; progress.AccessibleName = "安装进度";
        detailsLink.Location = new Point(0, 146); detailsLink.Size = new Size(100, 24);
        detailsLink.Text = "安装详情"; detailsLink.LinkColor = Color.FromArgb(108, 101, 120);
        detailsLink.ActiveLinkColor = Purple; detailsLink.VisitedLinkColor = Purple;
        detailsLink.LinkBehavior = LinkBehavior.HoverUnderline; detailsLink.TabIndex = 2;
        detailsLink.LinkClicked += (_, _) => ToggleDetails();
        details.Location = new Point(0, 176); details.Size = new Size(456, 100);
        details.ForeColor = Color.FromArgb(108, 101, 120); details.BackColor = Color.White;
        details.BorderStyle = BorderStyle.None; details.Visible = false; details.ReadOnly = true;
        details.Multiline = true; details.ScrollBars = ScrollBars.Vertical; details.TabIndex = 3;
        details.AccessibleName = "安装详情";
        UpdateDetails("安装到当前用户目录，添加开始菜单入口。安装时下载并校验所需数据，请保持网络连接。\r\n打开工具不会读取游戏；开始读取仍需在网页确认并允许管理员权限。");
        card.Controls.AddRange([text, progress, detailsLink, details]); Controls.Add(card);
        StyleButton(action, "开始安装", true); action.TabIndex = 0;
        StyleButton(cancel, "关闭", false); cancel.TabIndex = 1;
        StyleButton(uninstall, "卸载", false); uninstall.TabIndex = 4; uninstall.Visible = false;
        uninstall.Click += async (_, _) => await UninstallAsync();
        Controls.AddRange([action, cancel, uninstall]); AcceptButton = action; CancelButton = cancel;
        AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
        ResumeLayout(false); PerformLayout(); LayoutFooter();
        Shown += (_, _) => LayoutFooter(); DpiChanged += (_, _) => BeginInvoke(LayoutFooter);
    }

    private static void StyleButton(Button button, string label, bool primary)
    {
        button.Text = label; button.Size = new Size(primary ? 124 : 84, 36);
        button.BackColor = primary ? Purple : Color.White;
        button.ForeColor = primary ? Color.White : Color.FromArgb(108, 101, 120);
        button.FlatStyle = FlatStyle.Flat; button.Cursor = Cursors.Hand; button.UseVisualStyleBackColor = false;
        button.FlatAppearance.BorderSize = primary ? 0 : 1;
        button.FlatAppearance.BorderColor = primary ? Purple : Color.FromArgb(236, 233, 242);
        button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(91, 73, 132) : Color.FromArgb(248, 246, 252);
    }

    private int Unit(int logical) => (int)Math.Round(logical * card.Width / 456f);
    private void LayoutFooter()
    {
        action.Location = new Point(ClientSize.Width - Unit(32) - action.Width, ClientSize.Height - Unit(58));
        cancel.Location = new Point(Unit(32), ClientSize.Height - Unit(58));
        uninstall.Location = new Point(Unit(126), ClientSize.Height - Unit(58));
    }
    private void UpdateDetails(string message) => details.Text = message;
    internal void SetInstalledVersion(string? current)
    {
        if (current is null) return;
        card.Controls["version"]!.Text = "当前 " + current + "  →  安装 " + InstallEngine.Version;
        var upgrade = current != InstallEngine.Version;
        action.Text = upgrade ? "升级安装" : "修复安装";
        uninstall.Visible = true;
        text.Text = upgrade ? "已有资产快读，可以升级到新版本。" : "已检测到资产快读，可以修复安装。";
    }
    private async Task UninstallAsync()
    {
        if ((previewOnly && testOperations is null) || running is not null) return;
        if (testOperations is null && MessageBox.Show("卸载资产快读？\n读取结果和账户数据会保留。", "Soda Terminal · 资产快读",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        running = new CancellationTokenSource(); action.Enabled = cancel.Enabled = uninstall.Enabled = false;
        text.Text = "正在卸载资产快读…"; progress.Visible = true; progress.Indeterminate = true;
        try
        {
            if (testOperations is not null) await testOperations.Uninstall();
            else
            {
            using var stub = InstallRegistration.OpenUninstaller();
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stub));
            await Task.Run(() => AssetUninstall.Remove(InstallEngine.DefaultRoot, InstallEngine.ReadCatalog(), new UserRegistration(), new RegistryUninstallStore(), hash));
            }
            text.Text = "已卸载，读取结果和账户数据已保留。";
            UpdateDetails("资产快读的程序与入口已移除。读取结果和未知文件保留，可重新安装。");
            installed = null; serviceStarted = false; uninstall.Visible = false; action.Text = "开始安装";
            card.Controls["version"]!.Text = "安装版本 " + InstallEngine.Version;
            progress.Value = 100;
        }
        catch (Exception error) { text.Text = "卸载未完成，请关闭资产快读后重试。"; UpdateDetails(error.Message); progress.Visible = false; }
        finally { progress.Indeterminate = false; running.Dispose(); running = null; action.Enabled = cancel.Enabled = uninstall.Enabled = true; }
    }
    internal void ToggleDetails()
    {
        details.Visible = !details.Visible; detailsLink.Text = details.Visible ? "收起详情" : "安装详情";
        card.Height = Unit(details.Visible ? 284 : 180);
        ClientSize = new Size(ClientSize.Width, Unit(details.Visible ? 388 : 284)); LayoutFooter();
    }
    private void ShowInstallError(string message)
    {
        text.Text = message == "another_installer_is_running" ? "已有安装正在进行，请稍后重试。"
            : message.Contains("payload") || message.Contains("archive") || message.Contains("catalog")
                ? "安装包不完整，请重新下载。" : "安装未完成，请检查网络后重试。";
        UpdateDetails(message + "\r\n已有版本、读取结果和账户已保留。");
        progress.Visible = false; action.Text = "重试";
    }

    // Internal fixture does not run install, protocol, browser or service actions.
    internal void ApplyRenderFixture(string state)
    {
        switch (state)
        {
            case "initial": break;
            case "preparing": text.Text = "正在准备组件…"; progress.Visible = true; progress.Indeterminate = true; action.Visible = false; cancel.Text = "取消"; break;
            case "progress": text.Text = "正在安装资产快读…"; progress.Visible = true; progress.Value = 47; action.Visible = false; cancel.Text = "取消"; break;
            case "completion": text.Text = "安装完成，打开工具后返回网页连接。"; progress.Visible = true; progress.Value = 100; action.Text = "打开工具"; break;
            case "started": text.Text = "工具已打开，请返回网页连接。"; action.Text = "返回网页"; break;
            case "cancelled": text.Text = "安装已取消，可以重新安装。"; action.Text = "重新安装"; break;
            case "error": ShowInstallError("synthetic_download_failure"); break;
            default: throw new InvalidOperationException("test_preview_state_invalid");
        }
    }
    private sealed class SodaProgressBar : ProgressBar
    {
        private readonly System.Windows.Forms.Timer animation = new() { Interval = 40 };
        private int offset;
        private bool indeterminate;
        public bool Indeterminate { set { indeterminate = value; animation.Enabled = value; Invalidate(); } }
        internal SodaProgressBar()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            animation.Tick += (_, _) => { offset = (offset + 7) % Math.Max(1, Width + Width / 3); Invalidate(); };
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Color.FromArgb(239, 235, 247)); using var fill = new SolidBrush(Purple);
            if (indeterminate) e.Graphics.FillRectangle(fill, offset - Width / 3, 0, Width / 3, Height);
            else e.Graphics.FillRectangle(fill, 0, 0, Width * Value / Math.Max(1, Maximum), Height);
        }
        protected override void Dispose(bool disposing) { if (disposing) animation.Dispose(); base.Dispose(disposing); }
        protected override void WndProc(ref Message m) { base.WndProc(ref m); if (m.Msg == 0x402) Invalidate(); }
    }
}
