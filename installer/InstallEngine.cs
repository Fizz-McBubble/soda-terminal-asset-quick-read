using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
namespace Soda.AssetQuickRead.Setup;

internal sealed record CatalogFile(string Path, long Size, string Sha256, bool Download = false);
internal sealed record Catalog(string Version, string ArchiveSha256, CatalogFile[] Files);
internal interface IRegistration { void Apply(string executable); void Rollback(); }

internal static class InstallEngine
{
    internal const string Version = "1.0.1";
    internal static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SodaTerminal", "AssetQuickRead");
    internal static string Digest(string file) { using var stream = File.OpenRead(file); return Convert.ToHexString(SHA256.HashData(stream)); }
    internal static void NoLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("linked_path_rejected");
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
    internal static string Install(string archive, Catalog catalog, string root, IRegistration registration, CancellationToken cancellation, Action<int> progress, Func<CatalogFile, CancellationToken, Stream>? download = null)
    {
        if (catalog.Version != Version || catalog.Files.Length == 0 || catalog.Files.Select(x => x.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != catalog.Files.Length) throw new IOException("catalog_identity_rejected");
        NoLinks(archive); NoLinks(root);
        if (Digest(archive) != catalog.ArchiveSha256) throw new IOException("payload_hash_mismatch");
        var versions = Path.Combine(root, "versions"); var final = Path.Combine(versions, Version);
        var staging = Path.Combine(versions, ".install-" + Guid.NewGuid().ToString("N"));
        NoLinks(final);
        if (Directory.Exists(final))
        {
            Verify(final, catalog); cancellation.ThrowIfCancellationRequested();
            try { registration.Apply(Path.Combine(final, "Soda.AssetQuickRead.Bridge.exe")); }
            catch { registration.Rollback(); throw; }
            progress(100); return final;
        }
        Directory.CreateDirectory(staging); var promoted = false;
        try
        {
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
            foreach (var pin in catalog.Files.Where(x => x.Download))
            {
                cancellation.ThrowIfCancellationRequested();
                var path = Safe(staging, pin.Path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var input = (download ?? ProtocolDownload.Open)(pin, cancellation);
                using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
                {
                    var buffer = new byte[65536]; int count; long received = 0;
                    while ((count = input.Read(buffer)) != 0)
                    {
                        cancellation.ThrowIfCancellationRequested(); received += count;
                        if (received > pin.Size) throw new IOException("protocol_download_size_mismatch");
                        output.Write(buffer, 0, count); copied += count; progress((int)Math.Min(95, copied * 95 / total));
                    }
                    if (received != pin.Size) throw new IOException("protocol_download_incomplete");
                }
                if (Digest(path) != pin.Sha256) throw new IOException("protocol_download_hash_mismatch");
            }
            Verify(staging, catalog); cancellation.ThrowIfCancellationRequested();
            Directory.Move(staging, final); promoted = true;
            cancellation.ThrowIfCancellationRequested();
            registration.Apply(Path.Combine(final, "Soda.AssetQuickRead.Bridge.exe"));
            progress(100); return final;
        }
        catch
        {
            registration.Rollback();
            RemoveOwned(promoted ? final : staging, versions);
            throw;
        }
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
