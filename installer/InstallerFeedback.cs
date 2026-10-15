using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Soda.AssetQuickRead.Setup;

// Only finite whitelisted phases, classified codes and attempts enter this report.
// ProcessId, executableSha256, registration identities, local paths, URLs, raw exceptions,
// accounts, environment details and traffic never enter this DTO.
internal sealed record InstallerAttemptReport(
    [property: JsonPropertyName("item")] int Item,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("elapsedMs")] long ElapsedMs
);

internal sealed record InstallerFeedbackReport(
    [property: JsonPropertyName("schema")] int Schema,
    [property: JsonPropertyName("tool")] string Tool,
    [property: JsonPropertyName("reportId")] string ReportId,
    [property: JsonPropertyName("installerVersion")] string InstallerVersion,
    [property: JsonPropertyName("stage")] string Stage,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("durationMs")] long DurationMs,
    [property: JsonPropertyName("attempts")] InstallerAttemptReport[] Attempts
);

internal static class InstallerFeedback
{
    internal const string FeedbackHost = "https://sodaterminal.com/installer-feedback#";

    internal static readonly HashSet<string> WhitelistedStages = new(StringComparer.Ordinal)
    {
        "prepare",
        "payload_verify",
        "copy",
        "protocol",
        "verify",
        "register",
        "service_start",
        "failed",
    };

    internal static readonly HashSet<string> WhitelistedCodes = new(StringComparer.Ordinal)
    {
        "installer_access_denied",
        "installer_local_operation_failed",
        "local_service_bind_or_identity_unavailable",
        "local_service_readiness_timeout",
        "local_service_start_failed",
        "protocol_source_rejected",
        "payload_hash_mismatch",
        "catalog_identity_rejected",
        "installed_hash_mismatch",
        "installer_cancelled",
        "another_installer_is_running",
        "offline_payload_unavailable",
        "payload_file_set_mismatch",
        "payload_link_or_duplicate",
        "payload_catalog_mismatch",
        "payload_file_hash_mismatch",
        "catalog_path_rejected",
        "catalog_escape_rejected",
        "protocol_download_timeout",
        "protocol_download_connection_failed",
        "protocol_download_failed",
        "protocol_download_size_mismatch",
        "protocol_download_incomplete",
        "protocol_download_hash_mismatch",
        "protocol_download_failed_http_403",
        "protocol_download_failed_http_404",
        "protocol_download_failed_http_429",
        "protocol_download_failed_http_500",
        "protocol_download_failed_http_502",
        "protocol_download_failed_http_503",
        "protocol_download_failed_http_504",
        "installed_file_set_mismatch",
        "linked_path_rejected",
    };

    internal static readonly HashSet<string> WhitelistedAttemptSources = new(StringComparer.Ordinal)
    {
        "github_raw",
        "github_contents",
        "verified_local",
        "github_archive",
    };

    internal static readonly HashSet<string> WhitelistedAttemptCodes = new(StringComparer.Ordinal)
    {
        "request_started",
        "verified",
        "protocol_source_rejected",
        "protocol_download_timeout",
        "protocol_download_connection_failed",
        "protocol_download_failed",
        "protocol_download_size_mismatch",
        "protocol_download_incomplete",
        "protocol_download_hash_mismatch",
        "protocol_download_failed_http_403",
        "protocol_download_failed_http_404",
        "protocol_download_failed_http_429",
        "protocol_download_failed_http_500",
        "protocol_download_failed_http_502",
        "protocol_download_failed_http_503",
        "protocol_download_failed_http_504",
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    internal static string ClassifyCode(string? code)
    {
        if (code is not null && WhitelistedCodes.Contains(code)) return code;
        return "installer_local_operation_failed";
    }

    internal static InstallerFeedbackReport CreateSafeReport(
        string stage,
        string code,
        long durationMs,
        IEnumerable<InstallerAttemptReport> attempts)
    {
        var safeStage = WhitelistedStages.Contains(stage) ? stage : "failed";
        var safeCode = ClassifyCode(code);
        var safeDuration = Math.Clamp(durationMs, 0, 3600000);
        var boundedAttempts = attempts
            .Where(a => a.Item is >= 0 and <= 2 &&
                        WhitelistedAttemptSources.Contains(a.Source) &&
                        WhitelistedAttemptCodes.Contains(a.Code))
            .Take(16)
            .Select(a => a with { ElapsedMs = Math.Clamp(a.ElapsedMs, 0, 3600000) })
            .ToArray();

        return new InstallerFeedbackReport(
            1,
            "asset_quick_read_installer",
            Guid.NewGuid().ToString("D"),
            InstallEngine.Version,
            safeStage,
            safeCode,
            safeDuration,
            boundedAttempts
        );
    }

    internal static string SerializeReport(InstallerFeedbackReport report)
    {
        return JsonSerializer.Serialize(report, JsonOptions);
    }

    internal static string BuildLaunchUrl(InstallerFeedbackReport report)
    {
        var json = SerializeReport(report);
        return FeedbackHost + Uri.EscapeDataString(json);
    }

    internal static bool TryLaunchBrowser(string url)
    {
        try
        {
            var start = new ProcessStartInfo(url) { UseShellExecute = true };
            using var proc = Process.Start(start);
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static bool TrySetClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
