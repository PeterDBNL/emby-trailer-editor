using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;
using TrailerEditor.Actions;

namespace TrailerEditor.Search
{
    /// <summary>
    /// Looks for the best trailer of a movie or TV show: candidates from TMDb (optional, needs an API key)
    /// and from YouTube's search page, minus rejected and known-broken videos, scored by
    /// <see cref="TrailerScorer"/> and then checked with YouTube in score order.
    /// Copyright blocks can't be detected on the server, so a pick may still be blocked in some countries.
    /// </summary>
    public class TrailerFinder
    {
        public const int MaxChecks = 8;
        public const int MaxCandidatesShown = 5;

        private readonly IHttpFetcher _http;
        private readonly Func<YouTubeChecker> _checker;
        private readonly RejectedStore _rejected;
        private readonly Func<string, bool> _isKnownBroken;
        private readonly ILogger _logger;

        public TrailerFinder(IHttpFetcher http, Func<YouTubeChecker> checker, RejectedStore rejected, Func<string, bool> isKnownBroken, ILogger logger)
        {
            _http = http;
            _checker = checker;
            _rejected = rejected;
            _isKnownBroken = isKnownBroken ?? (_ => false);
            _logger = logger;
        }

        public async Task<FindResult> FindAsync(FindRequest request, CancellationToken ct)
        {
            var notes = new List<string>();
            var byId = new Dictionary<string, Candidate>();

            if (!string.IsNullOrWhiteSpace(request.TmdbApiKey))
            {
                try
                {
                    foreach (var c in await TmdbClient.GetCandidatesAsync(_http, request, ct).ConfigureAwait(false))
                    {
                        Merge(byId, c);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.Warn("Trailer Editor: TMDb lookup failed for {0}: {1}", request.Name, ex.Message);
                    notes.Add("TMDb couldn't be reached or the API key was refused.");
                }
            }

            var searched = 0;
            foreach (var query in Queries(request))
            {
                try
                {
                    var html = await _http.GetStringAsync(YouTubeSearch.SearchUrl(query), ct).ConfigureAwait(false);
                    var found = YouTubeSearch.ParseResults(html);
                    searched += found.Count;
                    foreach (var c in found)
                    {
                        Merge(byId, c);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.Warn("Trailer Editor: YouTube search failed for {0}: {1}", request.Name, ex.Message);
                    notes.Add("YouTube's search page couldn't be read.");
                }
            }

            if (searched == 0 && !byId.Values.Any(c => c.InSearch))
            {
                notes.Add("YouTube's search page had no results (or its layout has changed).");
            }

            var target = new ScoreTarget
            {
                Name = request.Name,
                OriginalTitle = request.OriginalTitle,
                Year = request.Year,
                IsSeries = request.IsSeries,
                Language = string.IsNullOrWhiteSpace(request.Language) ? "en" : request.Language
            };

            var ranked = new List<Candidate>();
            foreach (var c in byId.Values)
            {
                if (_rejected != null && _rejected.IsRejected(request.ItemKey, c.VideoId))
                {
                    continue;
                }

                if (_isKnownBroken(c.VideoId))
                {
                    continue;
                }

                TrailerScorer.Score(target, c);
                if (TrailerScorer.IsMatch(c))
                {
                    ranked.Add(c);
                }
            }

            ranked = ranked.OrderByDescending(c => c.Score).ThenByDescending(c => c.Published ?? DateTime.MinValue).ToList();

            var result = new FindResult();
            if (ranked.Count == 0)
            {
                result.Message = "No good trailer match was found." + (notes.Count > 0 ? " " + string.Join(" ", notes) : string.Empty);
                return result;
            }

            // Check in score order; the first that plays wins. An unclear answer is a fallback.
            Candidate chosen = null;
            Candidate fallback = null;
            var broken = new HashSet<string>();
            var checker = _checker();
            foreach (var c in ranked.Take(MaxChecks))
            {
                var check = await checker.CheckAsync(c.Url, true, ct).ConfigureAwait(false);
                if (string.IsNullOrEmpty(c.Title) || (!c.InSearch && check.Title != null))
                {
                    c.Title = check.Title ?? c.Title;
                }

                c.Channel = c.Channel ?? check.Channel;

                if (check.Status == YouTubeChecker.Error)
                {
                    broken.Add(c.VideoId);
                    continue;
                }

                c.Status = check.Status;
                c.StatusMessage = check.Message;
                if (check.Status == YouTubeChecker.Ok)
                {
                    chosen = c;
                    break;
                }

                fallback = fallback ?? c;
                if (check.RateLimited)
                {
                    break;
                }
            }

            chosen = chosen ?? fallback;
            var remaining = ranked.Where(c => !broken.Contains(c.VideoId) && c != chosen).ToList();
            if (chosen != null)
            {
                remaining.Insert(0, chosen);
            }

            result.Candidates = remaining.Take(MaxCandidatesShown).ToList();
            if (chosen == null)
            {
                result.Message = broken.Count > 0
                    ? "Every good match was unavailable or can't be played in Emby."
                    : "No good trailer match was found.";
                return result;
            }

            result.Found = true;
            result.Chosen = chosen;
            result.Message = "Best match: \"" + chosen.Title + "\"" + (string.IsNullOrEmpty(chosen.Channel) ? string.Empty : " by " + chosen.Channel) +
                " (score " + chosen.Score.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + ", " + (chosen.InTmdb ? "TMDb" : "YouTube search") + ").";
            return result;
        }

        private static IEnumerable<string> Queries(FindRequest request)
        {
            var names = new[] { request.Name, request.OriginalTitle }
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var name in names)
            {
                yield return (name + " " + (request.Year?.ToString() ?? string.Empty) + " trailer").Replace("  ", " ");
            }
        }

        /// <summary>Adds a candidate, or combines it with the same video already found by another source.</summary>
        private static void Merge(Dictionary<string, Candidate> byId, Candidate c)
        {
            if (!byId.TryGetValue(c.VideoId, out var existing))
            {
                byId[c.VideoId] = c;
                return;
            }

            var search = c.InSearch ? c : existing.InSearch ? existing : null;
            var tmdb = c.InTmdb ? c : existing.InTmdb ? existing : null;
            if (search != null && tmdb != null && search != tmdb)
            {
                search.InTmdb = true;
                search.Official = tmdb.Official;
                search.Type = tmdb.Type;
                search.Language = tmdb.Language;
                search.Size = tmdb.Size;
                search.Published = tmdb.Published;
                search.Source = "tmdb";
                byId[c.VideoId] = search;
            }
        }
    }
}
