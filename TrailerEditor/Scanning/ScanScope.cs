using System;

namespace TrailerEditor.Scanning
{
    /// <summary>
    /// Which part of the library a scan covers: all titles, or movies / TV shows only,
    /// and a range of first letters ("#" = anything that isn't A-Z, then A to Z).
    /// </summary>
    public class ScanScope
    {
        public const string MovieType = "Movie";
        public const string SeriesType = "Series";

        /// <summary>"" for movies and TV shows, otherwise "Movie" or "Series".</summary>
        public string Types { get; }

        /// <summary>First letter of the range: "#" or "A".."Z".</summary>
        public string From { get; }

        /// <summary>Last letter of the range: "#" or "A".."Z".</summary>
        public string To { get; }

        /// <summary>Only titles added (Emby's "date added") in the last this many days; 0 = any time.</summary>
        public int AddedWithinDays { get; }

        public const int MaxAddedWithinDays = 3650;

        public static ScanScope All { get; } = new ScanScope(null, null, null);

        public ScanScope(string types, string from, string to, int? addedWithinDays = null)
        {
            AddedWithinDays = Math.Max(0, Math.Min(MaxAddedWithinDays, addedWithinDays ?? 0));
            var t = (types ?? string.Empty).Trim();
            Types = string.Equals(t, MovieType, StringComparison.OrdinalIgnoreCase) ? MovieType
                : string.Equals(t, SeriesType, StringComparison.OrdinalIgnoreCase) ? SeriesType
                : string.Empty;

            var f = NormalizeLetter(from, "#");
            var l = NormalizeLetter(to, "Z");
            if (Index(f) > Index(l))
            {
                var swap = f;
                f = l;
                l = swap;
            }

            From = f;
            To = l;
        }

        /// <summary>The scope a stored scan used. Results from older versions have none, which means everything.</summary>
        public static ScanScope Of(ScanResults results) => new ScanScope(results.ScopeTypes, results.ScopeFrom, results.ScopeTo, results.ScopeAddedWithinDays);

        public bool AllTypes => Types.Length == 0;

        public bool AllLetters => From == "#" && To == "Z";

        public bool DateLimited => AddedWithinDays > 0;

        public bool IsEverything => AllTypes && AllLetters && !DateLimited;

        /// <summary>True if a title added at addedUtc is inside the date limit, counted back from referenceUtc. Unknown dates never match a date limit.</summary>
        public bool MatchesDate(DateTime? addedUtc, DateTime referenceUtc) =>
            !DateLimited || (addedUtc != null && addedUtc.Value >= referenceUtc.AddDays(-AddedWithinDays));

        /// <summary>"A" for A-Z and "#" for everything else, from a title's sort name (or name).</summary>
        public static string LetterOf(string sortName, string name)
        {
            var text = !string.IsNullOrWhiteSpace(sortName) ? sortName : name;
            text = (text ?? string.Empty).TrimStart();
            if (text.Length == 0)
            {
                return "#";
            }

            var c = char.ToUpperInvariant(text[0]);
            return c >= 'A' && c <= 'Z' ? c.ToString() : "#";
        }

        /// <summary>
        /// True if an entry is inside the scope. An entry with no known letter (saved by an older
        /// version) or no known type counts as inside only for the part that isn't restricted.
        /// </summary>
        public bool Matches(string type, string letter)
        {
            var typeOk = AllTypes || string.Equals(type, Types, StringComparison.OrdinalIgnoreCase);
            var letterOk = AllLetters || (!string.IsNullOrEmpty(letter) && Index(letter) >= Index(From) && Index(letter) <= Index(To));
            return typeOk && letterOk;
        }

        /// <summary>e.g. "Movies, A–C"; null when the scope is everything.</summary>
        public string Describe()
        {
            var parts = new System.Collections.Generic.List<string>();
            if (!AllTypes)
            {
                parts.Add(Types == MovieType ? "Movies" : "TV shows");
            }

            if (!AllLetters)
            {
                parts.Add(From == To ? From : From + "–" + To);
            }

            if (DateLimited)
            {
                parts.Add(AddedWithinDays == 1 ? "added in the last day" : "added in the last " + AddedWithinDays + " days");
            }

            return parts.Count == 0 ? null : string.Join(", ", parts);
        }

        private static string NormalizeLetter(string value, string fallback)
        {
            var text = (value ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                return fallback;
            }

            var c = char.ToUpperInvariant(text[0]);
            return c >= 'A' && c <= 'Z' ? c.ToString() : "#";
        }

        private static int Index(string letter) => letter == "#" ? 0 : letter[0] - 'A' + 1;
    }
}
