namespace suggester.Models
{
    public class AuthenticationResponse
    {
        public string AccessToken { get; set; } = string.Empty;
        public AuthUser? User { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    public class AuthUser
    {
        public string Id { get; set; } = string.Empty;
    }

    public class MediaResponse
    {
        public List<Movie> Items { get; set; } = new();
        public int TotalRecordCount { get; set; }
    }

    public class Movie
    {
        public string Name { get; set; } = string.Empty;
        public string ServerId { get; set; } = string.Empty;
        public string Id { get; set; } = string.Empty;
        public DateTime? DateCreated { get; set; }
        public DateTime? PremiereDate { get; set; }
        public int? CriticRating { get; set; }
        public List<string> ProductionLocations { get; set; } = new();
        public string? OfficialRating { get; set; }
        public string? Overview { get; set; }
        public List<string> Taglines { get; set; } = new();
        public List<string> Genres { get; set; } = new();
        public double? CommunityRating { get; set; }
        public long? RunTimeTicks { get; set; }
        public int? ProductionYear { get; set; }
        public bool IsFolder { get; set; }
        public string Type { get; set; } = string.Empty;
        public List<Person> People { get; set; } = new();
        public List<Studio> Studios { get; set; } = new();
        public List<GenreItem> GenreItems { get; set; } = new();
        public List<TagItem> TagItems { get; set; } = new();
        public string MediaType { get; set; } = string.Empty;
    }

    public class Person
    {
        public string Name { get; set; } = string.Empty;
        public string Id { get; set; } = string.Empty;
        public string? Role { get; set; }
        public string Type { get; set; } = string.Empty;
        public string? PrimaryImageTag { get; set; }
    }

    public class Studio
    {
        public string Name { get; set; } = string.Empty;
        public int Id { get; set; }
    }

    public class GenreItem
    {
        public string Name { get; set; } = string.Empty;
        public int Id { get; set; }
    }

    public class TagItem
    {
        public string Name { get; set; } = string.Empty;
        public int Id { get; set; }
    }

    public class BoxSetResponse
    {
        public List<BoxSet> Items { get; set; } = new();
        public int TotalRecordCount { get; set; }
    }

    public class BoxSet
    {
        public string Name { get; set; } = string.Empty;
        public string ServerId { get; set; } = string.Empty;
        public string Id { get; set; } = string.Empty;
        public bool IsFolder { get; set; }
        public string Type { get; set; } = string.Empty;
        public Dictionary<string, string> ImageTags { get; set; } = new();
        public List<string> BackdropImageTags { get; set; } = new();
        public List<Movie> Movies { get; set; } = new();
    }
}
