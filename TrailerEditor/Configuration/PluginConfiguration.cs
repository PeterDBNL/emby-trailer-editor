using MediaBrowser.Model.Plugins;

namespace TrailerEditor.Configuration
{
    public class PluginConfiguration : BasePluginConfiguration
    {
        /// <summary>
        /// When true, trailers edited through this plugin are re-applied if a
        /// metadata refresh replaces them.
        /// </summary>
        public bool PinEditedTrailers { get; set; } = true;

        public TrailerOverride[] Overrides { get; set; } = new TrailerOverride[0];

        // ---- Library scan ----

        /// <summary>Report movies and TV shows that have no online trailer links.</summary>
        public bool ScanReportNoTrailers { get; set; } = true;

        /// <summary>Don't report "no trailers" for items that have local trailer files.</summary>
        public bool ScanIgnoreItemsWithLocalTrailers { get; set; } = true;

        /// <summary>Ask YouTube whether each trailer link still works.</summary>
        public bool ScanCheckLinks { get; set; } = true;

        /// <summary>Also detect age-restricted videos (one extra request per link).</summary>
        public bool ScanCheckAgeRestriction { get; set; } = true;

        /// <summary>
        /// Let the Trailer Library page play each scanned link in a hidden, muted player to catch
        /// copyright blocks. Only runs while that page is open in a browser.
        /// </summary>
        public bool ScanBrowserPlaybackTest { get; set; } = true;

        /// <summary>Mark a YouTube trailer as working when Emby reports it playing.</summary>
        public bool ScanLearnFromPlayback { get; set; } = true;

        /// <summary>Automatic scan: "Off", "Daily" or "Weekly".</summary>
        public string ScanSchedule { get; set; } = "Off";

        /// <summary>Day of the week for weekly scans (0 = Sunday).</summary>
        public int ScanScheduleDay { get; set; } = 0;

        /// <summary>Hour of the day (0-23) for automatic scans.</summary>
        public int ScanScheduleHour { get; set; } = 3;

        /// <summary>Used to update settings saved by older versions. Missing in old files, so it reads as 0.</summary>
        public int ConfigVersion { get; set; }

        /// <summary>Pause between YouTube requests, to avoid being rate limited.</summary>
        public int ScanRequestDelayMs { get; set; } = 1000;

        /// <summary>Re-use a link's previous result for this many days before checking it again.</summary>
        public int ScanRecheckDays { get; set; } = 30;
    }

    public class TrailerOverride
    {
        /// <summary>Item Guid ("N" format).</summary>
        public string ItemId { get; set; }

        /// <summary>Display name, kept only to make the config file readable.</summary>
        public string ItemName { get; set; }

        public string[] Urls { get; set; } = new string[0];
    }
}
