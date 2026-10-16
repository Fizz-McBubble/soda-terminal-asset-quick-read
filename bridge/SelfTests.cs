using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Http;
namespace Soda.AssetQuickRead;
internal static class SelfTests
{
    private static int checks;
    private const string Origin = "http://127.0.0.1:50565";
    private static readonly byte[] Empty = "{}"u8.ToArray();
    private static void Check(bool value, string name) { if (!value) throw new Exception("test_failed:" + name); checks++; }
    private static async Task Refuses(Func<Task> action, int status, string name)
    { try { await action(); } catch (ApiFault e) { Check(e.Status == status, name); return; } throw new Exception("test_did_not_refuse:" + name); }
    internal static object Fixture(string status = "response_semantics_unverified", bool all = true, object[]? issues = null) => new
    {
        schema = "soda-asset-snapshot-probe/v1", status, stop_reason = "load_responses_observed",
        source = new { requested_game_region = "CN", game_account_identity = "not_collected_or_verified" },
        coverage = new { disc_load_received = all, engine_load_received = true, agent_load_received = true },
        counts = new { discs = 0, engines = 0, agents = 0 }, assets = new { discs = Array.Empty<object>(), engines = Array.Empty<object>(), agents = Array.Empty<object>() },
        issues = issues ?? [], account_write_enabled = false, raw_traffic_saved = false, ready_for_account_import = false,
        full_inventory_verified = false, independent_inventory_total_verified = false, response_status_semantics_verified = false
    };
    private sealed class FakeLauncher : IWorkerLauncher { public Task Start(Guid id, string directory) => Task.CompletedTask; }
    private sealed class DelayedLauncher : IWorkerLauncher
    {
        internal readonly TaskCompletionSource Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Start(Guid id, string directory) => Pending.Task;
    }
    internal static async Task<int> Run()
    {
        if (await HostSelfTests.RunChild() is { } childExit) return childExit;
        await CaptureFailureSelfTests.Run();
        checks += await HostSelfTests.Run();
        // Synthetic-only files are deliberately retained for review; no cleanup touches existing candidates.
        var root = Path.Combine(AppContext.BaseDirectory, "self-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var client = Path.Combine(root, "synthetic-client"); Directory.CreateDirectory(Path.Combine(client, "ZenlessZoneZero_Data"));
        File.WriteAllText(Path.Combine(client, "ZenlessZoneZero_Data", "resources.assets"), "synthetic marker, never parsed or sent");
        var service = new BridgeService(new FakeLauncher(), Path.Combine(root, "jobs"), true);
        try
        {
            Check(LaunchIdentity.ValidUri("soda-asset-quick-read://launch") && LaunchIdentity.ValidUri("soda-asset-quick-read://launch/"), "fixed_protocol_uri");
            foreach (var uri in new[] { "soda-asset-quick-read://launch?x=1", "soda-asset-quick-read://launch/#x", "soda-asset-quick-read://evil", "soda-asset-quick-read://launch/path", "SODA-ASSET-QUICK-READ://launch", "soda-asset-quick-read://launch%2f", "soda-asset-quick-read://launch --capture-worker" })
                Check(!LaunchIdentity.ValidUri(uri), "protocol_rejects_extra_input");
            Check(LaunchIdentity.SameFiles(AppContext.BaseDirectory, AppContext.BaseDirectory), "same_build_identity");
            Check(!LaunchIdentity.SameFiles(AppContext.BaseDirectory, root), "foreign_build_identity");
            var pipeName = "SodaTerminal.AssetQuickRead.Test." + Guid.NewGuid().ToString("N");
            using (var pipeLifetime = new CancellationTokenSource())
            {
                var identity = LaunchIdentity.IdentityServer(pipeLifetime.Token, pipeName);
                Check(await LaunchIdentity.TrustedPipeOwner(pipeName), "os_pipe_owner_same_build");
                Check(await LaunchIdentity.TrustedPipeOwner(pipeName), "repeated_identity_open");
                pipeLifetime.Cancel(); try { await identity; } catch (OperationCanceledException) { }
            }
            Check(!await LaunchIdentity.TrustedPipeOwner("SodaTerminal.AssetQuickRead.Missing." + Guid.NewGuid().ToString("N")), "unknown_identity_rejected");
            // Ephemeral loopback only: verifies the real non-HTTP.sys transport without
            // touching the user's existing fixed-port service or starting any capture.
            var webBuilder = WebApplication.CreateSlimBuilder(); webBuilder.Logging.ClearProviders();
            webBuilder.WebHost.UseUrls("http://127.0.0.1:0");
            await using (var web = webBuilder.Build())
            {
                var foreignHealth = false;
                web.Run(context => foreignHealth ? context.Response.WriteAsJsonAsync(new { service = "unknown-service", version = "1.0.0", protocolVersion = 1 }) : Program.Handle(context, service));
                await web.StartAsync();
                var address = web.Urls.Single(); var testPort = new Uri(address).Port;
                using var webIdentityLifetime = new CancellationTokenSource();
                var webPipeName = "SodaTerminal.AssetQuickRead.WebTest." + Guid.NewGuid().ToString("N");
                var webIdentity = LaunchIdentity.IdentityServer(webIdentityLifetime.Token, webPipeName);
                try
                {
                    Check(await LaunchIdentity.TrustedExisting(testPort, webPipeName), "ordinary_socket_health_os_identity");
                    Check(await LaunchIdentity.TrustedExisting(testPort, webPipeName), "duplicate_open_existing_service");
                    using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
                    Check((int)(await http.GetAsync(address + "/health")).StatusCode == 403, "transport_host_rejected");
                    http.DefaultRequestHeaders.Host = "127.0.0.1:50609";
                    http.DefaultRequestHeaders.Add("Origin", "https://evil.example");
                    Check((int)(await http.GetAsync(address + "/health")).StatusCode == 403, "transport_origin_rejected");
                    http.DefaultRequestHeaders.Remove("Origin");
                    Check((int)(await http.GetAsync(address + "/health?x=1")).StatusCode == 400, "transport_query_rejected");
                    Check((int)(await http.GetAsync(address + "/health")).StatusCode == 200, "transport_no_origin_health");
                    foreignHealth = true;
                    Check(!await LaunchIdentity.TrustedExisting(testPort, webPipeName), "foreign_health_service_rejected");
                }
                finally { webIdentityLifetime.Cancel(); try { await webIdentity; } catch (OperationCanceledException) { } await web.StopAsync(); }
            }
            Check(Security.ValidHost("127.0.0.1:50609"), "host_exact");
            Check(ServiceReadiness.CandidatePorts(Program.Port).SequenceEqual([50609, 46009, 46010]) &&
                ServiceReadiness.CandidatePorts(46011).SequenceEqual([46011]), "fixed_fallback_ports_and_isolated_test_boundary");
            Check(Security.ValidHost("127.0.0.1:46009") && Security.ValidHost("127.0.0.1:46010") &&
                !Security.ValidHost("127.0.0.1:46011"), "only_fixed_fallback_hosts_allowed");
            Check(!Security.ValidHost("localhost:50609") && !Security.ValidHost("evil.test:50609") && !Security.ValidHost("127.0.0.1:50609.evil"), "host_rebinding");
            Check(Security.ValidOrigin(Origin) && Security.ValidOrigin("https://sodaterminal.com"), "origin_allowlist");
            Check(Security.ValidOrigin("http://127.0.0.1:5173") && !Security.ValidOrigin("http://127.0.0.1:5174") && !Security.ValidOrigin("http://127.0.0.1:5173/"), "alternate_dev_origin_exact");
            Check(!Security.ValidOrigin("https://sodaterminal.com.evil") && !Security.ValidOrigin("null") && !Security.ValidOrigin("http://127.0.0.1:5199") && !Security.ValidOrigin("https://sodaterminal.com/"), "origin_exact");
            Check((await service.Dispatch("GET", "/health", null, null, [])).Status == 200, "health_no_paths");
            await Refuses(() => service.Dispatch("POST", "/connect", null, null, Empty), 403, "origin_required");
            await Refuses(() => service.Dispatch("GET", "/jobs?token=secret", Origin, null, []), 400, "query_rejected");
            await Refuses(() => service.Dispatch("DELETE", "/health", Origin, null, []), 405, "method_rejected");
            await Refuses(() => service.Dispatch("POST", "/connect", Origin, null, "{\"token\":\"x\"}"u8.ToArray()), 400, "unexpected_fields");
            await Refuses(() => service.Dispatch("POST", "/connect", Origin, null, new byte[4097]), 400, "body_cap");
            var pair = (await service.Dispatch("POST", "/connect", Origin, null, Empty)).Body;
            var token = JsonSerializer.SerializeToElement(pair, Program.Json).GetProperty("token").GetString()!; var auth = "Bearer " + token;
            Check(token.Length == 64, "token_entropy");
            await Refuses(() => service.Dispatch("POST", "/jobs", Origin, null, Empty), 401, "auth_required");
            await Refuses(() => service.Dispatch("POST", "/jobs", "https://sodaterminal.com", auth, Empty), 401, "token_origin_bound");
            await Refuses(() => service.Dispatch("POST", "/jobs", Origin, auth, Empty), 400, "explicit_risk_required");
            var body = JsonSerializer.SerializeToUtf8Bytes(new { clientDirectory = client, targetAccountId = "synthetic-account", riskAcknowledged = true }, Program.Json);
            var jobReply = await service.Dispatch("POST", "/jobs", Origin, auth, body); Check(jobReply.Status == 202, "start_once");
            var status = JsonSerializer.SerializeToElement(jobReply.Body, Program.Json); var id = status.GetProperty("jobId").GetGuid(); var route = "/jobs/" + id;
            Check(status.GetProperty("state").GetString() == "starting" && status.GetProperty("readyAt").ValueKind == JsonValueKind.Null && status.GetProperty("remainingSeconds").ValueKind == JsonValueKind.Null, "clock_waits_for_driver");
            await Refuses(() => service.Dispatch("POST", "/jobs", Origin, auth, body), 409, "parallel_start_blocked");
            var secondPair = JsonSerializer.SerializeToElement((await service.Dispatch("POST", "/connect", Origin, null, Empty)).Body, Program.Json);
            await Refuses(() => service.Dispatch("GET", route, Origin, "Bearer " + secondPair.GetProperty("token").GetString(), []), 403, "job_session_owned");
            await Refuses(() => service.Dispatch("GET", route + "/result", Origin, auth, []), 409, "early_result_blocked");
            await Refuses(() => service.Dispatch("GET", "/jobs/not-a-guid", Origin, auth, []), 404, "job_id_validated");
            await Refuses(() => service.Dispatch("POST", "/connect", Origin, null, "{\"x\":1,\"x\":2}"u8.ToArray()), 400, "duplicate_body_fields");
            Check(Security.ValidateClient(client) == Path.GetFullPath(client), "synthetic_install_path");
            await Refuses(() => Task.Run(() => Security.ValidateClient("relative")), 400, "relative_path_blocked");
            await Refuses(() => Task.Run(() => Security.ValidateClient(@"\\server\share")), 400, "unc_path_blocked");
            var dir = Security.JobDirectory(id, Path.Combine(root, "jobs"));
            var progress = CaptureWorker.OnStderr("status:cn_public_metadata_derived", new("starting", "public_metadata", null));
            Check(progress.State == "starting" && progress.ReadyAt is null, "metadata_no_timer");
            progress = CaptureWorker.OnStderr("status:waiting_for_normal_game_login", progress);
            Check(progress.State == "capturing" && progress.ReadyAt is not null, "driver_ready_timer");
            Check(CaptureWorker.OnStderr("secret UID or seed=arbitrary", progress) == progress, "stderr_private_discarded");
            Check(CaptureWorker.OnStderr("error:C:\\private\\account", progress) == progress, "unsafe_error_discarded");
            var output = Path.Combine(dir, "asset-snapshot.json"); Security.WriteNew(output, Fixture());
            var final = CaptureWorker.Finish(2, progress, output, false); Check(final.State == "received" && final.Code == "response_semantics_unverified", "exit2_candidate_preserved");
            Check(CaptureWorker.Finish(2, progress, output, true).State == "stopped", "cancel_blocks_candidate");
            CaptureWorker.WriteProgress(dir, final);
            var result = await service.Dispatch("GET", route + "/result", Origin, auth, []);
            var resultJson = JsonSerializer.SerializeToElement(result.Body, Program.Json);
            Check(resultJson.GetProperty("targetAccountId").GetString() == "synthetic-account" && resultJson.GetProperty("sha256").GetString()!.Length == 64, "result_account_and_hash");
            await Refuses(() => service.Dispatch("GET", route + "/result", null, auth, []), 403, "result_origin_required");
            var invalid = Path.Combine(root, "incomplete.json"); Security.WriteNew(invalid, Fixture(all: false));
            Check(CaptureWorker.Finish(2, progress, invalid, false).State == "error", "coverage_gate");
            var issue = Path.Combine(root, "issue.json"); Security.WriteNew(issue, Fixture(issues: [new { code = "synthetic_issue" }]));
            Check(CaptureWorker.Finish(2, progress, issue, false).State == "error", "issues_gate");
            var timeout = Path.Combine(root, "timeout.json"); Security.WriteNew(timeout, new { stop_reason = "timed_out" });
            Check(CaptureWorker.Finish(2, progress, timeout, false).State == "timed_out", "timeout_state");
            var refusedOverwrite = false;
            try { Security.WriteNew(output, Fixture()); } catch (IOException) { refusedOverwrite = true; }
            Check(refusedOverwrite, "create_new_preserves_candidate");
            // A separate active synthetic job exercises cancellation and post-close token revocation.
            var tokenObj = JsonSerializer.SerializeToElement((await service.Dispatch("POST", "/connect", Origin, null, Empty)).Body, Program.Json);
            var nextAuth = "Bearer " + tokenObj.GetProperty("token").GetString();
            var nextBody = JsonSerializer.SerializeToUtf8Bytes(new { clientDirectory = client, targetAccountId = "synthetic-next", riskAcknowledged = true }, Program.Json);
            var active = JsonSerializer.SerializeToElement((await service.Dispatch("POST", "/jobs", Origin, nextAuth, nextBody)).Body, Program.Json);
            var nextRoute = "/jobs/" + active.GetProperty("jobId").GetGuid();
            var cancelled = JsonSerializer.SerializeToElement((await service.Dispatch("POST", nextRoute + "/cancel", Origin, nextAuth, Empty)).Body, Program.Json);
            Check(cancelled.GetProperty("state").GetString() == "stopped", "cancelled_job");
            await Refuses(() => service.Dispatch("GET", nextRoute + "/result", Origin, nextAuth, []), 409, "cancel_result_blocked");
            service.StopOwnedJobs();
            await Refuses(() => service.Dispatch("GET", nextRoute, Origin, nextAuth, []), 401, "tokens_revoked_on_close");
            var delayed = new DelayedLauncher();
            var pendingRoot = Path.Combine(root, "pending-uac-jobs");
            var pendingService = new BridgeService(delayed, pendingRoot, true);
            var pendingToken = JsonSerializer.SerializeToElement((await pendingService.Dispatch("POST", "/connect", Origin, null, Empty)).Body, Program.Json).GetProperty("token").GetString();
            var pendingAuth = "Bearer " + pendingToken;
            var pendingStart = pendingService.Dispatch("POST", "/jobs", Origin, pendingAuth, nextBody);
            Check(pendingStart.IsCompletedSuccessfully, "start_returns_before_uac_completes");
            var pendingId = JsonSerializer.SerializeToElement((await pendingStart).Body, Program.Json).GetProperty("jobId").GetGuid();
            var pendingRoute = "/jobs/" + pendingId;
            await pendingService.Dispatch("POST", pendingRoute + "/cancel", Origin, pendingAuth, Empty);
            delayed.Pending.SetException(new ApiFault(500, "synthetic_uac_failure"));
            await Task.Delay(10);
            CaptureWorker.WriteProgress(Security.JobDirectory(pendingId, pendingRoot), new("capturing", "capture", null, DateTimeOffset.UtcNow));
            var afterCancel = JsonSerializer.SerializeToElement((await pendingService.Dispatch("GET", pendingRoute, Origin, pendingAuth, [])).Body, Program.Json);
            Check(afterCancel.GetProperty("state").GetString() == "stopped", "cancel_stays_terminal_after_delayed_uac_or_progress");
            pendingService.StopOwnedJobs();
            // An NTFS junction needs no elevation; all synthetic paths stay within this isolated test directory.
            var junction = Path.Combine(root, "synthetic-junction");
            var junctionInfo = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            junctionInfo.ArgumentList.Add("/c"); junctionInfo.ArgumentList.Add("mklink"); junctionInfo.ArgumentList.Add("/J"); junctionInfo.ArgumentList.Add(junction); junctionInfo.ArgumentList.Add(client);
            using (var junctionProcess = System.Diagnostics.Process.Start(junctionInfo)!)
            {
                await junctionProcess.WaitForExitAsync();
                Check(junctionProcess.ExitCode == 0, "synthetic_junction_created_without_uac");
            }
            await Refuses(() => Task.Run(() => Security.ValidateClient(junction)), 400, "linked_install_rejected");
            Console.WriteLine(JsonSerializer.Serialize(new { suite = "asset-quick-read-bridge", passed = checks, captureStarted = false, externalNetworkRequests = 0, syntheticLoopbackRequests = 7, uacRequested = false }, Program.Json));
            return 0;
        }
        finally { service.StopOwnedJobs(); }
    }
}
