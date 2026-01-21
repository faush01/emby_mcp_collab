namespace suggester.Models
{
    public class EmbeddingResult
    {
        /// <summary>
        /// The document info with the generated embedding
        /// </summary>
        public DocInfo DocInfo { get; set; } = new();

        /// <summary>
        /// The number of input tokens used
        /// </summary>
        public long? InputTokens { get; set; }

        /// <summary>
        /// The total number of tokens used
        /// </summary>
        public long? TotalTokens { get; set; }

        /// <summary>
        /// The model used to generate the embedding
        /// </summary>
        public string? ModelId { get; set; }

        /// <summary>
        /// The timestamp when the embedding was created
        /// </summary>
        public DateTimeOffset? CreatedAt { get; set; }

        /// <summary>
        /// Additional metadata from the embedding generation
        /// </summary>
        public IReadOnlyDictionary<string, object?>? AdditionalProperties { get; set; }
    }
}
