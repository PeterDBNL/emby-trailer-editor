using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using TrailerEditor.Scanning;

namespace TrailerEditor
{
    /// <summary>
    /// Starts with the server. Watches for item updates so pinned trailer links
    /// survive metadata refreshes, and exposes the shared TrailerManager.
    /// </summary>
    public class EntryPoint : IServerEntryPoint
    {
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger _logger;
        private readonly ConcurrentDictionary<Guid, byte> _inProgress = new ConcurrentDictionary<Guid, byte>();

        public EntryPoint(ILibraryManager libraryManager, ILogManager logManager, IApplicationPaths applicationPaths, IJsonSerializer json)
        {
            _libraryManager = libraryManager;
            _logger = logManager.GetLogger("TrailerEditor");
            Store = new ScanStore(applicationPaths, json, _logger);
            Manager = new TrailerManager(libraryManager, _logger);
        }

        /// <summary>Results of the last library scan.</summary>
        public static ScanStore Store { get; private set; }

        public static TrailerManager Manager { get; private set; }

        public static YouTubeChecker Checker { get; internal set; } = new YouTubeChecker();

        public void Run()
        {
            _libraryManager.ItemUpdated += OnItemUpdated;
        }

        private void OnItemUpdated(object sender, ItemChangeEventArgs e)
        {
            var item = e.Item;

            // Our own edits don't need checking, and this also stops update loops.
            if (item == null || e.UpdateReason == ItemUpdateType.MetadataEdit || !TrailerManager.IsSupported(item))
            {
                return;
            }

            if (!_inProgress.TryAdd(item.Id, 0))
            {
                return;
            }

            // Run off the event thread so we don't hold up the refresh that raised it.
            Task.Run(() =>
            {
                try
                {
                    Manager.ReapplyIfChanged(item);
                }
                catch (Exception ex)
                {
                    _logger.ErrorException("Trailer Editor: failed to restore pinned trailers for {0}", ex, item.Name);
                }
                finally
                {
                    _inProgress.TryRemove(item.Id, out _);
                }
            });
        }

        public void Dispose()
        {
            _libraryManager.ItemUpdated -= OnItemUpdated;
        }
    }
}
