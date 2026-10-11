using System.Diagnostics;
namespace Soda.AssetQuickRead.Setup;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--inspect-install") return InstallInspection.Run(args[1]);
        if (args.Length == 2 && args[0] == "--self-test") return Tests.Run(args[1]);
        if (args.Length == 3 && args[0] == "--upgrade-self-test") return UpgradeIdentityTests.Run(args[1], args[2]);
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
    private bool serviceStarting;
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
            SetInstalledVersion(current, HasInstallationTraces(InstallEngine.DefaultRoot, new RegistryUninstallStore().Read()));
        }
        action.Click += async (_, _) => await InstallOrOpen();
        cancel.Click += (_, _) => { if (running is not null) { running.Cancel(); text.Text = "正在取消安装…"; cancel.Enabled = false; } else Close(); };
        FormClosing += (_, e) => { if (running is not null || serviceStarting) { e.Cancel = true; running?.Cancel(); } };
    }
    // UI discovery is separate from uninstall authority. Removal still validates
    // the catalog, exact registration and uninstaller identities.
    internal static bool HasInstallationTraces(string root, UninstallRecord? registration)
    {
        try
        {
            InstallEngine.NoLinks(root);
            var expected = InstallRegistration.Record(Path.GetFullPath(root));
            if (registration is { OwnedShape: true } && registration == expected with { Version = registration.Version }) return true;
            if (File.Exists(InstallEngine.Safe(root, "uninstall.exe"))) return true;
            foreach (var version in InstallRegistration.PublishedVersions.Prepend(InstallEngine.Version).Distinct())
                if (File.Exists(InstallEngine.Safe(root, "versions/" + version + "/Soda.AssetQuickRead.Bridge.exe"))) return true;
            return false;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
    private async Task InstallOrOpen()
    {
        if (previewOnly && testOperations is null) return;
        if (running is not null || serviceStarting) return;
        if (serviceStarted)
        {
            Close();
            return;
        }
        if (installed is not null)
        {
            await StartInstalledService();
            return;
        }
        running = new CancellationTokenSource(); action.Enabled = false; action.Visible = false; cancel.Text = "取消";
        uninstall.Enabled = false;
        progress.Visible = true; progress.Value = 0; progress.Indeterminate = true;
        var token = running.Token;
        var diagnostics = new InstallerDiagnostics(Path.Combine(testOperations?.Root ?? InstallEngine.DefaultRoot, "installer-diagnostics"), phase =>
        {
            if (!IsHandleCreated) return;
            BeginInvoke(() => { if (running is null) return; text.Text = phase switch
                { "protocol" => "正在获取并校验所需数据…", "verify" => "正在校验安装文件…", "register" => "正在添加工具入口…", "copy" => "正在写入安装文件…", _ => text.Text }; });
        });
        diagnostics.Record("prepare");
        var reporter = new Progress<int>(value => { if (running is null) return; progress.Indeterminate = false; progress.Value = Math.Clamp(value, 0, 100); });
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
                installed = await Task.Run(() => InstallEngine.Install(Path.Combine(payload, "asset-quick-read.zip"), InstallEngine.ReadCatalog(), InstallEngine.DefaultRoot, new InstallRegistration(InstallEngine.DefaultRoot, diagnostics), token, ((IProgress<int>)reporter).Report, diagnostics: diagnostics), token);
            }
            finally { operation.ReleaseMutex(); }
            }
            diagnostics.Record("complete", "completed");
            uninstall.Visible = true;
            await StartInstalledService();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { diagnostics.Record("cancelled", "installer_cancelled"); text.Text = "安装已取消，可以重新安装。"; UpdateDetails("本次新建安装文件已回滚，已有版本和读取结果保留。"); progress.Visible = false; action.Text = "重新安装"; }
        catch (Exception error) { var code = InstallerDiagnostics.Code(error); diagnostics.Record("failed", code); ShowInstallError(code); }
        finally
        {
            if (temporary is not null && Directory.Exists(temporary))
            { InstallEngine.NoLinks(temporary); File.Delete(Path.Combine(temporary, "asset-quick-read.zip")); Directory.Delete(temporary); }
            running.Dispose(); running = null; progress.Indeterminate = false; action.Visible = true; action.Enabled = true; cancel.Enabled = true; uninstall.Enabled = true; cancel.Text = "关闭";
        }
    }
    private async Task StartInstalledService()
    {
        if (installed is null || serviceStarted || serviceStarting) return;
        serviceStarting = true;
        action.Enabled = cancel.Enabled = uninstall.Enabled = false;
        action.Visible = false; cancel.Text = "关闭";
        text.Text = "安装完成，正在启动连接服务…";
        progress.Visible = true; progress.Value = 100; progress.Indeterminate = false;
        try
        {
            await Dispatch(new ProcessStartInfo(Path.Combine(installed, "Soda.AssetQuickRead.Bridge.exe"), "--serve")
                { UseShellExecute = true, WorkingDirectory = installed }, true);
            serviceStarted = true;
            text.Text = "安装完成，请返回网页连接。";
            UpdateDetails("资产快读连接服务已启动。开始读取仍需在网页确认并允许管理员权限。\r\n读取结果和账户数据已保留。");
            action.Text = "完成";
        }
        catch
        {
            text.Text = "安装完成，连接服务未启动，请重试启动。";
            UpdateDetails("连接服务启动失败。请关闭已有资产快读后重试启动。\r\n读取结果和账户数据已保留，无需重新安装。");
            action.Text = "重试启动";
        }
        finally
        {
            serviceStarting = false; action.Visible = true;
            action.Enabled = cancel.Enabled = uninstall.Enabled = running is null;
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
