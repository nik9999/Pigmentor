namespace Pigmentor.App.Paths;

/// <summary>
/// Cross-platform application paths abstraction separating configuration and runtime data.
/// </summary>
public interface IPigmentorAppPaths
{
    /// <summary>
    /// Directory containing user-level configuration files (e.g. config.json).
    /// </summary>
    string ConfigDirectory { get; }

    /// <summary>
    /// Directory containing runtime state, vector database, and credentials.
    /// </summary>
    string DataDirectory { get; }

    /// <summary>
    /// Absolute path to the configuration file (config.json).
    /// </summary>
    string ConfigFilePath { get; }

    /// <summary>
    /// Absolute default path to the SQLite vector database.
    /// </summary>
    string DefaultDatabasePath { get; }

    /// <summary>
    /// Absolute path to the encrypted secret store file on platforms using file-backed protected storage.
    /// </summary>
    string SecretsFilePath { get; }

    /// <summary>
    /// Ensures both the configuration and data directories exist on disk.
    /// </summary>
    void EnsureDirectoriesCreated();
}

