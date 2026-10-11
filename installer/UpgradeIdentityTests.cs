using System.Text.Json;
namespace Soda.AssetQuickRead.Setup;

internal static class UpgradeIdentityTests
{
    private sealed class ProtocolStore : IProtocolRegistrationStore
    {
        internal ProtocolRegistration? Value;
        public ProtocolRegistration? Read() => Value;
        public void Create(string command) => Value = new(UserRegistration.Description, "", command, true);
        public bool Replace(ProtocolRegistration expected, string command)
        { if (Value != expected) return false; Value = expected with { Command = command }; return true; }
        public void RemoveCreated(string command) { if (Value?.Command == command) Value = null; }
    }
    private sealed class UninstallStore : IUninstallStore
    {
        internal UninstallRecord? Value;
        internal bool Fail;
        public UninstallRecord? Read() => Value;
        public void Write(UninstallRecord value) { Value = value; if (Fail) throw new IOException("synthetic_write_failure"); }
        public bool Remove(UninstallRecord value) { if (Value != value) return false; Value = null; return true; }
    }
    internal static int RunPreflight(string output)
    {
        var root = Path.Combine(Path.GetDirectoryName(output)!, "preflight-counterexample");
        Directory.CreateDirectory(root); var oldStub = Path.Combine(root, "uninstall.exe"); File.WriteAllText(oldStub, "UNKNOWN PRESERVE");
        var diagnostics = new InstallerDiagnostics(Path.Combine(root, "diagnostics"));
        var registration = new InstallRegistration(root, new UserRegistration(new ProtocolStore(), Path.Combine(root, "reader.url")), new UninstallStore(), () => new MemoryStream("MZ new"u8.ToArray()), diagnostics);
        var code = "";
        try { InstallEngine.Install(Path.Combine(root, "missing.zip"), new Catalog(InstallEngine.Version, "unused", new[] { new CatalogFile("file", 1, "unused") }), root, registration, CancellationToken.None, _ => { }, diagnostics: diagnostics); }
        catch (IOException error) { code = error.Message; }
        var identity = File.ReadAllText(Path.Combine(root, "diagnostics", "latest.json"));
        var ok = identity.Contains("preflight_failed") && !identity.Contains(root) && code == "uninstaller_file_conflict" && !Directory.Exists(Path.Combine(root, "versions")) && File.ReadAllText(oldStub) == "UNKNOWN PRESERVE";
        File.WriteAllText(output, JsonSerializer.Serialize(new { ok, code, payloadCopyStarted = Directory.Exists(Path.Combine(root, "versions")), scope = "isolated foreign stub; no real registry or installer execution" }));
        return ok ? 0 : 1;
    }
    internal static int Run(string output, string oldStub)
    {
        var checks = new List<string>();
        void Check(bool condition, string name) { if (!condition) throw new Exception(name); checks.Add(name); }
        try
        {
            Check(InstallEngine.ReadCatalog().Version == InstallEngine.Version, "installer_catalog_identity");
            Check(InstallEngine.Digest(oldStub) == "B217E640A656FB5B5B6BAF4167DE0BAD2364164847C347F5FF4FEA86C0FECC5B", "actual_published_101_uninstaller");
            var pins = AssetUninstall.Published101Catalog().Files.Where(file => UserRegistration.Published100Pins.ContainsKey(file.Path)).ToArray();
            Check(pins.Length == 5, "actual_published_101_launch_pins");
            foreach (var outcome in new[] { "success", "rollback", "foreign-stub", "orphan-success", "orphan-rollback", "orphan-foreign-stub", "orphan-foreign-runtime", "orphan-foreign-protocol", "orphan-missing-protocol", "orphan-missing-protocol-rollback", "orphan-missing-protocol-foreign-runtime", "orphan-missing-protocol-race", "orphan-registry-race", "orphan-stub-race", "orphan-runtime-race", "orphan-protocol-race" })
            {
                var orphan = outcome.StartsWith("orphan-", StringComparison.Ordinal);
                var success = outcome is "success" or "orphan-success" or "orphan-missing-protocol";
                var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(output)!, "upgrade-" + outcome + "-" + Guid.NewGuid().ToString("N")));
                var previous = Path.Combine(root, "versions", "1.0.1");
                foreach (var pin in pins)
                { var file = InstallEngine.Safe(previous, pin.Path); Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file, "SYNTHETIC:" + pin.Path); }
                Directory.CreateDirectory(Path.Combine(root, "jobs")); File.WriteAllText(Path.Combine(root, "jobs", "keep.json"), "PRESERVE");
                var installedStub = Path.Combine(root, "uninstall.exe");
                if (outcome.EndsWith("foreign-stub", StringComparison.Ordinal)) File.WriteAllText(installedStub, "UNKNOWN"); else File.Copy(oldStub, installedStub);
                var originalHash = InstallEngine.Digest(installedStub);
                var protocol = new ProtocolStore { Value = new(UserRegistration.Description, "", UserRegistration.Command(Path.Combine(previous, "Soda.AssetQuickRead.Bridge.exe")), true) };
                if (outcome == "orphan-foreign-protocol") protocol.Value = protocol.Value with { OwnedShape = false };
                if (outcome.StartsWith("orphan-missing-protocol", StringComparison.Ordinal)) protocol.Value = null;
                var oldProtocol = protocol.Value;
                var store = new UninstallStore { Value = orphan ? null : InstallRegistration.Record(root) with { Version = "1.0.1" }, Fail = outcome is "rollback" or "orphan-rollback" or "orphan-missing-protocol-rollback" };
                var runtimeChanged = outcome is "orphan-foreign-runtime" or "orphan-missing-protocol-foreign-runtime";
                var registration = new UserRegistration(protocol, Path.Combine(root, "start-menu", "reader.url"), path =>
                {
                    if (runtimeChanged) return "UNKNOWN";
                    var relative = Path.GetRelativePath(previous, path).Replace('\\', '/');
                    return pins.Single(pin => pin.Path == relative).Sha256;
                });
                var replacement = "MZ synthetic new icon uninstaller"u8.ToArray();
                var entries = new InstallRegistration(root, registration, store, () => new MemoryStream(replacement));
                var target = Path.Combine(root, "versions", InstallEngine.Version, "Soda.AssetQuickRead.Bridge.exe");
                UninstallRecord? foreignRecord = null;
                try
                {
                    entries.Preflight(target);
                    if (outcome == "orphan-registry-race") store.Value = foreignRecord = InstallRegistration.Record(root) with { Root = "FOREIGN" };
                    if (outcome == "orphan-stub-race") File.WriteAllText(installedStub, "CHANGED AFTER PREFLIGHT");
                    if (outcome == "orphan-runtime-race") runtimeChanged = true;
                    if (outcome == "orphan-protocol-race") protocol.Value = oldProtocol = protocol.Value! with { OwnedShape = false };
                    if (outcome == "orphan-missing-protocol-race") protocol.Value = oldProtocol = new("FOREIGN", "", "FOREIGN", false);
                    entries.Apply(target); Check(success, outcome + "_must_reject");
                }
                catch (IOException) { Check(!success, outcome + "_expected_failure"); store.Fail = false; entries.Rollback(); }
                if (success)
                {
                    Check(store.Value == InstallRegistration.Record(root), "101_registry_promoted");
                    Check(protocol.Value?.Command == UserRegistration.Command(target), "101_protocol_promoted");
                    Check(File.ReadAllBytes(installedStub).SequenceEqual(replacement), "101_uninstaller_replaced");
                }
                else
                {
                    Check(store.Value == (foreignRecord ?? (orphan ? null : InstallRegistration.Record(root) with { Version = "1.0.1" })) && protocol.Value == oldProtocol, outcome + "_registration_restored");
                    Check(outcome == "orphan-stub-race" ? File.ReadAllText(installedStub) == "CHANGED AFTER PREFLIGHT" : InstallEngine.Digest(installedStub) == originalHash, outcome + "_stub_preserved");
                }
                Check(File.ReadAllText(Path.Combine(root, "jobs", "keep.json")) == "PRESERVE", outcome + "_results_preserved");
                Check(pins.All(pin => File.ReadAllText(InstallEngine.Safe(previous, pin.Path)) == "SYNTHETIC:" + pin.Path), outcome + "_old_runtime_preserved");
            }
            File.WriteAllText(output, JsonSerializer.Serialize(new { ok = true, checks, scope = "synthetic registration upgrade only; no service, UAC, game or real-account operation" }));
            return 0;
        }
        catch (Exception error) { File.WriteAllText(output, JsonSerializer.Serialize(new { ok = false, checks, error = error.Message })); return 1; }
    }
}
