using System.IO;
using Microsoft.Win32;
using ScreenTranslator.Core.Browser;

namespace ScreenTranslator.App.Services.Browser;

public sealed record BrowserInstallation(
    BrowserKind Browser,
    bool IsInstalled,
    string? ExecutablePath);

public interface IBrowserInstallationProbe
{
    string? ReadAppPath(string executableName);

    bool FileExists(string path);
}

public sealed class WindowsBrowserInstallationProbe
    : IBrowserInstallationProbe
{
    public string? ReadAppPath(string executableName)
    {
        var keyPath =
            $@"Software\Microsoft\Windows\CurrentVersion\App Paths\{executableName}";
        foreach (var root in new[]
                 {
                     Registry.CurrentUser,
                     Registry.LocalMachine,
                 })
        {
            using var key = root.OpenSubKey(keyPath);
            if (key?.GetValue(null) is string path
                && !string.IsNullOrWhiteSpace(path))
            {
                return path.Trim('"');
            }
        }

        return null;
    }

    public bool FileExists(string path) => File.Exists(path);
}

public sealed class BrowserInstallationDetector
{
    private readonly IBrowserInstallationProbe _probe;
    private readonly string _localAppData;
    private readonly string _programFiles;
    private readonly string _programFilesX86;

    public BrowserInstallationDetector()
        : this(
            new WindowsBrowserInstallationProbe(),
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(
                Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(
                Environment.SpecialFolder.ProgramFilesX86))
    {
    }

    public BrowserInstallationDetector(
        IBrowserInstallationProbe probe,
        string localAppData,
        string programFiles,
        string programFilesX86)
    {
        _probe = probe;
        _localAppData = localAppData;
        _programFiles = programFiles;
        _programFilesX86 = programFilesX86;
    }

    public BrowserInstallation Detect(BrowserKind browser)
    {
        var executableName = browser == BrowserKind.Chrome
            ? "chrome.exe"
            : "msedge.exe";
        var candidates = new List<string?>(4)
        {
            _probe.ReadAppPath(executableName),
        };
        if (browser == BrowserKind.Chrome)
        {
            candidates.Add(Path.Combine(
                _localAppData,
                "Google",
                "Chrome",
                "Application",
                executableName));
            candidates.Add(Path.Combine(
                _programFiles,
                "Google",
                "Chrome",
                "Application",
                executableName));
            candidates.Add(Path.Combine(
                _programFilesX86,
                "Google",
                "Chrome",
                "Application",
                executableName));
        }
        else
        {
            candidates.Add(Path.Combine(
                _programFiles,
                "Microsoft",
                "Edge",
                "Application",
                executableName));
            candidates.Add(Path.Combine(
                _programFilesX86,
                "Microsoft",
                "Edge",
                "Application",
                executableName));
            candidates.Add(Path.Combine(
                _localAppData,
                "Microsoft",
                "Edge",
                "Application",
                executableName));
        }

        var path = candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
            .Select(candidate => Path.GetFullPath(candidate!))
            .FirstOrDefault(_probe.FileExists);
        return new BrowserInstallation(
            browser,
            path is not null,
            path);
    }
}
