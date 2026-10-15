using System.Drawing;
using System.Text.Json;
using System.Windows.Forms;

namespace Soda.AssetQuickRead.Setup;

internal static class InstallerFeedbackTests
{
    internal static int Run(string evidencePath, bool returnAssertionCount = false)
    {
        var passed = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new Exception("feedback_test_failed: " + message);
            passed++;
        }

        // Test 1: Whitelist classification and raw error mapping
        Check(InstallerFeedback.ClassifyCode("protocol_download_timeout") == "protocol_download_timeout", "valid_code_preserved");
        Check(InstallerFeedback.ClassifyCode("installer_access_denied") == "installer_access_denied", "access_denied_preserved");
        Check(InstallerFeedback.ClassifyCode("local_service_readiness_timeout") == "local_service_readiness_timeout", "service_timeout_preserved");
        Check(InstallerFeedback.CreateSafeReport("service_start", "local_service_bind_or_identity_unavailable", 100, []).Stage == "service_start", "service_stage_preserved");
        Check(InstallerFeedback.ClassifyCode("random_untrusted_error_message") == "installer_local_operation_failed", "untrusted_error_mapped");
        Check(InstallerFeedback.ClassifyCode("C:\\Secret\\Path\\Error") == "installer_local_operation_failed", "path_mapped_to_local_failed");
        Check(InstallerFeedback.ClassifyCode(null) == "installer_local_operation_failed", "null_code_mapped");

        // Test 2: Safe report creation from in-memory attempts
        var sampleAttempts = new[]
        {
            new InstallerAttemptReport(0, "github_raw", "request_started", 100),
            new InstallerAttemptReport(0, "github_raw", "protocol_download_timeout", 20000),
            new InstallerAttemptReport(0, "github_contents", "request_started", 21000),
            new InstallerAttemptReport(0, "github_contents", "verified", 23000),
            // Foreign attempts that should be rejected
            new InstallerAttemptReport(5, "github_raw", "verified", 24000), // invalid item index
            new InstallerAttemptReport(0, "evil_source", "verified", 25000), // invalid source
            new InstallerAttemptReport(0, "github_raw", "untrusted_code", 26000), // invalid code
        };

        var report = InstallerFeedback.CreateSafeReport("protocol", "protocol_download_timeout", 25000, sampleAttempts);
        Check(report.Schema == 1, "schema_is_1");
        Check(report.Tool == "asset_quick_read_installer", "tool_matches");
        Check(Guid.TryParse(report.ReportId, out _), "reportId_is_guid");
        Check(report.InstallerVersion == InstallEngine.Version, "installer_version_matches");
        Check(report.Stage == "protocol", "stage_matches");
        Check(report.Code == "protocol_download_timeout", "code_matches");
        Check(report.DurationMs == 25000, "duration_matches");
        Check(report.Attempts.Length == 4, "filtered_attempts_count");

        // Test 3: Serialization privacy and strict keys
        var json = InstallerFeedback.SerializeReport(report);
        using (var doc = JsonDocument.Parse(json))
        {
            var root = doc.RootElement;
            Check(root.TryGetProperty("schema", out _), "has_schema");
            Check(root.TryGetProperty("tool", out _), "has_tool");
            Check(root.TryGetProperty("reportId", out _), "has_reportId");
            Check(root.TryGetProperty("installerVersion", out _), "has_installerVersion");
            Check(root.TryGetProperty("stage", out _), "has_stage");
            Check(root.TryGetProperty("code", out _), "has_code");
            Check(root.TryGetProperty("durationMs", out _), "has_durationMs");
            Check(root.TryGetProperty("attempts", out _), "has_attempts");

            // Verify absence of sensitive properties
            Check(!root.TryGetProperty("processId", out _), "no_processId");
            Check(!root.TryGetProperty("executableSha256", out _), "no_executableSha256");
            Check(!root.TryGetProperty("registration_identity", out _), "no_registration_identity");
            Check(!root.TryGetProperty("localpath", out _), "no_localpath");
            Check(!root.TryGetProperty("URLs", out _), "no_URLs");
            Check(!root.TryGetProperty("rawerror", out _), "no_rawerror");
            Check(!root.TryGetProperty("account", out _), "no_account");
            Check(!root.TryGetProperty("traffic", out _), "no_traffic");
            Check(!root.TryGetProperty("environment", out _), "no_environment");
            Check(!root.TryGetProperty("release", out _), "no_release");
        }

        // Test 4: URL structure (hash fragment only, no query string)
        var url = InstallerFeedback.BuildLaunchUrl(report);
        Check(url.StartsWith("https://sodaterminal.com/installer-feedback#"), "url_prefix_correct");
        Check(!url.Contains('?'), "url_has_no_query");
        var fragment = url["https://sodaterminal.com/installer-feedback#".Length..];
        var unescaped = Uri.UnescapeDataString(fragment);
        Check(unescaped == json, "unescaped_json_matches");

        // Test 5: In-memory diagnostics report generation
        var tempDiagnosticsDir = Path.Combine(Path.GetTempPath(), "SodaTestDiagnostics-" + Guid.NewGuid().ToString("N"));
        try
        {
            var diag = new InstallerDiagnostics(tempDiagnosticsDir);
            diag.Record("prepare");
            diag.Record("payload_verify");
            diag.Record("copy");
            diag.Record("protocol", "request_started", 0, "github_raw");
            diag.Record("protocol", "protocol_download_timeout", 0, "github_raw");
            diag.Record("failed", "protocol_download_timeout");

            var diagReport = diag.BuildSafeReport("protocol_download_timeout");
            Check(diagReport.Stage == "protocol", "diag_keeps_failed_operation_stage");
            Check(diagReport.Code == "protocol_download_timeout", "diag_code_matches");
            Check(diagReport.Attempts.Length == 2, "diag_attempts_length");
            Check(diagReport.Attempts[0].Code == "request_started", "attempt0_code");
            Check(diagReport.Attempts[1].Code == "protocol_download_timeout", "attempt1_code");
        }
        finally
        {
            if (Directory.Exists(tempDiagnosticsDir)) Directory.Delete(tempDiagnosticsDir, true);
        }

        // Test 6: Form UI footer layout and feedback button lifecycle
        using (var form = new InstallForm(preview: true))
        {
            form.ShowInTaskbar = false; form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-32000, -32000); form.Opacity = 0; form.Show();

            // Initial state: feedback button must be hidden
            var feedbackBtn = form.Controls.OfType<Button>().Single(b => b.Text == "反馈安装问题");
            Check(!feedbackBtn.Visible, "initial_feedback_button_hidden");

            // Error state: feedback button must be visible
            form.ApplyRenderFixture("error");
            Check(feedbackBtn.Visible, "error_feedback_button_visible");

            // Restart/progress state: feedback button must be hidden again
            form.ApplyRenderFixture("preparing");
            Check(!feedbackBtn.Visible, "preparing_feedback_button_hidden");

            // Completion state: feedback button must be hidden
            form.ApplyRenderFixture("completion");
            Check(!feedbackBtn.Visible, "completion_feedback_button_hidden");
            form.Close();
        }

        // Test 7: Form button clicks, browser launch and clipboard fallback
        string? launchedUrl = null;
        string? copiedClipboard = null;
        var operations = new FormOperations(
            Root: Path.Combine(Path.GetTempPath(), "SodaFormTest-" + Guid.NewGuid().ToString("N")),
            Install: (_, _) => Task.FromException<string>(new IOException("protocol_download_timeout")),
            Dispatch: _ => Task.CompletedTask,
            Uninstall: () => Task.CompletedTask,
            LaunchBrowser: u => { launchedUrl = u; },
            SetClipboard: c => { copiedClipboard = c; }
        );

        using (var interactiveForm = new InstallForm(preview: true, testOperations: operations))
        {
            interactiveForm.ShowInTaskbar = false; interactiveForm.StartPosition = FormStartPosition.Manual;
            interactiveForm.Location = new Point(-32000, -32000); interactiveForm.Opacity = 0; interactiveForm.Show();

            interactiveForm.ShowInstallError("protocol_download_timeout");
            var feedbackBtn = interactiveForm.Controls.OfType<Button>().Single(b => b.Text == "反馈安装问题");
            Check(feedbackBtn.Visible, "feedback_visible_after_error");

            // Simulate user click on Feedback button
            feedbackBtn.PerformClick();
            Check(launchedUrl != null, "browser_launched_on_click");
            Check(launchedUrl!.StartsWith("https://sodaterminal.com/installer-feedback#"), "browser_url_valid");

            // Test fallback when browser launch fails
            var fallbackOps = new FormOperations(
                Root: operations.Root,
                Install: operations.Install,
                Dispatch: operations.Dispatch,
                Uninstall: operations.Uninstall,
                LaunchBrowser: _ => throw new Exception("synthetic_browser_failure"),
                SetClipboard: c => { copiedClipboard = c; }
            );

            using var fallbackForm = new InstallForm(preview: true, testOperations: fallbackOps);
            fallbackForm.ShowInTaskbar = false; fallbackForm.StartPosition = FormStartPosition.Manual;
            fallbackForm.Location = new Point(-32000, -32000); fallbackForm.Opacity = 0; fallbackForm.Show();

            fallbackForm.ShowInstallError("protocol_download_timeout");
            var fallbackBtn = fallbackForm.Controls.OfType<Button>().Single(b => b.Text == "反馈安装问题");
            fallbackBtn.PerformClick();

            Check(copiedClipboard != null, "clipboard_copied_on_browser_failure");
            Check(copiedClipboard!.Contains("\"tool\":\"asset_quick_read_installer\""), "clipboard_content_is_safe_report");
            fallbackForm.Close();
            interactiveForm.Close();
        }

        // Test 8: DPI scaling (100%, 150%, 200%)
        foreach (var scale in new[] { 1.0f, 1.5f, 2.0f })
        {
            using var dpiForm = new InstallForm(preview: true);
            dpiForm.ApplyRenderFixture("error");
            dpiForm.ShowInTaskbar = false;
            dpiForm.StartPosition = FormStartPosition.Manual;
            dpiForm.Location = new Point(-32000, -32000);
            dpiForm.Opacity = 0;
            dpiForm.Show();

            var ratio = scale / (dpiForm.DeviceDpi / 96f);
            dpiForm.Scale(new SizeF(ratio, ratio));
            Application.DoEvents();

            var panel = dpiForm.Controls.OfType<Panel>().Single();
            var buttons = dpiForm.Controls.OfType<Button>().Where(b => b.Visible).ToArray();

            Check(buttons.Length >= 3, "buttons_visible_under_error");
            Check(panel.Bottom <= buttons.Min(b => b.Top), "panel_bottom_above_buttons_top_scaled_" + (int)(scale * 100));

            // Verify all visible buttons share the exact same Top row (no extra UI row)
            var firstTop = buttons[0].Top;
            Check(buttons.All(b => Math.Abs(b.Top - firstTop) <= 2), "all_buttons_share_same_footer_row_" + (int)(scale * 100));

            dpiForm.Close();
        }

        // Persist evidence
        var evidenceDir = Path.GetDirectoryName(Path.GetFullPath(evidencePath));
        if (evidenceDir is not null && !Directory.Exists(evidenceDir)) Directory.CreateDirectory(evidenceDir);
        File.WriteAllText(evidencePath, JsonSerializer.Serialize(new
        {
            schema = "soda-installer-feedback-tests/v1",
            passedCount = passed,
            timestampUtc = DateTime.UtcNow,
            status = "completed"
        }, new JsonSerializerOptions { WriteIndented = true }));

        return returnAssertionCount ? passed : 0;
    }
}
