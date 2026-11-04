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
        public string Kind { get; set; }
        public string Url { get; set; }
        public string Message { get; set; }
    }

    public class CachedLinkResult
    {
        public string VideoId { get; set; }
        public string Status { get; set; }
        public string Message { get; set; }
        public DateTime CheckedUtc { get; set; }
    }

    public class ScanResults
    {
        public DateTime? StartedUtc { get; set; }
        public DateTime? FinishedUtc { get; set; }

        /// <summary>False if the last scan was cancelled or failed part way.</summary>
        public bool Completed { get; set; }

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
    }
}
