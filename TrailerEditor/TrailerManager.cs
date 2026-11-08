using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using TrailerEditor.Actions;
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

        private readonly Action<BaseItem> _persist;

        /// <param name="persist">Saves an item after its links changed; the default writes to Emby's library. Tests pass their own.</param>
        public TrailerManager(ILibraryManager libraryManager, ILogger logger, RejectedStore rejected = null, Action<BaseItem> persist = null)
        {
            _libraryManager = libraryManager;
            _logger = logger;
            Rejected = rejected;
            _persist = persist ?? (item => item.UpdateToRepository(ItemUpdateType.MetadataEdit));
        }

        /// <summary>Links that were deleted or removed from items; null in tests that don't need it.</summary>
        public RejectedStore Rejected { get; }

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
            _persist(item);
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

        /// <summary>
        /// Saves links edited by a person. Links that were on the item but are missing now are
        /// rejected (so the trailer finder and refreshes don't bring them back); links that were
        /// rejected earlier but are saved again are allowed again.
        /// </summary>
        public string[] SaveEdited(BaseItem item, IEnumerable<string> urls, bool pin)
        {
            var normalized = TrailerUrls.Normalize(urls); // throws for invalid links before anything changes
            if (Rejected != null)
            {
                var key = Key(item);
                var keptIds = new HashSet<string>(normalized.Select(TrailerUrls.YouTubeIdOf).Where(id => id != null));
                foreach (var id in keptIds)
                {
                    Rejected.Allow(key, id);
                }

                var removed = (item.RemoteTrailers ?? new string[0])
                    .Where(u => { var id = TrailerUrls.YouTubeIdOf(u); return id != null && !keptIds.Contains(id); })
                    .ToList();
                Rejected.Reject(key, item.Name, removed, RejectedStore.ReasonRemovedInEditor);
            }

            return SetTrailers(item, normalized, pin);
        }

        /// <summary>
        /// Removes all trailer links of an item, rejects them so they don't come back, and unpins the item.
        /// Returns how many links were removed.
        /// </summary>
        public int DeleteTrailers(BaseItem item)
        {
            var current = item.RemoteTrailers ?? new string[0];
            var pinned = GetOverride(item)?.Urls ?? new string[0];
            Rejected?.Reject(Key(item), item.Name, current.Concat(pinned), RejectedStore.ReasonDeleted);

            item.RemoteTrailers = new string[0];
            _persist(item);
            Unpin(item);
            EntryPoint.Store?.RemoveIssuesFor(item.InternalId.ToString());
            _logger.Info("Trailer Editor: deleted {0} trailer link(s) of {1} ({2})", current.Length, item.Name, item.InternalId);
            return current.Length;
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
            if (item == null || !IsSupported(item))
            {
                return false;
            }

            var current = item.RemoteTrailers ?? new string[0];
            var pinned = Config.PinEditedTrailers ? GetOverride(item) : null;
            var wanted = pinned != null ? pinned.Urls ?? new string[0] : current;

            // A refresh can bring back links the person deleted; never keep (or restore) those.
            if (Rejected != null)
            {
                var key = Key(item);
                wanted = wanted.Where(u => !Rejected.IsRejectedUrl(key, u)).ToArray();
            }

            if (current.SequenceEqual(wanted, StringComparer.Ordinal))
            {
                return false;
            }

            item.RemoteTrailers = wanted;
            _persist(item);
            _logger.Info("Trailer Editor: {0} trailer link(s) on {1} ({2})",
                pinned != null ? "restored pinned" : "removed rejected", item.Name, item.InternalId);
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

        public static string Key(BaseItem item) => item.Id.ToString("N");
    }
}
