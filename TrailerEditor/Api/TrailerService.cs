using System;
using System.IO;
using System.Linq;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Serialization;
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

    public class SetTrailersBody
    {
        public string[] Urls { get; set; }
        public bool Pin { get; set; } = true;
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
    public class SetItemTrailers : IRequiresRequestStream, IReturn<TrailerItemInfo>
    {
        [ApiMember(Name = "Id", IsRequired = true, DataType = "string", ParameterType = "path")]
        public string Id { get; set; }

        public Stream RequestStream { get; set; }
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
        private readonly IJsonSerializer _json;

        public TrailerService(ILibraryManager libraryManager, IJsonSerializer json)
        {
            _libraryManager = libraryManager;
            _json = json;
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

            SetTrailersBody body;
            try
            {
                body = _json.DeserializeFromStream<SetTrailersBody>(request.RequestStream);
            }
            catch (Exception ex)
            {
                throw new ArgumentException("Request body must be JSON like { \"Urls\": [\"https://...\"], \"Pin\": true }", ex);
            }

            if (body == null)
            {
                throw new ArgumentException("Request body is missing.");
            }

            // Throws ArgumentException (HTTP 400) for invalid links.
            Manager.SetTrailers(item, body.Urls ?? new string[0], body.Pin);
            return ToInfo(item);
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
