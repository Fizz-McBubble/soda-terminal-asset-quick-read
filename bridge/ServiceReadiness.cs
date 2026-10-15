using System.Diagnostics;
using System.IO.Pipes;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
namespace Soda.AssetQuickRead;

// Linked into the installer as well: process survival and HTTP health alone
// cannot establish ownership of the service we are about to report as ready.
internal static class ServiceReadiness
{
    // The identity pipe and mutex stay stable across ports: one installed
    // product owns one service, even when Windows excludes the legacy port.
    internal static readonly int[] ServicePorts = [50609, 46009, 46010];
    internal static int[] CandidatePorts(int port) => port == ServicePorts[0] ? ServicePorts : [port];
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);

    private static readonly IReadOnlyDictionary<string, (string ExeSha256, string DllSha256)> PublishedBridgePins =
        new Dictionary<string, (string ExeSha256, string DllSha256)>(StringComparer.Ordinal)
        {
            ["1.0.0"] = ("DC075C8AEA5497FD9ABBAA6B4E8B6C7D46C9462BCFF2FD0BD3C49A848B0C824F", "9EAF856FA997732DE0C7E8CF3320245F934BD31F5A199D5BEEDE23755E7A38FC"),
            ["1.0.1"] = ("D61B977C869607EF1C91C916D916C66F2414EDA9FF1511F77B223219EA5BFD22", "859398F869448C10AED6E4CB0D1FCD2B458AF4B120C236734E1CECF9DD3D13FC"),
            ["1.0.2"] = ("40A19313E4018247202CB31ECE1F48237FA68E6142F1D083504BE96F2A185BCA", "F04507F60A592595D570AB86F41C1A755A2420E61D2965EA5EE2C5CCD06A4448"),
            ["1.0.3"] = ("E91778191B5A067B9C02729F55414510E66FB6F42927CFA0ACFDE5D08713008B", "39AAEA7A34CBADE61D0AE797228814A01C978796212FEEA41F9765C04260706E"),
            ["1.0.4"] = ("7C4D4D02FCE0A3D1A9C219F712EADD43DBFBCF5DB297964C26C5641574C70621", "76E549E8E5542C20E519E3FCB2CB2725B8A3AD918E4D6BC101FD93AB53D9BDC6"),
            ["1.0.5"] = ("E876BA58EF0AD1FCDD8D0EA086456AA0257A26AA65CBD5438A4794371DB85361", "E1BA0B98936B3563D911026459FCE2C60B23328B3E3E23BDBF32CA5B73CFA4AB"),
            ["1.0.6"] = ("DBF5294530FB807199480A91821F70E693E63416182682E4BCF87A1E7D015C4B", "9F86BF559DC891CDAFB67FB10C4A083699FCA88A8DD73CFBAC529A5B3D61A37F"),
        };

    internal static bool MatchesPublishedBridgePin(string version, string exeSha256, string dllSha256)
    {
        if (PublishedBridgePins.TryGetValue(version, out var pin))
        {
            return string.Equals(pin.ExeSha256, exeSha256, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(pin.DllSha256, dllSha256, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    private static string DefaultRoot => Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SodaTerminal", "AssetQuickRead"));
    private static string? PublishedBridgeVersion(string installationVersion) => installationVersion switch
    {
        "1.0.0" or "1.0.1" or "1.0.2" => installationVersion,
        "1.0.3" or "1.0.4" or "1.0.5" or "1.0.6" or "1.0.7" => "1.0.3",
        "1.0.8" or "1.0.9" => "1.0.4",
        "1.0.10" => "1.0.5",
        "1.1.0" => "1.0.6",
        _ => null
    };
    // Explicit per-call synthetic identity. No mutable process-wide trust hook.
    // Production callers never supply this; its constructor forbids real service coordinates.
    internal sealed record IsolatedTestContract
    {
        internal string Root { get; }
        internal int Port { get; }
        internal string PipeName { get; }
        internal string ExeHash { get; }
        internal string DllHash { get; }
        internal IsolatedTestContract(string root, int port, string pipeName, string exeHash, string dllHash)
        {
            Root = Path.GetFullPath(root); Port = port; PipeName = pipeName; ExeHash = exeHash; DllHash = dllHash;
            if (Root.Equals(DefaultRoot, StringComparison.OrdinalIgnoreCase) || port == 50609 ||
                !pipeName.StartsWith("SodaTerminal.AssetQuickRead.HostSelfTest.", StringComparison.Ordinal) ||
                !Guid.TryParseExact(pipeName["SodaTerminal.AssetQuickRead.HostSelfTest.".Length..], "N", out _))
                throw new IOException("isolated_old_host_contract_required");
        }
    }
    private static bool AllowedOldFiles(string executable, string directory, string version, int port, string pipeName, IsolatedTestContract? test)
    {
        if (test is null ? port != 50609 || pipeName != "SodaTerminal.AssetQuickRead.Identity.50609" : port != test.Port || pipeName != test.PipeName) return false;
        var installVersion = Path.GetFileName(directory);
        var expected = Path.Combine(test?.Root ?? DefaultRoot, "versions", installVersion);
        if (!Path.GetFullPath(directory).Equals(expected, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFullPath(executable).Equals(Path.Combine(expected, "Soda.AssetQuickRead.Bridge.exe"), StringComparison.OrdinalIgnoreCase) ||
            PublishedBridgeVersion(installVersion) != version) return false;
        RejectLinks(executable); RejectLinks(Path.Combine(directory, "Soda.AssetQuickRead.Bridge.dll"));
        using var exe = File.OpenRead(executable);
        using var dll = File.OpenRead(Path.Combine(directory, "Soda.AssetQuickRead.Bridge.dll"));
        var exeHash = Convert.ToHexString(SHA256.HashData(exe)); var dllHash = Convert.ToHexString(SHA256.HashData(dll));
        return test is null ? MatchesPublishedBridgePin(version, exeHash, dllHash) :
            version == "1.0.3" && installVersion == "1.0.7" && exeHash == test.ExeHash && dllHash == test.DllHash;
    }
    private static async Task<string?> ReadBanner(StreamReader reader, CancellationToken cancellation)
    {
        var chars = new char[129]; var length = 0;
        while (length < chars.Length)
        {
            var read = await reader.ReadAsync(chars.AsMemory(length), cancellation);
            if (read == 0) return new string(chars, 0, length);
            length += read;
        }
        return null;
    }

    internal static bool PortInUse(int port)
    {
        try { return IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(x => x.Port == port); }
        catch { return false; }
    }

    internal static bool SameFiles(string expected, string observed)
    {
        try
        {
            RejectLinks(expected); RejectLinks(observed);
            foreach (var name in new[] { "Soda.AssetQuickRead.Bridge.exe", "Soda.AssetQuickRead.Bridge.dll" })
            {
                using var a = File.OpenRead(Path.Combine(expected, name));
                using var b = File.OpenRead(Path.Combine(observed, name));
                if (!SHA256.HashData(a).SequenceEqual(SHA256.HashData(b))) return false;
            }
            return true;
        }
        catch { return false; }
    }
    private static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("linked_path_rejected");
    }
    internal static async Task<bool> TrustedPipeOwner(string expectedDirectory, string version, string pipeName, CancellationToken cancellation = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(500);
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.In, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(timeout.Token);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid)) return false;
            using var owner = Process.GetProcessById((int)pid);
            var executable = owner.MainModule?.FileName;
            if (executable is null || Path.GetFileName(executable) != "Soda.AssetQuickRead.Bridge.exe" || !SameFiles(expectedDirectory, Path.GetDirectoryName(executable)!)) return false;
            using var reader = new StreamReader(pipe);
            return await ReadBanner(reader, timeout.Token) == "soda-asset-quick-read/" + version;
        }
        catch { return false; }
    }
    internal static async Task<bool> TrustedExisting(string expectedDirectory, string version, int port, string pipeName, CancellationToken cancellation = default)
    {
        try
        {
            if (!await TrustedPipeOwner(expectedDirectory, version, pipeName, cancellation)) return false;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(1000);
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
            http.DefaultRequestHeaders.Host = $"127.0.0.1:{(ServicePorts.Contains(port) ? port : ServicePorts[0])}";
            using var response = await http.GetAsync($"http://127.0.0.1:{port}/health", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode) return false;
            // The owned service emits a small fixed public health record. Bound
            // reads even if another listener attempts to stream a large body.
            using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
            var buffer = new byte[4097]; var count = 0;
            while (count < buffer.Length)
            {
                var read = await body.ReadAsync(buffer.AsMemory(count), timeout.Token);
                if (read == 0) break;
                count += read;
            }
            if (count > 4096) return false;
            using var doc = JsonDocument.Parse(buffer.AsMemory(0, count));
            var health = doc.RootElement;
            return health.GetProperty("service").GetString() == "soda-asset-quick-read" && health.GetProperty("version").GetString() == version && health.GetProperty("protocolVersion").GetInt32() == 1;
        }
        catch { return false; }
    }
    internal static async Task Start(ProcessStartInfo start, string version, int port, string pipeName, TimeSpan? readinessTimeout = null, Func<ProcessStartInfo, Process?>? launch = null)
    {
        if (start.UseShellExecute || !start.CreateNoWindow || start.WindowStyle != ProcessWindowStyle.Hidden) throw new IOException("windowless_service_launch_required");
        var expectedDirectory = Path.GetDirectoryName(Path.GetFullPath(start.FileName))!;
        using var timeout = new CancellationTokenSource(readinessTimeout ?? TimeSpan.FromSeconds(8));
        foreach (var candidate in CandidatePorts(port))
            if (await TrustedExisting(expectedDirectory, version, candidate, pipeName, timeout.Token)) return;
        using var launched = (launch ?? Process.Start)(start) ?? throw new IOException("launch_failed");
        while (!timeout.IsCancellationRequested)
        {
            if (launched.HasExited && launched.ExitCode != 0)
                throw new IOException(launched.ExitCode == 73 ? "local_service_bind_or_identity_unavailable" : "local_service_start_failed");
            foreach (var candidate in CandidatePorts(port))
                if (await TrustedExisting(expectedDirectory, version, candidate, pipeName, timeout.Token)) return;
            try { await Task.Delay(100, timeout.Token); } catch (OperationCanceledException) { break; }
        }
        // Never terminate an existing or newly launched service on a timeout.
        throw new IOException("local_service_readiness_timeout");
    }

    internal sealed record VerifiedOldBridge(int ProcessId, long StartTimeTicks, string ExecutablePath, string Directory, string Version, int Port, string PipeName);

    internal static async Task<VerifiedOldBridge?> InspectOldBridge(int port, string pipeName, CancellationToken cancellation = default, IsolatedTestContract? test = null)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(800);
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.In, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try { await pipe.ConnectAsync(timeout.Token); } catch { return null; }
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid) || pid == 0) return null;
            using var owner = Process.GetProcessById((int)pid);
            if (owner.HasExited) return null;
            long startTicks;
            string? executable;
            try
            {
                startTicks = owner.StartTime.ToUniversalTime().Ticks;
                executable = owner.MainModule?.FileName;
            }
            catch { return null; }
            if (string.IsNullOrWhiteSpace(executable)) return null;
            executable = Path.GetFullPath(executable);
            if (!Path.IsPathRooted(executable) || Path.GetFileName(executable) != "Soda.AssetQuickRead.Bridge.exe") return null;
            var directory = Path.GetDirectoryName(executable);
            if (directory is null) return null;
            RejectLinks(executable);
            RejectLinks(directory);

            using var reader = new StreamReader(pipe);
            var banner = await ReadBanner(reader, timeout.Token);
            if (banner is null || !banner.StartsWith("soda-asset-quick-read/", StringComparison.Ordinal)) return null;
            var observedVersion = banner["soda-asset-quick-read/".Length..];
            if (string.IsNullOrWhiteSpace(observedVersion)) return null;

            if (!AllowedOldFiles(executable, directory, observedVersion, port, pipeName, test)) return null;

            using var httpTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            httpTimeout.CancelAfter(1000);
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
            http.DefaultRequestHeaders.Host = $"127.0.0.1:{port}";
            using var response = await http.GetAsync($"http://127.0.0.1:{port}/health", HttpCompletionOption.ResponseHeadersRead, httpTimeout.Token);
            if (!response.IsSuccessStatusCode) return null;
            using var body = await response.Content.ReadAsStreamAsync(httpTimeout.Token);
            var buffer = new byte[4097]; var count = 0;
            while (count < buffer.Length)
            {
                var read = await body.ReadAsync(buffer.AsMemory(count), httpTimeout.Token);
                if (read == 0) break;
                count += read;
            }
            if (count > 4096) return null;
            using var doc = JsonDocument.Parse(buffer.AsMemory(0, count));
            var health = doc.RootElement;
            if (health.GetProperty("service").GetString() != "soda-asset-quick-read" ||
                health.GetProperty("version").GetString() != observedVersion ||
                health.GetProperty("protocolVersion").GetInt32() != 1) return null;

            return new VerifiedOldBridge((int)pid, startTicks, executable, directory, observedVersion, port, pipeName);
        }
        catch { return null; }
    }

    internal static async Task<bool> RecheckAndTerminate(VerifiedOldBridge oldBridge, CancellationToken cancellation = default, IsolatedTestContract? test = null)
    {
        try
        {
            // Rebuild authority from the OS pipe, actual module path, public health
            // and immutable pins. A record supplied by a caller grants no authority.
            if (await InspectOldBridge(oldBridge.Port, oldBridge.PipeName, cancellation, test) != oldBridge) return false;
            using var owner = Process.GetProcessById(oldBridge.ProcessId);
            if (owner.HasExited) return false;
            try
            {
                if (owner.StartTime.ToUniversalTime().Ticks != oldBridge.StartTimeTicks) return false;
                var currentExe = owner.MainModule?.FileName;
                if (currentExe is null || !string.Equals(Path.GetFullPath(currentExe), oldBridge.ExecutablePath, StringComparison.OrdinalIgnoreCase)) return false;
            }
            catch { return false; }

            RejectLinks(oldBridge.ExecutablePath);
            RejectLinks(oldBridge.Directory);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(500);
            using var pipe = new NamedPipeClientStream(".", oldBridge.PipeName, PipeDirection.In, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(timeout.Token);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var currentPid) || currentPid != (uint)oldBridge.ProcessId) return false;
            using var reader = new StreamReader(pipe);
            var banner = await ReadBanner(reader, timeout.Token);
            if (banner != "soda-asset-quick-read/" + oldBridge.Version) return false;

            // Keep both components open without write/delete sharing through Kill.
            using var exeLock = File.OpenRead(oldBridge.ExecutablePath);
            using var dllLock = File.OpenRead(Path.Combine(oldBridge.Directory, "Soda.AssetQuickRead.Bridge.dll"));
            if (!AllowedOldFiles(oldBridge.ExecutablePath, oldBridge.Directory, oldBridge.Version, oldBridge.Port, oldBridge.PipeName, test) ||
                owner.HasExited || owner.StartTime.ToUniversalTime().Ticks != oldBridge.StartTimeTicks) return false;
            owner.Kill();
            await owner.WaitForExitAsync(cancellation).WaitAsync(TimeSpan.FromSeconds(4), cancellation);

            var deadline = DateTime.UtcNow.AddSeconds(4);
            while (DateTime.UtcNow < deadline && !cancellation.IsCancellationRequested)
            {
                if (!PortInUse(oldBridge.Port)) return true;
                await Task.Delay(50, cancellation);
            }
            return false;
        }
        catch { return false; }
    }

    internal static async Task SwitchService(VerifiedOldBridge oldBridge, ProcessStartInfo newStart, string newVersion, TimeSpan? readinessTimeout = null, Func<ProcessStartInfo, Process?>? launch = null, CancellationToken cancellation = default, IsolatedTestContract? test = null)
    {
        if (!await RecheckAndTerminate(oldBridge, cancellation, test)) throw new IOException("service_switch_race_rejected");
        await Start(newStart, newVersion, oldBridge.Port, oldBridge.PipeName, readinessTimeout, launch);
    }
}
