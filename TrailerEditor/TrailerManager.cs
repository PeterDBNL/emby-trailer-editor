using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using TrailerEditor.Configuration;

namespace TrailerEditor
{
    /// <summary>Reads and writes trailer links and keeps track of pinned overrides.</summary>
    public class TrailerManager
    {
        public static readonly string[] SupportedTypes = { "Movie", "Series" };

        private readonly object _lock = new object();
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger _logger;

        public TrailerManager(ILibraryManager libraryManager, ILogger logger)
        {
            _libraryManager = libraryManager;
            _logger = logger;
        }

        private static PluginConfiguration Config => Plugin.Instance.Configuration;

        /// <summary>Finds an item by its numeric (internal) id or its Guid.</summary>
        public BaseItem FindItem(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }

            BaseItem item = null;
            if (long.TryParse(id, out var internalId))
            {
                item = _libraryManager.GetItemById(internalId);
            }
            else if (Guid.TryParse(id, out var guid))
            {
                item = _libraryManager.GetItemById(guid);
            }

            return item != null && IsSupported(item) ? item : null;
        }

        public static bool IsSupported(BaseItem item) =>
            SupportedTypes.Contains(item.GetClientTypeName(), StringComparer.OrdinalIgnoreCase);

        public bool IsPinned(BaseItem item) => GetOverride(item) != null;

        public TrailerOverride GetOverride(BaseItem item)
        {
            var key = Key(item);
            lock (_lock)
            {
                return (Config.Overrides ?? new TrailerOverride[0]).FirstOrDefault(o => o.ItemId == key);
            }
        }

        /// <summary>Saves new trailer links to the item and optionally pins them.</summary>
        public string[] SetTrailers(BaseItem item, IEnumerable<string> urls, bool pin)
        {
            var normalized = TrailerUrls.Normalize(urls);

            item.RemoteTrailers = normalized;
            item.UpdateToRepository(ItemUpdateType.MetadataEdit);
            _logger.Info("Trailer Editor: set {0} trailer link(s) on {1} ({2})", normalized.Length, item.Name, item.InternalId);

            lock (_lock)
            {
                var key = Key(item);
                var list = (Config.Overrides ?? new TrailerOverride[0]).Where(o => o.ItemId != key).ToList();
                if (pin)
                {
                    list.Add(new TrailerOverride { ItemId = key, ItemName = item.Name, Urls = normalized });
                }

                Config.Overrides = list.ToArray();
                Plugin.Instance.SaveConfiguration();
            }

            // The item was just fixed (or changed); the next scan will look at it again.
            EntryPoint.Store?.RemoveIssuesFor(item.InternalId.ToString());

            return normalized;
        }

        public void Unpin(BaseItem item)
        {
            lock (_lock)
            {
                var key = Key(item);
                Config.Overrides = (Config.Overrides ?? new TrailerOverride[0]).Where(o => o.ItemId != key).ToArray();
                Plugin.Instance.SaveConfiguration();
            }
        }

        /// <summary>
        /// Puts pinned trailer links back if something (usually a metadata refresh)
        /// changed them. Returns true if the item was updated.
        /// </summary>
        public bool ReapplyIfChanged(BaseItem item)
        {
            if (item == null || !Config.PinEditedTrailers || !IsSupported(item))
            {
                return false;
            }

            var pinned = GetOverride(item);
            if (pinned == null)
            {
                return false;
            }

            var wanted = pinned.Urls ?? new string[0];
            var current = item.RemoteTrailers ?? new string[0];
            if (current.SequenceEqual(wanted, StringComparer.Ordinal))
            {
                return false;
            }

            item.RemoteTrailers = wanted;
            item.UpdateToRepository(ItemUpdateType.MetadataEdit);
            _logger.Info("Trailer Editor: restored pinned trailer link(s) on {0} ({1})", item.Name, item.InternalId);
            return true;
        }

        public int PinnedCount
        {
            get
            {
                lock (_lock)
                {
                    return (Config.Overrides ?? new TrailerOverride[0]).Length;
                }
            }
        }

        /// <summary>
        /// One page of pinned items, sorted by name. Filtering and paging use the names
        /// stored with the pins, so only the items on the page are loaded.
        /// </summary>
        public List<BaseItem> GetPinnedPage(string searchTerm, int startIndex, int limit, out int total)
        {
            TrailerOverride[] overrides;
            lock (_lock)
            {
                overrides = (Config.Overrides ?? new TrailerOverride[0]).ToArray();
            }

            IEnumerable<TrailerOverride> query = overrides;
            if (!string.IsNullOrWhiteSpace(searchTerm))
            {
                var term = searchTerm.Trim();
                query = query.Where(o => (o.ItemName ?? string.Empty).IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            var matches = query.OrderBy(o => o.ItemName ?? string.Empty, StringComparer.OrdinalIgnoreCase).ToList();
            total = matches.Count;

            var page = new List<BaseItem>();
            foreach (var o in matches.Skip(Math.Max(0, startIndex)).Take(Math.Max(1, limit)))
            {
                if (Guid.TryParse(o.ItemId, out var guid))
                {
                    var item = _libraryManager.GetItemById(guid);
                    if (item != null)
                    {
                        page.Add(item);
                    }
                }
            }

            return page;
        }

        private static string Key(BaseItem item) => item.Id.ToString("N");
    }
}
