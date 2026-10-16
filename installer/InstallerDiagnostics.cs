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
            new[] { "protocol_download_", "protocol_source_", "payload_", "catalog_", "installed_", "offline_payload_", "uninstaller_", "protocol_registration_", "uninstall_registration_", "start_menu_", "linked_path_", "another_installer_", "local_service_" }.Any(prefix => value.StartsWith(prefix, StringComparison.Ordinal))) return value;
        return "installer_local_operation_failed";
    }
    internal static string Message(string code) => code == "another_installer_is_running" ? "已有安装正在进行，请稍后重试。"
        : code == "installed_repair_files_in_use" ? "现有程序文件暂不可切换，请查看详情后重试。"
        : code == "installed_repair_switch_unavailable" ? "现有版本暂不可切换，原因未明，请查看详情。"
        : code == "installed_repair_restore_conflict" ? "修复回滚遇到目录变化，原目录备份已保留。"
        : code == "installed_version_path_conflict" ? "安装版本路径冲突，原有文件已保留。"
        : code.StartsWith("protocol_download_") ? "所需数据下载或校验失败，请查看安装详情后重试。"
        : code.StartsWith("payload_") || code.StartsWith("catalog_") || code.StartsWith("offline_payload_") ? "安装包准备或校验失败，请重新下载。"
        : code.Contains("registration") || code.Contains("shortcut_conflict") || code.Contains("uninstaller_file_conflict") ? "现有安装入口不匹配，请查看详情；已有版本已保留。"
        : code == "installer_access_denied" ? "安装目录访问被拒绝，请检查权限或安全软件提示。"
        : "安装未完成，请查看安装详情后重试。";
    internal static string RecoveryAction(string code) => code switch
    {
        "installed_repair_files_in_use" or "installed_repair_switch_unavailable" => "请先在网页完成或取消读取，再退出已确认的资产快读后重试；仍失败可保存工作并重启电脑。不要结束未知进程。",
        "installed_repair_restore_conflict" => "原目录保存在 versions 下本次 .repair- 开头的目录中。请反馈诊断，保留该目录与本次 .install- 目录，不要删除或手动覆盖。",
        "installed_version_path_conflict" => "版本目录的位置已有文件，安装器不会覆盖它。请反馈诊断，不要删除该文件或整个安装目录。",
        _ => "",
    };
    internal static string UninstallMessage(string code) => code switch
    {
        "uninstaller_service_stop_failed" => "连接服务状态已变化，卸载已中止。",
        "uninstaller_files_in_use" => "仍有程序文件被占用，卸载未完成。",
        "installer_access_denied" => "卸载访问被拒绝，请检查权限或安全软件提示。",
        "installer_cancelled" => "卸载已取消，已有文件和结果保留。",
        _ => "卸载未完成，已有文件和结果保留。",
    };
    internal static string UninstallRecovery(string code) => code switch
    {
        "uninstaller_service_stop_failed" => "请先完成或取消读取，再重试卸载；仍失败请反馈诊断。工具不会结束未知进程。",
        "uninstaller_files_in_use" => "请先完成或取消读取，再重试卸载。仍失败请反馈诊断，不要删除版本目录或结束未知进程。",
        _ => "请重试卸载；仍失败请反馈诊断。读取结果与账户数据保留。",
    };
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
        if (phase is not ("prepare" or "payload_verify" or "copy" or "protocol" or "verify" or "register" or "service_start" or "uninstall" or "complete" or "failed" or "cancelled")) return;
        if (source is not (null or "github_raw" or "github_contents" or "verified_local" or "github_archive")) return;
        code = code == "started" || code == "request_started" || code == "verified" || code == "completed" ? code : Code(new IOException(code));
        phaseChanged?.Invoke(phase);
        lock (gate)
        {
            if (phase is not ("failed" or "cancelled" or "complete")) currentPhase = phase;
            if (phase == "protocol" && item is >= 0 and <= 2 && source is "github_raw" or "github_contents" or "verified_local" or "github_archive" && InstallerFeedback.WhitelistedAttemptCodes.Contains(code))
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
