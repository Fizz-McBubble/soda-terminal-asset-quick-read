using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
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
    internal const int LegacyUnavailableExitCode = 73;
    internal const int PortConflictExitCode = 74;
    internal const int AddressUnavailableExitCode = 75;
    internal const int IdentityUnavailableExitCode = 76;
    internal static string StartFailureCode(int exitCode) => exitCode switch
    {
        LegacyUnavailableExitCode => "local_service_bind_or_identity_unavailable",
        PortConflictExitCode => "local_service_port_conflict",
        AddressUnavailableExitCode => "local_service_address_unavailable",
        IdentityUnavailableExitCode => "local_service_identity_unavailable",
        _ => "local_service_start_failed",
    };
    internal static int[] CandidatePorts(int port) => port == ServicePorts[0] ? ServicePorts : [port];
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int length, bool sorted, int addressFamily, int tableClass, uint reserved);

    internal static bool ListenerOwnedBy(int port, int processId)
    {
        var length = 0;
        try
        {
            if (GetExtendedTcpTable(IntPtr.Zero, ref length, false, 2, 3, 0) != 122 || length is < 4 or > 1048576) return false;
            var table = Marshal.AllocHGlobal(length);
            try
            {
                if (GetExtendedTcpTable(table, ref length, false, 2, 3, 0) != 0) return false;
                var count = Marshal.ReadInt32(table);
                if (count < 0 || count > (length - 4) / 24) return false;
                var loopback = BitConverter.ToInt32(IPAddress.Loopback.GetAddressBytes());
                for (var index = 0; index < count; index++)
                {
                    var row = IntPtr.Add(table, 4 + index * 24);
                    var observedPort = (ushort)IPAddress.NetworkToHostOrder((short)(Marshal.ReadInt32(row, 8) & 0xffff));
                    if (Marshal.ReadInt32(row) == 2 && Marshal.ReadInt32(row, 4) == loopback && observedPort == port && Marshal.ReadInt32(row, 20) == processId) return true;
                }
                return false;
            }
            finally { Marshal.FreeHGlobal(table); }
        }
        catch { return false; }
    }

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
            ["1.0.7"] = ("F253FED3EB9A93E0177ADA1B2F342FFF7E0FA75AC1D4CC486CD73934D364E8F8", "3FCDB206302D318780FD22289FD9563EC9AA5BD70CB8A582319613CA1F2C14E9"),
            ["1.0.8"] = ("B57D907F32763FA120BABF8CE9609869B3A1FCC9665D2DD2FCF3DDF6B57BD3BE", "B9AA35189B489F9F7788FBBB6876E74B7436255951AD8F587864112D4C85E5D6"),
            ["1.0.11"] = ("2E9DD72B3D0F1990273DBC2486DB9E6EFC7FA7AFEDF8B8672EF9840C77292186", "73D8EC8389277DF398310EC4FC22CC05E4BAD42E84B83FF75EFD6410F4AF0AD6"),
            ["1.0.10"] = ("36768FB463980DD8708EFD20003496DA5A4EF7C34C1FE2513BD69416C42A8AC1", "1BEAB044A86E90B8EFFB332DA5E423C6482A1F5ADE09FFEC0F2F8C611829307B"),
            ["1.0.9"] = ("1945B59744A3082927F8A9870FA690E121508DDBB0FBB9B57E416FE4FE82BDFD", "14716C679EF718F52D43D13FE88F040B61C458FDBDF75068AD1AAFEB9A420728"),
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
        "1.1.1" => "1.0.7",
        "1.1.2" => "1.0.8",
        "1.1.3" => "1.0.9",
        "1.1.4" or "1.1.5" or "1.1.6" => "1.0.10",
        "1.1.7" or "1.1.8" => "1.0.11",
        _ => null
    };
    // The published 1.1.0 package contains component 1.0.6 bytes, but its
    // compiled pipe/health identity is 1.0.4. Neither identity substitutes for
    // the other: trust still requires the exact published component hashes.
    private static string? PublishedWireVersion(string installationVersion) =>
        installationVersion == "1.1.0" ? "1.0.4" : PublishedBridgeVersion(installationVersion);
    internal static bool MatchesPublishedInstallation(string installationVersion, string wireVersion, string exeSha256, string dllSha256) =>
        PublishedWireVersion(installationVersion) == wireVersion &&
        PublishedBridgeVersion(installationVersion) is { } componentVersion &&
        MatchesPublishedBridgePin(componentVersion, exeSha256, dllSha256);
    // Explicit per-call synthetic identity. No mutable process-wide trust hook.
    // Production callers never supply this; its constructor forbids real service coordinates.
    internal sealed record IsolatedTestContract
    {
        internal string Root { get; }
        internal int Port { get; }
        internal string PipeName { get; }
        internal string ExeHash { get; }
        internal string DllHash { get; }
        internal int[] Ports { get; private init; }
        internal bool PublishedOnly { get; private init; }
        internal IsolatedTestContract(string root, int port, string pipeName, string exeHash, string dllHash)
        {
            Root = Path.GetFullPath(root); Port = port; Ports = [port]; PipeName = pipeName; ExeHash = exeHash; DllHash = dllHash;
            if (Root.Equals(DefaultRoot, StringComparison.OrdinalIgnoreCase) || Root.StartsWith(DefaultRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || ServicePorts.Contains(port) || port is < 1024 or > 65535 ||
                !pipeName.StartsWith("SodaTerminal.AssetQuickRead.HostSelfTest.", StringComparison.Ordinal) ||
                !Guid.TryParseExact(pipeName["SodaTerminal.AssetQuickRead.HostSelfTest.".Length..], "N", out _))
                throw new IOException("isolated_old_host_contract_required");
        }
        internal static IsolatedTestContract ForPublishedFixtures(string root, int[] ports, string pipeName)
        {
            if (ports.Length is < 1 or > 3 || ports.Distinct().Count() != ports.Length || ports.Any(port => ServicePorts.Contains(port) || port is < 1024 or > 65535))
                throw new IOException("isolated_old_host_contract_required");
            return new(root, ports[0], pipeName, "", "") { Ports = ports.ToArray(), PublishedOnly = true };
        }
    }
    internal sealed record CurrentInstallation(string Version, string WireVersion, string ExeSha256, string DllSha256);
    private static bool AllowedOldFiles(string executable, string directory, string version, int port, string pipeName, IsolatedTestContract? test, CurrentInstallation? current = null)
    {
        if (test is null ? !ServicePorts.Contains(port) || pipeName != "SodaTerminal.AssetQuickRead.Identity.50609" : !test.Ports.Contains(port) || pipeName != test.PipeName) return false;
        var installVersion = Path.GetFileName(directory);
        var expected = Path.Combine(test?.Root ?? DefaultRoot, "versions", installVersion);
        if (!Path.GetFullPath(directory).Equals(expected, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFullPath(executable).Equals(Path.Combine(expected, "Soda.AssetQuickRead.Bridge.exe"), StringComparison.OrdinalIgnoreCase) ||
            !(PublishedWireVersion(installVersion) == version || current is not null && current.Version == installVersion && current.WireVersion == version)) return false;
        RejectLinks(executable); RejectLinks(Path.Combine(directory, "Soda.AssetQuickRead.Bridge.dll"));
        using var exe = File.OpenRead(executable);
        using var dll = File.OpenRead(Path.Combine(directory, "Soda.AssetQuickRead.Bridge.dll"));
        var exeHash = Convert.ToHexString(SHA256.HashData(exe)); var dllHash = Convert.ToHexString(SHA256.HashData(dll));
        if (current is not null && installVersion == current.Version && version == current.WireVersion && exeHash == current.ExeSha256 && dllHash == current.DllSha256) return true;
        return test is null || test.PublishedOnly ? MatchesPublishedInstallation(installVersion, version, exeHash, dllHash) :
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
        => await TrustedPipeProcess(expectedDirectory, version, pipeName, cancellation) is not null;
    private static async Task<int?> TrustedPipeProcess(string expectedDirectory, string version, string pipeName, CancellationToken cancellation)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(500);
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.In, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(timeout.Token);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid)) return null;
            using var owner = Process.GetProcessById((int)pid);
            var executable = owner.MainModule?.FileName;
            if (executable is null || Path.GetFileName(executable) != "Soda.AssetQuickRead.Bridge.exe" || !SameFiles(expectedDirectory, Path.GetDirectoryName(executable)!)) return null;
            using var reader = new StreamReader(pipe);
            return await ReadBanner(reader, timeout.Token) == "soda-asset-quick-read/" + version ? (int)pid : null;
        }
        catch { return null; }
    }
    internal static async Task<bool> TrustedExisting(string expectedDirectory, string version, int port, string pipeName, CancellationToken cancellation = default)
    {
        try
        {
            if (await TrustedPipeProcess(expectedDirectory, version, pipeName, cancellation) is not { } pid || !ListenerOwnedBy(port, pid)) return false;
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
        // The host itself allows 8 seconds for duplicate-instance verification.
        // Leave room for that explicit terminal diagnostic and our initial probes.
        using var timeout = new CancellationTokenSource(readinessTimeout ?? TimeSpan.FromSeconds(12));
        foreach (var candidate in CandidatePorts(port))
            if (await TrustedExisting(expectedDirectory, version, candidate, pipeName, timeout.Token)) return;
        using var launched = (launch ?? Process.Start)(start) ?? throw new IOException("launch_failed");
        while (!timeout.IsCancellationRequested)
        {
            if (launched.HasExited && launched.ExitCode != 0)
                throw new IOException(StartFailureCode(launched.ExitCode));
            foreach (var candidate in CandidatePorts(port))
                if (await TrustedExisting(expectedDirectory, version, candidate, pipeName, timeout.Token)) return;
            try { await Task.Delay(100, timeout.Token); } catch (OperationCanceledException) { break; }
        }
        // Never terminate an existing or newly launched service on a timeout.
        throw new IOException("local_service_readiness_timeout");
    }

    internal sealed record VerifiedOldBridge(int ProcessId, long StartTimeTicks, string ExecutablePath, string Directory, string Version, int Port, string PipeName);

    internal static async Task<VerifiedOldBridge?> InspectAnyKnownBridge(CancellationToken cancellation = default, IsolatedTestContract? test = null, CurrentInstallation? current = null)
    {
        foreach (var port in test?.Ports ?? ServicePorts)
            if (await InspectOldBridge(port, test?.PipeName ?? "SodaTerminal.AssetQuickRead.Identity.50609", cancellation, test, current) is { } bridge) return bridge;
        return null;
    }

    internal static async Task<VerifiedOldBridge?> InspectOldBridge(int port, string pipeName, CancellationToken cancellation = default, IsolatedTestContract? test = null, CurrentInstallation? current = null)
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

            if (!AllowedOldFiles(executable, directory, observedVersion, port, pipeName, test, current) || !ListenerOwnedBy(port, (int)pid)) return null;

            using var httpTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            httpTimeout.CancelAfter(1000);
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
            http.DefaultRequestHeaders.Host = $"127.0.0.1:{(ServicePorts.Contains(port) ? port : ServicePorts[0])}";
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
                health.GetProperty("protocolVersion").GetInt32() != 1 || owner.HasExited ||
                owner.StartTime.ToUniversalTime().Ticks != startTicks || !ListenerOwnedBy(port, (int)pid)) return null;

            return new VerifiedOldBridge((int)pid, startTicks, executable, directory, observedVersion, port, pipeName);
        }
        catch { return null; }
    }

    internal static async Task<bool> RecheckAndTerminate(VerifiedOldBridge oldBridge, CancellationToken cancellation = default, IsolatedTestContract? test = null, CurrentInstallation? current = null)
    {
        try
        {
            // Rebuild authority from the OS pipe, actual module path, public health
            // and immutable pins. A record supplied by a caller grants no authority.
            if (await InspectOldBridge(oldBridge.Port, oldBridge.PipeName, cancellation, test, current) != oldBridge) return false;
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
            if (!AllowedOldFiles(oldBridge.ExecutablePath, oldBridge.Directory, oldBridge.Version, oldBridge.Port, oldBridge.PipeName, test, current) ||
                owner.HasExited || owner.StartTime.ToUniversalTime().Ticks != oldBridge.StartTimeTicks || !ListenerOwnedBy(oldBridge.Port, oldBridge.ProcessId)) return false;
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
