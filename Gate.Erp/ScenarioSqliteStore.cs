// Sqlite backing for scenario number sequences and the generated-id record.
// Tables: sequences (one counter per document prefix), documents (scenario
// event key -> generated number), meta (scenario file, loaded-at). The ERP
// rows themselves are seeded through the adapter's existing APIs; this store
// only mints the numbers the adapter does not sequence (SO/ASN/INV) and
// mirrors every generated id so startup and reset stay deterministic.
using Microsoft.Data.Sqlite;

namespace Gate.Erp;

public sealed class ScenarioSqliteStore : IDisposable
{
    private readonly SqliteConnection _connection;
    private bool _disposed;

    private ScenarioSqliteStore(SqliteConnection connection)
    {
        _connection = connection;
    }

    public static ScenarioSqliteStore Open(string sqlitePath)
    {
        var fullPath = Path.GetFullPath(sqlitePath);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var connection = new SqliteConnection($"Data Source={fullPath}");
        connection.Open();
        var store = new ScenarioSqliteStore(connection);
        store.EnsureSchema();
        return store;
    }

    private void EnsureSchema()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS sequences (
                name TEXT PRIMARY KEY,
                next INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS documents (
                event TEXT PRIMARY KEY,
                doc_type TEXT NOT NULL,
                number TEXT NOT NULL,
                created_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS meta (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public bool IsEmpty()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM documents;";
        return Convert.ToInt64(cmd.ExecuteScalar()) == 0;
    }

    /// <summary>Mints the next number for a prefix (SO-1, ASN-1, INV-1, ...).</summary>
    public string NextNumber(string prefix)
    {
        using var tx = _connection.BeginTransaction();
        long next;
        using (var select = _connection.CreateCommand())
        {
            select.Transaction = tx;
            select.CommandText = "SELECT next FROM sequences WHERE name = @name;";
            select.Parameters.AddWithValue("@name", prefix);
            var raw = select.ExecuteScalar();
            next = raw is null ? 1 : Convert.ToInt64(raw);
        }

        using (var upsert = _connection.CreateCommand())
        {
            upsert.Transaction = tx;
            upsert.CommandText = """
                INSERT INTO sequences (name, next) VALUES (@name, @next)
                ON CONFLICT(name) DO UPDATE SET next = excluded.next;
                """;
            upsert.Parameters.AddWithValue("@name", prefix);
            upsert.Parameters.AddWithValue("@next", next + 1);
            upsert.ExecuteNonQuery();
        }

        tx.Commit();
        return $"{prefix}-{next}";
    }

    public void RecordDocument(string eventKey, string docType, string number, DateTimeOffset createdAt)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO documents (event, doc_type, number, created_at)
            VALUES (@event, @docType, @number, @createdAt)
            ON CONFLICT(event) DO UPDATE SET
                doc_type = excluded.doc_type,
                number = excluded.number,
                created_at = excluded.created_at;
            """;
        cmd.Parameters.AddWithValue("@event", eventKey);
        cmd.Parameters.AddWithValue("@docType", docType);
        cmd.Parameters.AddWithValue("@number", number);
        cmd.Parameters.AddWithValue("@createdAt", createdAt.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public void WriteMeta(string key, string value)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO meta (key, value) VALUES (@key, @value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        cmd.Parameters.AddWithValue("@key", key);
        cmd.Parameters.AddWithValue("@value", value);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<ManifestDocument> ReadDocuments()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT event, doc_type, number FROM documents ORDER BY rowid;";
        using var reader = cmd.ExecuteReader();
        var result = new List<ManifestDocument>();
        while (reader.Read())
        {
            result.Add(new ManifestDocument(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        return result;
    }

    public void Clear()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM documents; DELETE FROM sequences; DELETE FROM meta;";
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _connection.Dispose();
            _disposed = true;
        }
    }
}
