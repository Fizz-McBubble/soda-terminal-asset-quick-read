using System.Text.Json;
namespace Soda.AssetQuickRead.Setup;

// Read-only support probe. It never installs, launches the bridge, or reads jobs.
internal static class InstallInspection
{
    internal static int Run(string output)
    {
        ApplicationConfiguration.Initialize();
        var result = 1;
        using var form = new InstallForm(preview: true) { Opacity = 0, ShowInTaskbar = false };
        form.Shown += async (_, _) =>
        {
            try { result = await Task.Run(() => Inspect(output)); }
            finally { form.Close(); }
        };
        Application.Run(form);
        return result;
    }
    private static int Inspect(string output)
    {
        var root = InstallEngine.DefaultRoot;
        var registration = new RegistryUninstallStore().Read();
        var protocol = new RegistryProtocolRegistrationStore().Read();
        var user = new UserRegistration();
        var current = user.InstalledVersion(root);
        var code = "ready";
        try
        {
            new InstallRegistration(root).Preflight(Path.Combine(root, "versions", InstallEngine.Version, "Soda.AssetQuickRead.Bridge.exe"));
        }
        catch (Exception error) { code = InstallerDiagnostics.Code(error); }
        InstallEngine.NoLinks(output);
        using var report = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(report, new
        {
            schema = "soda-asset-install-inspection/v1", version = InstallEngine.Version,
            processId = Environment.ProcessId, writtenUtc = DateTime.UtcNow,
            executableHash = InstallEngine.Digest(Environment.ProcessPath!),
            registrationExists = registration is not null, ownedRegistration = registration?.OwnedShape,
            registeredVersion = registration?.Version, protocolExists = protocol is not null,
            ownedProtocol = protocol?.OwnedShape, recognizedVersion = current,
            trustedPreviousRuntime = current is not null && user.TrustedInstalledVersion(root, current),
            preflight = code, registryWrites = 0, installedFilesChanged = 0, captureStarted = false
        });
        return code == "ready" ? 0 : 1;
    }
}
