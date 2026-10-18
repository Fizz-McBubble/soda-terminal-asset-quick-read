using System.Security.Cryptography;
using System.Text.Json;
namespace Soda.AssetQuickRead;
internal sealed record Candidate(JsonElement Document, string Sha256, DateTimeOffset CapturedAt, SafeCounts Counts, string CandidateStatus, int? CandidateIssueCount);
internal static class Snapshot
{
    internal const int MaxBytes = 16 * 1024 * 1024;
    internal static Candidate Read(string file)
    {
        Security.RejectLinks(file);
        if (new FileInfo(file).Length is <= 0 or > MaxBytes) throw new ApiFault(409, "candidate_size_invalid");
        byte[] bytes = File.ReadAllBytes(file);
        try
        {
            using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            var root = json.RootElement;
            var evidence = CandidateEvidenceRules.FromDocument(root);
            if (evidence.Status == "validation_failed" || evidence.IssueCount is > 0) throw new ApiFault(409, "candidate_validation_issues", evidence.Status, evidence.IssueCount);
            if (root.GetProperty("schema").GetString() != "soda-asset-snapshot-probe/v1" || root.GetProperty("stop_reason").GetString() != "load_responses_observed") throw new ApiFault(409, "candidate_schema_invalid", evidence.Status, evidence.IssueCount);
            if (evidence.Status is not ("staged_for_field_review" or "response_semantics_unverified")) throw new ApiFault(409, "candidate_status_invalid", evidence.Status, evidence.IssueCount);
            var issues = root.GetProperty("issues");
            if (issues.ValueKind != JsonValueKind.Array || issues.GetArrayLength() != 0) throw new ApiFault(409, "candidate_validation_issues", evidence.Status, evidence.IssueCount);
            var coverage = root.GetProperty("coverage");
            if (!coverage.GetProperty("disc_load_received").GetBoolean() || !coverage.GetProperty("engine_load_received").GetBoolean() || !coverage.GetProperty("agent_load_received").GetBoolean()) throw new ApiFault(409, "candidate_coverage_incomplete", evidence.Status, evidence.IssueCount);
            foreach (var flag in new[] { "account_write_enabled", "raw_traffic_saved", "ready_for_account_import", "full_inventory_verified", "independent_inventory_total_verified" })
                if (root.GetProperty(flag).ValueKind != JsonValueKind.False) throw new ApiFault(409, "candidate_safety_flags_invalid", evidence.Status, evidence.IssueCount);
            if (root.GetProperty("response_status_semantics_verified").ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ApiFault(409, "candidate_semantics_invalid", evidence.Status, evidence.IssueCount);
            var source = root.GetProperty("source");
            if (source.GetProperty("requested_game_region").GetString() != "CN" || source.GetProperty("game_account_identity").GetString() != "not_collected_or_verified") throw new ApiFault(409, "candidate_source_invalid", evidence.Status, evidence.IssueCount);
            var assets = root.GetProperty("assets"); var counts = root.GetProperty("counts");
            foreach (var category in new[] { "discs", "engines", "agents" })
                if (assets.GetProperty(category).ValueKind != JsonValueKind.Array || !counts.GetProperty(category).TryGetInt32(out var amount) || amount < 0 || amount != assets.GetProperty(category).GetArrayLength()) throw new ApiFault(409, "candidate_counts_invalid", evidence.Status, evidence.IssueCount);
            return new(root.Clone(), Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), File.GetLastWriteTimeUtc(file), new(counts.GetProperty("discs").GetInt32(), counts.GetProperty("engines").GetInt32(), counts.GetProperty("agents").GetInt32()), evidence.Status, evidence.IssueCount);
        }
        catch (ApiFault) { throw; }
        catch { throw new ApiFault(409, "candidate_schema_invalid"); }
    }
}
