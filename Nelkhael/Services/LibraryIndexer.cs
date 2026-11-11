using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;

namespace Nelkhael.Services;

public sealed class LibraryIndexer(
    IConfiguration configuration,
    IWebHostEnvironment environment,
    PdfLibraryStore store,
    OllamaClient ollama,
    ILogger<LibraryIndexer> logger)
{
    private const int ChunkSize = 1200;
    private const int ChunkOverlap = 180;
    private const int EmbeddingBatchSize = 24;
    private const int AnswerChunkCount = 6;
    private readonly SemaphoreSlim _indexLock = new(1, 1);
    private readonly string _libraryPath = ResolveLibraryPath(configuration, environment);

    public async Task<IndexResult> IndexAsync(CancellationToken cancellationToken)
    {
        if (!await _indexLock.WaitAsync(0, cancellationToken))
        {
            return new IndexResult(true, 0, 0, []);
        }

        try
        {
            if (!Directory.Exists(_libraryPath))
            {
                throw new DirectoryNotFoundException($"PDF library directory was not found: {_libraryPath}");
            }

            var ollamaIssue = await ollama.GetEmbeddingModelIssueAsync(cancellationToken);
            if (ollamaIssue is not null)
            {
                return new IndexResult(false, 0, 0, [ollamaIssue]);
            }

            var failures = new List<string>();
            var indexedFiles = 0;
            var skippedFiles = 0;
            var pdfPaths = Directory.GetFiles(_libraryPath, "*.pdf", SearchOption.TopDirectoryOnly);

            foreach (var path in pdfPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fileInfo = new FileInfo(path);
                var lastWriteUtcTicks = fileInfo.LastWriteTimeUtc.Ticks;
                if (await store.IsCurrentAsync(path, lastWriteUtcTicks, fileInfo.Length, cancellationToken))
                {
                    skippedFiles++;
                    continue;
                }

                try
                {
                    var chunks = ReadChunks(path);
                    if (chunks.Count == 0)
                    {
                        throw new InvalidDataException("No searchable text was found. Scanned PDFs require OCR before indexing.");
                    }

                    for (var offset = 0; offset < chunks.Count; offset += EmbeddingBatchSize)
                    {
                        var batch = chunks.Skip(offset).Take(EmbeddingBatchSize).ToArray();
                        var embeddings = await ollama.EmbedAsync(batch.Select(chunk => chunk.Content).ToArray(), cancellationToken);
                        for (var index = 0; index < batch.Length; index++)
                        {
                            chunks[offset + index] = chunks[offset + index] with { Embedding = embeddings[index] };
                        }
                    }

                    await store.ReplaceDocumentAsync(
                        path,
                        lastWriteUtcTicks,
                        fileInfo.Length,
                        chunks,
                        cancellationToken);
                    indexedFiles++;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogError(exception, "Could not index PDF {PdfPath}", path);
                    failures.Add($"{Path.GetFileName(path)}: {exception.Message}");
                }
            }

            return new IndexResult(false, indexedFiles, skippedFiles, failures);
        }
        finally
        {
            _indexLock.Release();
        }
    }

    public async Task<AskResponse?> AnswerAsync(string question, CancellationToken cancellationToken)
    {
        var chunks = await store.GetChunksAsync(cancellationToken);
        if (chunks.Count == 0)
        {
            return null;
        }

        var queryEmbedding = (await ollama.EmbedAsync([question], cancellationToken))[0];
        var matches = chunks
            .Select(chunk => (Chunk: chunk, Score: CosineSimilarity(queryEmbedding, chunk.Embedding)))
            .OrderByDescending(match => match.Score)
            .Take(AnswerChunkCount)
            .ToArray();

        if (matches.Length == 0 || matches[0].Score < 0.15f)
        {
            return new AskResponse(
                "I could not find a sufficiently relevant passage in the indexed PDFs to answer that.",
                []);
        }

        var context = string.Join(
            "\n\n",
            matches.Select(match =>
                $"[Source: {match.Chunk.Source}, page {match.Chunk.PageNumber}]\n{match.Chunk.Content}"));
        var answer = await ollama.AnswerAsync(context, question, cancellationToken);
        var sources = matches
            .Select(match => new SourceCitation(match.Chunk.Source, match.Chunk.PageNumber))
            .Distinct()
            .ToArray();

        return new AskResponse(answer, sources);
    }

    public async Task<LibraryStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var counts = await store.GetCountsAsync(cancellationToken);
        return new LibraryStatus(_libraryPath, Directory.Exists(_libraryPath), _indexLock.CurrentCount == 0, counts.Documents, counts.Chunks);
    }

    private static List<IndexedChunk> ReadChunks(string path)
    {
        var chunks = new List<IndexedChunk>();
        using var document = PdfDocument.Open(path);
        var source = Path.GetFileName(path);

        foreach (var page in document.GetPages())
        {
            var text = Regex.Replace(page.Text, @"\s+", " ").Trim();
            var part = 0;
            foreach (var content in SplitIntoChunks(text))
            {
                var identifier = $"{Path.GetFullPath(path)}|{page.Number}|{part++}";
                var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identifier)));
                chunks.Add(new IndexedChunk(id, source, page.Number, content, []));
            }
        }

        return chunks;
    }

    private static IEnumerable<string> SplitIntoChunks(string text)
    {
        var start = 0;
        while (start < text.Length)
        {
            var end = Math.Min(start + ChunkSize, text.Length);
            if (end < text.Length)
            {
                var boundary = text.LastIndexOf(' ', end - 1, end - start);
                if (boundary > start)
                {
                    end = boundary;
                }
            }

            var chunk = text[start..end].Trim();
            if (chunk.Length > 0)
            {
                yield return chunk;
            }

            if (end >= text.Length)
            {
                yield break;
            }

            start = Math.Max(start + 1, end - ChunkOverlap);
            while (start < text.Length && text[start] != ' ')
            {
                start++;
            }

            while (start < text.Length && text[start] == ' ')
            {
                start++;
            }
        }
    }

    private static float CosineSimilarity(float[] left, float[] right)
    {
        if (left.Length != right.Length)
        {
            throw new InvalidDataException("Stored and query embeddings have different dimensions. Re-index after changing the embedding model.");
        }

        double dot = 0;
        double leftMagnitude = 0;
        double rightMagnitude = 0;
        for (var index = 0; index < left.Length; index++)
        {
            dot += left[index] * right[index];
            leftMagnitude += left[index] * left[index];
            rightMagnitude += right[index] * right[index];
        }

        return leftMagnitude == 0 || rightMagnitude == 0
            ? 0
            : (float)(dot / (Math.Sqrt(leftMagnitude) * Math.Sqrt(rightMagnitude)));
    }

    private static string ResolveLibraryPath(IConfiguration configuration, IWebHostEnvironment environment)
    {
        var configuredPath = configuration["LibraryPath"] ?? "..\\library\\pdfs";
        return Path.GetFullPath(Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(environment.ContentRootPath, configuredPath));
    }
}

public sealed record IndexResult(bool IsRunning, int IndexedFiles, int SkippedFiles, IReadOnlyList<string> Failures);
public sealed record LibraryStatus(string Path, bool DirectoryExists, bool IsIndexing, int Documents, int Chunks);
public sealed record SourceCitation(string Book, int Page);
public sealed record AskResponse(string Answer, IReadOnlyList<SourceCitation> Sources);
