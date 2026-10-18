using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
namespace Soda.AssetQuickRead.Setup;

internal sealed record CatalogFile(string Path, long Size, string Sha256, bool Download = false);
internal sealed record Catalog(string Version, string ArchiveSha256, CatalogFile[] Files);
internal interface IRegistration { void Preflight(string executable) { } void Apply(string executable); void Rollback(); string? PreviousRegisteredVersion => null; }

internal static class InstallEngine
{
    internal const string Version = "1.1.7";
    internal const string BridgeVersion = "1.0.11";
    internal static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SodaTerminal", "AssetQuickRead");
    internal static string Digest(string file) { using var stream = File.OpenRead(file); return Convert.ToHexString(SHA256.HashData(stream)); }
    internal static void NoLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("linked_path_rejected");
        }
    }
    internal static string Safe(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains('\\') || relative.Contains(':') || relative.Split('/').Any(x => x is "" or "." or "..") || Path.IsPathRooted(relative)) throw new IOException("catalog_path_rejected");
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("catalog_escape_rejected");
        NoLinks(path); return path;
    }
    internal static void Verify(string root, Catalog catalog)
    {
        NoLinks(root);
        var expected = catalog.Files.Select(x => x.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var actual = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.TryPop(out var directory))
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                NoLinks(entry);
                if (Directory.Exists(entry)) pending.Push(entry);
                else actual.Add(Path.GetRelativePath(root, entry).Replace('\\', '/'));
            }
        if (!actual.SetEquals(expected)) throw new IOException("installed_file_set_mismatch");
        foreach (var item in catalog.Files)
        {
            var file = Safe(root, item.Path);
            if (new FileInfo(file).Length != item.Size || Digest(file) != item.Sha256) throw new IOException("installed_hash_mismatch");
        }
    }
    // A repair may preserve arbitrary old contents, but must never move a link.
    // Walk without SearchOption.AllDirectories so links are rejected before descent.
    private static void InspectRepairTree(string root)
    {
        NoLinks(root);
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.TryPop(out var directory))
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            { NoLinks(entry); if (Directory.Exists(entry)) pending.Push(entry); }
    }
    internal static string RepairSwitchCode(IOException error) => error.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021)
        ? "installed_repair_files_in_use" : "installed_repair_switch_unavailable";
    internal static string Install(string archive, Catalog catalog, string root, IRegistration registration, CancellationToken cancellation, Action<int> progress, Func<CatalogFile, CancellationToken, Stream>? download = null, InstallerDiagnostics? diagnostics = null)
    {
        if (catalog.Version != Version || catalog.Files.Length == 0 || catalog.Files.Select(x => x.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != catalog.Files.Length) throw new IOException("catalog_identity_rejected");
        NoLinks(archive); NoLinks(root);
        registration.Preflight(Path.Combine(root, "versions", Version, "Soda.AssetQuickRead.Bridge.exe"));
        diagnostics?.Record("payload_verify");
        if (Digest(archive) != catalog.ArchiveSha256) throw new IOException("payload_hash_mismatch");
        var versions = Path.Combine(root, "versions"); var final = Path.Combine(versions, Version);
        var staging = Path.Combine(versions, ".install-" + Guid.NewGuid().ToString("N"));
        NoLinks(final);
        foreach (var pin in catalog.Files) Safe(final, pin.Path);
        if (File.Exists(final)) throw new IOException("installed_version_path_conflict");
        var repairing = Directory.Exists(final);
        if (repairing)
        {
            InspectRepairTree(final);
            var complete = false;
            try { Verify(final, catalog); complete = true; }
            catch (IOException error) when (error.Message is "installed_file_set_mismatch" or "installed_hash_mismatch") { }
            if (complete)
            {
                cancellation.ThrowIfCancellationRequested();
                try { registration.Apply(Path.Combine(final, "Soda.AssetQuickRead.Bridge.exe")); cancellation.ThrowIfCancellationRequested(); progress(100); }
                catch { registration.Rollback(); throw; }
                VersionRetention.RetireOlderVersions(root, Version, registration);
                return final;
            }
        }
        // This name is deliberately outside PublishedVersions/retention candidates.
        // Keep the entire original tree after successful repair, including unknown
        // and modified files. Never merge it into the verified executable payload.
        var backup = Path.Combine(versions, ".repair-" + Version + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging); var promoted = false; var committed = false; var backedUp = false;
        try
        {
            diagnostics?.Record("copy");
            using var zip = ZipFile.OpenRead(archive);
            if (zip.Entries.Count != catalog.Files.Count(x => !x.Download)) throw new IOException("payload_file_set_mismatch");
            long copied = 0, total = Math.Max(1, catalog.Files.Sum(x => x.Size));
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in zip.Entries)
            {
                cancellation.ThrowIfCancellationRequested();
                if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 || !seen.Add(entry.FullName)) throw new IOException("payload_link_or_duplicate");
                var pin = catalog.Files.SingleOrDefault(x => x.Path == entry.FullName && !x.Download);
                if (pin is null || pin.Size != entry.Length) throw new IOException("payload_catalog_mismatch");
                var path = Safe(staging, entry.FullName); Directory.CreateDirectory(Path.GetDirectoryName(path)!); NoLinks(path);
                using (var input = entry.Open()) using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
                {
                    var buffer = new byte[65536]; int count;
                    while ((count = input.Read(buffer)) != 0) { cancellation.ThrowIfCancellationRequested(); output.Write(buffer, 0, count); copied += count; progress((int)Math.Min(95, copied * 95 / total)); }
                }
                if (Digest(path) != pin.Sha256) throw new IOException("payload_file_hash_mismatch");
            }
            var protocolFiles = catalog.Files.Where(x => x.Download).ToArray();
            diagnostics?.Record("protocol");
            var progressGate = new object();
            using var fallbackScope = new ProtocolFallbackScope();
            try { Parallel.ForEach(Enumerable.Range(0, protocolFiles.Length), new ParallelOptions { CancellationToken = cancellation, MaxDegreeOfParallelism = 3 }, index =>
            {
                var pin = protocolFiles[index];
                cancellation.ThrowIfCancellationRequested();
                var path = Safe(staging, pin.Path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var reused = ReuseProtocol(root, pin);
                if (reused is not null) diagnostics?.Record("protocol", "verified", index, "verified_local");
                using var input = reused ?? (download is not null ? download(pin, cancellation)
                    : ProtocolDownload.Open(pin, cancellation, (source, code) => diagnostics?.Record("protocol", code, index, source), fallbackScope: fallbackScope));
                using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
                {
                    var buffer = new byte[65536]; int count; long received = 0;
                    while ((count = input.Read(buffer)) != 0)
                    {
                        cancellation.ThrowIfCancellationRequested(); received += count;
                        if (received > pin.Size) throw new IOException("protocol_download_size_mismatch");
                        output.Write(buffer, 0, count);
                        lock (progressGate) { copied += count; progress((int)Math.Min(95, copied * 95 / total)); }
                    }
                    if (received != pin.Size) throw new IOException("protocol_download_incomplete");
                }
                if (Digest(path) != pin.Sha256) throw new IOException("protocol_download_hash_mismatch");
                diagnostics?.Record("protocol", "verified", index);
            }); } catch (AggregateException error) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.Flatten().InnerExceptions[0]).Throw(); throw; }
            diagnostics?.Record("verify"); Verify(staging, catalog); cancellation.ThrowIfCancellationRequested();
            if (repairing)
            {
                InspectRepairTree(final); NoLinks(staging); NoLinks(backup);
                try { Directory.Move(final, backup); backedUp = true; }
                catch (IOException error) { throw new IOException(RepairSwitchCode(error), error); }
                cancellation.ThrowIfCancellationRequested();
            }
            NoLinks(final); NoLinks(staging);
            Directory.Move(staging, final); promoted = true;
            cancellation.ThrowIfCancellationRequested();
            diagnostics?.Record("register"); registration.Apply(Path.Combine(final, "Soda.AssetQuickRead.Bridge.exe"));
            cancellation.ThrowIfCancellationRequested();
            progress(100);
            // Registration and verification are now committed. Cancellation during best-effort
            // retirement cannot roll back the new version after older versions have been retired.
            committed = true;
            VersionRetention.RetireOlderVersions(root, Version, registration);
            return final;
        }
        catch
        {
            if (!committed)
            {
                // Restore payload even if registration rollback itself fails.
                try { registration.Rollback(); }
                finally
                {
                    if (backedUp)
                    {
                        try
                        {
                            NoLinks(final); InspectRepairTree(backup); NoLinks(staging);
                            // Only our promoted replacement can be moved out of the way.
                            if (promoted) Directory.Move(final, staging);
                            if (Directory.Exists(final) || File.Exists(final)) throw new IOException("installed_repair_restore_conflict");
                            Directory.Move(backup, final);
                            // Preserve a replacement changed by another writer; it is
                            // no longer an owned, fully verified operation directory.
                            if (Directory.Exists(staging) && VersionRetention.IsFullyVerified(staging, catalog, Digest)) RemoveOwned(staging, versions);
                        }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                        { throw new IOException("installed_repair_restore_conflict", error); }
                    }
                    else RemoveOwned(promoted ? final : staging, versions);
                }
            }
            throw;
        }
    }
    internal static Stream? ReuseProtocol(string root, CatalogFile pin)
    {
        // Only the three fixed protocol identities may be borrowed; old versions are read-only.
        ProtocolDownload.Url(pin);
        foreach (var version in InstallRegistration.PublishedVersions.Reverse())
        {
            try
            {
                var path = Safe(Path.Combine(root, "versions", version), pin.Path);
                if (!File.Exists(path) || new FileInfo(path).Length != pin.Size) continue;
                using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (Convert.ToHexString(SHA256.HashData(input)) != pin.Sha256) continue;
                input.Position = 0; var bytes = new MemoryStream(); input.CopyTo(bytes); bytes.Position = 0; return bytes;
            }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return null;
    }
    private static void RemoveOwned(string path, string parent)
    {
        if (!Path.GetFullPath(path).StartsWith(Path.GetFullPath(parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("rollback_outside_root");
        NoLinks(path);
        if (!Directory.Exists(path)) return;
        // Only the newly created operation directory; never jobs, other versions, or the product root.
        foreach (var child in Directory.EnumerateFileSystemEntries(path))
        { NoLinks(child); if (Directory.Exists(child)) RemoveOwned(child, parent); else File.Delete(child); }
        Directory.Delete(path);
    }
    internal static Catalog ReadCatalog()
    {
        using var stream = typeof(InstallEngine).Assembly.GetManifestResourceStream("catalog.json")!;
        return JsonSerializer.Deserialize<Catalog>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }
}
