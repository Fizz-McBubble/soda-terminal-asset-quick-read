using System.Security.Cryptography;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Soda.AssetQuickRead.Setup;

// Retain current + actual previous registered owned version.
// If no trusted previous registered version, retain highest fully verified prior known version as rollback.
// Same-version repair retains pre-existing trusted rollback.
// Never prune previous on failed install/cancel/registration rollback.
// Only exact known PublishedVersions and corresponding PublishedCatalog full file identities.
// Missing, modified, unknown-file, reparse, active or busy candidates are preserved.
// User jobs/results/data are outside versions/ and always preserved.
// No recursive delete fallback.
internal static class VersionRetention
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint sharing, IntPtr security,
        uint creation, uint flags, IntPtr template);
    [StructLayout(LayoutKind.Sequential)]
    private struct FileDisposition { [MarshalAs(UnmanagedType.U1)] internal bool Delete; }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass,
        ref FileDisposition information, uint size);
    private static FileStream OpenRetirementGuard(string path)
    {
        // DELETE access, read sharing only: changes and rename/replacement stay denied.
        var fullPath = Path.GetFullPath(path);
        var nativePath = fullPath.StartsWith(@"\\?\", StringComparison.Ordinal) ? fullPath
            : fullPath.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + fullPath[2..]
            : @"\\?\" + fullPath;
        var handle = CreateFileW(nativePath, 0x80010000, 1, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid) { var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new System.ComponentModel.Win32Exception(error); }
        try { return new FileStream(handle, FileAccess.Read); }
        catch { handle.Dispose(); throw; }
    }
    internal static void RetireOlderVersions(
        string root,
        string currentVersion,
        IRegistration? registration = null,
        IReadOnlyList<string>? publishedVersions = null,
        Func<string, Catalog>? publishedCatalog = null,
        Func<string, string>? digest = null,
        Func<string, Stream, string>? streamDigest = null,
        Action<string, string, int>? syntheticDiagnostic = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(currentVersion)) return;
            root = Path.GetFullPath(root);
            InstallEngine.NoLinks(root);
            var versionsRoot = Path.Combine(root, "versions");
            InstallEngine.NoLinks(versionsRoot);
            if (!Directory.Exists(versionsRoot)) return;

            var knownVersions = publishedVersions ?? InstallRegistration.PublishedVersions;
            var getCatalog = publishedCatalog ?? AssetUninstall.PublishedCatalog;
            var computeDigest = digest ?? InstallEngine.Digest;

            var registeredPrevious = registration?.PreviousRegisteredVersion;
            string? rollbackVersion = null;

            // Retain actual previous registered owned version if it exists and is a known version
            if (registeredPrevious is not null &&
                registeredPrevious != currentVersion &&
                knownVersions.Contains(registeredPrevious))
            {
                var prevDir = Path.Combine(versionsRoot, registeredPrevious);
                if (Directory.Exists(prevDir))
                {
                    rollbackVersion = registeredPrevious;
                }
            }

            // If no trusted previous registered version (fresh install, missing registry, or same-version repair),
            // search for the highest fully verified prior version to retain as rollback
            if (rollbackVersion is null)
            {
                foreach (var candidate in knownVersions.Where(v => v != currentVersion).Reverse())
                {
                    var candidateDir = Path.Combine(versionsRoot, candidate);
                    if (!Directory.Exists(candidateDir)) continue;
                    try
                    {
                        var cat = getCatalog(candidate);
                        if (IsFullyVerified(candidateDir, cat, computeDigest))
                        {
                            rollbackVersion = candidate;
                            break;
                        }
                    }
                    catch
                    {
                        // Catalog resolution failure preserves candidate
                    }
                }
            }

            var retained = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { currentVersion };
            if (rollbackVersion is not null) retained.Add(rollbackVersion);

            // Prune candidates: only exact known published versions not in the retained set
            foreach (var candidate in knownVersions)
            {
                if (retained.Contains(candidate)) continue;
                var candidateDir = Path.Combine(versionsRoot, candidate);
                if (!Directory.Exists(candidateDir)) continue;

                RetireCandidate(candidateDir, candidate, getCatalog, digest, streamDigest, syntheticDiagnostic);
            }
        }
        catch
        {
            // Cleanup failure must not turn successful installation into rollback.
        }
    }

    internal static bool IsFullyVerified(string versionDir, Catalog catalog, Func<string, string> digest)
    {
        try
        {
            InstallEngine.NoLinks(versionDir);
            if (!Directory.Exists(versionDir)) return false;

            var fullVersionDir = Path.GetFullPath(versionDir);
            var expectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var expectedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { fullVersionDir };

            foreach (var pin in catalog.Files)
            {
                var filePath = InstallEngine.Safe(versionDir, pin.Path);
                InstallEngine.NoLinks(filePath);
                if (!File.Exists(filePath)) return false;
                var info = new FileInfo(filePath);
                if (info.Length != pin.Size) return false;
                if (digest(filePath) != pin.Sha256) return false;

                var fullFilePath = Path.GetFullPath(filePath);
                expectedPaths.Add(fullFilePath);

                for (var parent = Path.GetDirectoryName(fullFilePath);
                     parent is not null && parent.StartsWith(fullVersionDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
                     parent = Path.GetDirectoryName(parent))
                {
                    expectedDirs.Add(parent);
                }
            }

            // Ensure no unknown files or directories exist in versionDir
            var stack = new Stack<string>();
            stack.Push(versionDir);
            while (stack.TryPop(out var dir))
            {
                InstallEngine.NoLinks(dir);
                foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
                {
                    InstallEngine.NoLinks(entry);
                    var fullEntry = Path.GetFullPath(entry);
                    if (Directory.Exists(entry))
                    {
                        if (!expectedDirs.Contains(fullEntry))
                        {
                            return false; // Unknown directory!
                        }
                        stack.Push(entry);
                    }
                    else
                    {
                        if (!expectedPaths.Contains(fullEntry))
                        {
                            return false; // Unknown file present -> not clean catalog identity!
                        }
                    }
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void RetireCandidate(
        string candidateDir,
        string version,
        Func<string, Catalog> getCatalog,
        Func<string, string>? digest,
        Func<string, Stream, string>? streamDigest,
        Action<string, string, int>? syntheticDiagnostic)
    {
        Catalog catalog;
        try
        {
            InstallEngine.NoLinks(candidateDir);
            catalog = getCatalog(version);
        }
        catch
        {
            return; // Unsupported catalog or linked path -> preserve!
        }

        var computeDigest = digest ?? InstallEngine.Digest;

        // Preliminary check: missing, modified, unknown-file, or reparse candidates are preserved
        if (!IsFullyVerified(candidateDir, catalog, computeDigest))
        {
            return;
        }

        var handles = new List<(string Path, CatalogFile Pin, FileStream Stream)>();
        var stage = "acquire_guards";
        try
        {
            // Acquire guarded handles for all expected pinned files with read-only sharing:
            // writers and rename/replacement stay denied until retirement completes.
            foreach (var pin in catalog.Files)
            {
                var filePath = InstallEngine.Safe(candidateDir, pin.Path);
                InstallEngine.NoLinks(filePath);
                if (!File.Exists(filePath)) return;

                var stream = OpenRetirementGuard(filePath);
                handles.Add((filePath, pin, stream));
            }

            stage = "verify_guarded_identity";
            // Under guard, verify all file sizes and SHA256 hashes directly from the open streams
            foreach (var (path, pin, stream) in handles)
            {
                if (stream.Length != pin.Size) return;
                stream.Position = 0;

                string hash;
                if (streamDigest is not null)
                {
                    hash = streamDigest(path, stream);
                }
                else if (digest is not null)
                {
                    hash = digest(path);
                }
                else
                {
                    hash = Convert.ToHexString(SHA256.HashData(stream));
                }

                if (!string.Equals(hash, pin.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    return; // Hash changed or tampered under guard -> preserve!
                }
            }

            stage = "verify_final_entries";
            // Recheck final filesystem identity and NoLinks before deleting pinned exact files
            InstallEngine.NoLinks(candidateDir);

            // Verify no unexpected unknown files were injected into candidateDir
            var expectedPaths = handles.Select(h => Path.GetFullPath(h.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var stack = new Stack<string>();
            stack.Push(candidateDir);
            while (stack.TryPop(out var dir))
            {
                InstallEngine.NoLinks(dir);
                foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
                {
                    InstallEngine.NoLinks(entry);
                    if (Directory.Exists(entry))
                    {
                        stack.Push(entry);
                    }
                    else
                    {
                        if (!expectedPaths.Contains(Path.GetFullPath(entry)))
                        {
                            return; // Unknown file appeared -> preserve!
                        }
                    }
                }
            }

            stage = "mark_guarded_files_for_deletion";
            // Delete pinned exact files while handles are held
            foreach (var (path, _, stream) in handles)
            {
                InstallEngine.NoLinks(path);
                var disposition = new FileDisposition { Delete = true };
                if (!SetFileInformationByHandle(stream.SafeFileHandle, 4, ref disposition,
                    (uint)Marshal.SizeOf<FileDisposition>())) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        catch (Exception error)
        {
            Report(stage, error);
            // Any access denial, sharing violation, link rejection or IO failure preserves candidate
            return;
        }
        finally
        {
            foreach (var h in handles)
            {
                try { h.Stream.Dispose(); } catch (Exception error) { Report("close_guards", error); }
            }
        }

        // Clean up empty subdirectories bottom-up; no recursive delete ever
        try
        {
            InstallEngine.NoLinks(candidateDir);
            var directories = catalog.Files
                .SelectMany(pin => Parents(InstallEngine.Safe(candidateDir, pin.Path), candidateDir))
                .Append(candidateDir)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(p => p.Length);

            foreach (var dir in directories)
            {
                InstallEngine.NoLinks(dir);
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                {
                    Directory.Delete(dir, recursive: false);
                }
            }
        }
        catch (Exception error)
        {
            Report("remove_empty_directories", error);
            // Directory cleanup failure preserves candidate; never unsafe fallback
        }
        void Report(string failedStage, Exception error)
        {
            // Only synthetic tests supply this callback. No paths or exception messages leave it.
            try { syntheticDiagnostic?.Invoke(version, failedStage,
                error is System.ComponentModel.Win32Exception native ? native.NativeErrorCode : error.HResult); }
            catch { }
        }
    }

    private static IEnumerable<string> Parents(string file, string root)
    {
        for (var parent = Path.GetDirectoryName(file);
             parent is not null && parent.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
             parent = Path.GetDirectoryName(parent))
        {
            yield return parent;
        }
    }
}
