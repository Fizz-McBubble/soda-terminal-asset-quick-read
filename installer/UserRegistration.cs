using Microsoft.Win32;
using System.Text;
namespace Soda.AssetQuickRead.Setup;

internal sealed record ProtocolRegistration(string? Description, string? UrlProtocol, string? Command, bool OwnedShape);
internal interface IProtocolRegistrationStore
{
    ProtocolRegistration? Read();
    void Create(string command);
    bool Replace(ProtocolRegistration expected, string command);
    void RemoveCreated(string command);
}

internal sealed class UserRegistration : IRegistration
{
    internal const string Description = "URL:Soda Terminal Asset Quick Read";
    private readonly IProtocolRegistrationStore store;
    private readonly string shortcut;
    private readonly Func<string, string> digest;
    internal const string ShortcutBody = "[InternetShortcut]\r\nURL=soda-asset-quick-read://launch\r\n";
    private bool createdKey, createdShortcut;
    private string? command;
    private ProtocolRegistration? upgradedFrom;
    // Identities from the actually published 1.0.0 installer, SHA256 F5BF32058F813C8677A4B1DFB54A836EDED219483AFE5589032D4B0DDD544B5E.
    internal static readonly IReadOnlyDictionary<string, string> Published100Pins = new Dictionary<string, string>
    {
        ["Soda.AssetQuickRead.Bridge.exe"] = "DC075C8AEA5497FD9ABBAA6B4E8B6C7D46C9462BCFF2FD0BD3C49A848B0C824F",
        ["Soda.AssetQuickRead.Bridge.dll"] = "9EAF856FA997732DE0C7E8CF3320245F934BD31F5A199D5BEEDE23755E7A38FC",
        ["Soda.AssetQuickRead.Bridge.deps.json"] = "2FAD8B3B4C854B0262D6C4CBFE660C36D48BCD273E32F673E02A638AF1E0E548",
        ["Soda.AssetQuickRead.Bridge.runtimeconfig.json"] = "072863399556D733FFB5A244403B7A5B53DC2D99EE31216C1511F3AEDA3ABFDC",
        ["runtime/soda-asset-capture.exe"] = "94E9D6ED79B4886D4E2A0B55A20CC79A2821228B158EE0BA6972526AF10E047B",
    };
    internal UserRegistration() : this(new RegistryProtocolRegistrationStore(), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Soda Terminal 资产快读.url")) { }
    internal UserRegistration(IProtocolRegistrationStore store, string shortcut, Func<string, string>? digest = null)
    { this.store = store; this.shortcut = shortcut; this.digest = digest ?? InstallEngine.Digest; }
    internal static string Command(string executable) => "\"" + executable + "\" --protocol \"%1\"";
    internal string? InstalledVersion(string root)
    {
        var existing = store.Read();
        if (existing?.Description != Description || existing.UrlProtocol != "" || !existing.OwnedShape) return null;
        foreach (var version in new[] { InstallEngine.Version, "1.0.1", "1.0.0" })
            if (existing.Command == Command(Path.Combine(root, "versions", version, "Soda.AssetQuickRead.Bridge.exe"))) return version;
        return null;
    }
    internal void RemoveMatching(string executable)
    {
        var existing = store.Read();
        if (existing == new ProtocolRegistration(Description, "", Command(executable), true)) store.RemoveCreated(Command(executable));
        InstallEngine.NoLinks(shortcut);
        if (File.Exists(shortcut) && File.ReadAllText(shortcut) == ShortcutBody) File.Delete(shortcut);
    }
    private bool Published100Upgrade(string executable, ProtocolRegistration existing)
    {
        if (existing.Description != Description || existing.UrlProtocol != "" || !existing.OwnedShape) return false;
        var version = Path.GetDirectoryName(Path.GetFullPath(executable));
        var versions = version is null ? null : Path.GetDirectoryName(version);
        if (version is null || versions is null || Path.GetFileName(executable) != "Soda.AssetQuickRead.Bridge.exe" ||
            Path.GetFileName(version) != InstallEngine.Version || Path.GetFileName(versions) != "versions") return false;
        var oldVersion = existing.Command == Command(Path.Combine(versions, "1.0.1", "Soda.AssetQuickRead.Bridge.exe")) ? "1.0.1" : "1.0.0";
        var previous = Path.Combine(versions, oldVersion);
        if (existing.Command != Command(Path.Combine(previous, "Soda.AssetQuickRead.Bridge.exe"))) return false;
        try
        {
            var pins = oldVersion == "1.0.0" ? Published100Pins : AssetUninstall.Published101Catalog().Files
                .Where(file => Published100Pins.ContainsKey(file.Path)).ToDictionary(file => file.Path, file => file.Sha256);
            foreach (var pin in pins)
            {
                var file = InstallEngine.Safe(previous, pin.Key);
                if (!File.Exists(file) || digest(file) != pin.Value) return false;
            }
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
    public void Apply(string executable)
    {
        InstallEngine.NoLinks(executable); InstallEngine.NoLinks(shortcut);
        command = Command(executable);
        var existing = store.Read();
        var upgrading = false;
        if (existing is not null)
        {
            var identical = existing.Description == Description && existing.UrlProtocol == "" && existing.Command == command && existing.OwnedShape;
            upgrading = !identical && Published100Upgrade(executable, existing);
            if (!identical && !upgrading) throw new IOException("protocol_registration_conflict");
        }
        if (File.Exists(shortcut) && File.ReadAllText(shortcut) != ShortcutBody) throw new IOException("start_menu_shortcut_conflict");
        if (existing is null)
        {
            createdKey = true; store.Create(command);
        }
        else if (upgrading)
        {
            // Record before writing: even a write followed by a later failure must restore the old command.
            upgradedFrom = existing;
            if (!store.Replace(existing, command)) { upgradedFrom = null; throw new IOException("protocol_registration_changed"); }
        }
        else if (store.Read() != existing) throw new IOException("protocol_registration_changed");
        if (!File.Exists(shortcut))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(shortcut)!);
            using var stream = new FileStream(shortcut, FileMode.CreateNew, FileAccess.Write);
            createdShortcut = true; var bytes = Encoding.UTF8.GetBytes(ShortcutBody); stream.Write(bytes);
        }
    }
    public void Rollback()
    {
        if (createdShortcut && File.Exists(shortcut) && File.ReadAllText(shortcut) == ShortcutBody) File.Delete(shortcut);
        if (upgradedFrom is not null) store.Replace(upgradedFrom with { Command = command }, upgradedFrom.Command!);
        if (createdKey) store.RemoveCreated(command!);
        upgradedFrom = null;
        createdKey = createdShortcut = false;
    }
}

internal sealed class RegistryProtocolRegistrationStore : IProtocolRegistrationStore
{
    private const string Key = @"Software\Classes\soda-asset-quick-read";
    public ProtocolRegistration? Read()
    {
        using var existing = Registry.CurrentUser.OpenSubKey(Key);
        if (existing is null) return null;
        using var open = existing.OpenSubKey(@"shell\open\command");
        var command = open?.GetValue("") as string;
        return new(existing.GetValue("") as string, existing.GetValue("URL Protocol") as string, command, command is not null && OwnedPartial(existing, "", command));
    }
    public void Create(string command)
    {
        if (Read() is not null) throw new IOException("protocol_registration_changed");
        using var protocol = Registry.CurrentUser.CreateSubKey(Key);
        protocol.SetValue("", UserRegistration.Description); protocol.SetValue("URL Protocol", "");
        using var open = protocol.CreateSubKey(@"shell\open\command"); open.SetValue("", command);
    }
    public bool Replace(ProtocolRegistration expected, string command)
    {
        using var root = Registry.CurrentUser.OpenSubKey(Key, true);
        if (root is null || Read() != expected) return false;
        using var open = root.OpenSubKey(@"shell\open\command", true);
        if (open is null) return false;
        open.SetValue("", command); return true;
    }
    public void RemoveCreated(string command)
    {
        using var check = Registry.CurrentUser.OpenSubKey(Key);
        var owned = check is not null && OwnedPartial(check, "", command);
        check?.Dispose(); if (owned) Registry.CurrentUser.DeleteSubKeyTree(Key, false);
    }
    private static bool OwnedPartial(RegistryKey key, string path, string expectedCommand)
    {
        var allowedChild = path switch { "" => "shell", "shell" => "open", "shell/open" => "command", _ => null };
        foreach (var name in key.GetValueNames())
        {
            var expected = (path, name) switch
            {
                ("", "") => UserRegistration.Description,
                ("", "URL Protocol") => "",
                ("shell/open/command", "") => expectedCommand,
                _ => null
            };
            if (expected is null || key.GetValue(name) as string != expected) return false;
        }
        foreach (var name in key.GetSubKeyNames())
        {
            if (name != allowedChild) return false;
            using var child = key.OpenSubKey(name);
            if (child is null || !OwnedPartial(child, path.Length == 0 ? name : path + "/" + name, expectedCommand)) return false;
        }
        return true;
    }
}
