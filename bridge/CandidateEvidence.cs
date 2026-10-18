using System.Text.Json;
namespace Soda.AssetQuickRead;

internal sealed record CandidateEvidence(string Status, int? IssueCount);

internal static class CandidateEvidenceRules
{
    internal const int MaxIssueCount = 100;
    internal static readonly string[] Statuses = [
        "validation_failed",
        "staged_for_field_review",
        "response_semantics_unverified",
        "load_responses_observed",
        "timed_out",
        "cancelled",
        "unknown"
    ];

    internal static CandidateEvidence FromDocument(JsonElement root)
    {
        var rawStatus = root.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String
            ? status.GetString()
            : null;
        var safeStatus = rawStatus is not null && Statuses.Contains(rawStatus, StringComparer.Ordinal)
            ? rawStatus
            : "unknown";
        var issueCount = root.TryGetProperty("issues", out var issues) && issues.ValueKind == JsonValueKind.Array
            ? Math.Min(issues.GetArrayLength(), MaxIssueCount)
            : (int?)null;
        return new(safeStatus, issueCount);
    }

    internal static bool SafeStatus(string? status) => status is null || Statuses.Contains(status, StringComparer.Ordinal);

    internal static bool SafeIssueCount(string? status, int? issueCount) =>
        issueCount is null || (status is not null && issueCount is >= 0 and <= MaxIssueCount);
}
