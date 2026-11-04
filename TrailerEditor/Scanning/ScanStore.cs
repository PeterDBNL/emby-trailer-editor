using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;

namespace TrailerEditor.Scanning
{
    public class PlaybackQueueEntry
    {
        public string VideoId { get; set; }
        public string Url { get; set; }
        public string ItemName { get; set; }
    }

    public class PlaybackCounts
    {
        /// <summary>YouTube videos from the last scan that need a playback test.</summary>
        public int Total { get; set; }

        /// <summary>Of those, how many have an up-to-date playback result.</summary>
        public int Done { get; set; }

        /// <summary>Of those, how many were blocked.</summary>
        public int Blocked { get; set; }
    }

    /// <summary>
    /// Keeps the last scan's results, plus playback-test results, in memory and in a
    /// JSON file next to the plugin config.
    /// </summary>
    public class ScanStore
    {
        private static readonly TimeSpan UnknownRetryAfter = TimeSpan.FromDays(1);
        private static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(20);

        private readonly object _lock = new object();
        private readonly IJsonSerializer _json;
        private readonly ILogger _logger;
        private readonly string _path;
        private readonly Timer _saveTimer;
        private ScanResults _results;
        private Dictionary<string, CachedLinkResult> _playback;

        public ScanStore(IApplicationPaths paths, IJsonSerializer json, ILogger logger)
        {
            _json = json;
            _logger = logger;
            _path = Path.Combine(paths.PluginConfigurationsPath, "TrailerEditor.scan.json");
            _saveTimer = new Timer(_ => SaveNow(), null, Timeout.Infinite, Timeout.Infinite);
        }

        public ScanResults Current
        {
            get
            {
                lock (_lock)
                {
                    EnsureLoaded();
                    return _results;
                }
            }
        }

        /// <summary>Stores a finished (or stopped) scan, keeping the latest playback results.</summary>
        public void SaveScan(ScanResults results)
        {
            lock (_lock)
            {
                EnsureLoaded();
                _results = results;
                SaveNow();
            }
        }

        /// <summary>Stops (or lets continue) the browser copyright-block test.</summary>
        public void SetPlaybackStopped(bool stopped)
        {
            lock (_lock)
            {
                EnsureLoaded();
                if (_results.PlaybackStopped != stopped)
                {
                    _results.PlaybackStopped = stopped;
                    SaveNow();
                }
            }
        }

        /// <summary>Records the result of a playback test, or of a trailer seen playing in Emby.</summary>
        public void RecordPlayback(string videoId, string status, string message, string source)
        {
            if (string.IsNullOrEmpty(videoId) || string.IsNullOrEmpty(status))
            {
                return;
            }

            lock (_lock)
            {
                EnsureLoaded();
                _playback[videoId] = new CachedLinkResult
                {
                    VideoId = videoId,
                    Status = status,
                    Message = message,
                    Source = source,
                    CheckedUtc = DateTime.UtcNow
                };
                _saveTimer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
            }
        }

        public CachedLinkResult GetPlayback(string videoId)
        {
            lock (_lock)
            {
                EnsureLoaded();
                return videoId != null && _playback.TryGetValue(videoId, out var r) ? r : null;
            }
        }

        /// <summary>Drops an item's issues and links after it was edited; the next scan re-checks it.</summary>
        public void RemoveIssuesFor(string itemId)
        {
            lock (_lock)
            {
                EnsureLoaded();
                var removed = _results.Issues.RemoveAll(i => i.ItemId == itemId)
                    + _results.Links.RemoveAll(l => l.ItemId == itemId);
                if (removed > 0)
                {
                    SaveNow();
                }
            }
        }

        /// <summary>
        /// Issues from the last scan plus "won't play" issues from playback tests, sorted by title.
        /// </summary>
        public List<ScanIssue> GetIssues()
        {
            lock (_lock)
            {
                EnsureLoaded();
                var issues = _results.Issues.ToList();
                var reported = new HashSet<string>(
                    issues.Where(i => i.Kind == IssueKinds.Unplayable).Select(i => i.ItemId + "|" + i.VideoId));

                foreach (var link in _results.Links)
                {
                    if (_playback.TryGetValue(link.VideoId, out var r) && r.Status == YouTubeChecker.Error
                        && reported.Add(link.ItemId + "|" + link.VideoId))
                    {
                        issues.Add(new ScanIssue
                        {
                            ItemId = link.ItemId,
                            ItemName = link.ItemName,
                            Year = link.Year,
                            Type = link.Type,
                            Letter = link.Letter,
                            Kind = IssueKinds.Unplayable,
                            Url = link.Url,
                            VideoId = link.VideoId,
                            Message = r.Message,
                            FromPlaybackTest = true
                        });
                    }
                }

                return issues
                    .OrderBy(x => x.ItemName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(x => x.Kind)
                    .ToList();
            }
        }

        /// <summary>
        /// The next YouTube videos that need a browser playback test: found by the last scan,
        /// not already known to be broken, and without an up-to-date playback result.
        /// </summary>
        public List<PlaybackQueueEntry> GetPlaybackQueue(int limit, int recheckDays)
        {
            lock (_lock)
            {
                EnsureLoaded();
                var serverErrors = ServerErrors();
                var freshAfter = FreshAfter(recheckDays);
                if (_results.PlaybackStopped)
                {
                    return new List<PlaybackQueueEntry>();
                }

                return InScope()
                    .Where(l => !serverErrors.Contains(l.VideoId) && !IsFresh(l.VideoId, freshAfter))
                    .GroupBy(l => l.VideoId)
                    .Take(Math.Max(1, limit))
                    .Select(g => new PlaybackQueueEntry { VideoId = g.Key, Url = g.First().Url, ItemName = g.First().ItemName })
                    .ToList();
            }
        }

        public PlaybackCounts GetPlaybackCounts(int recheckDays)
        {
            lock (_lock)
            {
                EnsureLoaded();
                var serverErrors = ServerErrors();
                var freshAfter = FreshAfter(recheckDays);
                var ids = InScope().Select(l => l.VideoId).Where(id => !serverErrors.Contains(id)).Distinct().ToList();
                return new PlaybackCounts
                {
                    Total = ids.Count,
                    Done = ids.Count(id => IsFresh(id, freshAfter)),
                    Blocked = ids.Count(id => _playback.TryGetValue(id, out var r) && r.Status == YouTubeChecker.Error)
                };
            }
        }

        /// <summary>Links inside the last scan's scope, so progress matches what the user asked to scan.</summary>
        private IEnumerable<LinkRef> InScope()
        {
            var scope = ScanScope.Of(_results);
            // A date limit is counted back from when that scan started, so the set doesn't shrink as time passes.
            var started = _results.StartedUtc ?? DateTime.UtcNow;
            return _results.Links.Where(l => scope.Matches(l.Type, l.Letter) && scope.MatchesDate(l.AddedUtc, started));
        }

        private HashSet<string> ServerErrors() => new HashSet<string>(
            _results.LinkCache.Where(c => c.Status == YouTubeChecker.Error && c.VideoId != null).Select(c => c.VideoId));

        /// <summary>Results newer than this are up to date. 0 days = re-test after every scan.</summary>
        private DateTime FreshAfter(int recheckDays) =>
            recheckDays > 0 ? DateTime.UtcNow.AddDays(-recheckDays) : (_results.StartedUtc ?? DateTime.MinValue);

        private bool IsFresh(string videoId, DateTime freshAfter)
        {
            if (!_playback.TryGetValue(videoId, out var r))
            {
                return false;
            }

            // A test that couldn't decide is retried the next day.
            return r.Status == YouTubeChecker.Unknown
                ? r.CheckedUtc >= DateTime.UtcNow - UnknownRetryAfter
                : r.CheckedUtc >= freshAfter;
        }

        private void EnsureLoaded()
        {
            if (_results != null)
            {
                return;
            }

            _results = Load();
            _playback = _results.PlaybackCache
                .Where(c => c.VideoId != null)
                .GroupBy(c => c.VideoId)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(c => c.CheckedUtc).First());
        }

        private void SaveNow()
        {
            lock (_lock)
            {
                if (_results == null)
                {
                    return;
                }

                _results.PlaybackCache = _playback.Values.ToList();
                try
                {
                    _json.SerializeToFile(_results, _path);
                }
                catch (Exception ex)
                {
                    _logger.ErrorException("Trailer Editor: couldn't save scan results to {0}", ex, _path);
                }
            }
        }

        private ScanResults Load()
        {
            try
            {
                if (File.Exists(_path))
                {
                    var loaded = _json.DeserializeFromFile<ScanResults>(_path);
                    if (loaded != null)
                    {
                        loaded.Issues = loaded.Issues ?? new List<ScanIssue>();
                        loaded.LinkCache = loaded.LinkCache ?? new List<CachedLinkResult>();
                        loaded.Links = loaded.Links ?? new List<LinkRef>();
                        loaded.PlaybackCache = loaded.PlaybackCache ?? new List<CachedLinkResult>();
                        return loaded;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Trailer Editor: couldn't read scan results from {0}", ex, _path);
            }

            return new ScanResults();
        }
    }
}
