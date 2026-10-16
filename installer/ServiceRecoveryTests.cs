using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Soda.AssetQuickRead;
namespace Soda.AssetQuickRead.Setup;

internal static class ServiceRecoveryTests
{
    private sealed class ProtocolStore : IProtocolRegistrationStore
    {
        internal ProtocolRegistration? Value;
        public ProtocolRegistration? Read() => Value;
        public void Create(string command) => Value = new(UserRegistration.Description, "", command, true);
        public bool Replace(ProtocolRegistration expected, string command) { if (Value != expected) return false; Value = expected with { Command = command }; return true; }
        public void RemoveCreated(string command) { if (Value?.Command == command) Value = null; }
    }
    private sealed class UninstallStore : IUninstallStore
    {
        internal UninstallRecord? Value;
        public UninstallRecord? Read() => Value;
        public void Write(UninstallRecord value) => Value = value;
        public bool Remove(UninstallRecord value) { if (Value != value) return false; Value = null; return true; }
    }
    private sealed class MirrorHealth : IDisposable
    {
        private readonly CancellationTokenSource cancellation = new();
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly Task loop;
        internal int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
        internal MirrorHealth()
        {
            listener.Start();
            loop = Task.Run(async () =>
            {
                while (!cancellation.IsCancellationRequested)
                {
                    try
                    {
                        using var client = await listener.AcceptTcpClientAsync(cancellation.Token);
                        using var stream = client.GetStream();
                        var buffer = new byte[2048]; await stream.ReadAsync(buffer, cancellation.Token);
                        var body = "{\"service\":\"soda-asset-quick-read\",\"version\":\"1.0.7\",\"protocolVersion\":1}";
                        var message = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nConnection: close\r\nContent-Type: application/json\r\nContent-Length: " + body.Length + "\r\n\r\n" + body);
                        await stream.WriteAsync(message, cancellation.Token);
                    }
                    catch (Exception) when (cancellation.IsCancellationRequested) { break; }
                }
            });
        }
        public void Dispose() { cancellation.Cancel(); listener.Stop(); try { loop.GetAwaiter().GetResult(); } catch (OperationCanceledException) { } cancellation.Dispose(); }
    }

    // Version-release check only: the broader 111/112 race/UI counterexamples
    // have their own evidence and need not be rerun for a new published pin.
    internal static int RunPublished113(string output, string publishedPackage, string bridgeOutput)
    {
        var checks = new List<string>();
        void Check(bool value, string name) { if (!value) throw new Exception("published_service_recovery:" + name); checks.Add(name); }
        var root = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, "isolated-published-113-" + Guid.NewGuid().ToString("N"));
        var fixtures = new List<(Process Process, long Ticks)>();
        try
        {
            Directory.CreateDirectory(root);
            var oldCatalog = AssetUninstall.PublishedCatalog("1.1.3");
            Check(InstallEngine.Digest(Path.Combine(publishedPackage, "catalog.json")) == "8BB069851101E66C00FC4642B47E6C22B59FB26F54864ECA5BD7D9BC4D21C7ED", "published113_catalog_exact_bytes");
            Check(InstallEngine.Digest(Path.Combine(publishedPackage, "setup", "payload", "asset-quick-read.zip")) == oldCatalog.ArchiveSha256, "published113_archive");
            Check(InstallEngine.Digest(Path.Combine(publishedPackage, "uninstall", "Soda.AssetQuickRead.Setup.exe")) == InstallRegistration.OldPin("1.1.3"), "published113_stub");
            foreach (var pin in oldCatalog.Files)
            {
                var source = InstallEngine.Safe(Path.Combine(publishedPackage, pin.Download ? "private-complete-package" : "public-embedded-payload"), pin.Path);
                Check(new FileInfo(source).Length == pin.Size && InstallEngine.Digest(source) == pin.Sha256, "published113_file_" + pin.Path);
                var target = InstallEngine.Safe(Path.Combine(root, "versions", "1.1.3"), pin.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(source, target);
            }
            var currentDirectory = Path.Combine(root, "versions", InstallEngine.Version); Directory.CreateDirectory(currentDirectory);
            foreach (var file in Directory.GetFiles(Path.Combine(bridgeOutput, "companion")))
                if (!file.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)) File.Copy(file, Path.Combine(currentDirectory, Path.GetFileName(file)));
            var currentFiles = Directory.GetFiles(currentDirectory).Select(file => new CatalogFile(Path.GetFileName(file), new FileInfo(file).Length, InstallEngine.Digest(file))).ToArray();
            var currentCatalog = new Catalog(InstallEngine.Version, "isolated-no-archive", currentFiles);
            var socket = new TcpListener(IPAddress.Loopback, 0); socket.Start(); var port = ((IPEndPoint)socket.LocalEndpoint).Port; socket.Stop();
            Check(!ServiceReadiness.ServicePorts.Contains(port), "nonproduction_port");
            var id = Guid.NewGuid(); var pipe = "SodaTerminal.AssetQuickRead.HostSelfTest." + id.ToString("N");
            var contract = ServiceReadiness.IsolatedTestContract.ForPublishedFixtures(root, [port], pipe);
            ProcessStartInfo Start(string version)
            {
                var start = new ProcessStartInfo(Path.Combine(root, "versions", version, "Soda.AssetQuickRead.Bridge.exe"), "--self-test")
                    { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Path.Combine(root, "versions", version) };
                start.Environment["SODA_ASSET_QUICK_READ_HOST_SELF_TEST"] = "health-only:" + port + ":" + id.ToString("N"); return start;
            }
            Process Launch(ProcessStartInfo start)
            {
                var launched = Process.Start(start) ?? throw new IOException("fixture_launch_failed");
                fixtures.Add((Process.GetProcessById(launched.Id), launched.StartTime.ToUniversalTime().Ticks)); return launched;
            }
            Task.Run(async () =>
            {
                async Task<ServiceReadiness.VerifiedOldBridge> WaitOld()
                {
                    var deadline = DateTime.UtcNow.AddSeconds(8);
                    while (DateTime.UtcNow < deadline)
                    {
                        if (await ServiceReadiness.InspectAnyKnownBridge(test: contract) is { } found) return found;
                        await Task.Delay(50);
                    }
                    throw new IOException("published113_fixture_not_ready");
                }
                var old = Launch(Start("1.1.3")); var known = await WaitOld();
                Check(known.ProcessId == old.Id && known.Version == "1.0.9" && known.Port == port, "published113_pinned_process_discovered");
                Check(!await ServiceReadiness.RecheckAndTerminate(known with { StartTimeTicks = known.StartTimeTicks + 1 }, test: contract) && !old.HasExited, "published113_stale_process_record_rejected");
                await ServiceReadiness.SwitchService(known, Start(InstallEngine.Version), InstallEngine.BridgeVersion, TimeSpan.FromSeconds(8), Launch, test: contract);
                var current = fixtures.Last().Process;
                Check(old.HasExited && !current.HasExited && await ServiceReadiness.TrustedExisting(currentDirectory, InstallEngine.BridgeVersion, port, pipe), "published113_switch_to_current_ready");
                AssetUninstall.StopOwnedService(root, currentCatalog, test: contract);
                Check(current.HasExited, "current_catalog_identity_stopped");
                old = Launch(Start("1.1.3")); await WaitOld();
                var protocol = new UserRegistration(new ProtocolStore(), Path.Combine(root, "isolated-start-menu.url"));
                var store = new UninstallStore();
                File.WriteAllText(Path.Combine(root, "uninstall.exe"), "ISOLATED_STUB"); var stubHash = InstallEngine.Digest(Path.Combine(root, "uninstall.exe"));
                foreach (var relative in new[] { "jobs/keep.json", "outputs/keep.bin", "versions/1.1.3/unknown.txt" })
                { var path = InstallEngine.Safe(root, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "PRESERVE"); }
                var modified = Path.Combine(root, "versions", "1.1.3", "Launch-AssetQuickRead.ps1"); File.AppendAllText(modified, "\nMODIFIED"); var modifiedHash = InstallEngine.Digest(modified);
                AssetUninstall.RemoveForTests(root, currentCatalog, protocol, store, stubHash, AssetUninstall.PublishedCatalog,
                    prepareOwnedService: () => AssetUninstall.StopOwnedService(root, currentCatalog, test: contract));
                Check(old.HasExited && !File.Exists(Path.Combine(root, "versions", "1.1.3", "clrjit.dll")), "published113_uninstall_stops_before_loaded_runtime_removal");
                Check(InstallEngine.Digest(modified) == modifiedHash && File.ReadAllText(Path.Combine(root, "versions", "1.1.3", "unknown.txt")) == "PRESERVE" &&
                    File.ReadAllText(Path.Combine(root, "jobs", "keep.json")) == "PRESERVE" && File.ReadAllText(Path.Combine(root, "outputs", "keep.bin")) == "PRESERVE", "published113_uninstall_preserves_modified_unknown_results");
            }).GetAwaiter().GetResult();
            File.WriteAllText(output, JsonSerializer.Serialize(new { suite = "published113-service-recovery", passed = checks.Count, checks, syntheticRoot = root, productionProcessesTouched = 0, registryWrites = 0, gameCapture = false, exitCode = 0 })); return 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(output, JsonSerializer.Serialize(new { passed = checks.Count, failure = error.Message, type = error.GetType().Name, syntheticRoot = root, exitCode = 1 })); return 1;
        }
        finally
        {
            foreach (var fixture in fixtures)
            {
                try { if (!fixture.Process.HasExited && fixture.Process.StartTime.ToUniversalTime().Ticks == fixture.Ticks) { fixture.Process.Kill(); fixture.Process.WaitForExit(4000); } }
                catch { }
                fixture.Process.Dispose();
            }
        }
    }

    internal static int Run(string output, string published111, string published112, string bridgeOutput)
    {
        var checks = new List<string>();
        void Check(bool value, string name) { if (!value) throw new Exception("service_recovery_test:" + name); checks.Add(name); }
        var evidenceRoot = Path.GetDirectoryName(Path.GetFullPath(output))!;
        var reuse = Environment.GetEnvironmentVariable("SODA_SERVICE_RECOVERY_REUSE_ROOT");
        var root = reuse is null ? Path.Combine(evidenceRoot, "isolated-installed-tool-" + Guid.NewGuid().ToString("N")) : Path.GetFullPath(reuse);
        if (!Path.GetDirectoryName(root)!.Equals(evidenceRoot, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(root).StartsWith("isolated-installed-tool-", StringComparison.Ordinal) ||
            !Guid.TryParseExact(Path.GetFileName(root)["isolated-installed-tool-".Length..], "N", out _)) throw new IOException("isolated_old_host_contract_required");
        var fixtures = new List<(Process Process, long Started)>();
        try
        {
            Directory.CreateDirectory(root);
            Task.Run(async () =>
            {
                foreach (var (version, package) in new[] { ("1.1.1", published111), ("1.1.2", published112) })
                {
                    var catalog = AssetUninstall.PublishedCatalog(version);
                    Check(InstallEngine.Digest(Path.Combine(package, "setup", "payload", "asset-quick-read.zip")) == catalog.ArchiveSha256, version + "_actual_archive");
                    foreach (var pin in catalog.Files)
                    {
                        var source = InstallEngine.Safe(Path.Combine(package, pin.Download ? "private-complete-package" : "public-embedded-payload"), pin.Path);
                        var destination = InstallEngine.Safe(Path.Combine(root, "versions", version), pin.Path);
                        var input = reuse is null ? source : destination;
                        Check(new FileInfo(input).Length == pin.Size && InstallEngine.Digest(input) == pin.Sha256, version + "_actual_file_" + pin.Path);
                        if (reuse is null) { Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(source, destination); }
                    }
                }
                var currentDirectory = Path.Combine(root, "versions", InstallEngine.Version); Directory.CreateDirectory(currentDirectory);
                foreach (var file in Directory.GetFiles(Path.Combine(bridgeOutput, "companion")))
                    if (!file.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase))
                    {
                        var destination = Path.Combine(currentDirectory, Path.GetFileName(file));
                        if (reuse is null) File.Copy(file, destination);
                        else if (!File.Exists(destination) || InstallEngine.Digest(file) != InstallEngine.Digest(destination)) throw new IOException("isolated_fixture_changed");
                    }
                var currentFiles = Directory.GetFiles(currentDirectory).Select(path => new CatalogFile(Path.GetFileName(path), new FileInfo(path).Length, InstallEngine.Digest(path))).ToArray();
                var currentCatalog = new Catalog(InstallEngine.Version, "test-only-no-archive", currentFiles);
                using var mirror = new MirrorHealth();
                var spare = new TcpListener(IPAddress.Loopback, 0); spare.Start(); var port = ((IPEndPoint)spare.LocalEndpoint).Port; spare.Stop();
                Check(!ServiceReadiness.ServicePorts.Contains(mirror.Port) && !ServiceReadiness.ServicePorts.Contains(port), "all_process_coordinates_are_isolated");
                var id = Guid.NewGuid(); var pipe = "SodaTerminal.AssetQuickRead.HostSelfTest." + id.ToString("N");
                var contract = ServiceReadiness.IsolatedTestContract.ForPublishedFixtures(root, [mirror.Port, port], pipe);
                ProcessStartInfo Start(string version, string mode = "health-only")
                {
                    var start = new ProcessStartInfo(Path.Combine(root, "versions", version, "Soda.AssetQuickRead.Bridge.exe"), "--self-test")
                        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Path.Combine(root, "versions", version) };
                    start.Environment["SODA_ASSET_QUICK_READ_HOST_SELF_TEST"] = mode + ":" + port + ":" + id.ToString("N"); return start;
                }
                Process Launch(ProcessStartInfo start)
                {
                    var process = Process.Start(start) ?? throw new Exception("service_recovery_test:fixture_launch_failed");
                    // Readiness owns/disposes its Process wrapper after a successful
                    // launch. Keep an independent OS handle for fixture assertions.
                    fixtures.Add((Process.GetProcessById(process.Id), process.StartTime.ToUniversalTime().Ticks)); return process;
                }
                async Task<ServiceReadiness.VerifiedOldBridge> WaitKnown(ServiceReadiness.CurrentInstallation? current = null)
                {
                    var deadline = DateTime.UtcNow.AddSeconds(8);
                    while (DateTime.UtcNow < deadline)
                    {
                        if (await ServiceReadiness.InspectAnyKnownBridge(test: contract, current: current) is { } found) return found;
                        await Task.Delay(50);
                    }
                    throw new Exception("service_recovery_test:known_fixture_not_ready");
                }
                var old111 = Launch(Start("1.1.1")); var known111 = await WaitKnown();
                Check(known111.ProcessId == old111.Id && known111.Port == port && known111.Version == "1.0.7", "published111_fallback_discovered");
                Check(ServiceReadiness.ListenerOwnedBy(port, old111.Id) && !ServiceReadiness.ListenerOwnedBy(mirror.Port, old111.Id), "tcp_listener_pid_matches_pipe_owner");
                Check(await ServiceReadiness.InspectOldBridge(mirror.Port, pipe, test: contract) is null, "copied_public_health_on_foreign_port_rejected");
                Check(!await ServiceReadiness.TrustedExisting(Path.Combine(root, "versions", "1.1.1"), "1.0.7", mirror.Port, pipe), "readiness_rejects_foreign_mirrored_health");
                Check(!await ServiceReadiness.RecheckAndTerminate(known111 with { Port = mirror.Port }, test: contract) && !old111.HasExited, "forged_fallback_keeps_old_and_foreign_listener");
                var lockedClr = Path.Combine(known111.Directory, "clrjit.dll"); var clrHash = InstallEngine.Digest(lockedClr);
                var busy = false; try { using var guard = new FileStream(lockedClr, FileMode.Open, FileAccess.ReadWrite, FileShare.Read); }
                catch (IOException error) when (error.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021)) { busy = true; }
                Check(busy, "actual_published111_process_reproduces_clrjit_lock");
                await ServiceReadiness.SwitchService(known111, Start(InstallEngine.Version), InstallEngine.BridgeVersion, TimeSpan.FromSeconds(8), Launch, test: contract);
                var currentProcess = fixtures.Last().Process;
                Check(old111.HasExited && !currentProcess.HasExited && await ServiceReadiness.TrustedExisting(currentDirectory, InstallEngine.BridgeVersion, port, pipe), "confirmed_switch_stops_only_old_and_starts_current");
                Check(InstallEngine.Digest(lockedClr) == clrHash && ServiceReadiness.PortInUse(mirror.Port), "switch_preserves_payload_and_foreign_listener");
                AssetUninstall.StopOwnedService(root, currentCatalog, test: contract);
                Check(currentProcess.HasExited, "uninstall_can_stop_current_via_embedded_catalog_identity");

                var old112 = Launch(Start("1.1.2")); var known112 = await WaitKnown();
                Check(known112.ProcessId == old112.Id && known112.Version == "1.0.8" && known112.Port == port, "published112_fallback_discovered");
                Check(!await ServiceReadiness.RecheckAndTerminate(known112 with { StartTimeTicks = known112.StartTimeTicks + 1 }, test: contract) && !old112.HasExited, "start_time_race_rejected_without_stopping_process");
                var protocolStore = new ProtocolStore { Value = new(UserRegistration.Description, "", UserRegistration.Command(Path.Combine(currentDirectory, "Soda.AssetQuickRead.Bridge.exe")), true) };
                var store = new UninstallStore { Value = InstallRegistration.Record(root) };
                var protocol = new UserRegistration(protocolStore, Path.Combine(root, "synthetic-start-menu.url"));
                File.WriteAllText(Path.Combine(root, "synthetic-start-menu.url"), UserRegistration.ShortcutBody);
                File.WriteAllText(Path.Combine(root, "uninstall.exe"), "SYNTHETIC_UNINSTALLER"); var stubHash = InstallEngine.Digest(Path.Combine(root, "uninstall.exe"));
                foreach (var relative in new[] { "jobs/keep.json", "outputs/keep.bin", "versions/1.1.1/unknown.txt" })
                { var path = InstallEngine.Safe(root, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "PRESERVE"); }
                var modified = Path.Combine(root, "versions", "1.1.2", "Launch-AssetQuickRead.ps1"); File.AppendAllText(modified, "\nUSER MODIFICATION"); var modifiedHash = InstallEngine.Digest(modified);
                var fileCount = Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length;
                void Remove(Action prepare) => AssetUninstall.RemoveForTests(root, currentCatalog, protocol, store, stubHash, AssetUninstall.PublishedCatalog, prepareOwnedService: prepare);
                var failed = false;
                try { Remove(() => throw new IOException("uninstaller_service_stop_failed")); }
                catch (IOException error) { failed = error.Message == "uninstaller_service_stop_failed"; }
                Check(failed && Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length == fileCount && store.Value is not null && protocolStore.Value is not null && !old112.HasExited,
                    "stop_failure_leaves_all_files_registration_and_process_intact");
                Check(await ServiceReadiness.RecheckAndTerminate(known112, test: contract), "published112_confirmed_stop");
                var unknown = Launch(Start(InstallEngine.Version, "legacy-9.9.9"));
                var deadline = DateTime.UtcNow.AddSeconds(5); while (!ServiceReadiness.ListenerOwnedBy(port, unknown.Id) && DateTime.UtcNow < deadline) await Task.Delay(50);
                Check(await ServiceReadiness.InspectAnyKnownBridge(test: contract, current: new(InstallEngine.Version, InstallEngine.BridgeVersion,
                    currentFiles.Single(file => file.Path == "Soda.AssetQuickRead.Bridge.exe").Sha256, currentFiles.Single(file => file.Path == "Soda.AssetQuickRead.Bridge.dll").Sha256)) is null, "unknown_wire_identity_has_no_stop_authority");
                failed = false;
                try { Remove(() => AssetUninstall.StopOwnedService(root, currentCatalog, test: contract)); }
                catch (IOException error) { failed = error.Message == "uninstaller_files_in_use"; }
                Check(failed && !unknown.HasExited && Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length == fileCount && store.Value is not null && protocolStore.Value is not null,
                    "unknown_process_not_stopped_and_uninstall_is_non_destructive");
                StopFixture(unknown); // Only this test's exact PID/start-time may be cleaned up.
                old112 = Launch(Start("1.1.2")); await WaitKnown();
                Remove(() => AssetUninstall.StopOwnedService(root, currentCatalog, test: contract));
                Check(old112.HasExited && store.Value is null && protocolStore.Value is null && !File.Exists(Path.Combine(root, "uninstall.exe")), "uninstall_stops_verified_fallback_before_file_removal");
                Check(!File.Exists(Path.Combine(root, "versions", "1.1.1", "clrjit.dll")) && !File.Exists(Path.Combine(root, "versions", "1.1.2", "clrjit.dll")), "previously_locked_runtime_removed_after_confirmed_stop");
                Check(InstallEngine.Digest(modified) == modifiedHash && File.ReadAllText(Path.Combine(root, "versions", "1.1.1", "unknown.txt")) == "PRESERVE" &&
                    File.ReadAllText(Path.Combine(root, "jobs", "keep.json")) == "PRESERVE" && File.ReadAllText(Path.Combine(root, "outputs", "keep.bin")) == "PRESERVE" && ServiceReadiness.PortInUse(mirror.Port),
                    "uninstall_preserves_modified_unknown_results_outputs_and_foreign_listener");
            }).GetAwaiter().GetResult();

            checks.Add("existing_uninstall_guards:" + UninstallVersionTests.Run(root));
            ApplicationConfiguration.Initialize();
            foreach (var code in new[] { "uninstaller_service_stop_failed", "uninstaller_files_in_use" })
            {
                var safe = InstallerFeedback.CreateSafeReport("uninstall", code, 1, []);
                Check(safe.Stage == "uninstall" && safe.Code == code && !InstallerFeedback.SerializeReport(safe).Contains(root), code + "_finite_feedback_contract");
                foreach (var scale in new[] { 1f, 1.25f, 1.5f })
                {
                    using var form = new InstallForm(preview: true) { ShowInTaskbar = false, Opacity = 0, StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000) };
                    form.ShowUninstallError(code); form.Show(); form.Scale(new SizeF(scale / (form.DeviceDpi / 96f), scale / (form.DeviceDpi / 96f))); Application.DoEvents();
                    var panel = form.Controls.OfType<Panel>().Single(); var details = panel.Controls.OfType<TextBox>().Single();
                    var height = TextRenderer.MeasureText(details.Text, details.Font, new Size(details.ClientSize.Width, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPadding).Height;
                    Check(height <= details.ClientSize.Height && !details.Text.Contains(root) && form.Controls.OfType<Button>().Any(button => button.Visible && button.Text == "反馈安装问题"), code + "_visible_safe_recovery_" + scale);
                    using var image = new Bitmap(form.Width, form.Height); form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size)); image.Save(Path.Combine(Path.GetDirectoryName(output)!, code + "-" + (int)(scale * 100) + ".png"));
                }
            }
            Check(AssetUninstall.FailureCode(new IOException("DO_NOT_EXPOSE_PATH", unchecked((int)0x80070020))) == "uninstaller_files_in_use" &&
                AssetUninstall.FailureCode(new IOException("DO_NOT_EXPOSE_PATH")) == "installer_local_operation_failed", "raw_uninstall_errors_are_classified");
            File.WriteAllText(output, JsonSerializer.Serialize(new { suite = "native-service-recovery", passed = checks.Count, checks, syntheticRoot = root, publishedVersionsExercised = new[] { "1.1.1", "1.1.2" }, productionProcessesTouched = 0, registryWrites = 0, gameCapture = false, exitCode = 0 })); return 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(output, JsonSerializer.Serialize(new { passed = checks.Count, failure = error.Message.StartsWith("service_recovery_test:", StringComparison.Ordinal) ? error.Message : error.GetType().Name,
                code = AssetUninstall.FailureCode(error), frames = new StackTrace(error, true).GetFrames()?.Take(6).Select(frame => new { type = frame.GetMethod()?.DeclaringType?.Name, method = frame.GetMethod()?.Name, line = frame.GetFileLineNumber() }),
                syntheticRoot = root, productionProcessesTouched = 0, registryWrites = 0, exitCode = 1 })); return 1;
        }
        finally { foreach (var fixture in fixtures) { try { StopFixture(fixture.Process); } catch { } fixture.Process.Dispose(); } }
        void StopFixture(Process process)
        {
            var recorded = fixtures.Single(item => item.Process.Id == process.Id);
            if (!process.HasExited && process.StartTime.ToUniversalTime().Ticks == recorded.Started) { process.Kill(); process.WaitForExit(4000); }
        }
    }
}
