using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace TrailerEditor.Search
{
    /// <summary>What the scorer needs to know about the movie or TV show.</summary>
    public class ScoreTarget
    {
        public string Name { get; set; }
        public string OriginalTitle { get; set; }
        public int? Year { get; set; }
        public bool IsSeries { get; set; }

        /// <summary>Server's preferred metadata language, e.g. "nl".</summary>
        public string Language { get; set; } = "en";
    }

    /// <summary>
    /// Scores how likely a video is the official trailer of a title. Pure and deterministic.
    ///
    /// Weights (points):
    ///   Title match (YouTube results)  0 to +40  (70% how much of the title is in the video title, 30% how little else is)
    ///                                  recall below 50% = not a match
    ///   Year in the title              +10 same year, -15 a different year (series: -6)
    ///   "official trailer"             +12   ("trailer" +8, "teaser" +3, none of them -30)
    ///   Listed by TMDb                 +45   (+15 official, +5 Trailer / +1 Teaser type, +6 language match / +2 English, +2 for 1080p)
    ///   Verified channel               +6
    ///   Length                         45s to 4:30 +8; under 30s -10; over 6:00 -25; over 20:00 another -15
    ///   Views                          +1.5 per power of ten, at most +12
    ///   Unwanted words                 -45 each, at most -90: reaction, review, fan made, concept, parody, explained,
    ///                                  breakdown, recap, honest trailer, full movie, behind the scenes, interview,
    ///                                  clip, scene, soundtrack, music video, edit, vs, ending, ...
    /// A score below <see cref="MinimumScore"/> is not a match.
    /// </summary>
    public static class TrailerScorer
    {
        public const double MinimumScore = 35;

        private static readonly HashSet<string> NoiseWords = new HashSet<string>(new[]
        {
            "official", "trailer", "trailers", "teaser", "hd", "uhd", "4k", "1080p", "720p", "2160p", "hdr",
            "movie", "film", "season", "series", "new", "final", "main", "full", "tv", "show", "spot",
            "theaters", "theatres", "cinemas", "in", "the", "a", "an", "of", "and"
        });

        // Checked in this order; text matched by an earlier pattern is removed before the next is tried,
        // so "behind the scenes" doesn't also count as "scene".
        private static readonly string[] UnwantedPatterns =
        {
            @"\bbehind the scenes\b", @"\bhonest trailers?\b", @"\bfull (?:movie|film|episodes?)\b", @"\bmusic video\b",
            @"\bfan[ -]?made\b", @"\bfan (?:trailer|film|edit)\b", @"\bmaking of\b", @"\bwhat if\b",
            @"\breact(?:ion|ions|s|ing)?\b", @"\breviews?\b", @"\bconcept\b", @"\bparody\b", @"\bexplained\b",
            @"\bbreakdown\b", @"\brecap\b", @"\binterview\b", @"\bclips?\b", @"\bscenes?\b", @"\bsoundtrack\b",
            @"\bedit\b", @"\bvs\b", @"\bending\b", @"\banalysis\b", @"\btheory\b", @"\brecut\b", @"\btribute\b", @"\bost\b"
        };

        private static readonly Regex YearRegex = new Regex(@"(?<!\d)(?:19|20)\d{2}(?!\d)", RegexOptions.Compiled);

        public static bool IsMatch(Candidate c) => c.Score >= MinimumScore;

        /// <summary>Calculates c.Score and c.Reasons.</summary>
        public static void Score(ScoreTarget item, Candidate c)
        {
            var reasons = new List<string>();
            double score = 0;
            var title = c.Title ?? string.Empty;
            var lower = Fold(title);
            var itemTokens = TitleTokens(item);

            if (c.InSearch)
            {
                var sim = TitleSimilarity(item, c, out var recall);
                if (recall < 0.5)
                {
                    c.Score = -100;
                    c.Reasons = new List<string> { "title doesn't match" };
                    return;
                }

                score += 40 * sim;
                reasons.Add("title match " + (int)Math.Round(sim * 100) + "%");

                score += YearScore(item, title, itemTokens, reasons);
            }

            if (lower.Length > 0 || c.InTmdb)
            {
                if (lower.Contains("official trailer") || lower.Contains("official teaser trailer"))
                {
                    score += 12;
                    reasons.Add("official trailer");
                }
                else if (Regex.IsMatch(lower, @"\btrailer\b"))
                {
                    score += 8;
                    reasons.Add("trailer");
                }
                else if (Regex.IsMatch(lower, @"\bteaser\b"))
                {
                    score += 3;
                    reasons.Add("teaser");
                }
                else if (c.InSearch && !c.InTmdb)
                {
                    score -= 30;
                    reasons.Add("no \"trailer\" in the title");
                }
            }

            if (c.InTmdb)
            {
                score += 45;
                reasons.Add("listed by TMDb");
                if (c.Official)
                {
                    score += 15;
                    reasons.Add("TMDb: official");
                }

                if (string.Equals(c.Type, "Trailer", StringComparison.OrdinalIgnoreCase))
                {
                    score += 5;
                }
                else if (string.Equals(c.Type, "Teaser", StringComparison.OrdinalIgnoreCase))
                {
                    score += 1;
                }

                if (!string.IsNullOrEmpty(c.Language) && string.Equals(c.Language, item.Language, StringComparison.OrdinalIgnoreCase))
                {
                    score += 6;
                    reasons.Add("your language");
                }
                else if (string.Equals(c.Language, "en", StringComparison.OrdinalIgnoreCase))
                {
                    score += 2;
                }

                if (c.Size >= 1080)
                {
                    score += 2;
                }
            }

            if (c.Verified)
            {
                score += 6;
                reasons.Add("verified channel");
            }

            score += LengthScore(c.LengthSeconds, reasons);

            if (c.Views > 0)
            {
                score += Math.Min(12, 1.5 * Math.Log10(c.Views));
            }

            score += UnwantedScore(lower, itemTokens, reasons);

            c.Score = Math.Round(score, 1);
            c.Reasons = reasons;
        }

        private static double LengthScore(int seconds, List<string> reasons)
        {
            if (seconds <= 0)
            {
                return 0;
            }

            if (seconds >= 45 && seconds <= 270)
            {
                reasons.Add("trailer length");
                return 8;
            }

            if (seconds < 30)
            {
                reasons.Add("very short");
                return -10;
            }

            if (seconds > 1200)
            {
                reasons.Add("very long");
                return -40;
            }

            if (seconds > 360)
            {
                reasons.Add("too long for a trailer");
                return -25;
            }

            return 0;
        }

        private static double YearScore(ScoreTarget item, string title, HashSet<string> itemTokens, List<string> reasons)
        {
            if (item.Year == null)
            {
                return 0;
            }

            var years = YearRegex.Matches(title).Cast<Match>()
                .Select(m => m.Value).Where(y => !itemTokens.Contains(y)).Select(y => int.Parse(y, CultureInfo.InvariantCulture)).ToList();
            if (years.Count == 0)
            {
                return 0;
            }

            if (years.Any(y => y == item.Year.Value))
            {
                reasons.Add("year " + item.Year);
                return 10;
            }

            if (years.Any(y => Math.Abs(y - item.Year.Value) <= 1))
            {
                return 0;
            }

            reasons.Add("different year");
            return item.IsSeries ? -6 : -15;
        }

        private static double UnwantedScore(string lowerTitle, HashSet<string> itemTokens, List<string> reasons)
        {
            var remaining = lowerTitle;
            double penalty = 0;
            foreach (var pattern in UnwantedPatterns)
            {
                var m = Regex.Match(remaining, pattern);
                if (!m.Success)
                {
                    continue;
                }

                remaining = remaining.Remove(m.Index, m.Length).Insert(m.Index, new string(' ', m.Length));

                // A title that really contains the word (e.g. "The Interview") isn't penalised for it.
                var words = m.Value.Split(' ');
                if (words.All(itemTokens.Contains))
                {
                    continue;
                }

                penalty -= 45;
                reasons.Add("\"" + m.Value + "\" in the title");
                if (penalty <= -90)
                {
                    break;
                }
            }

            return penalty;
        }

        /// <summary>How well the video title matches the item's name (or original title), 0 to 1, and how much of the item's title it contains.</summary>
        internal static double TitleSimilarity(ScoreTarget item, Candidate c, out double recall)
        {
            var best = 0.0;
            recall = 0;
            var channel = new HashSet<string>(Tokens(c.Channel ?? string.Empty, null));

            foreach (var name in new[] { item.Name, item.OriginalTitle }.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct())
            {
                var wanted = Tokens(name, null);
                if (wanted.Count == 0)
                {
                    wanted = Fold(name).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList();
                }

                var wantedSet = new HashSet<string>(wanted);

                // Titles often end with "| Official Trailer | Studio", so also try the first parts alone.
                var parts = (c.Title ?? string.Empty).Split('|');
                for (var n = 1; n <= parts.Length; n++)
                {
                    var shown = Tokens(string.Join(" ", parts.Take(n)), wantedSet);
                    shown = shown.Where(t => wantedSet.Contains(t) || !channel.Contains(t)).ToList();
                    var set = new HashSet<string>(shown);
                    var hits = wantedSet.Count(set.Contains);
                    var r = (double)hits / wantedSet.Count;
                    var precision = set.Count == 0 ? 0 : (double)hits / set.Count;
                    var sim = 0.7 * r + 0.3 * precision;
                    if (sim > best)
                    {
                        best = sim;
                        recall = r;
                    }
                }
            }

            return best;
        }

        private static HashSet<string> TitleTokens(ScoreTarget item)
        {
            var set = new HashSet<string>();
            foreach (var name in new[] { item.Name, item.OriginalTitle })
            {
                foreach (var t in Fold(name ?? string.Empty).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    set.Add(t);
                }
            }

            return set;
        }

        /// <summary>
        /// Lower-case words of a title without noise words (official, trailer, hd, ...).
        /// Years that aren't part of the wanted title are dropped; they are scored separately.
        /// </summary>
        public static List<string> Tokens(string title, HashSet<string> wantedTitleWords)
        {
            var result = new List<string>();
            foreach (var t in Fold(title).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (NoiseWords.Contains(t))
                {
                    continue;
                }

                if (wantedTitleWords != null && YearRegex.IsMatch(t) && t.Length == 4 && !wantedTitleWords.Contains(t))
                {
                    continue;
                }

                result.Add(t);
            }

            return result;
        }

        /// <summary>Lower case, accents removed, anything but letters and digits turned into spaces.</summary>
        internal static string Fold(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            var sb = new StringBuilder(text.Length);
            foreach (var ch in text.Normalize(NormalizationForm.FormD))
            {
                var category = CharUnicodeInfo.GetUnicodeCategory(ch);
                if (category == UnicodeCategory.NonSpacingMark)
                {
                    continue;
                }

                sb.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : ' ');
            }

            return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
        }
    }
}
