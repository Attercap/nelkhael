using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Nelkhael.Services;

namespace Nelkhael.Tests;

[TestClass]
public sealed class LibraryIndexerTests
{
    private string _root = "";
    private string _libraryPath = "";
    private PdfLibraryStore _store = null!;
    private IConfiguration _configuration = null!;
    private HttpClient? _httpClient;

    [TestInitialize]
    public async Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), "Nelkhael.Tests", Guid.NewGuid().ToString("N"));
        _libraryPath = Path.Combine(_root, "library");
        Directory.CreateDirectory(Path.Combine(_libraryPath, "Official"));
        Directory.CreateDirectory(Path.Combine(_libraryPath, "Unofficial"));

        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_root, "index.db"),
                ["LibraryPath"] = _libraryPath,
                ["Ollama:Endpoint"] = "http://ollama.test",
                ["Ollama:EmbeddingModel"] = "nomic-embed-text"
            })
            .Build();
        _store = new PdfLibraryStore(_configuration, CreateEnvironment());
        await _store.InitializeAsync();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _httpClient?.Dispose();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [TestMethod]
    public async Task IndexAsync_RemovesMissingPdfsAndPreservesCurrentAndUnmanagedDocuments()
    {
        var existingPdf = Path.Combine(_libraryPath, "Official", "current.pdf");
        await File.WriteAllTextAsync(existingPdf, "already indexed");
        var existingFile = new FileInfo(existingPdf);
        await AddIndexedDocumentAsync(
            existingPdf,
            "current-chunk",
            existingFile.LastWriteTimeUtc.Ticks,
            existingFile.Length);

        var missingOfficialPdf = Path.Combine(_libraryPath, "Official", "deleted-official.pdf");
        var missingUnofficialPdf = Path.Combine(_libraryPath, "Unofficial", "deleted-unofficial.pdf");
        await AddIndexedDocumentAsync(missingOfficialPdf, "deleted-official-chunk");
        await AddIndexedDocumentAsync(missingUnofficialPdf, "deleted-unofficial-chunk");

        var unmanagedPdf = Path.Combine(_libraryPath, "Archive", "unmanaged.pdf");
        await AddIndexedDocumentAsync(unmanagedPdf, "unmanaged-chunk");

        var indexer = CreateIndexer(embeddingModelAvailable: true);
        var result = await indexer.IndexAsync(CancellationToken.None);

        Assert.AreEqual(1, result.SkippedFiles);
        Assert.AreEqual(2, result.RemovedFiles);
        Assert.IsEmpty(result.Failures);
        CollectionAssert.AreEquivalent(
            new[] { existingPdf, unmanagedPdf },
            (await _store.GetDocumentPathsAsync(CancellationToken.None)).ToArray());
        CollectionAssert.AreEquivalent(
            new[] { "current-chunk", "unmanaged-chunk" },
            (await _store.GetChunksAsync(CancellationToken.None)).Select(chunk => chunk.Id).ToArray());

        var secondResult = await indexer.IndexAsync(CancellationToken.None);
        Assert.AreEqual(0, secondResult.RemovedFiles);
    }

    [TestMethod]
    public async Task IndexAsync_RemovesMissingPdfsWhenEmbeddingModelIsUnavailable()
    {
        var missingPdf = Path.Combine(_libraryPath, "Official", "deleted.pdf");
        await AddIndexedDocumentAsync(missingPdf, "deleted-chunk");

        var result = await CreateIndexer(embeddingModelAvailable: false).IndexAsync(CancellationToken.None);

        Assert.AreEqual(1, result.RemovedFiles);
        Assert.HasCount(1, result.Failures);
        Assert.IsEmpty(await _store.GetDocumentPathsAsync(CancellationToken.None));
        Assert.IsEmpty(await _store.GetChunksAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task IndexAsync_DoesNotRemoveDocumentsWhenSourceDirectoryIsMissing()
    {
        var missingPdf = Path.Combine(_libraryPath, "Official", "deleted.pdf");
        await AddIndexedDocumentAsync(missingPdf, "deleted-chunk");
        Directory.Delete(Path.Combine(_libraryPath, "Unofficial"));

        var indexer = CreateIndexer(embeddingModelAvailable: true);

        await Assert.ThrowsExactlyAsync<DirectoryNotFoundException>(
            () => indexer.IndexAsync(CancellationToken.None));

        CollectionAssert.AreEqual(
            new[] { missingPdf },
            (await _store.GetDocumentPathsAsync(CancellationToken.None)).ToArray());
    }

    [TestMethod]
    public async Task IndexAsync_IndexesSupportedTextFilesAndIgnoresUnsupportedFiles()
    {
        var officialPath = Path.Combine(_libraryPath, "Official");
        await File.WriteAllTextAsync(Path.Combine(officialPath, "rules.txt"), "A plain text rule.");
        await File.WriteAllTextAsync(
            Path.Combine(officialPath, "rules.html"),
            "<html><head><style>.hidden{display:none}</style><script>hidden script</script></head><body><p>HTML &amp; text rule</p></body></html>");
        await File.WriteAllTextAsync(Path.Combine(officialPath, "rules.json"), """{"name":"JSON rule","detail":"structured text"}""");
        await File.WriteAllTextAsync(Path.Combine(officialPath, "rules.PHP"), "<?php echo 'PHP rule'; ?>");
        await File.WriteAllTextAsync(Path.Combine(officialPath, "image.png"), "not indexed");

        var result = await CreateIndexer(embeddingModelAvailable: true).IndexAsync(CancellationToken.None);

        Assert.AreEqual(4, result.IndexedFiles);
        Assert.IsEmpty(result.Failures);
        var chunks = await _store.GetChunksAsync(CancellationToken.None);
        Assert.HasCount(4, chunks);
        Assert.Contains("plain text rule", chunks.Single(chunk => chunk.Source == "rules.txt").Content);
        var html = chunks.Single(chunk => chunk.Source == "rules.html").Content;
        Assert.Contains("HTML & text rule", html);
        Assert.DoesNotContain("hidden", html);
        Assert.Contains("JSON rule", chunks.Single(chunk => chunk.Source == "rules.json").Content);
        Assert.Contains("PHP rule", chunks.Single(chunk => chunk.Source == "rules.PHP").Content);
    }

    [TestMethod]
    public async Task FlushIndexAsync_RemovesAllIndexedDocumentsAndChunksWithoutDeletingPdfs()
    {
        var pdfPath = Path.Combine(_libraryPath, "Official", "book.pdf");
        await File.WriteAllTextAsync(pdfPath, "source PDF remains");
        var fileInfo = new FileInfo(pdfPath);
        await AddIndexedDocumentAsync(
            pdfPath,
            "book-chunk",
            fileInfo.LastWriteTimeUtc.Ticks,
            fileInfo.Length);
        await AddIndexedDocumentAsync(
            Path.Combine(_libraryPath, "Archive", "unmanaged.pdf"),
            "unmanaged-chunk");

        var flushed = await CreateIndexer(embeddingModelAvailable: true)
            .FlushIndexAsync(CancellationToken.None);

        Assert.IsTrue(flushed);
        Assert.IsTrue(File.Exists(pdfPath));
        Assert.IsEmpty(await _store.GetDocumentPathsAsync(CancellationToken.None));
        Assert.IsEmpty(await _store.GetChunksAsync(CancellationToken.None));
    }

    private async Task AddIndexedDocumentAsync(
        string path,
        string chunkId,
        long lastWriteUtcTicks = 1,
        long fileLength = 1)
    {
        var sourceType = Path.GetFileName(Path.GetDirectoryName(path)) == "Unofficial"
            ? "Unofficial"
            : "Official";
        await _store.ReplaceDocumentAsync(
            path,
            lastWriteUtcTicks,
            fileLength,
            [new IndexedChunk(chunkId, Path.GetFileName(path), 1, "indexed text", [0.1f], sourceType)],
            CancellationToken.None);
    }

    private LibraryIndexer CreateIndexer(bool embeddingModelAvailable)
    {
        _httpClient = new HttpClient(new OllamaHandler(embeddingModelAvailable));
        var ollama = new OllamaClient(_httpClient, _configuration);
        return new LibraryIndexer(
            _configuration,
            CreateEnvironment(),
            _store,
            ollama,
            NullLogger<LibraryIndexer>.Instance);
    }

    private IWebHostEnvironment CreateEnvironment() => new TestWebHostEnvironment(_root);

    private sealed class TestWebHostEnvironment(string contentRootPath) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = nameof(LibraryIndexerTests);
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = contentRootPath;
        public string EnvironmentName { get; set; } = "Development";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = contentRootPath;
    }

    private sealed class OllamaHandler(bool embeddingModelAvailable) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath == "/api/tags")
            {
                var models = embeddingModelAvailable
                    ? """{"models":[{"name":"nomic-embed-text"}]}"""
                    : """{"models":[]}""";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(models, Encoding.UTF8, "application/json")
                };
            }

            if (request.RequestUri?.AbsolutePath == "/api/embed")
            {
                await using var stream = await request.Content!.ReadAsStreamAsync(cancellationToken);
                using var body = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                var count = body.RootElement.GetProperty("input").GetArrayLength();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new
                    {
                        embeddings = Enumerable.Range(0, count).Select(_ => new[] { 0.1f, 0.2f }).ToArray()
                    })
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }
}
