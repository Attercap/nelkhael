# Nelkhael

Nelkhael is a local-first .NET 10 web app for asking questions about files in `library/Official` and `library/Unofficial`. It extracts text from PDFs, TXT, HTML, JSON, and PHP files, creates local embeddings with Ollama, stores the index in SQLite, and returns concise paraphrased answers with source type and file locations.

In theory, this can work for any RPG ruleset, but it's advised to not combine multiple tabletop rpgs under a single application in this version.

## Disclaimer

PDFs and db have not been provided to avoid sharing copyrighted material from Steve Jackson Games.

## Requirements

- .NET 10 SDK
- [Ollama](https://ollama.com/) running locally
- The configured Ollama models (defaults shown below):

```powershell
ollama pull nomic-embed-text
ollama pull qwen2.5:7b
```

## Run

From the repository root:

```powershell
New-Item -ItemType Directory -Force .\library\Official, .\library\Unofficial
dotnet run --project .\Nelkhael\Nelkhael.csproj
```

Before indexing, add PDFs, TXT, HTML/HTM, JSON, or PHP files directly to `library\Official` or `library\Unofficial`. The folders are ignored by Git for these file types, so each user must populate them locally; Nelkhael does not include books. PDFs that were previously in `library\pdfs` belong in `library\Official`. The default `LibraryPath` points to the `library` folder and indexes supported files from both source folders.

Open <http://localhost:5080>, then select **Index library**. Indexing skips files that have not changed and reports how many indexed entries were removed for supported files deleted from either source folder. **Flush Index** clears all indexed passages after confirmation; it does not delete source files. PDF sources cite page numbers; text-file sources cite indexed sections. The SQLite database is created at `Nelkhael\App_Data\nelkhael.db`.

If indexing reports that Ollama refused the connection, start the Ollama desktop app (or run `ollama serve`) and confirm `http://localhost:11434` is reachable. Install the models before indexing:

```powershell
ollama pull nomic-embed-text
ollama pull qwen2.5:7b
```

The indexer checks Ollama and the embedding model before reading files and reports a single setup error if either is unavailable.

Run the test suite with `dotnet test .\Nelkhael.Tests\Nelkhael.Tests.csproj`.

## Configuration

Edit `Nelkhael\appsettings.json` or use standard .NET configuration overrides:

- `LibraryPath`: parent folder containing `Official` and `Unofficial`; relative paths resolve from the project directory.
- `DataPath`: SQLite database path; relative paths resolve from the project directory.
- `Ollama:Endpoint`, `Ollama:EmbeddingModel`, and `Ollama:ChatModel`: local Ollama settings.

The app binds to loopback port 5080. It does not expose a public or LAN endpoint. Source files and prompts are sent only to the configured Ollama endpoint; keep that endpoint local if the library must remain private. The index contains extracted text passages and embeddings in SQLite. Text, HTML, JSON, and PHP files are read as text; scanned PDFs require OCR before indexing.

Nelkhael is designed to explain rules in its own words and cite source files and locations; use the cited files for full context.

## Upcoming Enhancements

- Read and store from public html pages