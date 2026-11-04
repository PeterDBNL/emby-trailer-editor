using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace TrailerEditor.Downloads
{
    /// <summary>Pure helpers for running yt-dlp: file names, arguments, output parsing. No I/O except CheckWritable.</summary>
    public static class YtDlp
    {
        public const string ReleaseBase = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/";

        /// <summary>Name of the official release file for this system, or null if there is none.</summary>
        public static string AssetName(string os, Architecture arch)
        {
            switch ((os ?? string.Empty).ToLowerInvariant())
            {
                case "linux":
                    if (arch == Architecture.X64) { return "yt-dlp_linux"; }
                    if (arch == Architecture.Arm64) { return "yt-dlp_linux_aarch64"; }
                    if (arch == Architecture.Arm) { return "yt-dlp_linux_armv7l"; }
                    return null;
                case "windows":
                    return arch == Architecture.X86 ? "yt-dlp_x86.exe" : "yt-dlp.exe";
                case "macos":
                    return "yt-dlp_macos";
                default:
                    return null;
            }
        }

        public static string CurrentOs()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) { return "windows"; }
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) { return "macos"; }
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) { return "linux"; }
            return "other";
        }

        // ---------------- target file ----------------

        private static readonly char[] Invalid = Path.GetInvalidFileNameChars()
            .Concat(new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' }).Distinct().ToArray();

        /// <summary>"Name (Year)" with characters that are not allowed in file names removed.</summary>
        public static string SafeTitle(string name, int? year)
        {
            var sb = new StringBuilder();
            foreach (var c in name ?? string.Empty)
            {
                if (!Invalid.Contains(c) && !char.IsControl(c)) { sb.Append(c); }
            }

            var title = Regex.Replace(sb.ToString(), "\\s+", " ").Trim().TrimEnd('.');
            if (title.Length == 0) { title = "Trailer"; }
            return year.HasValue && year.Value > 0 ? title + " (" + year.Value + ")" : title;
        }

        /// <summary>
        /// Where a downloaded trailer goes, without the extension (yt-dlp adds it). Emby recognises a
        /// file whose name ends in -trailer, next to the movie or in a "trailers" folder.
        /// <paramref name="exists"/> says whether a file with that path and any extension already exists.
        /// </summary>
        public static string TargetStem(bool isMovie, bool pathIsFolder, string itemPath, string name, int? year, Func<string, bool> exists)
        {
            if (string.IsNullOrWhiteSpace(itemPath)) { throw new ArgumentException("This item has no location on disk."); }

            var title = SafeTitle(name, year);
            string dir;
            string baseName;
            var path = itemPath.TrimEnd('/', '\\');

            if (isMovie && !pathIsFolder)
            {
                dir = Path.GetDirectoryName(path);
                baseName = Path.GetFileNameWithoutExtension(path);
            }
            else
            {
                // Series, or a movie stored as a folder (BD/DVD): <folder>/trailers/<Name (Year)>-trailer
                dir = Path.Combine(path, "trailers");
                baseName = title;
            }

            var stem = Path.Combine(dir, baseName + "-trailer");
            for (var n = 2; exists(stem) && n < 1000; n++)
            {
                stem = Path.Combine(dir, baseName + " " + n + "-trailer");
            }

            return stem;
        }

        // ---------------- arguments ----------------

        /// <summary>Builds the yt-dlp command line. Only the normalized YouTube URL is passed.</summary>
        public static string BuildArguments(string normalizedUrl, string outputStem, string ffmpegPath, int maxHeight, string cacheDir = null)
        {
            if (maxHeight < 144) { maxHeight = 1080; }
            var h = maxHeight.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var args = new List<string> { "--no-playlist", "--newline", "--no-mtime", "--no-warnings" };

            if (!string.IsNullOrEmpty(cacheDir))
            {
                args.Add("--cache-dir");
                args.Add(cacheDir);
            }

            args.Add("-f");
            if (!string.IsNullOrEmpty(ffmpegPath))
            {
                args.Add("bv*[height<=" + h + "]+ba/b[height<=" + h + "]/b");
                args.Add("--merge-output-format");
                args.Add("mp4");
                args.Add("--ffmpeg-location");
                args.Add(ffmpegPath);
            }
            else
            {
                args.Add("b[height<=" + h + "][ext=mp4]/b[height<=" + h + "]/b");
            }

            args.Add("-o");
            args.Add(outputStem + ".%(ext)s");
            args.Add("--");
            args.Add(normalizedUrl);
            return string.Join(" ", args.Select(Quote));
        }

        /// <summary>Quotes one argument for ProcessStartInfo.Arguments (Windows command-line rules, also used by .NET on Linux).</summary>
        public static string Quote(string arg)
        {
            if (arg == null) { arg = string.Empty; }
            if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0) { return arg; }

            var sb = new StringBuilder("\"");
            var backslashes = 0;
            foreach (var c in arg)
            {
                if (c == '\\')
                {
                    backslashes++;
                }
                else if (c == '"')
                {
                    sb.Append('\\', backslashes * 2 + 1).Append('"');
                    backslashes = 0;
                }
                else
                {
                    sb.Append('\\', backslashes).Append(c);
                    backslashes = 0;
                }
            }

            sb.Append('\\', backslashes * 2).Append('"');
            return sb.ToString();
        }

        // ---------------- output ----------------

        private static readonly Regex ProgressLine = new Regex(@"^\[download\]\s+(\d{1,3}(?:\.\d+)?)%", RegexOptions.Compiled);

        /// <summary>Percent from a "[download]  42.3% of ..." line, or null.</summary>
        public static double? ParseProgress(string line)
        {
            if (line == null) { return null; }
            var m = ProgressLine.Match(line.Trim());
            if (!m.Success) { return null; }
            return double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v) && v >= 0 && v <= 100 ? v : (double?)null;
        }

        /// <summary>The last line that starts with ERROR:, or null.</summary>
        public static string LastErrorLine(IEnumerable<string> lines)
        {
            return (lines ?? Enumerable.Empty<string>())
                .Select(l => (l ?? string.Empty).Trim())
                .LastOrDefault(l => l.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Turns a yt-dlp ERROR line into a message for the user.</summary>
        public static string MapError(string errorLine)
        {
            var text = (errorLine ?? string.Empty).Trim();
            if (text.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase)) { text = text.Substring(6).Trim(); }
            text = Regex.Replace(text, @"^(\[[^\]]+\]\s+)+[A-Za-z0-9_-]{11}:\s*", string.Empty);
            var lower = text.ToLowerInvariant();

            if (lower.Contains("confirm your age") || lower.Contains("age-restricted") || lower.Contains("age restricted") || lower.Contains("inappropriate for some users"))
            {
                return "YouTube requires a signed-in account for this video (age-restricted), so it can't be downloaded.";
            }

            if (lower.Contains("not a bot"))
            {
                return "YouTube is asking this server to prove it isn't a bot, so the download was blocked. Try again later, or update yt-dlp.";
            }

            if (lower.Contains("private video"))
            {
                return "This video is private, so it can't be downloaded.";
            }

            if (lower.Contains("video unavailable") || lower.Contains("not available") || lower.Contains("has been removed") || lower.Contains("no longer available") || lower.Contains("who has blocked it") || lower.Contains("copyright"))
            {
                return "YouTube says this video is unavailable (removed, private, or blocked for this server's country).";
            }

            if (lower.Contains("429") || lower.Contains("too many requests"))
            {
                return "YouTube is limiting requests from this server. Try again later.";
            }

            if (text.Length == 0) { return "yt-dlp failed without an error message."; }
            if (text.Length > 300) { text = text.Substring(0, 300) + "…"; }
            return "Download failed: " + text;
        }

        // ---------------- folders ----------------

        public static string WriteProblem(string dir) =>
            "Emby can't write to " + dir + ". Check the folder permissions or Docker volume (it may be mounted read-only).";

        /// <summary>Makes sure the folder exists and Emby can write to it. Returns null when fine, otherwise a message.</summary>
        public static string CheckWritable(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                var probe = Path.Combine(dir, ".trailereditor-" + Guid.NewGuid().ToString("N") + ".tmp");
                File.WriteAllText(probe, "x");
                File.Delete(probe);
                return null;
            }
            catch (Exception)
            {
                return WriteProblem(dir);
            }
        }
    }
}
