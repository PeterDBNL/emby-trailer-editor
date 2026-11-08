using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace TrailerEditor.Search
{
    /// <summary>Reads the official trailer list from The Movie Database (needs a free API key).</summary>
    public static class TmdbClient
    {
        private const string Base = "https://api.themoviedb.org/3/";

        /// <summary>YouTube trailers and teasers of a title, or an empty list. Throws on network errors.</summary>
        public static async Task<List<Candidate>> GetCandidatesAsync(IHttpFetcher http, FindRequest request, CancellationToken ct)
        {
            var kind = request.IsSeries ? "tv" : "movie";
            var id = request.TmdbId;
            if (string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(request.ImdbId))
            {
                id = ParseFindResult(await http.GetStringAsync(
                    Base + "find/" + Uri.EscapeDataString(request.ImdbId) + "?external_source=imdb_id&api_key=" + Uri.EscapeDataString(request.TmdbApiKey), ct).ConfigureAwait(false), kind);
            }

            if (string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(request.Name))
            {
                id = await SearchAsync(http, request, kind, request.Name, ct).ConfigureAwait(false);
                if (id == null && !string.IsNullOrWhiteSpace(request.OriginalTitle) && request.OriginalTitle != request.Name)
                {
                    id = await SearchAsync(http, request, kind, request.OriginalTitle, ct).ConfigureAwait(false);
                }
            }

            if (string.IsNullOrWhiteSpace(id))
            {
                return new List<Candidate>();
            }

            var lang = string.IsNullOrWhiteSpace(request.Language) ? "en" : request.Language;
            var url = Base + kind + "/" + Uri.EscapeDataString(id) + "/videos?api_key=" + Uri.EscapeDataString(request.TmdbApiKey) +
                "&include_video_language=" + Uri.EscapeDataString(lang + ",en,null");
            return ParseVideos(await http.GetStringAsync(url, ct).ConfigureAwait(false));
        }

        private static async Task<string> SearchAsync(IHttpFetcher http, FindRequest request, string kind, string query, CancellationToken ct)
        {
            var url = Base + "search/" + kind + "?api_key=" + Uri.EscapeDataString(request.TmdbApiKey) + "&query=" + Uri.EscapeDataString(query);
            if (request.Year != null)
            {
                url += (request.IsSeries ? "&first_air_date_year=" : "&year=") + request.Year.Value.ToString(CultureInfo.InvariantCulture);
            }

            return ParseSearchResult(await http.GetStringAsync(url, ct).ConfigureAwait(false));
        }

        /// <summary>Id of the first search result, or null.</summary>
        public static string ParseSearchResult(string json)
        {
            var results = MiniJson.List(MiniJson.TryParse(json), "results");
            if (results == null || results.Count == 0)
            {
                return null;
            }

            var n = MiniJson.Num(results[0], "id");
            return n > 0 ? ((long)n).ToString(CultureInfo.InvariantCulture) : null;
        }

        /// <summary>TMDb id from a /find answer (movie_results or tv_results), or null.</summary>
        public static string ParseFindResult(string json, string kind)
        {
            var results = MiniJson.List(MiniJson.TryParse(json), kind == "tv" ? "tv_results" : "movie_results");
            if (results == null || results.Count == 0)
            {
                return null;
            }

            var n = MiniJson.Num(results[0], "id");
            return n > 0 ? ((long)n).ToString(CultureInfo.InvariantCulture) : null;
        }

        /// <summary>The YouTube trailers and teasers in a /videos answer.</summary>
        public static List<Candidate> ParseVideos(string json)
        {
            var list = new List<Candidate>();
            var results = MiniJson.List(MiniJson.TryParse(json), "results");
            if (results == null)
            {
                return list;
            }

            foreach (var v in results)
            {
                var type = MiniJson.Str(v, "type");
                var key = MiniJson.Str(v, "key");
                if (!string.Equals(MiniJson.Str(v, "site"), "YouTube", StringComparison.OrdinalIgnoreCase)
                    || !(string.Equals(type, "Trailer", StringComparison.OrdinalIgnoreCase) || string.Equals(type, "Teaser", StringComparison.OrdinalIgnoreCase))
                    || key == null || YouTubeChecker.VideoIdOf("https://www.youtube.com/watch?v=" + key) != key)
                {
                    continue;
                }

                DateTime? published = null;
                if (DateTime.TryParse(MiniJson.Str(v, "published_at"), CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var date))
                {
                    published = date;
                }

                list.Add(new Candidate
                {
                    VideoId = key,
                    Url = "https://www.youtube.com/watch?v=" + key,
                    Title = MiniJson.Str(v, "name") ?? string.Empty,
                    Source = "tmdb",
                    InTmdb = true,
                    Official = MiniJson.Bool(v, "official"),
                    Type = type,
                    Language = MiniJson.Str(v, "iso_639_1"),
                    Size = (int)MiniJson.Num(v, "size"),
                    Published = published
                });
            }

            return list;
        }
    }
}
