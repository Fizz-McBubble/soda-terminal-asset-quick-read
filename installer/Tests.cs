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
            var catalog = new Catalog("1.0.0", InstallEngine.Digest(archive), files);
            var install = Path.Combine(root, "中文 带空格", "AssetQuickRead");
            Directory.CreateDirectory(Path.Combine(install, "jobs")); File.WriteAllText(Path.Combine(install, "jobs", "keep.json"), "previous job");
            Directory.CreateDirectory(Path.Combine(install, "versions", "0.9.0")); File.WriteAllText(Path.Combine(install, "versions", "0.9.0", "keep.txt"), "old version");
            var oldJob = InstallEngine.Digest(Path.Combine(install, "jobs", "keep.json"));
            var oldVersion = InstallEngine.Digest(Path.Combine(install, "versions", "0.9.0", "keep.txt"));
            var reg = new Registration(); var progresses = new List<int>();
            var target = InstallEngine.Install(archive, catalog, install, reg, CancellationToken.None, progresses.Add);
            Check(target == Path.Combine(install, "versions", "1.0.0") && reg.Applied == 1, "versioned_install");
            InstallEngine.Verify(target, catalog); passed++;
            Check(progresses.Last() == 100 && progresses.SequenceEqual(progresses.Order()), "real_progress");
            Check(InstallEngine.Digest(Path.Combine(install, "jobs", "keep.json")) == oldJob && InstallEngine.Digest(Path.Combine(install, "versions", "0.9.0", "keep.txt")) == oldVersion, "existing_jobs_versions_preserved");
            Check(InstallEngine.Install(archive, catalog, install, reg, CancellationToken.None, _ => { }) == target, "same_version_idempotent");
            Refuses(() => InstallEngine.Install(archive, catalog with { ArchiveSha256 = new string('0', 64) }, Path.Combine(root, "bad-archive"), new Registration(), CancellationToken.None, _ => { }), "archive_hash_rejected");
            Refuses(() => InstallEngine.Install(archive, catalog with { Version = "9.9.9" }, Path.Combine(root, "bad-version"), new Registration(), CancellationToken.None, _ => { }), "version_rejected");
            foreach (var path in new[] { "../escape", "runtime/../escape", "/absolute", "C:/escape", "runtime\\evil", "runtime//evil", "runtime/./evil" }) Refuses(() => InstallEngine.Safe(root, path), "path_rejected");
            var unknown = Path.Combine(root, "unknown", "versions", "1.0.0"); Directory.CreateDirectory(unknown); File.WriteAllText(Path.Combine(unknown, "keep.txt"), "foreign");
            Refuses(() => InstallEngine.Install(archive, catalog, Path.Combine(root, "unknown"), new Registration(), CancellationToken.None, _ => { }), "unknown_same_version_rejected");
            Check(File.ReadAllText(Path.Combine(unknown, "keep.txt")) == "foreign", "foreign_same_version_preserved");
            var cancelledRoot = Path.Combine(root, "cancelled"); using var cancellation = new CancellationTokenSource();
            var cancelReg = new Registration();
            Refuses(() => InstallEngine.Install(archive, catalog, cancelledRoot, cancelReg, cancellation.Token, _ => cancellation.Cancel()), "mid_copy_cancel");
            Check(!Directory.Exists(Path.Combine(cancelledRoot, "versions", "1.0.0")) && !Directory.EnumerateDirectories(Path.Combine(cancelledRoot, "versions")).Any(), "cancel_owned_rollback");
            Check(cancelReg.Applied == 0, "cancel_no_protocol_write");
            var failureRoot = Path.Combine(root, "registration-failure"); var failingReg = new Registration(true);
            Refuses(() => InstallEngine.Install(archive, catalog, failureRoot, failingReg, CancellationToken.None, _ => { }), "registration_failure");
            Check(failingReg.RolledBack == 1 && !Directory.Exists(Path.Combine(failureRoot, "versions", "1.0.0")), "registration_failure_rolls_back_new_version");
            File.AppendAllText(Path.Combine(target, files[0].Path), "tampered");
            Refuses(() => InstallEngine.Verify(target, catalog), "installed_hash_rejected");
            var command = UserRegistration.Command(@"C:\中文 带空格\Soda.AssetQuickRead.Bridge.exe");
            Check(command == "\"C:\\中文 带空格\\Soda.AssetQuickRead.Bridge.exe\" --protocol \"%1\"", "quoted_fixed_protocol_command");
            var malformed = catalog with { Files = [files[0], files[0]] };
            Refuses(() => InstallEngine.Install(archive, malformed, Path.Combine(root, "duplicates"), new Registration(), CancellationToken.None, _ => { }), "catalog_duplicates_rejected");
            var realArchive = Path.Combine(AppContext.BaseDirectory, "payload", "asset-quick-read.zip");
            if (File.Exists(realArchive))
            {
                var realCatalog = InstallEngine.ReadCatalog(); var realRoot = Path.Combine(root, "actual-complete-package");
                using var embedded = typeof(InstallEngine).Assembly.GetManifestResourceStream("payload.zip");
                if (embedded is not null) Check(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(embedded)) == realCatalog.ArchiveSha256, "single_file_embedded_payload_hash");
                var privatePayload = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "private-complete-package"));
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
                Check(rejectedRegistration.Applied == 0 && !Directory.Exists(Path.Combine(rejectedRoot, "versions", "1.0.0")), "download_failure_no_registration_no_install");
            }
            File.WriteAllText(evidencePath, JsonSerializer.Serialize(new { suite = "asset-quick-read-installer", passed, syntheticRoot = root, registryWrites = 0, captureStarted = false, uacRequested = false, exitCode = 0 })); return 0;
        }
        catch (Exception error) { File.WriteAllText(evidencePath, JsonSerializer.Serialize(new { passed, error = error.ToString(), exitCode = 1 })); return 1; }
    }
}
