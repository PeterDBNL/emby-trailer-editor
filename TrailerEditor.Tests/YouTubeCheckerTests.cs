using System.Threading;
using TrailerEditor;
using Xunit;

public class YouTubeCheckerTests
{
    private static string Page(string playability, string title = "Some Trailer") =>
        "<html><script>var ytInitialPlayerResponse = {\"responseContext\":{},\"playabilityStatus\":" + playability +
        ",\"videoDetails\":{\"videoId\":\"abcdefghijk\",\"title\":\"" + title + "\"}};</script></html>";

    [Fact]
    public void PlayableVideoIsNotBlocked()
    {
        var info = YouTubeChecker.ParseWatchPage(Page("{\"status\":\"OK\",\"playableInEmbed\":true,\"miniplayer\":{\"reason\":\"page\"}}"));
        Assert.Equal("OK", info.Status);
        Assert.False(info.Blocked);
        Assert.Equal("Some Trailer", info.Title);
    }

    [Fact]
    public void AgeRestrictedVideoIsBlocked()
    {
        var info = YouTubeChecker.ParseWatchPage(Page(
            "{\"status\":\"LOGIN_REQUIRED\",\"reason\":\"Sign in to confirm your age\",\"errorScreen\":{\"playerErrorMessageRenderer\":{}},\"desktopLegacyAgeGateReason\":1}"));
        Assert.True(info.Blocked);
        Assert.Contains("age-restricted", info.Message);
    }

    [Fact]
    public void InappropriateContentReasonCountsAsAgeGate()
    {
        var info = YouTubeChecker.ParseWatchPage(Page(
            "{\"status\":\"LOGIN_REQUIRED\",\"reason\":\"This video may be inappropriate for some users.\"}"));
        Assert.True(info.Blocked);
        Assert.Contains("age-restricted", info.Message);
    }

    [Fact]
    public void EmbeddingDisabledIsBlocked()
    {
        var info = YouTubeChecker.ParseWatchPage(Page("{\"status\":\"OK\",\"playableInEmbed\":false}"));
        Assert.True(info.Blocked);
        Assert.Contains("other sites", info.Message);
    }

    [Fact]
    public void UnplayableVideoIsBlockedWithReason()
    {
        var info = YouTubeChecker.ParseWatchPage(Page("{\"status\":\"UNPLAYABLE\",\"reason\":\"Video unavailable\"}"));
        Assert.True(info.Blocked);
        Assert.Contains("Video unavailable", info.Message);
        Assert.DoesNotContain("age-restricted", info.Message);
    }

    [Fact]
    public void PrivateVideoNeedsSignIn()
    {
        var info = YouTubeChecker.ParseWatchPage(Page("{\"status\":\"LOGIN_REQUIRED\",\"reason\":\"This video is private\"}"));
        Assert.True(info.Blocked);
        Assert.Contains("sign-in", info.Message);
    }

    [Fact]
    public void UnrecognisedPageIsNotBlocked()
    {
        var info = YouTubeChecker.ParseWatchPage("<html>consent page</html>");
        Assert.Null(info.Status);
        Assert.False(info.Blocked);
    }

    [Fact]
    public void JsonStringHandlesEscapes()
    {
        Assert.Equal("Alien: \"Covenant\" \u00e9", YouTubeChecker.JsonString("{\"title\":\"Alien: \\\"Covenant\\\" \\u00e9\"}", "title"));
    }

    [Fact]
    public void NonYouTubeLinksAreNotChecked()
    {
        var r = new YouTubeChecker().CheckAsync("https://vimeo.com/123", CancellationToken.None).GetAwaiter().GetResult();
        Assert.Equal(YouTubeChecker.Unknown, r.Status);
    }

    [Fact]
    public void InvalidLinksAreErrors()
    {
        var r = new YouTubeChecker().CheckAsync("not a link", CancellationToken.None).GetAwaiter().GetResult();
        Assert.Equal(YouTubeChecker.Error, r.Status);
    }
}
