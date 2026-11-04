using System;
using System.Collections.Generic;

namespace TrailerEditor.Scanning
{
    public static class IssueKinds
    {
        public const string NoTrailers = "NoTrailers";
        public const string Unplayable = "Unplayable";
        public const string InvalidLink = "InvalidLink";
        public const string DuplicateLink = "DuplicateLink";
    }

    public class ScanIssue
    {
        public string ItemId { get; set; }
        public string ItemName { get; set; }
        public int? Year { get; set; }
        public string Type { get; set; }

        /// <summary>First letter of the title's sort name ("#" for non-letters); null in results from older versions.</summary>
        public string Letter { get; set; }
        public string Kind { get; set; }
        public string Url { get; set; }
        public string VideoId { get; set; }
        public string Message { get; set; }

        /// <summary>True when found by a browser playback test rather than the server check.</summary>
        public bool FromPlaybackTest { get; set; }
    }

    /// <summary>A YouTube trailer link found by the last scan, used for browser playback tests.</summary>
    public class LinkRef
    {
        public string VideoId { get; set; }
        public string Url { get; set; }
        public string ItemId { get; set; }
        public string ItemName { get; set; }
        public int? Year { get; set; }
        public string Type { get; set; }
        public string Letter { get; set; }
    }

    public class CachedLinkResult
    {
        public string VideoId { get; set; }
        public string Status { get; set; }
        public string Message { get; set; }
        public DateTime CheckedUtc { get; set; }

        /// <summary>For playback results: "browser" (library page), "editor" or "emby" (a real trailer play).</summary>
        public string Source { get; set; }
    }

    public class ScanResults
    {
        public DateTime? StartedUtc { get; set; }
        public DateTime? FinishedUtc { get; set; }

        /// <summary>False if the last scan was cancelled or failed part way.</summary>
        public bool Completed { get; set; }

        /// <summary>What the last scan covered: "" (movies and TV shows), "Movie" or "Series"; null in older results.</summary>
        public string ScopeTypes { get; set; }

        /// <summary>First and last letter of the last scan ("#" or A-Z); null in older results (everything).</summary>
        public string ScopeFrom { get; set; }
        public string ScopeTo { get; set; }

        /// <summary>True when the scan, or the copyright-block test that follows it, was stopped by the user.</summary>
        public bool PlaybackStopped { get; set; }

        public int ItemsScanned { get; set; }
        public int ItemsTotal { get; set; }
        public int LinksTotal { get; set; }
        public int LinksChecked { get; set; }
        public int LinksFromCache { get; set; }
        public int LinksNotChecked { get; set; }

        /// <summary>Anything worth telling the user, e.g. that YouTube rate limited the scan.</summary>
        public string Note { get; set; }

        public List<ScanIssue> Issues { get; set; } = new List<ScanIssue>();
        public List<CachedLinkResult> LinkCache { get; set; } = new List<CachedLinkResult>();

        /// <summary>YouTube links found by the last scan (one per title and video).</summary>
        public List<LinkRef> Links { get; set; } = new List<LinkRef>();

        /// <summary>Results of playback tests in a browser, and of trailers seen playing in Emby. Kept across scans.</summary>
        public List<CachedLinkResult> PlaybackCache { get; set; } = new List<CachedLinkResult>();
    }
}
