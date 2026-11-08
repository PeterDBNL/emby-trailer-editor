using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Tasks;
using TrailerEditor.Actions;
using TrailerEditor.Scanning;
using TrailerEditor.Search;

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
        private readonly ITaskManager _taskManager;

        private static EntryPoint Instance { get; set; }
        private readonly ConcurrentDictionary<Guid, byte> _inProgress = new ConcurrentDictionary<Guid, byte>();

        public EntryPoint(ILibraryManager libraryManager, ILogManager logManager, IApplicationPaths applicationPaths,
            IJsonSerializer json, ISessionManager sessionManager, ITaskManager taskManager,
            ICollectionManager collectionManager, IServerConfigurationManager serverConfiguration)
        {
            _taskManager = taskManager;
            Instance = this;
            _libraryManager = libraryManager;
            _sessionManager = sessionManager;
            _logger = logManager.GetLogger("TrailerEditor");
            Store = new ScanStore(applicationPaths, json, _logger);
            Rejected = new RejectedStore(applicationPaths, json, _logger);
            Manager = new TrailerManager(libraryManager, _logger, Rejected);
            ActionLog = new ActionLog(applicationPaths, json, _logger);
            Finder = new TrailerFinder(new HttpFetcher(), () => Checker, Rejected, id => Store.IsKnownBroken(id), _logger);
            Actions = new TrailerActions(Manager, Finder, ActionLog, () => Plugin.Instance.Configuration,
                () => serverConfiguration.Configuration.PreferredMetadataLanguage, id => Store.IsKnownBroken(id), _logger);
            Collections = new CollectionActions(collectionManager, libraryManager, Actions,
                () => Plugin.Instance.Configuration.CollectionActionsEnabled, _logger);
        }

        /// <summary>Results of the last library scan.</summary>
        public static ScanStore Store { get; private set; }

        public static TrailerManager Manager { get; private set; }

        /// <summary>Trailer links that were deleted or removed, per item.</summary>
        public static RejectedStore Rejected { get; private set; }

        /// <summary>Recent find / delete actions.</summary>
        public static ActionLog ActionLog { get; private set; }

        public static TrailerFinder Finder { get; private set; }

        public static TrailerActions Actions { get; private set; }

        /// <summary>The "Find trailer" / "Delete trailer" collections.</summary>
        public static CollectionActions Collections { get; private set; }

        /// <summary>Called when the settings were saved.</summary>
        public static void ApplySettings()
        {
            ApplyScanSchedule();
            Collections?.Wake();
        }

        public static YouTubeChecker Checker { get; internal set; } = new YouTubeChecker();

        public void Run()
        {
            _libraryManager.ItemUpdated += OnItemUpdated;
            _sessionManager.PlaybackStart += OnPlaybackStart;
            UpgradeSettings();

            // Give the server a moment to finish starting before creating collections.
            Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
                Collections.Start();
            });
        }

        /// <summary>Updates settings saved by older versions of the plugin.</summary>
        private void UpgradeSettings()
        {
            var config = Plugin.Instance.Configuration;
            if (config.ConfigVersion >= 2)
            {
                return;
            }

            // 1.6: these checks are on by default, and the scan no longer runs weekly
            // unless chosen in the settings (older versions added a weekly schedule).
            config.ScanCheckAgeRestriction = true;
            config.ScanBrowserPlaybackTest = true;
            config.ConfigVersion = 2;
            Plugin.Instance.SaveConfiguration();

            // Scheduled tasks may not be registered yet when the server starts.
            Task.Run(async () =>
            {
                for (var attempt = 0; attempt < 10 && !ApplyScanSchedule(); attempt++)
                {
                    await Task.Delay(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                }
            });
        }

        /// <summary>Sets the scan task's schedule from the plugin settings. False if the task isn't registered yet.</summary>
        public static bool ApplyScanSchedule()
        {
            var self = Instance;
            if (self == null)
            {
                return false;
            }

            try
            {
                var worker = self._taskManager.ScheduledTasks.FirstOrDefault(t => t.ScheduledTask is TrailerScanTask);
                if (worker == null)
                {
                    return false;
                }

                worker.Triggers = TrailerScanTask.TriggersFor(Plugin.Instance.Configuration);
                self._logger.Info("Trailer Editor: automatic scan set to {0}",
                    TrailerScanTask.Describe(Plugin.Instance.Configuration) ?? "off");
                return true;
            }
            catch (Exception ex)
            {
                self._logger.ErrorException("Trailer Editor: couldn't set the scan schedule", ex);
                return true;
            }
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
            Collections?.Dispose();
        }
    }
}
