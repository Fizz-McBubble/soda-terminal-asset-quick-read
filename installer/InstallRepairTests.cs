using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
namespace Soda.AssetQuickRead.Setup;

// Every write/handle/link belongs to a newly created synthetic test root.
internal static class InstallRepairTests
{
    private sealed class Registration(Action<string>? apply = null) : IRegistration
    {
        internal string Value = "original";
        internal int Applied, RolledBack;
        public void Apply(string executable) { Applied++; Value = executable; apply?.Invoke(executable); }
        public void Rollback() { RolledBack++; Value = "original"; }
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint sharing, IntPtr security, uint creation, uint flags, IntPtr template);

    internal static int Run(string root, string archive, Catalog catalog)
    {
        var passed = 0;
        void Check(bool value, string name) { if (!value) throw new Exception("repair_" + name); passed++; }
        void Refuses(Action action, string name)
        {
            try { action(); }
            catch (Exception error) when (error is IOException or OperationCanceledException) { passed++; return; }
            throw new Exception("repair_" + name);
        }
        Dictionary<string, string> Snapshot(string directory) => Directory.GetFileSystemEntries(directory, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(directory, path).Replace('\\', '/'), path => Directory.Exists(path) ? "directory" : InstallEngine.Digest(path), StringComparer.OrdinalIgnoreCase);
        bool Same(Dictionary<string, string> a, Dictionary<string, string> b) => a.Count == b.Count && a.All(x => b.TryGetValue(x.Key, out var value) && value == x.Value);
        string Prepare(string name)
        {
            var install = Path.Combine(root, name);
            InstallEngine.Install(archive, catalog, install, new Registration(), CancellationToken.None, _ => { });
            foreach (var relative in new[] { "jobs/keep.json", "outputs/keep.bin", "versions/0.9.0/history", "uninstall.exe" })
            {
                var file = InstallEngine.Safe(install, relative); Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file, "UNRELATED " + relative);
            }
            return install;
        }
        string Final(string install) => Path.Combine(install, "versions", InstallEngine.Version);
        string[] Backups(string install) => Directory.GetDirectories(Path.Combine(install, "versions"), ".repair-*");
        foreach (var hresult in new[] { unchecked((int)0x80070020), unchecked((int)0x80070021), unchecked((int)0x80070003), unchecked((int)0x80070005), unchecked((int)0x80131620) })
        {
            var expected = hresult is unchecked((int)0x80070020) or unchecked((int)0x80070021) ? "installed_repair_files_in_use" : "installed_repair_switch_unavailable";
            Check(InstallEngine.RepairSwitchCode(new IOException("synthetic", hresult)) == expected && InstallerFeedback.ClassifyCode(expected) == expected,
                "switch_failure_specific_only_for_sharing_or_lock_hresult_" + hresult);
        }
        Check(InstallerDiagnostics.Code(new UnauthorizedAccessException()) == "installer_access_denied", "access_denied_keeps_existing_classification");
        void Damage(string final)
        {
            File.Delete(InstallEngine.Safe(final, catalog.Files[0].Path));
            File.AppendAllText(InstallEngine.Safe(final, catalog.Files[1].Path), "CHANGED");
            Directory.CreateDirectory(Path.Combine(final, "unknown", "empty"));
            File.WriteAllText(Path.Combine(final, "unknown", "keep.txt"), "USER CONTENT");
        }
        foreach (var mode in new[] { "missing", "hash", "unknown", "empty-directory", "combined" })
        {
            var install = Prepare(mode); var final = Final(install);
            if (mode == "missing") File.Delete(InstallEngine.Safe(final, catalog.Files[0].Path));
            if (mode == "hash")
            {
                var file = InstallEngine.Safe(final, catalog.Files[0].Path);
                var bytes = File.ReadAllBytes(file); bytes[0] ^= 1; File.WriteAllBytes(file, bytes);
            }
            if (mode == "unknown") File.WriteAllText(Path.Combine(final, "user.txt"), "PRESERVE");
            if (mode == "empty-directory") Directory.CreateDirectory(Path.Combine(final, "user-empty"));
            if (mode == "combined") Damage(final);
            var before = Snapshot(install); var old = Snapshot(final); var registration = new Registration();
            InstallEngine.Install(archive, catalog, install, registration, CancellationToken.None, _ => { });
            InstallEngine.Verify(final, catalog); passed++;
            if (mode == "empty-directory")
            {
                // Exact payload bytes plus an empty directory remain idempotent.
                Check(Backups(install).Length == 0 && Directory.Exists(Path.Combine(final, "user-empty")), mode + "_preserved");
            }
            else
            {
                Check(Backups(install).Length == 1 && Same(old, Snapshot(Backups(install).Single())), mode + "_original_tree_preserved");
                var backup = Backups(install).Single();
                InstallEngine.Install(archive, catalog, install, registration, CancellationToken.None, _ => { });
                Check(Backups(install).Single() == backup && Same(old, Snapshot(backup)), mode + "_idempotent_repair_keeps_backup");
            }
            var after = Snapshot(install);
            Check(before.Where(x => !x.Key.StartsWith("versions/" + InstallEngine.Version + "/", StringComparison.OrdinalIgnoreCase))
                .All(x => after.TryGetValue(x.Key, out var value) && value == x.Value), mode + "_unrelated_jobs_outputs_history_uninstaller_unchanged");
        }
        foreach (var mode in new[] { "registration-failure", "cancel-copy", "cancel-register", "changed-replacement" })
        {
            var install = Prepare(mode); var final = Final(install); Damage(final); var before = Snapshot(install);
            using var cancellation = new CancellationTokenSource();
            var registration = new Registration(executable =>
            {
                if (mode == "cancel-register") cancellation.Cancel();
                if (mode == "changed-replacement") File.WriteAllText(Path.Combine(Path.GetDirectoryName(executable)!, "external.txt"), "PRESERVE LATER CHANGE");
                if (mode is "registration-failure" or "changed-replacement") throw new IOException("synthetic_registration_failure");
            });
            Refuses(() => InstallEngine.Install(archive, catalog, install, registration, cancellation.Token,
                value => { if (mode == "cancel-copy" && value > 0) cancellation.Cancel(); }), mode + "_refused");
            Check(registration.Value == "original" && registration.RolledBack == 1, mode + "_registration_restored");
            Check(Same(before.Where(x => !x.Key.StartsWith("versions/.install-", StringComparison.OrdinalIgnoreCase)).ToDictionary(),
                Snapshot(install).Where(x => !x.Key.StartsWith("versions/.install-", StringComparison.OrdinalIgnoreCase)).ToDictionary()), mode + "_original_and_unrelated_restored");
            Check(Backups(install).Length == 0, mode + "_backup_restored_to_original_path");
            var staging = Directory.GetDirectories(Path.Combine(install, "versions"), ".install-*");
            Check(mode == "changed-replacement" ? staging.Length == 1 && File.ReadAllText(Path.Combine(staging[0], "external.txt")) == "PRESERVE LATER CHANGE" : staging.Length == 0,
                mode + "_only_verified_operation_files_removed");
        }
        {
            var install = Prepare("locked"); var final = Final(install); Damage(final); var before = Snapshot(install);
            using var guard = CreateFileW(final, 1, 3, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
            Check(!guard.IsInvalid, "synthetic_directory_guard_open"); var registration = new Registration();
            try { InstallEngine.Install(archive, catalog, install, registration, CancellationToken.None, _ => { }); throw new Exception("repair_locked_directory_not_refused"); }
            catch (IOException error) { Check(error.Message == "installed_repair_files_in_use", "actual_sharing_lock_classification"); }
            Check(Same(before, Snapshot(install)) && registration.Applied == 0 && registration.Value == "original", "locked_original_and_registration_preserved");
        }
        {
            var install = Prepare("locked-child"); var final = Final(install); Damage(final);
            var before = Snapshot(install); var registration = new Registration();
            var diagnostics = new InstallerDiagnostics(Path.Combine(root, "locked-child-diagnostics"));
            using (var guard = new FileStream(Path.Combine(final, "unknown", "keep.txt"), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                try
                {
                    InstallEngine.Install(archive, catalog, install, registration, CancellationToken.None, _ => { }, diagnostics: diagnostics);
                    throw new Exception("repair_locked_child_not_refused");
                }
                catch (IOException error)
                {
                    Check(error.Message == "installed_repair_switch_unavailable", "child_handle_access_denied_is_not_assumed_acl_or_sharing_hresult");
                    diagnostics.CaptureFailure(error);
                    var report = diagnostics.BuildSafeReport(error.Message);
                    Check(report.Failure is { Operation: "repair_switch", Kind: "access_denied", NativeErrorCode: 5 }, "child_handle_keeps_actual_operation_and_native_code");
                    Check(InstallerDiagnostics.Message(error.Message, report.Failure).Contains("文件占用"), "child_handle_guidance_does_not_assume_acl_failure");
                    File.WriteAllText(Path.Combine(root, "repair-switch-feedback.json"), InstallerFeedback.SerializeReport(report));
                }
                Check(Same(before, Snapshot(install)) && registration.Applied == 0, "child_handle_failure_preserves_original_and_registration");
            }
            InstallEngine.Install(archive, catalog, install, registration, CancellationToken.None, _ => { });
            Check(Backups(install).Length == 1 && File.ReadAllText(Path.Combine(Backups(install)[0], "unknown", "keep.txt")) == "USER CONTENT", "child_handle_release_allows_repair_and_preserves_original");
        }
        {
            var install = Prepare("junction"); var final = Final(install); var outside = Path.Combine(root, "junction-target"); Directory.CreateDirectory(outside);
            File.WriteAllText(Path.Combine(outside, "keep.txt"), "OUTSIDE"); var junction = Path.Combine(final, "linked");
            var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "/c", "mklink", "/J", junction, outside }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!; process.WaitForExit(); Check(process.ExitCode == 0, "synthetic_junction_created");
            var registration = new Registration();
            Refuses(() => InstallEngine.Install(archive, catalog, install, registration, CancellationToken.None, _ => { }), "junction_refused_before_repair");
            Check(registration.Applied == 0 && File.ReadAllText(Path.Combine(outside, "keep.txt")) == "OUTSIDE" && Directory.Exists(junction), "junction_and_target_preserved");
        }
        foreach (var (exitCode, expected) in new[] {
            (Soda.AssetQuickRead.ServiceReadiness.IdentityUnavailableExitCode, "local_service_identity_unavailable"),
            (Soda.AssetQuickRead.ServiceReadiness.PortConflictExitCode, "local_service_port_conflict"),
            (Soda.AssetQuickRead.ServiceReadiness.AddressUnavailableExitCode, "local_service_address_unavailable"),
            (73, "local_service_bind_or_identity_unavailable"),
            (1, "local_service_start_failed") })
        {
            Check(Soda.AssetQuickRead.ServiceReadiness.StartFailureCode(exitCode) == expected, "finite_service_failure_" + expected);
            Check(InstallerFeedback.ClassifyCode(expected) == expected && InstallForm.ServiceStartMessage(expected).Contains("重试"), "actionable_feedback_" + expected);
        }
        return passed;
    }
}
