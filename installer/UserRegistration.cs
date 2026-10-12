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
        foreach (var version in InstallRegistration.PublishedVersions.Prepend(InstallEngine.Version).Distinct())
            if (existing.Command == Command(Path.Combine(root, "versions", version, "Soda.AssetQuickRead.Bridge.exe"))) return version;
        return null;
    }
    internal bool TrustedInstalledVersion(string root, string version) => InstalledVersion(root) == version &&
        store.Read() is { } existing && Published100Upgrade(Path.Combine(root, "versions", InstallEngine.Version, "Soda.AssetQuickRead.Bridge.exe"), existing);
    internal bool TrustedPublished106Payload(string root)
    {
        try
        {
            if (!TrustedInstalledVersion(root, "1.0.6")) return false;
            var previous = Path.Combine(root, "versions", "1.0.6");
            var catalog = AssetUninstall.Published103Catalog();
            InstallEngine.NoLinks(previous);
            var actual = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pending = new Stack<string>(); pending.Push(previous);
            while (pending.TryPop(out var directory))
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    InstallEngine.NoLinks(entry);
                    if (Directory.Exists(entry)) pending.Push(entry);
                    else actual.Add(Path.GetRelativePath(previous, entry).Replace('\\', '/'));
                }
            if (!actual.SetEquals(catalog.Files.Select(pin => pin.Path))) return false;
            return catalog.Files.All(pin => digest(InstallEngine.Safe(previous, pin.Path)) == pin.Sha256);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
    internal bool RecoverableInstalledVersion(string root, string version)
    {
        // A missing protocol is repairable; an existing foreign protocol is not.
        // The historical uninstaller AND every pinned launch component must match.
        var existing = store.Read();
        var expected = new ProtocolRegistration(Description, "", Command(Path.Combine(root, "versions", version, "Soda.AssetQuickRead.Bridge.exe")), true);
        return (existing is null || existing == expected) && PublishedRuntimeMatches(Path.Combine(root, "versions", version), version);
    }
    internal ProtocolRegistration? PreflightRemoval(string root, IReadOnlyCollection<string> versions)
    {
        var existing = store.Read();
        if (existing is not null && !versions.Any(version => existing == new ProtocolRegistration(Description, "",
            Command(Path.Combine(root, "versions", version, "Soda.AssetQuickRead.Bridge.exe")), true)))
            throw new IOException("protocol_registration_conflict");
        AssertRemovalUnchanged(existing);
        return existing;
    }
    internal void AssertRemovalUnchanged(ProtocolRegistration? expected)
    {
        if (store.Read() != expected) throw new IOException("protocol_registration_changed");
        InstallEngine.NoLinks(shortcut);
        if (File.Exists(shortcut) && File.ReadAllText(shortcut) != ShortcutBody)
            throw new IOException("start_menu_shortcut_conflict");
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
        var oldVersion = InstallRegistration.PublishedVersions.FirstOrDefault(candidate =>
            existing.Command == Command(Path.Combine(versions, candidate, "Soda.AssetQuickRead.Bridge.exe")));
        if (oldVersion is null) return false;
        var previous = Path.Combine(versions, oldVersion);
        if (existing.Command != Command(Path.Combine(previous, "Soda.AssetQuickRead.Bridge.exe"))) return false;
        return PublishedRuntimeMatches(previous, oldVersion);
    }
    private bool PublishedRuntimeMatches(string previous, string oldVersion)
    {
        try
        {
            if (!InstallRegistration.PublishedVersions.Contains(oldVersion)) return false;
            // Installers 1.0.4-1.0.7 reused the immutable 1.0.3 runtime payload.
            var pins = oldVersion == "1.0.0" ? Published100Pins : (oldVersion is "1.0.3" or "1.0.4" or "1.0.5" or "1.0.6" or "1.0.7" ? AssetUninstall.Published103Catalog() : oldVersion == "1.0.2" ? AssetUninstall.Published102Catalog() : AssetUninstall.Published101Catalog()).Files
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
    public void Preflight(string executable)
    {
        InstallEngine.NoLinks(executable); InstallEngine.NoLinks(shortcut);
        var existing = store.Read();
        if (existing is not null && existing != new ProtocolRegistration(Description, "", Command(executable), true) && !Published100Upgrade(executable, existing)) throw new IOException("protocol_registration_conflict");
        if (File.Exists(shortcut) && File.ReadAllText(shortcut) != ShortcutBody) throw new IOException("start_menu_shortcut_conflict");
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
        if (store.Read() != new ProtocolRegistration(Description, "", command, true)) throw new IOException("protocol_registration_verify_failed");
        if (!File.Exists(shortcut))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(shortcut)!);
            using var stream = new FileStream(shortcut, FileMode.CreateNew, FileAccess.Write);
            createdShortcut = true; var bytes = Encoding.UTF8.GetBytes(ShortcutBody); stream.Write(bytes);
        }
    }
    internal void VerifyApplied(string executable)
    {
        if (store.Read() != new ProtocolRegistration(Description, "", Command(executable), true)) throw new IOException("protocol_registration_verify_failed");
        InstallEngine.NoLinks(shortcut);
        if (!File.Exists(shortcut) || File.ReadAllText(shortcut) != ShortcutBody) throw new IOException("start_menu_verify_failed");
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
