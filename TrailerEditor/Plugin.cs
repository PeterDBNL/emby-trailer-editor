using System;
using System.Collections.Generic;
using System.IO;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using TrailerEditor.Configuration;

namespace TrailerEditor
{
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages, IHasThumbImage
    {
        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
        }

        public static Plugin Instance { get; private set; }

        public override string Name => "Trailer Editor";

        public override string Description =>
            "Edit the trailer links (e.g. YouTube URLs) stored in the metadata of movies and TV shows.";

        public override Guid Id => new Guid("6f1d7a7e-3c1b-4f4e-9b8a-2d5c0e7a91b4");

        public override void UpdateConfiguration(BasePluginConfiguration configuration)
        {
            base.UpdateConfiguration(configuration);

            // Settings were saved from the Trailer Library page: apply the scan schedule.
            EntryPoint.ApplyScanSchedule();
        }

        public ImageFormat ThumbImageFormat => ImageFormat.Jpg;

        public Stream GetThumbImage() =>
            GetType().Assembly.GetManifestResourceStream(GetType().Namespace + ".thumb.jpg");

        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = "trailereditor",
                    DisplayName = "Trailer Editor",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html",
                    EnableInMainMenu = true,
                    MenuIcon = "movie",
                    IsMainConfigPage = true
                },
                new PluginPageInfo
                {
                    Name = "trailereditorjs",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.js"
                },
                new PluginPageInfo
                {
                    Name = "trailereditorlibrary",
                    DisplayName = "Trailer Library",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.libraryPage.html",
                    EnableInMainMenu = true,
                    MenuIcon = "video_library"
                },
                new PluginPageInfo
                {
                    Name = "trailereditorlibraryjs",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.libraryPage.js"
                }
            };
        }
    }
}
