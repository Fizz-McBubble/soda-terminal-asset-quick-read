using System.Text.Json;
namespace Soda.AssetQuickRead;

internal static class CaptureFailureSelfTests
{
    private static int checks;
    private static void Check(bool condition, string name) { if (!condition) throw new Exception("failure_diagnostic_test_failed:" + name); checks++; }
    private sealed class FakeLauncher : IWorkerLauncher { public Task Start(Guid id, string directory) => Task.CompletedTask; }
    private sealed class FailedLauncher(int native) : IWorkerLauncher
    {
        internal int Native => native;
        public Task Start(Guid id, string directory) => Task.FromException(new System.ComponentModel.Win32Exception(native));
    }
    private static async Task<int> ReplyStatus(Func<Task<Reply>> action)
    {
        try { return (await action()).Status; }
        catch (ApiFault error) { return error.Status; }
    }
    internal static async Task<int> Run()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "failure-self-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var initial = new Progress("starting", "public_metadata", null);
        var missing = Path.Combine(root, "no-candidate.json");
        foreach (var code in CaptureFailureDetails.Codes)
        {
            var filename = code.StartsWith("protocol_", StringComparison.Ordinal) && code != "protocol_data_invalid" ? ":nap.json" : code.StartsWith("driver_", StringComparison.Ordinal) ? ":WinDivert.dll" : "";
            var parsed = CaptureWorker.OnStderr("error:" + code + filename, initial);
            Check(parsed.Code == code && parsed.State == "error" && CaptureWorker.SafeCode(parsed.Code), "native_code_" + code);
            Check(CaptureWorker.Finish(1, parsed, missing, false).Code == code, "native_code_survives_finish_" + code);
        }
        foreach (var pair in new[] { ("protocol_file_missing", "manifest.json"), ("protocol_hash_mismatch", "datamine.json"), ("protocol_hash_mismatch", "nap.json"), ("driver_file_missing", "WinDivert.dll"), ("driver_hash_mismatch", "WinDivert64.sys") })
            Check(CaptureWorker.OnStderr("error:" + pair.Item1 + ":" + pair.Item2, initial).Code == pair.Item1, "fixed_filename");
        foreach (var prefix in new[] { "capture_open_failed", "capture_receive_failed" })
        {
            foreach (uint value in new uint[] { 0, 5, 577, 4294967295 })
            {
                var progress = CaptureWorker.OnStderr("error:" + prefix + "_win32:" + value, initial);
                Check(progress.Code == prefix && progress.NativeErrorCode == value, "win32_uint");
                Check(CaptureWorker.OnStderr("error:" + prefix, progress) == progress, "generic_does_not_erase_win32");
                Check(CaptureWorker.OnStderr("status:waiting_for_normal_game_login", progress) == progress, "status_does_not_erase_failure");
                Check(CaptureWorker.Finish(unchecked((int)0xC0000005), progress, missing, false) is { Code: var saved, ChildExitCode: 0xC0000005, NativeErrorCode: var savedNative } && saved == prefix && savedNative == value, "windows_exit_unsigned");
            }
        }
        foreach (var input in new[] { "error:capture_open_failed_win32:4294967296", "error:capture_open_failed_win32:-1", "error:capture_open_failed_win32:+1", "error:capture_open_failed_win32: 5", "error:capture_open_failed_win32:5\nprivate", "error:capture_open_failed_win32:00000000005", "error:protocol_file_missing:C:\\private\\nap.json", "error:protocol_file_missing:../nap.json", "error:protocol_file_missing:nap.json:private", "error:driver_hash_mismatch:nap.json", "error:public_private_account", "error:unknown_failure", "error:capture_open_failed:anything", "private seed arbitrary stderr" })
            Check(CaptureWorker.OnStderr(input, initial) == initial, "untrusted_stderr_discarded");
        Check(CaptureWorker.Finish(-1, initial, missing, false) is { Code: "capture_child_failed", ChildExitCode: uint.MaxValue }, "unknown_exit_diagnostic");
        Check(CaptureWorker.Finish(1, new("error", "driver", "capture_open_failed", NativeErrorCode: 5), missing, true).Code == "cancelled", "cancel_priority");
        var candidate = Path.Combine(root, "candidate.json"); Security.WriteNew(candidate, SelfTests.Fixture());
        foreach (var exit in new[] { 0, 2 })
        {
            Check(CaptureWorker.Finish(exit, initial, candidate, false) is { State: "received", Code: "response_semantics_unverified", ChildExitCode: var receivedExit, CandidateStatus: "response_semantics_unverified", CandidateIssueCount: 0 } && receivedExit == (uint)exit, "candidate_preserved");
            Check(CaptureWorker.Finish(exit, new("error", "capture", "capture_receive_failed", NativeErrorCode: 5), candidate, false) is { Code: "capture_receive_failed", NativeErrorCode: 5 }, "candidate_does_not_hide_known_failure");
        }
        var validationIssues = Enumerable.Range(0, 150).Select(index => (object)new { code = "private_issue_" + index }).ToArray();
        var invalidCandidate = Path.Combine(root, "validation-failed.json"); Security.WriteNew(invalidCandidate, SelfTests.Fixture("validation_failed", issues: validationIssues, stopReason: "capture_failed"));
        var validationFailure = CaptureWorker.Finish(2, initial, invalidCandidate, false);
        Check(validationFailure is { State: "error", Phase: "validation", Code: "candidate_validation_issues", CandidateStatus: "validation_failed", CandidateIssueCount: 100 }, "validation_issues_classified_and_bounded");
        Check(!JsonSerializer.Serialize(validationFailure, Program.Json).Contains("private_issue_", StringComparison.Ordinal), "validation_issue_bodies_not_forwarded");
        var missingIssues = Path.Combine(root, "missing-issues.json"); Security.WriteNew(missingIssues, new { status = "validation_failed", stop_reason = "capture_failed" });
        Check(CaptureWorker.Finish(2, initial, missingIssues, false) is { Code: "candidate_validation_issues", CandidateStatus: "validation_failed", CandidateIssueCount: null }, "missing_issues_count_stays_unknown");
        var incompleteCandidate = Path.Combine(root, "coverage-incomplete.json"); Security.WriteNew(incompleteCandidate, SelfTests.Fixture(all: false));
        Check(CaptureWorker.Finish(2, initial, incompleteCandidate, false) is { State: "error", Code: "candidate_coverage_incomplete", CandidateStatus: "response_semantics_unverified", CandidateIssueCount: 0 }, "coverage_failure_classified");
        var unknownCandidate = Path.Combine(root, "unknown-status.json");
        const string privateStatus = "C:\\private\\arbitrary_status";
        Security.WriteNew(unknownCandidate, SelfTests.Fixture(privateStatus));
        var unknownFailure = CaptureWorker.Finish(2, initial, unknownCandidate, false);
        Check(unknownFailure is { State: "error", Code: "candidate_status_invalid", CandidateStatus: "unknown", CandidateIssueCount: 0 }, "unknown_status_collapsed");
        Check(!JsonSerializer.Serialize(unknownFailure, Program.Json).Contains(privateStatus, StringComparison.Ordinal), "raw_status_not_forwarded");
        var timedOutCandidate = Path.Combine(root, "timed-out.json"); Security.WriteNew(timedOutCandidate, SelfTests.Fixture("timed_out", stopReason: "timed_out"));
        Check(CaptureWorker.Finish(2, initial, timedOutCandidate, false) is { State: "timed_out", Code: "timed_out", CandidateStatus: "timed_out", CandidateIssueCount: 0 }, "timeout_classified");
        Check(CaptureFailureDetails.ChildStartFailure(new System.ComponentModel.Win32Exception(2), initial) is { Code: "capture_child_start_failed", NativeErrorCode: 2, Phase: "launch" }, "child_start_win32_failure");
        var old = JsonSerializer.Deserialize<Progress>("{\"state\":\"error\",\"phase\":\"public_metadata\",\"code\":\"public_query_http_rejected\"}", Program.Json)!;
        Check(old.NativeErrorCode is null && old.ChildExitCode is null && old.CandidateStatus is null && old.CandidateIssueCount is null && CaptureWorker.SafeCode(old.Code), "old_progress_json");
        Check(CaptureWorker.Finish(1, new("capturing", "capture", null, DateTimeOffset.UtcNow), missing, true) is { State: "stopped", Code: "cancelled", CandidateStatus: "cancelled" }, "cancel_classified");
        foreach (var code in new[] { "timed_out", "cancelled", "staged_for_field_review", "response_semantics_unverified", "candidate_size_invalid", "capture_already_active", "worker_launch_failed" })
            Check(CaptureWorker.SafeCode(code), "existing_bridge_code");
        Check(!CaptureWorker.SafeCode("arbitrary:123") && !CaptureWorker.SafeCode("private2"), "safe_code_not_widened");
        var client = Path.Combine(root, "client"); Directory.CreateDirectory(Path.Combine(client, "ZenlessZoneZero_Data"));
        File.WriteAllText(Path.Combine(client, "ZenlessZoneZero_Data", "resources.assets"), "synthetic fixture");
        const string origin = "http://127.0.0.1:5173";
        foreach (var launcher in new IWorkerLauncher[] { new FakeLauncher(), new FailedLauncher(5), new FailedLauncher(1223) })
        {
            var jobs = Path.Combine(root, Guid.NewGuid().ToString("N"));
            var service = new BridgeService(launcher, jobs, true);
            var pair = JsonSerializer.SerializeToElement((await service.Dispatch("POST", "/connect", origin, null, "{}"u8.ToArray())).Body, Program.Json);
            var auth = "Bearer " + pair.GetProperty("token").GetString();
            var body = JsonSerializer.SerializeToUtf8Bytes(new { clientDirectory = client, targetAccountId = "synthetic", riskAcknowledged = true }, Program.Json);
            var started = JsonSerializer.SerializeToElement((await service.Dispatch("POST", "/jobs", origin, auth, body)).Body, Program.Json);
            var id = started.GetProperty("jobId").GetGuid(); var route = "/jobs/" + id;
            if (launcher is FailedLauncher failedLauncher)
            {
                var failed = JsonSerializer.SerializeToElement((await service.Dispatch("GET", route, origin, auth, [])).Body, Program.Json);
                Check(failed.GetProperty("code").GetString() == (failedLauncher.Native == 1223 ? "uac_declined" : "worker_launch_failed"), "launch_error_retained");
                Check(failed.GetProperty("diagnostics").GetProperty("nativeErrorCode").GetUInt32() == (uint)failedLauncher.Native, "launch_win32_retained");
                Check(!failed.GetProperty("diagnostics").TryGetProperty("childExitCode", out _), "diagnostic_null_child_omitted");
            }
            else
            {
                var directory = Security.JobDirectory(id, jobs);
                var receivedCandidate = Path.Combine(directory, "asset-snapshot.json");
                CaptureWorker.WriteProgress(directory, new("received", "complete", "response_semantics_unverified", ChildExitCode: 2, CandidateStatus: "response_semantics_unverified", CandidateIssueCount: 0));
                var missingCandidate = JsonSerializer.SerializeToElement((await service.Dispatch("GET", route, origin, auth, [])).Body, Program.Json);
                Check(missingCandidate.GetProperty("state").GetString() == "starting" && missingCandidate.GetProperty("candidateAvailable").GetBoolean() == false, "missing_candidate_keeps_previous_safe_status");
                Security.WriteNew(receivedCandidate, SelfTests.Fixture());
                var received = JsonSerializer.SerializeToElement((await service.Dispatch("GET", route, origin, auth, [])).Body, Program.Json);
                Check(received.GetProperty("code").GetString() == "response_semantics_unverified" && received.GetProperty("candidateAvailable").GetBoolean(), "received_candidate_available");
                Check(received.GetProperty("diagnostics").GetProperty("candidateStatus").GetString() == "response_semantics_unverified" && received.GetProperty("diagnostics").GetProperty("candidateIssueCount").GetInt32() == 0, "received_candidate_evidence_roundtrip");
                Check(await ReplyStatus(() => service.Dispatch("GET", route + "/result", origin, auth, [])) == 200, "received_candidate_result");
                File.WriteAllText(receivedCandidate, JsonSerializer.Serialize(SelfTests.Fixture("validation_failed", issues: [new { code = "synthetic_issue" }], stopReason: "capture_failed"), Program.Json));
                var invalidReceived = JsonSerializer.SerializeToElement((await service.Dispatch("GET", route, origin, auth, [])).Body, Program.Json);
                Check(invalidReceived.GetProperty("state").GetString() == "error" && invalidReceived.GetProperty("code").GetString() == "candidate_validation_issues" && invalidReceived.GetProperty("candidateAvailable").GetBoolean() == false, "invalid_received_candidate_rejected");
                Check(invalidReceived.GetProperty("diagnostics").GetProperty("childExitCode").GetUInt32() == 2 && invalidReceived.GetProperty("diagnostics").GetProperty("candidateStatus").GetString() == "validation_failed" && invalidReceived.GetProperty("diagnostics").GetProperty("candidateIssueCount").GetInt32() == 1, "invalid_received_candidate_preserves_progress_evidence");
                Check(await ReplyStatus(() => service.Dispatch("GET", route + "/result", origin, auth, [])) == 409, "invalid_received_candidate_result_blocked");
                var failed = CaptureWorker.Finish(unchecked((int)0xC0000005), CaptureWorker.OnStderr("error:capture_open_failed_win32:577", initial), missing, false);
                CaptureWorker.WriteProgress(directory, failed);
                var status = JsonSerializer.SerializeToElement((await service.Dispatch("GET", route, origin, auth, [])).Body, Program.Json);
                Check(status.GetProperty("code").GetString() == "capture_open_failed" && status.GetProperty("diagnostics").GetProperty("nativeErrorCode").GetUInt32() == 577 && status.GetProperty("diagnostics").GetProperty("childExitCode").GetUInt32() == 0xC0000005, "http_diagnostics_roundtrip");
                var issueFailure = CaptureWorker.Finish(2, initial, invalidCandidate, false);
                CaptureWorker.WriteProgress(directory, issueFailure);
                var issueStatus = JsonSerializer.SerializeToElement((await service.Dispatch("GET", route, origin, auth, [])).Body, Program.Json);
                Check(issueStatus.GetProperty("code").GetString() == "candidate_validation_issues" && issueStatus.GetProperty("candidateAvailable").GetBoolean() == false, "validation_issue_rejected_by_http");
                Check(issueStatus.GetProperty("diagnostics").GetProperty("candidateStatus").GetString() == "validation_failed" && issueStatus.GetProperty("diagnostics").GetProperty("candidateIssueCount").GetInt32() == 100, "validation_issue_http_evidence");
                Check(!issueStatus.GetRawText().Contains("private_issue_", StringComparison.Ordinal), "validation_issue_http_redacted");
                Check(await ReplyStatus(() => service.Dispatch("GET", route + "/result", origin, auth, [])) == 409, "validation_issue_result_blocked");
                CaptureWorker.WriteProgress(directory, CaptureWorker.Finish(2, initial, incompleteCandidate, false));
                var coverageStatus = JsonSerializer.SerializeToElement((await service.Dispatch("GET", route, origin, auth, [])).Body, Program.Json);
                Check(coverageStatus.GetProperty("code").GetString() == "candidate_coverage_incomplete" && coverageStatus.GetProperty("candidateAvailable").GetBoolean() == false, "coverage_failure_rejected_by_http");
                Check(await ReplyStatus(() => service.Dispatch("GET", route + "/result", origin, auth, [])) == 409, "coverage_failure_result_blocked");
                CaptureWorker.WriteProgress(directory, unknownFailure);
                var unknownStatus = JsonSerializer.SerializeToElement((await service.Dispatch("GET", route, origin, auth, [])).Body, Program.Json);
                Check(unknownStatus.GetProperty("code").GetString() == "candidate_status_invalid" && unknownStatus.GetProperty("diagnostics").GetProperty("candidateStatus").GetString() == "unknown", "unknown_status_http_collapsed");
                Check(!unknownStatus.GetRawText().Contains(privateStatus, StringComparison.Ordinal), "unknown_status_http_redacted");
                var path = Path.Combine(directory, "progress.json");
                foreach (var invalid in new[] { "{\"state\":\"error\",\"phase\":\"capture\",\"code\":\"capture_open_failed\",\"nativeErrorCode\":4294967296}", "{\"state\":\"error\",\"phase\":\"capture\",\"code\":\"capture_open_failed\",\"childExitCode\":-1}", "{\"state\":\"error\",\"phase\":\"capture\",\"code\":\"public_query_http_rejected\",\"nativeErrorCode\":5}", "{\"state\":\"error\",\"phase\":\"capture\",\"code\":\"capture_open_failed_win32:5\"}", "{\"state\":\"error\",\"phase\":\"validation\",\"code\":\"candidate_validation_issues\",\"candidateStatus\":\"private raw status\"}", "{\"state\":\"error\",\"phase\":\"validation\",\"code\":\"candidate_validation_issues\",\"candidateStatus\":\"validation_failed\",\"candidateIssueCount\":-1}", "{\"state\":\"error\",\"phase\":\"validation\",\"code\":\"candidate_validation_issues\",\"candidateStatus\":\"validation_failed\",\"candidateIssueCount\":101}", "{\"state\":\"error\",\"phase\":\"validation\",\"code\":\"candidate_validation_issues\",\"candidateIssueCount\":0}" })
                {
                    File.WriteAllText(path, invalid);
                    var rejected = JsonSerializer.SerializeToElement((await service.Dispatch("GET", route, origin, auth, [])).Body, Program.Json);
                    Check(rejected.GetProperty("code").GetString() == "progress_invalid" && rejected.GetProperty("diagnostics").ValueKind == JsonValueKind.Null, "malformed_progress_rejected");
                }
                File.WriteAllText(path, JsonSerializer.Serialize(old, Program.Json));
                var restored = JsonSerializer.SerializeToElement((await service.Dispatch("GET", route, origin, auth, [])).Body, Program.Json);
                Check(restored.GetProperty("code").GetString() == old.Code && restored.GetProperty("diagnostics").ValueKind == JsonValueKind.Null, "old_http_progress_restored");
            }
            service.StopOwnedJobs();
        }
        Console.WriteLine(JsonSerializer.Serialize(new { suite = "capture-failure-diagnostics", passed = checks, captureStarted = false, serviceStarted = false, uacRequested = false, externalNetworkRequests = 0 }, Program.Json));
        return 0;
    }
}
