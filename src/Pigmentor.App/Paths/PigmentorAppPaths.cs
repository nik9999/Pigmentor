using System.Runtime.InteropServices;

namespace Pigmentor.App.Paths;

/// <summary>
/// Cross-platform implementation of <see cref="IPigmentorAppPaths"/> adhering to OS conventions.
/// Windows: %APPDATA%/Pigmentor/ and %LOCALAPPDATA%/Pigmentor/
/// Linux: $XDG_CONFIG_HOME/pigmentor/ and $XDG_DATA_HOME/pigmentor/
/// macOS: ~/Library/Application Support/Pigmentor/
/// </summary>
public sealed class PigmentorAppPaths : IPigmentorAppPaths
{
    private const string AppFolderName = "Pigmentor";
    private const string LinuxAppFolderName = "pigmentor";
    private const string ConfigFileName = "config.json";
    private const string DatabaseFileName = "pigmentor.db";
    private const string SecretsFileName = "secrets.dat";

    public string ConfigDirectory { get; }
    public string DataDirectory { get; }

    public string ConfigFilePath => Path.Combine(ConfigDirectory, ConfigFileName);
    public string DefaultDatabasePath => Path.Combine(DataDirectory, DatabaseFileName);
    public string SecretsFilePath => Path.Combine(DataDirectory, SecretsFileName);

    public PigmentorAppPaths(string? configDirectory = null, string? dataDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(configDirectory) && !string.IsNullOrWhiteSpace(dataDirectory))
        {
            ConfigDirectory = Path.GetFullPath(configDirectory);
            DataDirectory = Path.GetFullPath(dataDirectory);
            return;
        }

        var (resolvedConfig, resolvedData) = ResolvePlatformDirectories();

        ConfigDirectory = !string.IsNullOrWhiteSpace(configDirectory)
            ? Path.GetFullPath(configDirectory)
            : resolvedConfig;

        DataDirectory = !string.IsNullOrWhiteSpace(dataDirectory)
            ? Path.GetFullPath(dataDirectory)
            : resolvedData;
    }

    public void EnsureDirectoriesCreated()
    {
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(DataDirectory);
    }

    private static (string ConfigDir, string DataDir) ResolvePlatformDirectories()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            if (string.IsNullOrWhiteSpace(appData))
            {
                appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Roaming");
            }

            if (string.IsNullOrWhiteSpace(localAppData))
            {
                localAppData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Local");
            }

            var configDir = Path.Combine(appData, AppFolderName);
            var dataDir = Path.Combine(localAppData, AppFolderName);

            return (configDir, dataDir);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var appSupport = Path.Combine(home, "Library", "Application Support", AppFolderName);
            return (appSupport, appSupport);
        }

        // Linux / Unix / FreeBSD fallback per XDG Base Directory specification
        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var xdgConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var configRoot = !string.IsNullOrWhiteSpace(xdgConfig)
            ? xdgConfig
            : Path.Combine(userHome, ".config");

        var xdgData = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var dataRoot = !string.IsNullOrWhiteSpace(xdgData)
            ? xdgData
            : Path.Combine(userHome, ".local", "share");

        return (
            Path.Combine(configRoot, LinuxAppFolderName),
            Path.Combine(dataRoot, LinuxAppFolderName)
        );
    }
}

