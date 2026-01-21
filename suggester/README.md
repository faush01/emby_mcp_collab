# Movie Suggester MCP Server

A movie recommendation system that uses embeddings to find similar movies from your Emby library. It exposes its functionality via the Model Context Protocol (MCP), making it callable by AI systems like Open WebUI, Claude Desktop, and other MCP-compatible clients.

## Features

- **Find Similar Movies**: Find movies similar to a given movie by ID
- **Search by Description**: Search for movies using natural language descriptions (e.g., "space horror movie with aliens")
- **Get Movie Document**: Retrieve the full document text generated for a movie (used for embeddings)
- **List Movies**: Search and browse movies in the database by name
- **Movie Count**: Get statistics about the indexed library

## Prerequisites

- .NET 8.0 SDK
- Emby Media Server (with API access)
- Ollama running with an embedding model (default: `qwen3-embedding:0.6b`)
- SQLite database with pre-indexed movie embeddings

## Configuration

Configuration is managed via `appsettings.json`:

```json
{
  "Suggester": {
    "EmbyApiBaseUrl": "http://192.168.0.17:8096/emby",
    "EmbyApiKey": "your-api-key",
    "OllamaEndpoint": "http://192.168.0.17:11434/v1",
    "EmbeddingModel": "qwen3-embedding:0.6b",
    "DatabasePath": "docs.db"
  }
}
```

You can also override settings using environment variables or create environment-specific files like `appsettings.Development.json`.

## Usage

### 1. Index Your Movie Library (First Time Setup)

Before using the MCP server, you need to index your movie library:

```bash
dotnet run -- add
```

This will:
- Fetch all movies from your Emby server
- Generate embeddings for each movie using Ollama
- Store the embeddings in a SQLite database (`docs.db`)
- Skip movies whose content hash hasn't changed (incremental updates)

### 2. Run MCP HTTP Server

For use with Open WebUI or other HTTP-based MCP clients:

```bash
dotnet run -- serve [port]
```

Default port is 5050. The MCP endpoint will be available at:
- `http://localhost:5050/mcp`
- Debug tools list: `http://localhost:5050/tools`

Example with custom port:
```bash
dotnet run -- serve 8080
```

### 3. Search Mode (CLI Testing)

For direct testing of similarity search from the command line:

```bash
dotnet run -- search "space horror movie with aliens"
dotnet run -- search 12345  # Search by movie ID
```

### 4. Test MCP Client

Run automated tests against a running MCP server:

```bash
dotnet run -- test <testId> [serverUrl]
```

Default server URL is `http://localhost:5050/mcp`.

## MCP Tools Available

### `FindSimilarMovies`
Find movies similar to a given movie by its ID.
- **Parameters**: 
  - `movieId` (string): The Emby movie ID
  - `topN` (int, optional): Number of results (default: 10)

### `SearchMoviesByDescription`
Search for movies using natural language.
- **Parameters**:
  - `description` (string): Description of what you're looking for (e.g., "space horror movie with aliens")
  - `topN` (int, optional): Number of results (default: 10)

### `GetMovieDocument`
Get the detailed document text generated for a movie (the text used for creating embeddings).
- **Parameters**:
  - `movieId` (string): The Emby movie ID

### `GetMovieCount`
Returns the total number of indexed movies in the database.

### `ListMoviesMatching`
Search for movies in the database with names matching a filter.
- **Parameters**:
  - `nameFilter` (string, optional): Filter movies by name (default: empty, returns all)
  - `limit` (int, optional): Number of movies to list (default: 20, max: 100)

## Integration with Open WebUI

1. Start the HTTP server:
   ```bash
   dotnet run -- serve 5050
   ```

2. In Open WebUI, add an MCP tool connection with:
   - URL: `http://your-server-ip:5050/mcp`
   - Type: Streamable HTTP

3. Enable the Tool in the model options

4. Use the following system prompt with the model
    ```
    You are a movie assistant.

    You have access to the following tools and MUST use them when appropriate:

    === Available Tools ===
      - (search_movies_by_description) : Search for movies based on a text description. Describe what kind of movie you're looking for.
          • description: string (required) - A text description of the movie you're looking for (e.g., 'space horror movie with aliens')
          • topN: integer - Number of results to return (default: 10)
      - (get_movie_count) : Get the total count of movies in the database.
      - (find_similar_movies) : Search for movies similar to a given movie by its ID. Returns a list of similar movies based on embedding similarity.
          • movieId: string (required) - The movie ID to find similar movies for
          • topN: integer - Number of similar movies to return (default: 10)
      - (list_movies_matching) : Find movies in the database with a name matching the filter.
          • nameFilter: string - Filter movies by name (default: empty, no filter)
          • limit: integer - Number of movies to list (default: 20, max: 100)
      - (get_movie_document) : Get the detailed information about a specific movie by ID.
          • movieId: string (required) - The Emby movie ID

    Rules:
    - Do NOT make up movie data.
    - If a question can be answered using a tool, call the tool instead of answering from memory.
    - Ask a clarifying question if required inputs are missing.
    ```

## Integration with Claude Desktop

Add to your `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "movie-suggester": {
      "command": "dotnet",
      "args": ["run", "--project", "path/to/suggester", "--", "serve"]
    }
  }
}
```

Or using the built executable:

```json
{
  "mcpServers": {
    "movie-suggester": {
      "command": "path/to/suggester.exe",
      "args": ["serve"]
    }
  }
}
```

## Architecture

```
┌─────────────────┐     ┌──────────────────┐     ┌─────────────────┐
│   MCP Client    │---->│   MCP Server     │---->│  Emby Server    │
│  (Open WebUI,   │     │  (this project)  │     │                 │
│  Claude, etc.)  │     │                  │     └─────────────────┘
└─────────────────┘     │  ┌────────────┐  │
                        │  │  SQLite DB │  │     ┌─────────────────┐
                        │  │(embeddings)│  │---->│     Ollama      │
                        │  └────────────┘  │     │  (embeddings)   │
                        └──────────────────┘     └─────────────────┘
```

## Building

```bash
dotnet build
```

## Publishing

To create a self-contained executable:

```bash
dotnet publish -c Release -r win-x64 --self-contained
```

**Note:** Make sure to copy `appsettings.json` alongside the published executable.
