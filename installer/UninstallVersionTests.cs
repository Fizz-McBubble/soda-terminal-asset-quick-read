namespace Soda.AssetQuickRead.Setup;

internal static class UninstallVersionTests
{
    internal static int RunStandalone(string reportPath)
    {
        reportPath = Path.GetFullPath(reportPath);
        var root = Path.Combine(Path.GetDirectoryName(reportPath)!, "uninstall-run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var assertions = Run(root);
            File.WriteAllText(reportPath, System.Text.Json.JsonSerializer.Serialize(new { passed = true, assertions, root }));
            return 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(reportPath, System.Text.Json.JsonSerializer.Serialize(new { passed = false, error = error.Message, root }));
            return 1;
        }
    }
    private sealed class ProtocolStore : IProtocolRegistrationStore
    {
        internal ProtocolRegistration? Value;
        internal bool RefuseRemoval;
        internal Action<int>? OnRead;
        private int reads;
        public ProtocolRegistration? Read() { OnRead?.Invoke(++reads); return Value; }
        public void Create(string command) => throw new InvalidOperationException("unexpected_test_write");
        public bool Replace(ProtocolRegistration expected, string command) => throw new InvalidOperationException("unexpected_test_write");
        public void RemoveCreated(string command)
        { if (!RefuseRemoval && Value?.Command == command && Value.OwnedShape) Value = null; }
    }
    private sealed class UninstallStore : IUninstallStore
    {
        internal UninstallRecord? Value;
        internal bool RefuseRemoval, ChangeOnSecondRead;
        private int reads;
        public UninstallRecord? Read()
        {
            if (++reads == 2 && ChangeOnSecondRead && Value is not null) Value = Value with { OwnedShape = false };
            return Value;
        }
        public void Write(UninstallRecord value) => throw new InvalidOperationException("unexpected_test_write");
        public bool Remove(UninstallRecord value)
        { if (RefuseRemoval || Value != value) return false; Value = null; return true; }
    }
    private sealed record Fixture(string Root, Catalog Current, Dictionary<string, Catalog> Catalogs,
        ProtocolStore ProtocolStore, UserRegistration Protocol, UninstallStore Store, string StubHash, string Shortcut);

    internal static int Run(string baseRoot)
    {
        var passed = 0;
        void Check(bool value, string name) { if (!value) throw new Exception("uninstall_versions_" + name); passed++; }
        void Reject(Action action, string code)
        {
            try { action(); }
            catch (IOException error) { Check(error.Message == code, code); return; }
            throw new Exception("uninstall_versions_expected_failure_" + code);
        }
        var versions = InstallRegistration.PublishedVersions.Append(InstallEngine.Version).Distinct().ToArray();
        foreach (var version in InstallRegistration.PublishedVersions)
        {
            var actual = AssetUninstall.PublishedCatalog(version);
            Check(actual.Version == version && actual.Files.Length > 5, "published_catalog_" + version);
            if (version is "1.0.4" or "1.0.5")
                Check(actual.Files.SequenceEqual(AssetUninstall.Published103Catalog().Files), "immutable_runtime_" + version);
        }
        Fixture New(string name, string registeredVersion)
        {
            var root = Path.Combine(baseRoot, "uninstall-versions", name);
            var catalogs = new Dictionary<string, Catalog>();
            foreach (var version in versions)
            {
                var pins = new List<CatalogFile>();
                foreach (var relative in new[] { "Soda.AssetQuickRead.Bridge.exe", "runtime/tool.bin", "runtime/modified.bin" })
                {
                    var file = InstallEngine.Safe(Path.Combine(root, "versions", version), relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    File.WriteAllText(file, "SYNTHETIC:" + version + ":" + relative);
                    pins.Add(new(relative, new FileInfo(file).Length, InstallEngine.Digest(file)));
                }
                catalogs.Add(version, new(version, "synthetic_archive", pins.ToArray()));
                File.AppendAllText(Path.Combine(root, "versions", version, "runtime", "modified.bin"), ":USER_EDIT");
                File.WriteAllText(Path.Combine(root, "versions", version, "unknown.txt"), "FOREIGN");
            }
            var stub = Path.Combine(root, "uninstall.exe"); File.WriteAllText(stub, "MZ synthetic uninstall stub");
            Directory.CreateDirectory(Path.Combine(root, "jobs")); File.WriteAllText(Path.Combine(root, "jobs", "keep.json"), "PRESERVE");
            var shortcut = Path.Combine(root, "start-menu", "reader.url");
            Directory.CreateDirectory(Path.GetDirectoryName(shortcut)!); File.WriteAllText(shortcut, UserRegistration.ShortcutBody);
            var protocolStore = new ProtocolStore { Value = new(UserRegistration.Description, "",
                UserRegistration.Command(Path.Combine(root, "versions", registeredVersion, "Soda.AssetQuickRead.Bridge.exe")), true) };
            return new(root, catalogs[InstallEngine.Version], catalogs, protocolStore,
                new UserRegistration(protocolStore, shortcut), new UninstallStore { Value = InstallRegistration.Record(root) with { Version = registeredVersion } },
                InstallEngine.Digest(stub), shortcut);
        }
        void Remove(Fixture fixture, Func<string, string>? digest = null) => AssetUninstall.RemoveForTests(
            fixture.Root, fixture.Current, fixture.Protocol, fixture.Store, fixture.StubHash, version => fixture.Catalogs[version], digest);
        Dictionary<string, string> Snapshot(Fixture fixture) => Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, InstallEngine.Digest);
        void Unchanged(Fixture fixture, Dictionary<string, string> before)
        {
            var after = Snapshot(fixture);
            Check(after.Count == before.Count && before.All(pair => after.GetValueOrDefault(pair.Key) == pair.Value), "failure_preserves_files");
        }
        foreach (var version in versions)
        {
            var fixture = New("registered-" + version, version);
            Remove(fixture);
            Check(fixture.Store.Value is null && fixture.ProtocolStore.Value is null && !File.Exists(fixture.Shortcut) &&
                !File.Exists(Path.Combine(fixture.Root, "uninstall.exe")), "owned_entries_removed_" + version);
            foreach (var installedVersion in versions)
            {
                var root = Path.Combine(fixture.Root, "versions", installedVersion);
                Check(!File.Exists(Path.Combine(root, "Soda.AssetQuickRead.Bridge.exe")) && !File.Exists(Path.Combine(root, "runtime", "tool.bin")), "all_exact_files_removed");
                Check(File.ReadAllText(Path.Combine(root, "runtime", "modified.bin")).EndsWith(":USER_EDIT") &&
                    File.ReadAllText(Path.Combine(root, "unknown.txt")) == "FOREIGN", "modified_and_unknown_preserved");
            }
            Check(File.ReadAllText(Path.Combine(fixture.Root, "jobs", "keep.json")) == "PRESERVE", "results_preserved");
            Remove(fixture); Check(fixture.Store.Value is null, "repeat_is_idempotent");
        }
        foreach (var version in InstallRegistration.PublishedVersions.Where(version => InstallRegistration.OldPin(version) is not null))
        {
            var fixture = New("old-stub-" + version, version);
            var stub = Path.Combine(fixture.Root, "uninstall.exe");
            Remove(fixture, path => path == stub ? InstallRegistration.OldPin(version)! : InstallEngine.Digest(path));
            Check(fixture.Store.Value is null && !File.Exists(stub), "published_stub_identity_" + version);
        }
        {
            var fixture = New("orphan-published-stub", "1.0.1"); fixture.Store.Value = null;
            var stub = Path.Combine(fixture.Root, "uninstall.exe");
            Remove(fixture, path => path == stub ? InstallRegistration.OldPin("1.0.1")! : InstallEngine.Digest(path));
            Check(fixture.ProtocolStore.Value is null && !File.Exists(stub), "missing_record_still_removes_owned_entries");
        }
        {
            var fixture = New("mismatched-published-stub", "1.0.2"); var before = Snapshot(fixture);
            var stub = Path.Combine(fixture.Root, "uninstall.exe");
            Reject(() => Remove(fixture, path => path == stub ? InstallRegistration.OldPin("1.0.1")! : InstallEngine.Digest(path)), "uninstaller_file_conflict");
            Unchanged(fixture, before);
        }
        foreach (var failure in new[] { "unknown-stub", "foreign-registration", "wrong-root", "unsupported-version", "registration-race" })
        {
            var fixture = New(failure, "1.0.1");
            if (failure == "unknown-stub") File.WriteAllText(Path.Combine(fixture.Root, "uninstall.exe"), "FOREIGN_STUB");
            if (failure == "foreign-registration") fixture.Store.Value = fixture.Store.Value! with { OwnedShape = false };
            if (failure == "wrong-root") fixture.Store.Value = fixture.Store.Value! with { Root = fixture.Root + "-foreign" };
            if (failure == "unsupported-version") fixture.Store.Value = fixture.Store.Value! with { Version = "9.9.9" };
            if (failure == "registration-race") fixture.Store.ChangeOnSecondRead = true;
            var before = Snapshot(fixture); var protocol = fixture.ProtocolStore.Value;
            Reject(() => Remove(fixture), failure == "unknown-stub" ? "uninstaller_file_conflict" :
                failure == "registration-race" ? "uninstall_registration_changed" : "uninstall_registration_conflict");
            Unchanged(fixture, before); Check(fixture.ProtocolStore.Value == protocol && fixture.Store.Value is not null, "failure_preserves_entries");
        }
        foreach (var failure in new[] { "foreign-protocol", "foreign-protocol-command", "foreign-shortcut" })
        {
            var fixture = New(failure, "1.0.4");
            if (failure == "foreign-protocol") fixture.ProtocolStore.Value = fixture.ProtocolStore.Value! with { OwnedShape = false };
            if (failure == "foreign-protocol-command") fixture.ProtocolStore.Value = fixture.ProtocolStore.Value! with { Command = "FOREIGN_COMMAND" };
            if (failure == "foreign-shortcut") File.WriteAllText(fixture.Shortcut, "FOREIGN_SHORTCUT");
            var before = Snapshot(fixture); var foreign = fixture.ProtocolStore.Value;
            Reject(() => Remove(fixture), failure == "foreign-shortcut" ? "start_menu_shortcut_conflict" : "protocol_registration_conflict");
            Unchanged(fixture, before);
            Check(fixture.ProtocolStore.Value == foreign && fixture.Store.Value is not null, "foreign_context_preserved");
        }
        foreach (var failure in new[] { "protocol-refuses", "registration-refuses" })
        {
            var fixture = New(failure, "1.0.2");
            fixture.ProtocolStore.RefuseRemoval = failure == "protocol-refuses";
            fixture.Store.RefuseRemoval = failure == "registration-refuses";
            Reject(() => Remove(fixture), failure == "protocol-refuses" ? "protocol_registration_changed" : "uninstall_registration_changed");
            Check(fixture.Store.Value is not null && File.Exists(Path.Combine(fixture.Root, "uninstall.exe")), "failed_entries_keep_recovery_stub");
            Check(File.ReadAllText(Path.Combine(fixture.Root, "jobs", "keep.json")) == "PRESERVE", "failed_entries_keep_results");
        }
        {
            var fixture = New("protocol-replaced-during-preflight", "1.0.5"); var before = Snapshot(fixture);
            var foreign = fixture.ProtocolStore.Value! with { Command = "FOREIGN_AFTER_SNAPSHOT" };
            fixture.ProtocolStore.OnRead = count => { if (count == 2) fixture.ProtocolStore.Value = foreign; };
            Reject(() => Remove(fixture), "protocol_registration_changed");
            Unchanged(fixture, before);
            Check(fixture.ProtocolStore.Value == foreign && fixture.Store.Value is not null, "replacement_before_removal_preserved");
        }
        {
            // A reader that permits deletion must not be rejected merely because
            // it denies writes (the same distinction as a resident WinDivert file).
            var fixture = New("delete-shared-runtime", "1.1.3");
            var path = Path.Combine(fixture.Root, "versions", "1.1.3", "runtime", "tool.bin");
            using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            Remove(fixture);
            Check(fixture.Store.Value is null && fixture.ProtocolStore.Value is null && !File.Exists(path), "delete_shared_reader_allows_uninstall");
            Check(File.ReadAllText(Path.Combine(fixture.Root, "versions", "1.1.3", "unknown.txt")) == "FOREIGN", "delete_shared_preserves_unknown");
        }
        {
            var fixture = New("locked-runtime", "1.0.3");
            var before = Snapshot(fixture);
            using (var busy = new FileStream(Path.Combine(fixture.Root, "versions", "1.0.3", "Soda.AssetQuickRead.Bridge.exe"), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var rejected = false;
                try { Remove(fixture); } catch (IOException) { rejected = true; }
                Check(rejected && fixture.Store.Value is not null && fixture.ProtocolStore.Value is not null, "busy_tool_rejects");
            }
            Unchanged(fixture, before);
        }
        return passed;
    }
}
