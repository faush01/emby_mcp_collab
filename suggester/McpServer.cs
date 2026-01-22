using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.Collections.Concurrent;
using System.ComponentModel;
using suggester.Models;

namespace suggester;

/// <summary>
/// MCP Server tools for movie suggestion functionality.
/// These tools can be called by MCP clients like Open WebUI.
/// </summary>
[McpServerToolType]

public class SessionData
{
    public ConcurrentDictionary<string, object> Data { get; } = new ConcurrentDictionary<string, object>();
    public DateTime LastAccessed { get; set; } = DateTime.UtcNow;
}

public class SessionContext
{
    private ConcurrentDictionary<string, SessionData> Data { get; } = new ConcurrentDictionary<string, SessionData>();
    private ILogger<SessionContext> _logger;

    public SessionContext(ILogger<SessionContext> logger) 
    { 
        _logger = logger;
    }

    public SessionData GetSessionData(string sessionId)
    {
        _logger?.LogInformation("Retrieving session data for session '{SessionId}'", sessionId);

        var sessionData = Data.GetOrAdd(sessionId, _ =>
        {
            _logger?.LogInformation("Adding SessionData() for session '{SessionId}'", sessionId);
            return new SessionData();
        });

        sessionData.LastAccessed = DateTime.UtcNow;
        return sessionData;
    }

    /// <summary>
    /// Remove session data when a session ends. Called from the RunSessionHandler.
    /// </summary>
    public bool RemoveSession(string sessionId)
    {
        _logger?.LogInformation("Removing SessionData() for session '{SessionId}'", sessionId);
        return Data.TryRemove(sessionId, out _);
    }

    public void PrintAllSessions()
    {
        _logger?.LogInformation("Current sessions:");
        foreach (var kvp in Data)
        {
            double age = (DateTime.UtcNow - kvp.Value.LastAccessed).TotalSeconds;
            _logger?.LogInformation(" - Session '{SessionId}' last accessed {Age:F2} seconds ago.", kvp.Key, age);
        }
    }   
}


public class SuggesterTools : IDisposable
{
    private readonly EmbeddingClient _embeddingClient;
    private readonly DocInfoStorage _storageClient;
    private readonly EmbyMediaApiClient _embyClient;
    private readonly ILogger<SuggesterTools> _logger;
    private readonly string _sessionId;
    private readonly SessionContext _sessionContext;

    public SuggesterTools(McpServer server, 
        ILogger<SuggesterTools> logger,
        SessionContext sessionContext)
    {
        var config = SuggesterConfig.Settings;
        
        // Create our own instances of helper classes - each MCP session gets its own
        _embeddingClient = new EmbeddingClient(config.OllamaEndpoint, config.EmbeddingModel);
        _storageClient = new DocInfoStorage(config.DatabasePath);
        _embyClient = new EmbyMediaApiClient(config.EmbyApiBaseUrl, config.EmbyApiKey);
        
        _logger = logger;
        _sessionId = server.SessionId ?? "unknown";
        _logger.LogInformation("{sessionId} - SuggesterTools initialized", _sessionId);
        _sessionContext = sessionContext;
    }

    public void Dispose()
    {
        _logger.LogInformation("{sessionId} - SuggesterTools disposed", _sessionId);
        _storageClient?.Dispose();
        _embyClient?.Dispose();

        _sessionContext.PrintAllSessions();
        SessionData sessionData = _sessionContext.GetSessionData(_sessionId);
        _logger.LogInformation("{sessionId} - Printing all session data", _sessionId);
        foreach(var key in sessionData.Data.Keys.ToList())
        {
            _logger.LogInformation("{sessionId} - key: {Key} = {Value}", _sessionId, key, sessionData.Data[key]);
        }
    }

    private void LogToolCall(string toolName, string parameters)
    {
        _logger.LogInformation("{sessionId} - [MCP TOOL CALL] {ToolName} called with: {Parameters}", _sessionId, toolName, parameters);
    }

    private void LogToolResponse(string toolName, string response)
    {
        var truncatedResponse = response.Length > 500 ? response[..500] + "..." : response;
        _logger.LogInformation("{sessionId} - [MCP TOOL RESPONSE] {ToolName} returned: \n{Response} ", _sessionId, toolName, truncatedResponse);
    }

    [McpServerTool, Description("Search for movies similar to a given movie by its ID. Returns a list of similar movies based on embedding similarity.")]
    public async Task<string> FindSimilarMovies(
        [Description("The movie ID to find similar movies for")] string movieId,
        [Description("Number of similar movies to return (default: 10)")] int topN = 10)
    {
        _sessionContext.PrintAllSessions();
        SessionData sessionData = _sessionContext.GetSessionData(_sessionId);
        sessionData.Data["last_movie_id"] = movieId;

        try
        {
            // Try to find the movie - first check if it's an ID
            Movie? movie = null;
            
            // Try as ID first
            if(int.TryParse(movieId, out int parsedMovieId))
            {
                movie = await _embyClient.GetMovieAsync(parsedMovieId.ToString());
            }

            if (movie == null)
            {
                var notFoundResult = $"Could not find a movie matching '{movieId}'. Please try a different movie ID.";
                LogToolResponse(nameof(FindSimilarMovies), notFoundResult);
                return notFoundResult;
            }

            // Load collections on demand and create document with embedding
            var collectionsWithMovies = await _embyClient.GetBoxSetCollectionsWithMoviesAsync();
            var movieToCollections = EmbyMediaApiClient.BuildCollectionLookup(collectionsWithMovies);
            
            var queryDocs = _embeddingClient.GetMovieDocuments(
                new List<Movie> { movie },
                collectionsWithMovies,
                movieToCollections);

            var queryDoc = queryDocs[0];
            await _embeddingClient.GetEmbeddingAsync(queryDoc);

            // Find similar documents
            var similarDocs = _storageClient.FindSimilarDocumentsStreaming(queryDoc, topN: topN);

            // Format results
            var results = new List<string>();
            foreach (var result in similarDocs)
            {
                var doc = result.Document;
                var similarity = result.Similarity;
                results.Add($" - {results.Count + 1} : {doc.DocName} (Id: {doc.DocId}) (Similarity: {similarity:F3})");
            }

            var successResult = $"Movies similar to '{movie.Name}':\n\n{string.Join("\n", results)}";
            LogToolResponse(nameof(FindSimilarMovies), successResult);
            return successResult;
        }
        catch (Exception ex)
        {
            var errorResult = $"Error finding similar movies: {ex.Message}";
            LogToolResponse(nameof(FindSimilarMovies), errorResult);
            return errorResult;
        }
    }

    [McpServerTool, Description("Search for movies based on a text description. Describe what kind of movie you're looking for.")]
    public async Task<string> SearchMoviesByDescription(
        [Description("A text description of the movie you're looking for (e.g., 'space horror movie with aliens')")] string description,
        [Description("Number of results to return (default: 10)")] int topN = 10)
    {
        LogToolCall(nameof(SearchMoviesByDescription), $"description='{description}', topN={topN}");
        try
        {
            // Create a query document from the description
            var queryDoc = new DocInfo
            {
                DocId = "query",
                DocName = "Search Query",
                DocText = description,
                DocHash = ""
            };

            // Get embedding for the query
            await _embeddingClient.GetEmbeddingAsync(queryDoc);

            // Find similar documents
            var similarDocs = _storageClient.FindSimilarDocumentsStreaming(queryDoc, topN: topN);

            // Format results
            var results = new List<string>();
            foreach(var result in similarDocs)
            {
                var doc = result.Document;
                var similarity = result.Similarity;
                results.Add($" - {results.Count + 1} : {doc.DocName} (Id: {doc.DocId}) (Similarity: {similarity:F3})");
            }
            var successResult = $"Movies matching '{description}':\n\n{string.Join("\n", results)}";
            LogToolResponse(nameof(SearchMoviesByDescription), successResult);
            return successResult;
        }
        catch (Exception ex)
        {
            var errorResult = $"Error searching movies: {ex.Message}";
            LogToolResponse(nameof(SearchMoviesByDescription), errorResult);
            return errorResult;
        }
    }

    [McpServerTool, Description("Get the detailed information about a specific movie by ID.")]
    public async Task<string> GetMovieDocument(
        [Description("The Emby movie ID")] string movieId)
    {
        LogToolCall(nameof(GetMovieDocument), $"movieId='{movieId}'");
        try
        {
            Movie? movie = null;

            // Try as ID first
            if (int.TryParse(movieId, out int parsedMovieId))
            {
                movie = await _embyClient.GetMovieAsync(parsedMovieId.ToString());
            }

            if (movie == null)
            {
                var notFoundResult = $"Could not find a movie with id '{movieId}'.";
                LogToolResponse(nameof(GetMovieDocument), notFoundResult);
                return notFoundResult;
            }

            // Load collections on demand and create document with embedding
            var collectionsWithMovies = await _embyClient.GetBoxSetCollectionsWithMoviesAsync();
            var movieToCollections = EmbyMediaApiClient.BuildCollectionLookup(collectionsWithMovies);
            
            var queryDocs = _embeddingClient.GetMovieDocuments(
                new List<Movie> { movie },
                collectionsWithMovies,
                movieToCollections);

            var queryDoc = queryDocs[0];
            var movie_document = queryDoc.DocText;

            LogToolResponse(nameof(GetMovieDocument), movie_document);
            return movie_document;
        }
        catch (Exception ex)
        {
            var errorResult = $"Error getting movie details: {ex.Message}";
            LogToolResponse(nameof(GetMovieDocument), errorResult);
            return errorResult;
        }
    }

    [McpServerTool, Description("Get the total count of movies in the database.")]
    public string GetMovieCount()
    {
        _sessionContext.PrintAllSessions();
        SessionData sessionData = _sessionContext.GetSessionData(_sessionId);
        LogToolCall(nameof(GetMovieCount), "(no parameters)");
        try
        {
            var allDocs = _storageClient.LoadAllDocItems();
            sessionData.Data["movie_count"] = allDocs.Count;
            sessionData.Data["last_updated"] = DateTime.UtcNow.ToString();

            var successResult = $"There are {allDocs.Count} movies indexed in the database.";
            LogToolResponse(nameof(GetMovieCount), successResult);
            return successResult;
        }
        catch (Exception ex)
        {
            var errorResult = $"Error getting movie count: {ex.Message}";
            LogToolResponse(nameof(GetMovieCount), errorResult);
            return errorResult;
        }
    }

    [McpServerTool, Description("Find movies in the database with a name matching the filter.")]
    public string ListMoviesMatching(
        [Description("Filter movies by name (default: empty, no filter)")] string nameFilter = "",
        [Description("Number of movies to list (default: 20, max: 100)")] int limit = 20)
    {
        LogToolCall(nameof(ListMoviesMatching), $"nameFilter={nameFilter}, limit={limit}");
        try
        {
            limit = Math.Min(limit, 100);
            var allDocs = _storageClient.SearchByName(nameFilter);

            var results = new List<string>();
            foreach (var doc in allDocs)
            {
                results.Add($" - {results.Count + 1} : {doc.DocName} (Id: {doc.DocId})");
                if (results.Count >= limit)
                {
                    break;
                }
            }
            var result = $"Showing {results.Count} of {allDocs.Count} movies:\n\n{string.Join("\n", results)}";
            if (allDocs.Count > results.Count)
            {
                result += $"\n\n... and {allDocs.Count - results.Count} more.";
            }
            LogToolResponse(nameof(ListMoviesMatching), result);
            return result;
        }
        catch (Exception ex)
        {
            var errorResult = $"Error listing movies: {ex.Message}";
            LogToolResponse(nameof(ListMoviesMatching), errorResult);
            return errorResult;
        }
    }
}
