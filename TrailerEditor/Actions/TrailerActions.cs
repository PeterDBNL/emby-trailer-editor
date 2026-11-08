using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using TrailerEditor.Configuration;
using TrailerEditor.Search;

namespace TrailerEditor.Actions
{
    public class DeleteResult
    {
        public bool Ok { get; set; }
        public int Removed { get; set; }
        public string Message { get; set; }
        public string[] TrailerUrls { get; set; }
    }

    /// <summary>What an item action does; collection actions call this for every item.</summary>
    public interface IItemActions
    {
        Task RunAsync(string action, BaseItem item, string source, CancellationToken ct);
    }

    /// <summary>The two actions: find a trailer, and delete the trailers. Both are written to the action log.</summary>
    public class TrailerActions : IItemActions
    {
        public const int MaxLinks = 3;

        private readonly TrailerManager _manager;
        private readonly TrailerFinder _finder;
        private readonly ActionLog _log;
        private readonly Func<PluginConfiguration> _config;
        private readonly Func<string> _language;
        private readonly Func<string, bool> _isKnownBroken;
        private readonly ILogger _logger;

        public TrailerActions(TrailerManager manager, TrailerFinder finder, ActionLog log, Func<PluginConfiguration> config,
            Func<string> language, Func<string, bool> isKnownBroken, ILogger logger)
        {
            _manager = manager;
            _finder = finder;
            _log = log;
            _config = config;
            _language = language;
            _isKnownBroken = isKnownBroken ?? (_ => false);
            _logger = logger;
        }

        public async Task RunAsync(string action, BaseItem item, string source, CancellationToken ct)
        {
            if (action == ActionKinds.Delete)
            {
                Delete(item, source);
            }
            else
            {
                await FindAsync(item, true, source, ct).ConfigureAwait(false);
            }
        }

        public FindRequest BuildRequest(BaseItem item) => new FindRequest
        {
            ItemKey = TrailerManager.Key(item),
            Name = item.Name,
            OriginalTitle = item.OriginalTitle,
            Year = item.ProductionYear,
            IsSeries = string.Equals(item.GetClientTypeName(), "Series", StringComparison.OrdinalIgnoreCase),
            TmdbId = item.GetProviderId(MetadataProviders.Tmdb),
            ImdbId = item.GetProviderId(MetadataProviders.Imdb),
            Language = string.IsNullOrWhiteSpace(_language()) ? "en" : _language().Trim(),
            TmdbApiKey = (_config().TmdbApiKey ?? string.Empty).Trim()
        };

        /// <summary>Searches for a trailer; with apply, puts the best one first on the item (pinned). Always logged when applied.</summary>
        public async Task<FindResult> FindAsync(BaseItem item, bool apply, string source, CancellationToken ct)
        {
            FindResult result;
            try
            {
                result = await _finder.FindAsync(BuildRequest(item), ct).ConfigureAwait(false);
                if (apply && result.Found)
                {
                    Apply(item, result);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Trailer Editor: trailer search failed for {0}", ex, item.Name);
                result = new FindResult { Message = "The trailer search failed: " + ex.Message };
                if (apply)
                {
                    Log(item, ActionKinds.Find, source, ActionOutcomes.Failed, result.Message, null);
                }

                result.TrailerUrls = item.RemoteTrailers ?? new string[0];
                return result;
            }

            result.TrailerUrls = item.RemoteTrailers ?? new string[0];
            if (apply)
            {
                Log(item, ActionKinds.Find, source, result.Found ? ActionOutcomes.Ok : ActionOutcomes.NoMatch, result.Message, result.Chosen?.Url);
            }

            return result;
        }

        /// <summary>Chosen link first, then the item's existing links that still look fine (at most <see cref="MaxLinks"/> in total).</summary>
        private void Apply(BaseItem item, FindResult result)
        {
            var key = TrailerManager.Key(item);
            var chosen = result.Chosen;
            var keep = new List<string>();
            var broken = new List<string>();
            foreach (var url in item.RemoteTrailers ?? new string[0])
            {
                var id = TrailerUrls.YouTubeIdOf(url);
                if (id == chosen.VideoId)
                {
                    continue;
                }

                if (id != null && (_manager.Rejected?.IsRejected(key, id) == true))
                {
                    continue;
                }

                if (id != null && _isKnownBroken(id))
                {
                    broken.Add(url);
                    continue;
                }

                keep.Add(url);
            }

            _manager.Rejected?.Reject(key, item.Name, broken, RejectedStore.ReasonBroken);
            var links = new[] { chosen.Url }.Concat(keep).Take(MaxLinks);
            _manager.SetTrailers(item, links, true);
            result.Applied = true;
        }

        public DeleteResult Delete(BaseItem item, string source)
        {
            try
            {
                var removed = _manager.DeleteTrailers(item);
                var message = removed == 0
                    ? "There were no trailer links to delete."
                    : "Deleted " + removed + " trailer link" + (removed == 1 ? string.Empty : "s") + ". They won't be picked again for this title.";
                Log(item, ActionKinds.Delete, source, ActionOutcomes.Ok, message, null);
                return new DeleteResult { Ok = true, Removed = removed, Message = message, TrailerUrls = item.RemoteTrailers ?? new string[0] };
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Trailer Editor: couldn't delete the trailers of {0}", ex, item.Name);
                var message = "Deleting the trailers failed: " + ex.Message;
                Log(item, ActionKinds.Delete, source, ActionOutcomes.Failed, message, null);
                return new DeleteResult { Ok = false, Message = message, TrailerUrls = item.RemoteTrailers ?? new string[0] };
            }
        }

        private void Log(BaseItem item, string action, string source, string outcome, string message, string chosenUrl)
        {
            _log.Add(new ActionRecord
            {
                TimeUtc = DateTime.UtcNow,
                ItemId = item.InternalId.ToString(),
                ItemName = item.Name,
                Year = item.ProductionYear,
                Type = item.GetClientTypeName(),
                Action = action,
                Source = source,
                Outcome = outcome,
                Message = message,
                ChosenUrl = chosenUrl
            });
        }
    }
}
