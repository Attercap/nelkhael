using Nelkhael.Services;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(options => options.ListenLocalhost(5080));
builder.Services.AddHttpClient<OllamaClient>(client => client.Timeout = TimeSpan.FromMinutes(5));
builder.Services.AddSingleton<PdfLibraryStore>();
builder.Services.AddSingleton<LibraryIndexer>();

var app = builder.Build();

var store = app.Services.GetRequiredService<PdfLibraryStore>();
await store.InitializeAsync();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/status", async (LibraryIndexer indexer, CancellationToken cancellationToken) =>
    Results.Ok(await indexer.GetStatusAsync(cancellationToken)));

app.MapPost("/api/index", async (LibraryIndexer indexer, CancellationToken cancellationToken) =>
{
    var result = await indexer.IndexAsync(cancellationToken);
    return result.IsRunning
        ? Results.Conflict(result)
        : Results.Ok(result);
});

app.MapDelete("/api/index", async Task<IResult> (
    LibraryIndexer indexer,
    CancellationToken cancellationToken) =>
{
    var flushed = await indexer.FlushIndexAsync(cancellationToken);
    return flushed
        ? TypedResults.NoContent()
        : TypedResults.Conflict();
})
    .WithName("FlushIndex")
    .WithSummary("Flush the PDF index")
    .WithDescription("Removes all indexed documents and passages from SQLite without deleting source PDFs. Returns 409 if indexing is in progress.")
    .Produces(StatusCodes.Status204NoContent)
    .Produces(StatusCodes.Status409Conflict);

app.MapPost("/api/ask", async (AskRequest request, LibraryIndexer indexer, CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Question) || request.Question.Length > 500)
    {
        return Results.BadRequest(new { error = "Enter a question of 1 to 500 characters." });
    }

    var answer = await indexer.AnswerAsync(request.Question.Trim(), cancellationToken);
    return answer is null
        ? Results.Conflict(new { error = "No indexed PDF content is available. Index the library first." })
        : Results.Ok(answer);
});

app.Run();

public sealed record AskRequest(string Question);
