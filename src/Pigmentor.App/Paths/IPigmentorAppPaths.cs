namespace Pigmentor.App.Paths;

/// <summary>
/// Cross-platform application paths abstraction separating configuration, workspace root, and runtime data.
/// Supports Windows and Linux.
/// </summary>
public interface IPigmentorAppPaths
{
    /// <summary>
    /// Base directory of the application/executable (AppContext.BaseDirectory).
    /// Used as the root for resolving all application-relative paths.
    /// </summary>
    string ApplicationBaseDirectory { get; }

    /// <summary>
    /// Resolves an application path:
    /// - If path is absolute, returns normalized absolute path.
    /// - If path is relative, resolves it relative to ApplicationBaseDirectory (not process CWD).
    /// </summary>
    string ResolveApplicationPath(string path);

    /// <summary>
    /// Directory containing user-level configuration files (config.json).
    /// </summary>
    string ConfigDirectory { get; }

    /// <summary>
    /// Default workspace root path if not explicitly configured by the user.
    /// </summary>
    string DefaultRootPath { get; }

    /// <summary>
    /// Absolute path to the user configuration file (config.json).
    /// </summary>
    string ConfigFilePath { get; }

    /// <summary>
    /// Absolute path to the encrypted secret store file on Windows (DPAPI).
    /// </summary>
    string SecretsFilePath { get; }

    /// <summary>
    /// Resolves and normalizes the effective absolute workspace root path.
    /// </summary>
    string ResolveEffectiveRootPath(string? configuredRootPath);

    /// <summary>
    /// Derives the absolute path to the SQLite vector database from the workspace root.
    /// The database is always &lt;rootPath&gt;/pigmentor.db and is never independently configured.
    /// </summary>
    string GetDatabasePath(string rootPath);

    /// <summary>
    /// Ensures the configuration directory exists.
    /// </summary>
    void EnsureConfigDirectoryCreated();

    /// <summary>
    /// Idempotently ensures the workspace root and the six fixed document silos exist on disk.
    /// Does not delete or overwrite existing files or database.
    /// </summary>
    void EnsureWorkspaceDirectories(string rootPath);
}
