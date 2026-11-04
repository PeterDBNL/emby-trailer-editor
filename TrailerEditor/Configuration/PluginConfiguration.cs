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
