using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Pigmentor.VectorStore.SqliteVec;

/// <summary>
/// Single authority for SQLite schema initialization, version inspection, non-destructive migration,
/// and schema validation across <see cref="SqliteVecStore"/> and <see cref="SqliteIngestionStateStore"/>.
/// </summary>
internal static class PigmentorSqliteSchema
{
    public const int CurrentVersion = 2;

    /// <summary>
    /// Reads the current <c>PRAGMA user_version</c> from the SQLite connection.
    /// </summary>
    public static int ReadVersion(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        var result = cmd.ExecuteScalar();
        return result is null or DBNull ? 0 : Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Initializes or validates schema for <see cref="SqliteIngestionStateStore"/>.
    /// Creates the ingestion state tables and sets user_version to 2 if the database is newly created.
    /// Migrates v1 databases to v2. Validates schema version on existing databases.
    /// </summary>
    public static void EnsureInitializedForStateStore(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using (var fkCmd = connection.CreateCommand())
        {
            fkCmd.CommandText = "PRAGMA journal_mode = WAL; PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
            fkCmd.ExecuteNonQuery();
        }

        var userVersion = ReadVersion(connection);

        if (userVersion == 0)
        {
            var hasForeignTables = CheckHasTables(connection, excludeIngestion: true);
            if (hasForeignTables)
            {
                throw new InvalidOperationException(
                    $"Cannot open unversioned SQLite database with existing tables. Expected PRAGMA user_version = {CurrentVersion} or migratable version.");
            }

            CreateIngestionTables(connection);
            SetUserVersion(connection, CurrentVersion);
        }
        else if (userVersion == 1)
        {
            MigrateV1ToV2(connection);
        }
        else if (userVersion == CurrentVersion)
        {
            CreateIngestionTables(connection);
        }
        else
        {
            throw new InvalidOperationException(
                $"Unsupported Pigmentor SQLite schema version {userVersion}. Expected version {CurrentVersion} or migratable version.");
        }
    }

    /// <summary>
    /// Initializes or validates schema for <see cref="SqliteVecStore"/>.
    /// Ensures all vector and ingestion tables are created, validated, and user_version is 2.
    /// </summary>
    public static void EnsureInitializedForVectorStore(
        SqliteConnection connection,
        int dimensions,
        string embeddingModel)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dimensions);
        ArgumentException.ThrowIfNullOrWhiteSpace(embeddingModel);

        using (var fkCmd = connection.CreateCommand())
        {
            fkCmd.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
            fkCmd.ExecuteNonQuery();
        }

        var userVersion = ReadVersion(connection);

        if (userVersion == 0)
        {
            var hasExistingTables = CheckHasTables(connection, excludeIngestion: false);
            if (hasExistingTables)
            {
                throw new InvalidOperationException(
                    $"Cannot open unversioned SQLite database with existing Pigmentor tables. Expected PRAGMA user_version = {CurrentVersion} or migratable version.");
            }

            CreateFullV2Schema(connection, dimensions, embeddingModel);
            SetUserVersion(connection, CurrentVersion);
        }
        else if (userVersion == 1)
        {
            MigrateV1ToV2(connection);
            ValidateV2(connection, requireVectorTables: true);
        }
        else if (userVersion == CurrentVersion)
        {
            // If SqliteIngestionStateStore was opened first on an empty database,
            // user_version is 2 but vector tables (chunks, vec_chunks) might not exist yet.
            if (!CheckHasVectorTables(connection))
            {
                CreateVectorTables(connection, dimensions, embeddingModel);
            }

            ValidateV2(connection, requireVectorTables: true);
        }
        else
        {
            throw new InvalidOperationException(
                $"Unsupported Pigmentor SQLite schema version {userVersion}. Expected version {CurrentVersion} or migratable version.");
        }
    }

    /// <summary>
    /// Non-destructively migrates an existing Pigmentor schema from v1 to v2,
    /// adding the persistent ingestion state tables without modifying vector data.
    /// </summary>
    public static void MigrateV1ToV2(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        CreateIngestionTables(connection);
        SetUserVersion(connection, CurrentVersion);
    }

    /// <summary>
    /// Validates that all required tables exist in the schema.
    /// </summary>
    public static void ValidateV2(SqliteConnection connection, bool requireVectorTables = true)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var cmd = connection.CreateCommand();
        if (requireVectorTables)
        {
            cmd.CommandText = @"
                SELECT COUNT(*) FROM sqlite_master 
                WHERE type IN ('table', 'shadow') 
                  AND name IN ('chunks', 'pigmentor_metadata', 'vec_chunks', 'ingestion_documents', 'ingestion_document_chunks');
            ";
            var count = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            if (count < 5)
            {
                throw new InvalidOperationException("Incompatible SQLite schema: required Pigmentor tables are missing.");
            }
        }
        else
        {
            cmd.CommandText = @"
                SELECT COUNT(*) FROM sqlite_master 
                WHERE type IN ('table', 'shadow') 
                  AND name IN ('ingestion_documents', 'ingestion_document_chunks');
            ";
            var count = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            if (count < 2)
            {
                throw new InvalidOperationException("Incompatible SQLite schema: required Pigmentor ingestion tables are missing.");
            }
        }
    }

    private static void CreateIngestionTables(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS ingestion_documents (
                source_id TEXT NOT NULL,
                document_id TEXT NOT NULL,
                content_hash TEXT NOT NULL,
                metadata_hash TEXT NOT NULL,
                version INTEGER NOT NULL,
                status TEXT NOT NULL,
                last_successful_ingestion TEXT NULL,
                PRIMARY KEY (source_id, document_id)
            );

            CREATE TABLE IF NOT EXISTS ingestion_document_chunks (
                source_id TEXT NOT NULL,
                document_id TEXT NOT NULL,
                chunk_id TEXT NOT NULL,
                PRIMARY KEY (source_id, document_id, chunk_id),
                FOREIGN KEY (source_id, document_id)
                    REFERENCES ingestion_documents(source_id, document_id)
                    ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS idx_ingestion_docs_status
                ON ingestion_documents(status);

            CREATE INDEX IF NOT EXISTS idx_ingestion_docs_document_id
                ON ingestion_documents(document_id);
        ";
        cmd.ExecuteNonQuery();
    }

    private static void CreateVectorTables(SqliteConnection connection, int dimensions, string embeddingModel)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $@"
            CREATE TABLE IF NOT EXISTS pigmentor_metadata (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS chunks (
                row_id INTEGER PRIMARY KEY AUTOINCREMENT,
                chunk_id TEXT UNIQUE NOT NULL,
                document_id TEXT NOT NULL,
                chunk_index INTEGER NOT NULL,
                text TEXT NOT NULL,
                start_char_offset INTEGER NOT NULL,
                end_char_offset INTEGER NOT NULL,
                silo TEXT NOT NULL,
                department TEXT NOT NULL,
                office TEXT NOT NULL,
                confidentiality_level TEXT NOT NULL,
                document_type TEXT NOT NULL,
                fiscal_year INTEGER NOT NULL,
                author TEXT NOT NULL,
                last_modified TEXT NOT NULL,
                title TEXT NOT NULL,
                embedding_model TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_chunks_document_id ON chunks(document_id);
            CREATE INDEX IF NOT EXISTS idx_chunks_department ON chunks(department);
            CREATE INDEX IF NOT EXISTS idx_chunks_silo ON chunks(silo);
            CREATE INDEX IF NOT EXISTS idx_chunks_office ON chunks(office);

            CREATE VIRTUAL TABLE IF NOT EXISTS vec_chunks USING vec0(
                embedding float[{dimensions}] distance_metric=cosine
            );

            INSERT OR REPLACE INTO pigmentor_metadata (key, value) VALUES ('dimensions', '{dimensions}');
            INSERT OR REPLACE INTO pigmentor_metadata (key, value) VALUES ('embedding_model', $embedding_model);
        ";
        cmd.Parameters.AddWithValue("$embedding_model", embeddingModel);
        cmd.ExecuteNonQuery();
    }

    private static void CreateFullV2Schema(SqliteConnection connection, int dimensions, string embeddingModel)
    {
        CreateVectorTables(connection, dimensions, embeddingModel);
        CreateIngestionTables(connection);
    }

    private static bool CheckHasTables(SqliteConnection connection, bool excludeIngestion)
    {
        using var cmd = connection.CreateCommand();
        if (excludeIngestion)
        {
            cmd.CommandText = @"
                SELECT COUNT(*) FROM sqlite_master 
                WHERE type IN ('table', 'shadow') 
                  AND name NOT LIKE 'sqlite_%'
                  AND name NOT IN ('ingestion_documents', 'ingestion_document_chunks');
            ";
        }
        else
        {
            cmd.CommandText = @"
                SELECT COUNT(*) FROM sqlite_master 
                WHERE type IN ('table', 'shadow') 
                  AND name NOT LIKE 'sqlite_%';
            ";
        }
        var count = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        return count > 0;
    }

    private static bool CheckHasVectorTables(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT COUNT(*) FROM sqlite_master 
            WHERE type IN ('table', 'shadow') 
              AND name IN ('chunks', 'pigmentor_metadata', 'vec_chunks');
        ";
        var count = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        return count >= 3;
    }

    private static void SetUserVersion(SqliteConnection connection, int version)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA user_version = {version};";
        cmd.ExecuteNonQuery();
    }
}
