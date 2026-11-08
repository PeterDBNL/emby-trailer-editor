using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace TrailerEditor.Search
{
    /// <summary>
    /// A small, tolerant JSON reader for pages we don't control (YouTube's ytInitialData, TMDb answers).
    /// Objects become Dictionary&lt;string, object&gt;, arrays List&lt;object&gt;, numbers double,
    /// plus string, bool and null. Used instead of a JSON package so the plugin needs no extra DLLs.
    /// </summary>
    public static class MiniJson
    {
        private const int MaxDepth = 200;

        /// <summary>Parses the first JSON value found at or after <paramref name="start"/>. Returns null if it isn't valid.</summary>
        public static object TryParse(string text, int start = 0)
        {
            if (string.IsNullOrEmpty(text) || start < 0 || start >= text.Length)
            {
                return null;
            }

            try
            {
                var pos = start;
                return ParseValue(text, ref pos, 0);
            }
            catch (FormatException)
            {
                return null;
            }
        }

        /// <summary>
        /// Finds "marker" followed by an assignment (e.g. ytInitialData = {...}) in an HTML page and
        /// parses the JSON object after it. The object's end is found by the parser itself, so braces
        /// inside strings and anything after the object (a trailing ";&lt;/script&gt;") don't matter.
        /// </summary>
        public static Dictionary<string, object> ExtractObject(string text, string marker)
        {
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }

            var search = 0;
            while (search < text.Length)
            {
                var at = text.IndexOf(marker, search, StringComparison.Ordinal);
                if (at < 0)
                {
                    return null;
                }

                var i = at + marker.Length;
                while (i < text.Length && (char.IsWhiteSpace(text[i]) || text[i] == '=' || text[i] == '"' || text[i] == '\''))
                {
                    i++;
                }

                if (i < text.Length && text[i] == '{' && TryParse(text, i) is Dictionary<string, object> obj)
                {
                    return obj;
                }

                search = at + marker.Length;
            }

            return null;
        }

        // ---- Helpers for reading the parsed tree ----

        public static object Get(object node, string key) =>
            node is Dictionary<string, object> d && d.TryGetValue(key, out var v) ? v : null;

        public static string Str(object node, string key) => Get(node, key) as string;

        public static List<object> List(object node, string key) => Get(node, key) as List<object>;

        public static bool Bool(object node, string key) => Get(node, key) is bool b && b;

        public static double Num(object node, string key) => Get(node, key) is double n ? n : 0;

        // ---- Parser ----

        private static object ParseValue(string s, ref int i, int depth)
        {
            if (depth > MaxDepth)
            {
                throw new FormatException("too deep");
            }

            SkipSpace(s, ref i);
            if (i >= s.Length)
            {
                throw new FormatException("unexpected end");
            }

            switch (s[i])
            {
                case '{':
                    return ParseObject(s, ref i, depth);
                case '[':
                    return ParseArray(s, ref i, depth);
                case '"':
                    return ParseString(s, ref i);
                case 't':
                    Expect(s, ref i, "true");
                    return true;
                case 'f':
                    Expect(s, ref i, "false");
                    return false;
                case 'n':
                    Expect(s, ref i, "null");
                    return null;
                default:
                    return ParseNumber(s, ref i);
            }
        }

        private static Dictionary<string, object> ParseObject(string s, ref int i, int depth)
        {
            var result = new Dictionary<string, object>();
            i++; // {
            SkipSpace(s, ref i);
            if (i < s.Length && s[i] == '}')
            {
                i++;
                return result;
            }

            while (true)
            {
                SkipSpace(s, ref i);
                if (i >= s.Length || s[i] != '"')
                {
                    throw new FormatException("expected a property name");
                }

                var key = ParseString(s, ref i);
                SkipSpace(s, ref i);
                if (i >= s.Length || s[i] != ':')
                {
                    throw new FormatException("expected ':'");
                }

                i++;
                result[key] = ParseValue(s, ref i, depth + 1);
                SkipSpace(s, ref i);
                if (i >= s.Length)
                {
                    throw new FormatException("unexpected end");
                }

                if (s[i] == ',')
                {
                    i++;
                    continue;
                }

                if (s[i] == '}')
                {
                    i++;
                    return result;
                }

                throw new FormatException("expected ',' or '}'");
            }
        }

        private static List<object> ParseArray(string s, ref int i, int depth)
        {
            var result = new List<object>();
            i++; // [
            SkipSpace(s, ref i);
            if (i < s.Length && s[i] == ']')
            {
                i++;
                return result;
            }

            while (true)
            {
                result.Add(ParseValue(s, ref i, depth + 1));
                SkipSpace(s, ref i);
                if (i >= s.Length)
                {
                    throw new FormatException("unexpected end");
                }

                if (s[i] == ',')
                {
                    i++;
                    continue;
                }

                if (s[i] == ']')
                {
                    i++;
                    return result;
                }

                throw new FormatException("expected ',' or ']'");
            }
        }

        private static string ParseString(string s, ref int i)
        {
            i++; // opening quote
            var sb = new StringBuilder();
            while (i < s.Length)
            {
                var c = s[i++];
                if (c == '"')
                {
                    return sb.ToString();
                }

                if (c != '\\')
                {
                    sb.Append(c);
                    continue;
                }

                if (i >= s.Length)
                {
                    break;
                }

                var e = s[i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        if (i + 4 > s.Length || !int.TryParse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                        {
                            throw new FormatException("bad unicode escape");
                        }

                        sb.Append((char)code);
                        i += 4;
                        break;
                    default: sb.Append(e); break; // \" \\ \/ and \x are taken literally
                }
            }

            throw new FormatException("unterminated string");
        }

        private static double ParseNumber(string s, ref int i)
        {
            var start = i;
            while (i < s.Length && ("+-0123456789.eE".IndexOf(s[i]) >= 0))
            {
                i++;
            }

            if (i == start || !double.TryParse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
            {
                throw new FormatException("bad number");
            }

            return n;
        }

        private static void Expect(string s, ref int i, string word)
        {
            if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0)
            {
                throw new FormatException("expected " + word);
            }

            i += word.Length;
        }

        private static void SkipSpace(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i]))
            {
                i++;
            }
        }
    }
}
