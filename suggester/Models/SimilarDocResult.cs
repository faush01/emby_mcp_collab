namespace suggester.Models;

public class SimilarDocResult
{
    public DocInfo Document { get; set; } = null!;
    public float Similarity { get; set; }
}
