using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using TrailerEditor.Downloads;
using Xunit;

public class DownloadTests
{
    private static readonly string Sep = Path.DirectorySeparatorChar.ToString();
    private static string P(string unixPath) => unixPath.Replace("/", Sep);
    private static readonly Func<string, bool> Nothing = _ => false;

    // ---- asset names ----

    [Theory]
    [InlineData("linux", Architecture.X64, "yt-dlp_linux")]
    [InlineData("linux", Architecture.Arm64, "yt-dlp_linux_aarch64")]
    [InlineData("linux", Architecture.Arm, "yt-dlp_linux_armv7l")]
    [InlineData("linux", Architecture.X86, null)]
    [InlineData("windows", Architecture.X64, "yt-dlp.exe")]
    [InlineData("windows", Architecture.X86, "yt-dlp_x86.exe")]
    [InlineData("macos", Architecture.Arm64, "yt-dlp_macos")]
    [InlineData("macos", Architecture.X64, "yt-dlp_macos")]
    [InlineData("freebsd", Architecture.X64, null)]
    public void AssetNameDependsOnSystem(string os, Architecture arch, string expected)
    {
        Assert.Equal(expected, YtDlp.AssetName(os, arch));
    }

    // ---- target path ----

    [Fact]
    public void MovieFileGetsTrailerNextToIt()
    {
        var stem = YtDlp.TargetStem(true, false, P("/media/Movies/Alien (1979)/Alien (1979).mkv"), "Alien", 1979, Nothing);
        Assert.Equal(P("/media/Movies/Alien (1979)/Alien (1979)-trailer"), stem);
    }

    [Fact]
    public void MovieFolderUsesTrailersSubfolder()
    {
        var stem = YtDlp.TargetStem(true, true, P("/media/Movies/Alien (1979)/"), "Alien", 1979, Nothing);
        Assert.Equal(P("/media/Movies/Alien (1979)/trailers/Alien (1979)-trailer"), stem);
    }

    [Fact]
    public void SeriesUsesTrailersSubfolder()
    {
        var stem = YtDlp.TargetStem(false, true, P("/media/TV/Severance"), "Severance", 2022, Nothing);
        Assert.Equal(P("/media/TV/Severance/trailers/Severance (2022)-trailer"), stem);
    }

    [Fact]
    public void NameWithoutYearAndBadCharacters()
    {
        var stem = YtDlp.TargetStem(false, true, P("/tv/Show"), "Who: What?/Why", null, Nothing);
        Assert.Equal(P("/tv/Show/trailers/Who WhatWhy-trailer"), stem);
    }

    [Fact]
    public void ExistingTrailersAreNotOverwritten()
    {
        var taken = new HashSet<string>
        {
            P("/m/Alien (1979)/Alien (1979)-trailer"),
            P("/m/Alien (1979)/Alien (1979) 2-trailer")
        };
        var stem = YtDlp.TargetStem(true, false, P("/m/Alien (1979)/Alien (1979).mkv"), "Alien", 1979, taken.Contains);
        Assert.Equal(P("/m/Alien (1979)/Alien (1979) 3-trailer"), stem);
        Assert.EndsWith("-trailer", stem);
    }

    [Fact]
    public void NoPathIsRejected()
    {
        Assert.Throws<ArgumentException>(() => YtDlp.TargetStem(true, false, "", "X", 1, Nothing));
    }

    // ---- arguments ----

    private const string Url = "https://www.youtube.com/watch?v=dQw4w9WgXcQ";

    [Fact]
    public void ArgumentsWithFfmpegMergeStreams()
    {
        var args = YtDlp.BuildArguments(Url, "/m/A/A-trailer", "/usr/bin/ffmpeg-emby", 720);
        Assert.Contains("--no-playlist", args);
        Assert.Contains("--newline", args);
        Assert.Contains("--no-mtime", args);
        Assert.Contains("bv*[height<=720]+ba/b[height<=720]/b", args);
        Assert.Contains("--merge-output-format mp4", args);
        Assert.Contains("--ffmpeg-location /usr/bin/ffmpeg-emby", args);
        Assert.Contains("-o /m/A/A-trailer.%(ext)s", args);
        Assert.EndsWith("-- " + Url, args);
    }

    [Fact]
    public void ArgumentsWithoutFfmpegUseSingleFile()
    {
        var args = YtDlp.BuildArguments(Url, "/m/A/A-trailer", null, 1080);
        Assert.Contains("b[height<=1080][ext=mp4]/b[height<=1080]/b", args);
        Assert.DoesNotContain("--merge-output-format", args);
        Assert.DoesNotContain("--ffmpeg-location", args);
    }

    [Fact]
    public void ArgumentsQuotePathsWithSpacesAndQuotes()
    {
        var args = YtDlp.BuildArguments(Url, @"/m/My ""Movie""/x-trailer", "/opt/my ffmpeg/ffmpeg", 1080, "/cache dir");
        Assert.Contains("--cache-dir \"/cache dir\"", args);
        Assert.Contains("\"/opt/my ffmpeg/ffmpeg\"", args);
        Assert.Contains("\"/m/My \\\"Movie\\\"/x-trailer.%(ext)s\"", args);
        // The URL only appears once, as the last argument, after "--".
        Assert.Equal(1, args.Split(new[] { "youtube.com" }, StringSplitOptions.None).Length - 1);
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("", "\"\"")]
    [InlineData("a b", "\"a b\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData("C:\\dir with space\\", "\"C:\\dir with space\\\\\"")]
    public void QuoteEscapesArguments(string input, string expected)
    {
        Assert.Equal(expected, YtDlp.Quote(input));
    }

    // ---- ffmpeg lookup ----

    [Fact]
    public void FfmpegPrefersEmbysEncoderThenKnownPathsThenPath()
    {
        Assert.Equal("/e/ffmpeg", TrailerDownloader.PickFfmpeg("/e/ffmpeg", p => true, "/usr/bin", false));
        Assert.Equal("/usr/bin/ffmpeg-emby", TrailerDownloader.PickFfmpeg("/missing", p => p == "/usr/bin/ffmpeg-emby", "/x", false));
        Assert.Equal(P("/opt/bin/ffmpeg"), TrailerDownloader.PickFfmpeg(null, p => p == P("/opt/bin/ffmpeg"), "/usr/local/bin:/opt/bin", false));
        Assert.Null(TrailerDownloader.PickFfmpeg("ffmpeg", p => false, "/usr/bin", false));
    }

    // ---- output parsing ----

    [Theory]
    [InlineData("[download]  42.3% of   10.00MiB at    1.00MiB/s ETA 00:05", 42.3)]
    [InlineData("[download] 100% of 10.00MiB in 00:08", 100.0)]
    [InlineData("[download]   0.0% of ~  10.00MiB", 0.0)]
    public void ProgressLinesAreParsed(string line, double expected)
    {
        Assert.Equal(expected, YtDlp.ParseProgress(line));
    }

    [Theory]
    [InlineData("[download] Destination: /m/x.mp4")]
    [InlineData("[youtube] Extracting URL")]
    [InlineData("")]
    [InlineData(null)]
    public void OtherLinesAreNotProgress(string line)
    {
        Assert.Null(YtDlp.ParseProgress(line));
    }

    [Fact]
    public void LastErrorLineIsFound()
    {
        var lines = new[] { "WARNING: x", "ERROR: first", "[info] whatever", "ERROR: second", "" };
        Assert.Equal("ERROR: second", YtDlp.LastErrorLine(lines));
        Assert.Null(YtDlp.LastErrorLine(new[] { "WARNING: only" }));
    }

    [Theory]
    [InlineData("ERROR: [youtube] dQw4w9WgXcQ: Sign in to confirm your age. This video may be inappropriate for some users.", "age-restricted")]
    [InlineData("ERROR: [youtube] dQw4w9WgXcQ: Private video. Sign in if you've been granted access to this video", "private")]
    [InlineData("ERROR: [youtube] dQw4w9WgXcQ: Video unavailable", "unavailable")]
    [InlineData("ERROR: [youtube] dQw4w9WgXcQ: This video is not available in your country", "unavailable")]
    [InlineData("ERROR: [youtube] dQw4w9WgXcQ: Sign in to confirm you're not a bot", "bot")]
    [InlineData("ERROR: Something odd happened", "Download failed: Something odd happened")]
    public void ErrorsAreMapped(string line, string expectedPart)
    {
        Assert.Contains(expectedPart, YtDlp.MapError(line));
    }

    [Fact]
    public void AgeRestrictionMessageIsExact()
    {
        Assert.Equal("YouTube requires a signed-in account for this video (age-restricted), so it can't be downloaded.",
            YtDlp.MapError("ERROR: [youtube] x1234567890: Sign in to confirm your age"));
    }

    [Fact]
    public void MissingErrorGivesGenericMessage()
    {
        Assert.Contains("without an error", YtDlp.MapError(null));
    }

    // ---- folders ----

    [Fact]
    public void WritableFolderPasses()
    {
        var dir = Path.Combine(Path.GetTempPath(), "te-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Null(YtDlp.CheckWritable(Path.Combine(dir, "trailers")));
            Assert.Empty(Directory.GetFiles(Path.Combine(dir, "trailers")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void UnwritableFolderGivesClearMessage()
    {
        // A file where the folder should be: the folder can't be created.
        var file = Path.GetTempFileName();
        try
        {
            var dir = Path.Combine(file, "trailers");
            var message = YtDlp.CheckWritable(dir);
            Assert.Equal("Emby can't write to " + dir + ". Check the folder permissions or Docker volume (it may be mounted read-only).", message);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void CleanupRemovesPartialFilesOnly()
    {
        var dir = Path.Combine(Path.GetTempPath(), "te-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "A-trailer.mp4.part"), "x");
            File.WriteAllText(Path.Combine(dir, "A-trailer.mp4.ytdl"), "x");
            File.WriteAllText(Path.Combine(dir, "A.mkv"), "x");
            TrailerDownloader.Cleanup(Path.Combine(dir, "A-trailer"));
            Assert.Equal(new[] { "A.mkv" }, Directory.GetFiles(dir).Select(Path.GetFileName).ToArray());
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---- process runner with a fake yt-dlp (Linux only) ----

    private static bool IsLinux => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    private static string FakeTool(string dir, string body)
    {
        var path = Path.Combine(dir, "fake-yt-dlp");
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        var chmod = System.Diagnostics.Process.Start("chmod", "+x \"" + path + "\"");
        chmod.WaitForExit();
        return path;
    }

    [Fact]
    public async Task FakeToolReportsProgressAndWritesFile()
    {
        if (!IsLinux) { return; }
        var dir = Path.Combine(Path.GetTempPath(), "te-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var tool = FakeTool(dir,
                "echo '[download]  10.0% of 5MiB'\necho '[download]  50.0% of 5MiB'\necho '[download] 100% of 5MiB'\n" +
                "echo 'ERROR: [youtube] x: Video unavailable' >&2\n" +
                "echo data > \"" + dir + "/Movie-trailer.mp4\"\nexit 0");
            var progress = new List<double>();
            var errors = new List<string>();
            var code = await ProcessRunner.RunAsync(tool, "--newline " + YtDlp.Quote("a b"),
                l => { var p = YtDlp.ParseProgress(l); if (p.HasValue) { progress.Add(p.Value); } },
                errors.Add, TimeSpan.FromSeconds(20), CancellationToken.None);

            Assert.Equal(0, code);
            Assert.Equal(new[] { 10.0, 50.0, 100.0 }, progress.ToArray());
            Assert.Contains("unavailable", YtDlp.MapError(YtDlp.LastErrorLine(errors)));
            Assert.True(File.Exists(Path.Combine(dir, "Movie-trailer.mp4")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task FakeToolFailureExitCodeIsReturned()
    {
        if (!IsLinux) { return; }
        var dir = Path.Combine(Path.GetTempPath(), "te-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var tool = FakeTool(dir, "echo 'ERROR: Private video' >&2\nexit 1");
            var errors = new List<string>();
            var code = await ProcessRunner.RunAsync(tool, "", null, errors.Add, TimeSpan.FromSeconds(20), CancellationToken.None);
            Assert.Equal(1, code);
            Assert.Contains("private", YtDlp.MapError(YtDlp.LastErrorLine(errors)));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task SlowToolIsKilledOnTimeout()
    {
        if (!IsLinux) { return; }
        var dir = Path.Combine(Path.GetTempPath(), "te-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var tool = FakeTool(dir, "sleep 30");
            var started = DateTime.UtcNow;
            await Assert.ThrowsAsync<TimeoutException>(() =>
                ProcessRunner.RunAsync(tool, "", null, null, TimeSpan.FromMilliseconds(500), CancellationToken.None));
            Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
        }
        finally { Directory.Delete(dir, true); }
    }
}
