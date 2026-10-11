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
    internal static Stream Open(CatalogFile pin, CancellationToken cancellation) => Open(pin, cancellation, null);
    internal static Stream Open(CatalogFile pin, CancellationToken cancellation, Action<string, string>? status,
        Func<HttpClient>? clientFactory = null)
    {
        var raw = Url(pin); // Validate identity before constructing either public source.
        var name = pin.Path.Split('/').Last();
        var contents = "https://api.github.com/repos/pingwinekZ/zzz_packet_capture_0delay/contents/assets/" + name +
            "?ref=4853f07c8fb5322c12b9b580d6364b577c2ac96a";
        var failure = "protocol_download_failed";
        foreach (var source in new[] { (Url: raw, Id: "github_raw"), (Url: contents, Id: "github_contents") })
        {
            cancellation.ThrowIfCancellationRequested(); status?.Invoke(source.Id, "request_started");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            using var client = clientFactory?.Invoke() ?? new HttpClient(new HttpClientHandler
                { AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false }) { Timeout = Timeout.InfiniteTimeSpan };
            using var request = new HttpRequestMessage(HttpMethod.Get, source.Url);
            request.Headers.UserAgent.ParseAdd("SodaTerminal-AssetQuickRead-Installer/" + InstallEngine.Version);
            if (source.Id == "github_contents") request.Headers.Accept.ParseAdd("application/vnd.github.raw+json");
            try
            {
                using var response = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).GetAwaiter().GetResult();
                if (response.StatusCode != System.Net.HttpStatusCode.OK) throw new IOException("protocol_download_failed_http_" + (int)response.StatusCode);
                if (response.Content.Headers.ContentLength is long size && size != pin.Size) throw new IOException("protocol_download_size_mismatch");
                using var input = response.Content.ReadAsStream(timeout.Token);
                using var bytes = new MemoryStream(); var buffer = new byte[65536]; int count;
                while ((count = input.ReadAsync(buffer.AsMemory(), timeout.Token).AsTask().GetAwaiter().GetResult()) != 0)
                { if (bytes.Length + count > pin.Size) throw new IOException("protocol_download_size_mismatch"); bytes.Write(buffer, 0, count); }
                if (bytes.Length != pin.Size) throw new IOException("protocol_download_incomplete");
                var content = bytes.ToArray();
                if (Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)) != pin.Sha256) throw new IOException("protocol_download_hash_mismatch");
                status?.Invoke(source.Id, "verified"); return new MemoryStream(content, writable: false);
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            { failure = "protocol_download_timeout"; status?.Invoke(source.Id, failure); }
            catch (HttpRequestException) { failure = "protocol_download_connection_failed"; status?.Invoke(source.Id, failure); }
            catch (IOException error) { failure = error.Message; status?.Invoke(source.Id, failure); }
        }
        cancellation.ThrowIfCancellationRequested(); throw new IOException(failure);
    }
}
