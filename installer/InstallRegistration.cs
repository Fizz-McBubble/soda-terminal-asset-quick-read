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
    private bool fileCreated;
    private string? stubHash;
    internal static UninstallRecord Record(string root) => new(root, "\"" + Path.Combine(root, "uninstall.exe") + "\" --uninstall", InstallEngine.Version);
    internal InstallRegistration(string root) : this(root, new UserRegistration(), new RegistryUninstallStore(),
        OpenUninstaller) { }
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
    internal InstallRegistration(string root, UserRegistration protocol, IUninstallStore store, Func<Stream> stub)
    { this.root = Path.GetFullPath(root); this.protocol = protocol; this.store = store; this.stub = stub; }
    public void Apply(string executable)
    {
        var path = InstallEngine.Safe(root, "uninstall.exe");
        previous = store.Read();
        applied = Record(root);
        if (previous is not null && previous != applied) throw new IOException("uninstall_registration_conflict");
        using var source = stub(); using var bytes = new MemoryStream(); source.CopyTo(bytes);
        stubHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes.ToArray()));
        if (File.Exists(path) && InstallEngine.Digest(path) != stubHash) throw new IOException("uninstaller_file_conflict");
        protocol.Apply(executable);
        if (!File.Exists(path))
        {
            using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            fileCreated = true; bytes.Position = 0; bytes.CopyTo(output);
        }
        if (store.Read() != previous) throw new IOException("uninstall_registration_changed");
        store.Write(applied);
    }
    public void Rollback()
    {
        if (applied is not null && store.Read() == applied)
        { if (previous is null) store.Remove(applied); else store.Write(previous); }
        var path = InstallEngine.Safe(root, "uninstall.exe");
        if (fileCreated && File.Exists(path) && InstallEngine.Digest(path) == stubHash) File.Delete(path);
        protocol.Rollback(); fileCreated = false;
    }
}
