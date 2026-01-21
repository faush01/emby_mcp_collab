using System.Net.Http.Json;
using System.Text.Json;
using suggester.Models;

namespace suggester
{
    public class EmbyMediaApiClient : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly string _apiKey;
        private readonly JsonSerializerOptions _jsonOptions;

        private static readonly string[] FieldNames =
        [
            "DateCreated",
            "Genres",
            "Studios",
            "People",
            "Overview",
            "Taglines",
            "ProductionLocations",
            "CriticRating",
            "OfficialRating",
            "CommunityRating",
            "PremiereDate",
            "ProductionYear",
            "Tags"
        ];

        public EmbyMediaApiClient(string baseUrl, string apiKey)
        {
            _apiKey = apiKey;
            _httpClient = new HttpClient
            {
                BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/")
            };
            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };
        }

        public async Task<Movie?> GetMovieAsync(string movieId, CancellationToken cancellationToken = default)
        {
            var queryParams = new Dictionary<string, string>
            {
                ["IncludeItemTypes"] = "Movie",
                ["fields"] = string.Join(",", FieldNames),
                ["EnableImageTypes"] = "none",
                ["Ids"] = movieId,
                ["api_key"] = _apiKey
            };

            var queryString = string.Join("&", queryParams.Select(kvp => $"{kvp.Key}={Uri.EscapeDataString(kvp.Value)}"));
            var requestUri = $"Items?{queryString}";

            try
            {
                var response = await _httpClient.GetAsync(requestUri, cancellationToken);
                response.EnsureSuccessStatusCode();
                MediaResponse? mediaResponse =
                    await response.Content.ReadFromJsonAsync<MediaResponse>(_jsonOptions, cancellationToken);

                if (mediaResponse == null || mediaResponse.Items == null || mediaResponse.Items.Count == 0)
                {
                    return null;
                }

                return mediaResponse.Items[0];
            }
            catch (HttpRequestException ex)
            {
                Console.WriteLine($"HTTP request failed: {ex.Message}");
                throw;
            }
            catch (JsonException ex)
            {
                Console.WriteLine($"JSON parsing failed: {ex.Message}");
                throw;
            }
        }

        public async Task<MediaResponse?> GetMoviesAsync(int startIndex = 0, int limit = 5000, CancellationToken cancellationToken = default)
        {
            var queryParams = new Dictionary<string, string>
            {
                ["IncludeItemTypes"] = "Movie",
                ["fields"] = string.Join(",", FieldNames),
                ["StartIndex"] = startIndex.ToString(),
                ["EnableImageTypes"] = "none",
                ["Recursive"] = "true",
                ["Limit"] = limit.ToString(),
                ["api_key"] = _apiKey
            };

            var queryString = string.Join("&", queryParams.Select(kvp => $"{kvp.Key}={Uri.EscapeDataString(kvp.Value)}"));
            var requestUri = $"Items?{queryString}";

            try
            {
                var response = await _httpClient.GetAsync(requestUri, cancellationToken);
                response.EnsureSuccessStatusCode();
                MediaResponse? mediaResponse = 
                    await response.Content.ReadFromJsonAsync<MediaResponse>(_jsonOptions, cancellationToken);
                return mediaResponse;
            }
            catch (HttpRequestException ex)
            {
                Console.WriteLine($"HTTP request failed: {ex.Message}");
                throw;
            }
            catch (JsonException ex)
            {
                Console.WriteLine($"JSON parsing failed: {ex.Message}");
                throw;
            }
        }

        public async Task<BoxSetResponse?> GetBoxSetCollectionsAsync(CancellationToken cancellationToken = default)
        {
            var queryParams = new Dictionary<string, string>
            {
                ["IncludeItemTypes"] = "BoxSet",
                ["Recursive"] = "true",
                ["api_key"] = _apiKey
            };

            var queryString = string.Join("&", queryParams.Select(kvp => $"{kvp.Key}={Uri.EscapeDataString(kvp.Value)}"));
            var requestUri = $"Items?{queryString}";

            try
            {
                var response = await _httpClient.GetAsync(requestUri, cancellationToken);
                response.EnsureSuccessStatusCode();
                BoxSetResponse? boxSetResponse 
                    = await response.Content.ReadFromJsonAsync<BoxSetResponse>(_jsonOptions, cancellationToken);
                return boxSetResponse;
            }
            catch (HttpRequestException ex)
            {
                Console.WriteLine($"HTTP request failed: {ex.Message}");
                throw;
            }
            catch (JsonException ex)
            {
                Console.WriteLine($"JSON parsing failed: {ex.Message}");
                throw;
            }
        }

        public async Task<MediaResponse?> GetMoviesInCollectionAsync(string collectionId, CancellationToken cancellationToken = default)
        {
            var queryParams = new Dictionary<string, string>
            {
                ["IncludeItemTypes"] = "Movie",
                ["Recursive"] = "true",
                ["ParentId"] = collectionId,
                ["api_key"] = _apiKey
            };

            var queryString = string.Join("&", queryParams.Select(kvp => $"{kvp.Key}={Uri.EscapeDataString(kvp.Value)}"));
            var requestUri = $"Items?{queryString}";

            try
            {
                var response = await _httpClient.GetAsync(requestUri, cancellationToken);
                response.EnsureSuccessStatusCode();
                MediaResponse? mediaResponse = 
                    await response.Content.ReadFromJsonAsync<MediaResponse>(_jsonOptions, cancellationToken);
                return mediaResponse;
            }
            catch (HttpRequestException ex)
            {
                Console.WriteLine($"HTTP request failed: {ex.Message}");
                throw;
            }
            catch (JsonException ex)
            {
                Console.WriteLine($"JSON parsing failed: {ex.Message}");
                throw;
            }
        }

        public async Task<Dictionary<string, BoxSet>> GetBoxSetCollectionsWithMoviesAsync(CancellationToken cancellationToken = default)
        {
            var collectionsResponse = await GetBoxSetCollectionsAsync(cancellationToken);
            var collections = collectionsResponse?.Items ?? new List<BoxSet>();

            foreach (var collection in collections)
            {
                var moviesResponse = await GetMoviesInCollectionAsync(collection.Id, cancellationToken);
                collection.Movies = moviesResponse?.Items ?? new List<Movie>();
            }

            return collections.ToDictionary(c => c.Id);
        }

        public static Dictionary<string, List<string>> BuildCollectionLookup(Dictionary<string, BoxSet> collections)
        {
            var collectionMap = new Dictionary<string, List<string>>();

            foreach (var collection in collections.Values)
            {
                foreach (var movie in collection.Movies)
                {
                    if (!collectionMap.TryGetValue(movie.Id, out var itemCollections))
                    {
                        itemCollections = new List<string>();
                        collectionMap[movie.Id] = itemCollections;
                    }
                    itemCollections.Add(collection.Id);
                }
            }

            return collectionMap;
        }

        public void Dispose()
        {
            _httpClient.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
