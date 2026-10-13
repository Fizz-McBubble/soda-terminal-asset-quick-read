using Microsoft.Win32;
namespace Soda.AssetQuickRead.Setup;

internal sealed record UninstallRecord(string Root, string Command, string Version, bool OwnedShape = true);
internal interface IUninstallStore
{
    UninstallRecord? Read();
    void Write(UninstallRecord value);
    bool Remove(UninstallRecord value);
}
internal sealed class RegistryUninstallStore : IUninstallStore
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\SodaTerminalAssetQuickRead";
    internal const string DisplayName = "Soda Terminal · 资产快读";
    public UninstallRecord? Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(Key);
        if (key is null) return null;
        var owned = key.GetSubKeyNames().Length == 0 && key.GetValueNames().Order().SequenceEqual(
            new[] { "DisplayIcon", "DisplayName", "DisplayVersion", "InstallLocation", "NoModify", "NoRepair", "Publisher", "UninstallString" }.Order());
        owned &= key.GetValue("DisplayName") as string == DisplayName && key.GetValue("Publisher") as string == "Soda Terminal";
        var root = key.GetValue("InstallLocation") as string ?? "";
        var command = key.GetValue("UninstallString") as string ?? "";
        owned &= key.GetValue("NoModify") is int modify && modify == 1 && key.GetValue("NoRepair") is int repair && repair == 0;
        owned &= key.GetValue("DisplayIcon") as string == Path.Combine(root, "uninstall.exe");
        return new(root, command, key.GetValue("DisplayVersion") as string ?? "", owned);
    }
    public void Write(UninstallRecord value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Key);
        key.SetValue("DisplayName", DisplayName); key.SetValue("DisplayVersion", value.Version);
        key.SetValue("Publisher", "Soda Terminal"); key.SetValue("InstallLocation", value.Root);
        key.SetValue("UninstallString", value.Command); key.SetValue("DisplayIcon", Path.Combine(value.Root, "uninstall.exe"));
        key.SetValue("NoModify", 1); key.SetValue("NoRepair", 0);
    }
    public bool Remove(UninstallRecord value)
    { if (Read() != value) return false; Registry.CurrentUser.DeleteSubKeyTree(Key, false); return true; }
}

internal sealed class InstallRegistration : IRegistration
{
    private readonly string root;
    private readonly UserRegistration protocol;
    private readonly IUninstallStore store;
    private readonly Func<Stream> stub;
    private UninstallRecord? previous, applied;
    private InstallerDiagnostics? diagnostics;
    private UninstallRecord? preflightRecord;
    private string? preflightHash;
    private bool preflightDone;
    private bool fileCreated;
    private byte[]? replacedStub;
    private string? stubHash;
    internal static UninstallRecord Record(string root) => new(root, "\"" + Path.Combine(root, "uninstall.exe") + "\" --uninstall", InstallEngine.Version);
    public string? PreviousRegisteredVersion => previous is { OwnedShape: true } ? previous.Version : null;
    internal InstallRegistration(string root, InstallerDiagnostics? diagnostics = null) : this(root, new UserRegistration(), new RegistryUninstallStore(),
        OpenUninstaller) { this.diagnostics = diagnostics; }
    internal static Stream OpenUninstaller()
    {
        var assembly = typeof(InstallRegistration).Assembly;
        var embedded = assembly.GetManifestResourceStream("uninstaller.exe");
        if (embedded is not null) return embedded;
        using var metadata = assembly.GetManifestResourceStream("uninstall-payload.json") ?? throw new IOException("uninstaller_missing");
        using var identity = System.Text.Json.JsonDocument.Parse(metadata);
        var payload = Path.Combine(AppContext.BaseDirectory, "payload"); InstallEngine.NoLinks(payload);
        for (var attempt = 0; !File.Exists(Path.Combine(payload, "ready")); attempt++)
        { if (File.Exists(Path.Combine(payload, "failed")) || attempt >= 600) throw new IOException("offline_payload_unavailable"); Thread.Sleep(200); }
        var path = Path.Combine(AppContext.BaseDirectory, "payload", "uninstaller.exe"); InstallEngine.NoLinks(path);
        var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(input));
            if (input.Length != identity.RootElement.GetProperty("size").GetInt64() ||
                hash != identity.RootElement.GetProperty("sha256").GetString()) throw new IOException("uninstaller_payload_identity_mismatch");
            input.Position = 0; return input;
        }
        catch { input.Dispose(); throw; }
    }
    internal InstallRegistration(string root, UserRegistration protocol, IUninstallStore store, Func<Stream> stub, InstallerDiagnostics? diagnostics = null)
    { this.diagnostics = diagnostics; this.root = Path.GetFullPath(root); this.protocol = protocol; this.store = store; this.stub = stub; }
    internal static IReadOnlyList<string> PublishedVersions { get; } = ["1.0.0", "1.0.1", "1.0.2", "1.0.3", "1.0.4", "1.0.5", "1.0.6", "1.0.7", "1.0.8", "1.0.9"];
    internal static string? OldPin(string? version) => version switch
    {
        "1.0.1" => "B217E640A656FB5B5B6BAF4167DE0BAD2364164847C347F5FF4FEA86C0FECC5B",
        "1.0.2" => "20A3C4D98F79FBC3604ECC8759D914D5968C8B0F5222BCF4E334B328162F371C",
        "1.0.3" => "BC78BF0274F4A9CB7B99F2A6D4E672F96F1C8D71179DA73E05E3157B9002587A",
        "1.0.4" => "AA22B2FD4F585BF622DD080F6A00B817F49C2BFDFDA8D0D50CACE21C51E5871A",
        "1.0.5" => "B611351FBDB5029DCABEC33006E2EDB86A77E3A338E4F026D4F8618B021E40E2",
        // Locally delivered 1.0.6 must also upgrade without a manual uninstall.
        "1.0.6" => "3DE0F301D4AC4E3784494823AA1B35CBC31A5BC9D7E4F90BF2C1106E7D03B1CF",
        "1.0.7" => "EFD73C697FECE39CD1BCF6224E3B19E42D18BD2CC6AFC84298BDB0D5A3FFA08A",
        "1.0.8" => "33B93EFAED51BCAB9112D2546F196DFFFD94D3CF56E62B07726A56F644E74EA7",
        "1.0.9" => "0B260AC2EBD14302D23596AD40A92C011F96697C3DD262C09FB358F3817E813D",
        _ => null
    };
    private string? RecoverableVersion(UninstallRecord? record, string? hash) => record is null && hash is not null
        ? PublishedVersions.FirstOrDefault(version => hash == OldPin(version) && protocol.RecoverableInstalledVersion(root, version))
        : null;
    private bool Stale106Registration(UninstallRecord? record, string? hash) =>
        record is { OwnedShape: true, Version: "1.0.6" } && record == Record(root) with { Version = "1.0.6" } &&
        hash == OldPin("1.0.7") && protocol.TrustedPublished106Payload(root);
    private (UninstallRecord? Record, string? Hash) Inspect(string stage, string incomingHash)
    {
        var record = store.Read(); var expected = Record(root);
        var path = InstallEngine.Safe(root, "uninstall.exe");
        var hash = File.Exists(path) ? InstallEngine.Digest(path) : null;
        var upgrading = record is { OwnedShape: true } && OldPin(record.Version) is not null && record == expected with { Version = record.Version };
        var recovering = RecoverableVersion(record, hash);
        var stale106 = Stale106Registration(record, hash);
        var knownPin = hash is null ? "absent" : hash == incomingHash ? "current" : PublishedVersions.FirstOrDefault(v => hash == OldPin(v)) ?? "unknown";
        diagnostics?.Identity(stage, record is not null, record?.Version is not null && PublishedVersions.Contains(record.Version) ? record.Version : record?.Version == InstallEngine.Version ? "current" : record is null ? "absent" : "unknown",
            record?.OwnedShape == true, record == expected, upgrading || recovering is not null || stale106, hash is not null, knownPin, hash == incomingHash || upgrading && hash == OldPin(record?.Version) || recovering is not null || stale106,
            preflightDone && record != preflightRecord, preflightDone && hash != preflightHash);
        if (record is not null && record != expected && !upgrading) throw new IOException("uninstall_registration_conflict");
        if (hash is not null && hash != incomingHash && recovering is null && !stale106 && (!upgrading || hash != OldPin(record?.Version))) throw new IOException("uninstaller_file_conflict");
        return (record, hash);
    }
    public void Preflight(string executable)
    {
        using var source = stub();
        var incomingHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(source));
        try
        {
            preflightRecord = store.Read();
            var path = InstallEngine.Safe(root, "uninstall.exe");
            preflightHash = File.Exists(path) ? InstallEngine.Digest(path) : null; preflightDone = true;
            Inspect("before_copy", incomingHash);
            protocol.Preflight(executable);
        }
        catch { InspectFailure("preflight_failed", incomingHash); throw; }
    }
    private void InspectFailure(string stage, string incomingHash)
    { try { Inspect(stage, incomingHash); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    public void Apply(string executable)
    {
        try { ApplyStrict(executable); }
        catch { if (stubHash is not null) InspectFailure("apply_failed", stubHash); throw; }
    }
    private void ApplyStrict(string executable)
    {
        var path = InstallEngine.Safe(root, "uninstall.exe");
        using var source = stub(); using var bytes = new MemoryStream(); source.CopyTo(bytes);
        stubHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes.ToArray()));
        Inspect("before_apply", stubHash);
        previous = store.Read();
        applied = Record(root);
        var upgrading = previous is { OwnedShape: true } && OldPin(previous.Version) is not null && previous == applied with { Version = previous.Version };
        if (previous is not null && previous != applied && !upgrading) throw new IOException("uninstall_registration_conflict");
        if (preflightDone && store.Read() != preflightRecord) throw new IOException("uninstall_registration_changed");
        if (preflightDone && (File.Exists(path) ? InstallEngine.Digest(path) : null) != preflightHash) throw new IOException("uninstaller_file_changed");
        if (File.Exists(path) && InstallEngine.Digest(path) != stubHash)
        {
            // Missing registry metadata is repairable only when the pinned stub,
            // five published launch-runtime identities agree, and the protocol
            // is either owned by that version or absent.
            var oldHash = InstallEngine.Digest(path);
            var recovering = RecoverableVersion(previous, oldHash);
            var oldStubHash = OldPin(previous?.Version ?? recovering);
            if (((!upgrading && recovering is null) || oldHash != oldStubHash) && !Stale106Registration(previous, oldHash))
                throw new IOException("uninstaller_file_conflict");
            replacedStub = File.ReadAllBytes(path);
        }
        protocol.Apply(executable);
        if (!File.Exists(path))
        {
            using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            fileCreated = true; bytes.Position = 0; bytes.CopyTo(output);
        }
        else if (replacedStub is not null)
        {
            if (InstallEngine.Digest(path) != Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(replacedStub)))
                throw new IOException("uninstaller_file_changed");
            ReplaceStub(path, bytes.ToArray());
        }
        if (store.Read() != previous) throw new IOException("uninstall_registration_changed");
        store.Write(applied);
        if (store.Read() != applied) throw new IOException("uninstall_registration_verify_failed");
        protocol.VerifyApplied(executable);
        if (!File.Exists(path) || InstallEngine.Digest(path) != stubHash) throw new IOException("uninstaller_file_verify_failed");
        diagnostics?.Record("register", "verified");
    }
    public void Rollback()
    {
        if (applied is not null && store.Read() == applied)
        { if (previous is null) store.Remove(applied); else store.Write(previous); }
        var path = InstallEngine.Safe(root, "uninstall.exe");
        if (fileCreated && File.Exists(path) && InstallEngine.Digest(path) == stubHash) File.Delete(path);
        if (replacedStub is not null && File.Exists(path) && InstallEngine.Digest(path) == stubHash) ReplaceStub(path, replacedStub);
        replacedStub = null;
        protocol.Rollback(); fileCreated = false;
    }
    private static void ReplaceStub(string path, byte[] bytes)
    {
        var temporary = path + ".upgrade-" + Guid.NewGuid().ToString("N");
        InstallEngine.NoLinks(temporary);
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) output.Write(bytes);
            InstallEngine.NoLinks(path);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) { InstallEngine.NoLinks(temporary); File.Delete(temporary); } }
    }
}
