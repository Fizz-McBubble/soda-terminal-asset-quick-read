using System.Diagnostics;
using System.Text.Json;
namespace Soda.AssetQuickRead.Setup;

// Only finite installer phases, item indexes and classified codes are persisted.
// Exception messages, paths, URLs, account data and environment values never enter this record.
internal sealed class InstallerDiagnostics(string directory, Action<string>? phaseChanged = null)
{
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly object gate = new();
    private readonly List<object> events = new();
    private readonly List<InstallerAttemptReport> inMemoryAttempts = new();
    private string currentPhase = "prepare";
    private static readonly Lazy<string?> ExecutableHash = new(() =>
    {
        try { return Environment.ProcessPath is { } path ? InstallEngine.Digest(path) : null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    });
    internal static string Code(Exception error)
    {
        if (error is UnauthorizedAccessException) return "installer_access_denied";
        if (error is OperationCanceledException) return "installer_cancelled";
        if (error is System.Net.Http.HttpRequestException) return "protocol_download_connection_failed";
        var value = error.Message;
        if (value.Length <= 80 && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') &&
            new[] { "protocol_download_", "protocol_source_", "payload_", "catalog_", "installed_", "offline_payload_", "uninstaller_", "protocol_registration_", "uninstall_registration_", "start_menu_", "linked_path_", "another_installer_" }.Any(prefix => value.StartsWith(prefix, StringComparison.Ordinal))) return value;
        return "installer_local_operation_failed";
    }
    internal static string Message(string code) => code == "another_installer_is_running" ? "已有安装正在进行，请稍后重试。"
        : code.StartsWith("protocol_download_") ? "所需数据下载或校验失败，请查看安装详情后重试。"
        : code.StartsWith("payload_") || code.StartsWith("catalog_") || code.StartsWith("offline_payload_") ? "安装包准备或校验失败，请重新下载。"
        : code.Contains("registration") || code.Contains("shortcut_conflict") || code.Contains("uninstaller_file_conflict") ? "现有安装入口不匹配，请查看详情；已有版本已保留。"
        : code == "installer_access_denied" ? "安装目录访问被拒绝，请检查权限或安全软件提示。"
        : "安装未完成，请查看安装详情后重试。";
    internal void Identity(string stage, bool registrationExists, string previousVersion, bool ownedShape, bool sameVersion, bool upgrading, bool stubExists, string knownPin, bool expectedMatch, bool registrySnapshotChanged, bool stubSnapshotChanged)
    {
        lock (gate)
        {
            if (events.Count < 80) events.Add(new { phase = "registration_identity", stage, registrationExists, previousVersion, ownedShape, sameVersion, upgrading, stubExists, knownPin, expectedMatch, registrySnapshotChanged, stubSnapshotChanged, elapsedMs = clock.ElapsedMilliseconds });
            Persist();
        }
    }
    private void Persist()
    {
        try
        {
            InstallEngine.NoLinks(directory); Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "latest.json"); InstallEngine.NoLinks(path);
            File.WriteAllText(path, JsonSerializer.Serialize(new { schema = "soda-asset-installer-diagnostic/v1", version = InstallEngine.Version, processId = Environment.ProcessId, executableSha256 = ExecutableHash.Value, writtenUtc = DateTime.UtcNow, events }));
        }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    internal void Record(string phase, string code = "started", int? item = null, string? source = null)
    {
        if (phase is not ("prepare" or "payload_verify" or "copy" or "protocol" or "verify" or "register" or "complete" or "failed" or "cancelled")) return;
        if (source is not (null or "github_raw" or "github_contents" or "verified_local")) return;
        code = code == "started" || code == "request_started" || code == "verified" || code == "completed" ? code : Code(new IOException(code));
        phaseChanged?.Invoke(phase);
        lock (gate)
        {
            if (phase is not ("failed" or "cancelled" or "complete")) currentPhase = phase;
            if (phase == "protocol" && item is >= 0 and <= 2 && source is "github_raw" or "github_contents" or "verified_local" && InstallerFeedback.WhitelistedAttemptCodes.Contains(code))
            {
                if (inMemoryAttempts.Count == 16) inMemoryAttempts.RemoveAt(0);
                inMemoryAttempts.Add(new InstallerAttemptReport(item.Value, source, code, Math.Clamp(clock.ElapsedMilliseconds, 0, 3600000)));
            }
            if (events.Count < 80) events.Add(new { phase, code, item, source, elapsedMs = clock.ElapsedMilliseconds });
            Persist();
        }
    }
    internal InstallerFeedbackReport BuildSafeReport(string finalCode)
    {
        lock (gate)
        {
            return InstallerFeedback.CreateSafeReport(
                currentPhase,
                finalCode,
                clock.ElapsedMilliseconds,
                inMemoryAttempts
            );
        }
    }
}
