using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace Soda.AssetQuickRead.Setup;

internal static class ProtocolDownloadTests
{
    internal static int RunStandalone(string archivePath, string testRoot, string evidencePath)
    {
        var tempDir = Path.Combine(Path.GetFullPath(testRoot), "soda-protocol-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempDir);
            if (new FileInfo(archivePath).Length != ProtocolDownload.ArchiveSize)
                throw new IOException("protocol_download_size_mismatch");
            var archiveBytes = File.ReadAllBytes(archivePath);
            var manifestPin = new CatalogFile("runtime/assets/manifest.json", 24, "38006E92317CF8EBA3E578D58E5B0FC9AECFEC117D20DC375AA9BED2B073302E", true);
            using var manifest = ProtocolFallbackScope.ExtractAndVerifyEntry(archiveBytes, manifestPin, CancellationToken.None);
            var passed = Run(tempDir, manifest.ToArray(), archiveBytes);
            var report = new
            {
                suite = "asset-quick-read-protocol-download",
                passed,
                syntheticRoot = tempDir,
                archiveVerified = true,
                exitCode = 0
            };
            var fullEvidencePath = Path.GetFullPath(evidencePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullEvidencePath)!);
            File.WriteAllText(fullEvidencePath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex)
        {
            var fullEvidencePath = Path.GetFullPath(evidencePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullEvidencePath)!);
            File.WriteAllText(fullEvidencePath, JsonSerializer.Serialize(new { suite = "asset-quick-read-protocol-download", error = ex.ToString(), exitCode = 1 }, new JsonSerializerOptions { WriteIndented = true }));
            return 1;
        }
        // Retain this explicitly located synthetic input/evidence for owner review.
        // Retirement uses the existing approved cleanup entry, never a test-run deletion.
    }

    internal static int Run(string root, byte[] bytes, byte[]? archiveBytes = null)
    {
        Directory.CreateDirectory(root);
        var pin = new CatalogFile("runtime/assets/manifest.json", bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)), true);
        ProtocolDownload.Url(pin); // Tests require the actual pinned bytes, never a relaxed allowlist.
        int passed = 0, requests = 0;
        void Check(bool value, string name) { if (!value) throw new Exception(name); Interlocked.Increment(ref passed); }
        var budget = new DownloadBudget(TimeSpan.FromMilliseconds(60), TimeSpan.FromMilliseconds(60), TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(1));
        HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) => new(new Fake(respond));
        HttpResponseMessage Good() => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        Stream Open(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond, CancellationToken token = default, Action<string, string>? status = null, ProtocolFallbackScope? fallbackScope = null)
            => ProtocolDownload.Open(pin, token, status, () => Client(respond), budget, fallbackScope: fallbackScope);

        void Refuses(Func<Stream> operation, string code, string name, int count = 6)
        {
            requests = 0;
            try { using var ignored = operation(); throw new Exception(name + "_did_not_throw"); }
            catch (IOException e) { if (!(e.Message == code && requests == count)) throw new Exception($"{name}: msg expected '{code}', got '{e.Message}'; reqs expected {count}, got {requests}"); passed++; }
        }

        var seen = new List<string>();
        using (var stream = Open((request, _) =>
        {
            requests++;
            if (requests == 1) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
            Check(request.Headers.Accept.ToString() == "application/vnd.github.raw+json", "official_contents_accept");
            Check(request.RequestUri!.Query == "?ref=4853f07c8fb5322c12b9b580d6364b577c2ac96a", "fixed_commit");
            return Task.FromResult(Good());
        }, status: (source, code) => seen.Add(source + ":" + code)))
            Check(stream.Length == bytes.Length && requests == 2, "bounded_fallback_success");
        Check(seen.Contains("github_raw:protocol_download_failed_http_403") && seen.Contains("github_contents:verified"), "source_stage_codes");

        requests = 0;
        using (var stream = Open(async (_, token) => { requests++; if (requests == 1) await Task.Delay(Timeout.Infinite, token); return Good(); }))
            Check(requests == 2 && stream.Length == bytes.Length, "header_timeout_then_fallback");

        requests = 0;
        using (var stream = Open((_, _) => { requests++; if (requests < 3) throw new HttpRequestException("private secret"); return Task.FromResult(Good()); }))
            Check(requests == 3, "transient_connection_retry_round");

        // When all sources fail, raw (2) + contents (2) + archive (2) = 6 requests
        Refuses(() => Open((req, _) =>
        {
            requests++;
            var payload = new byte[req.RequestUri?.AbsoluteUri == ProtocolDownload.ArchiveUrl ? ProtocolDownload.ArchiveSize : bytes.Length];
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
        }), "protocol_download_hash_mismatch", "corrupt_both_sources_rejected");

        Refuses(() => Open((req, _) =>
        {
            requests++;
            var payload = new byte[req.RequestUri?.AbsoluteUri == ProtocolDownload.ArchiveUrl ? ProtocolDownload.ArchiveSize + 1 : bytes.Length + 1];
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
        }), "protocol_download_size_mismatch", "declared_size_rejected");

        Refuses(() => Open((req, _) =>
        {
            requests++;
            var s = req.RequestUri?.AbsoluteUri == ProtocolDownload.ArchiveUrl ? new Slow(new byte[ProtocolDownload.ArchiveSize], 0, truncate: true) : new Slow(bytes, 0, truncate: true);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(s) });
        }), "protocol_download_incomplete", "undeclared_incomplete_rejected");

        Refuses(() => Open((req, _) =>
        {
            requests++;
            var s = req.RequestUri?.AbsoluteUri == ProtocolDownload.ArchiveUrl ? new Slow(new byte[ProtocolDownload.ArchiveSize + 1], 0) : new Slow(new byte[bytes.Length + 1], 0);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(s) });
        }), "protocol_download_size_mismatch", "undeclared_oversize_rejected");

        Refuses(() => Open((_, _) => { requests++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://example.org/untrusted") } }); }), "protocol_download_failed_http_302", "redirect_rejected");
        Refuses(() => Open((_, _) => { requests++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new Slow(bytes, Timeout.Infinite)) }); }), "protocol_download_timeout", "body_stall_bounded");
        Refuses(() => Open((_, _) => { requests++; throw new IOException("private-user-path secret=abc"); }), "protocol_download_connection_failed", "raw_errors_redacted");

        requests = 0;
        using (var stream = Open((_, _) => { requests++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new Slow(bytes, 20)) }); }))
            Check(stream.Length == bytes.Length && requests == 1, "healthy_body_exceeds_header_budget");

        Refuses(() => ProtocolDownload.Open(pin, CancellationToken.None, null, () => Client((_, _) =>
        {
            requests++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new Slow(bytes, 20)) });
        }), budget with { Attempt = TimeSpan.FromMilliseconds(80) }), "protocol_download_timeout", "progressing_body_total_attempt_bounded");

        requests = 0; using var retryCancel = new CancellationTokenSource();
        try
        {
            using var ignored = Open((_, _) =>
            {
                requests++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
            }, retryCancel.Token, (_, code) => { if (requests == 2 && code == "protocol_download_failed_http_403") retryCancel.Cancel(); });
            throw new Exception("cancel_retry");
        }
        catch (OperationCanceledException) { Check(requests == 2, "cancel_before_retry_round"); }

        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { using var ignored = Open((_, _) => throw new Exception("must_not_request"), cancelled.Token); throw new Exception("cancel_before_request"); }
        catch (OperationCanceledException) { passed++; }

        requests = 0; using var active = new CancellationTokenSource();
        try
        {
            using var ignored = Open((_, _) => { requests++; active.CancelAfter(10); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new Slow(bytes, Timeout.Infinite)) }); }, active.Token);
            throw new Exception("cancel_body");
        }
        catch (OperationCanceledException) { Check(requests == 1, "body_cancellation_no_fallback"); }

        requests = 0; using var headerCancel = new CancellationTokenSource();
        try { using var ignored = Open(async (_, token) => { requests++; headerCancel.CancelAfter(10); await Task.Delay(Timeout.Infinite, token); return Good(); }, headerCancel.Token); throw new Exception("cancel_headers"); }
        catch (OperationCanceledException) { Check(requests == 1, "header_cancellation_no_fallback"); }

        try { ProtocolDownload.Url(pin with { Path = "runtime/assets/unknown.json" }); throw new Exception("allowlist"); }
        catch (IOException e) { Check(e.Message == "protocol_source_rejected", "allowlist_before_io"); }

        // The dedicated protocol suite requires a verified fixture explicitly.
        // Legacy package tests keep their original direct-download coverage.
        if (archiveBytes is not null)
        {
            Check(archiveBytes.Length == (int)ProtocolDownload.ArchiveSize, "archive_file_exact_size");

            // 1. Raw + contents bounded failures, then verified archive fallback success
            var archiveSeen = new List<string>();
            requests = 0;
            using (var stream = Open((req, _) =>
            {
                requests++;
                if (req.RequestUri!.AbsoluteUri == ProtocolDownload.ArchiveUrl)
                {
                    Check(req.Headers.UserAgent.ToString().StartsWith("SodaTerminal-AssetQuickRead-Installer/"), "archive_user_agent");
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archiveBytes) });
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
            }, status: (source, code) => archiveSeen.Add(source + ":" + code)))
            {
                Check(stream.Length == bytes.Length && requests == 5, "archive_fallback_success_five_requests");
            }
            Check(archiveSeen.Contains("github_raw:protocol_download_failed_http_403") &&
                  archiveSeen.Contains("github_contents:protocol_download_failed_http_403") &&
                  archiveSeen.Contains("github_archive:request_started") &&
                  archiveSeen.Contains("github_archive:verified"), "archive_source_stage_codes");

            // 2. Cache reuse across 3 protocol files in shared scope: archive downloaded at most once
            var manifestPin = new CatalogFile("runtime/assets/manifest.json", 24, "38006E92317CF8EBA3E578D58E5B0FC9AECFEC117D20DC375AA9BED2B073302E", true);
            var dataminePin = new CatalogFile("runtime/assets/datamine.json", 1491, "9702AB5EE0CB44A18D8D3BE9AE2C5934DEB4883989ADA292A3BBD44654EFB449", true);
            var napPin = new CatalogFile("runtime/assets/nap.json", 1943626, "2DFB176209CECEAB5AB53701F12FA3050AD70140004118B7A80AC32A01DF982D", true);
            var protocolPins = new[] { manifestPin, dataminePin, napPin };

            using (var sharedScope = new ProtocolFallbackScope())
            {
                int archiveDownloadCount = 0;
                Parallel.ForEach(protocolPins, new ParallelOptions { MaxDegreeOfParallelism = 3 }, targetPin =>
                {
                    using var s = ProtocolDownload.Open(targetPin, CancellationToken.None, null, () => Client((req, _) =>
                    {
                        if (req.RequestUri!.AbsoluteUri == ProtocolDownload.ArchiveUrl)
                        {
                            Interlocked.Increment(ref archiveDownloadCount);
                            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archiveBytes) });
                        }
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.GatewayTimeout));
                    }), budget, fallbackScope: sharedScope);
                    Check(s.Length == targetPin.Size, "parallel_archive_entry_size_" + targetPin.Path);
                });
                Check(archiveDownloadCount == 1, "shared_scope_single_archive_fetch_across_3_files");
            }

            // 3. Rejection of corrupt / truncated / oversize archive
            // Corrupt archive hash
            var corruptArchive = new byte[archiveBytes.Length];
            Refuses(() => Open((req, _) =>
            {
                requests++;
                if (req.RequestUri!.AbsoluteUri == ProtocolDownload.ArchiveUrl)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(corruptArchive) });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }), "protocol_download_hash_mismatch", "archive_hash_mismatch_rejected");

            // Oversize declared archive
            Refuses(() => Open((req, _) =>
            {
                requests++;
                if (req.RequestUri!.AbsoluteUri == ProtocolDownload.ArchiveUrl)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[archiveBytes.Length + 1]) });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }), "protocol_download_size_mismatch", "archive_oversize_declared_rejected");

            // Incomplete undeclared archive stream
            Refuses(() => Open((req, _) =>
            {
                requests++;
                if (req.RequestUri!.AbsoluteUri == ProtocolDownload.ArchiveUrl)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new Slow(archiveBytes, 0, truncate: true)) });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }), "protocol_download_incomplete", "archive_incomplete_rejected");

            // 4. In-memory Zip entry checks: duplicate entries and symlink entries
            // Duplicate exact entry rejection
            using (var dupMs = new MemoryStream())
            {
                using (var zipCreate = new ZipArchive(dupMs, ZipArchiveMode.Create, leaveOpen: true))
                {
                    var e1 = zipCreate.CreateEntry(ProtocolDownload.ArchiveZipPrefix + "manifest.json");
                    using (var s = e1.Open()) s.Write(bytes);
                    var e2 = zipCreate.CreateEntry(ProtocolDownload.ArchiveZipPrefix + "manifest.json");
                    using (var s = e2.Open()) s.Write(bytes);
                }
                dupMs.Position = 0;
                using var dupRead = new ZipArchive(dupMs, ZipArchiveMode.Read);
                try
                {
                    ProtocolFallbackScope.VerifyAndExtractZipEntry(dupRead, pin, CancellationToken.None);
                    throw new Exception("duplicate_should_fail");
                }
                catch (IOException ex) { Check(ex.Message == "protocol_source_rejected", "duplicate_entry_rejected"); }
            }

            // Symlink entry rejection
            using (var symMs = new MemoryStream())
            {
                using (var zipCreate = new ZipArchive(symMs, ZipArchiveMode.Create, leaveOpen: true))
                {
                    var e = zipCreate.CreateEntry(ProtocolDownload.ArchiveZipPrefix + "manifest.json");
                    e.ExternalAttributes = (int)(0xA1FF << 16); // S_IFLNK
                    using (var s = e.Open()) s.Write(bytes);
                }
                symMs.Position = 0;
                using var symRead = new ZipArchive(symMs, ZipArchiveMode.Read);
                try
                {
                    ProtocolFallbackScope.VerifyAndExtractZipEntry(symRead, pin, CancellationToken.None);
                    throw new Exception("symlink_should_fail");
                }
                catch (IOException ex) { Check(ex.Message == "protocol_source_rejected", "symlink_entry_rejected"); }
            }
        }

        foreach (var version in InstallRegistration.PublishedVersions)
        {
            var oldRoot = Path.Combine(root, "reuse-" + version); var path = InstallEngine.Safe(Path.Combine(oldRoot, "versions", version), pin.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, bytes);
            using (var reused = InstallEngine.ReuseProtocol(oldRoot, pin)) Check(reused?.Length == bytes.Length, "published_" + version + "_reused");
            Check(InstallEngine.Digest(path) == pin.Sha256, "published_source_read_only");
            File.WriteAllBytes(path, new byte[bytes.Length]); Check(InstallEngine.ReuseProtocol(oldRoot, pin) == null, "corrupt_local_rejected");
            File.WriteAllBytes(path, new byte[bytes.Length + 1]); Check(InstallEngine.ReuseProtocol(oldRoot, pin) == null, "local_size_rejected");
        }
        var unknown = InstallEngine.Safe(Path.Combine(root, "unknown", "versions", "9.9.9"), pin.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(unknown)!); File.WriteAllBytes(unknown, bytes);
        Check(InstallEngine.ReuseProtocol(Path.Combine(root, "unknown"), pin) == null, "arbitrary_directory_not_reused");
        var diagnostic = new InstallerDiagnostics(Path.Combine(root, "diagnostic")); diagnostic.Record("protocol", "verified", 0, "github_contents"); diagnostic.Record("failed", InstallerDiagnostics.Code(new IOException("private-user-path secret=abc")));
        var record = File.ReadAllText(Path.Combine(root, "diagnostic/latest.json"));
        Check(!record.Contains("secret") && !record.Contains("private-user") && record.Contains("installer_local_operation_failed"), "redacted_diagnostic");
        Check(InstallerDiagnostics.Message("protocol_registration_conflict").Contains("入口") && !InstallerDiagnostics.Message("protocol_registration_conflict").Contains("网络"), "local_failure_not_network");
        Check(InstallEngine.Version == "1.0.10", "current_version");
        return passed;
    }
    private sealed class Fake(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) { token.ThrowIfCancellationRequested(); return response(request, token); } }
    private sealed class Slow(byte[] bytes, int delay, bool truncate = false) : Stream
    {
        private int position;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(delay, cancellationToken);
            var length = truncate ? bytes.Length - 1 : bytes.Length;
            // Six chunks prove progress survives a header-size budget; the idle budget resets per read.
            var count = Math.Min(buffer.Length, Math.Min(Math.Max(1, bytes.Length / 6), length - position));
            if (count == 0) return 0;
            bytes.AsMemory(position, count).CopyTo(buffer); position += count; return count;
        }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
