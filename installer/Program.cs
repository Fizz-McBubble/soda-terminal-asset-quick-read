using System.Diagnostics;
namespace Soda.AssetQuickRead.Setup;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--self-test") return Tests.Run(args[1]);
        if (args.Length != 0) return 64;
        ApplicationConfiguration.Initialize(); Application.Run(new InstallForm()); return 0;
    }
}
internal sealed class InstallForm : Form
{
    private readonly Label text = new() { Dock = DockStyle.Top, Height = 185, Padding = new Padding(24), Text = "Soda Terminal · 资产快读\r\n\r\n独立本机工具，内嵌运行时与驱动文件。\r\n点击安装后，从 GitHub 固定公开来源下载3个协议数据文件，核对 SHA-256。\r\n仅安装到当前用户目录并注册启动协议。\r\n启动只打开本机服务；采集仍需网页确认风险并单独授权 UAC。\r\n不会自动读取游戏、加载驱动或写入账户。" };
    private readonly ProgressBar progress = new() { Dock = DockStyle.Top, Height = 22 };
    private readonly Button action = new() { Text = "安装", Width = 130, Height = 40 };
    private readonly Button cancel = new() { Text = "关闭", Width = 90, Height = 40 };
    private CancellationTokenSource? running;
    private string? installed;
    internal InstallForm()
    {
        Text = "Soda Terminal 资产快读安装"; Width = 620; Height = 340; MinimumSize = new Size(620, 340); StartPosition = FormStartPosition.CenterScreen;
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 70, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(20, 10, 20, 10) };
        buttons.Controls.Add(cancel); buttons.Controls.Add(action); Controls.Add(progress); Controls.Add(text); Controls.Add(buttons);
        action.Click += async (_, _) => await InstallOrOpen();
        cancel.Click += (_, _) => { if (running is not null) { running.Cancel(); text.Text = "正在取消并回滚本次新建文件…"; } else Close(); };
        FormClosing += (_, e) => { if (running is not null) { e.Cancel = true; running.Cancel(); } };
    }
    private async Task InstallOrOpen()
    {
        if (installed is not null)
        {
            try
            {
                using var launched = Process.Start(new ProcessStartInfo(Path.Combine(installed, "Soda.AssetQuickRead.Bridge.exe"), "--serve") { UseShellExecute = true, WorkingDirectory = installed }) ?? throw new IOException("launch_failed");
                await Task.WhenAny(launched.WaitForExitAsync(), Task.Delay(700));
                if (launched.HasExited && launched.ExitCode != 0) throw new IOException("local_service_start_failed");
                Close();
            }
            catch { text.Text = "工具启动失败。安装文件已保留，可以重试打开或重新运行安装器。"; }
            return;
        }
        running = new CancellationTokenSource(); action.Enabled = false; cancel.Text = "取消";
        var token = running.Token;
        var reporter = new Progress<int>(value => { progress.Value = value; text.Text = $"安装到当前用户目录… {value}%\r\n正在复制、下载固定协议数据并逐项核对 SHA-256。采集尚未启动。"; });
        string? temporary = null;
        try
        {
            using var operation = new Mutex(false, "Local\\SodaTerminal.AssetQuickRead.Setup", out _);
            bool acquired; try { acquired = operation.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new IOException("another_installer_is_running");
            try
            {
                var payload = Path.Combine(AppContext.BaseDirectory, "payload");
                text.Text = "准备离线文件…可以取消。";
                using var embedded = typeof(InstallEngine).Assembly.GetManifestResourceStream("payload.zip");
                if (embedded is not null)
                {
                    temporary = Path.Combine(Path.GetTempPath(), "SodaAssetQuickRead-" + Guid.NewGuid().ToString("N"));
                    InstallEngine.NoLinks(temporary); Directory.CreateDirectory(temporary); payload = temporary;
                    using var output = new FileStream(Path.Combine(payload, "asset-quick-read.zip"), FileMode.CreateNew, FileAccess.Write);
                    await embedded.CopyToAsync(output, token); await output.FlushAsync(token);
                }
                else
                    for (var attempt = 0; !File.Exists(Path.Combine(payload, "ready")); attempt++)
                    { token.ThrowIfCancellationRequested(); if (File.Exists(Path.Combine(payload, "failed")) || attempt >= 600) throw new IOException("offline_payload_unavailable"); await Task.Delay(200, token); }
                installed = await Task.Run(() => InstallEngine.Install(Path.Combine(payload, "asset-quick-read.zip"), InstallEngine.ReadCatalog(), InstallEngine.DefaultRoot, new UserRegistration(), token, ((IProgress<int>)reporter).Report), token);
            }
            finally { operation.ReleaseMutex(); }
            text.Text = "安装完成。\r\n\r\n打开工具后，请回网页连接。当前还没有读取游戏或启动采集。\r\n开始菜单也可打开「Soda Terminal 资产快读」。";
            action.Text = "打开工具";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { text.Text = "安装已取消，本次新建文件已回滚。可以重试安装。"; action.Text = "重新安装"; }
        catch (Exception error) { text.Text = "安装未完成：" + error.Message + "\r\n已有版本、jobs 和账户不受影响。可修正后重试或重新打开安装器。"; action.Text = "重试"; }
        finally
        {
            if (temporary is not null && Directory.Exists(temporary))
            { InstallEngine.NoLinks(temporary); File.Delete(Path.Combine(temporary, "asset-quick-read.zip")); Directory.Delete(temporary); }
            running.Dispose(); running = null; action.Enabled = true; cancel.Text = "关闭";
        }
    }
}
