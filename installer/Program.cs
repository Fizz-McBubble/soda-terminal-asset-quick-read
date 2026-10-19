using System.Diagnostics;
namespace Soda.AssetQuickRead.Setup;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--inspect-install") return InstallInspection.Run(args[1]);
        if (args.Length == 2 && args[0] == "--self-test") return Tests.Run(args[1]);
        if (args.Length == 2 && args[0] == "--uninstall-self-test") return UninstallVersionTests.RunStandalone(args[1]);
        if (args.Length == 2 && args[0] == "--feedback-self-test") { ApplicationConfiguration.Initialize(); return InstallerFeedbackTests.Run(args[1]); }
        if (args.Length == 3 && args[0] == "--upgrade-self-test") return UpgradeIdentityTests.Run(args[1], args[2]);
        if (args.Length == 2 && args[0] == "--retention-self-test") return VersionRetentionTests.Run(args[1]);
        if (args.Length == 4 && args[0] == "--protocol-self-test") return ProtocolDownloadTests.RunStandalone(args[1], args[2], args[3]);
        if (args.Length == 5 && args[0] == "--service-recovery-self-test") return ServiceRecoveryTests.Run(args[1], args[2], args[3], args[4]);
        if (args.Length == 4 && args[0] == "--published-service-recovery-self-test") return ServiceRecoveryTests.RunPublished113(args[1], args[2], args[3]);
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
    private bool installationCommitted;
    private readonly bool previewOnly;
    private readonly FormOperations? testOperations;
    private Soda.AssetQuickRead.ServiceReadiness.VerifiedOldBridge? pendingOldBridge;
    private InstallerDiagnostics? activeDiagnostics;
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
        cancel.Click += (_, _) => { if (running is not null) { if (installationCommitted) return; running.Cancel(); text.Text = "正在取消安装…"; cancel.Enabled = false; } else Close(); };
        FormClosing += (_, e) => { if (running is not null || serviceStarting) { e.Cancel = true; if (!installationCommitted) running?.Cancel(); } };
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
        if (pendingOldBridge is not null)
        {
            await SwitchOldService();
            return;
        }
        if (installed is not null)
        {
            await StartInstalledService();
            return;
        }
        running = new CancellationTokenSource(); installationCommitted = false; action.Enabled = false; action.Visible = false; cancel.Text = "取消";
        uninstall.Enabled = false; feedback.Visible = false; lastFeedbackReport = null; lastFeedbackUrl = null;
        progress.Visible = true; progress.Value = 0; progress.Indeterminate = true;
        var token = running.Token;
        var diagnostics = new InstallerDiagnostics(Path.Combine(testOperations?.Root ?? InstallEngine.DefaultRoot, "installer-diagnostics"), phase =>
        {
            if (!IsHandleCreated) return;
            BeginInvoke(() => { if (running is null) return; text.Text = phase switch
                { "protocol" => "正在获取并校验所需数据…", "verify" => "正在校验安装文件…", "register" => "正在添加工具入口…", "copy" => "正在写入安装文件…", _ => text.Text }; });
        });
        activeDiagnostics = diagnostics;
        diagnostics.Record("prepare");
        var reporter = new Progress<int>(value => { if (running is null) return; progress.Indeterminate = false; progress.Value = Math.Clamp(value, 0, 100); if (value >= 100) { installationCommitted = true; cancel.Enabled = false; text.Text = "正在完成安装…"; } });
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
                diagnostics.SetOperation("payload_path");
                using var embedded = typeof(InstallEngine).Assembly.GetManifestResourceStream("payload.zip");
                if (embedded is not null)
                {
                    diagnostics.SetOperation("payload_extract");
                    temporary = Path.Combine(Path.GetTempPath(), "SodaAssetQuickRead-" + Guid.NewGuid().ToString("N"));
                    InstallEngine.NoLinks(temporary); Directory.CreateDirectory(temporary); payload = temporary;
                    using var output = new FileStream(Path.Combine(payload, "asset-quick-read.zip"), FileMode.CreateNew, FileAccess.Write);
                    await embedded.CopyToAsync(output, token); await output.FlushAsync(token);
                }
                else
                    for (var attempt = 0; !File.Exists(Path.Combine(payload, "ready")); attempt++)
                    { diagnostics.SetOperation("payload_wait"); token.ThrowIfCancellationRequested(); if (File.Exists(Path.Combine(payload, "failed")) || attempt >= 600) throw new IOException("offline_payload_unavailable"); await Task.Delay(200, token); }
                installed = await Task.Run(() => InstallEngine.Install(Path.Combine(payload, "asset-quick-read.zip"), InstallEngine.ReadCatalog(), InstallEngine.DefaultRoot, new InstallRegistration(InstallEngine.DefaultRoot, diagnostics), token, ((IProgress<int>)reporter).Report, diagnostics: diagnostics), token);
            }
            finally { operation.ReleaseMutex(); }
            }
            diagnostics.Record("complete", "completed");
            card.Controls["version"]!.Text = "当前 " + InstallEngine.Version;
            uninstall.Visible = true;
            await StartInstalledService();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { diagnostics.Record("cancelled", "installer_cancelled"); text.Text = "安装已取消，可以重新安装。"; UpdateDetails("本次新建安装文件已回滚，已有版本和读取结果保留。"); progress.Visible = false; action.Text = "重新安装"; feedback.Visible = false; lastFeedbackReport = null; lastFeedbackUrl = null; }
        catch (Exception error) { var code = InstallerDiagnostics.Code(error); diagnostics.CaptureFailure(error); diagnostics.Record("failed", code); ShowInstallError(code, diagnostics); }
        finally
        {
            if (temporary is not null && Directory.Exists(temporary))
            { InstallEngine.NoLinks(temporary); File.Delete(Path.Combine(temporary, "asset-quick-read.zip")); Directory.Delete(temporary); }
            running.Dispose(); running = null; progress.Indeterminate = false; action.Visible = true; action.Enabled = true; cancel.Enabled = true; uninstall.Enabled = true; cancel.Text = "关闭";
        }
    }
    private async Task SwitchOldService()
    {
        if (pendingOldBridge is null || installed is null || serviceStarting || running is not null) return;
        var oldBridge = pendingOldBridge;
        const string confirmText = "请先在网页取消或完成正在进行的读取，再切换到新版。\n切换将结束旧版连接服务，但不会保证已提权的读取子进程一同结束。\n已经保存的读取结果和账户数据保留。\n\n是否已完成或取消读取，并确认结束旧版连接服务、切换到新版？";
        const string confirmCaption = "Soda Terminal · 资产快读";
        bool confirmed;
        if (testOperations is not null)
        {
            confirmed = testOperations.ConfirmSwitch?.Invoke() ?? false;
        }
        else
        {
            confirmed = MessageBox.Show(confirmText, confirmCaption, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
        }
        if (!confirmed) return;

        serviceStarting = true;
        action.Enabled = cancel.Enabled = uninstall.Enabled = false;
        action.Visible = false; cancel.Text = "关闭";
        text.Text = "正在切换到新版连接服务…";
        progress.Visible = true; progress.Value = 100; progress.Indeterminate = true;

        try
        {
            if (testOperations?.SwitchOldService is not null)
            {
                var switched = await testOperations.SwitchOldService(oldBridge);
                if (!switched) throw new IOException("service_switch_race_rejected");
            }
            else
            {
                var switched = await Soda.AssetQuickRead.ServiceReadiness.RecheckAndTerminate(oldBridge);
                if (!switched) throw new IOException("service_switch_race_rejected");
            }

            pendingOldBridge = null;
            await Dispatch(new ProcessStartInfo(Path.Combine(installed, "Soda.AssetQuickRead.Bridge.exe"), "--serve")
                { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = installed }, true);
            serviceStarted = true;
            text.Text = "安装完成，请返回网页连接。";
            UpdateDetails("资产快读连接服务已切换到新版并启动。开始读取仍需在网页确认并允许管理员权限。\r\n读取结果和账户数据已保留。");
            action.Text = "完成";
        }
        catch (Exception error)
        {
            pendingOldBridge = null;
            var isRace = error is IOException io && io.Message == "service_switch_race_rejected";
            text.Text = "切换未完成，连接服务未启动，请重试启动。";
            if (isRace)
            {
                UpdateDetails("切换前检测到服务状态、文件或端口占用发生变化，已中止切换以保护现有进程。\r\n读取结果和账户数据已保留，请手动检查后重试启动。");
            }
            else
            {
                UpdateDetails("连接服务切换或启动失败。请重试；若仍失败，请查看网页诊断。\r\n工具不会停止未知进程，读取结果和账户数据已保留。");
            }
            action.Text = "重试启动";
        }
        finally
        {
            serviceStarting = false;
            progress.Indeterminate = false;
            action.Visible = true;
            action.Enabled = cancel.Enabled = uninstall.Enabled = running is null;
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
            var targetExe = Path.Combine(installed, "Soda.AssetQuickRead.Bridge.exe");
            var start = new ProcessStartInfo(targetExe, "--serve")
                { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = installed };

            if (testOperations is null)
            {
                if (await AlreadyRunning(installed))
                {
                    serviceStarted = true;
                    text.Text = "安装完成，请返回网页连接。";
                    UpdateDetails("资产快读连接服务已启动。开始读取仍需在网页确认并允许管理员权限。\r\n读取结果和账户数据已保留。");
                    action.Text = "完成";
                    return;
                }
            }

            Soda.AssetQuickRead.ServiceReadiness.VerifiedOldBridge? oldBridge = null;
            if (testOperations?.InspectOldService is not null)
            {
                oldBridge = await testOperations.InspectOldService();
            }
            else if (testOperations is null)
            {
                oldBridge = await Soda.AssetQuickRead.ServiceReadiness.InspectAnyKnownBridge();
            }

            if (oldBridge is not null)
            {
                pendingOldBridge = oldBridge;
                text.Text = "安装完成，检测到旧版连接服务，可切换到新版。";
                UpdateDetails($"检测到正在运行的旧版资产快读连接服务（版本 {oldBridge.Version}）。\r\n请先在网页取消或完成读取，再点击“切换到新版”并确认。\r\n切换只结束旧版连接服务，已提权读取进程可能继续运行；已保存结果和账户数据保留。");
                action.Text = "切换到新版";
                return;
            }

            await Dispatch(start, true);
            serviceStarted = true;
            text.Text = "安装完成，请返回网页连接。";
            UpdateDetails("资产快读连接服务已启动。开始读取仍需在网页确认并允许管理员权限。\r\n读取结果和账户数据已保留。");
            action.Text = "完成";
        }
        catch (Exception error)
        {
            var code = InstallerDiagnostics.Code(error);
            var diagnostics = activeDiagnostics ??= new InstallerDiagnostics(Path.Combine(testOperations?.Root ?? InstallEngine.DefaultRoot, "installer-diagnostics"));
            diagnostics.Record("service_start", code);
            diagnostics.CaptureFailure(error);
            ShowServiceStartError(code, diagnostics);
        }
        finally
        {
            serviceStarting = false; action.Visible = true;
            action.Enabled = cancel.Enabled = uninstall.Enabled = running is null;
        }
    }
    internal void ShowServiceStartError(string code, InstallerDiagnostics? diagnostics = null)
    {
        text.Text = "安装完成，连接服务未启动，请重试启动。";
        UpdateDetails(ServiceStartMessage(code) + "\r\n诊断代码：" + code + "\r\n请点“反馈安装问题”。读取结果和账户保留，无需重新安装。");
        lastFeedbackReport = diagnostics?.BuildSafeReport(code) ?? InstallerFeedback.CreateSafeReport("service_start", code, 0, []);
        lastFeedbackUrl = InstallerFeedback.BuildLaunchUrl(lastFeedbackReport);
        feedback.Visible = true;
        action.Text = "重试启动";
    }
    internal static string ServiceStartMessage(string code) => code switch
    {
        "local_service_identity_unavailable" => "服务身份未能确认。先完成或取消读取，保存工作并重启电脑后重试；不要结束未知进程。",
        "local_service_port_conflict" => "全部连接地址被占用。请保存工作并重启电脑后重试；仍失败请反馈，不要结束未知进程。",
        "local_service_address_unavailable" => "Windows 拒绝全部连接地址。保存工作并重启电脑后重试；仍失败请反馈并保留拦截提示，无需关闭防护。",
        "local_service_bind_or_identity_unavailable" => "连接地址或服务身份不可用，原因未明。先完成或取消读取，保存工作并重启电脑后重试；不要结束未知进程。",
        "local_service_readiness_timeout" => "连接服务未及时通过检查。先重试启动；仍失败请完成或取消读取，保存工作并重启电脑后再试。",
        "local_service_start_failed" => "连接服务启动失败；原因尚未确定，请查看本机诊断后重试。",
        _ => "连接服务未通过启动与身份检查；原因尚未确定，请查看本机诊断后重试。",
    };
    private static async Task<bool> AlreadyRunning(string installed)
    {
        foreach (var port in Soda.AssetQuickRead.ServiceReadiness.ServicePorts)
            if (await Soda.AssetQuickRead.ServiceReadiness.TrustedExisting(installed, InstallEngine.BridgeVersion, port, "SodaTerminal.AssetQuickRead.Identity.50609")) return true;
        return false;
    }
    private async Task Dispatch(ProcessStartInfo start, bool service)
    {
        if (testOperations is not null) { await testOperations.Dispatch(start); return; }
        if (service)
        {
            await Soda.AssetQuickRead.ServiceReadiness.Start(start, InstallEngine.BridgeVersion, 50609, "SodaTerminal.AssetQuickRead.Identity.50609");
            return;
        }
        using var launched = Process.Start(start) ?? throw new IOException("launch_failed");
    }
}
internal sealed record FormOperations(string Root,
    Func<IProgress<int>, CancellationToken, Task<string>> Install,
    Func<ProcessStartInfo, Task> Dispatch,
    Func<Task> Uninstall,
    Func<Task<Soda.AssetQuickRead.ServiceReadiness.VerifiedOldBridge?>>? InspectOldService = null,
    Func<Soda.AssetQuickRead.ServiceReadiness.VerifiedOldBridge, Task<bool>>? SwitchOldService = null,
    Func<bool>? ConfirmSwitch = null,
    Action<string>? LaunchBrowser = null,
    Action<string>? SetClipboard = null);
