using System.Diagnostics;
namespace Soda.AssetQuickRead.Setup;

internal static class AssetUninstall
{
    // Catalog EB52DA40... from the published F5BF3205... installer; source and payload verified together.
    internal static Catalog Published100Catalog()
    {
        using var stream = typeof(AssetUninstall).Assembly.GetManifestResourceStream("published-1.0.0-catalog.json")!;
        return System.Text.Json.JsonSerializer.Deserialize<Catalog>(stream, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }
    internal static Catalog Published101Catalog()
    {
        using var stream = typeof(AssetUninstall).Assembly.GetManifestResourceStream("published-1.0.1-catalog.json")!;
        return System.Text.Json.JsonSerializer.Deserialize<Catalog>(stream, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }
    internal static int Run(string[] args)
    {
        var relocated = args.Length == 3 && args[1] == "--wait-pid" && int.TryParse(args[2], out _);
        if (args.Length != 1 && !relocated) return 64;
        var root = InstallEngine.DefaultRoot;
        try
        {
            InstallEngine.NoLinks(root);
            if (!relocated)
            {
                if (MessageBox.Show("卸载资产快读？\n读取结果和账户数据会保留。", "Soda Terminal · 资产快读",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return 0;
                var current = Environment.ProcessPath ?? throw new IOException("uninstaller_identity_missing");
                var installed = Path.Combine(root, "uninstall.exe");
                if (Path.GetFullPath(current) != Path.GetFullPath(installed)) throw new IOException("uninstaller_location_mismatch");
                var temp = Path.Combine(Path.GetTempPath(), "SodaAssetUninstall-" + Guid.NewGuid().ToString("N"));
                InstallEngine.NoLinks(temp); Directory.CreateDirectory(temp);
                var copy = Path.Combine(temp, "uninstall.exe"); File.Copy(current, copy);
                var start = new ProcessStartInfo(copy) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
                start.ArgumentList.Add("--uninstall"); start.ArgumentList.Add("--wait-pid"); start.ArgumentList.Add(Environment.ProcessId.ToString());
                Process.Start(start); return 0;
            }
            try { using var parent = Process.GetProcessById(int.Parse(args[2])); if (!parent.WaitForExit(10000)) throw new IOException("uninstaller_parent_busy"); }
            catch (ArgumentException) { }
            Remove(root, InstallEngine.ReadCatalog(), new UserRegistration(), new RegistryUninstallStore(),
                InstallEngine.Digest(Environment.ProcessPath!));
            MessageBox.Show("已卸载，读取结果和账户数据已保留。", "Soda Terminal · 资产快读"); return 0;
        }
        catch { MessageBox.Show("卸载未完成，请关闭资产快读后重试。", "Soda Terminal · 资产快读", MessageBoxButtons.OK, MessageBoxIcon.Error); return 1; }
    }
    internal static void Remove(string root, Catalog catalog, UserRegistration protocol, IUninstallStore store, string stubHash)
    {
        InstallEngine.NoLinks(root);
        using var operation = new Mutex(false, "Local\\SodaTerminal.AssetQuickRead.Setup");
        bool acquired; try { acquired = operation.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) throw new IOException("another_installer_is_running");
        try
        {
            var version = Path.Combine(root, "versions", catalog.Version);
            foreach (var pin in catalog.Files)
            {
                var path = InstallEngine.Safe(version, pin.Path);
                if (File.Exists(path) && new FileInfo(path).Length == pin.Size && InstallEngine.Digest(path) == pin.Sha256) File.Delete(path);
            }
            var old = Path.Combine(root, "versions", "1.0.0");
            var oldCatalog = Published100Catalog();
            foreach (var pin in oldCatalog.Files)
            {
                var path = InstallEngine.Safe(old, pin.Path);
                if (File.Exists(path) && new FileInfo(path).Length == pin.Size && InstallEngine.Digest(path) == pin.Sha256) File.Delete(path);
            }
            protocol.RemoveMatching(Path.Combine(version, "Soda.AssetQuickRead.Bridge.exe"));
            protocol.RemoveMatching(Path.Combine(old, "Soda.AssetQuickRead.Bridge.exe"));
            var uninstall = InstallEngine.Safe(root, "uninstall.exe");
            if (File.Exists(uninstall) && InstallEngine.Digest(uninstall) == stubHash) File.Delete(uninstall);
            store.Remove(InstallRegistration.Record(Path.GetFullPath(root)));
            var directories = catalog.Files.SelectMany(pin => Parents(Path.Combine(version, pin.Path), root))
                .Concat(oldCatalog.Files.SelectMany(pin => Parents(Path.Combine(old, pin.Path), root)))
                .Append(root).Distinct().OrderByDescending(path => path.Length);
            foreach (var path in directories)
            { InstallEngine.NoLinks(path); if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path); }
        }
        finally { operation.ReleaseMutex(); }
    }
    private static IEnumerable<string> Parents(string file, string root)
    {
        for (var parent = Path.GetDirectoryName(file); parent is not null && parent.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase); parent = Path.GetDirectoryName(parent)) yield return parent;
    }
}
