using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
namespace Soda.AssetQuickRead;

internal interface IWorkerLauncher { Task Start(Guid id, string directory); }
internal sealed class ElevatedLauncher : IWorkerLauncher
{
    public Task Start(Guid id, string directory) => Task.Run(() =>
    {
        var self = Path.Combine(AppContext.BaseDirectory, "Soda.AssetQuickRead.Bridge.exe");
        if (!File.Exists(self)) throw new ApiFault(409, "bridge_apphost_missing");
        var info = new ProcessStartInfo(self) { UseShellExecute = true, Verb = "runas", WorkingDirectory = AppContext.BaseDirectory, WindowStyle = ProcessWindowStyle.Hidden };
        info.Arguments = "--capture-worker " + id.ToString("D");
        using var process = Process.Start(info) ?? throw new ApiFault(500, "worker_launch_failed");
    });
}
internal sealed record Pairing(string Origin, DateTimeOffset ExpiresAt);
internal sealed record WorkerRequest(string ClientDirectory, int ParentProcessId, long ParentStartTicks);
internal sealed record SafeCounts(int Discs = 0, int Engines = 0, int Agents = 0);
internal sealed record Progress(string State, string Phase, string? Code, DateTimeOffset? ReadyAt = null, SafeCounts? Counts = null, uint? NativeErrorCode = null, uint? ChildExitCode = null);
internal sealed class Job(Guid id, string owner, string target, string directory)
{
    internal readonly Guid Id = id;
    internal readonly string Owner = owner;
    internal readonly string Target = target;
    internal readonly string Directory = directory;
    internal readonly DateTimeOffset StartedAt = DateTimeOffset.UtcNow;
    internal Progress Progress = new("starting", "uac", null);
    internal bool Cancelled;
}
internal sealed class BridgeService(IWorkerLauncher launcher, string? testRoot = null, bool synthetic = false)
{
    private readonly object gate = new();
    private readonly Dictionary<string, Pairing> tokens = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, Job> jobs = new();
    internal Task<Reply> Dispatch(string method, string path, string? origin, string? authorization, byte[] body) => Task.FromResult(DispatchCore(method, path, origin, authorization, body));
    private Reply DispatchCore(string method, string path, string? origin, string? authorization, byte[] body)
    {
        if (method is not ("GET" or "POST")) throw new ApiFault(405, "method_rejected");
        if (path.Contains('?') || path.Contains('%')) throw new ApiFault(400, "query_or_encoding_rejected");
        if (origin is not null && !Security.ValidOrigin(origin)) throw new ApiFault(403, "origin_rejected");
        if (method == "GET" && path == "/health") return new(200, new { service = "soda-asset-quick-read", version = LaunchIdentity.Version, protocolVersion = 1, capabilities = new[] { "asset_snapshot", "explicit_uac_start", "cancel" }, state = "idle" });
        if (!Security.ValidOrigin(origin)) throw new ApiFault(403, "origin_required");
        if (method == "POST" && path == "/connect")
        {
            Security.ParseObject(body);
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var expiry = DateTimeOffset.UtcNow.AddHours(2);
            lock (gate)
            {
                foreach (var expired in tokens.Where(t => t.Value.ExpiresAt <= DateTimeOffset.UtcNow).Select(t => t.Key).ToArray()) tokens.Remove(expired);
                if (tokens.Count >= 32) throw new ApiFault(429, "pairing_limit");
                tokens.Add(token, new(origin!, expiry));
            }
            return new(200, new { token, version = LaunchIdentity.Version, expiresAt = expiry });
        }
        var owner = Authenticate(origin!, authorization);
        if (method == "POST" && path == "/jobs")
        {
            var payload = Security.ParseObject(body, "clientDirectory", "targetAccountId", "riskAcknowledged");
            if (!payload.TryGetProperty("riskAcknowledged", out var risk) || risk.ValueKind != JsonValueKind.True) throw new ApiFault(400, "risk_acknowledgement_required");
            if (!payload.TryGetProperty("targetAccountId", out var target) || target.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(target.GetString()) || target.GetString()!.Length > 160 || target.GetString()!.Any(char.IsControl)) throw new ApiFault(400, "target_account_required");
            if (!payload.TryGetProperty("clientDirectory", out var client) || client.ValueKind != JsonValueKind.String) throw new ApiFault(400, "client_directory_required");
            var clientDirectory = Security.ValidateClient(client.GetString());
            if (!synthetic) { Security.VerifyRuntime(Security.RuntimeDirectory); Security.RequireGameClosed(); }
            Job job;
            lock (gate)
            {
                foreach (var existing in jobs.Values) Refresh(existing);
                if (jobs.Values.Any(j => j.Progress.State is "starting" or "capturing")) throw new ApiFault(409, "capture_already_active");
                if (jobs.Count >= 64) throw new ApiFault(429, "job_limit_restart_bridge");
                var id = Guid.NewGuid(); var directory = Security.JobDirectory(id, testRoot);
                Directory.CreateDirectory(Path.GetDirectoryName(directory)!);
                Security.RejectLinks(Path.GetDirectoryName(directory)!);
                if (Directory.Exists(directory)) throw new ApiFault(409, "job_directory_exists");
                Directory.CreateDirectory(directory);
                Security.RejectLinks(directory);
                using var parent = Process.GetCurrentProcess();
                Security.WriteNew(Path.Combine(directory, "request.json"), new WorkerRequest(clientDirectory, Environment.ProcessId, parent.StartTime.ToUniversalTime().Ticks));
                job = new(id, owner, target.GetString()!, directory); jobs.Add(id, job);
            }
            _ = Launch(job);
            return new(202, Status(job));
        }
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 2 or > 3 || parts[0] != "jobs" || !Guid.TryParseExact(parts[1], "D", out var jobId) || path != "/" + string.Join('/', parts)) throw new ApiFault(404, "route_not_found");
        Job owned;
        lock (gate)
        {
            if (!jobs.TryGetValue(jobId, out owned!)) throw new ApiFault(404, "job_not_found");
            if (owned.Owner != owner) throw new ApiFault(403, "job_not_owned");
            Refresh(owned);
            if (method == "GET" && parts.Length == 2) return new(200, Status(owned));
            if (method == "POST" && parts.Length == 3 && parts[2] == "cancel")
            {
                Security.ParseObject(body);
                Cancel(owned); return new(200, Status(owned));
            }
            if (method == "GET" && parts.Length == 3 && parts[2] == "result")
            {
                if (owned.Cancelled || owned.Progress.State != "received") throw new ApiFault(409, "candidate_not_available");
                var candidate = Snapshot.Read(Path.Combine(owned.Directory, "asset-snapshot.json"));
                return new(200, new { jobId = owned.Id, targetAccountId = owned.Target, snapshot = candidate.Document, sha256 = candidate.Sha256, capturedAt = candidate.CapturedAt });
            }
        }
        throw new ApiFault(405, "method_or_route_rejected");
    }
    private string Authenticate(string origin, string? authorization)
    {
        if (authorization is null || !authorization.StartsWith("Bearer ", StringComparison.Ordinal)) throw new ApiFault(401, "token_required");
        var token = authorization[7..];
        lock (gate)
            if (!tokens.TryGetValue(token, out var pair) || pair.Origin != origin || pair.ExpiresAt <= DateTimeOffset.UtcNow) throw new ApiFault(401, "token_invalid");
        return token;
    }
    private async Task Launch(Job job)
    {
        try { await launcher.Start(job.Id, job.Directory); }
        catch (System.ComponentModel.Win32Exception e) { lock (gate) if (!job.Cancelled) job.Progress = new("error", "uac", e.NativeErrorCode == 1223 ? "uac_declined" : "worker_launch_failed", NativeErrorCode: unchecked((uint)e.NativeErrorCode)); }
        catch (ApiFault e) { lock (gate) if (!job.Cancelled) job.Progress = new("error", "launch", e.Code); }
        catch { lock (gate) if (!job.Cancelled) job.Progress = new("error", "launch", "worker_launch_failed"); }
    }
    private void Refresh(Job job)
    {
        if (job.Cancelled) return;
        var path = Path.Combine(job.Directory, "progress.json");
        try
        {
            if (!File.Exists(path))
            {
                if (DateTimeOffset.UtcNow - job.StartedAt > TimeSpan.FromMinutes(5) && job.Progress.State == "starting") { Cancel(job); job.Progress = new("error", "launch", "worker_start_timeout"); }
                return;
            }
            Security.RejectLinks(path);
            if (new FileInfo(path).Length > 8192) throw new ApiFault(409, "progress_invalid");
            var progress = JsonSerializer.Deserialize<Progress>(File.ReadAllBytes(path), Program.Json) ?? throw new ApiFault(409, "progress_invalid");
            if (progress.State is not ("starting" or "capturing" or "received" or "timed_out" or "stopped" or "error") || !CaptureWorker.SafePhase(progress.Phase) || !CaptureWorker.SafeCode(progress.Code) || !CaptureFailureDetails.Valid(progress)) throw new ApiFault(409, "progress_invalid");
            if (progress.State == "received") Snapshot.Read(Path.Combine(job.Directory, "asset-snapshot.json"));
            job.Progress = progress;
            // A worker must never leave the browser looking active indefinitely after disappearing.
            if (progress.ReadyAt is not null && progress.State == "capturing" && DateTimeOffset.UtcNow > progress.ReadyAt.Value.AddSeconds(195))
            { Cancel(job); job.Progress = new("error", "capture", "worker_completion_timeout"); }
        }
        catch (IOException) { /* Atomic replacement/read contention: retain last safe status. */ }
        catch (ApiFault e) { job.Progress = new("error", "validation", e.Code); }
        catch { job.Progress = new("error", "validation", "progress_invalid"); }
    }
    private static object Status(Job job)
    {
        var ready = job.Progress.ReadyAt;
        return new { jobId = job.Id, targetAccountId = job.Target, state = job.Progress.State, phase = job.Progress.Phase, code = job.Progress.Code, startedAt = job.StartedAt, readyAt = ready, deadline = ready?.AddSeconds(180), remainingSeconds = ready is null ? (int?)null : Math.Max(0, (int)Math.Ceiling((ready.Value.AddSeconds(180) - DateTimeOffset.UtcNow).TotalSeconds)), counts = job.Progress.Counts ?? new SafeCounts(), diagnostics = job.Progress.NativeErrorCode is null && job.Progress.ChildExitCode is null ? null : new CaptureDiagnostics(job.Progress.NativeErrorCode, job.Progress.ChildExitCode), candidateAvailable = !job.Cancelled && job.Progress.State == "received" };
    }
    private static void Cancel(Job job)
    {
        if (job.Progress.State is not ("starting" or "capturing")) return;
        try { Security.WriteNew(Path.Combine(job.Directory, "cancel"), new { stop = true }); } catch (IOException) { }
        job.Cancelled = true; job.Progress = new("stopped", "cancel", "cancelled", job.Progress.ReadyAt, job.Progress.Counts);
    }
    internal void StopOwnedJobs() { lock (gate) { foreach (var job in jobs.Values) Cancel(job); tokens.Clear(); } }
}
