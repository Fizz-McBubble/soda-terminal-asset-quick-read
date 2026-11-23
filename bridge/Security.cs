using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
namespace Soda.AssetQuickRead;

internal static class Security
{
    internal const int BodyLimit = 4096;
    internal static readonly string[] Origins = ["http://127.0.0.1:50565", "http://127.0.0.1:5173", "https://sodaterminal.com"];
    internal static bool ValidHost(string? host) => ServiceReadiness.ServicePorts.Any(port => host == $"127.0.0.1:{port}");
    internal static bool ValidOrigin(string? origin) => origin is not null && Origins.Contains(origin, StringComparer.Ordinal);
    internal static readonly Dictionary<string, string> Pinned = new(StringComparer.Ordinal)
    {
        ["soda-asset-capture.exe"] = "8D8F05E53A2C72356D076CD36902C3CEEF1616DC65FA0BAA89D7005B4C4764F4",
        ["WinDivert.dll"] = "C1E060EE19444A259B2162F8AF0F3FE8C4428A1C6F694DCE20DE194AC8D7D9A2",
        ["WinDivert64.sys"] = "8DA085332782708D8767BCACE5327A6EC7283C17CFB85E40B03CD2323A90DDC2",
        ["assets/manifest.json"] = "CAEBBE1A8AC8324109521B1477F67F6B10E387FC2B550486DE044027121D1CF8",
        ["assets/datamine.json"] = "C7FAC8E4CDF0E59CAD967AA0C8A21D5DB54E746902E484DF632E65800F127F04",
        ["assets/nap.json"] = "8FFA529920FE01265A8538BE06B821DC9B62247419C2200C837F24F4441F6DF6"
    };
    internal static string RuntimeDirectory => Path.Combine(AppContext.BaseDirectory, "runtime");
    internal static void VerifyRuntime(string root)
    {
        RejectLinks(root);
        foreach (var pair in Pinned)
        {
            var file = Path.Combine(root, pair.Key.Replace('/', Path.DirectorySeparatorChar));
            RejectLinks(file);
            using var stream = File.OpenRead(file);
            if (Convert.ToHexString(SHA256.HashData(stream)) != pair.Value) throw new ApiFault(409, "runtime_identity_mismatch");
        }
    }
    // Reject every ancestor, not merely the final file: junctions can escape the installation directory.
    internal static void RejectLinks(string path)
    {
        var full = Path.GetFullPath(path);
        for (var current = full; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new ApiFault(400, "linked_path_rejected");
        }
    }
    internal static void RejectExistingLinks(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new ApiFault(400, "linked_path_rejected");
    }
    internal static string ValidateClient(string? input)
    {
        if (string.IsNullOrWhiteSpace(input) || input.Length > 1024 || !Path.IsPathFullyQualified(input) || input.StartsWith("\\\\") || !char.IsAsciiLetter(input[0]) || input.Length < 3 || input[1] != ':' || (input[2] != '\\' && input[2] != '/')) throw new ApiFault(400, "client_directory_rejected");
        var full = Path.GetFullPath(input);
        if (!Directory.Exists(full)) throw new ApiFault(400, "client_directory_missing");
        RejectLinks(full);
        var resource = Path.Combine(full, "ZenlessZoneZero_Data", "resources.assets");
        if (!File.Exists(resource)) throw new ApiFault(400, "client_resource_missing");
        RejectLinks(resource);
        var info = new FileInfo(resource);
        if (info.Length is <= 0 or > 64 * 1024 * 1024) throw new ApiFault(400, "client_resource_size_rejected");
        return full;
    }
    internal static void RequireGameClosed()
    {
        foreach (var name in new[] { "ZenlessZoneZero", "ZenlessZoneZeroGame", "ZenlessZoneZero_CN" })
        {
            var processes = Process.GetProcessesByName(name);
            try { if (processes.Length != 0) throw new ApiFault(409, "game_must_be_closed"); }
            finally { foreach (var p in processes) p.Dispose(); }
        }
    }
    internal static string JobDirectory(Guid id, string? root = null)
    {
        var basePath = Path.GetFullPath(root ?? Program.JobRoot);
        var path = Path.Combine(basePath, id.ToString("D"));
        RejectExistingLinks(path);
        return path;
    }
    internal static void WriteNew(string path, object body)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(stream, body, Program.Json);
        stream.Flush(true);
    }
    internal static JsonElement ParseObject(byte[] bytes, params string[] allowed)
    {
        if (bytes.Length is 0 or > BodyLimit) throw new ApiFault(400, "invalid_body");
        try
        {
            using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
            var obj = doc.RootElement;
            if (obj.ValueKind != JsonValueKind.Object) throw new ApiFault(400, "invalid_body");
            var names = obj.EnumerateObject().Select(p => p.Name).ToArray();
            if (names.Distinct(StringComparer.Ordinal).Count() != names.Length || names.Any(n => !allowed.Contains(n, StringComparer.Ordinal))) throw new ApiFault(400, "unexpected_body_fields");
            return obj.Clone();
        }
        catch (JsonException) { throw new ApiFault(400, "invalid_json"); }
    }
}
