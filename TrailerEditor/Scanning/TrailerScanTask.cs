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

        public string Name => "Scan trailer links";

        public string Key => "TrailerEditorScan";

        public string Description =>
            "Finds movies and TV shows without trailers, and trailer links that are broken or won't play in Emby. Results are shown on the Trailer Library page.";

        public string Category => "Trailer Editor";

        public bool IsHidden => false;

        public bool IsEnabled => true;

        public bool IsLogged => true;

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return new[]
            {
                new TaskTriggerInfo
                {
                    Type = TaskTriggerInfo.TriggerWeekly,
                    DayOfWeek = DayOfWeek.Sunday,
                    TimeOfDayTicks = TimeSpan.FromHours(3).Ticks
                }
            };
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            var config = Plugin.Instance.Configuration;
            var store = EntryPoint.Store;
            var previous = store.Current;

            var results = new ScanResults { StartedUtc = DateTime.UtcNow };
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
            });

            results.ItemsTotal = items.Length;
            _logger.Info("Trailer Editor: scanning {0} movies and TV shows", items.Length);

            try
            {
                for (var i = 0; i < items.Length; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    progress.Report(100.0 * i / Math.Max(1, items.Length));

                    var item = items[i];
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
                            results.Issues.Add(Issue(item, IssueKinds.Unplayable, normalized, known.Message));
                        }
                    }
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

                results.LinkCache = cache.Values.ToList();
                results.Issues = results.Issues
                    .OrderBy(x => x.ItemName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(x => x.Kind)
                    .ToList();
                store.Save(results);

                _logger.Info("Trailer Editor: scan {0}. {1} titles, {2} links ({3} checked with YouTube, {4} from earlier results), {5} issues",
                    results.Completed ? "finished" : "stopped", results.ItemsScanned, results.LinksTotal,
                    results.LinksChecked, results.LinksFromCache, results.Issues.Count);
            }
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
                Kind = kind,
                Url = url,
                Message = message
            };
        }
    }
}
