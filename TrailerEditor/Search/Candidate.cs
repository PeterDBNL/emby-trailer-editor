using System;
using System.Collections.Generic;

namespace TrailerEditor.Search
{
    /// <summary>A possible trailer found by the trailer finder.</summary>
    public class Candidate
    {
        public string VideoId { get; set; }
        public string Url { get; set; }
        public string Title { get; set; }
        public string Channel { get; set; }
        public bool Verified { get; set; }

        /// <summary>Length in seconds; 0 when unknown.</summary>
        public int LengthSeconds { get; set; }

        public string LengthText { get; set; }

        /// <summary>View count; -1 when unknown.</summary>
        public long Views { get; set; } = -1;

        /// <summary>"youtube" (YouTube search page) or "tmdb" (The Movie Database's trailer list).</summary>
        public string Source { get; set; } = "youtube";

        /// <summary>Listed by YouTube's search page (so its title, channel, length and views are known).</summary>
        public bool InSearch { get; set; }

        /// <summary>Listed by TMDb as a trailer or teaser of this title.</summary>
        public bool InTmdb { get; set; }

        // TMDb details
        public DateTime? Published { get; set; }
        public bool Official { get; set; }
        public string Type { get; set; }
        public string Language { get; set; }
        public int Size { get; set; }

        public double Score { get; set; }

        /// <summary>Short, human-readable reasons behind the score.</summary>
        public List<string> Reasons { get; set; } = new List<string>();

        /// <summary>Result of the YouTube check: ok, unknown, or null when not checked.</summary>
        public string Status { get; set; }

        public string StatusMessage { get; set; }
    }

    /// <summary>What the finder knows about the movie or TV show.</summary>
    public class FindRequest
    {
        public string ItemKey { get; set; }
        public string Name { get; set; }
        public string OriginalTitle { get; set; }
        public int? Year { get; set; }
        public bool IsSeries { get; set; }
        public string TmdbId { get; set; }
        public string ImdbId { get; set; }

        /// <summary>Preferred metadata language (e.g. "nl"); "en" when unknown.</summary>
        public string Language { get; set; } = "en";

        public string TmdbApiKey { get; set; }
    }

    public class FindResult
    {
        public bool Found { get; set; }
        public Candidate Chosen { get; set; }

        /// <summary>The best few candidates, best first (the chosen one is first).</summary>
        public List<Candidate> Candidates { get; set; } = new List<Candidate>();

        public string Message { get; set; }

        /// <summary>True when the new links were saved to the item (Apply=true).</summary>
        public bool Applied { get; set; }

        /// <summary>The item's trailer links after the search (and after saving, when applied).</summary>
        public string[] TrailerUrls { get; set; }
    }
}
