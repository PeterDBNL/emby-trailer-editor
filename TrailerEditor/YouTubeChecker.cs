using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace TrailerEditor
{
    public class LinkCheckResult
    {
        /// <summary>"ok", "error" or "unknown".</summary>
        public string Status { get; set; }

        public string Url { get; set; }
        public string VideoId { get; set; }
        public string Title { get; set; }
        public string Channel { get; set; }
        public string Message { get; set; }
    }

    /// <summary>
    /// Checks whether a YouTube video can play inside Emby, which uses YouTube's
    /// embedded player. Uses YouTube's oEmbed endpoint (reliable: catches videos with
    /// embedding turned off, and deleted or private ones) plus a best-effort read of
    /// the public watch page (catches age restrictions and other playback blocks).
    /// </summary>
    public class YouTubeChecker
    {
        public const string Ok = "ok";
        public const string Error = "error";
        public const string Unknown = "unknown";

        private static readonly TimeSpan CacheTime = TimeSpan.FromHours(1);
        private static readonly Regex AgeReason = new Regex(@"\bage\b|age-restricted|age restricted|inappropriate", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex VideoIdRegex = new Regex(@"[?&]v=([A-Za-z0-9_-]{11})", RegexOptions.Compiled);

        private readonly HttpClient _http;
        private readonly ConcurrentDictionary<string, Tuple<DateTime, LinkCheckResult>> _cache =
            new ConcurrentDictionary<string, Tuple<DateTime, LinkCheckResult>>();

        public YouTubeChecker()
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                UseCookies = false
            };
            _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
            _http.DefaultRequestHeaders.TryAddWithoutValidation(
                "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
            _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");

            // Skip the EU cookie consent page so we get the real watch page.
            _http.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", "SOCS=CAI; CONSENT=YES+1");
        }

        public async Task<LinkCheckResult> CheckAsync(string input, CancellationToken cancellationToken)
        {
            string url;
            try
            {
                url = TrailerUrls.NormalizeOne((input ?? string.Empty).Trim());
            }
            catch (ArgumentException ex)
            {
                return new LinkCheckResult { Status = Error, Url = input, Message = ex.Message };
            }

            var match = VideoIdRegex.Match(url);
            if (!url.StartsWith("https://www.youtube.com/watch?v=", StringComparison.Ordinal) || !match.Success)
            {
                return new LinkCheckResult { Status = Unknown, Url = url, Message = "Not a YouTube link, so it can't be checked." };
            }

            var id = match.Groups[1].Value;
            if (_cache.TryGetValue(id, out var cached) && DateTime.UtcNow - cached.Item1 < CacheTime)
            {
                return cached.Item2;
            }

            var result = new LinkCheckResult { Url = url, VideoId = id };
            await CheckOEmbedAsync(result, cancellationToken).ConfigureAwait(false);

            if (result.Status != Error)
            {
                await CheckWatchPageAsync(result, cancellationToken).ConfigureAwait(false);
            }

            if (result.Status == null)
            {
                result.Status = Ok;
                result.Message = "No problems found.";
            }

            if (result.Status != Unknown)
            {
                _cache[id] = Tuple.Create(DateTime.UtcNow, result);
            }

            return result;
        }

        private async Task CheckOEmbedAsync(LinkCheckResult result, CancellationToken cancellationToken)
        {
            var oembed = "https://www.youtube.com/oembed?format=json&url=" + Uri.EscapeDataString(result.Url);
            try
            {
                using (var response = await _http.GetAsync(oembed, cancellationToken).ConfigureAwait(false))
                {
                    var code = (int)response.StatusCode;
                    if (code == 401)
                    {
                        Fail(result, "The uploader doesn't allow this video to play on other sites, so Emby can't play it.");
                    }
                    else if (code == 403)
                    {
                        Fail(result, "This video is private, so Emby can't play it.");
                    }
                    else if (code == 400 || code == 404)
                    {
                        Fail(result, "Video not found. It may be deleted or private, or the link is wrong.");
                    }
                    else if (response.IsSuccessStatusCode)
                    {
                        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        result.Title = JsonString(json, "title");
                        result.Channel = JsonString(json, "author_name");
                    }
                    else
                    {
                        SetUnknown(result, "YouTube returned an unexpected response (HTTP " + code + ").");
                    }
                }
            }
            catch (Exception)
            {
                SetUnknown(result, "Couldn't reach YouTube to check this link.");
            }
        }

        private async Task CheckWatchPageAsync(LinkCheckResult result, CancellationToken cancellationToken)
        {
            string html;
            try
            {
                html = await _http.GetStringAsync("https://www.youtube.com/watch?v=" + result.VideoId + "&hl=en&gl=US")
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                // oEmbed already told us what it could; keep that result.
                return;
            }

            var page = ParseWatchPage(html);
            if (page.Blocked)
            {
                Fail(result, page.Message);
            }
            else if (result.Status == Unknown && page.Status == "OK")
            {
                // oEmbed failed to answer but the page says it's fine.
                result.Status = null;
                result.Message = null;
            }

            if (string.IsNullOrEmpty(result.Title))
            {
                result.Title = page.Title;
            }
        }

        public class WatchPageInfo
        {
            public string Status { get; set; }
            public string Reason { get; set; }
            public bool Blocked { get; set; }
            public string Message { get; set; }
            public string Title { get; set; }
        }

        /// <summary>
        /// Reads the playability info YouTube embeds in a watch page
        /// (ytInitialPlayerResponse.playabilityStatus).
        /// </summary>
        public static WatchPageInfo ParseWatchPage(string html)
        {
            var info = new WatchPageInfo();
            if (string.IsNullOrEmpty(html))
            {
                return info;
            }

            var start = html.IndexOf("ytInitialPlayerResponse", StringComparison.Ordinal);
            var at = html.IndexOf("\"playabilityStatus\":", start < 0 ? 0 : start, StringComparison.Ordinal);
            if (at < 0)
            {
                return info;
            }

            var block = html.Substring(at, Math.Min(4000, html.Length - at));
            info.Status = JsonString(block, "status");
            info.Reason = JsonString(block, "reason");

            var titleAt = html.IndexOf("\"videoDetails\":", start < 0 ? 0 : start, StringComparison.Ordinal);
            if (titleAt >= 0)
            {
                info.Title = JsonString(html.Substring(titleAt, Math.Min(2000, html.Length - titleAt)), "title");
            }

            // "reason" is only meaningful when playback is blocked.
            if (info.Status == "OK")
            {
                info.Reason = null;
            }

            var reason = info.Reason ?? string.Empty;
            var ageGate = AgeReason.IsMatch(reason)
                || info.Status == "AGE_CHECK_REQUIRED"
                || info.Status == "AGE_VERIFICATION_REQUIRED"
                || block.Contains("\"desktopLegacyAgeGateReason\"");

            if (ageGate)
            {
                info.Blocked = true;
                info.Message = "This video is age-restricted. YouTube won't play age-restricted videos inside Emby.";
            }
            else if (info.Status == "LOGIN_REQUIRED")
            {
                info.Blocked = true;
                info.Message = "This video needs a YouTube sign-in" + Suffix(reason) + ", so Emby can't play it.";
            }
            else if (info.Status == "UNPLAYABLE" || info.Status == "ERROR" || info.Status == "CONTENT_CHECK_REQUIRED")
            {
                info.Blocked = true;
                info.Message = "YouTube won't play this video" + Suffix(reason) + ".";
            }
            else if (Regex.IsMatch(block, "\"playableInEmbed\"\\s*:\\s*false"))
            {
                info.Blocked = true;
                info.Message = "The uploader doesn't allow this video to play on other sites, so Emby can't play it.";
            }

            return info;
        }

        private static string Suffix(string reason) => string.IsNullOrWhiteSpace(reason) ? string.Empty : " (\"" + reason + "\")";

        private static void Fail(LinkCheckResult result, string message)
        {
            result.Status = Error;
            result.Message = message;
        }

        private static void SetUnknown(LinkCheckResult result, string message)
        {
            result.Status = Unknown;
            result.Message = message;
        }

        /// <summary>Reads the first "key":"value" string from a JSON fragment.</summary>
        internal static string JsonString(string json, string key)
        {
            var m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            if (!m.Success)
            {
                return null;
            }

            try
            {
                return Regex.Unescape(m.Groups[1].Value);
            }
            catch (ArgumentException)
            {
                return m.Groups[1].Value;
            }
        }
    }
}
