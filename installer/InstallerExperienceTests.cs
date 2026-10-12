namespace Soda.AssetQuickRead.Setup;

internal static class InstallerExperienceTests
{
    private sealed class ProtocolStore : IProtocolRegistrationStore
    {
        internal ProtocolRegistration? Value;
        internal bool IgnoreCreate;
        public ProtocolRegistration? Read() => Value;
        public void Create(string command) { if (!IgnoreCreate) Value = new(UserRegistration.Description, "", command, true); }
        public bool Replace(ProtocolRegistration expected, string command) { if (Value != expected) return false; Value = expected with { Command = command }; return true; }
        public void RemoveCreated(string command) { if (Value?.Command == command && Value.OwnedShape) Value = null; }
    }
    private sealed class UninstallStore : IUninstallStore
    {
        internal UninstallRecord? Value;
        internal bool Fail;
        internal bool IgnoreWrite;
        public UninstallRecord? Read() => Value;
        public void Write(UninstallRecord value) { if (!IgnoreWrite) Value = value; if (Fail) throw new IOException("synthetic_registration_failure"); }
        public bool Remove(UninstallRecord value) { if (Value != value) return false; Value = null; return true; }
    }
    internal static int RunFullPackage(string install, string archive, Catalog catalog, string privatePayload)
    {
        var protocol = new ProtocolStore(); var store = new UninstallStore();
        var shortcut = Path.Combine(install, "synthetic-start-menu", "asset-reader.url");
        var registration = new UserRegistration(protocol, shortcut);
        var legacyFixture = Environment.GetEnvironmentVariable("SODA_ASSET_QUICK_READ_LEGACY_FIXTURE");
        var legacy = Path.Combine(install, "versions", "1.0.0");
        if (legacyFixture != null)
        {
            foreach (var pin in AssetUninstall.Published100Catalog().Files)
            {
                var source = InstallEngine.Safe(legacyFixture, pin.Path);
                if (!File.Exists(source)) source = InstallEngine.Safe(Path.GetFullPath(Path.Combine(legacyFixture, "..", "public-embedded-payload")), pin.Path);
                if (new FileInfo(source).Length != pin.Size || InstallEngine.Digest(source) != pin.Sha256) throw new Exception("legacy_catalog_fixture_identity");
                var target = InstallEngine.Safe(legacy, pin.Path); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(source, target);
            }
            File.WriteAllText(Path.Combine(legacy, "user.txt"), "PRESERVE");
            protocol.Value = new(UserRegistration.Description, "", UserRegistration.Command(Path.Combine(legacy, "Soda.AssetQuickRead.Bridge.exe")), true);
        }
        Func<Stream> stub = InstallRegistration.OpenUninstaller;
        var entries = new InstallRegistration(install, registration, store, stub);
        var version = InstallEngine.Install(archive, catalog, install, entries, CancellationToken.None, _ => { }, (pin, _) => File.OpenRead(InstallEngine.Safe(privatePayload, pin.Path)));
        InstallEngine.Verify(version, catalog);
        if (store.Value != InstallRegistration.Record(Path.GetFullPath(install))) throw new Exception("actual_package_uninstall_registration");
        using var self = stub(); var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(self));
        if (InstallEngine.Digest(Path.Combine(install, "uninstall.exe")) != hash) throw new Exception("actual_package_uninstaller_identity");
        Directory.CreateDirectory(Path.Combine(install, "jobs")); var keep = Path.Combine(install, "jobs", "preserve.json"); File.WriteAllText(keep, "SYNTHETIC_RESULT");
        AssetUninstall.Remove(install, catalog, registration, store, hash);
        if (protocol.Value != null || store.Value != null || File.Exists(shortcut) || File.Exists(Path.Combine(install, "uninstall.exe")) || catalog.Files.Any(pin => File.Exists(InstallEngine.Safe(version, pin.Path)))) throw new Exception("actual_package_uninstall_left_managed_files");
        if (File.ReadAllText(keep) != "SYNTHETIC_RESULT") throw new Exception("actual_package_uninstall_result_changed");
        if (legacyFixture != null && (AssetUninstall.Published100Catalog().Files.Any(pin => File.Exists(InstallEngine.Safe(legacy, pin.Path))) || File.ReadAllText(Path.Combine(legacy, "user.txt")) != "PRESERVE")) throw new Exception("legacy_package_uninstall_or_preservation");
        return 5;
    }
    internal static int Run(string root, string archive, Catalog catalog)
    {
        var passed = 0;
        void Check(bool value) { if (!value) throw new Exception("installer_experience_assertion"); passed++; }
        var sample = System.Text.Encoding.UTF8.GetBytes("MZ synthetic uninstall program");
        var stubHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(sample));
        foreach (var missing in new[] { "protocol", "uninstall" })
        {
            var install = Path.Combine(root, "silent-write-" + missing);
            var protocol = new ProtocolStore { IgnoreCreate = missing == "protocol" };
            var store = new UninstallStore { IgnoreWrite = missing == "uninstall" };
            var shortcut = Path.Combine(install, "synthetic-start-menu", "quick-read.url");
            var diagnostics = new InstallerDiagnostics(Path.Combine(install, "installer-diagnostics"));
            var entries = new InstallRegistration(install, new UserRegistration(protocol, shortcut), store, () => new MemoryStream(sample));
            Directory.CreateDirectory(Path.Combine(install, "jobs")); File.WriteAllText(Path.Combine(install, "jobs", "keep.json"), "PRESERVE");
            try { InstallEngine.Install(archive, catalog, install, entries, CancellationToken.None, _ => { }, diagnostics: diagnostics); throw new Exception("silent_registration_write_accepted"); }
            catch (IOException error) { Check(error.Message == (missing == "protocol" ? "protocol_registration_verify_failed" : "uninstall_registration_verify_failed")); }
            Check(protocol.Value is null && store.Value is null && !File.Exists(shortcut) && !File.Exists(Path.Combine(install, "uninstall.exe")));
            Check(!Directory.Exists(Path.Combine(install, "versions", catalog.Version)));
            Check(File.ReadAllText(Path.Combine(install, "jobs", "keep.json")) == "PRESERVE");
        }
        foreach (var fail in new[] { false, true })
        {
            var install = Path.Combine(root, "experience-" + fail);
            var protocol = new ProtocolStore(); var store = new UninstallStore { Fail = fail };
            var shortcut = Path.Combine(root, "shortcut-" + fail, "quick-read.url");
            var registration = new UserRegistration(protocol, shortcut);
            var entries = new InstallRegistration(install, registration, store, () => new MemoryStream(sample));
            Directory.CreateDirectory(Path.Combine(install, "jobs")); File.WriteAllText(Path.Combine(install, "jobs", "keep.json"), "PRESERVE");
            if (fail)
            {
                try { InstallEngine.Install(archive, catalog, install, entries, CancellationToken.None, _ => { }); throw new Exception("failure_missing"); }
                catch (IOException) { }
                Check(protocol.Value is null && store.Value is null && !File.Exists(shortcut));
                Check(!File.Exists(Path.Combine(install, "uninstall.exe")) && !Directory.Exists(Path.Combine(install, "versions", catalog.Version)));
            }
            else
            {
                var installed = InstallEngine.Install(archive, catalog, install, entries, CancellationToken.None, _ => { });
                Check(store.Value == InstallRegistration.Record(Path.GetFullPath(install)) && File.Exists(shortcut));
                Check(InstallEngine.Digest(Path.Combine(install, "uninstall.exe")) == stubHash);
                var unknown = Path.Combine(installed, "user-file.txt"); File.WriteAllText(unknown, "PRESERVE");
                var modified = InstallEngine.Safe(installed, catalog.Files[0].Path); File.AppendAllText(modified, "USER_EDIT");
                AssetUninstall.Remove(install, catalog, registration, store, stubHash);
                Check(protocol.Value is null && store.Value is null && !File.Exists(shortcut) && !File.Exists(Path.Combine(install, "uninstall.exe")));
                Check(File.Exists(modified) && File.ReadAllText(unknown) == "PRESERVE");
                Check(!File.Exists(InstallEngine.Safe(installed, catalog.Files[1].Path)));
                // Retry is idempotent and never consumes the user's modified file.
                AssetUninstall.Remove(install, catalog, registration, store, stubHash); Check(File.Exists(modified));
            }
            Check(File.ReadAllText(Path.Combine(install, "jobs", "keep.json")) == "PRESERVE");
        }
        ApplicationConfiguration.Initialize();
        var discoveryRoot = Path.Combine(root, "installation-discovery");
        Directory.CreateDirectory(discoveryRoot);
        Check(!InstallForm.HasInstallationTraces(discoveryRoot, null));
        Directory.CreateDirectory(Path.Combine(discoveryRoot, "jobs"));
        File.WriteAllText(Path.Combine(discoveryRoot, "jobs", "keep.json"), "PRESERVE");
        Check(!InstallForm.HasInstallationTraces(discoveryRoot, null));
        var ownedRecord = InstallRegistration.Record(Path.GetFullPath(discoveryRoot));
        Check(InstallForm.HasInstallationTraces(discoveryRoot, ownedRecord with { Version = "1.0.4" }));
        Check(!InstallForm.HasInstallationTraces(discoveryRoot, ownedRecord with { OwnedShape = false }));
        Check(!InstallForm.HasInstallationTraces(discoveryRoot, ownedRecord with { Root = root }));
        var trace = Path.Combine(discoveryRoot, "versions", "1.0.4", "Soda.AssetQuickRead.Bridge.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(trace)!); File.WriteAllText(trace, "SYNTHETIC_TRACE");
        Check(InstallForm.HasInstallationTraces(discoveryRoot, null));
        using (var partialForm = new InstallForm(preview: true))
        {
            partialForm.SetInstalledVersion(null, true); Show(partialForm);
            Check(Button(partialForm, "卸载").Visible && Has(partialForm, "修复安装"));
        }
        Check(File.ReadAllText(trace) == "SYNTHETIC_TRACE" && File.ReadAllText(Path.Combine(discoveryRoot, "jobs", "keep.json")) == "PRESERVE");
        passed += RunHandlers(root, archive, catalog, sample, stubHash);
        passed += RunServiceSwitchHandlers(root, archive, catalog, sample, stubHash);
        var render = Path.Combine(root, "installer-ui"); Directory.CreateDirectory(render);
        foreach (var scale in new[] { 1f, 1.25f, 1.5f })
        foreach (var state in new[] { "initial", "preparing", "progress", "starting", "completion", "started", "start-error", "cancelled", "error", "upgrade", "repair" })
        {
            using var form = new InstallForm(preview: true);
            if (state is "upgrade" or "repair") form.SetInstalledVersion(state == "upgrade" ? "1.0.0" : "1.0.1");
            else form.ApplyRenderFixture(state);
            form.ShowInTaskbar = false; form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-32000, -32000); form.Opacity = 0; form.Show();
            var ratio = scale / (form.DeviceDpi / 96f); form.Scale(new SizeF(ratio, ratio));
            Application.DoEvents();
            Save("");
            var panel = form.Controls.OfType<Panel>().Single();
            Check(panel.Bottom <= form.Controls.OfType<Button>().Min(button => button.Top));
            Check(panel.Controls.OfType<TextBox>().Single().ReadOnly);
            Check(panel.Controls.OfType<TextBox>().Single().Visible);
            Check(panel.Controls.OfType<TextBox>().Single().ScrollBars == ScrollBars.None);
            Check(!panel.Controls.OfType<TextBox>().Single().TabStop && panel.Controls.OfType<TextBox>().Single().SelectionLength == 0);
            Check(!panel.Controls.OfType<LinkLabel>().Any());
            foreach (Control control in panel.Controls) Check(control.Right <= panel.Width && control.Bottom <= panel.Height);
            if (state == "error") Check(!panel.Controls.OfType<Label>().Any(label => label.Text.Contains("synthetic")));
            form.Close();
            void Save(string suffix)
            {
                using var bitmap = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                bitmap.Save(Path.Combine(render, state + "-" + (int)(scale * 100) + suffix + ".png"));
            }
        }
        return passed;
    }
    private static int RunHandlers(string baseRoot, string archive, Catalog catalog, byte[] sample, string stubHash)
    {
        var passed = 0;
        var root = Path.Combine(baseRoot, "button-handlers");
        var protocol = new ProtocolStore(); var store = new UninstallStore { Fail = true };
        var shortcut = Path.Combine(root, "start-menu", "quick-read.url");
        var registration = new UserRegistration(protocol, shortcut);
        var entries = new InstallRegistration(root, registration, store, () => new MemoryStream(sample));
        var dispatches = new List<System.Diagnostics.ProcessStartInfo>();
        var serviceAttempts = 0; var installAttempts = 0;
        var operations = new FormOperations(root,
            (progress, token) => { installAttempts++; return Task.Run(() => InstallEngine.Install(archive, catalog, root, entries, token, progress.Report), token); },
            start =>
            {
                dispatches.Add(start);
                if (start.Arguments == "--serve" && ++serviceAttempts == 1) throw new IOException("synthetic_launch_failure");
                if (start.FileName.StartsWith("https://")) throw new Exception("unexpected_browser_dispatch");
                return Task.CompletedTask;
            },
            () => Task.Run(() => AssetUninstall.Remove(root, catalog, registration, store, stubHash)));
        using (var form = new InstallForm(true, operations))
        {
            Show(form); Button(form, "开始安装").PerformClick(); Pump(() => Has(form, "重试"));
            Check(protocol.Value is null && store.Value is null && !File.Exists(shortcut));
            store.Fail = false; Button(form, "重试").PerformClick(); Pump(() => Has(form, "重试启动"));
            Check(protocol.Value != null && store.Value != null && File.Exists(Path.Combine(root, "uninstall.exe")));
            Check(Button(form, "卸载").Visible);
            Check(serviceAttempts == 1 && installAttempts == 2);
            using (var diagnostics = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "installer-diagnostics", "latest.json"))))
                Check(diagnostics.RootElement.GetProperty("events").EnumerateArray().Any(entry => entry.GetProperty("phase").GetString() == "complete"));
            Button(form, "重试启动").PerformClick(); Pump(() => Has(form, "完成"));
            Check(serviceAttempts == 2 && installAttempts == 2);
            Check(dispatches.Take(2).All(start => start.FileName == Path.Combine(root, "versions", catalog.Version, "Soda.AssetQuickRead.Bridge.exe") && start.Arguments == "--serve" && start.WorkingDirectory == Path.GetDirectoryName(start.FileName)));
            Button(form, "完成").PerformClick(); Check(form.IsDisposed);
            Check(dispatches.Count == 2 && dispatches.All(start => start.Arguments == "--serve" && !start.UseShellExecute && start.CreateNoWindow && start.WindowStyle == System.Diagnostics.ProcessWindowStyle.Hidden));
        }
        using (var form = new InstallForm(true, operations))
        {
            form.SetInstalledVersion(registration.InstalledVersion(root)); Show(form);
            Button(form, "修复安装").PerformClick(); Pump(() => Has(form, "完成"));
            Check(File.Exists(Path.Combine(root, "uninstall.exe")) && serviceAttempts == 3 && installAttempts == 3);
            Button(form, "卸载").PerformClick(); Pump(() => Has(form, "开始安装"), form);
            Check(protocol.Value is null && store.Value is null && !File.Exists(shortcut) && !File.Exists(Path.Combine(root, "uninstall.exe")));
            Button(form, "开始安装").PerformClick(); Pump(() => Has(form, "完成") && Button(form, "卸载").Enabled);
            Check(serviceAttempts == 4 && installAttempts == 4);
            if (!Button(form, "卸载").Visible || !Button(form, "卸载").Enabled) throw new Exception("reinstalled_uninstall_state:visible=" + Button(form, "卸载").Visible + ":enabled=" + Button(form, "卸载").Enabled + ":formVisible=" + form.Visible + ":disposed=" + form.IsDisposed);
            passed++;
            Button(form, "卸载").PerformClick(); Pump(() => Has(form, "开始安装"), form);
            Check(protocol.Value is null && store.Value is null);
            Button(form, "关闭").PerformClick(); Check(form.IsDisposed);
        }
        {
            var pendingRoot = Path.Combine(baseRoot, "start-pending-handlers");
            var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var installs = 0; var starts = 0;
            using var form = new InstallForm(true, new FormOperations(pendingRoot,
                (_, _) => { installs++; return Task.FromResult(Path.Combine(pendingRoot, "versions", catalog.Version)); },
                start => { Check(start.Arguments == "--serve"); starts++; return pending.Task; }, () => Task.CompletedTask));
            Show(form); Button(form, "开始安装").PerformClick(); Pump(() => starts == 1);
            Check(!Button(form, "开始安装").Enabled && !Button(form, "关闭").Enabled && !Button(form, "卸载").Enabled);
            Button(form, "开始安装").PerformClick(); form.Close(); Check(!form.IsDisposed && starts == 1 && installs == 1);
            pending.SetResult(); Pump(() => Has(form, "完成"));
            Check(starts == 1 && installs == 1);
            Button(form, "完成").PerformClick(); Check(form.IsDisposed && starts == 1);
        }
        using (var form = new InstallForm(true, operations with { Install = async (_, token) => { await Task.Delay(Timeout.Infinite, token); return "unreachable"; } }))
        {
            Show(form); Button(form, "开始安装").PerformClick(); Pump(() => Has(form, "取消"));
            Button(form, "取消").PerformClick(); Pump(() => Has(form, "重新安装"));
            Check(protocol.Value is null && store.Value is null);
            Button(form, "关闭").PerformClick(); Check(form.IsDisposed);
        }
        return passed;
        void Check(bool condition) { if (!condition) throw new Exception("button_handler_assertion"); passed++; }
    }
    private static int RunServiceSwitchHandlers(string baseRoot, string archive, Catalog catalog, byte[] sample, string stubHash)
    {
        var passed = 0;
        void Check(bool condition, string name) { if (!condition) throw new Exception("service_switch_" + name); passed++; }
        var root = Path.Combine(baseRoot, "service-switch-handlers");
        var protocol = new ProtocolStore(); var store = new UninstallStore();
        var shortcut = Path.Combine(root, "start-menu", "quick-read.url");
        var registration = new UserRegistration(protocol, shortcut);
        var entries = new InstallRegistration(root, registration, store, () => new MemoryStream(sample));

        var switchAttempted = 0;
        var serviceDispatches = new List<System.Diagnostics.ProcessStartInfo>();
        var mockOldBridge = new Soda.AssetQuickRead.ServiceReadiness.VerifiedOldBridge(
            12345, DateTime.UtcNow.Ticks, Path.Combine(root, "versions", "1.0.3", "Soda.AssetQuickRead.Bridge.exe"),
            Path.Combine(root, "versions", "1.0.3"), "1.0.3", 50609, "SodaTerminal.AssetQuickRead.Identity.50609");

        var userConfirmed = false;
        var operations = new FormOperations(root,
            (progress, token) => Task.Run(() => InstallEngine.Install(archive, catalog, root, entries, token, progress.Report), token),
            start => { serviceDispatches.Add(start); return Task.CompletedTask; },
            () => Task.CompletedTask,
            InspectOldService: () => Task.FromResult<Soda.AssetQuickRead.ServiceReadiness.VerifiedOldBridge?>(mockOldBridge),
            SwitchOldService: bridge => { switchAttempted++; return Task.FromResult(true); },
            ConfirmSwitch: () => userConfirmed);

        using (var form = new InstallForm(true, operations))
        {
            Show(form);
            Button(form, "开始安装").PerformClick();
            Pump(() => Has(form, "切换到新版"));
            Check(Has(form, "切换到新版"), "offers_switch_to_new_version");
            Check(switchAttempted == 0, "zero_termination_before_switch");
            Check(serviceDispatches.Count == 0, "no_new_service_before_switch");

            userConfirmed = false;
            Button(form, "切换到新版").PerformClick();
            Application.DoEvents();
            Check(Has(form, "切换到新版"), "unconfirmed_click_keeps_button");
            Check(switchAttempted == 0, "unconfirmed_zero_termination");
            Check(serviceDispatches.Count == 0, "unconfirmed_no_dispatch");

            userConfirmed = true;
            Button(form, "切换到新版").PerformClick();
            Pump(() => Has(form, "完成"));
            Check(Has(form, "完成"), "switch_completed_to_done");
            Check(switchAttempted == 1, "switch_executed_once");
            Check(serviceDispatches.Count == 1 && serviceDispatches[0].Arguments == "--serve", "new_service_dispatched");
            Button(form, "完成").PerformClick();
            Check(form.IsDisposed, "form_disposed_on_done");
        }

        var raceOperations = new FormOperations(root,
            (progress, token) => Task.Run(() => InstallEngine.Install(archive, catalog, root, entries, token, progress.Report), token),
            start => Task.CompletedTask,
            () => Task.CompletedTask,
            InspectOldService: () => Task.FromResult<Soda.AssetQuickRead.ServiceReadiness.VerifiedOldBridge?>(mockOldBridge),
            SwitchOldService: bridge => Task.FromResult(false),
            ConfirmSwitch: () => true);

        using (var form = new InstallForm(true, operations with { ConfirmSwitch = null }))
        {
            var switchesBefore = switchAttempted; var dispatchesBefore = serviceDispatches.Count;
            Show(form); Button(form, "开始安装").PerformClick(); Pump(() => Has(form, "切换到新版"));
            Button(form, "切换到新版").PerformClick(); Application.DoEvents();
            Check(switchAttempted == switchesBefore && serviceDispatches.Count == dispatchesBefore, "missing_confirmation_zero_dispatch_or_termination");
            Button(form, "关闭").PerformClick(); Check(form.IsDisposed, "missing_confirmation_can_close");
        }

        using (var form = new InstallForm(true, raceOperations))
        {
            Show(form);
            Button(form, "开始安装").PerformClick();
            Pump(() => Has(form, "切换到新版"));
            Button(form, "切换到新版").PerformClick();
            Pump(() => Has(form, "重试启动"));
            Check(Has(form, "重试启动") && !Has(form, "切换到新版"), "race_rejection_falls_back_to_retry");
            Button(form, "关闭").PerformClick();
            Check(form.IsDisposed, "race_form_disposed");
        }

        var foreignOperations = new FormOperations(root,
            (progress, token) => Task.Run(() => InstallEngine.Install(archive, catalog, root, entries, token, progress.Report), token),
            start => throw new IOException("port_conflict"),
            () => Task.CompletedTask,
            InspectOldService: () => Task.FromResult<Soda.AssetQuickRead.ServiceReadiness.VerifiedOldBridge?>(null),
            SwitchOldService: null,
            ConfirmSwitch: null);

        using (var form = new InstallForm(true, foreignOperations))
        {
            Show(form);
            Button(form, "开始安装").PerformClick();
            Pump(() => Has(form, "重试启动"));
            Check(Has(form, "重试启动") && !Has(form, "切换到新版"), "foreign_occupant_no_switch_button");
            Button(form, "关闭").PerformClick();
            Check(form.IsDisposed, "foreign_form_disposed");
        }

        return passed;
    }
    private static bool Has(Form form, string label) => form.Controls.OfType<Button>().Any(button => button.Text == label && button.Enabled);
    private static Button Button(Form form, string label) => form.Controls.OfType<Button>().Single(button => button.Text == label);
    private static void Show(Form form)
    { form.ShowInTaskbar = false; form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-32000, -32000); form.Opacity = 0; form.Show(); }
    private static void Pump(Func<bool> condition, Form? form = null)
    { var timeout = DateTime.UtcNow.AddSeconds(20); while (!condition()) { Application.DoEvents(); if (DateTime.UtcNow > timeout) throw new Exception("button_handler_timeout:" + (form is null ? "unknown" : string.Join(";", form.Controls.OfType<Panel>().SelectMany(panel => panel.Controls.Cast<Control>()).Select(control => control.Text).Concat(form.Controls.OfType<Button>().Select(button => button.Text + ":visible=" + button.Visible + ":enabled=" + button.Enabled))))); Thread.Sleep(5); } Application.DoEvents(); }
}
