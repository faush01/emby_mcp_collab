using Microsoft.Data.Sqlite;
using suggester.Models;
using System.Text.Json;

namespace suggester;

public class DocInfoStorage : IDisposable
{
    private readonly string _connectionString;
    private SqliteConnection? _connection;

    public DocInfoStorage(string databasePath)
    {
        _connectionString = $"Data Source={databasePath};Pooling=true;Cache=Shared";
    }

    private SqliteConnection GetConnection()
    {
        if (_connection == null)
        {
            _connection = new SqliteConnection(_connectionString);
            _connection.Open();
        }
        return _connection;
    }

    /// <summary>
    /// Initializes the storage table for DocInfo items.
    /// Creates the table if it doesn't exist with an index on DocId for fast lookups.
    /// </summary>
    public void InitializeStorage()
    {
        var connection = GetConnection();
        
        using var command = connection.CreateCommand();
        command.CommandText = @"
            CREATE TABLE IF NOT EXISTS DocInfo (
                DocId TEXT PRIMARY KEY,
                DocName TEXT NOT NULL,
                DocText TEXT NOT NULL,
                DocHash TEXT NOT NULL,
                Embedding BLOB
            );
            CREATE INDEX IF NOT EXISTS idx_docinfo_docid ON DocInfo(DocId);
            CREATE INDEX IF NOT EXISTS idx_docinfo_dochash ON DocInfo(DocHash);
        ";
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Loads all DocInfo items from storage.
    /// </summary>
    /// <returns>List of all DocInfo items.</returns>
    public List<DocInfo> LoadAllDocItems()
    {
        var connection = GetConnection();
        var items = new List<DocInfo>();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT DocId, DocName, DocText, DocHash, Embedding FROM DocInfo";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            items.Add(ReadDocInfoFromReader(reader));
        }

        return items;
    }



    /// <summary>
    /// Loads a single DocInfo item by its DocId.
    /// </summary>
    /// <param name="docId">The document ID to load.</param>
    /// <returns>The DocInfo item if found, null otherwise.</returns>
    public DocInfo? LoadDocItem(string docId)
    {
        var connection = GetConnection();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT DocId, DocName, DocText, DocHash, Embedding FROM DocInfo WHERE DocId = @DocId";
        command.Parameters.AddWithValue("@DocId", docId);

        using var reader = command.ExecuteReader();
        if (reader.Read())
        {
            return ReadDocInfoFromReader(reader);
        }

        return null;
    }

    /// <summary>
    /// Searches for DocInfo items where the DocName contains the specified search term (case-insensitive).
    /// </summary>
    /// <param name="nameSearchTerm">The search term to match against DocName.</param>
    /// <returns>List of DocInfo items with names matching the search term.</returns>
    public List<DocInfo> SearchByName(string nameSearchTerm)
    {
        var connection = GetConnection();
        var items = new List<DocInfo>();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT DocId, DocName, DocText, DocHash, Embedding FROM DocInfo WHERE DocName LIKE @SearchTerm";
        command.Parameters.AddWithValue("@SearchTerm", $"%{nameSearchTerm}%");

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            items.Add(ReadDocInfoFromReader(reader));
        }

        return items;
    }

    /// <summary>
    /// Saves a single DocInfo item using upsert logic.
    /// Only updates if the hash is different from the existing record.
    /// </summary>
    /// <param name="docItem">The DocInfo item to save.</param>
    /// <returns>True if the item was saved, false if skipped due to same hash.</returns>
    public bool SaveDocItem(DocInfo docItem)
    {
        var connection = GetConnection();

        // Check if item exists and has the same hash
        var existingItem = LoadDocItem(docItem.DocId);
        if (existingItem != null && existingItem.DocHash == docItem.DocHash)
        {
            return false; // Skip saving - hash is the same
        }

        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO DocInfo (DocId, DocName, DocText, DocHash, Embedding)
            VALUES (@DocId, @DocName, @DocText, @DocHash, @Embedding)
            ON CONFLICT(DocId) DO UPDATE SET
                DocName = excluded.DocName,
                DocText = excluded.DocText,
                DocHash = excluded.DocHash,
                Embedding = excluded.Embedding
        ";

        command.Parameters.AddWithValue("@DocId", docItem.DocId);
        command.Parameters.AddWithValue("@DocName", docItem.DocName);
        command.Parameters.AddWithValue("@DocText", docItem.DocText);
        command.Parameters.AddWithValue("@DocHash", docItem.DocHash);
        command.Parameters.AddWithValue("@Embedding", docItem.Embedding != null 
            ? SerializeEmbedding(docItem.Embedding) 
            : DBNull.Value);

        command.ExecuteNonQuery();
        return true;
    }

    /// <summary>
    /// Saves a list of DocInfo items using upsert logic.
    /// Only updates items if their hash is different from existing records.
    /// </summary>
    /// <param name="docItems">The list of DocInfo items to save.</param>
    /// <returns>The number of items that were actually saved (not skipped).</returns>
    public int SaveDocItems(IEnumerable<DocInfo> docItems)
    {
        var connection = GetConnection();
        int savedCount = 0;

        using var transaction = connection.BeginTransaction();
        try
        {
            // First, get all existing hashes in one query for efficiency
            var existingHashes = new Dictionary<string, string>();
            using (var selectCommand = connection.CreateCommand())
            {
                selectCommand.Transaction = transaction;
                selectCommand.CommandText = "SELECT DocId, DocHash FROM DocInfo";
                using var reader = selectCommand.ExecuteReader();
                while (reader.Read())
                {
                    existingHashes[reader.GetString(0)] = reader.GetString(1);
                }
            }
            Console.WriteLine($"Loaded {existingHashes.Count} existing document hashes.");

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
                INSERT INTO DocInfo (DocId, DocName, DocText, DocHash, Embedding)
                VALUES (@DocId, @DocName, @DocText, @DocHash, @Embedding)
                ON CONFLICT(DocId) DO UPDATE SET
                    DocName = excluded.DocName,
                    DocText = excluded.DocText,
                    DocHash = excluded.DocHash,
                    Embedding = excluded.Embedding
            ";

            var docIdParam = command.Parameters.Add("@DocId", SqliteType.Text);
            var docNameParam = command.Parameters.Add("@DocName", SqliteType.Text);
            var docTextParam = command.Parameters.Add("@DocText", SqliteType.Text);
            var docHashParam = command.Parameters.Add("@DocHash", SqliteType.Text);
            var embeddingParam = command.Parameters.Add("@Embedding", SqliteType.Blob);

            foreach (var docItem in docItems)
            {
                // Skip if hash is the same
                if (existingHashes.TryGetValue(docItem.DocId, out var existingHash) 
                    && existingHash == docItem.DocHash)
                {
                    Console.WriteLine($"Skipping DocId {docItem.DocId} - hash unchanged.");
                    continue;
                }

                docIdParam.Value = docItem.DocId;
                docNameParam.Value = docItem.DocName;
                docTextParam.Value = docItem.DocText;
                docHashParam.Value = docItem.DocHash;
                embeddingParam.Value = docItem.Embedding != null 
                    ? SerializeEmbedding(docItem.Embedding) 
                    : DBNull.Value;

                command.ExecuteNonQuery();
                savedCount++;
            }

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }

        return savedCount;
    }

    /// <summary>
    /// Loads all DocInfo items that have embeddings (for KNN lookups).
    /// </summary>
    /// <returns>List of DocInfo items with non-null embeddings.</returns>
    public List<DocInfo> LoadAllDocItemsWithEmbeddings()
    {
        var connection = GetConnection();
        var items = new List<DocInfo>();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT DocId, DocName, DocText, DocHash, Embedding FROM DocInfo WHERE Embedding IS NOT NULL";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            items.Add(ReadDocInfoFromReader(reader));
        }

        return items;
    }

    private DocInfo ReadDocInfoFromReader(SqliteDataReader reader)
    {
        var docInfo = new DocInfo
        {
            DocId = reader.GetString(0),
            DocName = reader.GetString(1),
            DocText = reader.GetString(2),
            DocHash = reader.GetString(3)
        };

        if (!reader.IsDBNull(4))
        {
            var embeddingBytes = (byte[])reader.GetValue(4);
            docInfo.Embedding = DeserializeEmbedding(embeddingBytes);
        }

        return docInfo;
    }

    /// <summary>
    /// Serializes a float array to bytes for efficient storage.
    /// Stores as raw bytes (4 bytes per float) for fast access during cosine similarity calculations.
    /// </summary>
    private static byte[] SerializeEmbedding(float[] embedding)
    {
        var bytes = new byte[embedding.Length * sizeof(float)];
        Buffer.BlockCopy(embedding, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    /// <summary>
    /// Deserializes bytes back to a float array.
    /// </summary>
    private static float[] DeserializeEmbedding(byte[] bytes)
    {
        var floats = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
        return floats;
    }

    /// <summary>
    /// Finds the most similar documents to the query document using cosine similarity.
    /// </summary>
    /// <param name="queryDoc">The query document with an embedding to compare against.</param>
    /// <param name="topN">The maximum number of similar documents to return.</param>
    /// <returns>List of SimilarDocResult items sorted by similarity (highest first).</returns>
    public List<SimilarDocResult> FindSimilarDocuments(DocInfo queryDoc, int topN = 10)
    {
        if (queryDoc.Embedding == null || queryDoc.Embedding.Length == 0)
        {
            throw new ArgumentException("Query document must have an embedding.", nameof(queryDoc));
        }

        var allDocs = LoadAllDocItemsWithEmbeddings();
        var results = new List<SimilarDocResult>();

        foreach (var doc in allDocs)
        {
            // Skip the query document itself if it's in storage
            //if (doc.DocId == queryDoc.DocId)
            //{
            //    continue;
            //}

            if (doc.Embedding != null && doc.Embedding.Length == queryDoc.Embedding.Length)
            {
                float similarity = CosineSimilarity(queryDoc.Embedding, doc.Embedding);
                results.Add(new SimilarDocResult { Document = doc, Similarity = similarity });
            }
        }

        return results
            .OrderByDescending(r => r.Similarity)
            .Take(topN)
            .ToList();
    }

    /// <summary>
    /// Finds the most similar documents to the query document using cosine similarity.
    /// Uses a streaming approach with a min-heap to maintain only the top N results in memory,
    /// avoiding loading all documents at once before sorting.
    /// </summary>
    /// <param name="queryDoc">The query document with an embedding to compare against.</param>
    /// <param name="topN">The maximum number of similar documents to return.</param>
    /// <returns>List of SimilarDocResult items sorted by similarity (highest first).</returns>
    public List<SimilarDocResult> FindSimilarDocumentsStreaming(DocInfo queryDoc, int topN = 10)
    {
        if (queryDoc.Embedding == null || queryDoc.Embedding.Length == 0)
        {
            throw new ArgumentException("Query document must have an embedding.", nameof(queryDoc));
        }

        var connection = GetConnection();
        
        // Min-heap: priority is similarity (lowest first), so we can efficiently remove the lowest
        var topResults = new PriorityQueue<SimilarDocResult, float>();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT DocId, DocName, DocText, DocHash, Embedding FROM DocInfo WHERE Embedding IS NOT NULL";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var embeddingBytes = (byte[])reader.GetValue(4);
            var embedding = DeserializeEmbedding(embeddingBytes);

            // Skip if embedding dimensions don't match
            if (embedding.Length != queryDoc.Embedding.Length)
            {
                continue;
            }

            float similarity = CosineSimilarity(queryDoc.Embedding, embedding);

            if (topResults.Count < topN)
            {
                // Haven't reached topN yet, add this result
                var doc = new DocInfo
                {
                    DocId = reader.GetString(0),
                    DocName = reader.GetString(1),
                    DocText = reader.GetString(2),
                    DocHash = reader.GetString(3),
                    Embedding = embedding
                };
                var result = new SimilarDocResult { Document = doc, Similarity = similarity };
                topResults.Enqueue(result, similarity);
            }
            else if (topResults.TryPeek(out _, out float lowestSimilarity) && similarity > lowestSimilarity)
            {
                // This result is better than our current worst, replace it
                topResults.Dequeue();
                var doc = new DocInfo
                {
                    DocId = reader.GetString(0),
                    DocName = reader.GetString(1),
                    DocText = reader.GetString(2),
                    DocHash = reader.GetString(3),
                    Embedding = embedding
                };
                var result = new SimilarDocResult { Document = doc, Similarity = similarity };
                topResults.Enqueue(result, similarity);
            }
            // Otherwise, skip this document - it's not good enough for top N
        }

        // Extract results and sort by similarity descending
        var results = new List<SimilarDocResult>();
        while (topResults.Count > 0)
        {
            results.Add(topResults.Dequeue());
        }
        results.Reverse(); // Reverse to get highest similarity first

        return results;
    }

    /// <summary>
    /// Calculates the cosine similarity between two vectors.
    /// Cosine similarity = (A · B) / (||A|| * ||B||)
    /// </summary>
    /// <param name="vectorA">First embedding vector.</param>
    /// <param name="vectorB">Second embedding vector.</param>
    /// <returns>Cosine similarity value between -1 and 1 (1 = identical, 0 = orthogonal, -1 = opposite).</returns>
    private static float CosineSimilarity(float[] vectorA, float[] vectorB)
    {
        float dotProduct = 0f;
        float magnitudeA = 0f;
        float magnitudeB = 0f;

        for (int i = 0; i < vectorA.Length; i++)
        {
            dotProduct += vectorA[i] * vectorB[i];
            magnitudeA += vectorA[i] * vectorA[i];
            magnitudeB += vectorB[i] * vectorB[i];
        }

        magnitudeA = MathF.Sqrt(magnitudeA);
        magnitudeB = MathF.Sqrt(magnitudeB);

        if (magnitudeA == 0f || magnitudeB == 0f)
        {
            return 0f;
        }

        return dotProduct / (magnitudeA * magnitudeB);
    }

    public void Dispose()
    {
        _connection?.Close();
        _connection?.Dispose();
        _connection = null;
    }
}
