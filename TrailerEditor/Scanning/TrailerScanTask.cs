using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;

namespace TrailerEditor.Scanning
{
    /// <summary>
    /// Goes through every movie and TV show and records trailer problems:
    /// no trailers, invalid or duplicate links, and YouTube links that won't play.
    /// Shows up under Scheduled Tasks, where it can also be put on a schedule.
    /// </summary>
    public class TrailerScanTask : IScheduledTask, IConfigurableScheduledTask
    {
        // Stop checking links after this many YouTube failures in a row.
        private const int MaxFailuresInARow = 25;
        private const int MaxDelayMs = 60000;

        private readonly ILibraryManager _libraryManager;
        private readonly ILogger _logger;

        public TrailerScanTask(ILibraryManager libraryManager, ILogManager logManager)
        {
            _libraryManager = libraryManager;
            _logger = logManager.GetLogger("TrailerEditor");
        }

        private static readonly object StateLock = new object();
        private static DateTime? _requestedUtc;
        private static ScanScope _pendingScope;

        /// <summary>The scan that's running right now (live counts), or null.</summary>
        public static ScanResults InProgress { get; private set; }

        /// <summary>
        /// Marks a scan as requested. Returns false if one is already running or about to
        /// start, so pressing Scan now twice doesn't queue a second scan.
        /// </summary>
        public static bool TryRequestStart(ScanScope scope = null)
        {
            lock (StateLock)
            {
                if (InProgress != null || IsStarting)
                {
                    return false;
                }

                _requestedUtc = DateTime.UtcNow;
                _pendingScope = scope;
                return true;
            }
        }

        /// <summary>True between pressing Scan now and Emby actually starting the task (up to a minute).</summary>
        public static bool IsStarting
        {
            get
            {
                lock (StateLock)
                {
                    return InProgress == null && _requestedUtc != null && DateTime.UtcNow - _requestedUtc.Value < TimeSpan.FromMinutes(1);
                }
            }
        }

        public string Name => "Scan trailer links";

        public string Key => "TrailerEditorScan";

        public string Description =>
            "Finds movies and TV shows without trailers, and trailer links that are broken or won't play in Emby. Results are shown on the Trailer Library page.";

        public string Category => "Trailer Editor";

        public bool IsHidden => false;

        public bool IsEnabled => true;

        public bool IsLogged => true;

        /// <summary>No schedule by default; it's set from the plugin's settings (see ScanSchedule).</summary>
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new TaskTriggerInfo[0];

        /// <summary>The triggers that match the plugin's schedule settings.</summary>
        public static TaskTriggerInfo[] TriggersFor(Configuration.PluginConfiguration config)
        {
            var time = TimeSpan.FromHours(Math.Max(0, Math.Min(23, config.ScanScheduleHour))).Ticks;
            switch (config.ScanSchedule)
            {
                case "Daily":
                    return new[] { new TaskTriggerInfo { Type = TaskTriggerInfo.TriggerDaily, TimeOfDayTicks = time } };
                case "Weekly":
                    return new[]
                    {
                        new TaskTriggerInfo
                        {
                            Type = TaskTriggerInfo.TriggerWeekly,
                            DayOfWeek = (DayOfWeek)Math.Max(0, Math.Min(6, config.ScanScheduleDay)),
                            TimeOfDayTicks = time
                        }
                    };
                default:
                    return new TaskTriggerInfo[0];
            }
        }

        /// <summary>Human-readable schedule, e.g. "every Sunday at 03:00".</summary>
        public static string Describe(Configuration.PluginConfiguration config)
        {
            var hour = Math.Max(0, Math.Min(23, config.ScanScheduleHour)).ToString("00") + ":00";
            switch (config.ScanSchedule)
            {
                case "Daily":
                    return "every day at " + hour + Covers(config);
                case "Weekly":
                    return "every " + (DayOfWeek)Math.Max(0, Math.Min(6, config.ScanScheduleDay)) + " at " + hour + Covers(config);
                default:
                    return null;
            }
        }

        private static string Covers(Configuration.PluginConfiguration config)
        {
            var days = new ScanScope(null, null, null, config.ScanScheduleAddedWithinDays).AddedWithinDays;
            return days == 0 ? string.Empty : days == 1 ? ", titles added in the last day" : ", titles added in the last " + days + " days";
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            var config = Plugin.Instance.Configuration;
            var store = EntryPoint.Store;
            var previous = store.Current;

            // The scope asked for with Scan now; automatic and scheduled runs scan the whole library, or just recently added titles if the settings say so.
            ScanScope scope;
            lock (StateLock)
            {
                var fresh = _requestedUtc != null && DateTime.UtcNow - _requestedUtc.Value < TimeSpan.FromMinutes(10);
                scope = fresh && _pendingScope != null
                    ? _pendingScope
                    : new ScanScope(null, null, null, config.ScanScheduleAddedWithinDays);
                _pendingScope = null;
            }

            store.SetPlaybackStopped(false);
            var results = new ScanResults
            {
                StartedUtc = DateTime.UtcNow,
                ScopeTypes = scope.Types,
                ScopeFrom = scope.From,
                ScopeTo = scope.To,
                ScopeAddedWithinDays = scope.DateLimited ? scope.AddedWithinDays : (int?)null
            };
            var dateReference = results.StartedUtc.Value;
            var recheckAfter = DateTime.UtcNow.AddDays(-Math.Max(0, config.ScanRecheckDays));
            var cache = previous.LinkCache
                .Where(c => c.VideoId != null)
                .GroupBy(c => c.VideoId)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(c => c.CheckedUtc).First());

            var delayMs = Math.Max(0, config.ScanRequestDelayMs);
            var failuresInARow = 0;
            var checkLinks = config.ScanCheckLinks;

            var items = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = TrailerManager.SupportedTypes,
                Recursive = true,
                IsVirtualItem = false
            }).Where(i => scope.Matches(i.GetClientTypeName(), LetterOf(i)) && scope.MatchesDate(AddedUtc(i), dateReference)).ToArray();

            results.ItemsTotal = items.Length;
            _logger.Info("Trailer Editor: scanning {0} movies and TV shows{1}", items.Length, scope.IsEverything ? string.Empty : " (" + scope.Describe() + ")");

            lock (StateLock)
            {
                InProgress = results;
                _requestedUtc = null;
            }

            // Titles this run got all the way through, and the one it was in the middle of (if stopped).
            var scanned = new HashSet<string>();
            string current = null;

            try
            {
                for (var i = 0; i < items.Length; i++)
                {
                    if (current != null)
                    {
                        scanned.Add(current);
                        current = null;
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    progress.Report(100.0 * i / Math.Max(1, items.Length));

                    var item = items[i];
                    current = item.InternalId.ToString();
                    var urls = item.RemoteTrailers ?? new string[0];
                    results.ItemsScanned++;

                    if (urls.Length == 0)
                    {
                        if (config.ScanReportNoTrailers)
                        {
                            var local = LocalTrailerCount(item);
                            if (local == 0)
                            {
                                results.Issues.Add(Issue(item, IssueKinds.NoTrailers, null, "No trailers."));
                            }
                            else if (!config.ScanIgnoreItemsWithLocalTrailers)
                            {
                                results.Issues.Add(Issue(item, IssueKinds.NoTrailers, null,
                                    "No online trailer links (has " + local + " local trailer file" + (local == 1 ? "" : "s") + ")."));
                            }
                        }

                        continue;
                    }

                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var url in urls)
                    {
                        results.LinksTotal++;

                        string normalized;
                        try
                        {
                            normalized = TrailerUrls.NormalizeOne((url ?? string.Empty).Trim());
                        }
                        catch (ArgumentException)
                        {
                            results.Issues.Add(Issue(item, IssueKinds.InvalidLink, url, "Not a valid web link."));
                            continue;
                        }

                        if (!seen.Add(normalized))
                        {
                            results.Issues.Add(Issue(item, IssueKinds.DuplicateLink, url, "The same trailer is listed more than once."));
                            continue;
                        }

                        var videoId = YouTubeChecker.VideoIdOf(normalized);
                        if (videoId == null)
                        {
                            // Not a YouTube link; nothing we can check.
                            continue;
                        }

                        // Remembered for browser playback tests (copyright blocks).
                        results.Links.Add(new LinkRef
                        {
                            VideoId = videoId,
                            Url = normalized,
                            ItemId = item.InternalId.ToString(),
                            ItemName = item.Name,
                            Year = item.ProductionYear,
                            Type = item.GetClientTypeName(),
                            Letter = LetterOf(item),
                            AddedUtc = AddedUtc(item)
                        });

                        if (cache.TryGetValue(videoId, out var known) && known.CheckedUtc >= recheckAfter)
                        {
                            results.LinksFromCache++;
                        }
                        else if (checkLinks)
                        {
                            var check = await EntryPoint.Checker
                                .CheckAsync(normalized, config.ScanCheckAgeRestriction, cancellationToken)
                                .ConfigureAwait(false);
                            results.LinksChecked++;

                            if (check.Status == YouTubeChecker.Unknown)
                            {
                                known = null;
                                results.LinksNotChecked++;
                                failuresInARow++;
                                if (check.RateLimited)
                                {
                                    delayMs = Math.Min(MaxDelayMs, Math.Max(2000, delayMs * 2));
                                    _logger.Warn("Trailer Editor: YouTube is rate limiting the scan; slowing down to one request every {0} ms", delayMs);
                                }

                                if (failuresInARow >= MaxFailuresInARow)
                                {
                                    checkLinks = false;
                                    results.Note = "YouTube stopped answering part way through, so the remaining links weren't checked. " +
                                        "They'll be checked on the next scan. If this keeps happening, raise the pause between requests in the settings.";
                                    _logger.Warn("Trailer Editor: {0} YouTube checks failed in a row; skipping the remaining link checks", failuresInARow);
                                }
                            }
                            else
                            {
                                failuresInARow = 0;
                                known = new CachedLinkResult
                                {
                                    VideoId = videoId,
                                    Status = check.Status,
                                    Message = check.Message,
                                    CheckedUtc = DateTime.UtcNow
                                };
                                cache[videoId] = known;
                            }

                            if (delayMs > 0)
                            {
                                await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                            }
                        }
                        else
                        {
                            known = null;
                            results.LinksNotChecked++;
                        }

                        if (known != null && known.Status == YouTubeChecker.Error)
                        {
                            var issue = Issue(item, IssueKinds.Unplayable, normalized, known.Message);
                            issue.VideoId = videoId;
                            results.Issues.Add(issue);
                        }
                    }
                }

                if (current != null)
                {
                    scanned.Add(current);
                    current = null;
                }

                results.Completed = true;
                progress.Report(100);
            }
            finally
            {
                results.FinishedUtc = DateTime.UtcNow;
                if (!results.Completed)
                {
                    results.Note = (results.Note == null ? string.Empty : results.Note + " ") +
                        "The scan was stopped after " + results.ItemsScanned + " of " + results.ItemsTotal + " titles.";
                }

                // A title stopped half way has only some of its new results; drop them and keep its old ones.
                if (current != null)
                {
                    results.Issues.RemoveAll(x => x.ItemId == current);
                    results.Links.RemoveAll(x => x.ItemId == current);
                }

                Merge(results, previous, scope, scanned);
                results.PlaybackStopped = cancellationToken.IsCancellationRequested || store.Current.PlaybackStopped;
                results.LinkCache = cache.Values.ToList();
                results.Issues = results.Issues
                    .OrderBy(x => x.ItemName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(x => x.Kind)
                    .ToList();
                store.SaveScan(results);
                lock (StateLock)
                {
                    InProgress = null;
                }

                _logger.Info("Trailer Editor: scan {0}. {1} titles, {2} links ({3} checked with YouTube, {4} from earlier results), {5} issues",
                    results.Completed ? "finished" : "stopped", results.ItemsScanned, results.LinksTotal,
                    results.LinksChecked, results.LinksFromCache, results.Issues.Count);
            }
        }

        /// <summary>
        /// Adds the earlier results that are still valid to this run's. An earlier entry is kept
        /// when this run did not scan its title AND (the title is outside this run's scope OR this
        /// run didn't finish). So a finished scan of a segment replaces that segment completely
        /// (titles that no longer exist there disappear), while a stopped scan only replaces the
        /// titles it reached. Entries from older versions have no Letter: they count as outside any
        /// letter range that isn't the full # to Z, so they're kept rather than lost; a finished
        /// scan of everything still replaces them.
        /// A date-limited run (added in the last N days) is different: which titles belong to
        /// "recently added" changes over time, so it can't tell which older results it replaces.
        /// It keeps every earlier entry whose title it didn't scan, finished or not.
        /// </summary>
        private static void Merge(ScanResults results, ScanResults previous, ScanScope scope, HashSet<string> scanned)
        {
            bool Keep(string itemId, string type, string letter) =>
                !scanned.Contains(itemId) && (scope.DateLimited || !results.Completed || !scope.Matches(type, letter));

            results.Issues.AddRange(previous.Issues.Where(x => Keep(x.ItemId, x.Type, x.Letter)));
            results.Links.AddRange(previous.Links.Where(x => Keep(x.ItemId, x.Type, x.Letter)));
        }

        /// <summary>When the title was added to the library (Emby's "date added"), in UTC; null if unknown.</summary>
        private static DateTime? AddedUtc(BaseItem item)
        {
            var created = item.DateCreated;
            return created == default(DateTimeOffset) ? (DateTime?)null : created.UtcDateTime;
        }

        private static string LetterOf(BaseItem item)
        {
            string sortName = null;
            try
            {
                sortName = item.SortName;
            }
            catch (Exception)
            {
                // Fall back to the name below.
            }

            return ScanScope.LetterOf(sortName, item.Name);
        }

        private static int LocalTrailerCount(BaseItem item)
        {
            try
            {
                return item.GetExtraIds(new[] { ExtraType.Trailer }).Length;
            }
            catch (Exception)
            {
                return item.LocalTrailerCount ?? 0;
            }
        }

        private static ScanIssue Issue(BaseItem item, string kind, string url, string message)
        {
            return new ScanIssue
            {
                ItemId = item.InternalId.ToString(),
                ItemName = item.Name,
                Year = item.ProductionYear,
                Type = item.GetClientTypeName(),
                Letter = LetterOf(item),
                Kind = kind,
                Url = url,
                Message = message
            };
        }
    }
}
