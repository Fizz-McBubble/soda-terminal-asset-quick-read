using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Soda.AssetQuickRead.Setup;

internal static class VersionRetentionTests
{
    private sealed class MockRegistration(string? previousVersion = null, bool failOnApply = false, Action? afterApply = null) : IRegistration
    {
        internal int AppliedCount { get; private set; }
        internal int RolledBackCount { get; private set; }
        public string? PreviousRegisteredVersion => previousVersion;

        public void Apply(string executable)
        {
            AppliedCount++;
            if (failOnApply) throw new IOException("synthetic_apply_failure");
            afterApply?.Invoke();
        }

        public void Rollback()
        {
            RolledBackCount++;
        }
    }

    internal static int Run(string evidencePath, bool returnAssertionCount = false)
    {
        var testResults = new List<object>();
        var passed = 0;
        var reparseTested = false;
        var baseDir = Path.GetDirectoryName(Path.GetFullPath(evidencePath))!;
        var root = Path.Combine(baseDir, "retention-run-" + Guid.NewGuid().ToString("N"));
        var syntheticDiagnostics = new List<object>();

        void Check(bool condition, string testName, string detail)
        {
            if (!condition)
            {
                File.WriteAllText(evidencePath + ".failure.json", JsonSerializer.Serialize(new {
                    passed, testName, detail, syntheticDiagnostics,
                    residualEntries = Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
                        .Select(path => new { relativePath = Path.GetRelativePath(root, path), pathLength = path.Length, directory = Directory.Exists(path) }).ToArray()
                }, new JsonSerializerOptions { WriteIndented = true }));
                throw new InvalidOperationException($"retention_test_failed: {testName} - {detail}");
            }
            passed++;
            testResults.Add(new { name = testName, status = "passed", detail });
        }

        // Safeguard: strictly verify isolated test root
        var productionRoot = Path.GetFullPath(InstallEngine.DefaultRoot);
        if (Path.GetFullPath(root).Equals(productionRoot, StringComparison.OrdinalIgnoreCase) ||
            Path.GetFullPath(root).StartsWith(productionRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("isolated_retention_test_required");
        }

        Directory.CreateDirectory(root);

        try
        {
            var publishedVersions = new[] { "1.0.0", "1.0.1", "1.0.2", "1.0.6", "1.0.7", "1.0.8", "1.0.9" };
            var catalogStore = new Dictionary<string, Catalog>(StringComparer.OrdinalIgnoreCase);

            Catalog SetupSyntheticVersion(string baseRoot, string version, params (string Path, string Content)[] files)
            {
                var versionDir = Path.Combine(baseRoot, "versions", version);
                Directory.CreateDirectory(versionDir);
                var catFiles = new List<CatalogFile>();
                foreach (var (relPath, content) in files)
                {
                    var fullPath = Path.Combine(versionDir, relPath.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                    File.WriteAllText(fullPath, content);
                    var sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fullPath)));
                    var len = new FileInfo(fullPath).Length;
                    catFiles.Add(new CatalogFile(relPath, len, sha));
                }
                var cat = new Catalog(version, "synthetic-archive-sha", catFiles.ToArray());
                catalogStore[version] = cat;
                return cat;
            }

            Catalog GetCatalog(string v) => catalogStore.TryGetValue(v, out var c) ? c : throw new IOException("unknown_version_catalog");

            // -------------------------------------------------------------
            // Test 1: Multiple upgrades (1.0.0, 1.0.1, 1.0.8, 1.0.9)
            // Retains current (1.0.9) + registered previous (1.0.8), prunes 1.0.0 and 1.0.1
            // -------------------------------------------------------------
            {
                var testRoot = Path.Combine(root, "test-1-multi-upgrade");
                Directory.CreateDirectory(testRoot);

                SetupSyntheticVersion(testRoot, "1.0.0", ("app.exe", "v1.0.0 exe"), ("data.bin", "v1.0.0 data"));
                SetupSyntheticVersion(testRoot, "1.0.1", ("app.exe", "v1.0.1 exe"), ("data.bin", "v1.0.1 data"));
                SetupSyntheticVersion(testRoot, "1.0.8", ("app.exe", "v1.0.8 exe"), ("data.bin", "v1.0.8 data"));
                SetupSyntheticVersion(testRoot, "1.0.9", ("app.exe", "v1.0.9 exe"), ("data.bin", "v1.0.9 data"));

                var reg = new MockRegistration("1.0.8");
                VersionRetention.RetireOlderVersions(testRoot, "1.0.9", reg, publishedVersions, GetCatalog,
                    syntheticDiagnostic: (version, stage, code) => syntheticDiagnostics.Add(new { version, stage, code }));

                Check(Directory.Exists(Path.Combine(testRoot, "versions", "1.0.9")), "multiple_upgrades", "current 1.0.9 retained");
                Check(Directory.Exists(Path.Combine(testRoot, "versions", "1.0.8")), "multiple_upgrades", "previous 1.0.8 retained");
                Check(!Directory.Exists(Path.Combine(testRoot, "versions", "1.0.0")), "multiple_upgrades", "older 1.0.0 retired");
                Check(!Directory.Exists(Path.Combine(testRoot, "versions", "1.0.1")), "multiple_upgrades", "older 1.0.1 retired");
            }

            // -------------------------------------------------------------
            // Test 2: Actual previous registered owned version vs disk folder
            // Registered is 1.0.6, rogue/unregistered folder 1.0.7 also on disk.
            // Retains 1.0.6 and 1.0.9, prunes 1.0.7.
            // -------------------------------------------------------------
            {
                var testRoot = Path.Combine(root, "test-2-registered-vs-disk");
                Directory.CreateDirectory(testRoot);

                SetupSyntheticVersion(testRoot, "1.0.6", ("app.exe", "v1.0.6 exe"));
                SetupSyntheticVersion(testRoot, "1.0.7", ("app.exe", "v1.0.7 exe"));
                SetupSyntheticVersion(testRoot, "1.0.9", ("app.exe", "v1.0.9 exe"));

                var reg = new MockRegistration("1.0.6");
                VersionRetention.RetireOlderVersions(testRoot, "1.0.9", reg, publishedVersions, GetCatalog,
                    syntheticDiagnostic: (version, stage, code) => syntheticDiagnostics.Add(new { version, stage, code }));

                Check(Directory.Exists(Path.Combine(testRoot, "versions", "1.0.9")), "actual_previous_registered", "current 1.0.9 retained");
                Check(Directory.Exists(Path.Combine(testRoot, "versions", "1.0.6")), "actual_previous_registered", "actual registered 1.0.6 retained");
                Check(!Directory.Exists(Path.Combine(testRoot, "versions", "1.0.7")), "actual_previous_registered", "unregistered 1.0.7 retired");
            }

            // -------------------------------------------------------------
            // Test 3: Same-version repeat repair
            // 1.0.9 is being repaired with previous registered == "1.0.9".
            // Retains 1.0.9 and pre-existing rollback 1.0.8 across repeated repairs.
            // -------------------------------------------------------------
            {
                var testRoot = Path.Combine(root, "test-3-repeat-repair");
                Directory.CreateDirectory(testRoot);

                SetupSyntheticVersion(testRoot, "1.0.0", ("app.exe", "v1.0.0 exe"));
                SetupSyntheticVersion(testRoot, "1.0.8", ("app.exe", "v1.0.8 exe"));
                SetupSyntheticVersion(testRoot, "1.0.9", ("app.exe", "v1.0.9 exe"));

                var regRepair = new MockRegistration("1.0.9");
                VersionRetention.RetireOlderVersions(testRoot, "1.0.9", regRepair, publishedVersions, GetCatalog);

                Check(Directory.Exists(Path.Combine(testRoot, "versions", "1.0.9")), "repeat_repair", "current 1.0.9 retained after repair 1");
                Check(Directory.Exists(Path.Combine(testRoot, "versions", "1.0.8")), "repeat_repair", "rollback 1.0.8 retained after repair 1");
                Check(!Directory.Exists(Path.Combine(testRoot, "versions", "1.0.0")), "repeat_repair", "older 1.0.0 retired after repair 1");

                // Repeat repair a second time
                VersionRetention.RetireOlderVersions(testRoot, "1.0.9", regRepair, publishedVersions, GetCatalog);
                Check(Directory.Exists(Path.Combine(testRoot, "versions", "1.0.9")), "repeat_repair", "current 1.0.9 retained after repair 2");
                Check(Directory.Exists(Path.Combine(testRoot, "versions", "1.0.8")), "repeat_repair", "rollback 1.0.8 retained after repair 2");
            }

            // -------------------------------------------------------------
            // Test 4: Missing or null registered previous selects highest fully verified prior
            // -------------------------------------------------------------
            {
                var testRoot = Path.Combine(root, "test-4-null-registration");
                Directory.CreateDirectory(testRoot);

                SetupSyntheticVersion(testRoot, "1.0.1", ("app.exe", "v1.0.1 exe"));
                SetupSyntheticVersion(testRoot, "1.0.2", ("app.exe", "v1.0.2 exe"));
                SetupSyntheticVersion(testRoot, "1.0.8", ("app.exe", "v1.0.8 exe"));
                SetupSyntheticVersion(testRoot, "1.0.9", ("app.exe", "v1.0.9 exe"));

                VersionRetention.RetireOlderVersions(testRoot, "1.0.9", registration: null, publishedVersions, GetCatalog);

                Check(Directory.Exists(Path.Combine(testRoot, "versions", "1.0.9")), "null_registration_fallback", "current 1.0.9 retained");
                Check(Directory.Exists(Path.Combine(testRoot, "versions", "1.0.8")), "null_registration_fallback", "highest verified prior 1.0.8 retained");
                Check(!Directory.Exists(Path.Combine(testRoot, "versions", "1.0.1")), "null_registration_fallback", "older 1.0.1 retired");
                Check(!Directory.Exists(Path.Combine(testRoot, "versions", "1.0.2")), "null_registration_fallback", "older 1.0.2 retired");
            }

            // -------------------------------------------------------------
            // Test 5: Candidate with modified file is preserved intact
            // -------------------------------------------------------------
            {
                var testRoot = Path.Combine(root, "test-5-modified-file");
                Directory.CreateDirectory(testRoot);

                SetupSyntheticVersion(testRoot, "1.0.0", ("app.exe", "v1.0.0 original"));
                SetupSyntheticVersion(testRoot, "1.0.8", ("app.exe", "v1.0.8 exe"));
                SetupSyntheticVersion(testRoot, "1.0.9", ("app.exe", "v1.0.9 exe"));

                // Tamper with candidate 1.0.0
                File.WriteAllText(Path.Combine(testRoot, "versions", "1.0.0", "app.exe"), "v1.0.0 TAMPERED");

                var reg = new MockRegistration("1.0.8");
                VersionRetention.RetireOlderVersions(testRoot, "1.0.9", reg, publishedVersions, GetCatalog);

                Check(Directory.Exists(Path.Combine(testRoot, "versions", "1.0.0")), "candidate_modified", "tampered 1.0.0 preserved intact");
                Check(File.ReadAllText(Path.Combine(testRoot, "versions", "1.0.0", "app.exe")) == "v1.0.0 TAMPERED", "candidate_modified", "tampered file preserved");
            }

            // -------------------------------------------------------------
            // Test 6: Candidate with unknown file is preserved intact
            // -------------------------------------------------------------
            {
                var testRoot = Path.Combine(root, "test-6-unknown-file");
                Directory.CreateDirectory(testRoot);

                SetupSyntheticVersion(testRoot, "1.0.0", ("app.exe", "v1.0.0 exe"));
                SetupSyntheticVersion(testRoot, "1.0.8", ("app.exe", "v1.0.8 exe"));
                SetupSyntheticVersion(testRoot, "1.0.9", ("app.exe", "v1.0.9 exe"));

                // Add an unknown foreign file into candidate 1.0.0
                File.WriteAllText(Path.Combine(testRoot, "versions", "1.0.0", "extra_user_file.txt"), "important custom note");

                var reg = new MockRegistration("1.0.8");
                VersionRetention.RetireOlderVersions(testRoot, "1.0.9", reg, publishedVersions, GetCatalog);

                Check(Directory.Exists(Path.Combine(testRoot, "versions", "1.0.0")), "candidate_unknown_file", "candidate with unknown file preserved");
                Check(File.Exists(Path.Combine(testRoot, "versions", "1.0.0", "extra_user_file.txt")), "candidate_unknown_file", "unknown file kept");
            }

            // -------------------------------------------------------------
            // Test 7: Candidate with missing expected file is preserved intact
            // -------------------------------------------------------------
            {
                var testRoot = Path.Combine(root, "test-7-missing-file");
                Directory.CreateDirectory(testRoot);

                SetupSyntheticVersion(testRoot, "1.0.0", ("app.exe", "v1.0.0 exe"), ("module.dll", "v1.0.0 dll"));
                SetupSyntheticVersion(testRoot, "1.0.8", ("app.exe", "v1.0.8 exe"));
                SetupSyntheticVersion(testRoot, "1.0.9", ("app.exe", "v1.0.9 exe"));

                // Delete expected module.dll from candidate 1.0.0
                File.Delete(Path.Combine(testRoot, "versions", "1.0.0", "module.dll"));

                var reg = new MockRegistration("1.0.8");
                VersionRetention.RetireOlderVersions(testRoot, "1.0.9", reg, publishedVersions, GetCatalog);

                Check(Directory.Exists(Path.Combine(testRoot, "versions", "1.0.0")), "candidate_missing_file", "incomplete candidate preserved intact");
            }

            // -------------------------------------------------------------
            // Test 8: Candidate with active file lock is preserved intact
            // -------------------------------------------------------------
            {
                var testRoot = Path.Combine(root, "test-8-busy-file");
                Directory.CreateDirectory(testRoot);

                SetupSyntheticVersion(testRoot, "1.0.0", ("app.exe", "v1.0.0 exe"));
                SetupSyntheticVersion(testRoot, "1.0.8", ("app.exe", "v1.0.8 exe"));
                SetupSyntheticVersion(testRoot, "1.0.9", ("app.exe", "v1.0.9 exe"));

                var lockedPath = Path.Combine(testRoot, "versions", "1.0.0", "app.exe");
                using (var busyStream = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var reg = new MockRegistration("1.0.8");
                    VersionRetention.RetireOlderVersions(testRoot, "1.0.9", reg, publishedVersions, GetCatalog);

                    Check(Directory.Exists(Path.Combine(testRoot, "versions", "1.0.0")), "candidate_busy_file", "busy candidate preserved intact during lock");
                }
            }

            // -------------------------------------------------------------
            // Test 9: Reparse point protection (if supported in environment)
            // -------------------------------------------------------------
            {
                var testRoot = Path.Combine(root, "test-9-reparse");
                Directory.CreateDirectory(testRoot);

                SetupSyntheticVersion(testRoot, "1.0.0", ("app.exe", "v1.0.0 exe"));
                SetupSyntheticVersion(testRoot, "1.0.8", ("app.exe", "v1.0.8 exe"));
                SetupSyntheticVersion(testRoot, "1.0.9", ("app.exe", "v1.0.9 exe"));

                var linkTarget = Path.Combine(testRoot, "link-target");
                Directory.CreateDirectory(linkTarget);
                var linkPath = Path.Combine(testRoot, "versions", "1.0.0", "link");

                bool created = false;
                try
                {
                    Directory.CreateSymbolicLink(linkPath, linkTarget);
                    created = true;
                }
                catch
                {
                    try
                    {
                        using var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{linkPath}\" \"{linkTarget}\"")
                        {
                            CreateNoWindow = true,
                            UseShellExecute = false
                        });
                        proc?.WaitForExit();
                        created = Directory.Exists(linkPath);
                    }
                    catch { }
                }

                if (created)
                {
                    reparseTested = true;
                    var reg = new MockRegistration("1.0.8");
                    VersionRetention.RetireOlderVersions(testRoot, "1.0.9", reg, publishedVersions, GetCatalog);

                    Check(Directory.Exists(Path.Combine(testRoot, "versions", "1.0.0")), "reparse_protection", "reparse candidate preserved intact");
                    try { Directory.Delete(linkPath); } catch { }
                }
                else
                {
                    testResults.Add(new { name = "reparse_protection", status = "skipped_unsupported_environment", detail = "reparse point creation restricted in environment" });
                }
            }

            // -------------------------------------------------------------
            // Test 10: User data, jobs, results, and foreign roots preserved
            // -------------------------------------------------------------
            {
                var testRoot = Path.Combine(root, "test-10-user-data");
                Directory.CreateDirectory(testRoot);

                var jobsDir = Path.Combine(testRoot, "jobs");
                Directory.CreateDirectory(jobsDir);
                File.WriteAllText(Path.Combine(jobsDir, "keep.json"), "{\"userJob\":\"important\"}");

                var dataDir = Path.Combine(testRoot, "data");
                Directory.CreateDirectory(dataDir);
                File.WriteAllText(Path.Combine(dataDir, "account.db"), "user account data");

                var foreignDir = Path.Combine(testRoot, "versions", "custom-tool");
                Directory.CreateDirectory(foreignDir);
                File.WriteAllText(Path.Combine(foreignDir, "custom.exe"), "custom user executable");

                SetupSyntheticVersion(testRoot, "1.0.0", ("app.exe", "v1.0.0 exe"));
                SetupSyntheticVersion(testRoot, "1.0.8", ("app.exe", "v1.0.8 exe"));
                SetupSyntheticVersion(testRoot, "1.0.9", ("app.exe", "v1.0.9 exe"));

                var reg = new MockRegistration("1.0.8");
                VersionRetention.RetireOlderVersions(testRoot, "1.0.9", reg, publishedVersions, GetCatalog);

                Check(File.Exists(Path.Combine(jobsDir, "keep.json")), "user_data_preserved", "jobs/keep.json preserved");
                Check(File.Exists(Path.Combine(dataDir, "account.db")), "user_data_preserved", "data/account.db preserved");
                Check(Directory.Exists(foreignDir) && File.Exists(Path.Combine(foreignDir, "custom.exe")), "user_data_preserved", "foreign versions/ folder preserved");
                Check(!Directory.Exists(Path.Combine(testRoot, "versions", "1.0.0")), "user_data_preserved", "older 1.0.0 retired normally");
            }

            // -------------------------------------------------------------
            // Test 11: End-to-end install failure / cancellation / rollback protection
            // -------------------------------------------------------------
            {
                var testRoot = Path.Combine(root, "test-11-install-failures");
                Directory.CreateDirectory(testRoot);

                SetupSyntheticVersion(testRoot, "1.0.8", ("Soda.AssetQuickRead.Bridge.exe", "bridge 108"));

                // Create a zip payload for 1.0.9
                var archive = Path.Combine(testRoot, "payload.zip");
                using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
                {
                    var entry = zip.CreateEntry("Soda.AssetQuickRead.Bridge.exe");
                    using var writer = new StreamWriter(entry.Open());
                    writer.Write("bridge 109 content");
                }

                var archiveDigest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archive)));
                var bridgeFile = new CatalogFile("Soda.AssetQuickRead.Bridge.exe", 18, Convert.ToHexString(SHA256.HashData("bridge 109 content"u8.ToArray())));
                var goodCatalog = new Catalog(InstallEngine.Version, archiveDigest, [bridgeFile]);

                // Case A: Corrupt payload hash fails before copy/retire
                var badCatalog = goodCatalog with { ArchiveSha256 = new string('0', 64) };
                var failingRegA = new MockRegistration("1.0.8");
                try
                {
                    InstallEngine.Install(archive, badCatalog, testRoot, failingRegA, CancellationToken.None, _ => { });
                    throw new Exception("expected payload_hash_mismatch exception");
                }
                catch (IOException ex) when (ex.Message == "payload_hash_mismatch")
                {
                    Check(Directory.Exists(Path.Combine(testRoot, "versions", "1.0.8")), "failure_protection", "1.0.8 preserved on payload mismatch");
                }

                // Case B: Cancellation during install never prunes
                using var cts = new CancellationTokenSource();
                cts.Cancel();
                var failingRegB = new MockRegistration("1.0.8");
                try
                {
                    InstallEngine.Install(archive, goodCatalog, testRoot, failingRegB, cts.Token, _ => { });
                    throw new Exception("expected cancellation exception");
                }
                catch (OperationCanceledException)
                {
                    Check(Directory.Exists(Path.Combine(testRoot, "versions", "1.0.8")), "failure_protection", "1.0.8 preserved on cancellation");
                }

                // Case C: Registry apply failure rolls back and never prunes
                var failingRegC = new MockRegistration("1.0.8", failOnApply: true);
                try
                {
                    InstallEngine.Install(archive, goodCatalog, testRoot, failingRegC, CancellationToken.None, _ => { });
                    throw new Exception("expected registry failure exception");
                }
                catch (IOException ex) when (ex.Message == "synthetic_apply_failure")
                {
                    Check(failingRegC.RolledBackCount == 1, "failure_protection", "registration rolled back");
                    Check(Directory.Exists(Path.Combine(testRoot, "versions", "1.0.8")), "failure_protection", "1.0.8 preserved on registration rollback");
                }
            }

            // -------------------------------------------------------------
            // Test 12: Deterministic mutation between initial validation and guarded removal
            // Using a digest override to simulate mutation after early IsFullyVerified passes.
            // Under guarded handle verification, size/hash check detects change and candidate survives.
            // -------------------------------------------------------------
            {
                var testRoot = Path.Combine(root, "test-12-mutation-race");
                Directory.CreateDirectory(testRoot);

                var v100Cat = SetupSyntheticVersion(testRoot, "1.0.0", ("app.exe", "v1.0.0 initial valid exe"));
                SetupSyntheticVersion(testRoot, "1.0.8", ("app.exe", "v1.0.8 exe"));
                SetupSyntheticVersion(testRoot, "1.0.9", ("app.exe", "v1.0.9 exe"));

                var validSha = v100Cat.Files[0].Sha256;
                var tamperedSha = new string('F', 64);

                var invocationCount = 0;
                string MutatingDigestOverride(string path)
                {
                    // First call (during early IsFullyVerified): returns valid sha
                    // Second call (under guarded handle in RetireCandidate): returns tampered sha
                    invocationCount++;
                    return invocationCount == 1 ? validSha : tamperedSha;
                }

                var reg = new MockRegistration("1.0.8");
                VersionRetention.RetireOlderVersions(testRoot, "1.0.9", reg, publishedVersions, GetCatalog, digest: MutatingDigestOverride);

                Check(invocationCount >= 2, "deterministic_mutation_race", "digest override invoked across validation and guarded removal");
                Check(Directory.Exists(Path.Combine(testRoot, "versions", "1.0.0")), "deterministic_mutation_race", "mutated candidate preserved intact under guard");
                Check(File.Exists(Path.Combine(testRoot, "versions", "1.0.0", "app.exe")), "deterministic_mutation_race", "modified file survived removal attempt");
            }

            // Native apphost and dotnet.exe have different long-path manifests. Native guards
            // must work independently of the host manifest while preserving exact identities.
            {
                var testRoot = Path.Combine(root, "long-path-" + new string('x', 100), new string('y', 100));
                SetupSyntheticVersion(testRoot, "1.0.0", ("app.exe", "long path old bytes"));
                SetupSyntheticVersion(testRoot, "1.0.8", ("app.exe", "long path rollback bytes"));
                SetupSyntheticVersion(testRoot, "1.0.9", ("app.exe", "long path current bytes"));
                Check(Path.Combine(testRoot, "versions", "1.0.0", "app.exe").Length > 260,
                    "long_native_path", "fixture exceeds MAX_PATH");
                VersionRetention.RetireOlderVersions(testRoot, "1.0.9", new MockRegistration("1.0.8"), publishedVersions, GetCatalog,
                    syntheticDiagnostic: (version, stage, code) => syntheticDiagnostics.Add(new { version, stage, code }));
                Check(!Directory.Exists(Path.Combine(testRoot, "versions", "1.0.0")), "long_native_path", "verified old version retired");
                Check(File.ReadAllText(Path.Combine(testRoot, "versions", "1.0.8", "app.exe")) == "long path rollback bytes",
                    "long_native_path", "actual rollback preserved");
            }

            // Cancellation from Apply must roll back registration in both installation paths.
            foreach (var sameVersion in new[] { false, true })
            {
                var testRoot = Path.Combine(root, sameVersion ? "cancel-after-apply-repair" : "cancel-after-apply-new");
                Directory.CreateDirectory(testRoot);
                var archive = Path.Combine(testRoot, "payload.zip");
                using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
                {
                    using var writer = new StreamWriter(zip.CreateEntry("Soda.AssetQuickRead.Bridge.exe").Open());
                    writer.Write("synthetic bridge");
                }
                var pin = new CatalogFile("Soda.AssetQuickRead.Bridge.exe", "synthetic bridge"u8.Length,
                    Convert.ToHexString(SHA256.HashData("synthetic bridge"u8)));
                var catalog = new Catalog(InstallEngine.Version, InstallEngine.Digest(archive), [pin]);
                if (sameVersion) SetupSyntheticVersion(testRoot, InstallEngine.Version, (pin.Path, "synthetic bridge"));
                SetupSyntheticVersion(testRoot, "1.0.8", ("keep.bin", "previous version"));
                using var cts = new CancellationTokenSource();
                var reg = new MockRegistration("1.0.8", afterApply: cts.Cancel);
                var cancelled = false;
                try { InstallEngine.Install(archive, catalog, testRoot, reg, cts.Token, _ => { }); }
                catch (OperationCanceledException) { cancelled = true; }
                Check(cancelled && reg.AppliedCount == 1 && reg.RolledBackCount == 1,
                    "cancel_after_apply", sameVersion ? "repair registration rolled back" : "new registration rolled back");
                Check(Directory.Exists(Path.Combine(testRoot, "versions", InstallEngine.Version)) == sameVersion,
                    "cancel_after_apply", "only newly promoted version rolled back");
                Check(File.ReadAllText(Path.Combine(testRoot, "versions", "1.0.8", "keep.bin")) == "previous version",
                    "cancel_after_apply", "previous bytes preserved");

                // Progress 100 is the successful commit signal; later cancellation cannot roll back.
                var committedReg = new MockRegistration("1.0.8");
                using var committedCts = new CancellationTokenSource();
                var target = InstallEngine.Install(archive, catalog, testRoot, committedReg, committedCts.Token,
                    value => { if (value == 100) committedCts.Cancel(); });
                Check(committedCts.IsCancellationRequested && committedReg.RolledBackCount == 0 && Directory.Exists(target),
                    "cancel_after_commit", sameVersion ? "repair remains committed" : "new installation remains committed");
            }

            var report = new
            {
                testSuite = "VersionRetentionTests",
                passedCount = passed,
                failedCount = 0,
                reparseTested,
                timestamp = DateTime.UtcNow.ToString("O"),
                evidenceRoot = root,
                tests = testResults
            };

            var evidenceDir = Path.GetDirectoryName(Path.GetFullPath(evidencePath))!;
            Directory.CreateDirectory(evidenceDir);
            File.WriteAllText(evidencePath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

            return returnAssertionCount ? passed : 0;
        }
        finally
        {
            // Clean up temporary isolated test tree
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch
            {
                // Best effort test cleanup
            }
        }
    }
}
