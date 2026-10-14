using System.IO.Compression;
using System.Text.Json;
namespace Soda.AssetQuickRead.Setup;
internal static class Tests
{
    private sealed class Registration(bool fail = false) : IRegistration
    {
        internal int Applied, RolledBack;
        public void Apply(string executable) { Applied++; if (fail) throw new IOException("synthetic_registry_failure"); }
        public void Rollback() { RolledBack++; }
    }
    private sealed class MemoryProtocolStore(ProtocolRegistration? current) : IProtocolRegistrationStore
    {
        internal ProtocolRegistration? Current = current;
        internal bool FailAfterReplace;
        public ProtocolRegistration? Read() => Current;
        public void Create(string command) => Current = new(UserRegistration.Description, "", command, true);
        public bool Replace(ProtocolRegistration expected, string command)
        {
            if (Current != expected) return false;
            Current = expected with { Command = command };
            if (FailAfterReplace) { FailAfterReplace = false; throw new IOException("synthetic_failure_after_protocol_write"); }
            return true;
        }
        public void RemoveCreated(string command) { if (Current?.Command == command && Current.OwnedShape) Current = null; }
    }
    internal static int Run(string evidencePath)
    {
        var passed = 0;
        void Check(bool value, string name) { if (!value) throw new Exception(name); passed++; }
        void Refuses(Action action, string name) { try { action(); } catch (Exception e) when (e is IOException or OperationCanceledException) { passed++; return; } throw new Exception(name); }
        var root = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(evidencePath))!, "installer-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var archive = Path.Combine(root, "fixture.zip");
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
                foreach (var path in new[] { "Soda.AssetQuickRead.Bridge.exe", "runtime/assets/nap.json" })
                { var entry = zip.CreateEntry(path); using var writer = new StreamWriter(entry.Open()); writer.Write("synthetic " + path); }
            var expanded = Path.Combine(root, "fixture"); ZipFile.ExtractToDirectory(archive, expanded);
            var files = Directory.GetFiles(expanded, "*", SearchOption.AllDirectories).Select(path => new CatalogFile(Path.GetRelativePath(expanded, path).Replace('\\', '/'), new FileInfo(path).Length, InstallEngine.Digest(path))).ToArray();
            var catalog = new Catalog(InstallEngine.Version, InstallEngine.Digest(archive), files);
            var install = Path.Combine(root, "中文 带空格", "AssetQuickRead");
            Directory.CreateDirectory(Path.Combine(install, "jobs")); File.WriteAllText(Path.Combine(install, "jobs", "keep.json"), "previous job");
            Directory.CreateDirectory(Path.Combine(install, "versions", "0.9.0")); File.WriteAllText(Path.Combine(install, "versions", "0.9.0", "keep.txt"), "old version");
            var oldJob = InstallEngine.Digest(Path.Combine(install, "jobs", "keep.json"));
            var oldVersion = InstallEngine.Digest(Path.Combine(install, "versions", "0.9.0", "keep.txt"));
            var reg = new Registration(); var progresses = new List<int>();
            var target = InstallEngine.Install(archive, catalog, install, reg, CancellationToken.None, progresses.Add);
            Check(target == Path.Combine(install, "versions", InstallEngine.Version) && reg.Applied == 1, "versioned_install");
            InstallEngine.Verify(target, catalog); passed++;
            Check(progresses.Last() == 100 && progresses.SequenceEqual(progresses.Order()), "real_progress");
            Check(InstallEngine.Digest(Path.Combine(install, "jobs", "keep.json")) == oldJob && InstallEngine.Digest(Path.Combine(install, "versions", "0.9.0", "keep.txt")) == oldVersion, "existing_jobs_versions_preserved");
            Check(InstallEngine.Install(archive, catalog, install, reg, CancellationToken.None, _ => { }) == target, "same_version_idempotent");
            Refuses(() => InstallEngine.Install(archive, catalog with { ArchiveSha256 = new string('0', 64) }, Path.Combine(root, "bad-archive"), new Registration(), CancellationToken.None, _ => { }), "archive_hash_rejected");
            Refuses(() => InstallEngine.Install(archive, catalog with { Version = "9.9.9" }, Path.Combine(root, "bad-version"), new Registration(), CancellationToken.None, _ => { }), "version_rejected");
            foreach (var path in new[] { "../escape", "runtime/../escape", "/absolute", "C:/escape", "runtime\\evil", "runtime//evil", "runtime/./evil" }) Refuses(() => InstallEngine.Safe(root, path), "path_rejected");
            var unknown = Path.Combine(root, "unknown", "versions", InstallEngine.Version); Directory.CreateDirectory(unknown); File.WriteAllText(Path.Combine(unknown, "keep.txt"), "foreign");
            Refuses(() => InstallEngine.Install(archive, catalog, Path.Combine(root, "unknown"), new Registration(), CancellationToken.None, _ => { }), "unknown_same_version_rejected");
            Check(File.ReadAllText(Path.Combine(unknown, "keep.txt")) == "foreign", "foreign_same_version_preserved");
            var cancelledRoot = Path.Combine(root, "cancelled"); using var cancellation = new CancellationTokenSource();
            var cancelReg = new Registration();
            Refuses(() => InstallEngine.Install(archive, catalog, cancelledRoot, cancelReg, cancellation.Token, _ => cancellation.Cancel()), "mid_copy_cancel");
            Check(!Directory.Exists(Path.Combine(cancelledRoot, "versions", InstallEngine.Version)) && !Directory.EnumerateDirectories(Path.Combine(cancelledRoot, "versions")).Any(), "cancel_owned_rollback");
            Check(cancelReg.Applied == 0, "cancel_no_protocol_write");
            var failureRoot = Path.Combine(root, "registration-failure"); var failingReg = new Registration(true);
            Refuses(() => InstallEngine.Install(archive, catalog, failureRoot, failingReg, CancellationToken.None, _ => { }), "registration_failure");
            Check(failingReg.RolledBack == 1 && !Directory.Exists(Path.Combine(failureRoot, "versions", InstallEngine.Version)), "registration_failure_rolls_back_new_version");
            File.AppendAllText(Path.Combine(target, files[0].Path), "tampered");
            Refuses(() => InstallEngine.Verify(target, catalog), "installed_hash_rejected");
            var command = UserRegistration.Command(@"C:\中文 带空格\Soda.AssetQuickRead.Bridge.exe");
            Check(command == "\"C:\\中文 带空格\\Soda.AssetQuickRead.Bridge.exe\" --protocol \"%1\"", "quoted_fixed_protocol_command");
            var legacyFixture = Environment.GetEnvironmentVariable("SODA_ASSET_QUICK_READ_LEGACY_FIXTURE");
            Func<string, string>? PrepareLegacy(string installRoot)
            {
                var old = Path.Combine(installRoot, "versions", "1.0.0");
                foreach (var pin in UserRegistration.Published100Pins)
                {
                    var file = InstallEngine.Safe(old, pin.Key); Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    if (legacyFixture is not null)
                    {
                        var source = InstallEngine.Safe(legacyFixture, pin.Key);
                        Check(InstallEngine.Digest(source) == pin.Value, "published_100_fixture_pin");
                        File.Copy(source, file);
                    }
                    else File.WriteAllText(file, "synthetic legacy " + pin.Key);
                }
                // Ordinary self-tests use a digest double; delivery tests use actual pinned old bytes.
                return legacyFixture is not null ? null : file =>
                {
                    var relative = Path.GetRelativePath(old, file).Replace('\\', '/');
                    return UserRegistration.Published100Pins.TryGetValue(relative, out var pin) && File.ReadAllText(file) == "synthetic legacy " + relative ? pin : InstallEngine.Digest(file);
                };
            }
            MemoryProtocolStore OldStore(string installRoot) => new(new(UserRegistration.Description, "", UserRegistration.Command(Path.Combine(installRoot, "versions", "1.0.0", "Soda.AssetQuickRead.Bridge.exe")), true));
            var upgradeRoot = Path.Combine(root, "upgrade"); var legacyDigest = PrepareLegacy(upgradeRoot); var oldStore = OldStore(upgradeRoot);
            var oldCommand = oldStore.Current!.Command; var oldNative = InstallEngine.Digest(Path.Combine(upgradeRoot, "versions", "1.0.0", "runtime", "soda-asset-capture.exe"));
            var upgradeRegistration = new UserRegistration(oldStore, Path.Combine(upgradeRoot, "synthetic-start-menu", "tool.url"), legacyDigest);
            var upgraded = InstallEngine.Install(archive, catalog, upgradeRoot, upgradeRegistration, CancellationToken.None, _ => { });
            Check(oldStore.Current!.Command == UserRegistration.Command(Path.Combine(upgraded, "Soda.AssetQuickRead.Bridge.exe")), "published_100_registration_upgrade");
            Check(InstallEngine.Digest(Path.Combine(upgradeRoot, "versions", "1.0.0", "runtime", "soda-asset-capture.exe")) == oldNative, "upgrade_preserves_old_version");
            var rollbackRoot = Path.Combine(root, "upgrade-rollback"); var rollbackDigest = PrepareLegacy(rollbackRoot); var rollbackStore = OldStore(rollbackRoot); var rollbackCommand = rollbackStore.Current!.Command;
            rollbackStore.FailAfterReplace = true;
            var rollbackRegistration = new UserRegistration(rollbackStore, Path.Combine(rollbackRoot, "synthetic-start-menu", "tool.url"), rollbackDigest);
            Refuses(() => InstallEngine.Install(archive, catalog, rollbackRoot, rollbackRegistration, CancellationToken.None, _ => { }), "upgrade_failure_after_command_write");
            Check(rollbackStore.Current!.Command == rollbackCommand && !Directory.Exists(Path.Combine(rollbackRoot, "versions", InstallEngine.Version)), "upgrade_restores_old_command_and_rolls_back_new_version");
            var newExecutable = Path.Combine(upgradeRoot, "versions", InstallEngine.Version, "Soda.AssetQuickRead.Bridge.exe");
            foreach (var rejected in new[] {
                new ProtocolRegistration(UserRegistration.Description, "", UserRegistration.Command(Path.Combine(root, "different-root", "versions", "1.0.0", "Soda.AssetQuickRead.Bridge.exe")), true),
                new ProtocolRegistration(UserRegistration.Description, "", oldCommand + " --serve", true),
                new ProtocolRegistration(UserRegistration.Description, "", oldCommand, false),
            })
            {
                var unknownStore = new MemoryProtocolStore(rejected);
                Refuses(() => new UserRegistration(unknownStore, Path.Combine(root, "unknown-shortcut", "tool.url"), legacyDigest).Apply(newExecutable), "unknown_registration_upgrade_rejected");
                Check(unknownStore.Current == rejected, "unknown_registration_preserved");
            }
            var tamperedLegacy = Path.Combine(upgradeRoot, "versions", "1.0.0", "Soda.AssetQuickRead.Bridge.dll"); var originalLegacyBytes = File.ReadAllBytes(tamperedLegacy); File.AppendAllText(tamperedLegacy, "user replacement");
            var replacedStore = OldStore(upgradeRoot);
            Refuses(() => new UserRegistration(replacedStore, Path.Combine(root, "replaced-shortcut", "tool.url"), legacyDigest).Apply(newExecutable), "replaced_old_bridge_rejected");
            Check(replacedStore.Current!.Command == oldCommand, "replaced_old_bridge_command_preserved"); File.WriteAllBytes(tamperedLegacy, originalLegacyBytes);
            var externallyChanged = oldStore.Current! with { Command = "user replacement command" }; oldStore.Current = externallyChanged; upgradeRegistration.Rollback();
            Check(oldStore.Current == externallyChanged, "rollback_preserves_later_unknown_command");
            var malformed = catalog with { Files = [files[0], files[0]] };
            Refuses(() => InstallEngine.Install(archive, malformed, Path.Combine(root, "duplicates"), new Registration(), CancellationToken.None, _ => { }), "catalog_duplicates_rejected");
            var packageFixture = Environment.GetEnvironmentVariable("SODA_ASSET_QUICK_READ_PACKAGE_FIXTURE") ?? AppContext.BaseDirectory;
            var realArchive = Path.Combine(packageFixture, "payload", "asset-quick-read.zip");
            if (File.Exists(realArchive))
            {
                var realCatalog = InstallEngine.ReadCatalog(); var realRoot = Path.Combine(root, "actual-complete-package");
                using var embedded = typeof(InstallEngine).Assembly.GetManifestResourceStream("payload.zip");
                if (embedded is not null) Check(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(embedded)) == realCatalog.ArchiveSha256, "single_file_embedded_payload_hash");
                var privatePayload = Path.GetFullPath(Path.Combine(packageFixture, "..", "private-complete-package"));
                var realTarget = InstallEngine.Install(realArchive, realCatalog, realRoot, new Registration(), CancellationToken.None, _ => { }, (pin, _) => File.OpenRead(InstallEngine.Safe(privatePayload, pin.Path)));
                InstallEngine.Verify(realTarget, realCatalog); passed++;
                Check(File.Exists(Path.Combine(realTarget, "runtime", "WinDivert64.sys")), "curated_payload_complete");
                var rustNotices = File.ReadAllText(Path.Combine(realTarget, "licenses", "Rust-DEPENDENCY-NOTICES.txt"));
                Check(rustNotices.Contains("Package: ring 0.17.14") && rustNotices.Contains("BEGIN LICENSE-BoringSSL") && rustNotices.Contains("Package: unicode-ident 1.0.26") && rustNotices.Contains("BEGIN LICENSE-UNICODE"), "complete_complex_dependency_notices");
                Check(File.Exists(Path.Combine(realTarget, "licenses", "Rust-STDLIB-COPYRIGHT.html")), "rust_standard_library_notice");
                using (var publicZip = ZipFile.OpenRead(realArchive)) Check(!publicZip.Entries.Any(x => x.FullName.StartsWith("runtime/assets/")), "public_payload_excludes_protocol_assets");
                foreach (var pin in realCatalog.Files.Where(x => x.Download))
                    Check(ProtocolDownload.Url(pin).StartsWith("https://raw.githubusercontent.com/pingwinekZ/zzz_packet_capture_0delay/4853f07c8fb5322c12b9b580d6364b577c2ac96a/assets/", StringComparison.Ordinal), "fixed_protocol_source");
                Refuses(() => ProtocolDownload.Url(new CatalogFile("runtime/assets/evil.json", 1, "wrong", true)), "arbitrary_download_rejected");
                var rejectedRoot = Path.Combine(root, "download-failure"); var rejectedRegistration = new Registration();
                Refuses(() => InstallEngine.Install(realArchive, realCatalog, rejectedRoot, rejectedRegistration, CancellationToken.None, _ => { }, (_, _) => throw new IOException("synthetic_download_failure")), "download_failure");
                Check(rejectedRegistration.Applied == 0 && !Directory.Exists(Path.Combine(rejectedRoot, "versions", InstallEngine.Version)), "download_failure_no_registration_no_install");
                passed += ProtocolDownloadTests.Run(Path.Combine(root, "protocol-download"), File.ReadAllBytes(InstallEngine.Safe(privatePayload, "runtime/assets/manifest.json")));
                passed += InstallerExperienceTests.RunFullPackage(Path.Combine(root, "full-uninstall"), realArchive, realCatalog, privatePayload);
                var previousPackage = Environment.GetEnvironmentVariable("SODA_ASSET_QUICK_READ_PREVIOUS_PACKAGE");
                if (previousPackage is not null) passed += InstallerExperienceTests.RunPublishedUpgrade(Path.Combine(root, "published-upgrade"), realArchive, realCatalog, privatePayload, previousPackage);
            }
            passed += InstallerExperienceTests.Run(root, archive, catalog);
            passed += UninstallVersionTests.Run(root);
            passed += InstallerFeedbackTests.Run(Path.Combine(root, "installer-feedback.json"), true);
            passed += VersionRetentionTests.Run(Path.Combine(root, "version-retention.json"), true);
            var staleFixture = Environment.GetEnvironmentVariable("SODA_ASSET_QUICK_READ_STALE_REGISTRATION_FIXTURE");
            if (staleFixture is not null) passed += StaleRegistrationTests.Run(root, staleFixture);
            File.WriteAllText(evidencePath, JsonSerializer.Serialize(new { suite = "asset-quick-read-installer", passed, syntheticRoot = root, published100FixtureVerified = legacyFixture is not null, registryWrites = 0, captureStarted = false, uacRequested = false, exitCode = 0 })); return 0;
        }
        catch (Exception error) { File.WriteAllText(evidencePath, JsonSerializer.Serialize(new { passed, error = error.ToString(), exitCode = 1 })); return 1; }
    }
}
