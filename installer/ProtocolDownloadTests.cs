using System.Net;
using System.Security.Cryptography;
namespace Soda.AssetQuickRead.Setup;
internal static class ProtocolDownloadTests
{
    internal static int Run(string root, byte[] bytes)
    {
        Directory.CreateDirectory(root);
        var pin = new CatalogFile("runtime/assets/manifest.json", bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)), true);
        ProtocolDownload.Url(pin); // Tests require the actual pinned bytes, never a relaxed allowlist.
        int passed = 0, requests = 0;
        void Check(bool value, string name) { if (!value) throw new Exception(name); passed++; }
        var budget = new DownloadBudget(TimeSpan.FromMilliseconds(60), TimeSpan.FromMilliseconds(60), TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(1));
        HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) => new(new Fake(respond));
        HttpResponseMessage Good() => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        Stream Open(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond, CancellationToken token = default, Action<string, string>? status = null)
            => ProtocolDownload.Open(pin, token, status, () => Client(respond), budget);
        void Refuses(Func<Stream> operation, string code, string name, int count = 4)
        {
            requests = 0;
            try { using var ignored = operation(); throw new Exception(name); }
            catch (IOException e) { Check(e.Message == code && requests == count, name); }
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
        Refuses(() => Open((_, _) => { requests++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[bytes.Length]) }); }), "protocol_download_hash_mismatch", "corrupt_both_sources_rejected");
        Refuses(() => Open((_, _) => { requests++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[bytes.Length + 1]) }); }), "protocol_download_size_mismatch", "declared_size_rejected");
        Refuses(() => Open((_, _) => { requests++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new Slow(bytes, 0, truncate: true)) }); }), "protocol_download_incomplete", "undeclared_incomplete_rejected");
        Refuses(() => Open((_, _) => { requests++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new Slow(new byte[bytes.Length + 1], 0)) }); }), "protocol_download_size_mismatch", "undeclared_oversize_rejected");
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
        Check(InstallEngine.Version == "1.0.9", "current_version");
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
