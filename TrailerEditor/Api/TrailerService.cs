using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;

namespace TrailerEditor.Api
{
    public class TrailerItemInfo
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public int? Year { get; set; }
        public string Type { get; set; }
        public string[] TrailerUrls { get; set; }
        public bool IsPinned { get; set; }
    }

    [Route("/TrailerEditor/Search", "GET", Summary = "Finds movies and TV shows by name")]
    public class SearchItems : IReturn<TrailerItemInfo[]>
    {
        [ApiMember(Name = "SearchTerm", IsRequired = true, DataType = "string", ParameterType = "query")]
        public string SearchTerm { get; set; }

        [ApiMember(Name = "Limit", DataType = "int", ParameterType = "query")]
        public int? Limit { get; set; }
    }

    [Route("/TrailerEditor/Pinned", "GET", Summary = "Lists items whose trailer links are pinned")]
    public class GetPinned : IReturn<TrailerItemInfo[]>
    {
    }

    [Route("/TrailerEditor/Items/{Id}", "GET", Summary = "Gets the trailer links of an item")]
    public class GetItemTrailers : IReturn<TrailerItemInfo>
    {
        [ApiMember(Name = "Id", IsRequired = true, DataType = "string", ParameterType = "path")]
        public string Id { get; set; }
    }

    [Route("/TrailerEditor/Items/{Id}", "POST", Summary = "Replaces the trailer links of an item. Body: { Urls: [..], Pin: true }")]
    public class SetItemTrailers : IReturn<TrailerItemInfo>
    {
        [ApiMember(Name = "Id", IsRequired = true, DataType = "string", ParameterType = "path")]
        public string Id { get; set; }

        [ApiMember(Name = "Urls", Description = "Trailer links, separated by spaces or new lines.", DataType = "string", ParameterType = "query")]
        public string Urls { get; set; }

        [ApiMember(Name = "Pin", Description = "Pin the links so refreshes can't replace them (default true).", DataType = "bool", ParameterType = "query")]
        public bool? Pin { get; set; }

        [ApiMember(Name = "Clear", Description = "Set to true (with no Urls) to remove all trailer links.", DataType = "bool", ParameterType = "query")]
        public bool? Clear { get; set; }
    }

    [Route("/TrailerEditor/Check", "GET", Summary = "Checks whether a trailer link can play in Emby")]
    public class CheckLink : IReturn<LinkCheckResult>
    {
        [ApiMember(Name = "Url", IsRequired = true, DataType = "string", ParameterType = "query")]
        public string Url { get; set; }
    }

    [Route("/TrailerEditor/Items/{Id}/Pin", "DELETE", Summary = "Stops pinning the trailer links of an item (links stay as they are)")]
    public class UnpinItem : IReturn<TrailerItemInfo>
    {
        [ApiMember(Name = "Id", IsRequired = true, DataType = "string", ParameterType = "path")]
        public string Id { get; set; }
    }

    [Authenticated(Roles = "Admin")]
    public class TrailerService : IService
    {
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger _logger;

        public TrailerService(ILibraryManager libraryManager, ILogManager logManager)
        {
            _libraryManager = libraryManager;
            _logger = logManager.GetLogger("TrailerEditor");
        }

        private static TrailerManager Manager => EntryPoint.Manager;

        public object Get(SearchItems request)
        {
            var term = (request.SearchTerm ?? string.Empty).Trim();
            if (term.Length == 0)
            {
                return new TrailerItemInfo[0];
            }

            var items = _libraryManager.GetItemList(new InternalItemsQuery
            {
                SearchTerm = term,
                IncludeItemTypes = TrailerManager.SupportedTypes,
                Recursive = true,
                IsVirtualItem = false,
                Limit = Math.Max(1, Math.Min(request.Limit ?? 30, 100))
            });

            return items.Select(ToInfo).ToArray();
        }

        public object Get(GetPinned request)
        {
            return Manager.GetPinnedItems().Select(ToInfo).OrderBy(i => i.Name).ToArray();
        }

        public object Get(GetItemTrailers request)
        {
            return ToInfo(RequireItem(request.Id));
        }

        public object Post(SetItemTrailers request)
        {
            var item = RequireItem(request.Id);
            var urls = SplitLines(request.Urls);

            _logger.Info("Trailer Editor: save request for {0} ({1}): {2} link(s), Pin={3}, Clear={4}",
                item.Name, item.InternalId, urls.Length, request.Pin, request.Clear);

            // Never wipe an item's trailers just because the links didn't arrive.
            if (urls.Length == 0 && request.Clear != true)
            {
                throw new ArgumentException("No trailer links were received, so nothing was changed. " +
                    "To remove all trailers, send Clear=true.");
            }

            // Throws ArgumentException (HTTP 400) for invalid links.
            Manager.SetTrailers(item, urls, request.Pin ?? true);
            return ToInfo(item);
        }

        private static string[] SplitLines(string value) =>
            (value ?? string.Empty).Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

        public async Task<object> Get(CheckLink request)
        {
            return await EntryPoint.Checker.CheckAsync(request.Url, CancellationToken.None).ConfigureAwait(false);
        }

        public object Delete(UnpinItem request)
        {
            var item = RequireItem(request.Id);
            Manager.Unpin(item);
            return ToInfo(item);
        }

        private BaseItem RequireItem(string id)
        {
            var item = Manager.FindItem(id);
            if (item == null)
            {
                throw new ResourceNotFoundException("No movie or TV show found with id " + id);
            }

            return item;
        }

        private TrailerItemInfo ToInfo(BaseItem item)
        {
            return new TrailerItemInfo
            {
                Id = item.InternalId.ToString(),
                Name = item.Name,
                Year = item.ProductionYear,
                Type = item.GetClientTypeName(),
                TrailerUrls = item.RemoteTrailers ?? new string[0],
                IsPinned = Manager.IsPinned(item)
            };
        }
    }
}
