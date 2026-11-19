using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;

namespace Nelkhael.Services;

public sealed class LibraryIndexer(
    IConfiguration configuration,
    IWebHostEnvironment environment,
    PdfLibraryStore store,
    OllamaClient ollama,
    PublicWebPageClient webPages,
    ILogger<LibraryIndexer> logger)
{
    private const int ChunkSize = 1200;
    private const int ChunkOverlap = 180;
    private const int EmbeddingBatchSize = 24;
    private const int AnswerChunkCount = 6;
    private static readonly string[] SourceTypes = ["Official", "Unofficial"];
    private static readonly HashSet<string> SupportedExtensions =
        new([".pdf", ".txt", ".html", ".htm", ".json", ".php"], StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions WebSourceJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _indexLock = new(1, 1);
    private readonly string _libraryPath = ResolveLibraryPath(configuration, environment);

    public async Task<IndexResult> IndexAsync(CancellationToken cancellationToken)
    {
        if (!await _indexLock.WaitAsync(0, cancellationToken))
        {
            return new IndexResult(true, 0, 0, 0, []);
        }

        try
        {
            if (!Directory.Exists(_libraryPath))
            {
                throw new DirectoryNotFoundException($"Library directory was not found: {_libraryPath}");
            }

            var sourceDirectories = SourceTypes
                .Select(sourceType => (SourceType: sourceType, Path: Path.Combine(_libraryPath, sourceType)))
                .ToArray();
            var missingDirectory = sourceDirectories.FirstOrDefault(directory => !Directory.Exists(directory.Path));
            if (missingDirectory.Path is not null)
            {
                throw new DirectoryNotFoundException(
                    $"{missingDirectory.SourceType} library directory was not found: {missingDirectory.Path}");
            }

            var documentPaths = sourceDirectories
                .SelectMany(directory => Directory.GetFiles(directory.Path, "*", SearchOption.TopDirectoryOnly)
                    .Where(IsSupportedFile)
                    .Where(path => !directory.SourceType.Equals("Unofficial", StringComparison.OrdinalIgnoreCase)
                        || !Path.GetFileName(path).Equals("links.json", StringComparison.OrdinalIgnoreCase))
                    .Select(path => (Path: path, directory.SourceType)))
                .ToArray();

            var failures = new List<string>();
            var webSources = new List<WebSource>();
            var webManifestValid = true;
            var webManifestPath = Path.Combine(_libraryPath, "Unofficial", "links.json");
            if (File.Exists(webManifestPath))
            {
                try
                {
                    var manifest = await File.ReadAllTextAsync(webManifestPath, cancellationToken);
                    var entries = JsonSerializer.Deserialize<WebSource?[]>(manifest, WebSourceJsonOptions)
                        ?? throw new InvalidDataException("The links manifest must contain a JSON array.");
                    for (var index = 0; index < entries.Length; index++)
                    {
                        var entry = entries[index];
                        if (entry is null)
                        {
                            webManifestValid = false;
                            failures.Add($"links.json entry {index + 1}: entry must be an object.");
                            continue;
                        }

                        if (!entry.Enabled)
                        {
                            continue;
                        }

                        try
                        {
                            var topic = NormalizeWebSourceLabel(entry.Topic, "topic");
                            var title = NormalizeWebSourceLabel(entry.Title, "title");
                            var url = PublicWebPageClient.NormalizeUrl(entry.Url);
                            webSources.Add(entry with { Topic = topic, Title = title, Url = url });
                        }
                        catch (InvalidDataException exception)
                        {
                            webManifestValid = false;
                            failures.Add($"links.json entry {index + 1}: {exception.Message}");
                        }
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
                {
                    webManifestValid = false;
                    logger.LogError(exception, "Could not read web links manifest {ManifestPath}", webManifestPath);
                    failures.Add($"links.json: {exception.Message}");
                }
            }

            var removedFiles = await RemoveMissingDocumentsAsync(sourceDirectories, documentPaths, cancellationToken);
            if (webManifestValid)
            {
                removedFiles += await RemoveMissingWebDocumentsAsync(webSources, cancellationToken);
            }

            var ollamaIssue = await ollama.GetEmbeddingModelIssueAsync(cancellationToken);
            if (ollamaIssue is not null)
            {
                failures.Add(ollamaIssue);
                return new IndexResult(false, 0, 0, removedFiles, failures);
            }

            var indexedFiles = 0;
            var skippedFiles = 0;

            foreach (var (path, sourceType) in documentPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fileInfo = new FileInfo(path);
                var lastWriteUtcTicks = fileInfo.LastWriteTimeUtc.Ticks;
                var legacyPath = Path.Combine(_libraryPath, "pdfs", Path.GetFileName(path));
                if (await store.IsCurrentAsync(path, lastWriteUtcTicks, fileInfo.Length, cancellationToken))
                {
                    if (Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
                    {
                        await store.RemoveDocumentAsync(legacyPath, cancellationToken);
                    }

                    skippedFiles++;
                    continue;
                }

                try
                {
                    var chunks = ReadChunks(path, sourceType);
                    if (chunks.Count == 0)
                    {
                        throw new InvalidDataException("No searchable text was found.");
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
                    if (Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
                    {
                        await store.RemoveDocumentAsync(legacyPath, cancellationToken);
                    }

                    indexedFiles++;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogError(exception, "Could not index document {DocumentPath}", path);
                    failures.Add($"{Path.GetFileName(path)}: {exception.Message}");
                }
            }

            var indexedUrls = new HashSet<string>(StringComparer.Ordinal);
            foreach (var source in webSources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!indexedUrls.Add(source.Url))
                {
                    continue;
                }

                try
                {
                    var html = await webPages.FetchHtmlAsync(source.Url, cancellationToken);
                    var fingerprint = Encoding.UTF8.GetBytes($"{source.Topic}\n{source.Title}\n{html}");
                    var lastWriteUtcTicks = BitConverter.ToInt64(SHA256.HashData(fingerprint), 0);
                    var fileLength = Encoding.UTF8.GetByteCount(html);
                    if (await store.IsCurrentAsync(source.Url, lastWriteUtcTicks, fileLength, cancellationToken))
                    {
                        skippedFiles++;
                        continue;
                    }

                    var chunks = ReadWebPageChunks(html, source);
                    if (chunks.Count == 0)
                    {
                        throw new InvalidDataException("No searchable text was found.");
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
                        source.Url,
                        lastWriteUtcTicks,
                        fileLength,
                        chunks,
                        cancellationToken);
                    indexedFiles++;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogError(exception, "Could not index web page {PageUrl}", source.Url);
                    failures.Add($"{source.Title} ({source.Url}): {exception.Message}");
                }
            }

            return new IndexResult(false, indexedFiles, skippedFiles, removedFiles, failures);
        }
        finally
        {
            _indexLock.Release();
        }
    }

    private async Task<int> RemoveMissingDocumentsAsync(
        (string SourceType, string Path)[] sourceDirectories,
        (string Path, string SourceType)[] documentPaths,
        CancellationToken cancellationToken)
    {
        var pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var sourceDirectoryPaths = sourceDirectories
            .Select(directory => Path.GetFullPath(directory.Path))
            .ToHashSet(pathComparer);
        var currentDocumentPaths = documentPaths
            .Select(document => Path.GetFullPath(document.Path))
            .ToHashSet(pathComparer);

        var removedFiles = 0;
        foreach (var indexedPath in await store.GetDocumentPathsAsync(cancellationToken))
        {
            var fullIndexedPath = Path.GetFullPath(indexedPath);
            var parentDirectory = Path.GetDirectoryName(fullIndexedPath);
            if (parentDirectory is not null
                && sourceDirectoryPaths.Contains(parentDirectory)
                && !currentDocumentPaths.Contains(fullIndexedPath))
            {
                await store.RemoveDocumentAsync(indexedPath, cancellationToken);
                removedFiles++;
            }
        }

        return removedFiles;
    }

    private async Task<int> RemoveMissingWebDocumentsAsync(
        IReadOnlyCollection<WebSource> webSources,
        CancellationToken cancellationToken)
    {
        var currentUrls = webSources
            .Select(source => source.Url)
            .ToHashSet(StringComparer.Ordinal);
        var removedPages = 0;
        foreach (var indexedPath in await store.GetDocumentPathsAsync(cancellationToken))
        {
            if (Uri.TryCreate(indexedPath, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                && !currentUrls.Contains(indexedPath))
            {
                await store.RemoveDocumentAsync(indexedPath, cancellationToken);
                removedPages++;
            }
        }

        return removedPages;
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
                "I could not find a sufficiently relevant passage in the indexed library to answer that.",
                []);
        }

        var context = string.Join(
            "\n\n",
            matches.Select(match =>
                $"[{match.Chunk.SourceType} source: {match.Chunk.Source}, {GetLocationLabel(match.Chunk)} {match.Chunk.PageNumber}{(match.Chunk.SourceUrl is null ? "" : $", URL {match.Chunk.SourceUrl}")}]\n{match.Chunk.Content}"));
        var answer = await ollama.AnswerAsync(context, question, cancellationToken);
        var sources = matches
            .Select(match => new SourceCitation(
                match.Chunk.Source,
                match.Chunk.PageNumber,
                match.Chunk.SourceType,
                match.Chunk.SourceUrl))
            .Distinct()
            .ToArray();

        return new AskResponse(answer, sources);
    }

    public async Task<bool> FlushIndexAsync(CancellationToken cancellationToken)
    {
        if (!await _indexLock.WaitAsync(0, cancellationToken))
        {
            return false;
        }

        try
        {
            await store.ClearAsync(cancellationToken);
            return true;
        }
        finally
        {
            _indexLock.Release();
        }
    }

    public async Task<LibraryStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var counts = await store.GetCountsAsync(cancellationToken);
        var directoriesExist = Directory.Exists(_libraryPath)
            && SourceTypes.All(sourceType => Directory.Exists(Path.Combine(_libraryPath, sourceType)));
        return new LibraryStatus(
            _libraryPath,
            directoriesExist,
            _indexLock.CurrentCount == 0,
            counts.Documents,
            counts.Chunks,
            counts.OfficialDocuments,
            counts.UnofficialDocuments);
    }

    private static List<IndexedChunk> ReadChunks(string path, string sourceType)
    {
        return Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase)
            ? ReadPdfChunks(path, sourceType)
            : ReadTextChunks(path, sourceType);
    }

    private static List<IndexedChunk> ReadPdfChunks(string path, string sourceType)
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
                chunks.Add(new IndexedChunk(id, source, page.Number, content, [], sourceType));
            }
        }

        return chunks;
    }

    private static List<IndexedChunk> ReadTextChunks(string path, string sourceType)
    {
        var text = File.ReadAllText(path);
        if (Path.GetExtension(path).Equals(".html", StringComparison.OrdinalIgnoreCase)
            || Path.GetExtension(path).Equals(".htm", StringComparison.OrdinalIgnoreCase))
        {
            text = ExtractHtmlText(text);
        }

        return CreateTextChunks(text, sourceType, Path.GetFileName(path), Path.GetFullPath(path));
    }

    private static List<IndexedChunk> ReadWebPageChunks(string html, WebSource source)
    {
        var text = $"{source.Topic}. {source.Title}. {ExtractHtmlText(html)}";
        return CreateTextChunks(text, "Unofficial", source.Title, source.Url, source.Url);
    }

    private static string ExtractHtmlText(string html)
    {
        var text = Regex.Replace(html, @"(?is)<(script|style)\b[^>]*>.*?</\1\s*>", " ");
        text = Regex.Replace(text, @"(?s)<!--.*?-->", " ");
        text = Regex.Replace(text, @"<[^>]*>", " ");
        return WebUtility.HtmlDecode(text);
    }

    private static List<IndexedChunk> CreateTextChunks(
        string text,
        string sourceType,
        string source,
        string identifier,
        string? sourceUrl = null)
    {
        text = Regex.Replace(text, @"\s+", " ").Trim();
        var chunks = new List<IndexedChunk>();
        var section = 1;
        foreach (var content in SplitIntoChunks(text))
        {
            var chunkIdentifier = $"{identifier}|{section}";
            var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(chunkIdentifier)));
            chunks.Add(new IndexedChunk(id, source, section++, content, [], sourceType, sourceUrl));
        }

        return chunks;
    }

    private static bool IsSupportedFile(string path) =>
        SupportedExtensions.Contains(Path.GetExtension(path));

    private static string GetLocationLabel(IndexedChunk chunk) =>
        chunk.SourceUrl is null && Path.GetExtension(chunk.Source).Equals(".pdf", StringComparison.OrdinalIgnoreCase)
            ? "page"
            : "section";

    private static string NormalizeWebSourceLabel(string value, string label)
    {
        var normalized = Regex.Replace(value ?? "", @"\s+", " ").Trim();
        return normalized.Length == 0
            ? throw new InvalidDataException($"Web source {label} must not be empty.")
            : normalized;
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
        var configuredPath = configuration["LibraryPath"] ?? "..\\library";
        return Path.GetFullPath(Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(environment.ContentRootPath, configuredPath));
    }
}

public sealed record IndexResult(bool IsRunning, int IndexedFiles, int SkippedFiles, int RemovedFiles, IReadOnlyList<string> Failures);
public sealed record LibraryStatus(
    string Path,
    bool DirectoryExists,
    bool IsIndexing,
    int Documents,
    int Chunks,
    int OfficialDocuments,
    int UnofficialDocuments);
public sealed record SourceCitation(string Book, int Page, string SourceType, string? Url = null);
public sealed record AskResponse(string Answer, IReadOnlyList<SourceCitation> Sources);
public sealed record WebSource
{
    public required string Topic { get; init; }
    public required string Url { get; init; }
    public required string Title { get; init; }
    public required bool Enabled { get; init; }
}
