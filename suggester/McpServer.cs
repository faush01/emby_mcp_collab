using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.Collections.Concurrent;
using System.ComponentModel;
using suggester.Models;
using System.Security.Cryptography;

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

public interface ISessionContext
{
    SessionData GetSessionData(string sessionId);
    bool RemoveSession(string sessionId);
    void PrintAllSessions();
    int CleanupStaleSessions(TimeSpan maxAge);
}

public class SessionContext : ISessionContext
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

    public int CleanupStaleSessions(TimeSpan maxAge)
    {
        var cutoff = DateTime.UtcNow - maxAge;
        var staleSessions = Data.Where(x => x.Value.LastAccessed < cutoff).ToList();
        int removedCount = 0;

        foreach (var kvp in staleSessions)
        {
            if (Data.TryRemove(kvp.Key, out _))
            {
                TimeSpan age = DateTime.UtcNow - kvp.Value.LastAccessed;
                _logger?.LogInformation("Removed stale session '{SessionId}' (last accessed: {LastAccessed}, age: {Age})", 
                    kvp.Key, kvp.Value.LastAccessed, age);
                removedCount++;
            }
        }

        if (removedCount > 0)
        {
            _logger?.LogInformation("Session cleanup completed. Removed {Count} stale sessions.", removedCount);
        }

        return removedCount;
    }
}

public class SessionCleanupService : BackgroundService
{
    private readonly ISessionContext _sessionContext;
    private readonly ILogger<SessionCleanupService> _logger;
    private readonly TimeSpan _checkInterval = TimeSpan.FromMinutes(3);
    private readonly TimeSpan _maxSessionAge = TimeSpan.FromMinutes(60);

    public SessionCleanupService(ISessionContext sessionContext, ILogger<SessionCleanupService> logger)
    {
        _sessionContext = sessionContext;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Session cleanup service started. Checking every {Interval} for sessions older than {MaxAge}.",
            _checkInterval, _maxSessionAge);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_checkInterval, stoppingToken);
                //_logger.LogInformation("Running session cleanup...");
                _sessionContext.CleanupStaleSessions(_maxSessionAge);
            }
            catch (OperationCanceledException)
            {
                // Expected when stopping
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during session cleanup");
            }
        }

        _logger.LogInformation("Session cleanup service stopped.");
    }
}


public class SuggesterTools : IDisposable
{
    private readonly EmbeddingClient _embeddingClient;
    private readonly DocInfoStorage _storageClient;
    private readonly EmbyMediaApiClient _embyClient;
    private readonly ILogger<SuggesterTools> _logger;
    private readonly string _sessionId;
    private readonly ISessionContext _sessionContext;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public SuggesterTools(
        McpServer server, 
        ILogger<SuggesterTools> logger,
        ISessionContext sessionContext,
        IHttpContextAccessor httpContextAccessor)
    {
        var config = SuggesterConfig.Settings;
        
        // Create our own instances of helper classes - each MCP session gets its own
        _embeddingClient = new EmbeddingClient(config.OllamaEndpoint, config.EmbeddingModel);
        _storageClient = new DocInfoStorage(config.DatabasePath);
        _embyClient = new EmbyMediaApiClient(config.EmbyApiBaseUrl, config.EmbyApiKey);
        
        _logger = logger;
        _sessionContext = sessionContext;
        _httpContextAccessor = httpContextAccessor;

        // set up session context ID
        LogHeaders();
        string headersSessionId = GetSessionIdFromHeaders();
        if (!string.IsNullOrEmpty(headersSessionId))
        {
            _sessionId = headersSessionId;
            _logger.LogInformation("{sessionId} - Using SessionId from headers", _sessionId);
        }
        else if(!string.IsNullOrEmpty(server.SessionId))
        {
            _sessionId = server.SessionId;
            _logger.LogInformation("{sessionId} - Using MCP SessionId", _sessionId);
        }
        else
        {
            _sessionId = "Global";
            _logger.LogInformation("{sessionId} - Generated new SessionId", _sessionId);
        }
        _logger.LogInformation("{sessionId} - SuggesterTools initialized", _sessionId);
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

    ///////////////////////////////////////////////////////////////////////////////////
    // Helpers for logging tool calls and responses
    //

    private void LogToolCall(string toolName, string parameters)
    {
        _logger.LogInformation("{sessionId} - [MCP TOOL CALL] {ToolName} called with: {Parameters}", _sessionId, toolName, parameters);
    }

    private void LogToolResponse(string toolName, string response)
    {
        var truncatedResponse = response.Length > 500 ? response[..500] + "..." : response;
        _logger.LogInformation("{sessionId} - [MCP TOOL RESPONSE] {ToolName} returned: \n{Response} ", _sessionId, toolName, truncatedResponse);
    }

    private void LogHeaders()
    {
        if(_httpContextAccessor != null && _httpContextAccessor.HttpContext != null)
        {
            var headers = _httpContextAccessor.HttpContext.Request.Headers;
            // You can now use headers as needed
            _logger.LogInformation("HTTP Request Headers:");
            foreach (var header in headers)
            {
                _logger.LogInformation(" -   {HeaderKey}: {HeaderValue}", header.Key, header.Value);
            }
        }
    }

    private string GetSessionIdFromHeaders()
    {
        var config = SuggesterConfig.Settings;
        if (!string.IsNullOrEmpty(config.SessionIdHeader) && _httpContextAccessor != null && _httpContextAccessor.HttpContext != null)
        {
            _logger.LogInformation("Looking for SessionId Header : {HeaderName}", config.SessionIdHeader);
            var headers = _httpContextAccessor.HttpContext.Request.Headers;
            foreach(var name in headers.Keys)
            {
                if (name.Equals(config.SessionIdHeader, StringComparison.OrdinalIgnoreCase))
                {
                    string sessionId = headers[name].FirstOrDefault() ?? "";
                    if (!string.IsNullOrEmpty(sessionId))
                    {
                        _logger.LogInformation("Found SessionId Header: {HeaderName} = {HeaderValue}", name, sessionId);
                        sessionId = MD5.HashData(System.Text.Encoding.UTF8.GetBytes(sessionId))
                            .Aggregate("", (s, b) => s + b.ToString("x2"));
                        _logger.LogInformation("Hashed SessionId: {SessionId}", sessionId);
                        return sessionId;
                    }
                }
            }
        }
        return "";
    }

    ///////////////////////////////////////////////////////////////////////////////////
    // MCP Server Tools
    //

    [McpServerTool, Description("This tool returnes the stored session values.")]
    public Task<string> ShowToolSessionData()
    {
        _logger.LogInformation("{sessionId} - Tools session data shown", _sessionId);
        List<string> sessionDataLines = new List<string>();
        foreach(var kvp in _sessionContext.GetSessionData(_sessionId).Data)
        {
            sessionDataLines.Add($" - {kvp.Key} = {kvp.Value}");
            _logger.LogInformation("{sessionId} -   {Key} = {Value}", _sessionId, kvp.Key, kvp.Value);
        }
        return Task.FromResult("Your current session data:\n" + string.Join("\n", sessionDataLines));
    }

    [McpServerTool, Description("This tool resets the tools session, clearing all session data.")]
    public Task<string> ResetToolsSession()
    {
        _sessionContext.RemoveSession(_sessionId);
        _logger.LogInformation("{sessionId} - Tools session reset", _sessionId);
        return Task.FromResult("Your session has been reset. All session data has been cleared.");
    }

    [McpServerTool, Description("This tool logs a user into the Emby server using provided credentials.")]
    public async Task<string> LoginToEmbyServer(
        [Description("Emby user name")] string userName,
        [Description("Emby user password")] string password = "")
    {
        AuthenticationResponse authResponse = await _embyClient.AuthenticateAsync(userName, password);

        if (authResponse == null || string.IsNullOrEmpty(authResponse.AccessToken) || authResponse.User == null)
        {
            return "Login failed: Invalid username or password.\n\n";
        }

        string access_token = authResponse.AccessToken;
        string user_id = authResponse.User.Id;

        SessionData sessionData = _sessionContext.GetSessionData(_sessionId);
        sessionData.Data["access_token"] = access_token;
        sessionData.Data["user_id"] = user_id;
        sessionData.Data["user_name"] = userName;

        return "You are now logged into the Emby server with the following details:\n" +
               $"- User Name: {userName}\n" +
               $"- Access Token: {access_token}\n" +
               $"- User ID: {user_id}\n\n";
    }

    [McpServerTool, Description("This tool checks the login status of a user on the Emby server.")]
    public Task<string> CheckEmbyServerLoginStatus()
    {
        SessionData sessionData = _sessionContext.GetSessionData(_sessionId);
        string userName = sessionData.Data.ContainsKey("user_name") ? sessionData.Data["user_name"].ToString() ?? "" : "";
        string access_token = sessionData.Data.ContainsKey("access_token") ? sessionData.Data["access_token"].ToString() ?? "" : "";
        string user_id = sessionData.Data.ContainsKey("user_id") ? sessionData.Data["user_id"].ToString() ?? "" : "";

        if (string.IsNullOrEmpty(access_token) || string.IsNullOrEmpty(user_id))
        {
            return Task.FromResult("You are not logged into the Emby server. Please use the LoginToEmbyServer tool to log in.\n\n");
        }

        return Task.FromResult("You are logged into the Emby server with the following details:\n" +
               $"- User Name: {userName}\n" +
               $"- Access Token: {access_token}\n" +
               $"- User ID: {user_id}\n\n");
    }    

    [McpServerTool, Description("Get the list of recently watched movies by the logged-in user.")]
    public async Task<string> RecentlyWatched(
        [Description("Number of recently watched movies to return (default: 10)")] int topN = 10)
    {

        _sessionContext.PrintAllSessions();
        SessionData sessionData = _sessionContext.GetSessionData(_sessionId);

        string userId = sessionData.Data.ContainsKey("user_id") ? sessionData.Data["user_id"].ToString() ?? "" : "";
        string accessToken = sessionData.Data.ContainsKey("access_token") ? sessionData.Data["access_token"].ToString() ?? "" : "";
        if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(accessToken))
        {
            var notLoggedInResult = "You are not logged into the Emby server. Please use the LoginToEmbyServer tool to log in.";
            LogToolResponse(nameof(RecentlyWatched), notLoggedInResult);
            return notLoggedInResult;
        }

        var recentlyWatchedMovies = await _embyClient.RecentlyWatchedAsync(userId, accessToken, topN);

        // Format the list of recently watched movies
        if (recentlyWatchedMovies == null || recentlyWatchedMovies.Count == 0)
        {
            var noMoviesResult = "No recently watched movies found.";
            LogToolResponse(nameof(RecentlyWatched), noMoviesResult);
            return noMoviesResult;
        }

        var resultBuilder = new System.Text.StringBuilder();
        resultBuilder.AppendLine($"Recently watched movies (top {topN}):");
        resultBuilder.AppendLine();
        
        foreach (var movie in recentlyWatchedMovies)
        {
            resultBuilder.AppendLine($"## {movie.Name} ({movie.ProductionYear}) [ID: {movie.Id}]");
            
            // Genres
            if (movie.Genres?.Count > 0)
            {
                resultBuilder.AppendLine($"**Genres:** {string.Join(", ", movie.Genres)}");
            }
            
            // Overview/Plot
            if (!string.IsNullOrEmpty(movie.Overview))
            {
                var overview = movie.Overview.Length > 300 
                    ? movie.Overview[..300] + "..." 
                    : movie.Overview;
                resultBuilder.AppendLine($"**Overview:** {overview}");
            }
            
            // Taglines
            if (movie.Taglines?.Count > 0)
            {
                resultBuilder.AppendLine($"**Tagline:** {movie.Taglines[0]}");
            }
            
            // Directors
            var directors = movie.People?.Where(p => p.Type == "Director").Select(p => p.Name).ToList();
            if (directors?.Count > 0)
            {
                resultBuilder.AppendLine($"**Director(s):** {string.Join(", ", directors)}");
            }
            
            // Main Cast (top 5 actors)
            var actors = movie.People?.Where(p => p.Type == "Actor").Take(5).Select(p => p.Name).ToList();
            if (actors?.Count > 0)
            {
                resultBuilder.AppendLine($"**Cast:** {string.Join(", ", actors)}");
            }
            
            // Studios
            if (movie.Studios?.Count > 0)
            {
                var studioNames = movie.Studios.Select(s => s.Name).ToList();
                resultBuilder.AppendLine($"**Studios:** {string.Join(", ", studioNames)}");
            }
            
            // Ratings
            var ratings = new List<string>();
            if (!string.IsNullOrEmpty(movie.OfficialRating))
            {
                ratings.Add($"Rated {movie.OfficialRating}");
            }
            if (movie.CommunityRating.HasValue)
            {
                ratings.Add($"Community: {movie.CommunityRating:F1}/10");
            }
            if (movie.CriticRating.HasValue)
            {
                ratings.Add($"Critic: {movie.CriticRating}%");
            }
            if (ratings.Count > 0)
            {
                resultBuilder.AppendLine($"**Ratings:** {string.Join(" | ", ratings)}");
            }
            
            // Runtime
            if (movie.RunTimeTicks.HasValue)
            {
                var runtime = TimeSpan.FromTicks(movie.RunTimeTicks.Value);
                resultBuilder.AppendLine($"**Runtime:** {(int)runtime.TotalMinutes} minutes");
            }
            
            // Tags
            if (movie.TagItems?.Count > 0)
            {
                var tagNames = movie.TagItems.Select(t => t.Name).ToList();
                resultBuilder.AppendLine($"**Tags:** {string.Join(", ", tagNames)}");
            }
            
            resultBuilder.AppendLine();
        }

        var result = resultBuilder.ToString();
        LogToolResponse(nameof(RecentlyWatched), result);
        return result;
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
            if (similarDocs.Count > 0)
            {
                var name_of_first = similarDocs[0].Document.DocName;
                sessionData.Data["search_id_" + movieId] = name_of_first;
            }
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
            SessionData sessionData = _sessionContext.GetSessionData(_sessionId);
            sessionData.Data["last_updated"] = DateTime.UtcNow.ToString("HH:mm:ss.fff");
            sessionData.Data["last_updated_02"] = DateTime.UtcNow.ToString("HH:mm:ss.fff");

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
