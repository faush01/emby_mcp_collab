using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Server;
using suggester.Models;

namespace suggester
{
    class Program
    {

        static async Task<int> AddEmbeddingsToStorage()
        {
            var config = SuggesterConfig.Settings;
            
            using var client = new EmbyMediaApiClient(config.EmbyApiBaseUrl, config.EmbyApiKey);
            var collectionsWithMovies = await client.GetBoxSetCollectionsWithMoviesAsync();
            var movieToCollections = EmbyMediaApiClient.BuildCollectionLookup(collectionsWithMovies);
            var response = await client.GetMoviesAsync(startIndex: 0, limit: 5000);
            List<Movie> movies = response?.Items.ToList() ?? [];

            var embeddingClient = new EmbeddingClient(config.OllamaEndpoint, config.EmbeddingModel);
            var documents = embeddingClient.GetMovieDocuments(
                movies, 
                collectionsWithMovies,
                movieToCollections);

            using var storage = new DocInfoStorage(config.DatabasePath);
            storage.InitializeStorage();

            int count = 0;
            int total = documents.Count;
            int current = 0;
            int max_name_length = 35;
            List<double> time_per_doc = [];
            foreach (var doc in documents)
            {
                current++;
                var saved_doc = storage.LoadDocItem(doc.DocId);
                if (saved_doc != null && saved_doc.DocHash == doc.DocHash)
                {
                    Console.WriteLine($"[{current}/{total}] Skipping DocId {doc.DocId} - hash unchanged.");
                    continue;
                }

                DateTime start_time = DateTime.UtcNow;
                EmbeddingResult embeddingResult = await embeddingClient.GetEmbeddingAsync(doc);
                DateTime end_time = DateTime.UtcNow;
                double elapsed_seconds = (end_time - start_time).TotalSeconds;

                // save the doc with embedding
                if (storage.SaveDocItem(doc))
                {
                    count++;
                }

                // Output some status info
                time_per_doc.Add(elapsed_seconds);
                if (time_per_doc.Count > 10)
                {
                    time_per_doc.RemoveAt(0);
                }
                double avg_time = time_per_doc.Average();
                List<string> log_lines = [];
                log_lines.Add($"[{current}/{total}]");
                log_lines.Add($"ID: {doc.DocId}");
                //log_lines.Add($"Hash: {doc.DocHash}");
                //log_lines.Add($"DocSize: {doc.DocText.Length}");
                double seconds_left = avg_time * (total - current);
                TimeSpan time_left = TimeSpan.FromSeconds(seconds_left);
                string time_left_str = $"{(int)time_left.TotalHours:D2}:{time_left.Minutes:D2}:{time_left.Seconds:D2}";
                log_lines.Add($"Time: {elapsed_seconds:F2}s Avg: {avg_time:F2}s Left: {time_left_str}");
                //log_lines.Add($"EmbeddingSize: {doc.Embedding?.Length}");
                log_lines.Add($"Tokens: {embeddingResult.InputTokens}");
                string truncated_name = doc.DocName.Length > max_name_length ? doc.DocName.Substring(0, max_name_length-3) + "..." : doc.DocName;
                log_lines.Add($"Name: {truncated_name}");
                //var first4 = doc.Embedding?.Take(4).Select(v => v.ToString("F6")).ToArray() ?? [];
                //log_lines.Add($"Embedding Values: [{string.Join(", ", first4)}...]");
                Console.WriteLine(string.Join(", ", log_lines));
            }

            return count;
        }

        static async Task<List<SimilarDocResult>> SearchForSimilar(string searchFor)
        {
            var config = SuggesterConfig.Settings;

            if (string.IsNullOrWhiteSpace(searchFor))
            {
                Console.WriteLine("Please provide a search query.");
                return [];
            }
            
            DocInfo seachItem;
            var embeddingClient = new EmbeddingClient(config.OllamaEndpoint, config.EmbeddingModel);

            if (int.TryParse(searchFor, out int movieId))
            {
                Console.WriteLine($"Searching for similar to id : {movieId}");
                // Fetch the movie by ID
                using var client = new EmbyMediaApiClient(config.EmbyApiBaseUrl, config.EmbyApiKey);
                var collectionsWithMovies = await client.GetBoxSetCollectionsWithMoviesAsync();
                var movieToCollections = EmbyMediaApiClient.BuildCollectionLookup(collectionsWithMovies);

                Movie? movie = await client.GetMovieAsync(movieId.ToString());
                if (movie == null)
                {
                    Console.WriteLine($"Movie with ID {movieId} not found.");
                    return [];
                }
                Console.WriteLine($"Searching for movies similar to: {movie.Name} ({movie.Id})");
                List<DocInfo> queryDocs = embeddingClient.GetMovieDocuments(
                    new List<Movie> { movie },
                    collectionsWithMovies,
                    movieToCollections);
                seachItem = queryDocs[0];
                Console.WriteLine($"Query DocId: {seachItem.DocId}, Name: {seachItem.DocName}");
                Console.WriteLine($"Query Text: {seachItem.DocText}");
            }
            else
            {
                Console.WriteLine($"Searching for similar with description : {searchFor}");
                seachItem = new DocInfo
                {
                    DocId = "query_temp",
                    DocName = "Search Query",
                    DocText = searchFor
                };
                EmbeddingResult queryEmbedding =
                    await embeddingClient.GetEmbeddingAsync(seachItem);
            }

            // do the search
            using var storage = new DocInfoStorage(config.DatabasePath);
            var sw = Stopwatch.StartNew();
            //var similarDocs = storage.FindSimilarDocuments(queryDoc, topN: 10);
            var similarDocs = storage.FindSimilarDocumentsStreaming(seachItem, topN: 15);
            sw.Stop();
            double elapsed_milli = sw.Elapsed.TotalMilliseconds;
            Console.WriteLine($"Similarity search completed in {elapsed_milli:F2} milliseconds. Top results:");

            foreach (var result in similarDocs)
            {
                Console.WriteLine($"[{result.Similarity:F4}] {result.Document.DocName} ({result.Document.DocId}) ");
            }

            return similarDocs;
        }

        static async Task RunMcpServer(int port = 5050)
        {
            Console.WriteLine($"Starting MCP Server on http://0.0.0.0:{port} (all interfaces)...");
            Console.WriteLine($"MCP endpoint: http://<your-ip>:{port}/mcp");
            
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
            
            builder.Services
                .AddMcpServer()
                .WithHttpTransport()
                .WithTools<SuggesterTools>();

            var app = builder.Build();
            
            // Map the MCP endpoint for Streamable HTTP at /mcp path
            app.MapMcp("/mcp");
            
            // Debug endpoint to verify tools are registered
            app.MapGet("/tools", () => 
            {
                var toolType = typeof(SuggesterTools);
                var methods = toolType.GetMethods()
                    .Where(m => m.GetCustomAttributes(typeof(McpServerToolAttribute), false).Any())
                    .Select(m => new 
                    {
                        Name = m.Name,
                        Parameters = m.GetParameters().Select(p => new { p.Name, Type = p.ParameterType.Name }).ToArray(),
                        Description = m.GetCustomAttributes(typeof(DescriptionAttribute), false)
                            .Cast<DescriptionAttribute>()
                            .FirstOrDefault()?.Description ?? "No description"
                    })
                    .ToList();
                return Results.Json(new { ToolCount = methods.Count, Tools = methods });
            });
            
            Console.WriteLine("Server started. Press Ctrl+C to stop.");
            Console.WriteLine($"Debug: View registered tools at http://<your-ip>:{port}/tools");
            await app.RunAsync();
        }

        static async Task Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "serve")
            {
                int port = 5050;
                if (args.Length > 1 && int.TryParse(args[1], out var customPort))
                {
                    port = customPort;
                }
                await RunMcpServer(port);
                return;
            }
            else if (args.Length > 0 && args[0] == "search")
            {
                string searchFor = "An alien on a space ship";
                if (args.Length > 1)
                {
                    searchFor = args[1];
                }                
                await SearchForSimilar(searchFor);
                return;
            }
            else if (args.Length > 0 && args[0] == "test")
            {
                string serverUrl = "http://localhost:5050/mcp";
                if (args.Length > 2)
                {
                    serverUrl = args[2];
                }
                var testClient = new McpTestClient(args[1], serverUrl);
                await testClient.RunTestsAsync();
                return;
            }
            else if (args.Length > 0 && args[0] == "add")
            {
                Console.WriteLine("Adding movie embeddings to storage...");
                int addedCount = await AddEmbeddingsToStorage();
                Console.WriteLine($"Added {addedCount} new/updated documents to storage.");
            }
            else
            {
                Console.WriteLine("Usage:");
                Console.WriteLine("  suggester serve [port]                - Start MCP server on specified port (default 5050)");
                Console.WriteLine("  suggester search <query>              - Search for similar movies by ID or description");
                Console.WriteLine("  suggester add                         - Add movie embeddings to storage");
                Console.WriteLine("  suggester test <testId> [serverUrl]   - Run MCP client tests against server URL (default http://localhost:5050/mcp)");
            }
        }
    }
}
