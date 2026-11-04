using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Session;
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
        private readonly ISessionManager _sessionManager;
        private readonly ConcurrentDictionary<Guid, byte> _inProgress = new ConcurrentDictionary<Guid, byte>();

        public EntryPoint(ILibraryManager libraryManager, ILogManager logManager, IApplicationPaths applicationPaths,
            IJsonSerializer json, ISessionManager sessionManager)
        {
            _libraryManager = libraryManager;
            _sessionManager = sessionManager;
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
            _sessionManager.PlaybackStart += OnPlaybackStart;
        }

        /// <summary>
        /// When Emby reports a YouTube trailer playing, that link works, so it doesn't need a
        /// playback test. Failed trailers never reach the server: they fail in the browser.
        /// </summary>
        private void OnPlaybackStart(object sender, PlaybackProgressEventArgs e)
        {
            try
            {
                if (!Plugin.Instance.Configuration.ScanLearnFromPlayback || e == null)
                {
                    return;
                }

                var texts = new[] { e.Item?.Path, e.MediaInfo?.Path, e.MediaSourceId, e.Item?.Name, e.MediaInfo?.Name };
                var videoId = texts.Select(YouTubeChecker.FindVideoId).FirstOrDefault(id => id != null);

                var itemType = e.Item?.GetClientTypeName() ?? e.MediaInfo?.Type;
                var looksLikeTrailer = videoId != null
                    || string.Equals(itemType, "Trailer", StringComparison.OrdinalIgnoreCase)
                    || texts.Any(t => t != null && t.IndexOf("youtu", StringComparison.OrdinalIgnoreCase) >= 0);
                if (!looksLikeTrailer)
                {
                    return;
                }

                // Helps confirm what Emby reports for trailer plays.
                _logger.Info("Trailer Editor: trailer playback reported by {0}: type={1}, name={2}, path={3}, mediaSource={4}, youTubeId={5}",
                    e.ClientName, itemType, e.Item?.Name ?? e.MediaInfo?.Name, e.Item?.Path ?? e.MediaInfo?.Path, e.MediaSourceId, videoId ?? "none");

                if (videoId != null)
                {
                    Store.RecordPlayback(videoId, YouTubeChecker.Ok, "Played in Emby.", "emby");
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Trailer Editor: error while handling a playback event", ex);
            }
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
            _sessionManager.PlaybackStart -= OnPlaybackStart;
        }
    }
}
