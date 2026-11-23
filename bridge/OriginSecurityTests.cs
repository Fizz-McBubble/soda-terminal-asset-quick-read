using System.Reflection;
using System.Text.Json;

namespace Soda.AssetQuickRead;

// In-memory dispatch only: never starts the bridge, a capture job or a driver.
internal static class OriginSecurityTests
{
    private sealed class NoLauncher : IWorkerLauncher
    {
        public Task Start(Guid id, string directory) => throw new Exception("origin_test_must_not_launch");
    }

    internal static async Task<int> Run()
    {
        var checks = 0;
        void Check(bool value) { if (!value) throw new Exception("quick_read_origin_policy_failed"); checks++; }
        async Task Refuses(Func<Task> action, int status)
        {
            try { await action(); }
            catch (ApiFault e) { Check(e.Status == status); return; }
            throw new Exception("quick_read_origin_did_not_refuse");
        }
        const string domain = "https://sodaterminal.com";
        string[] allowed = [domain, "http://127.0.0.1:50565", "http://127.0.0.1:5173"];
        string?[] denied = [null, "null", "https://app.sodaterminal.workers.dev", "https://soda-terminal-production.up.railway.app", domain + "/", domain + ":443", domain + ".evil.example", "https://user@sodaterminal.com", "http://localhost:5173", "http://127.0.0.1:5174", "http://127.0.0.1:5173/", "http://127.0.0.1:50565.evil"];
        foreach (var host in new[] { "127.0.0.1:50609", "127.0.0.1:46009", "127.0.0.1:46010" }) Check(Security.ValidHost(host));
        foreach (var host in new string?[] { null, "localhost:50609", "127.0.0.1:50609.evil", "evil.example:50609", "127.0.0.1:46011", "127.0.0.1:50609@evil.example" }) Check(!Security.ValidHost(host));
        var service = new BridgeService(new NoLauncher(), synthetic: true);
        var empty = "{}"u8.ToArray();
        const string route = "/jobs/00000000-0000-0000-0000-000000000001";
        foreach (var origin in denied)
        {
            Check(!Security.ValidOrigin(origin));
            await Refuses(() => service.Dispatch("POST", "/connect", origin, null, empty), 403);
        }
        foreach (var origin in allowed)
        {
            Check(Security.ValidOrigin(origin));
            var reply = await service.Dispatch("POST", "/connect", origin, null, empty);
            var token = JsonSerializer.SerializeToElement(reply.Body, Program.Json).GetProperty("token").GetString()!;
            // A missing job returns 404 only after valid origin-bound authorization.
            await Refuses(() => service.Dispatch("GET", route, origin, "Bearer " + token, []), 404);
            foreach (var other in allowed.Where(value => value != origin))
                await Refuses(() => service.Dispatch("GET", route, other, "Bearer " + token, []), 401);
            foreach (var retired in denied.Where(value => value is not null))
                await Refuses(() => service.Dispatch("GET", route, retired, "Bearer " + token, []), 403);
            var tokens = (Dictionary<string, Pairing>)typeof(BridgeService).GetField("tokens", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(service)!;
            tokens[token] = new(origin, DateTimeOffset.UtcNow.AddSeconds(-1));
            await Refuses(() => service.Dispatch("GET", route, origin, "Bearer " + token, []), 401);
        }
        var paired = await service.Dispatch("POST", "/connect", domain, null, empty);
        var current = JsonSerializer.SerializeToElement(paired.Body, Program.Json).GetProperty("token").GetString()!;
        service.StopOwnedJobs();
        await Refuses(() => service.Dispatch("GET", route, domain, "Bearer " + current, []), 401);
        return checks;
    }
}
