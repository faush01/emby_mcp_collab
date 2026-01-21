namespace suggester.Models
{
    public class DocInfo
    {
        public string DocId { get; set; } = string.Empty;
        public string DocName { get; set; } = string.Empty;
        public string DocText { get; set; } = string.Empty;
        public string DocHash { get; set; } = string.Empty;
        public float[]? Embedding { get; set; }
    }
}
