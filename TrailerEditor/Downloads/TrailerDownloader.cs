using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;

namespace TrailerEditor.Downloads
{
    public class DownloadsStatus
    {
        /// <summary>The yt-dlp executable that will be used.</summary>
        public string YtDlpPath { get; set; }
        public bool Installed { get; set; }
        public string YtDlpVersion { get; set; }
        public string FfmpegPath { get; set; }

        /// <summary>Why yt-dlp can't be used, or null.</summary>
        public string Error { get; set; }
    }

    public class DownloadJob
    {
        public const string None = "none";
        public const string Running = "running";
        public const string Done = "done";
        public const string Failed = "failed";

        public string State { get; set; } = None;
        public double Percent { get; set; }
        public string Message { get; set; }
        public string FilePath { get; set; }
        public string Url { get; set; }
    }

    /// <summary>Downloads trailers with yt-dlp and saves them next to the media.</summary>
    public class TrailerDownloader
    {
        public static readonly TimeSpan JobTimeout = TimeSpan.FromMinutes(30);

        private readonly ILogger _logger;
        private readonly IApplicationPaths _paths;
        private readonly IMediaEncoder _mediaEncoder;
        private readonly IProviderManager _providerManager;
        private readonly IFileSystem _fileSystem;
        private readonly Func<TrailerManager> _manager;

        private readonly ConcurrentDictionary<long, DownloadJob> _jobs = new ConcurrentDictionary<long, DownloadJob>();
        private readonly SemaphoreSlim _slots = new SemaphoreSlim(2, 2);

        public TrailerDownloader(ILogger logger, IApplicationPaths paths, IMediaEncoder mediaEncoder,
            IProviderManager providerManager, IFileSystem fileSystem, Func<TrailerManager> manager)
        {
            _logger = logger;
            _paths = paths;
            _mediaEncoder = mediaEncoder;
            _providerManager = providerManager;
            _fileSystem = fileSystem;
            _manager = manager;
        }

        // ---------------- yt-dlp / ffmpeg ----------------

        private static bool IsWindows => YtDlp.CurrentOs() == "windows";

        public string ManagedPath =>
            Path.Combine(_paths.PluginConfigurationsPath, "TrailerEditor", "yt-dlp") + (IsWindows ? ".exe" : string.Empty);

        public string ResolveYtDlpPath()
        {
            var configured = (Plugin.Instance.Configuration.YtDlpPath ?? string.Empty).Trim();
            return configured.Length > 0 ? configured : ManagedPath;
        }

        /// <summary>Emby's own ffmpeg if it can be found (needed to merge separate video and audio streams).</summary>
        public string FindFfmpeg()
        {
            string encoder = null;
#pragma warning disable CS0618 // IMediaEncoder.EncoderPath is marked obsolete in the SDK but still works; failures fall back to known locations.
            try { encoder = _mediaEncoder?.EncoderPath; } catch (Exception) { /* not available */ }
#pragma warning restore CS0618
            return PickFfmpeg(encoder, File.Exists, Environment.GetEnvironmentVariable("PATH"), IsWindows);
        }

        public static string PickFfmpeg(string encoderPath, Func<string, bool> fileExists, string pathEnv, bool windows)
        {
            if (!string.IsNullOrWhiteSpace(encoderPath) && Path.IsPathRooted(encoderPath) && fileExists(encoderPath))
            {
                return encoderPath;
            }

            var known = new[] { "/usr/bin/ffmpeg-emby", "/opt/emby-server/bin/ffmpeg", "/usr/lib/emby-server/bin/ffmpeg" };
            foreach (var path in known)
            {
                if (fileExists(path)) { return path; }
            }

            var exe = windows ? "ffmpeg.exe" : "ffmpeg";
            foreach (var dir in (pathEnv ?? string.Empty).Split(windows ? ';' : ':').Where(d => d.Trim().Length > 0))
            {
                var candidate = Path.Combine(dir.Trim(), exe);
                if (fileExists(candidate)) { return candidate; }
            }

            return null;
        }

        public async Task<DownloadsStatus> GetStatusAsync()
        {
            var path = ResolveYtDlpPath();
            var status = new DownloadsStatus { YtDlpPath = path, FfmpegPath = FindFfmpeg() };
            if (!File.Exists(path))
            {
                status.Error = "yt-dlp isn't installed yet.";
                return status;
            }

            try
            {
                status.YtDlpVersion = await GetVersionAsync(path).ConfigureAwait(false);
                status.Installed = status.YtDlpVersion != null;
                if (!status.Installed) { status.Error = "yt-dlp didn't report a version."; }
            }
            catch (Exception ex)
            {
                status.Error = "yt-dlp can't be started: " + ex.Message;
            }

            return status;
        }

        private static async Task<string> GetVersionAsync(string exe)
        {
            var lines = new List<string>();
            var code = await ProcessRunner.RunAsync(exe, "--version", lines.Add, null, TimeSpan.FromSeconds(20), CancellationToken.None).ConfigureAwait(false);
            return code == 0 ? lines.Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) : null;
        }

        /// <summary>Downloads the official yt-dlp release for this system into the plugin's data folder.</summary>
        public async Task<DownloadsStatus> InstallAsync(CancellationToken ct)
        {
            var asset = YtDlp.AssetName(YtDlp.CurrentOs(), RuntimeInformation.OSArchitecture);
            if (asset == null)
            {
                throw new InvalidOperationException("There is no official yt-dlp download for this system (" +
                    RuntimeInformation.OSDescription + ", " + RuntimeInformation.OSArchitecture +
                    "). Install yt-dlp yourself and enter its path in the settings.");
            }

            var target = ManagedPath;
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            var temp = target + ".download";
            _logger.Info("Trailer Editor: downloading yt-dlp ({0}) to {1}", asset, target);

            try
            {
                using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
                {
                    http.DefaultRequestHeaders.UserAgent.ParseAdd("TrailerEditor-EmbyPlugin");
                    using (var response = await http.GetAsync(YtDlp.ReleaseBase + asset, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                    {
                        response.EnsureSuccessStatusCode();
                        using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (var output = File.Create(temp))
                        {
                            await input.CopyToAsync(output, 81920, ct).ConfigureAwait(false);
                        }
                    }
                }

                if (new FileInfo(temp).Length < 1024 * 1024)
                {
                    throw new InvalidOperationException("The downloaded file is too small to be yt-dlp.");
                }

                if (File.Exists(target)) { File.Delete(target); }
                File.Move(temp, target);
            }
            catch
            {
                try { if (File.Exists(temp)) { File.Delete(temp); } } catch (Exception) { /* ignore */ }
                throw;
            }

            if (!IsWindows)
            {
                await ProcessRunner.RunAsync("chmod", "+x " + YtDlp.Quote(target), null, null, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            }

            var status = await GetStatusAsync().ConfigureAwait(false);
            _logger.Info("Trailer Editor: yt-dlp installed, version {0}", status.YtDlpVersion ?? "unknown");
            return status;
        }

        // ---------------- jobs ----------------

        public DownloadJob GetJob(long itemId) =>
            _jobs.TryGetValue(itemId, out var job) ? Snapshot(job) : new DownloadJob();

        private static DownloadJob Snapshot(DownloadJob j)
        {
            lock (j)
            {
                return new DownloadJob { State = j.State, Percent = j.Percent, Message = j.Message, FilePath = j.FilePath, Url = j.Url };
            }
        }

        /// <summary>Starts a background download for the item. Throws ArgumentException with a user-readable message if it can't start.</summary>
        public DownloadJob Start(BaseItem item, string url, bool removeLink)
        {
            string normalized;
            try { normalized = TrailerUrls.NormalizeOne((url ?? string.Empty).Trim()); }
            catch (ArgumentException) { throw new ArgumentException("That is not a YouTube link."); }
            var videoId = YouTubeChecker.VideoIdOf(normalized);
            if (videoId == null) { throw new ArgumentException("Only YouTube links can be downloaded."); }
            normalized = "https://www.youtube.com/watch?v=" + videoId;

            var exe = ResolveYtDlpPath();
            if (!File.Exists(exe)) { throw new ArgumentException("yt-dlp isn't installed yet. Install it first."); }

            if (string.IsNullOrWhiteSpace(item.Path))
            {
                throw new ArgumentException("This item has no location on disk, so there is nowhere to save the trailer.");
            }

            var isMovie = string.Equals(item.GetClientTypeName(), "Movie", StringComparison.OrdinalIgnoreCase);
            var stem = YtDlp.TargetStem(isMovie, Directory.Exists(item.Path), item.Path, item.Name, item.ProductionYear, StemExists);
            var dir = Path.GetDirectoryName(stem);
            var problem = YtDlp.CheckWritable(dir);
            if (problem != null) { throw new ArgumentException(problem); }

            var job = new DownloadJob { State = DownloadJob.Running, Message = "Starting…", Url = normalized };
            var key = item.InternalId;
            while (true)
            {
                if (_jobs.TryGetValue(key, out var existing))
                {
                    bool running;
                    lock (existing) { running = existing.State == DownloadJob.Running; }
                    if (running) { throw new ArgumentException("A download for this title is already running."); }
                    if (!_jobs.TryUpdate(key, job, existing)) { continue; }
                    break;
                }

                if (_jobs.TryAdd(key, job)) { break; }
            }

            var maxHeight = Plugin.Instance.Configuration.DownloadMaxHeight;
            var ffmpeg = FindFfmpeg();
            var args = YtDlp.BuildArguments(normalized, stem, ffmpeg, maxHeight, Path.Combine(Path.GetDirectoryName(ManagedPath), "cache"));
            _logger.Info("Trailer Editor: downloading {0} for {1} ({2}) to {3}", normalized, item.Name, item.InternalId, stem);

            Task.Run(() => RunJobAsync(job, item, exe, args, stem, videoId, removeLink));
            return Snapshot(job);
        }

        private static bool StemExists(string stem)
        {
            var dir = Path.GetDirectoryName(stem);
            if (!Directory.Exists(dir)) { return false; }
            var prefix = Path.GetFileName(stem) + ".";
            return Directory.EnumerateFiles(dir).Any(f => Path.GetFileName(f).StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }

        private async Task RunJobAsync(DownloadJob job, BaseItem item, string exe, string args, string stem, string videoId, bool removeLink)
        {
            var errors = new List<string>();
            var waiting = !_slots.Wait(0);
            if (waiting)
            {
                lock (job) { job.Message = "Waiting for another download to finish…"; }
                await _slots.WaitAsync().ConfigureAwait(false);
            }

            try
            {
                lock (job) { job.Message = "Downloading…"; }
                var peak = 0.0;
                void OnOut(string line)
                {
                    var p = YtDlp.ParseProgress(line);
                    if (p.HasValue)
                    {
                        // Video and audio download one after the other; never go backwards, and stay below 100 until done.
                        peak = Math.Max(peak, p.Value);
                        lock (job) { job.Percent = Math.Min(99, peak); }
                    }
                    else if (line.StartsWith("[Merger]", StringComparison.Ordinal))
                    {
                        lock (job) { job.Message = "Merging video and audio…"; }
                    }
                }

                void OnErr(string line) { lock (errors) { errors.Add(line); } }

                int code;
                try
                {
                    code = await ProcessRunner.RunAsync(exe, args, OnOut, OnErr, JobTimeout, CancellationToken.None).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    Fail(job, stem, "The download took longer than " + (int)JobTimeout.TotalMinutes + " minutes and was stopped.");
                    return;
                }
                catch (Exception ex)
                {
                    Fail(job, stem, "yt-dlp couldn't be started: " + ex.Message);
                    return;
                }

                string[] errorLines;
                lock (errors) { errorLines = errors.ToArray(); }

                var file = code == 0 ? FindResult(stem) : null;
                if (file == null)
                {
                    var err = YtDlp.LastErrorLine(errorLines);
                    _logger.Error("Trailer Editor: yt-dlp failed for {0} (exit {1}): {2}", item.Name, code, err ?? string.Join(" | ", errorLines.Reverse().Take(3)));
                    Fail(job, stem, code == 0 ? "yt-dlp finished but no video file was saved." : YtDlp.MapError(err));
                    return;
                }

                lock (job) { job.Percent = 100; job.FilePath = file; job.Message = "Saved as " + Path.GetFileName(file) + "."; job.State = DownloadJob.Done; }
                _logger.Info("Trailer Editor: saved trailer for {0} as {1}", item.Name, file);

                AfterSuccess(job, item, videoId, removeLink);
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Trailer Editor: download failed", ex);
                Fail(job, stem, "Download failed: " + ex.Message);
            }
            finally
            {
                _slots.Release();
            }
        }

        private void AfterSuccess(DownloadJob job, BaseItem item, string videoId, bool removeLink)
        {
            var notes = new StringBuilder();
            try
            {
                if (removeLink)
                {
                    var manager = _manager();
                    var current = item.RemoteTrailers ?? new string[0];
                    var remaining = current.Where(u => YouTubeChecker.VideoIdOf(SafeNormalize(u)) != videoId).ToArray();
                    if (remaining.Length != current.Length)
                    {
                        manager.SetTrailers(item, remaining, manager.IsPinned(item));
                        notes.Append(" The YouTube link was removed.");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Trailer Editor: couldn't remove the trailer link", ex);
                notes.Append(" (The YouTube link couldn't be removed.)");
            }

            try
            {
                var options = new MetadataRefreshOptions(_fileSystem)
                {
                    MetadataRefreshMode = MetadataRefreshMode.ValidationOnly,
                    ImageRefreshMode = MetadataRefreshMode.ValidationOnly,
                    ReplaceAllMetadata = false,
                    ReplaceAllImages = false
                };
                _providerManager.QueueRefresh(item.InternalId, options, RefreshPriority.High);
                _logger.Info("Trailer Editor: queued a refresh of {0} to pick up the local trailer", item.Name);
                notes.Append(" Emby is refreshing the title so the trailer shows up.");
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Trailer Editor: couldn't queue a refresh", ex);
                notes.Append(" Refresh the title in Emby to make the trailer show up.");
            }

            lock (job) { job.Message += notes.ToString(); }
        }

        private static string SafeNormalize(string url)
        {
            try { return TrailerUrls.NormalizeOne((url ?? string.Empty).Trim()); }
            catch (ArgumentException) { return string.Empty; }
        }

        private static string FindResult(string stem)
        {
            var dir = Path.GetDirectoryName(stem);
            var prefix = Path.GetFileName(stem) + ".";
            var bad = new[] { ".part", ".ytdl", ".tmp", ".temp", ".json", ".jpg", ".webp", ".png" };
            return Directory.Exists(dir)
                ? Directory.EnumerateFiles(dir)
                    .Where(f => Path.GetFileName(f).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                        && !bad.Contains(Path.GetExtension(f).ToLowerInvariant()))
                    .OrderByDescending(f => new FileInfo(f).Length)
                    .FirstOrDefault()
                : null;
        }

        private static void Fail(DownloadJob job, string stem, string message)
        {
            Cleanup(stem);
            lock (job) { job.State = DownloadJob.Failed; job.Message = message; }
        }

        /// <summary>Removes partial files (.part, .ytdl, unmerged streams) left behind. The target name never existed before the download.</summary>
        public static void Cleanup(string stem)
        {
            try
            {
                var dir = Path.GetDirectoryName(stem);
                if (!Directory.Exists(dir)) { return; }
                var prefix = Path.GetFileName(stem) + ".";
                foreach (var f in Directory.EnumerateFiles(dir).Where(f => Path.GetFileName(f).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
                {
                    try { File.Delete(f); } catch (Exception) { /* ignore */ }
                }
            }
            catch (Exception) { /* ignore */ }
        }
    }

    /// <summary>Runs a program without a shell and reads its output line by line.</summary>
    public static class ProcessRunner
    {
        /// <summary>Returns the exit code. Throws TimeoutException (after killing the process) or OperationCanceledException.</summary>
        public static async Task<int> RunAsync(string exe, string arguments, Action<string> onStdout, Action<string> onStderr,
            TimeSpan timeout, CancellationToken ct)
        {
            var psi = new ProcessStartInfo(exe, arguments)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using (var process = new Process { StartInfo = psi, EnableRaisingEvents = true })
            {
                var exited = new TaskCompletionSource<bool>();
                var outDone = new TaskCompletionSource<bool>();
                var errDone = new TaskCompletionSource<bool>();
                process.OutputDataReceived += (s, e) => { if (e.Data == null) { outDone.TrySetResult(true); } else { onStdout?.Invoke(e.Data); } };
                process.ErrorDataReceived += (s, e) => { if (e.Data == null) { errDone.TrySetResult(true); } else { onStderr?.Invoke(e.Data); } };
                process.Exited += (s, e) => exited.TrySetResult(true);

                process.Start();
                try { process.StandardInput.Close(); } catch (Exception) { /* ignore */ }
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    var finished = await Task.WhenAny(exited.Task, Task.Delay(timeout, cts.Token)).ConfigureAwait(false);
                    if (finished != exited.Task || ct.IsCancellationRequested)
                    {
                        Kill(process);
                        if (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
                        throw new TimeoutException();
                    }

                    cts.Cancel();
                }

                // Let the output readers drain.
                await Task.WhenAny(Task.WhenAll(outDone.Task, errDone.Task), Task.Delay(2000)).ConfigureAwait(false);
                return process.ExitCode;
            }
        }

        private static void Kill(Process process)
        {
            try
            {
                if (process.HasExited) { return; }

                // yt-dlp's single-file build starts a child process; stop it too.
                if (YtDlp.CurrentOs() != "windows")
                {
                    try { Process.Start(new ProcessStartInfo("pkill", "-KILL -P " + process.Id) { UseShellExecute = false, CreateNoWindow = true })?.WaitForExit(2000); }
                    catch (Exception) { /* pkill may not exist */ }
                }

                process.Kill();
            }
            catch (Exception) { /* already gone */ }
        }
    }
}
