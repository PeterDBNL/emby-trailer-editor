using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace TrailerEditor.Search
{
    /// <summary>
    /// Reads YouTube's public search results page (no API key). The page carries its data as a
    /// JSON object assigned to ytInitialData; every video is a "videoRenderer" inside it.
    /// This is unofficial and may break if YouTube changes the page.
    /// </summary>
    public static class YouTubeSearch
    {
        public const int MaxResults = 15;

        private static readonly Regex VideoId = new Regex("^[A-Za-z0-9_-]{11}$", RegexOptions.Compiled);

        public static string SearchUrl(string query) =>
            "https://www.youtube.com/results?search_query=" + Uri.EscapeDataString(query) + "&hl=en&gl=US";

        /// <summary>The first videos in the page, in the order YouTube lists them. Empty if the page isn't understood.</summary>
        public static List<Candidate> ParseResults(string html, int max = MaxResults)
        {
            var result = new List<Candidate>();
            var data = MiniJson.ExtractObject(html, "ytInitialData");
            if (data == null)
            {
                return result;
            }

            var seen = new HashSet<string>();
            Walk(data, node =>
            {
                if (result.Count >= max)
                {
                    return;
                }

                var c = ToCandidate(node);
                if (c != null && seen.Add(c.VideoId))
                {
                    result.Add(c);
                }
            }, 0);
            return result;
        }

        /// <summary>Calls the action for every "videoRenderer" object, depth-first in page order.</summary>
        private static void Walk(object node, Action<Dictionary<string, object>> found, int depth)
        {
            if (depth > 60)
            {
                return;
            }

            if (node is Dictionary<string, object> d)
            {
                foreach (var pair in d)
                {
                    if (pair.Key == "videoRenderer" && pair.Value is Dictionary<string, object> renderer)
                    {
                        found(renderer);
                    }
                    else
                    {
                        Walk(pair.Value, found, depth + 1);
                    }
                }
            }
            else if (node is List<object> list)
            {
                foreach (var child in list)
                {
                    Walk(child, found, depth + 1);
                }
            }
        }

        private static Candidate ToCandidate(Dictionary<string, object> r)
        {
            var id = MiniJson.Str(r, "videoId");
            if (id == null || !VideoId.IsMatch(id))
            {
                return null;
            }

            var length = Text(MiniJson.Get(r, "lengthText"));
            var views = Text(MiniJson.Get(r, "viewCountText")) ?? Text(MiniJson.Get(r, "shortViewCountText"));
            var candidate = new Candidate
            {
                VideoId = id,
                Url = "https://www.youtube.com/watch?v=" + id,
                Title = Text(MiniJson.Get(r, "title")) ?? string.Empty,
                Channel = Text(MiniJson.Get(r, "ownerText")) ?? Text(MiniJson.Get(r, "longBylineText")),
                Verified = HasVerifiedBadge(MiniJson.Get(r, "ownerBadges")),
                LengthText = length,
                LengthSeconds = ParseLength(length),
                Views = ParseViews(views),
                Source = "youtube",
                InSearch = true
            };
            return candidate;
        }

        private static bool HasVerifiedBadge(object badges)
        {
            if (!(badges is List<object> list))
            {
                return false;
            }

            foreach (var badge in list)
            {
                var renderer = MiniJson.Get(badge, "metadataBadgeRenderer");
                var style = MiniJson.Str(renderer, "style") ?? string.Empty;
                var label = (MiniJson.Str(renderer, "tooltip") ?? MiniJson.Str(renderer, "accessibilityData") ?? string.Empty);
                if (style.IndexOf("VERIFIED", StringComparison.OrdinalIgnoreCase) >= 0
                    || label.IndexOf("Verified", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Reads a YouTube text object: {"simpleText": "..."} or {"runs": [{"text": "..."}]}.</summary>
        internal static string Text(object node)
        {
            if (node is string s)
            {
                return s;
            }

            var simple = MiniJson.Str(node, "simpleText");
            if (simple != null)
            {
                return simple;
            }

            var runs = MiniJson.List(node, "runs");
            if (runs != null)
            {
                var sb = new StringBuilder();
                foreach (var run in runs)
                {
                    sb.Append(MiniJson.Str(run, "text"));
                }

                return sb.Length > 0 ? sb.ToString() : null;
            }

            return null;
        }

        /// <summary>"3:05" or "1:02:30" to seconds; 0 when not understood (e.g. live streams have no length).</summary>
        public static int ParseLength(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return 0;
            }

            var seconds = 0;
            foreach (var part in text.Trim().Split(':'))
            {
                if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                {
                    return 0;
                }

                seconds = seconds * 60 + n;
            }

            return seconds;
        }

        /// <summary>"1,234,567 views" to 1234567; "No views" to 0; -1 when not understood.</summary>
        public static long ParseViews(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return -1;
            }

            if (text.StartsWith("No views", StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            // Short forms such as "1.2M views" / "950K views".
            var m = Regex.Match(text, @"([\d.,]+)\s*([KMB])?\s*(?:views?|watching)", RegexOptions.IgnoreCase);
            if (!m.Success)
            {
                return -1;
            }

            var number = m.Groups[1].Value;
            var suffix = m.Groups[2].Value.ToUpperInvariant();
            if (suffix.Length == 0)
            {
                return long.TryParse(number.Replace(",", string.Empty).Replace(".", string.Empty), NumberStyles.None, CultureInfo.InvariantCulture, out var whole) ? whole : -1;
            }

            if (!double.TryParse(number.Replace(",", string.Empty), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                return -1;
            }

            var factor = suffix == "K" ? 1e3 : suffix == "M" ? 1e6 : 1e9;
            return (long)Math.Round(value * factor);
        }
    }
}
