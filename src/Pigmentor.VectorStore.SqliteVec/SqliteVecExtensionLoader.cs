using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;

namespace Pigmentor.VectorStore.SqliteVec;

/// <summary>
/// Handles native loading of the sqlite-vec extension (vec0) across Windows, Linux, and macOS.
/// </summary>
public static class SqliteVecExtensionLoader
{
    private static int _providerInitialized;

    /// <summary>
    /// Loads the sqlite-vec extension on the provided SQLite connection.
    /// </summary>
    /// <param name="connection">The open SQLite connection.</param>
    /// <param name="explicitPath">Optional explicit path to the native extension file.</param>
    public static void Load(SqliteConnection connection, string? explicitPath = null)
    {
        ArgumentNullException.ThrowIfNull(connection);

        EnsureProvider();

        connection.EnableExtensions(true);

        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            if (!File.Exists(explicitPath))
            {
                throw new FileNotFoundException(
                    $"Specified sqlite-vec extension path does not exist: '{explicitPath}'",
                    explicitPath);
            }

            connection.LoadExtension(explicitPath);
            return;
        }

        var resolvedPath = ResolveExtensionPath();
        if (resolvedPath != null && File.Exists(resolvedPath))
        {
            connection.LoadExtension(resolvedPath);
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

    private static void EnsureProvider()
    {
        if (Interlocked.Exchange(ref _providerInitialized, 1) == 0)
        {
            try
            {
                SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_dynamic_cdecl());
            }
            catch
            {
                // Ambient provider already registered or dynamic_cdecl not available
            }
        }
    }
}

