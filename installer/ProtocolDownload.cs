namespace Soda.AssetQuickRead.Setup;
internal static class ProtocolDownload
{
    private const string Base = "https://raw.githubusercontent.com/pingwinekZ/zzz_packet_capture_0delay/4853f07c8fb5322c12b9b580d6364b577c2ac96a/assets/";
    private static readonly Dictionary<string, string> Pins = new(StringComparer.Ordinal)
    {
        ["runtime/assets/manifest.json"] = "38006E92317CF8EBA3E578D58E5B0FC9AECFEC117D20DC375AA9BED2B073302E",
        ["runtime/assets/datamine.json"] = "9702AB5EE0CB44A18D8D3BE9AE2C5934DEB4883989ADA292A3BBD44654EFB449",
        ["runtime/assets/nap.json"] = "2DFB176209CECEAB5AB53701F12FA3050AD70140004118B7A80AC32A01DF982D"
    };
    internal static string Url(CatalogFile pin)
    {
        if (!pin.Download || !Pins.TryGetValue(pin.Path, out var hash) || hash != pin.Sha256) throw new IOException("protocol_source_rejected");
        return Base + pin.Path.Split('/').Last();
    }
    internal static Stream Open(CatalogFile pin, CancellationToken cancellation)
    {
        // Exactly one pinned public file request: no cookies, credentials or redirects.
        var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        using var request = new HttpRequestMessage(HttpMethod.Get, Url(pin));
        HttpResponseMessage? response = null;
        try
        {
            response = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation).GetAwaiter().GetResult();
            if (response.StatusCode != System.Net.HttpStatusCode.OK) throw new IOException("protocol_download_failed_http_" + (int)response.StatusCode);
            if (response.Content.Headers.ContentLength is long size && size != pin.Size) throw new IOException("protocol_download_size_mismatch");
            return new DownloadStream(response.Content.ReadAsStream(cancellation), response, client, cancellation);
        }
        catch { response?.Dispose(); client.Dispose(); throw; }
    }
    private sealed class DownloadStream(Stream stream, HttpResponseMessage response, HttpClient client, CancellationToken cancellation) : Stream
    {
        private readonly CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        { lifetime.CancelAfter(TimeSpan.FromSeconds(60)); return stream.ReadAsync(buffer.AsMemory(offset, count), lifetime.Token).AsTask().GetAwaiter().GetResult(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) { stream.Dispose(); response.Dispose(); client.Dispose(); lifetime.Dispose(); } base.Dispose(disposing); }
    }
}
