using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;

namespace TrailerEditor.Actions
{
    public class CollectionStatus
    {
        public bool Enabled { get; set; }
        public bool FindExists { get; set; }
        public bool DeleteExists { get; set; }
        public string FindName { get; set; }
        public string DeleteName { get; set; }
    }

    /// <summary>
    /// Emby apps don't let plugins add menu entries, but every app can "Add to collection". So two
    /// collections act as buttons: adding a movie or TV show to one of them runs the action for that
    /// title, and the title is taken out of the collection again.
    /// </summary>
    public class CollectionActions : IDisposable
    {
        public const string FindName = "Trailer Editor: Find trailer";
        public const string DeleteName = "Trailer Editor: Delete trailer";

        private class Work
        {
            public string Action { get; set; }
            public long CollectionId { get; set; }
            public long ItemId { get; set; }
        }

        private readonly ICollectionManager _collections;
        private readonly ILibraryManager _library;
        private readonly IItemActions _actions;
        private readonly Func<bool> _enabled;
        private readonly ILogger _logger;
        private readonly TimeSpan _gap;
        private readonly TimeSpan _pollEvery;

        private readonly object _lock = new object();
        private readonly ConcurrentQueue<Work> _queue = new ConcurrentQueue<Work>();
        private readonly HashSet<string> _pending = new HashSet<string>();
        private readonly SemaphoreSlim _signal = new SemaphoreSlim(0);
        private readonly SemaphoreSlim _processing = new SemaphoreSlim(1, 1);
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly Dictionary<string, long> _ids = new Dictionary<string, long>();
        private readonly Dictionary<string, DateTime> _recent = new Dictionary<string, DateTime>();
        private int _failures;
        private DateTime _nextEnsure = DateTime.MinValue;
        private bool _started;

        public CollectionActions(ICollectionManager collections, ILibraryManager library, IItemActions actions, Func<bool> enabled,
            ILogger logger, TimeSpan? gap = null, TimeSpan? pollEvery = null)
        {
            _collections = collections;
            _library = library;
            _actions = actions;
            _enabled = enabled;
            _logger = logger;
            _gap = gap ?? TimeSpan.FromSeconds(1);
            _pollEvery = pollEvery ?? TimeSpan.FromSeconds(30);
        }

        /// <summary>Listens for additions and starts the background worker.</summary>
        public void Start()
        {
            if (Subscribe())
            {
                Task.Run(() => LoopAsync(_cts.Token));
            }
        }

        /// <summary>Starts listening for additions only (tests drive the rest by hand). False if already listening.</summary>
        internal bool Subscribe()
        {
            if (_started)
            {
                return false;
            }

            _started = true;
            _collections.ItemsAddedToCollection += OnItemsAdded;
            return true;
        }

        /// <summary>Wakes the worker, e.g. after the setting changed.</summary>
        public void Wake()
        {
            _nextEnsure = DateTime.MinValue;
            _signal.Release();
        }

        public CollectionStatus GetStatus()
        {
            lock (_lock)
            {
                return new CollectionStatus
                {
                    Enabled = _enabled(),
                    FindExists = _ids.ContainsKey(FindName),
                    DeleteExists = _ids.ContainsKey(DeleteName),
                    FindName = FindName,
                    DeleteName = DeleteName
                };
            }
        }

        /// <summary>Finds or creates both collections. True when both exist (or the feature is off).</summary>
        public async Task<bool> EnsureCollectionsAsync()
        {
            if (!_enabled())
            {
                return true;
            }

            foreach (var name in new[] { FindName, DeleteName })
            {
                lock (_lock)
                {
                    if (_ids.ContainsKey(name))
                    {
                        continue;
                    }
                }

                var existing = _library.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { "BoxSet" },
                    Name = name,
                    Recursive = true
                }).OfType<BoxSet>().FirstOrDefault(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase));

                if (existing == null)
                {
                    existing = await _collections.CreateCollection(new CollectionCreationOptions { Name = name, IsLocked = true }).ConfigureAwait(false);
                    _logger.Info("Trailer Editor: created the collection \"{0}\"", name);
                }

                if (existing != null)
                {
                    lock (_lock)
                    {
                        _ids[name] = existing.InternalId;
                    }
                }
            }

            lock (_lock)
            {
                return _ids.ContainsKey(FindName) && _ids.ContainsKey(DeleteName);
            }
        }

        private void OnItemsAdded(object sender, CollectionModifiedEventArgs e)
        {
            try
            {
                if (!_enabled() || e?.Collection == null || e.ItemsChanged == null)
                {
                    return;
                }

                var action = ActionFor(e.Collection.Name);
                if (action == null)
                {
                    return;
                }

                lock (_lock)
                {
                    _ids[e.Collection.Name] = e.Collection.InternalId;
                }

                foreach (var id in e.ItemsChanged)
                {
                    Enqueue(action, e.Collection.InternalId, id);
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Trailer Editor: error while handling an addition to a collection", ex);
            }
        }

        private static string ActionFor(string collectionName)
        {
            if (string.Equals(collectionName, FindName, StringComparison.OrdinalIgnoreCase))
            {
                return ActionKinds.Find;
            }

            return string.Equals(collectionName, DeleteName, StringComparison.OrdinalIgnoreCase) ? ActionKinds.Delete : null;
        }

        private void Enqueue(string action, long collectionId, long itemId, bool fromPoll = false)
        {
            lock (_lock)
            {
                // If a title somehow can't be taken out of the collection, don't repeat its action every poll.
                if (fromPoll && _recent.TryGetValue(action + "|" + itemId, out var done) && DateTime.UtcNow - done < TimeSpan.FromMinutes(2))
                {
                    return;
                }

                if (!_pending.Add(action + "|" + itemId))
                {
                    return;
                }
            }

            _queue.Enqueue(new Work { Action = action, CollectionId = collectionId, ItemId = itemId });
            _signal.Release();
        }

        /// <summary>Looks inside both collections for titles that were added (fallback for missed events). Returns how many were queued.</summary>
        public int PollOnce()
        {
            if (!_enabled())
            {
                return 0;
            }

            KeyValuePair<string, long>[] known;
            lock (_lock)
            {
                known = _ids.ToArray();
            }

            var queued = 0;
            foreach (var pair in known)
            {
                var action = ActionFor(pair.Key);
                foreach (var item in _library.GetItemList(new InternalItemsQuery { CollectionIds = new[] { pair.Value }, Recursive = true }))
                {
                    Enqueue(action, pair.Value, item.InternalId, true);
                    queued++;
                }
            }

            return queued;
        }

        /// <summary>Runs the queued actions one after the other. Returns how many were handled.</summary>
        public async Task<int> ProcessQueueAsync(CancellationToken ct = default(CancellationToken))
        {
            await _processing.WaitAsync(ct).ConfigureAwait(false);
            var handled = 0;
            try
            {
                while (!ct.IsCancellationRequested && _queue.TryDequeue(out var work))
                {
                    try
                    {
                        await HandleAsync(work, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.ErrorException("Trailer Editor: collection action failed", ex);
                    }
                    finally
                    {
                        lock (_lock)
                        {
                            _pending.Remove(work.Action + "|" + work.ItemId);
                            _recent[work.Action + "|" + work.ItemId] = DateTime.UtcNow;
                        }
                    }

                    handled++;
                    if (!_queue.IsEmpty && _gap > TimeSpan.Zero)
                    {
                        await Task.Delay(_gap, ct).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                _processing.Release();
            }

            return handled;
        }

        private async Task HandleAsync(Work work, CancellationToken ct)
        {
            var item = _library.GetItemById(work.ItemId);

            // Take the title out of the collection first, so the app shows it as done and it isn't picked up again.
            if (_library.GetItemById(work.CollectionId) is BoxSet box)
            {
                _collections.RemoveFromCollection(box, new[] { work.ItemId });
            }

            if (item == null)
            {
                return;
            }

            BaseItem target;
            switch (item)
            {
                case Movie _:
                case Series _:
                    target = item;
                    break;
                case Season season:
                    target = season.Series;
                    break;
                case Episode episode:
                    target = episode.Series;
                    break;
                default:
                    target = null;
                    break;
            }

            if (target == null)
            {
                _logger.Info("Trailer Editor: \"{0}\" ({1}) can't be used with trailer actions; only movies and TV shows can. It was removed from the collection.",
                    item.Name, item.GetClientTypeName());
                return;
            }

            await _actions.RunAsync(work.Action, target, ActionSources.Collection, ct).ConfigureAwait(false);
        }

        private async Task LoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (_enabled())
                    {
                        await EnsureWithBackoffAsync().ConfigureAwait(false);
                        PollOnce();
                        await ProcessQueueAsync(ct).ConfigureAwait(false);
                    }

                    await _signal.WaitAsync(_pollEvery, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.ErrorException("Trailer Editor: collection actions error", ex);
                    try
                    {
                        await Task.Delay(_pollEvery, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            }
        }

        private async Task EnsureWithBackoffAsync()
        {
            bool complete;
            lock (_lock)
            {
                complete = _ids.ContainsKey(FindName) && _ids.ContainsKey(DeleteName);
            }

            if (complete || DateTime.UtcNow < _nextEnsure)
            {
                return;
            }

            try
            {
                if (await EnsureCollectionsAsync().ConfigureAwait(false))
                {
                    _failures = 0;
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Trailer Editor: couldn't create the trailer collections", ex);
            }

            // Try often at first (the library may still be starting), then every 10 minutes.
            _failures++;
            _nextEnsure = DateTime.UtcNow + (_failures < 10 ? TimeSpan.FromSeconds(30) : TimeSpan.FromMinutes(10));
        }

        public void Dispose()
        {
            _cts.Cancel();
            if (_started)
            {
                _collections.ItemsAddedToCollection -= OnItemsAdded;
            }
        }
    }
}
