using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace TrailerEditor
{
    /// <summary>Cleans up and validates trailer links entered by the user.</summary>
    public static class TrailerUrls
    {
        private static readonly Regex YouTubeId = new Regex("^[A-Za-z0-9_-]{11}$", RegexOptions.Compiled);

        /// <summary>
        /// Trims, normalizes and de-duplicates the given links. A bare 11-character
        /// YouTube video id, youtu.be, /embed/ and /shorts/ links are turned into
        /// standard https://www.youtube.com/watch?v=ID links, which Emby apps play.
        /// Throws ArgumentException for anything that is not an http(s) URL.
        /// </summary>
        public static string[] Normalize(IEnumerable<string> input)
        {
            var result = new List<string>();
            foreach (var raw in input ?? Enumerable.Empty<string>())
            {
                var value = (raw ?? string.Empty).Trim();
                if (value.Length == 0)
                {
                    continue;
                }

                var url = NormalizeOne(value);
                if (!result.Contains(url, StringComparer.OrdinalIgnoreCase))
                {
                    result.Add(url);
                }
            }

            return result.ToArray();
        }

        /// <summary>YouTube video id of any YouTube link (or bare id), or null for other or invalid links.</summary>
        public static string YouTubeIdOf(string value)
        {
            try
            {
                return YouTubeChecker.VideoIdOf(NormalizeOne((value ?? string.Empty).Trim()));
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        public static string NormalizeOne(string value)
        {
            if (YouTubeId.IsMatch(value))
            {
                return WatchUrl(value);
            }

            if (!value.Contains("://"))
            {
                value = "https://" + value;
            }

            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                || string.IsNullOrEmpty(uri.Host)
                || !uri.Host.Contains("."))
            {
                throw new ArgumentException("Not a valid http(s) link: " + value);
            }

            var id = GetYouTubeId(uri);
            return id != null ? WatchUrl(id) : uri.AbsoluteUri;
        }

        private static string GetYouTubeId(Uri uri)
        {
            var host = uri.Host.ToLowerInvariant();
            var segments = uri.AbsolutePath.Trim('/').Split('/');

            if (host == "youtu.be")
            {
                return Valid(segments[0]);
            }

            if (host == "youtube.com" || host.EndsWith(".youtube.com") || host == "youtube-nocookie.com" || host.EndsWith(".youtube-nocookie.com"))
            {
                if (segments.Length >= 2 && (segments[0] == "embed" || segments[0] == "shorts" || segments[0] == "v" || segments[0] == "live"))
                {
                    return Valid(segments[1]);
                }

                if (segments[0] == "watch")
                {
                    foreach (var pair in uri.Query.TrimStart('?').Split('&'))
                    {
                        var kv = pair.Split(new[] { '=' }, 2);
                        if (kv.Length == 2 && kv[0] == "v")
                        {
                            return Valid(Uri.UnescapeDataString(kv[1]));
                        }
                    }
                }
            }

            return null;
        }

        private static string Valid(string id) => id != null && YouTubeId.IsMatch(id) ? id : null;

        private static string WatchUrl(string id) => "https://www.youtube.com/watch?v=" + id;
    }
}
