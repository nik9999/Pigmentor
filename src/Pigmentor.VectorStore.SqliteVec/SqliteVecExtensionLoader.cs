using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;

namespace Pigmentor.VectorStore.SqliteVec;

/// <summary>
/// Handles native loading of the sqlite-vec extension (vec0) across Windows, Linux, and macOS,
/// as well as deterministic SQLite provider bootstrapping.
/// </summary>
public static class SqliteVecExtensionLoader
{
    private static int _providerInitialized;

    /// <summary>
    /// Ensures that the SQLite native provider/runtime is initialized before any SQLite connection is opened.
    /// Safe to call multiple times.
    /// </summary>
    public static void EnsureSqliteProvider()
    {
        if (Volatile.Read(ref _providerInitialized) != 0)
        {
            return;
        }

        if (IsProviderOperational())
        {
            Interlocked.Exchange(ref _providerInitialized, 1);
            return;
        }

        try
        {
            SQLitePCL.Batteries_V2.Init();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Failed to initialize SQLite native provider required by Pigmentor.VectorStore.SqliteVec.",
                ex);
        }

        if (!IsProviderOperational())
        {
            throw new InvalidOperationException(
                "Failed to initialize SQLite native provider required by Pigmentor.VectorStore.SqliteVec: provider failed validation.");
        }

        Interlocked.Exchange(ref _providerInitialized, 1);
    }

    private static bool IsProviderOperational()
    {
        try
        {
            return SQLitePCL.raw.sqlite3_libversion_number() > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Loads and verifies the sqlite-vec extension on the provided SQLite connection.
    /// </summary>
    /// <param name="connection">The open SQLite connection.</param>
    /// <param name="explicitPath">Optional explicit path to the native extension file.</param>
    public static void Load(SqliteConnection connection, string? explicitPath = null)
    {
        ArgumentNullException.ThrowIfNull(connection);

        EnsureSqliteProvider();

        connection.EnableExtensions(true);

        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            if (!File.Exists(explicitPath))
            {
                throw new FileNotFoundException(
                    $"Specified sqlite-vec extension path does not exist: '{explicitPath}'",
                    explicitPath);
            }

            try
            {
                connection.LoadExtension(explicitPath);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Failed to load sqlite-vec extension from explicit path '{explicitPath}'.",
                    ex);
            }

            VerifyExtension(connection);
            return;
        }

        var resolvedPath = ResolveExtensionPath();
        if (resolvedPath != null && File.Exists(resolvedPath))
        {
            try
            {
                connection.LoadExtension(resolvedPath);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Failed to load sqlite-vec extension from resolved path '{resolvedPath}'.",
                    ex);
            }

            VerifyExtension(connection);
            return;
        }

        // Fallback to library name in system library search path
        try
        {
            connection.LoadExtension("vec0");
        }
        catch (Exception ex)
        {
            throw new DllNotFoundException(
                $"Could not load sqlite-vec native extension (vec0). Probed '{resolvedPath ?? "null"}' and system library path.",
                ex);
        }

        VerifyExtension(connection);
    }

    /// <summary>
    /// Verifies that the sqlite-vec extension functions are available and responsive.
    /// </summary>
    private static void VerifyExtension(SqliteConnection connection)
    {
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT vec_version();";
            var result = cmd.ExecuteScalar();
            if (result == null || string.IsNullOrWhiteSpace(result.ToString()))
            {
                throw new InvalidOperationException(
                    "sqlite-vec extension verification failed: vec_version() returned an empty result.");
            }
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException(
                "sqlite-vec extension is unavailable or incompatible: failed to execute vec_version().",
                ex);
        }
    }

    /// <summary>
    /// Resolves the expected location of the native sqlite-vec extension library based on the current OS and architecture.
    /// </summary>
    public static string? ResolveExtensionPath()
    {
        string rid;
        string libName;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            rid = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "win-x64",
                Architecture.X86 => "win-x86",
                Architecture.Arm64 => "win-arm64",
                _ => "win-x64"
            };
            libName = "vec0.dll";
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            rid = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "linux-x64",
                Architecture.Arm64 => "linux-arm64",
                _ => "linux-x64"
            };
            libName = "vec0.so";
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            rid = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.Arm64 => "osx-arm64",
                Architecture.X64 => "osx-x64",
                _ => "osx-arm64"
            };
            libName = "vec0.dylib";
        }
        else
        {
            return null;
        }

        // 1. Probing AppContext.BaseDirectory / runtimes / {rid} / native / {libName}
        var path = Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native", libName);
        if (File.Exists(path))
        {
            return path;
        }

        // 2. Probing AppContext.BaseDirectory / {libName}
        path = Path.Combine(AppContext.BaseDirectory, libName);
        if (File.Exists(path))
        {
            return path;
        }

        return null;
    }
}
