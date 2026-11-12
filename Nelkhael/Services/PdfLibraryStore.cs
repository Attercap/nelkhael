using Microsoft.Data.Sqlite;

namespace Nelkhael.Services;

public sealed class PdfLibraryStore
{
    private readonly string _connectionString;

    public PdfLibraryStore(IConfiguration configuration, IWebHostEnvironment environment)
    {
        var dataPath = configuration["DataPath"] ?? "App_Data\\nelkhael.db";
        if (!Path.IsPathRooted(dataPath))
        {
            dataPath = Path.Combine(environment.ContentRootPath, dataPath);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dataPath))!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(dataPath),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS Documents (
                Path TEXT PRIMARY KEY,
                LastWriteUtcTicks INTEGER NOT NULL,
                FileLength INTEGER NOT NULL,
                ChunkCount INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS Chunks (
                Id TEXT PRIMARY KEY,
                DocumentPath TEXT NOT NULL,
                Source TEXT NOT NULL,
                PageNumber INTEGER NOT NULL,
                Content TEXT NOT NULL,
                Embedding BLOB NOT NULL,
                FOREIGN KEY (DocumentPath) REFERENCES Documents(Path) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS IX_Chunks_DocumentPath ON Chunks(DocumentPath);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> IsCurrentAsync(
        string path,
        long lastWriteUtcTicks,
        long fileLength,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1 FROM Documents
                WHERE Path = $path AND LastWriteUtcTicks = $ticks AND FileLength = $length
            );
            """;
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$ticks", lastWriteUtcTicks);
        command.Parameters.AddWithValue("$length", fileLength);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) != 0;
    }

    public async Task ReplaceDocumentAsync(
        string path,
        long lastWriteUtcTicks,
        long fileLength,
        IReadOnlyList<IndexedChunk> chunks,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM Chunks WHERE DocumentPath = $path;";
            delete.Parameters.AddWithValue("$path", path);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var upsert = connection.CreateCommand())
        {
            upsert.Transaction = transaction;
            upsert.CommandText = """
                INSERT INTO Documents (Path, LastWriteUtcTicks, FileLength, ChunkCount)
                VALUES ($path, $ticks, $length, $count)
                ON CONFLICT(Path) DO UPDATE SET
                    LastWriteUtcTicks = excluded.LastWriteUtcTicks,
                    FileLength = excluded.FileLength,
                    ChunkCount = excluded.ChunkCount;
                """;
            upsert.Parameters.AddWithValue("$path", path);
            upsert.Parameters.AddWithValue("$ticks", lastWriteUtcTicks);
            upsert.Parameters.AddWithValue("$length", fileLength);
            upsert.Parameters.AddWithValue("$count", chunks.Count);
            await upsert.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO Chunks (Id, DocumentPath, Source, PageNumber, Content, Embedding)
                VALUES ($id, $path, $source, $page, $content, $embedding);
                """;
            var idParameter = insert.Parameters.Add("$id", SqliteType.Text);
            var pathParameter = insert.Parameters.Add("$path", SqliteType.Text);
            var sourceParameter = insert.Parameters.Add("$source", SqliteType.Text);
            var pageParameter = insert.Parameters.Add("$page", SqliteType.Integer);
            var contentParameter = insert.Parameters.Add("$content", SqliteType.Text);
            var embeddingParameter = insert.Parameters.Add("$embedding", SqliteType.Blob);

            foreach (var chunk in chunks)
            {
                idParameter.Value = chunk.Id;
                pathParameter.Value = path;
                sourceParameter.Value = chunk.Source;
                pageParameter.Value = chunk.PageNumber;
                contentParameter.Value = chunk.Content;
                embeddingParameter.Value = ToBytes(chunk.Embedding);
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task RemoveDocumentAsync(string path, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();

        await using (var deleteChunks = connection.CreateCommand())
        {
            deleteChunks.Transaction = transaction;
            deleteChunks.CommandText = "DELETE FROM Chunks WHERE DocumentPath = $path;";
            deleteChunks.Parameters.AddWithValue("$path", path);
            await deleteChunks.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var deleteDocument = connection.CreateCommand())
        {
            deleteDocument.Transaction = transaction;
            deleteDocument.CommandText = "DELETE FROM Documents WHERE Path = $path;";
            deleteDocument.Parameters.AddWithValue("$path", path);
            await deleteDocument.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<IndexedChunk>> GetChunksAsync(CancellationToken cancellationToken)
    {
        var chunks = new List<IndexedChunk>();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, DocumentPath, Source, PageNumber, Content, Embedding FROM Chunks;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            chunks.Add(new IndexedChunk(
                reader.GetString(0),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetString(4),
                FromBytes((byte[])reader[5]),
                GetSourceType(reader.GetString(1))));
        }

        return chunks;
    }

    public async Task<(int Documents, int Chunks, int OfficialDocuments, int UnofficialDocuments)> GetCountsAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var officialDocuments = 0;
        var unofficialDocuments = 0;

        await using (var documents = connection.CreateCommand())
        {
            documents.CommandText = "SELECT Path FROM Documents;";
            await using var reader = await documents.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (GetSourceType(reader.GetString(0)) == "Unofficial")
                {
                    unofficialDocuments++;
                }
                else
                {
                    officialDocuments++;
                }
            }
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Chunks;";
        var chunks = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
        return (officialDocuments + unofficialDocuments, chunks, officialDocuments, unofficialDocuments);
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static byte[] ToBytes(float[] values)
    {
        var bytes = new byte[values.Length * sizeof(float)];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static float[] FromBytes(byte[] bytes)
    {
        if (bytes.Length % sizeof(float) != 0)
        {
            throw new InvalidDataException("A stored embedding has an invalid byte length.");
        }

        var values = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);
        return values;
    }

    private static string GetSourceType(string documentPath) =>
        string.Equals(
            Path.GetFileName(Path.GetDirectoryName(documentPath)),
            "Unofficial",
            StringComparison.OrdinalIgnoreCase)
            ? "Unofficial"
            : "Official";
}

public sealed record IndexedChunk(
    string Id,
    string Source,
    int PageNumber,
    string Content,
    float[] Embedding,
    string SourceType);
