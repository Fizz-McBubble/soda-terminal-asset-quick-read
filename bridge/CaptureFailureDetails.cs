using System.Globalization;
using System.Text.Json.Serialization;
namespace Soda.AssetQuickRead;

internal sealed record CaptureDiagnostics(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] uint? NativeErrorCode = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] uint? ChildExitCode = null);

internal static class CaptureFailureDetails
{
    // Only errors reachable from the pinned Rust Capture command. No stderr, paths,
    // public response messages, filenames, or arbitrary colon suffixes cross this boundary.
    internal static readonly string[] Codes = [
        "executable_path_unknown", "executable_directory_unknown", "protocol_file_missing", "protocol_hash_mismatch", "protocol_data_invalid",
        "driver_file_missing", "driver_hash_mismatch", "output_already_exists_capture_not_started", "output_parent_missing_capture_not_started",
        "invalid_pinned_seed_metadata", "seed_label_not_in_pinned_metadata", "public_cn_bootstrap_requires_cn_region",
        "public_configuration_request_failed", "public_configuration_body_unavailable", "public_http_seed_shape_rejected",
        "public_query_http_rejected", "public_query_json_invalid", "public_query_return_code_rejected", "public_region_list_invalid", "public_region_selection_ambiguous",
        "public_gateway_url_missing", "public_gateway_url_invalid", "public_gateway_url_not_allowed", "public_gateway_http_rejected", "public_gateway_json_invalid",
        "public_gateway_return_code_rejected", "public_gateway_content_missing", "public_metadata_rsa_block_size_invalid", "public_metadata_rsa_decode_failed",
        "public_metadata_json_invalid", "public_metadata_return_code_type_invalid", "public_metadata_return_code_rejected", "public_metadata_ec2b_missing", "public_metadata_ec2b_decode_failed",
        "client_resource_input_rejected", "client_resource_unavailable", "client_resource_type_or_size_rejected", "client_resource_read_failed",
        "client_dispatch_fields_missing_or_ambiguous", "client_dispatch_version_or_seed_shape_rejected", "client_dispatch_same_object_unverified",
        "capture_dll_unavailable", "capture_open_failed", "capture_receive_failed", "dll_search_restriction_failed", "cancel_handler_unavailable", "live_capture_requires_windows",
        "report_serialization_failed", "output_create_failed_or_already_exists", "output_write_failed"
    ];
    internal static bool TryNormalize(string raw, out string code, out uint? nativeErrorCode)
    {
        code = "capture_child_failed"; nativeErrorCode = null;
        foreach (var pair in new[] { ("capture_open_failed_win32:", "capture_open_failed"), ("capture_receive_failed_win32:", "capture_receive_failed") })
        {
            if (!raw.StartsWith(pair.Item1, StringComparison.Ordinal)) continue;
            var digits = raw[pair.Item1.Length..];
            if (digits.Length is < 1 or > 10 || digits.Any(c => c is < '0' or > '9') || !uint.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var value)) return false;
            code = pair.Item2; nativeErrorCode = value; return true;
        }
        foreach (var prefix in new[] { "protocol_file_missing", "protocol_hash_mismatch", "driver_file_missing", "driver_hash_mismatch" })
        {
            if (!raw.StartsWith(prefix + ":", StringComparison.Ordinal)) continue;
            var filename = raw[(prefix.Length + 1)..];
            var safe = prefix.StartsWith("protocol_", StringComparison.Ordinal)
                ? filename is "manifest.json" or "datamine.json" or "nap.json"
                : filename is "WinDivert.dll" or "WinDivert64.sys";
            if (!safe) return false;
            code = prefix; return true;
        }
        if (!Codes.Contains(raw, StringComparer.Ordinal) || raw is "protocol_file_missing" or "protocol_hash_mismatch" or "driver_file_missing" or "driver_hash_mismatch") return false;
        code = raw; return true;
    }
    internal static bool Valid(Progress progress) => progress.NativeErrorCode is null || progress.Code is "capture_open_failed" or "capture_receive_failed" or "capture_child_start_failed" or "worker_launch_failed" or "uac_declined";
    internal static Progress ChildStartFailure(System.ComponentModel.Win32Exception error, Progress previous) =>
        new("error", "launch", "capture_child_start_failed", previous.ReadyAt, NativeErrorCode: unchecked((uint)error.NativeErrorCode));
    internal static string Phase(string code, Progress previous) => previous.ReadyAt is not null ? "capture" :
        code.StartsWith("capture_", StringComparison.Ordinal) || code.StartsWith("dll_", StringComparison.Ordinal) || code.StartsWith("driver_", StringComparison.Ordinal) ? "driver" :
        code.StartsWith("public_", StringComparison.Ordinal) || code.StartsWith("client_", StringComparison.Ordinal) ? "public_metadata" : "preflight";
}
