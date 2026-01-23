using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.Text.Json;

namespace suggester;

/// <summary>
/// Test client for the MCP server using HTTP/SSE transport.
/// </summary>
public class McpTestClient
{
    private readonly string _serverUrl;
    private readonly string _testId;

    public McpTestClient(string test_id, string serverUrl = "http://localhost:5050/mcp")
    {
        _serverUrl = serverUrl;
        _testId = test_id;
    }

    public async Task RunTestsAsync()
    {
        Console.WriteLine($"Connecting to MCP server at {_serverUrl}...");

        try
        {
            string _bearerToken = "Some_data_for_authentication"; 
            var transportOptions = new HttpClientTransportOptions
            {
                Endpoint = new Uri(_serverUrl)
            };
            transportOptions.AdditionalHeaders = new Dictionary<string, string>
            {
                ["Authorization"] = $"Bearer {_bearerToken}"
            };

            var clientTransport = new HttpClientTransport(transportOptions);
            await using var client = await McpClient.CreateAsync(clientTransport);

            Console.WriteLine("Connected successfully!\n");

            // List available tools
            Console.WriteLine("=== Available Tools ===");
            var tools = await client.ListToolsAsync();
            foreach (var tool in tools)
            {
                Console.WriteLine($"  - ({tool.Name}) : {tool.Description}");
                if (tool.JsonSchema.ValueKind != JsonValueKind.Undefined &&
                    tool.JsonSchema.TryGetProperty("properties", out var props))
                {
                    var required = tool.JsonSchema.TryGetProperty("required", out var reqArray) 
                        ? reqArray.EnumerateArray().Select(e => e.GetString()).ToHashSet() 
                        : new HashSet<string?>();
                    
                    foreach (var param in props.EnumerateObject())
                    {
                        var paramType = param.Value.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : "any";
                        var paramDesc = param.Value.TryGetProperty("description", out var descEl) ? descEl.GetString() : "";
                        var isRequired = required.Contains(param.Name) ? " (required)" : "";
                        Console.WriteLine($"      • {param.Name}: {paramType}{isRequired} - {paramDesc}");
                    }
                }
            }
            Console.WriteLine();

            // Test login_to_emby_server
            Console.WriteLine("=== Testing login_to_emby_server ===");
            var loginResult = await client.CallToolAsync("login_to_emby_server", new Dictionary<string, object?>
            {
                ["userName"] = "admin",
                ["password"] = "password"
            });
            PrintResult(loginResult);

            // Test check_emby_server_login_status
            Console.WriteLine("=== Testing check_emby_server_login_status ===");
            var statusResult = await client.CallToolAsync("check_emby_server_login_status", new Dictionary<string, object?>());
            PrintResult(statusResult);

            // Test get_movie_count
            Console.WriteLine("=== Testing get_movie_count ===");
            var countResult = await client.CallToolAsync("get_movie_count", new Dictionary<string, object?>());
            PrintResult(countResult);

            // Test find_similar_movies
            Console.WriteLine("=== Testing find_similar_movies (movieId:" + _testId + ") (limit: 5) ===");
            var findResult = await client.CallToolAsync("find_similar_movies", new Dictionary<string, object?>
            {
                ["movieId"] = _testId,
                ["topN"] = 5
            });
            PrintResult(findResult);            

            // Test list_movies_matching
            Console.WriteLine("=== Testing list_movies_matching (nameFilter: star) (limit: 5) ===");
            var listResult = await client.CallToolAsync("list_movies_matching", new Dictionary<string, object?>
            {
                ["nameFilter"] = "star",
                ["limit"] = 5
            });
            PrintResult(listResult);

            // Test search_movies_by_description
            Console.WriteLine("=== Testing search_movies_by_description (description: space horror movie with aliens) (limit: 5) ===");
            var searchResult = await client.CallToolAsync("search_movies_by_description", new Dictionary<string, object?>
            {
                ["description"] = "space horror movie with aliens",
                ["topN"] = 5
            });
            PrintResult(searchResult);

            // Test get_movie_document
            Console.WriteLine("=== Testing get_movie_document (movieId: " + _testId + ") ===");
            var detailsResult = await client.CallToolAsync("get_movie_document", new Dictionary<string, object?>
            {
                ["movieId"] = _testId
            });
            PrintResult(detailsResult);

            // Test show_tool_session_data
            Console.WriteLine("=== Testing show_tool_session_data ===");
            var sessionDataResult = await client.CallToolAsync("show_tool_session_data", new Dictionary<string, object?>());
            PrintResult(sessionDataResult);
            
            Console.WriteLine("\n=== All tests completed successfully! ===");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
        }
    }

    private static void PrintResult(CallToolResult result)
    {
        foreach (var content in result.Content)
        {
            if (content is TextContentBlock textContent)
            {
                Console.WriteLine(textContent.Text);
            }
            else
            {
                Console.WriteLine($"[{content.Type}] {content}");
            }
        }
        Console.WriteLine();
    }
}
