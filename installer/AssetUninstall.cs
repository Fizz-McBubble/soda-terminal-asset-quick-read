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
    internal static Catalog Published102Catalog()
    {
        using var stream = typeof(AssetUninstall).Assembly.GetManifestResourceStream("published-1.0.2-catalog.json")!;
        return System.Text.Json.JsonSerializer.Deserialize<Catalog>(stream, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }
    internal static Catalog Published103Catalog()
    {
        using var stream = typeof(AssetUninstall).Assembly.GetManifestResourceStream("published-1.0.3-catalog.json")!;
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
        => RemoveCore(root, catalog, protocol, store, stubHash, PublishedCatalog, InstallEngine.Digest);

    internal static Catalog PublishedCatalog(string version) => version switch
    {
        "1.0.0" => Published100Catalog(),
        "1.0.1" => Published101Catalog(),
        "1.0.2" => Published102Catalog(),
        "1.0.3" or "1.0.4" or "1.0.5" or "1.0.6" => Published103Catalog() with { Version = version },
        _ => throw new IOException("uninstaller_version_unsupported")
    };

    // Tests exercise real file removal with small synthetic catalogs, never the
    // current user's production root or registry stores.
    internal static void RemoveForTests(string root, Catalog catalog, UserRegistration protocol, IUninstallStore store,
        string stubHash, Func<string, Catalog> publishedCatalog, Func<string, string>? digest = null)
    {
        var candidate = Path.GetFullPath(root);
        var production = Path.GetFullPath(InstallEngine.DefaultRoot);
        if (candidate.Equals(production, StringComparison.OrdinalIgnoreCase) ||
            candidate.StartsWith(production + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            store is RegistryUninstallStore) throw new InvalidOperationException("isolated_uninstall_test_required");
        RemoveCore(candidate, catalog, protocol, store, stubHash, publishedCatalog, digest ?? InstallEngine.Digest);
    }

    private static void RemoveCore(string root, Catalog catalog, UserRegistration protocol, IUninstallStore store,
        string stubHash, Func<string, Catalog> publishedCatalog, Func<string, string> digest)
    {
        root = Path.GetFullPath(root);
        InstallEngine.NoLinks(root);
        using var operation = new Mutex(false, "Local\\SodaTerminal.AssetQuickRead.Setup");
        bool acquired; try { acquired = operation.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) throw new IOException("another_installer_is_running");
        try
        {
            if (catalog.Version != InstallEngine.Version) throw new IOException("uninstaller_version_unsupported");
            var catalogs = InstallRegistration.PublishedVersions.Where(version => version != catalog.Version)
                .Select(publishedCatalog).Append(catalog).ToArray();
            var record = store.Read();
            var expected = InstallRegistration.Record(root);
            if (record is not null && (record.OwnedShape != true ||
                !catalogs.Any(candidate => candidate.Version == record.Version) || record != expected with { Version = record.Version }))
                throw new IOException("uninstall_registration_conflict");
            var protocolSnapshot = protocol.PreflightRemoval(root, catalogs.Select(candidate => candidate.Version).ToArray());
            var protocolRemovalStarted = false;
            var uninstall = InstallEngine.Safe(root, "uninstall.exe");
            var observedStubHash = File.Exists(uninstall) ? digest(uninstall) : null;
            if (observedStubHash is not null && observedStubHash != stubHash &&
                !(record is null ? InstallRegistration.PublishedVersions.Any(version => observedStubHash == InstallRegistration.OldPin(version))
                    : observedStubHash == InstallRegistration.OldPin(record.Version)))
                throw new IOException("uninstaller_file_conflict");

            var files = catalogs.SelectMany(candidate => candidate.Files.Select(pin =>
                (Path: InstallEngine.Safe(Path.Combine(root, "versions", candidate.Version), pin.Path), Pin: pin))).ToArray();
            bool Matches((string Path, CatalogFile Pin) file) => File.Exists(file.Path) &&
                new FileInfo(file.Path).Length == file.Pin.Size && digest(file.Path) == file.Pin.Sha256;
            var ownedFiles = files.Where(Matches).ToArray();
            // Detect a busy tool before removing any component or entry. Unknown
            // and modified files remain outside this removal list.
            var handles = new List<FileStream>();
            try
            {
                foreach (var file in ownedFiles) handles.Add(new FileStream(file.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read));
                if (observedStubHash is not null) handles.Add(new FileStream(uninstall, FileMode.Open, FileAccess.ReadWrite, FileShare.Read));
                AssertRegistrationUnchanged();
            }
            finally { foreach (var handle in handles) handle.Dispose(); }
            // Keep the stub protected from writes and replacement while managed
            // components are removed; do not rehash a large bundled EXE per file.
            using var stubGuard = observedStubHash is null ? null : new FileStream(uninstall, FileMode.Open, FileAccess.Read, FileShare.Read);
            AssertUnchanged();
            foreach (var file in ownedFiles)
            {
                AssertRegistrationUnchanged();
                InstallEngine.NoLinks(file.Path);
                if (Matches(file)) File.Delete(file.Path);
            }
            AssertUnchanged();
            protocolRemovalStarted = true;
            foreach (var candidate in catalogs)
                protocol.RemoveMatching(Path.Combine(root, "versions", candidate.Version, "Soda.AssetQuickRead.Bridge.exe"));
            protocol.AssertRemovalUnchanged(null);
            AssertUnchanged();
            if (record is not null && !store.Remove(record)) throw new IOException("uninstall_registration_changed");
            if (store.Read() is not null) throw new IOException("uninstall_registration_changed");
            stubGuard?.Dispose();
            if (observedStubHash is not null)
            {
                InstallEngine.NoLinks(uninstall);
                if (!File.Exists(uninstall) || digest(uninstall) != observedStubHash) throw new IOException("uninstaller_file_changed");
                File.Delete(uninstall);
            }
            var directories = files.SelectMany(file => Parents(file.Path, root))
                .Append(root).Distinct().OrderByDescending(path => path.Length);
            foreach (var path in directories)
            { InstallEngine.NoLinks(path); if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path); }
            void AssertUnchanged()
            {
                AssertRegistrationUnchanged();
                if ((File.Exists(uninstall) ? digest(uninstall) : null) != observedStubHash) throw new IOException("uninstaller_file_changed");
            }
            void AssertRegistrationUnchanged()
            {
                if (store.Read() != record) throw new IOException("uninstall_registration_changed");
                if (!protocolRemovalStarted) protocol.AssertRemovalUnchanged(protocolSnapshot);
            }
        }
        finally { operation.ReleaseMutex(); }
    }
    private static IEnumerable<string> Parents(string file, string root)
    {
        for (var parent = Path.GetDirectoryName(file); parent is not null && parent.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase); parent = Path.GetDirectoryName(parent)) yield return parent;
    }
}
