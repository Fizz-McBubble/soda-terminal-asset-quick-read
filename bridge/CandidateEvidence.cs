using System.Text.Json;
namespace Soda.AssetQuickRead;

internal sealed record CandidateEvidence(string Status, int? IssueCount);

internal enum CandidateIssueClass
{
    None,
    FieldInvalid,
    IdentityIntegrity,
    Generic
}

internal static class CandidateEvidenceRules
{
    internal const int MaxIssueCount = 100;
    private static readonly HashSet<string> FieldInvalidCodes = new(StringComparer.Ordinal) {
        "u32_overflow", "wrong_wire_type", "malformed_nested", "invalid_response_status",
        "response_status_mapping_unverified", "missing_catalog_id", "invalid_slot", "out_of_range",
        "too_many_substats"
    };
    private static readonly HashSet<string> IdentityIntegrityCodes = new(StringComparer.Ordinal) {
        "missing_or_duplicate_instance_id", "missing_or_duplicate_agent_id",
        "missing_or_shared_engine_reference", "missing_or_shared_disc_reference",
        "invalid_or_duplicate_equipped_slot", "equipped_slot_mismatch",
        "repeated_load_requires_scope_review", "handshake_not_observed",
        "transport_or_decode_incomplete", "capture_direction_conflicts",
        "capture_ip_packets_rejected", "capture_receive_failed", "capture_timer_failed",
        "ambiguous_session_seed"
    };
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

    internal static CandidateIssueClass IssueClass(JsonElement root)
    {
        if (!root.TryGetProperty("issues", out var issues) || issues.ValueKind != JsonValueKind.Array || issues.GetArrayLength() == 0)
            return CandidateIssueClass.None;
        var field = false; var identity = false;
        foreach (var issue in issues.EnumerateArray().Take(MaxIssueCount))
        {
            if (issue.ValueKind != JsonValueKind.Object || !issue.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.String)
                return CandidateIssueClass.Generic;
            var value = code.GetString()!;
            var isField = FieldInvalidCodes.Contains(value);
            var isIdentity = IdentityIntegrityCodes.Contains(value);
            if (!isField && !isIdentity) return CandidateIssueClass.Generic;
            field |= isField;
            identity |= isIdentity;
        }
        return identity ? CandidateIssueClass.IdentityIntegrity
            : field ? CandidateIssueClass.FieldInvalid
            : CandidateIssueClass.None;
    }
}
