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

        // authenticate to the Emby server to get an access token to store in the session
        public async Task<AuthenticationResponse> AuthenticateAsync(string username, string password = "", CancellationToken cancellationToken = default)
        {
            username = username.Trim();
            if (string.IsNullOrEmpty(username))
            {
                return new AuthenticationResponse { Message = "No username provided" };
            }

            // Send as form-urlencoded data like the Python version
            var formData = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("username", username),
                new KeyValuePair<string, string>("pw", password)
            });

            // Add X-Emby-Authorization header required for authentication
            const string client = "Suggester";
            const string deviceName = "suggester_device";
            const string deviceId = "1234-1234-1234";
            const string version = "1.0.0";
            var authString = $"MediaBrowser Client=\"{client}\",Device=\"{deviceName}\",DeviceId=\"{deviceId}\",Version=\"{version}\"";
            
            using var request = new HttpRequestMessage(HttpMethod.Post, "Users/AuthenticateByName")
            {
                Content = formData
            };
            request.Headers.TryAddWithoutValidation("X-Emby-Authorization", authString);

            try
            {
                var response = await _httpClient.SendAsync(request, cancellationToken);

                // Dump full response text for debugging
                var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
                Console.WriteLine($"AuthenticateByName Response:\n{responseText}");
                
                if(response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    Console.WriteLine($"Authentication failed: Unauthorized:\n{responseText}");
                    return new AuthenticationResponse { Message = "Unauthorized: " + responseText };
                }
                response.EnsureSuccessStatusCode();

                var authResponse = JsonSerializer.Deserialize<AuthenticationResponse>(responseText, _jsonOptions);
                if (authResponse != null && !string.IsNullOrEmpty(authResponse.AccessToken))
                {
                    return authResponse;
                }

                return new AuthenticationResponse { Message = "Authentication failed: Unknown error: " + responseText};
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

        public async Task<List<Movie>> RecentlyWatchedAsync(
            string userId,
            string accessToken,
            int limit = 10,
            CancellationToken cancellationToken = default)
        {
            var queryParams = new Dictionary<string, string>
            {
                ["IncludeItemTypes"] = "Movie",
                ["Fields"] = string.Join(",", FieldNames),
                ["IsPlayed"] = "True",
                ["SortBy"] = "DatePlayed",
                ["SortOrder"] = "Descending",
                ["CollapseBoxSetItems"] = "False",
                ["GroupItemsIntoCollections"] = "False",
                ["Recursive"] = "True",
                ["IsMissing"] = "False",
                ["ImageTypeLimit"] = "1",
                ["Limit"] = limit.ToString()//,
                //["api_key"] = _apiKey
            };

            var queryString = string.Join("&", queryParams.Select(kvp => $"{kvp.Key}={Uri.EscapeDataString(kvp.Value)}"));
            var requestUri = $"Users/{userId}/Items?{queryString}";
            Console.WriteLine($"Request URI: {requestUri}");

            // add auth token to header as X-MediaBrowser-Token
            _httpClient.DefaultRequestHeaders.Remove("X-MediaBrowser-Token");
            _httpClient.DefaultRequestHeaders.Add("X-MediaBrowser-Token", accessToken);

            try
            {
                var response = await _httpClient.GetAsync(requestUri, cancellationToken);
                var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"RecentlyWatchedAsync failed with status {response.StatusCode}:\n{responseText}");
                }
                response.EnsureSuccessStatusCode();
                MediaResponse? mediaResponse =
                    //await response.Content.ReadFromJsonAsync<MediaResponse>(_jsonOptions, cancellationToken);
                    JsonSerializer.Deserialize<MediaResponse>(responseText, _jsonOptions);

                return mediaResponse?.Items ?? new List<Movie>();
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
                if (!response.IsSuccessStatusCode)
                {
                    var errorText = await response.Content.ReadAsStringAsync(cancellationToken);
                    Console.WriteLine($"GetMovieAsync failed with status {response.StatusCode}: {errorText}");
                }
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
                if (!response.IsSuccessStatusCode)
                {
                    var errorText = await response.Content.ReadAsStringAsync(cancellationToken);
                    Console.WriteLine($"GetMoviesAsync failed with status {response.StatusCode}: {errorText}");
                }
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
                if (!response.IsSuccessStatusCode)
                {
                    var errorText = await response.Content.ReadAsStringAsync(cancellationToken);
                    Console.WriteLine($"GetBoxSetCollectionsAsync failed with status {response.StatusCode}: {errorText}");
                }
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
                if (!response.IsSuccessStatusCode)
                {
                    var errorText = await response.Content.ReadAsStringAsync(cancellationToken);
                    Console.WriteLine($"GetMoviesInCollectionAsync failed with status {response.StatusCode}: {errorText}");
                }
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
