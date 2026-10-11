using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;
namespace Soda.AssetQuickRead;

internal static class Program
{
    internal const int Port = 50609;
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static string JobRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SodaTerminal", "AssetQuickRead", "jobs");
    internal static async Task<int> Main(string[] args)
    {
        var protocolLaunch = false;
        try
        {
            if (args.SequenceEqual(new[] { "--self-test" })) return await SelfTests.Run();
            if (args.Length == 2 && args[0] == "--capture-worker" && Guid.TryParseExact(args[1], "D", out var id))
                return await CaptureWorker.Run(id);
            protocolLaunch = args.Length == 2 && args[0] == "--protocol" && LaunchIdentity.ValidUri(args[1]);
            if (args.Length != 0 && !args.SequenceEqual(new[] { "--serve" }) && !protocolLaunch) return 64;
            using var instance = new Mutex(true, "Local\\SodaTerminal.AssetQuickRead.50609", out var created);
            if (!created)
            {
                for (var attempt = 0; attempt < 10; attempt++)
                {
                    if (await LaunchIdentity.TrustedExisting()) return 0;
                    await Task.Delay(200);
                }
                Console.Error.WriteLine("asset_quick_read_service_conflict"); if (protocolLaunch) LaunchIdentity.ShowLaunchFailure(); return 73;
            }
            if (LaunchIdentity.PortOccupied())
            {
                if (await LaunchIdentity.TrustedExisting()) return 0;
                Console.Error.WriteLine("asset_quick_read_port_conflict"); if (protocolLaunch) LaunchIdentity.ShowLaunchFailure(); return 73;
            }
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
            builder.Configuration.Sources.Clear();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.Listen(System.Net.IPAddress.Loopback, Port);
                options.Limits.MaxRequestBodySize = Security.BodyLimit;
                options.Limits.MaxConcurrentConnections = 64;
                options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
            });
            await using var app = builder.Build();
            var bridge = new BridgeService(new ElevatedLauncher());
            app.Run(context => Handle(context, bridge));
            using var shutdown = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
            await app.StartAsync(shutdown.Token);
            Console.WriteLine($"Soda Asset Quick Read 1.0.2 listening on 127.0.0.1:{Port}; capture requires an explicit browser start and UAC.");
            var identityServer = LaunchIdentity.IdentityServer(shutdown.Token);
            try { await app.WaitForShutdownAsync(shutdown.Token); }
            finally
            {
                shutdown.Cancel(); bridge.StopOwnedJobs();
                await app.StopAsync();
                try { await identityServer; } catch (OperationCanceledException) { }
            }
            return 0;
        }
        catch { Console.Error.WriteLine("bridge_start_failed"); if (protocolLaunch) LaunchIdentity.ShowLaunchFailure(); return 1; }
    }
    internal static async Task Handle(HttpContext context, BridgeService bridge)
    {
        var req = context.Request; var res = context.Response;
        try
        {
            var origin = req.Headers.ContainsKey("Origin") ? req.Headers["Origin"].ToString() : null;
            if (!Security.ValidHost(req.Headers.Host.ToString())) throw new ApiFault(403, "host_rejected");
            if (origin is not null && !Security.ValidOrigin(origin)) throw new ApiFault(403, "origin_rejected");
            if (Security.ValidOrigin(origin))
            {
                res.Headers["Access-Control-Allow-Origin"] = origin;
                res.Headers["Vary"] = "Origin";
                res.Headers["Access-Control-Allow-Private-Network"] = "true";
            }
            res.Headers["Cache-Control"] = "no-store";
            res.Headers["X-Content-Type-Options"] = "nosniff";
            if (req.Method == "OPTIONS")
            {
                if (!Security.ValidOrigin(origin) || req.Headers["Access-Control-Request-Method"].ToString() is not ("GET" or "POST")) throw new ApiFault(403, "preflight_rejected");
                var headers = req.Headers["Access-Control-Request-Headers"].ToString().Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (headers.Any(x => !x.Equals("authorization", StringComparison.OrdinalIgnoreCase) && !x.Equals("content-type", StringComparison.OrdinalIgnoreCase))) throw new ApiFault(403, "preflight_headers_rejected");
                res.Headers["Access-Control-Allow-Headers"] = "Authorization, Content-Type";
                res.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
                res.StatusCode = 204; return;
            }
            if (req.Method is not ("GET" or "POST")) throw new ApiFault(405, "method_rejected");
            if (req.QueryString.HasValue || context.Features.Get<IHttpRequestFeature>()?.RawTarget?.Contains('%') == true) throw new ApiFault(400, "query_or_encoding_rejected");
            byte[] body = [];
            if (req.Method == "POST")
            {
                if (req.ContentType?.Split(';')[0].Trim() != "application/json") throw new ApiFault(415, "json_required");
                if (req.ContentLength is null or < 0 or > Security.BodyLimit) throw new ApiFault(413, "body_limit");
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var stream = new MemoryStream();
                var buffer = new byte[1024]; int count;
                while ((count = await req.Body.ReadAsync(buffer, timeout.Token)) != 0)
                {
                    if (stream.Length + count > Security.BodyLimit) throw new ApiFault(413, "body_limit");
                    stream.Write(buffer, 0, count);
                }
                body = stream.ToArray();
            }
            else if (req.ContentLength > 0 || req.Headers.ContainsKey("Transfer-Encoding")) throw new ApiFault(400, "get_body_rejected");
            var reply = await bridge.Dispatch(req.Method, req.Path.Value!, origin, req.Headers["Authorization"], body);
            res.StatusCode = reply.Status;
            await Write(res, reply.Body);
        }
        catch (ApiFault error) { res.StatusCode = error.Status; await Write(res, new { error = new { code = error.Code } }); }
        catch (OperationCanceledException) { res.StatusCode = 408; await Write(res, new { error = new { code = "body_timeout" } }); }
        catch { res.StatusCode = 500; try { await Write(res, new { error = new { code = "bridge_request_failed" } }); } catch { } }
        finally { await res.CompleteAsync(); }
    }
    private static async Task Write(HttpResponse response, object value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        response.ContentType = "application/json; charset=utf-8";
        response.ContentLength = bytes.Length;
        await response.Body.WriteAsync(bytes);
    }
}
internal sealed class ApiFault(int status, string code) : Exception(code)
{ internal int Status { get; } = status; internal string Code { get; } = code; }
internal sealed record Reply(int Status, object Body);
