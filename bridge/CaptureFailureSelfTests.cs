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
            Check(CaptureWorker.Finish(exit, initial, candidate, false) is { State: "received", Code: "response_semantics_unverified", ChildExitCode: var receivedExit } && receivedExit == (uint)exit, "candidate_preserved");
            Check(CaptureWorker.Finish(exit, new("error", "capture", "capture_receive_failed", NativeErrorCode: 5), candidate, false) is { Code: "capture_receive_failed", NativeErrorCode: 5 }, "candidate_does_not_hide_known_failure");
        }
        Check(CaptureFailureDetails.ChildStartFailure(new System.ComponentModel.Win32Exception(2), initial) is { Code: "capture_child_start_failed", NativeErrorCode: 2, Phase: "launch" }, "child_start_win32_failure");
        var old = JsonSerializer.Deserialize<Progress>("{\"state\":\"error\",\"phase\":\"public_metadata\",\"code\":\"public_query_http_rejected\"}", Program.Json)!;
        Check(old.NativeErrorCode is null && old.ChildExitCode is null && CaptureWorker.SafeCode(old.Code), "old_progress_json");
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
                var failed = CaptureWorker.Finish(unchecked((int)0xC0000005), CaptureWorker.OnStderr("error:capture_open_failed_win32:577", initial), missing, false);
                CaptureWorker.WriteProgress(directory, failed);
                var status = JsonSerializer.SerializeToElement((await service.Dispatch("GET", route, origin, auth, [])).Body, Program.Json);
                Check(status.GetProperty("code").GetString() == "capture_open_failed" && status.GetProperty("diagnostics").GetProperty("nativeErrorCode").GetUInt32() == 577 && status.GetProperty("diagnostics").GetProperty("childExitCode").GetUInt32() == 0xC0000005, "http_diagnostics_roundtrip");
                var path = Path.Combine(directory, "progress.json");
                foreach (var invalid in new[] { "{\"state\":\"error\",\"phase\":\"capture\",\"code\":\"capture_open_failed\",\"nativeErrorCode\":4294967296}", "{\"state\":\"error\",\"phase\":\"capture\",\"code\":\"capture_open_failed\",\"childExitCode\":-1}", "{\"state\":\"error\",\"phase\":\"capture\",\"code\":\"public_query_http_rejected\",\"nativeErrorCode\":5}", "{\"state\":\"error\",\"phase\":\"capture\",\"code\":\"capture_open_failed_win32:5\"}" })
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
