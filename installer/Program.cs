using System.Diagnostics;
namespace Soda.AssetQuickRead.Setup;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--self-test") return Tests.Run(args[1]);
        if (args.Length > 0 && args[0] == "--uninstall") return AssetUninstall.Run(args);
        if (args.Length != 0) return 64;
        ApplicationConfiguration.Initialize(); Application.Run(new InstallForm()); return 0;
    }
}
internal sealed partial class InstallForm : Form
{
    private CancellationTokenSource? running;
    private string? installed;
    private bool serviceStarted;
    private readonly bool previewOnly;
    private readonly FormOperations? testOperations;
    internal InstallForm(bool preview = false, FormOperations? testOperations = null)
    {
        if (testOperations != null && (!preview || Path.GetFullPath(testOperations.Root) == Path.GetFullPath(InstallEngine.DefaultRoot))) throw new InvalidOperationException("isolated_form_operations_required");
        previewOnly = preview; this.testOperations = testOperations;
        InitializeInterface();
        if (!preview)
        {
            var current = new UserRegistration().InstalledVersion(InstallEngine.DefaultRoot);
            SetInstalledVersion(current);
        }
        action.Click += async (_, _) => await InstallOrOpen();
        cancel.Click += (_, _) => { if (running is not null) { running.Cancel(); text.Text = "正在取消安装…"; cancel.Enabled = false; } else Close(); };
        FormClosing += (_, e) => { if (running is not null) { e.Cancel = true; running.Cancel(); } };
    }
    private async Task InstallOrOpen()
    {
        if (previewOnly && testOperations is null) return;
        if (serviceStarted)
        {
            try { await Dispatch(new ProcessStartInfo("https://sodaterminal.com/system/scanner") { UseShellExecute = true }, false); Close(); }
            catch { text.Text = "网页未打开，请在浏览器访问 sodaterminal.com。"; }
            return;
        }
        if (installed is not null)
        {
            try
            {
                await Dispatch(new ProcessStartInfo(Path.Combine(installed, "Soda.AssetQuickRead.Bridge.exe"), "--serve") { UseShellExecute = true, WorkingDirectory = installed }, true);
                serviceStarted = true; text.Text = "工具已打开，请返回网页连接。"; action.Text = "返回网页";
            }
            catch (Exception error) { text.Text = "工具未打开，请关闭资产快读后重试。"; UpdateDetails(error.Message); }
            return;
        }
        running = new CancellationTokenSource(); action.Enabled = false; action.Visible = false; cancel.Text = "取消";
        uninstall.Enabled = false;
        progress.Visible = true; progress.Value = 0; progress.Indeterminate = true;
        var token = running.Token;
        var reporter = new Progress<int>(value => { if (running is null) return; progress.Indeterminate = false; progress.Value = Math.Clamp(value, 0, 100); text.Text = "正在安装资产快读…"; });
        string? temporary = null;
        try
        {
            if (testOperations is not null) installed = await testOperations.Install(reporter, token);
            else
            {
            using var operation = new Mutex(false, "Local\\SodaTerminal.AssetQuickRead.Setup", out _);
            bool acquired; try { acquired = operation.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new IOException("another_installer_is_running");
            try
            {
                var payload = Path.Combine(AppContext.BaseDirectory, "payload");
                text.Text = "正在准备组件…";
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
                installed = await Task.Run(() => InstallEngine.Install(Path.Combine(payload, "asset-quick-read.zip"), InstallEngine.ReadCatalog(), InstallEngine.DefaultRoot, new InstallRegistration(InstallEngine.DefaultRoot), token, ((IProgress<int>)reporter).Report), token);
            }
            finally { operation.ReleaseMutex(); }
            }
            text.Text = "安装完成，打开工具后返回网页连接。";
            UpdateDetails("资产快读已安装。开始菜单也可打开「Soda Terminal 资产快读」。当前没有读取游戏或启动采集。");
            action.Text = "打开工具";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { text.Text = "安装已取消，可以重新安装。"; UpdateDetails("本次新建安装文件已回滚，已有版本和读取结果保留。"); progress.Visible = false; action.Text = "重新安装"; }
        catch (Exception error) { ShowInstallError(error.Message); }
        finally
        {
            if (temporary is not null && Directory.Exists(temporary))
            { InstallEngine.NoLinks(temporary); File.Delete(Path.Combine(temporary, "asset-quick-read.zip")); Directory.Delete(temporary); }
            running.Dispose(); running = null; progress.Indeterminate = false; action.Visible = true; action.Enabled = true; cancel.Enabled = true; uninstall.Enabled = true; cancel.Text = "关闭";
        }
    }
    private async Task Dispatch(ProcessStartInfo start, bool service)
    {
        if (testOperations is not null) { await testOperations.Dispatch(start); return; }
        using var launched = Process.Start(start) ?? throw new IOException("launch_failed");
        if (!service) return;
        await Task.WhenAny(launched.WaitForExitAsync(), Task.Delay(700));
        if (launched.HasExited && launched.ExitCode != 0) throw new IOException("local_service_start_failed");
    }
}
internal sealed record FormOperations(string Root,
    Func<IProgress<int>, CancellationToken, Task<string>> Install,
    Func<ProcessStartInfo, Task> Dispatch,
    Func<Task> Uninstall);
