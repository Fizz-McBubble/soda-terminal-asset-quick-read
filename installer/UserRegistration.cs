using Microsoft.Win32;
using System.Text;
namespace Soda.AssetQuickRead.Setup;

internal sealed class UserRegistration : IRegistration
{
    private const string Key = @"Software\Classes\soda-asset-quick-read";
    private static readonly string Shortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Soda Terminal 资产快读.url");
    private const string ShortcutBody = "[InternetShortcut]\r\nURL=soda-asset-quick-read://launch\r\n";
    private bool createdKey, createdShortcut;
    private string? command;
    internal static string Command(string executable) => "\"" + executable + "\" --protocol \"%1\"";
    public void Apply(string executable)
    {
        InstallEngine.NoLinks(executable); InstallEngine.NoLinks(Shortcut);
        command = Command(executable);
        using (var existing = Registry.CurrentUser.OpenSubKey(Key))
        {
            if (existing is not null)
            {
                using var existingCommand = existing.OpenSubKey(@"shell\open\command");
                if (existingCommand?.GetValue("") as string != command || existing.GetValue("URL Protocol") as string != "") throw new IOException("protocol_registration_conflict");
            }
        }
        if (File.Exists(Shortcut) && File.ReadAllText(Shortcut) != ShortcutBody) throw new IOException("start_menu_shortcut_conflict");
        using (var existing = Registry.CurrentUser.OpenSubKey(Key))
        {
            if (existing is null)
            {
                createdKey = true;
                using var protocol = Registry.CurrentUser.CreateSubKey(Key);
                protocol.SetValue("", "URL:Soda Terminal Asset Quick Read"); protocol.SetValue("URL Protocol", "");
                using var open = protocol.CreateSubKey(@"shell\open\command"); open.SetValue("", command);
            }
        }
        if (!File.Exists(Shortcut))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Shortcut)!);
            using var stream = new FileStream(Shortcut, FileMode.CreateNew, FileAccess.Write);
            createdShortcut = true; var bytes = Encoding.UTF8.GetBytes(ShortcutBody); stream.Write(bytes);
        }
    }
    public void Rollback()
    {
        if (createdShortcut && File.Exists(Shortcut) && File.ReadAllText(Shortcut) == ShortcutBody) File.Delete(Shortcut);
        if (createdKey)
        {
            using var check = Registry.CurrentUser.OpenSubKey(Key);
            var owned = check is not null && OwnedPartial(check, "", command!);
            check?.Dispose(); if (owned) Registry.CurrentUser.DeleteSubKeyTree(Key, false);
        }
        createdKey = createdShortcut = false;
    }
    private static bool OwnedPartial(RegistryKey key, string path, string expectedCommand)
    {
        var allowedChild = path switch { "" => "shell", "shell" => "open", "shell/open" => "command", _ => null };
        foreach (var name in key.GetValueNames())
        {
            var expected = (path, name) switch
            {
                ("", "") => "URL:Soda Terminal Asset Quick Read",
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
