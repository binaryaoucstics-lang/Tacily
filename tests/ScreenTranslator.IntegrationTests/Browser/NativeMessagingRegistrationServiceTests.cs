using System.Text.Json;
using ScreenTranslator.App.Services.Browser;
using ScreenTranslator.Core.Browser;

namespace ScreenTranslator.IntegrationTests.Browser;

public sealed class NativeMessagingRegistrationServiceTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        "ScreenTranslator.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task RegisterAsync_Writes_Manifest_And_Both_User_Registrations()
    {
        var registry = new RecordingRegistry();
        var executablePath = Path.Combine(_temporaryDirectory, "ScreenTranslator.exe");
        var service = new NativeMessagingRegistrationService(
            registry,
            _temporaryDirectory,
            executablePath);

        var manifestPath = await service.RegisterAsync();

        Assert.True(File.Exists(manifestPath));
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
        var root = document.RootElement;
        Assert.Equal(
            NativeMessagingRegistrationService.HostName,
            root.GetProperty("name").GetString());
        Assert.Equal(
            Path.GetFullPath(executablePath),
            root.GetProperty("path").GetString());
        Assert.Equal(
            $"chrome-extension://{NativeMessagingRegistrationService.ExtensionId}/",
            root.GetProperty("allowed_origins")[0].GetString());
        Assert.Equal(2, registry.Writes.Count);
        Assert.Contains(registry.Writes, entry => entry.Browser == BrowserKind.Chrome);
        Assert.Contains(registry.Writes, entry => entry.Browser == BrowserKind.Edge);
        Assert.All(
            registry.Writes,
            entry => Assert.Equal(manifestPath, entry.ManifestPath));
        var status = await service.GetStatusAsync();
        Assert.True(status.IsHealthy);
    }

    [Fact]
    public async Task UnregisterAsync_Removes_Both_Registrations_And_Manifest()
    {
        var registry = new RecordingRegistry();
        var service = new NativeMessagingRegistrationService(
            registry,
            _temporaryDirectory,
            Path.Combine(_temporaryDirectory, "ScreenTranslator.exe"));
        var manifestPath = await service.RegisterAsync();

        await service.UnregisterAsync();

        Assert.False(File.Exists(manifestPath));
        Assert.Contains(BrowserKind.Chrome, registry.Deletes);
        Assert.Contains(BrowserKind.Edge, registry.Deletes);
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }
    }

    private sealed class RecordingRegistry : INativeMessagingRegistry
    {
        public List<RegistryWrite> Writes { get; } = [];

        public List<BrowserKind> Deletes { get; } = [];

        private readonly Dictionary<BrowserKind, string> _paths = [];

        public void SetHostManifest(
            BrowserKind browser,
            string hostName,
            string manifestPath)
        {
            _paths[browser] = manifestPath;
            Writes.Add(new RegistryWrite(browser, hostName, manifestPath));
        }

        public string? GetHostManifest(
            BrowserKind browser,
            string hostName) =>
            _paths.GetValueOrDefault(browser);

        public void DeleteHostManifest(
            BrowserKind browser,
            string hostName)
        {
            _paths.Remove(browser);
            Deletes.Add(browser);
        }
    }

    private sealed record RegistryWrite(
        BrowserKind Browser,
        string HostName,
        string ManifestPath);
}
