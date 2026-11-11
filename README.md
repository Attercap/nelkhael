# Nelkhael

Nelkhael is a local-first .NET 10 web app for asking questions about the In Nomine PDFs in `library/pdfs`. It extracts text from PDFs, creates local embeddings with Ollama, stores the index in SQLite, and returns concise paraphrased answers with book and page citations.

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
New-Item -ItemType Directory -Force .\library\pdfs
dotnet run --project .\Nelkhael\Nelkhael.csproj
```

Before indexing, add your own searchable RPG rulebook PDFs to `library\pdfs`. That folder is ignored by Git, so each user must create and populate it locally; Nelkhael does not include books. The default `LibraryPath` points to this folder.

Open <http://localhost:5080>, then select **Index PDF library**. Indexing skips files that have not changed. The SQLite database is created at `Nelkhael\App_Data\nelkhael.db`.

If indexing reports that Ollama refused the connection, start the Ollama desktop app (or run `ollama serve`) and confirm `http://localhost:11434` is reachable. Install the models before indexing:

```powershell
ollama pull nomic-embed-text
ollama pull qwen2.5:7b
```

The indexer checks Ollama and the embedding model before reading PDFs and reports a single setup error if either is unavailable.

## Configuration

Edit `Nelkhael\appsettings.json` or use standard .NET configuration overrides:

- `LibraryPath`: PDF folder; relative paths resolve from the project directory.
- `DataPath`: SQLite database path; relative paths resolve from the project directory.
- `Ollama:Endpoint`, `Ollama:EmbeddingModel`, and `Ollama:ChatModel`: local Ollama settings.

The app binds to loopback port 5080. It does not expose a public or LAN endpoint. PDFs and prompts are sent only to the configured Ollama endpoint; keep that endpoint local if the library must remain private. The index contains extracted text passages and embeddings in SQLite. Scanned PDFs require OCR before indexing.

Nelkhael is designed to explain rules in its own words and cite source pages; use the cited books for the full text.

## Upcoming Enhancements

- Allow for reading text-based files (including php and html) for custom/3rd party rules
- Read public html pages for custom/3rd party rules