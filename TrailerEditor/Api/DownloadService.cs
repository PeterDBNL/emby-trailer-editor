using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Services;
using TrailerEditor.Downloads;

namespace TrailerEditor.Api
{
    [Route("/TrailerEditor/Downloads/Status", "GET", Summary = "Shows whether yt-dlp (and ffmpeg) are available for trailer downloads")]
    public class GetDownloadsStatus : IReturn<DownloadsStatus>
    {
    }

    [Route("/TrailerEditor/Downloads/InstallYtDlp", "POST", Summary = "Downloads or updates the yt-dlp copy managed by this plugin")]
    public class InstallYtDlp : IReturn<DownloadsStatus>
    {
    }

    [Route("/TrailerEditor/Items/{Id}/Download", "POST", Summary = "Downloads a YouTube trailer and saves it with the media")]
    public class StartItemDownload : IReturn<DownloadJob>
    {
        [ApiMember(Name = "Id", IsRequired = true, DataType = "string", ParameterType = "path")]
        public string Id { get; set; }

        [ApiMember(Name = "Url", IsRequired = true, DataType = "string", ParameterType = "query")]
        public string Url { get; set; }

        [ApiMember(Name = "RemoveLink", Description = "Remove the link from the item after downloading (default false).", DataType = "bool", ParameterType = "query")]
        public bool? RemoveLink { get; set; }
    }

    [Route("/TrailerEditor/Items/{Id}/Download", "GET", Summary = "Gets the current or last trailer download of an item")]
    public class GetItemDownload : IReturn<DownloadJob>
    {
        [ApiMember(Name = "Id", IsRequired = true, DataType = "string", ParameterType = "path")]
        public string Id { get; set; }
    }

    [Authenticated(Roles = "Admin")]
    public class DownloadService : IService
    {
        private static TrailerDownloader Downloader => EntryPoint.Downloader;

        public Task<DownloadsStatus> Get(GetDownloadsStatus request) => Downloader.GetStatusAsync();

        public async Task<object> Post(InstallYtDlp request)
        {
            try
            {
                return await Downloader.InstallAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                throw new ArgumentException(ex.Message);
            }
            catch (Exception ex)
            {
                throw new ArgumentException("Couldn't download yt-dlp: " + ex.Message);
            }
        }

        public object Post(StartItemDownload request)
        {
            var item = RequireItem(request.Id);
            return Downloader.Start(item, request.Url, request.RemoveLink == true);
        }

        public object Get(GetItemDownload request)
        {
            var item = RequireItem(request.Id);
            return Downloader.GetJob(item.InternalId);
        }

        private static MediaBrowser.Controller.Entities.BaseItem RequireItem(string id)
        {
            var item = EntryPoint.Manager.FindItem(id);
            if (item == null)
            {
                throw new ResourceNotFoundException("No movie or TV show found with id " + id);
            }

            return item;
        }
    }
}
