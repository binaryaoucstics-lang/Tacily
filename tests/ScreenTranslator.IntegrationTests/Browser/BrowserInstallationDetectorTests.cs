using ScreenTranslator.App.Services.Browser;
using ScreenTranslator.Core.Browser;

namespace ScreenTranslator.IntegrationTests.Browser;

public sealed class BrowserInstallationDetectorTests
{
    [Fact]
    public void Uses_Registered_Chrome_Path_First()
    {
        var probe = new FakeProbe
        {
            RegisteredPath = @"D:\Browsers\Chrome\chrome.exe",
        };
        probe.Existing.Add(Path.GetFullPath(probe.RegisteredPath));
        var detector = CreateDetector(probe);

        var result = detector.Detect(BrowserKind.Chrome);

        Assert.True(result.IsInstalled);
        Assert.Equal(
            Path.GetFullPath(probe.RegisteredPath),
            result.ExecutablePath);
    }

    [Fact]
    public void Finds_Standard_Edge_Path()
    {
        var probe = new FakeProbe();
        var expected = Path.GetFullPath(
            @"C:\Program Files\Microsoft\Edge\Application\msedge.exe");
        probe.Existing.Add(expected);
        var detector = CreateDetector(probe);

        var result = detector.Detect(BrowserKind.Edge);

        Assert.True(result.IsInstalled);
        Assert.Equal(expected, result.ExecutablePath);
    }

    [Fact]
    public void Reports_Missing_Browser()
    {
        var detector = CreateDetector(new FakeProbe());

        var result = detector.Detect(BrowserKind.Chrome);

        Assert.False(result.IsInstalled);
        Assert.Null(result.ExecutablePath);
    }

    private static BrowserInstallationDetector CreateDetector(
        IBrowserInstallationProbe probe) =>
        new(
            probe,
            @"C:\Users\Test\AppData\Local",
            @"C:\Program Files",
            @"C:\Program Files (x86)");

    private sealed class FakeProbe : IBrowserInstallationProbe
    {
        public string? RegisteredPath { get; init; }

        public HashSet<string> Existing { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public string? ReadAppPath(string executableName) =>
            RegisteredPath;

        public bool FileExists(string path) => Existing.Contains(path);
    }
}
