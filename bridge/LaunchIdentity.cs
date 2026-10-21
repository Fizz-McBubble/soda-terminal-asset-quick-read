using System.Net.NetworkInformation;
using System.IO.Pipes;
using System.Runtime.InteropServices;
namespace Soda.AssetQuickRead;

internal static class LaunchIdentity
{
    internal const string Version = "1.1.0";
    internal const string PipeName = "SodaTerminal.AssetQuickRead.Identity.50609";
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);
    internal static void ShowLaunchFailure() => MessageBoxW(IntPtr.Zero, "资产快读连接服务未能启动。可能是本机监听地址不可用，也可能是服务身份或启动检查未完成。\n请在安装器中重试启动并查看本机诊断；工具不会停止其他进程。", "Soda Terminal 资产快读", 0x10);
    internal static bool ValidUri(string value) => value is "soda-asset-quick-read://launch" or "soda-asset-quick-read://launch/";
    internal static bool SameFiles(string expected, string observed) => ServiceReadiness.SameFiles(expected, observed);
    internal static bool PortOccupied(int port = Program.Port) => IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(x => x.Port == port);
    internal static async Task IdentityServer(CancellationToken cancellation, string name = PipeName)
    {
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(name, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellation);
                await pipe.WriteAsync(System.Text.Encoding.UTF8.GetBytes("soda-asset-quick-read/" + Version), cancellation);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return; }
            catch (IOException) { await Task.Delay(100, cancellation); }
        }
    }
    internal static Task<bool> TrustedPipeOwner(string name = PipeName) => ServiceReadiness.TrustedPipeOwner(AppContext.BaseDirectory, Version, name);
    internal static Task<bool> TrustedExisting(int port = Program.Port, string pipeName = PipeName) => ServiceReadiness.TrustedExisting(AppContext.BaseDirectory, Version, port, pipeName);
}
