using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;
using MediaBrowser.Model.Tasks;
using TrailerEditor.Actions;
using TrailerEditor.Scanning;
using TrailerEditor.Search;

namespace TrailerEditor.Api
{
    public class TrailerItemInfo
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public int? Year { get; set; }
        public string Type { get; set; }
        public string[] TrailerUrls { get; set; }
        public bool IsPinned { get; set; }
    }

    [Route("/TrailerEditor/Search", "GET", Summary = "Finds movies and TV shows by name")]
    public class SearchItems : IReturn<TrailerItemInfo[]>
    {
        [ApiMember(Name = "SearchTerm", IsRequired = true, DataType = "string", ParameterType = "query")]
        public string SearchTerm { get; set; }

        [ApiMember(Name = "Limit", DataType = "int", ParameterType = "query")]
        public int? Limit { get; set; }
    }

    public class TrailerItemPage
    {
        public TrailerItemInfo[] Items { get; set; }
        public int TotalRecordCount { get; set; }
    }

    public class ScanIssuePage
    {
        public ScanIssue[] Items { get; set; }
        public int TotalRecordCount { get; set; }
    }

    public class ScanStatus
    {
        /// <summary>Idle, Running or Cancelling.</summary>
        public string State { get; set; }
        public double? Progress { get; set; }
        public bool HasResults { get; set; }
        public DateTime? StartedUtc { get; set; }
        public DateTime? FinishedUtc { get; set; }
        public bool Completed { get; set; }
        public int ItemsScanned { get; set; }
        public int ItemsTotal { get; set; }
        public int LinksTotal { get; set; }
        public int LinksChecked { get; set; }
        public int LinksFromCache { get; set; }
        public int LinksNotChecked { get; set; }
        public string Note { get; set; }

        /// <summary>Live counts while a scan is running.</summary>
        public int ScannedSoFar { get; set; }
        public int TotalToScan { get; set; }
        public int ProblemsSoFar { get; set; }

        public int IssueCount { get; set; }
        public int NoTrailersCount { get; set; }
        public int UnplayableCount { get; set; }
        public int InvalidLinkCount { get; set; }
        public int DuplicateLinkCount { get; set; }
        public int PinnedCount { get; set; }
        public bool BrowserPlaybackTest { get; set; }
        public bool AgeRestrictionCheck { get; set; }

        /// <summary>e.g. "every Sunday at 03:00", or null when automatic scans are off.</summary>
        public string Schedule { get; set; }
        public int PlaybackTotal { get; set; }
        public int PlaybackDone { get; set; }
        public int PlaybackBlocked { get; set; }

        /// <summary>True after Stop scan: the copyright-block test doesn't run until continued or a new scan starts.</summary>
        public bool PlaybackStopped { get; set; }

        /// <summary>What the last (or running) scan covers, e.g. "Movies, A–C"; null for everything.</summary>
        public string ScopeText { get; set; }
    }

    [Route("/TrailerEditor/Pinned", "GET", Summary = "Lists items whose trailer links are pinned, one page at a time")]
    public class GetPinned : IReturn<TrailerItemPage>
    {
        public string SearchTerm { get; set; }
        public int? StartIndex { get; set; }
        public int? Limit { get; set; }
    }

    public class PlaybackQueue
    {
        public PlaybackQueueEntry[] Items { get; set; }
    }

    [Route("/TrailerEditor/Playback/Queue", "GET", Summary = "Next scanned YouTube links that need a browser playback test")]
    public class GetPlaybackQueue : IReturn<PlaybackQueue>
    {
        public int? Limit { get; set; }
    }

    [Route("/TrailerEditor/Playback/Result", "POST", Summary = "Records the result of a browser playback test")]
    public class PostPlaybackResult : IReturn<ScanStatus>
    {
        public string VideoId { get; set; }

        /// <summary>ok, error or unknown.</summary>
        public string Status { get; set; }

        public string Message { get; set; }

        /// <summary>browser (Trailer Library page) or editor.</summary>
        public string Source { get; set; }
    }

    [Route("/TrailerEditor/Scan", "GET", Summary = "Gets the state and summary of the library scan")]
    public class GetScanStatus : IReturn<ScanStatus>
    {
    }

    [Route("/TrailerEditor/Scan", "POST", Summary = "Starts a library scan")]
    public class StartScan : IReturn<ScanStatus>
    {
        /// <summary>Movie or Series. Empty for both.</summary>
        public string Types { get; set; }

        /// <summary>First letter to scan: # (anything that isn't A-Z) or A to Z. Default #.</summary>
        public string From { get; set; }

        /// <summary>Last letter to scan: # or A to Z. Default Z.</summary>
        public string To { get; set; }

        /// <summary>Only titles added (date added) in the last this many days, 1 to 3650. Empty or 0 for any time.</summary>
        public int? AddedWithinDays { get; set; }
    }

    [Route("/TrailerEditor/Playback/Stop", "POST", Summary = "Stops the browser copyright-block test")]
    public class StopPlayback : IReturn<ScanStatus>
    {
    }

    [Route("/TrailerEditor/Playback/Continue", "POST", Summary = "Lets the browser copyright-block test continue")]
    public class ContinuePlayback : IReturn<ScanStatus>
    {
    }

    [Route("/TrailerEditor/Scan/Cancel", "POST", Summary = "Stops a running library scan")]
    public class CancelScan : IReturn<ScanStatus>
    {
    }

    [Route("/TrailerEditor/Scan/Issues", "GET", Summary = "Lists problems found by the last library scan, one page at a time")]
    public class GetScanIssues : IReturn<ScanIssuePage>
    {
        /// <summary>NoTrailers, Unplayable, InvalidLink or DuplicateLink. Empty for all.</summary>
        public string Kind { get; set; }
        public string SearchTerm { get; set; }
        public int? StartIndex { get; set; }
        public int? Limit { get; set; }
    }

    [Route("/TrailerEditor/Items/{Id}", "GET", Summary = "Gets the trailer links of an item")]
    public class GetItemTrailers : IReturn<TrailerItemInfo>
    {
        [ApiMember(Name = "Id", IsRequired = true, DataType = "string", ParameterType = "path")]
        public string Id { get; set; }
    }

    [Route("/TrailerEditor/Items/{Id}", "POST", Summary = "Replaces the trailer links of an item. Body: { Urls: [..], Pin: true }")]
    public class SetItemTrailers : IReturn<TrailerItemInfo>
    {
        [ApiMember(Name = "Id", IsRequired = true, DataType = "string", ParameterType = "path")]
        public string Id { get; set; }

        [ApiMember(Name = "Urls", Description = "Trailer links, separated by spaces or new lines.", DataType = "string", ParameterType = "query")]
        public string Urls { get; set; }

        [ApiMember(Name = "Pin", Description = "Pin the links so refreshes can't replace them (default true).", DataType = "bool", ParameterType = "query")]
        public bool? Pin { get; set; }

        [ApiMember(Name = "Clear", Description = "Set to true (with no Urls) to remove all trailer links.", DataType = "bool", ParameterType = "query")]
        public bool? Clear { get; set; }
    }

    [Route("/TrailerEditor/Check", "GET", Summary = "Checks whether a trailer link can play in Emby")]
    public class CheckLink : IReturn<LinkCheckResult>
    {
        [ApiMember(Name = "Url", IsRequired = true, DataType = "string", ParameterType = "query")]
        public string Url { get; set; }
    }

    [Route("/TrailerEditor/Items/{Id}/Pin", "DELETE", Summary = "Stops pinning the trailer links of an item (links stay as they are)")]
    public class UnpinItem : IReturn<TrailerItemInfo>
    {
        [ApiMember(Name = "Id", IsRequired = true, DataType = "string", ParameterType = "path")]
        public string Id { get; set; }
    }

    [Route("/TrailerEditor/Items/{Id}/Find", "POST", Summary = "Searches for the best trailer of an item. Apply=false only returns the candidates")]
    public class FindTrailer : IReturn<FindResult>
    {
        [ApiMember(Name = "Id", IsRequired = true, DataType = "string", ParameterType = "path")]
        public string Id { get; set; }

        [ApiMember(Name = "Apply", Description = "True to put the best trailer first on the item (pinned); false (default) only lists candidates.", DataType = "bool", ParameterType = "query")]
        public bool? Apply { get; set; }
    }

    [Route("/TrailerEditor/Items/{Id}/DeleteTrailers", "POST", Summary = "Deletes all trailer links of an item and remembers them so they aren't picked again")]
    public class DeleteTrailers : IReturn<DeleteResult>
    {
        [ApiMember(Name = "Id", IsRequired = true, DataType = "string", ParameterType = "path")]
        public string Id { get; set; }
    }

    public class RejectedPage
    {
        public RejectedLink[] Items { get; set; }
        public int TotalRecordCount { get; set; }
    }

    [Route("/TrailerEditor/Items/{Id}/Rejected", "GET", Summary = "Lists the trailer links that were deleted from an item")]
    public class GetRejected : IReturn<RejectedPage>
    {
        [ApiMember(Name = "Id", IsRequired = true, DataType = "string", ParameterType = "path")]
        public string Id { get; set; }
    }

    [Route("/TrailerEditor/Items/{Id}/Rejected/Allow", "POST", Summary = "Allows a deleted trailer link again")]
    public class AllowRejected : IReturn<RejectedPage>
    {
        [ApiMember(Name = "Id", IsRequired = true, DataType = "string", ParameterType = "path")]
        public string Id { get; set; }

        [ApiMember(Name = "VideoId", IsRequired = true, DataType = "string", ParameterType = "query")]
        public string VideoId { get; set; }
    }

    public class ActionPage
    {
        public ActionRecord[] Items { get; set; }
        public int TotalRecordCount { get; set; }
    }

    [Route("/TrailerEditor/Actions", "GET", Summary = "Lists recent trailer find / delete actions, newest first")]
    public class GetActions : IReturn<ActionPage>
    {
        public int? Limit { get; set; }
    }

    [Route("/TrailerEditor/Collections", "GET", Summary = "Shows whether the Find / Delete trailer collections exist")]
    public class GetCollectionStatus : IReturn<CollectionStatus>
    {
    }

    [Authenticated(Roles = "Admin")]
    public class TrailerService : IService
    {
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger _logger;
        private readonly ITaskManager _taskManager;

        public TrailerService(ILibraryManager libraryManager, ILogManager logManager, ITaskManager taskManager)
        {
            _taskManager = taskManager;
            _libraryManager = libraryManager;
            _logger = logManager.GetLogger("TrailerEditor");
        }

        private static TrailerManager Manager => EntryPoint.Manager;

        public object Get(SearchItems request)
        {
            var term = (request.SearchTerm ?? string.Empty).Trim();
            if (term.Length == 0)
            {
                return new TrailerItemInfo[0];
            }

            var items = _libraryManager.GetItemList(new InternalItemsQuery
            {
                SearchTerm = term,
                IncludeItemTypes = TrailerManager.SupportedTypes,
                Recursive = true,
                IsVirtualItem = false,
                Limit = Math.Max(1, Math.Min(request.Limit ?? 30, 100))
            });

            return items.Select(ToInfo).ToArray();
        }

        public object Get(GetPinned request)
        {
            var page = Manager.GetPinnedPage(request.SearchTerm, request.StartIndex ?? 0, PageSize(request.Limit), out var total);
            return new TrailerItemPage { Items = page.Select(ToInfo).ToArray(), TotalRecordCount = total };
        }

        public object Get(GetScanStatus request) => BuildStatus();

        public object Post(StartScan request)
        {
            if (TrailerScanTask.TryRequestStart(new ScanScope(request.Types, request.From, request.To, request.AddedWithinDays)))
            {
                EntryPoint.Store.SetPlaybackStopped(false);
                _taskManager.QueueIfNotRunning<TrailerScanTask>();
            }

            return BuildStatus();
        }

        public object Post(CancelScan request)
        {
            _taskManager.CancelIfRunning<TrailerScanTask>();
            EntryPoint.Store.SetPlaybackStopped(true);
            return BuildStatus();
        }

        public object Post(StopPlayback request)
        {
            EntryPoint.Store.SetPlaybackStopped(true);
            return BuildStatus();
        }

        public object Post(ContinuePlayback request)
        {
            EntryPoint.Store.SetPlaybackStopped(false);
            return BuildStatus();
        }

        public object Get(GetScanIssues request)
        {
            IEnumerable<ScanIssue> issues = EntryPoint.Store.GetIssues();
            if (!string.IsNullOrWhiteSpace(request.Kind))
            {
                issues = issues.Where(i => string.Equals(i.Kind, request.Kind, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrWhiteSpace(request.SearchTerm))
            {
                var term = request.SearchTerm.Trim();
                issues = issues.Where(i => (i.ItemName ?? string.Empty).IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            var list = issues.ToList();
            return new ScanIssuePage
            {
                Items = list.Skip(Math.Max(0, request.StartIndex ?? 0)).Take(PageSize(request.Limit)).ToArray(),
                TotalRecordCount = list.Count
            };
        }

        public object Get(GetPlaybackQueue request)
        {
            var limit = Math.Max(1, Math.Min(request.Limit ?? 10, 100));
            var items = EntryPoint.Store.GetPlaybackQueue(limit, Plugin.Instance.Configuration.ScanRecheckDays);
            return new PlaybackQueue { Items = items.ToArray() };
        }

        public object Post(PostPlaybackResult request)
        {
            var status = (request.Status ?? string.Empty).ToLowerInvariant();
            if (status != YouTubeChecker.Ok && status != YouTubeChecker.Error && status != YouTubeChecker.Unknown)
            {
                throw new ArgumentException("Status must be ok, error or unknown.");
            }

            var videoId = request.VideoId ?? string.Empty;
            if (YouTubeChecker.VideoIdOf("https://www.youtube.com/watch?v=" + videoId) != videoId)
            {
                throw new ArgumentException("Not a YouTube video id: " + videoId);
            }

            var message = request.Message;
            if (message != null && message.Length > 300)
            {
                message = message.Substring(0, 300);
            }

            EntryPoint.Store.RecordPlayback(videoId, status, message, request.Source == "editor" ? "editor" : "browser");
            return BuildStatus();
        }

        private static int PageSize(int? limit) => Math.Max(1, Math.Min(limit ?? 50, 200));

        private ScanStatus BuildStatus()
        {
            var worker = _taskManager.ScheduledTasks.FirstOrDefault(t => t.ScheduledTask is TrailerScanTask);
            var results = EntryPoint.Store.Current;
            var issues = EntryPoint.Store.GetIssues();
            var config = Plugin.Instance.Configuration;
            var playback = EntryPoint.Store.GetPlaybackCounts(config.ScanRecheckDays);
            var state = worker?.State.ToString() ?? "Idle";
            var live = TrailerScanTask.InProgress;
            if (state != "Cancelling")
            {
                if (live != null)
                {
                    state = "Running";
                }
                else if (TrailerScanTask.IsStarting)
                {
                    state = "Starting";
                }
                else if (state == "Running")
                {
                    // Emby says running but the scan hasn't begun its work yet.
                    state = "Starting";
                }
            }

            double? progress = null;
            if (live != null)
            {
                progress = live.ItemsTotal > 0 ? 100.0 * live.ItemsScanned / live.ItemsTotal : 0;
            }
            else if (state != "Idle")
            {
                progress = worker?.CurrentProgress;
            }

            return new ScanStatus
            {
                State = state,
                Progress = progress,
                ScannedSoFar = live?.ItemsScanned ?? 0,
                TotalToScan = live?.ItemsTotal ?? 0,
                ProblemsSoFar = live?.Issues.Count ?? 0,
                HasResults = results.StartedUtc != null,
                StartedUtc = results.StartedUtc,
                FinishedUtc = results.FinishedUtc,
                Completed = results.Completed,
                ItemsScanned = results.ItemsScanned,
                ItemsTotal = results.ItemsTotal,
                LinksTotal = results.LinksTotal,
                LinksChecked = results.LinksChecked,
                LinksFromCache = results.LinksFromCache,
                LinksNotChecked = results.LinksNotChecked,
                Note = results.Note,
                IssueCount = issues.Count,
                NoTrailersCount = issues.Count(i => i.Kind == IssueKinds.NoTrailers),
                UnplayableCount = issues.Count(i => i.Kind == IssueKinds.Unplayable),
                InvalidLinkCount = issues.Count(i => i.Kind == IssueKinds.InvalidLink),
                DuplicateLinkCount = issues.Count(i => i.Kind == IssueKinds.DuplicateLink),
                PinnedCount = Manager.PinnedCount,
                BrowserPlaybackTest = config.ScanBrowserPlaybackTest,
                AgeRestrictionCheck = config.ScanCheckLinks && config.ScanCheckAgeRestriction,
                Schedule = TrailerScanTask.Describe(config),
                PlaybackTotal = playback.Total,
                PlaybackDone = playback.Done,
                PlaybackBlocked = playback.Blocked,
                PlaybackStopped = results.PlaybackStopped,
                ScopeText = ScanScope.Of(live ?? results).Describe()
            };
        }

        public object Get(GetItemTrailers request)
        {
            return ToInfo(RequireItem(request.Id));
        }

        public object Post(SetItemTrailers request)
        {
            var item = RequireItem(request.Id);
            var urls = SplitLines(request.Urls);

            _logger.Info("Trailer Editor: save request for {0} ({1}): {2} link(s), Pin={3}, Clear={4}",
                item.Name, item.InternalId, urls.Length, request.Pin, request.Clear);

            // Never wipe an item's trailers just because the links didn't arrive.
            if (urls.Length == 0 && request.Clear != true)
            {
                throw new ArgumentException("No trailer links were received, so nothing was changed. " +
                    "To remove all trailers, send Clear=true.");
            }

            // Throws ArgumentException (HTTP 400) for invalid links. Links missing from the list are rejected.
            Manager.SaveEdited(item, urls, request.Pin ?? true);
            return ToInfo(item);
        }

        private static string[] SplitLines(string value) =>
            (value ?? string.Empty).Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

        public async Task<object> Get(CheckLink request)
        {
            return await EntryPoint.Checker.CheckAsync(request.Url, CancellationToken.None).ConfigureAwait(false);
        }

        public async Task<object> Post(FindTrailer request)
        {
            var item = RequireItem(request.Id);
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90)))
            {
                return await EntryPoint.Actions.FindAsync(item, request.Apply == true, ActionSources.Editor, timeout.Token).ConfigureAwait(false);
            }
        }

        public object Post(DeleteTrailers request)
        {
            return EntryPoint.Actions.Delete(RequireItem(request.Id), ActionSources.Editor);
        }

        public object Get(GetRejected request) => RejectedFor(RequireItem(request.Id));

        public object Post(AllowRejected request)
        {
            var item = RequireItem(request.Id);
            Manager.Rejected.Allow(TrailerManager.Key(item), request.VideoId);
            return RejectedFor(item);
        }

        public object Get(GetActions request)
        {
            var items = EntryPoint.ActionLog.Recent(Math.Max(1, Math.Min(request.Limit ?? 50, ActionLog.Capacity))).ToArray();
            return new ActionPage { Items = items, TotalRecordCount = items.Length };
        }

        public object Get(GetCollectionStatus request) => EntryPoint.Collections.GetStatus();

        private static RejectedPage RejectedFor(BaseItem item)
        {
            var links = Manager.Rejected.Get(TrailerManager.Key(item)).ToArray();
            return new RejectedPage { Items = links, TotalRecordCount = links.Length };
        }

        public object Delete(UnpinItem request)
        {
            var item = RequireItem(request.Id);
            Manager.Unpin(item);
            return ToInfo(item);
        }

        private BaseItem RequireItem(string id)
        {
            var item = Manager.FindItem(id);
            if (item == null)
            {
                throw new ResourceNotFoundException("No movie or TV show found with id " + id);
            }

            return item;
        }

        private TrailerItemInfo ToInfo(BaseItem item)
        {
            return new TrailerItemInfo
            {
                Id = item.InternalId.ToString(),
                Name = item.Name,
                Year = item.ProductionYear,
                Type = item.GetClientTypeName(),
                TrailerUrls = item.RemoteTrailers ?? new string[0],
                IsPinned = Manager.IsPinned(item)
            };
        }
    }
}
