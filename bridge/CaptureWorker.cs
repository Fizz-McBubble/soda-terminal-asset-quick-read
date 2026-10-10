using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace Soda.AssetQuickRead;

internal static class CaptureWorker
{
    internal static bool SafePhase(string phase) => phase is "uac" or "launch" or "preflight" or "public_metadata" or "driver" or "capture" or "validation" or "complete" or "cancel";
    internal static bool SafeCode(string? code) => code is null || Regex.IsMatch(code, "\\A[a-z_]{1,100}(?::[0-9]{1,10})?\\z", RegexOptions.CultureInvariant);
    internal static Progress OnStderr(string line, Progress previous)
    {
        if (line == "status:cn_public_metadata_derived") return new("starting", "driver", null);
        // The pinned Rust emits this only after Capture::open succeeds; bootstrap is excluded from this clock.
        if (line == "status:waiting_for_normal_game_login") return new("capturing", "capture", null, DateTimeOffset.UtcNow);
        if (line == "status:login_handshake_observed") return previous;
        if (line.StartsWith("error:", StringComparison.Ordinal) && SafeCode(line[6..]))
        {
            var code = line[6..];
            var phase = previous.ReadyAt is not null ? "capture" : code.StartsWith("capture_", StringComparison.Ordinal) || code.StartsWith("dll_", StringComparison.Ordinal) ? "driver" : "public_metadata";
            return new("error", phase, code, previous.ReadyAt);
        }
        return previous;
    }
    internal static Progress Finish(int exitCode, Progress prior, string output, bool cancelled)
    {
        if (cancelled) return new("stopped", "cancel", "cancelled", prior.ReadyAt);
        if (exitCode is 0 or 2 && File.Exists(output))
        {
            try
            {
                var candidate = Snapshot.Read(output);
                return new("received", "complete", candidate.Document.GetProperty("response_status_semantics_verified").GetBoolean() ? "staged_for_field_review" : "response_semantics_unverified", prior.ReadyAt, candidate.Counts);
            }
            catch (ApiFault error)
            {
                try
                {
                    Security.RejectLinks(output);
                    if (new FileInfo(output).Length > Snapshot.MaxBytes) throw new ApiFault(409, "candidate_size_invalid");
                    using var json = JsonDocument.Parse(File.ReadAllBytes(output));
                    if (json.RootElement.TryGetProperty("stop_reason", out var reason) && reason.GetString() == "timed_out") return new("timed_out", "capture", "timed_out", prior.ReadyAt);
                }
                catch { }
                return new("error", "validation", error.Code, prior.ReadyAt);
            }
        }
        return prior.State == "error" ? prior : new("error", prior.Phase, "capture_child_failed", prior.ReadyAt);
    }
    internal static async Task<int> Run(Guid id)
    {
        var directory = Security.JobDirectory(id);
        Progress current = new("starting", "preflight", null);
        Process? child = null;
        var leaseHeld = false;
        // Named semaphores are not thread-affine: async continuations may release on a different thread.
        using var lease = new Semaphore(1, 1, @"Local\SodaTerminal.AssetQuickRead.Capture.v1");
        try
        {
            if (!OperatingSystem.IsWindows() || !new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)) throw new ApiFault(403, "worker_elevation_required");
            Security.RejectLinks(directory);
            var requestFile = Path.Combine(directory, "request.json"); Security.RejectLinks(requestFile);
            if (new FileInfo(requestFile).Length > Security.BodyLimit) throw new ApiFault(400, "worker_request_invalid");
            var request = JsonSerializer.Deserialize<WorkerRequest>(File.ReadAllBytes(requestFile), Program.Json) ?? throw new ApiFault(400, "worker_request_invalid");
            if (DateTimeOffset.UtcNow - File.GetCreationTimeUtc(requestFile) > TimeSpan.FromMinutes(5)) throw new ApiFault(400, "worker_request_expired");
            if (!ParentAlive(request) || Cancelled(directory)) { WriteProgress(directory, new("stopped", "cancel", "cancelled")); return 0; }
            leaseHeld = lease.WaitOne(0);
            if (!leaseHeld) throw new ApiFault(409, "capture_already_active");
            var client = Security.ValidateClient(request.ClientDirectory);
            Security.VerifyRuntime(Security.RuntimeDirectory);
            Security.RequireGameClosed();
            var other = Process.GetProcessesByName("soda-asset-capture");
            try { if (other.Length != 0) throw new ApiFault(409, "capture_already_active"); }
            finally { foreach (var p in other) p.Dispose(); }
            var output = Path.Combine(directory, "asset-snapshot.json");
            if (File.Exists(output)) throw new ApiFault(409, "candidate_output_already_exists");
            current = new("starting", "public_metadata", null); WriteProgress(directory, current);
            if (!ParentAlive(request) || Cancelled(directory)) { WriteProgress(directory, new("stopped", "cancel", "cancelled")); return 0; }
            var info = new ProcessStartInfo(Path.Combine(Security.RuntimeDirectory, "soda-asset-capture.exe")) { WorkingDirectory = Security.RuntimeDirectory, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "capture", "--seconds", "180", "--client-resources", Path.Combine(client, "ZenlessZoneZero_Data", "resources.assets"), "--out", output, "--game-region", "CN", "--account-label", "本地验证" }) info.ArgumentList.Add(arg);
            child = Process.Start(info) ?? throw new ApiFault(500, "capture_child_start_failed");
            var gate = new object();
            var stderr = Task.Run(async () =>
            {
                while (await child.StandardError.ReadLineAsync() is { } line)
                {
                    // Never persist or forward arbitrary child stderr/stdout, account names, seed values or payloads.
                    lock (gate) { var next = OnStderr(line, current); if (next != current) { current = next; WriteProgress(directory, current); } }
                }
            });
            var stdout = Task.Run(async () => { var buffer = new char[2048]; while (await child.StandardOutput.ReadAsync(buffer) != 0) { } });
            var started = DateTimeOffset.UtcNow; var cancelled = false; var watchdog = false;
            while (!child.HasExited)
            {
                DateTimeOffset? ready; lock (gate) ready = current.ReadyAt;
                cancelled = Cancelled(directory) || !ParentAlive(request);
                watchdog = ready is null ? DateTimeOffset.UtcNow - started > TimeSpan.FromSeconds(90) : DateTimeOffset.UtcNow > ready.Value.AddSeconds(195);
                if (cancelled || watchdog) { child.Kill(entireProcessTree: true); break; }
                await Task.Delay(100);
            }
            await child.WaitForExitAsync(); await Task.WhenAll(stderr, stdout);
            lock (gate)
            {
                current = watchdog && !cancelled ? new("error", current.ReadyAt is null ? "public_metadata" : "capture", "capture_watchdog_timeout", current.ReadyAt) : Finish(child.ExitCode, current, output, cancelled);
                WriteProgress(directory, current);
            }
            return current.State == "error" ? 1 : 0;
        }
        catch (ApiFault fault) { try { WriteProgress(directory, new("error", current.Phase, fault.Code, current.ReadyAt)); } catch { } return 1; }
        catch { try { WriteProgress(directory, new("error", current.Phase, "worker_failed", current.ReadyAt)); } catch { } return 1; }
        finally
        {
            // Terminate only the process launched by this worker; never stop or uninstall a shared driver.
            if (child is not null) { try { if (!child.HasExited) child.Kill(entireProcessTree: true); } catch { } child.Dispose(); }
            if (leaseHeld) lease.Release();
        }
    }
    private static bool Cancelled(string directory) => File.Exists(Path.Combine(directory, "cancel"));
    private static bool ParentAlive(WorkerRequest request)
    {
        try { using var parent = Process.GetProcessById(request.ParentProcessId); return !parent.HasExited && parent.StartTime.ToUniversalTime().Ticks == request.ParentStartTicks; }
        catch { return false; }
    }
    internal static void WriteProgress(string directory, Progress progress)
    {
        Security.RejectLinks(directory);
        var path = Path.Combine(directory, "progress.json"); if (File.Exists(path)) Security.RejectLinks(path);
        var temporary = Path.Combine(directory, "progress-" + Guid.NewGuid().ToString("N") + ".tmp");
        Security.WriteNew(temporary, progress);
        File.Move(temporary, path, true);
    }
}
