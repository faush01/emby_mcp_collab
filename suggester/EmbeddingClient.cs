using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;
using OpenAI;
using suggester.Models;

namespace suggester
{
    public class EmbeddingClient
    {
        private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;

        public EmbeddingClient(string ollamaEndpoint, string embeddingModel)
        {
            var openAiClient = new OpenAIClient(
                credential: new System.ClientModel.ApiKeyCredential("ollama"),
                options: new OpenAIClientOptions { Endpoint = new Uri(ollamaEndpoint) }
            );

            _embeddingGenerator = openAiClient.GetEmbeddingClient(embeddingModel).AsIEmbeddingGenerator();
        }

        public async Task<EmbeddingResult> GetEmbeddingAsync(DocInfo docInfo)
        {
            var embeddings = await _embeddingGenerator.GenerateAsync(new[] { docInfo.DocText });
            var embedding = embeddings[0];
            docInfo.Embedding = NormalizeVector(embedding.Vector.ToArray());

            var result = new EmbeddingResult
            {
                DocInfo = docInfo,
                InputTokens = embeddings.Usage?.InputTokenCount,
                TotalTokens = embeddings.Usage?.TotalTokenCount,
                ModelId = embedding.ModelId,
                CreatedAt = embedding.CreatedAt,
                AdditionalProperties = embedding.AdditionalProperties
            };

            return result;
        }

        private static float[] NormalizeVector(float[] vector)
        {
            float magnitude = MathF.Sqrt(vector.Sum(x => x * x));
            if (magnitude == 0) return vector;
            float[] normalizedVector = vector.Select(x => x / magnitude).ToArray();
            return normalizedVector;
        }

        public List<DocInfo> GetMovieDocuments(
            List<Movie> movies,
            Dictionary<string, BoxSet> collectionsWithMovies,
            Dictionary<string, List<string>> movieToCollections)
        {
            List<DocInfo> documents = [];

            foreach (var movie in movies)
            {
                var document = CreateItemDocument(movie, collectionsWithMovies, movieToCollections);
                
                var docInfo = new DocInfo
                {
                    DocId = movie.Id,
                    DocName = movie.Name,
                    DocText = document,
                    DocHash = ComputeMd5Hash(document)
                };
                
                documents.Add(docInfo);
            }

            return documents;
        }

        public static string CreateItemDocument(
            Movie movie,
            Dictionary<string, BoxSet> collectionData,
            Dictionary<string, List<string>> collectionMap)
        {
            var lines = new List<string>();

            // Basic Information
            lines.Add("--- BASIC INFORMATION ---");
            lines.Add($"Title: {movie.Name}");
            lines.Add("");

            // Collections
            if (collectionMap.TryGetValue(movie.Id, out var collectionIds))
            {
                HashSet<string> addedMovies = new HashSet<string>();
                foreach (var collectionId in collectionIds)
                {
                    if (collectionData.TryGetValue(collectionId, out var collection))
                    {
                        foreach (var collectionMovie in collection.Movies)
                        {
                            if (collectionMovie.Name != movie.Name && !addedMovies.Contains(collectionMovie.Name))
                            {
                                addedMovies.Add(collectionMovie.Name);
                            }
                        }
                    }
                }
                lines.Add("--- COLLECTIONS ---");
                lines.Add("Other movies in the collection:");

                foreach (var movieName in addedMovies)
                {
                    lines.Add($"• {movieName}");
                }
                lines.Add("");
            }

            // Dates
            lines.Add("--- DATES ---");
            lines.Add($"Production Year: {movie.ProductionYear?.ToString() ?? "N/A"}");
            var decade = (movie.ProductionYear ?? 0) / 10 * 10;
            lines.Add($"Decade: {decade}");
            lines.Add("");

            // Ratings and Classifications
            lines.Add("--- RATINGS & CLASSIFICATION ---");
            lines.Add($"Community Rating: {movie.CommunityRating?.ToString() ?? "N/A"}");
            lines.Add($"Critic Rating: {movie.CriticRating?.ToString() ?? "N/A"}");
            lines.Add($"Classification: {movie.OfficialRating ?? "N/A"}");
            lines.Add("");

            // Runtime
            if (movie.RunTimeTicks.HasValue)
            {
                var minutes = movie.RunTimeTicks.Value / 600000000;
                lines.Add("--- RUNTIME ---");
                lines.Add($"Duration: {minutes} minutes");
                lines.Add("");
            }

            // Overview
            if (!string.IsNullOrEmpty(movie.Overview))
            {
                lines.Add("--- OVERVIEW ---");
                lines.Add(movie.Overview);
                lines.Add("");
            }

            // Taglines
            if (movie.Taglines.Count > 0)
            {
                lines.Add("--- TAGLINES ---");
                foreach (var tagline in movie.Taglines)
                {
                    lines.Add($"• {tagline}");
                }
                lines.Add("");
            }

            // Genres
            if (movie.Genres.Count > 0 || movie.GenreItems.Count > 0)
            {
                lines.Add("--- GENRES ---");
                if (movie.Genres.Count > 0)
                {
                    lines.Add($"Genres: {string.Join(", ", movie.Genres)}");
                }
                foreach (var genreItem in movie.GenreItems)
                {
                    lines.Add($"• {genreItem.Name}");
                }
                lines.Add("");
            }

            // Production Locations
            if (movie.ProductionLocations.Count > 0)
            {
                lines.Add("--- PRODUCTION LOCATIONS ---");
                foreach (var location in movie.ProductionLocations)
                {
                    lines.Add($"• {location}");
                }
                lines.Add("");
            }

            // Studios
            if (movie.Studios.Count > 0)
            {
                lines.Add("--- STUDIOS ---");
                foreach (var studio in movie.Studios)
                {
                    lines.Add($"• {studio.Name}");
                }
                lines.Add("");
            }

            // People (Cast and Crew)
            if (movie.People.Count > 0)
            {
                lines.Add("--- CAST & CREW ---");

                var actors = new List<string>();
                var others = new List<string>();

                foreach (var person in movie.People)
                {
                    if (person.Type == "Actor" && !actors.Contains(person.Name))
                    {
                        actors.Add(person.Name);
                    }
                    else if (!actors.Contains(person.Name) && !others.Contains($"{person.Type} - {person.Name}"))
                    {
                        others.Add($"{person.Type} - {person.Name}");
                    }
                }

                if (actors.Count > 0)
                {
                    lines.Add("");
                    lines.Add("Actors:");
                    const int maxActors = 10;
                    foreach (var actor in actors.Take(maxActors))
                    {
                        lines.Add($"  • {actor}");
                    }
                }

                if (others.Count > 0)
                {
                    lines.Add("");
                    lines.Add("Other Credits:");
                    foreach (var other in others)
                    {
                        lines.Add($"  • {other}");
                    }
                }

                lines.Add("");
            }

            // Tags
            if (movie.TagItems.Count > 0)
            {
                lines.Add("--- TAGS ---");
                foreach (var tagItem in movie.TagItems)
                {
                    lines.Add($"• {tagItem.Name}");
                }
                lines.Add("");
            }

            return string.Join("\n", lines);
        }

        private static string ComputeMd5Hash(string input)
        {
            byte[] inputBytes = Encoding.UTF8.GetBytes(input);
            byte[] hashBytes = MD5.HashData(inputBytes);
            return Convert.ToHexString(hashBytes).ToLowerInvariant();
        }
    }
}
