using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text.Json;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
namespace Soda.AssetQuickRead;

internal static class LaunchIdentity
{
    internal const string Version = "1.0.2";
    internal const string PipeName = "SodaTerminal.AssetQuickRead.Identity.50609";
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);
    internal static void ShowLaunchFailure() => MessageBoxW(IntPtr.Zero, "资产快读未能启动。50609 端口或启动身份已被其他服务占用，或本机服务启动失败。\n请关闭你确认的旧版资产快读后重试；工具不会停止其他进程。", "Soda Terminal 资产快读", 0x10);
    internal static bool ValidUri(string value) => value is "soda-asset-quick-read://launch" or "soda-asset-quick-read://launch/";
    internal static bool SameFiles(string expected, string observed)
    {
        try
        {
            Security.RejectLinks(expected); Security.RejectLinks(observed);
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
    internal static bool PortOccupied() => IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(x => x.Port == Program.Port);
    internal static async Task IdentityServer(CancellationToken cancellation, string name = PipeName)
    {
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(name, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellation);
                await pipe.WriteAsync("soda-asset-quick-read/1.0.2"u8.ToArray(), cancellation);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return; }
            catch (IOException) { await Task.Delay(100, cancellation); }
        }
    }
    internal static async Task<bool> TrustedPipeOwner(string name = PipeName)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", name, PipeDirection.In, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(300);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid)) return false;
            using var owner = Process.GetProcessById((int)pid);
            var executable = owner.MainModule?.FileName;
            if (executable is null || Path.GetFileName(executable) != "Soda.AssetQuickRead.Bridge.exe" || !SameFiles(AppContext.BaseDirectory, Path.GetDirectoryName(executable)!)) return false;
            using var timeout = new CancellationTokenSource(500);
            using var reader = new StreamReader(pipe);
            return await reader.ReadToEndAsync(timeout.Token) == "soda-asset-quick-read/1.0.2";
        }
        catch { return false; }
    }
    internal static async Task<bool> TrustedExisting(int port = Program.Port, string pipeName = PipeName)
    {
        try
        {
            // HTTP.sys owns the TCP socket (PID 4). A current-user pipe exposes the actual
            // bridge owner through the OS; health alone must never prove identity.
            if (!await TrustedPipeOwner(pipeName)) return false;
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(2) };
            http.DefaultRequestHeaders.Host = $"127.0.0.1:{Program.Port}";
            using var doc = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/health"));
            var health = doc.RootElement;
            return health.GetProperty("service").GetString() == "soda-asset-quick-read" && health.GetProperty("version").GetString() == Version && health.GetProperty("protocolVersion").GetInt32() == 1;
        }
        catch { return false; }
    }
}
