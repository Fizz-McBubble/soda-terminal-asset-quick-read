namespace Soda.AssetQuickRead.Setup;

internal static class StaleRegistrationTests
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
        internal bool Fail, Ignore;
        public UninstallRecord? Read() => Value;
        public void Write(UninstallRecord value) { if (!Ignore) Value = value; if (Fail) throw new IOException("synthetic_registration_failure"); }
        public bool Remove(UninstallRecord value) { if (Value != value) return false; Value = null; return true; }
    }
    internal static int Run(string baseRoot, string oldStub)
    {
        var passed = 0;
        void Check(bool value, string name) { if (!value) throw new Exception("stale_registration_" + name); passed++; }
        Check(InstallEngine.Digest(oldStub) == InstallRegistration.OldPin("1.0.7"), "published_107_stub_identity");
        var catalog = AssetUninstall.Published103Catalog();
        foreach (var outcome in new[] { "success", "write-failure", "ignored-write", "foreign-stub", "foreign-shape", "foreign-root", "foreign-protocol", "foreign-nonlaunch-file", "unknown-file", "runtime-race", "protocol-race", "registry-race", "stub-race" })
        {
            var root = Path.Combine(baseRoot, "stale-106-107-" + outcome);
            var previous = Path.Combine(root, "versions", "1.0.6");
            foreach (var pin in catalog.Files)
            {
                var file = InstallEngine.Safe(previous, pin.Path); Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, "SYNTHETIC:" + pin.Path);
            }
            Directory.CreateDirectory(Path.Combine(root, "jobs")); var keep = Path.Combine(root, "jobs", "keep.json"); File.WriteAllText(keep, "PRESERVE");
            var installedStub = Path.Combine(root, "uninstall.exe");
            File.Copy(oldStub, installedStub);
            if (outcome == "foreign-stub") File.WriteAllText(installedStub, "FOREIGN");
            var originalHash = InstallEngine.Digest(installedStub);
            var protocol = new ProtocolStore { Value = new(UserRegistration.Description, "", UserRegistration.Command(Path.Combine(previous, "Soda.AssetQuickRead.Bridge.exe")), true) };
            if (outcome == "foreign-protocol") protocol.Value = protocol.Value with { Command = "FOREIGN" };
            var oldProtocol = protocol.Value;
            var oldRecord = InstallRegistration.Record(root) with { Version = "1.0.6" };
            if (outcome == "foreign-shape") oldRecord = oldRecord with { OwnedShape = false };
            if (outcome == "foreign-root") oldRecord = oldRecord with { Root = baseRoot };
            var store = new UninstallStore { Value = oldRecord, Fail = outcome == "write-failure", Ignore = outcome == "ignored-write" };
            var nonlaunch = catalog.Files.First(pin => !UserRegistration.Published100Pins.ContainsKey(pin.Path));
            if (outcome == "foreign-nonlaunch-file") File.AppendAllText(InstallEngine.Safe(previous, nonlaunch.Path), "USER_EDIT");
            if (outcome == "unknown-file") File.WriteAllText(Path.Combine(previous, "unknown.txt"), "FOREIGN");
            var shortcut = Path.Combine(root, "start-menu", "reader.url");
            var registration = new UserRegistration(protocol, shortcut, path =>
            {
                var relative = Path.GetRelativePath(previous, path).Replace('\\', '/');
                return catalog.Files.Single(pin => pin.Path == relative) is { } pin && File.ReadAllText(path) == "SYNTHETIC:" + relative ? pin.Sha256 : InstallEngine.Digest(path);
            });
            var replacement = "MZ synthetic new uninstaller"u8.ToArray();
            var entries = new InstallRegistration(root, registration, store, () => new MemoryStream(replacement));
            var target = Path.Combine(root, "versions", InstallEngine.Version, "Soda.AssetQuickRead.Bridge.exe");
            var succeeded = false;
            try
            {
                entries.Preflight(target);
                if (outcome == "runtime-race") File.AppendAllText(InstallEngine.Safe(previous, nonlaunch.Path), "USER_EDIT");
                if (outcome == "protocol-race") protocol.Value = oldProtocol = protocol.Value! with { Command = "FOREIGN" };
                if (outcome == "registry-race") store.Value = oldRecord = oldRecord with { OwnedShape = false };
                if (outcome == "stub-race") { File.WriteAllText(installedStub, "FOREIGN RACE"); originalHash = InstallEngine.Digest(installedStub); }
                entries.Apply(target); succeeded = true;
                Check(store.Value == InstallRegistration.Record(root) && protocol.Value?.Command == UserRegistration.Command(target), "promoted_both_registrations");
                Check(File.ReadAllBytes(installedStub).SequenceEqual(replacement), "replaced_exact_stub");
            }
            catch (IOException) { }
            Check(succeeded == (outcome == "success"), outcome + "_acceptance");
            store.Fail = false; store.Ignore = false; entries.Rollback();
            Check(store.Value == oldRecord && protocol.Value == oldProtocol, outcome + "_restored_owned_registration");
            Check(InstallEngine.Digest(installedStub) == originalHash, outcome + "_preserved_original_stub");
            Check(File.ReadAllText(keep) == "PRESERVE", outcome + "_preserved_jobs");
            if (outcome is "foreign-nonlaunch-file" or "runtime-race") Check(File.ReadAllText(InstallEngine.Safe(previous, nonlaunch.Path)).EndsWith("USER_EDIT"), "preserved_modified_payload");
            if (outcome == "unknown-file") Check(File.ReadAllText(Path.Combine(previous, "unknown.txt")) == "FOREIGN", "preserved_unknown_file");
        }
        return passed;
    }
}
