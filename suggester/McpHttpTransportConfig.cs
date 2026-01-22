using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore;

namespace suggester;

/// <summary>
/// Configuration for MCP HTTP transport session handling.
/// </summary>
public static class McpHttpTransportConfig
{
    /// <summary>
    /// Configures the HTTP transport options with session lifecycle management.
    /// </summary>
    public static void Configure(HttpServerTransportOptions options)
    {
        options.RunSessionHandler = HandleSessionAsync;
    }

    /// <summary>
    /// Handles the MCP session lifecycle, including initialization and cleanup.
    /// </summary>
    private static async Task HandleSessionAsync(
        HttpContext httpContext,
        ModelContextProtocol.Server.McpServer mcpServer,
        CancellationToken token)
    {
        // Resolve logger from DI container using ILoggerFactory (required for static classes)
        var loggerFactory = httpContext.RequestServices.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger(typeof(McpHttpTransportConfig));

        // Resolve SessionContext singleton from DI container
        var sessionContext = httpContext.RequestServices.GetRequiredService<SessionContext>();

        // Log all HTTP request headers
        logger.LogDebug("=== HTTP Request Headers ===");
        foreach (var header in httpContext.Request.Headers)
        {
            logger.LogDebug("  {HeaderKey}: {HeaderValue}", header.Key, header.Value);
        }
        logger.LogDebug("============================");

        if (mcpServer.SessionId == null)
        {
            // There is no sessionId if the serverOptions.Stateless is true
            await mcpServer.RunAsync(token);
            return;
        }

        try
        {
            // Initialize session data at the start of the session
            logger.LogInformation("Session '{SessionId}' starting.", mcpServer.SessionId);
            var sessionData = sessionContext.GetSessionData(mcpServer.SessionId);
            await mcpServer.RunAsync(token);
        }
        finally
        {
            // This code runs when the session ends - clean up session data
            logger.LogInformation("Session '{SessionId}' ended. Cleaning up session data.", mcpServer.SessionId);
            sessionContext.RemoveSession(mcpServer.SessionId);
        }
    }
}
