using System.Runtime.InteropServices;

namespace Pigmentor.App.Paths;

/// <summary>
/// Cross-platform implementation of <see cref="IPigmentorAppPaths"/> targeting Windows and Linux.
/// Windows: Config in %APPDATA%/Pigmentor, default root in %LOCALAPPDATA%/Pigmentor
/// Linux: Config in $XDG_CONFIG_HOME/pigmentor, default root in $XDG_DATA_HOME/pigmentor
/// </summary>
public sealed class PigmentorAppPaths : IPigmentorAppPaths
{
    private const string AppFolderName = "Pigmentor";
    private const string LinuxAppFolderName = "pigmentor";
    private const string ConfigFileName = "config.json";
    private const string DatabaseFileName = "pigmentor.db";
    private const string SecretsFileName = "secrets.dat";

    public string ApplicationBaseDirectory { get; }
    public string ConfigDirectory { get; }
    public string DefaultRootPath { get; }

    public string ConfigFilePath => Path.Combine(ConfigDirectory, ConfigFileName);
    public string SecretsFilePath => Path.Combine(DefaultRootPath, SecretsFileName);

    public PigmentorAppPaths(
        string? configDirectory = null,
        string? defaultRootPath = null,
        string? applicationBaseDirectory = null)
    {
        ApplicationBaseDirectory = !string.IsNullOrWhiteSpace(applicationBaseDirectory)
            ? Path.GetFullPath(applicationBaseDirectory)
            : AppContext.BaseDirectory;

        if (!string.IsNullOrWhiteSpace(configDirectory) && !string.IsNullOrWhiteSpace(defaultRootPath))
        {
            ConfigDirectory = Path.GetFullPath(configDirectory);
            DefaultRootPath = Path.GetFullPath(defaultRootPath);
            return;
        }

        var (resolvedConfig, resolvedData) = ResolvePlatformDirectories();

        ConfigDirectory = !string.IsNullOrWhiteSpace(configDirectory)
            ? Path.GetFullPath(configDirectory)
            : resolvedConfig;

        DefaultRootPath = !string.IsNullOrWhiteSpace(defaultRootPath)
            ? Path.GetFullPath(defaultRootPath)
            : Path.Combine(ApplicationBaseDirectory, "data");
    }

    public string ResolveApplicationPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var trimmed = path.Trim();
        if (Path.IsPathRooted(trimmed))
        {
            return Path.GetFullPath(trimmed);
        }

        var direct = Path.GetFullPath(Path.Combine(ApplicationBaseDirectory, trimmed));
        if (File.Exists(direct) || Directory.Exists(direct))
        {
            return direct;
        }

        // Cross-platform case fallback (e.g. Models/ vs models/ on case-sensitive filesystems like Linux)
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var fallback = ResolveCaseInsensitivePath(ApplicationBaseDirectory, trimmed);
            if (fallback != null)
            {
                return fallback;
            }
        }

        return direct;
    }

    private static string? ResolveCaseInsensitivePath(string baseDir, string relativePath)
    {
        var parts = relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        var current = baseDir;

        foreach (var part in parts)
        {
            if (!Directory.Exists(current))
            {
                return null;
            }

            var entries = Directory.GetFileSystemEntries(current);
            var match = entries.FirstOrDefault(e => string.Equals(Path.GetFileName(e), part, StringComparison.OrdinalIgnoreCase));
            if (match == null)
            {
                return null;
            }

            current = match;
        }

        return Path.GetFullPath(current);
    }

    public string ResolveEffectiveRootPath(string? configuredRootPath)
    {
        if (string.IsNullOrWhiteSpace(configuredRootPath))
        {
            return DefaultRootPath;
        }

        return ResolveApplicationPath(configuredRootPath);
    }

    public string GetDatabasePath(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        var normalizedRoot = ResolveEffectiveRootPath(rootPath);
        return Path.Combine(normalizedRoot, DatabaseFileName);
    }

    public void EnsureConfigDirectoryCreated()
    {
        Directory.CreateDirectory(ConfigDirectory);
    }

    public void EnsureWorkspaceDirectories(string rootPath)
    {
        var normalizedRoot = ResolveEffectiveRootPath(rootPath);
        Directory.CreateDirectory(normalizedRoot);

        foreach (var silo in PigmentorSilos.All)
        {
            var siloDir = Path.Combine(normalizedRoot, silo);
            Directory.CreateDirectory(siloDir);
        }
    }

    private static (string ConfigDir, string DefaultDataDir) ResolvePlatformDirectories()
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

        // Linux / Unix per XDG Base Directory specification
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
