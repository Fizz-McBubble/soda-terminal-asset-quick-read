using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
namespace Soda.AssetQuickRead;

internal static class HostSelfTests
{
    private const string FixtureVariable = "SODA_ASSET_QUICK_READ_HOST_SELF_TEST";
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();
    internal static async Task<int?> RunChild()
    {
        var fixture = Environment.GetEnvironmentVariable(FixtureVariable);
        if (fixture is null) return null;
        var parts = fixture.Split(':');
        if (parts.Length != 3 || !int.TryParse(parts[1], out var port) || port is < 1024 or > 65535 || !Guid.TryParseExact(parts[2], "N", out var id)) return 64;
        // Never run a capture worker or expose /connect or /jobs in this mode.
        if (parts[0] == "unready") { await Task.Delay(15000); return 0; }
        if (parts[0] == "exit-zero") return 0;
        if (parts[0] == "health-only" && GetConsoleWindow() == IntPtr.Zero)
        {
            using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            return await Program.RunHost(port, Pipe(id), Mutex(id), healthOnly: true, cancellation: lifetime.Token);
        }
        if (parts[0].StartsWith("legacy-") && GetConsoleWindow() == IntPtr.Zero)
        {
            var legacyVersion = parts[0]["legacy-".Length..];
            using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            return await RunLegacyHost(legacyVersion, port, Pipe(id), Mutex(id), lifetime.Token);
        }
        return 65;
    }
    private static async Task<int> RunLegacyHost(string version, int port, string pipeName, string mutexName, CancellationToken cancellation)
    {
        using var instance = new Mutex(true, mutexName, out var created);
        if (!created) return 73;
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        var pipeLoop = Task.Run(async () =>
        {
            while (!cancellation.IsCancellationRequested)
            {
                try
                {
                    using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await pipe.WaitForConnectionAsync(cancellation);
                    await pipe.WriteAsync(System.Text.Encoding.UTF8.GetBytes("soda-asset-quick-read/" + version), cancellation);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return; }
                catch (IOException) { try { await Task.Delay(50, cancellation); } catch { return; } }
            }
        }, cancellation);

        var httpLoop = Task.Run(async () =>
        {
            while (!cancellation.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(cancellation); }
                catch { break; }
                _ = Task.Run(async () =>
                {
                    using (client)
                    using (var stream = client.GetStream())
                    {
                        var buffer = new byte[1024];
                        var read = await stream.ReadAsync(buffer, cancellation);
                        var req = System.Text.Encoding.ASCII.GetString(buffer, 0, read);
                        if (req.StartsWith("GET /health"))
                        {
                            var json = $"{{\"service\":\"soda-asset-quick-read\",\"version\":\"{version}\",\"protocolVersion\":1,\"capabilities\":[\"asset_snapshot\",\"explicit_uac_start\",\"cancel\"],\"state\":\"idle\"}}";
                            var bodyBytes = System.Text.Encoding.UTF8.GetBytes(json);
                            var header = $"HTTP/1.1 200 OK\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n";
                            var headerBytes = System.Text.Encoding.ASCII.GetBytes(header);
                            await stream.WriteAsync(headerBytes, cancellation);
                            await stream.WriteAsync(bodyBytes, cancellation);
                        }
                        else
                        {
                            var header = "HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                            await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(header), cancellation);
                        }
                    }
                }, cancellation);
            }
        }, cancellation);

        try
        {
            await Task.Delay(Timeout.Infinite, cancellation);
        }
        catch (OperationCanceledException) { }
        finally
        {
            listener.Stop();
            try { await pipeLoop; } catch { }
            try { await httpLoop; } catch { }
        }
        return 0;
    }
    private static string Pipe(Guid id) => "SodaTerminal.AssetQuickRead.HostSelfTest." + id.ToString("N");
    private static string Mutex(Guid id) => "Local\\SodaTerminal.AssetQuickRead.HostSelfTest." + id.ToString("N");
    internal static async Task<int> Run()
    {
        var checks = 0;
        void Check(bool value, string name) { if (!value) throw new Exception("host_test_failed:" + name); checks++; }
        var self = Path.Combine(AppContext.BaseDirectory, "Soda.AssetQuickRead.Bridge.exe");
        using (var exe = new BinaryReader(File.OpenRead(self)))
        {
            exe.BaseStream.Position = 0x3c; var pe = exe.ReadInt32();
            exe.BaseStream.Position = pe + 92;
            Check(exe.ReadUInt16() == 2, "windows_gui_subsystem");
        }
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var id = Guid.NewGuid();
        ProcessStartInfo Start(string mode) {
            var start = new ProcessStartInfo(self, "--self-test") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = AppContext.BaseDirectory };
            start.Environment[FixtureVariable] = mode + ":" + port + ":" + id.ToString("N"); return start;
        }
        int? ownedPid = null; var fixtures = new List<(int Pid, long Ticks)>();
        Process? Launch(ProcessStartInfo info) { var process = Process.Start(info); ownedPid = process?.Id; if (process is not null) fixtures.Add((process.Id, process.StartTime.ToUniversalTime().Ticks)); return process; }
        async Task StopFixture()
        {
            if (ownedPid is not { } pid) return;
            try { using var process = Process.GetProcessById(pid); if (!process.HasExited && fixtures.Any(f => f.Pid == pid && f.Ticks == process.StartTime.ToUniversalTime().Ticks)) { process.Kill(); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(4)); } }
            catch (ArgumentException) { }
            ownedPid = null;
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (ServiceReadiness.PortInUse(port) && DateTime.UtcNow < deadline) await Task.Delay(25);
        }
        try
        {
            await ServiceReadiness.Start(Start("health-only"), LaunchIdentity.Version, port, Pipe(id), launch: Launch);
            Check(ownedPid is not null && await LaunchIdentity.TrustedExisting(port, Pipe(id)), "actual_executable_ready");
            using (var process = Process.GetProcessById(ownedPid!.Value)) Check(!process.HasExited && process.MainWindowHandle == IntPtr.Zero, "persistent_windowless_host");
            var originalPid = ownedPid;
            await ServiceReadiness.Start(Start("health-only"), LaunchIdentity.Version, port, Pipe(id), launch: _ => throw new Exception("duplicate_spawn"));
            Check(ownedPid == originalPid, "repeat_start_reuses_owned_ready_service");
            using (var duplicate = Process.Start(Start("health-only"))!)
            {
                await duplicate.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                Check(duplicate.ExitCode == 0, "duplicate_executable_exit_zero");
            }
            Check(await LaunchIdentity.TrustedExisting(port, Pipe(id)), "original_service_survives_duplicate");
            Check(!await ServiceReadiness.TrustedExisting(AppContext.BaseDirectory, "0.0.0", port, Pipe(id)), "wrong_version_rejected");
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
            http.DefaultRequestHeaders.Host = "127.0.0.1:50609";
            using var response = await http.PostAsync($"http://127.0.0.1:{port}/jobs", new StringContent("{}"));
            Check(response.StatusCode == HttpStatusCode.Forbidden, "fixture_cannot_capture");
            await StopFixture();

            // Legacy bridge coexistence, user confirmation, race rejection, and switch tests
            string selfExeHash, selfDllHash;
            using (var s = File.OpenRead(self)) selfExeHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(s));
            var dllPath = Path.Combine(AppContext.BaseDirectory, "Soda.AssetQuickRead.Bridge.dll");
            using (var s = File.OpenRead(dllPath)) selfDllHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(s));

            var installationRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", ".."));
            var contract = new ServiceReadiness.IsolatedTestContract(installationRoot, port, Pipe(id), selfExeHash, selfDllHash);
            Check(Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar) == Path.Combine(installationRoot, "versions", "1.0.7"), "isolated_versioned_root");
            using var legacyProcess = Launch(Start("legacy-1.0.3"))!;
            var legacyPid = legacyProcess.Id;
            var readyDeadline = DateTime.UtcNow.AddSeconds(5);
            ServiceReadiness.VerifiedOldBridge? inspected = null;
            while (DateTime.UtcNow < readyDeadline)
            {
                inspected = await ServiceReadiness.InspectOldBridge(port, Pipe(id), test: contract);
                if (inspected is not null) break;
                await Task.Delay(50);
            }
            Check(inspected is not null && inspected.Version == "1.0.3" && inspected.ProcessId == legacyPid, "accurate_old_version_detected");
            Check(await ServiceReadiness.InspectOldBridge(port, Pipe(id)) is null, "production_does_not_accept_test_identity");
            var autoStartFailed = false;
            try { await ServiceReadiness.Start(Start("health-only"), LaunchIdentity.Version, port, Pipe(id), TimeSpan.FromMilliseconds(500), Launch); }
            catch (IOException) { autoStartFailed = true; }
            Check(autoStartFailed && !legacyProcess.HasExited, "before_confirmation_zero_old_termination");
            using var oldResponse = await http.PostAsync($"http://127.0.0.1:{port}/jobs", new StringContent("{}"));
            Check(oldResponse.StatusCode == HttpStatusCode.Forbidden, "legacy_fixture_cannot_capture");
            Check(!ServiceReadiness.MatchesPublishedBridgePin("0.0.0", selfExeHash, selfDllHash) &&
                !ServiceReadiness.MatchesPublishedBridgePin("1.0.3", selfExeHash, selfDllHash) &&
                !ServiceReadiness.MatchesPublishedBridgePin("1.0.4", selfExeHash, selfDllHash), "test_pins_never_become_production_pins");
            Check(ServiceReadiness.MatchesPublishedBridgePin("1.0.4", "7C4D4D02FCE0A3D1A9C219F712EADD43DBFBCF5DB297964C26C5641574C70621", "76E549E8E5542C20E519E3FCB2CB2725B8A3AD918E4D6BC101FD93AB53D9BDC6"), "bridge_104_exact_pin_verified");
            Check(!ServiceReadiness.MatchesPublishedBridgePin("1.0.4", "0000000000000000000000000000000000000000000000000000000000000000", "76E549E8E5542C20E519E3FCB2CB2725B8A3AD918E4D6BC101FD93AB53D9BDC6"), "bridge_104_corrupted_exe_refused");
            Check(!ServiceReadiness.MatchesPublishedBridgePin("1.0.5", "7C4D4D02FCE0A3D1A9C219F712EADD43DBFBCF5DB297964C26C5641574C70621", "76E549E8E5542C20E519E3FCB2CB2725B8A3AD918E4D6BC101FD93AB53D9BDC6"), "bridge_unknown_future_version_refused");
            foreach (var altered in new[] {
                inspected! with { StartTimeTicks = inspected.StartTimeTicks + 1 },
                inspected with { ProcessId = Environment.ProcessId },
                inspected with { Directory = installationRoot },
                inspected with { Version = "1.0.2" },
                inspected with { PipeName = Pipe(Guid.NewGuid()) } })
            {
                Check(!await ServiceReadiness.RecheckAndTerminate(altered, test: contract), "forged_identity_refused");
                Check(!legacyProcess.HasExited, "forged_identity_keeps_old_process");
            }
            var foreign = new ServiceReadiness.IsolatedTestContract(Path.Combine(installationRoot, "foreign"), port, Pipe(id), selfExeHash, selfDllHash);
            Check(await ServiceReadiness.InspectOldBridge(port, Pipe(id), test: foreign) is null &&
                !await ServiceReadiness.RecheckAndTerminate(inspected!, test: foreign) && !legacyProcess.HasExited, "foreign_root_even_exact_bytes_preserved");
            var wrongPins = new ServiceReadiness.IsolatedTestContract(installationRoot, port, Pipe(id), new string('0', 64), selfDllHash);
            Check(await ServiceReadiness.InspectOldBridge(port, Pipe(id), test: wrongPins) is null &&
                !await ServiceReadiness.RecheckAndTerminate(inspected!, test: wrongPins) && !legacyProcess.HasExited, "wrong_pins_preserved");
            var junction = Path.Combine(installationRoot, "linked-root-" + Guid.NewGuid().ToString("N"));
            var linkStart = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "/c", "mklink", "/J", junction, installationRoot }) linkStart.ArgumentList.Add(arg);
            using (var link = Process.Start(linkStart)!) { await link.WaitForExitAsync(); Check(link.ExitCode == 0, "isolated_junction_created"); }
            var linked = new ServiceReadiness.IsolatedTestContract(junction, port, Pipe(id), selfExeHash, selfDllHash);
            var linkedRecord = inspected! with { Directory = Path.Combine(junction, "versions", "1.0.7"), ExecutablePath = Path.Combine(junction, "versions", "1.0.7", "Soda.AssetQuickRead.Bridge.exe") };
            Check(await ServiceReadiness.InspectOldBridge(port, Pipe(id), test: linked) is null &&
                !await ServiceReadiness.RecheckAndTerminate(linkedRecord, test: linked) && !legacyProcess.HasExited, "linked_root_refused_and_preserved");
            await ServiceReadiness.SwitchService(inspected!, Start("health-only"), LaunchIdentity.Version, TimeSpan.FromSeconds(5), Launch, test: contract);
            Check(legacyProcess.HasExited, "only_verified_old_process_terminated_after_confirmation");
            Check(ownedPid is not null && ownedPid != legacyPid && await LaunchIdentity.TrustedExisting(port, Pipe(id)), "new_104_healthy_after_switch");
            await StopFixture();
            foreach (var mode in new[] { "unready", "exit-zero" })
            {
                var elapsed = Stopwatch.StartNew();
                try { await ServiceReadiness.Start(Start(mode), LaunchIdentity.Version, port, Pipe(id), TimeSpan.FromMilliseconds(850), Launch); throw new Exception("unready_host_accepted"); }
                catch (IOException error) { Check(error.Message == "local_service_readiness_timeout", mode + "_not_ready"); }
                Check(elapsed.Elapsed < TimeSpan.FromSeconds(3), "bounded_readiness_" + mode);
                await StopFixture();
            }
            return checks;
        }
        catch (Exception error) { Console.Error.WriteLine("isolated_host_test_error:" + error.Message); throw; }
        finally
        {
            // Only exact processes created by this isolated fixture may be stopped.
            foreach (var fixture in fixtures)
            {
                try { using var child = Process.GetProcessById(fixture.Pid); if (!child.HasExited && child.StartTime.ToUniversalTime().Ticks == fixture.Ticks) { child.Kill(); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(4)); } }
                catch (ArgumentException) { }
            }
        }
    }
}
