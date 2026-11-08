using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;

namespace TrailerEditor.Actions
{
    public class RejectedLink
    {
        public string VideoId { get; set; }
        public string Url { get; set; }
        public DateTime RejectedUtc { get; set; }

        /// <summary>Why: "deleted", "removed in editor" or "broken".</summary>
        public string Reason { get; set; }
    }

    public class RejectedItem
    {
        /// <summary>Item Guid ("N" format).</summary>
        public string ItemId { get; set; }

        public string ItemName { get; set; }
        public List<RejectedLink> Links { get; set; } = new List<RejectedLink>();
    }

    public class RejectedData
    {
        public List<RejectedItem> Items { get; set; } = new List<RejectedItem>();
    }

    /// <summary>
    /// Trailer links that were deleted or removed from an item. They are never picked again by the
    /// trailer finder and are taken off the item again if a metadata refresh brings them back.
    /// Saved in TrailerEditor.rejected.json next to the plugin config.
    /// </summary>
    public class RejectedStore
    {
        public const string ReasonDeleted = "deleted";
        public const string ReasonRemovedInEditor = "removed in editor";
        public const string ReasonBroken = "broken";

        private readonly object _lock = new object();
        private readonly IJsonSerializer _json;
        private readonly ILogger _logger;
        private readonly string _path;
        private Dictionary<string, RejectedItem> _items;

        public RejectedStore(IApplicationPaths paths, IJsonSerializer json, ILogger logger)
        {
            _json = json;
            _logger = logger;
            _path = Path.Combine(paths.PluginConfigurationsPath, "TrailerEditor.rejected.json");
        }

        /// <summary>Rejects the given links of an item. Links that aren't YouTube links can't be tracked and are skipped.</summary>
        public int Reject(string itemKey, string itemName, IEnumerable<string> urls, string reason)
        {
            var added = 0;
            lock (_lock)
            {
                Load();
                foreach (var url in urls ?? Enumerable.Empty<string>())
                {
                    string normalized;
                    try
                    {
                        normalized = TrailerUrls.NormalizeOne((url ?? string.Empty).Trim());
                    }
                    catch (ArgumentException)
                    {
                        continue;
                    }

                    var videoId = YouTubeChecker.VideoIdOf(normalized);
                    if (videoId == null)
                    {
                        continue;
                    }

                    if (!_items.TryGetValue(itemKey, out var entry))
                    {
                        entry = new RejectedItem { ItemId = itemKey };
                        _items[itemKey] = entry;
                    }

                    entry.ItemName = itemName ?? entry.ItemName;
                    if (entry.Links.Any(l => l.VideoId == videoId))
                    {
                        continue;
                    }

                    entry.Links.Add(new RejectedLink { VideoId = videoId, Url = normalized, RejectedUtc = DateTime.UtcNow, Reason = reason });
                    added++;
                }

                if (added > 0)
                {
                    Save();
                }
            }

            return added;
        }

        public bool IsRejected(string itemKey, string videoId)
        {
            if (videoId == null)
            {
                return false;
            }

            lock (_lock)
            {
                Load();
                return _items.TryGetValue(itemKey, out var entry) && entry.Links.Any(l => l.VideoId == videoId);
            }
        }

        /// <summary>True if the (normalized or raw) link is a rejected YouTube video of this item.</summary>
        public bool IsRejectedUrl(string itemKey, string url)
        {
            try
            {
                return IsRejected(itemKey, YouTubeChecker.VideoIdOf(TrailerUrls.NormalizeOne((url ?? string.Empty).Trim())));
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        /// <summary>Takes a video off the item's rejected list. True if it was on it.</summary>
        public bool Allow(string itemKey, string videoId)
        {
            lock (_lock)
            {
                Load();
                if (videoId == null || !_items.TryGetValue(itemKey, out var entry))
                {
                    return false;
                }

                var removed = entry.Links.RemoveAll(l => l.VideoId == videoId) > 0;
                if (entry.Links.Count == 0)
                {
                    _items.Remove(itemKey);
                }

                if (removed)
                {
                    Save();
                }

                return removed;
            }
        }

        public List<RejectedLink> Get(string itemKey)
        {
            lock (_lock)
            {
                Load();
                return _items.TryGetValue(itemKey, out var entry)
                    ? entry.Links.OrderByDescending(l => l.RejectedUtc).Select(l => Clone(l)).ToList()
                    : new List<RejectedLink>();
            }
        }

        private static RejectedLink Clone(RejectedLink l) =>
            new RejectedLink { VideoId = l.VideoId, Url = l.Url, RejectedUtc = l.RejectedUtc, Reason = l.Reason };

        private void Load()
        {
            if (_items != null)
            {
                return;
            }

            _items = new Dictionary<string, RejectedItem>();
            try
            {
                if (File.Exists(_path))
                {
                    var data = _json.DeserializeFromFile<RejectedData>(_path);
                    foreach (var item in data?.Items ?? new List<RejectedItem>())
                    {
                        if (item?.ItemId != null)
                        {
                            item.Links = item.Links ?? new List<RejectedLink>();
                            _items[item.ItemId] = item;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Trailer Editor: couldn't read rejected links from {0}", ex, _path);
            }
        }

        private void Save()
        {
            try
            {
                _json.SerializeToFile(new RejectedData { Items = _items.Values.ToList() }, _path);
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Trailer Editor: couldn't save rejected links to {0}", ex, _path);
            }
        }
    }
}
