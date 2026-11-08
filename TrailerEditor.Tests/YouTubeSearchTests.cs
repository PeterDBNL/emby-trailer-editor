using System.Linq;
using TrailerEditor.Search;
using Xunit;

public class YouTubeSearchTests
{
    private static readonly string Html = TestEnv.Fixture("youtube-results.html");

    [Fact]
    public void ExtractsInitialDataEvenWithBracesAndScriptEndsInsideStrings()
    {
        var data = MiniJson.ExtractObject(Html, "ytInitialData");
        Assert.NotNull(data);
        var header = MiniJson.Get(MiniJson.Get(data, "header"), "searchHeaderRenderer");
        Assert.Equal("}; </script> in a string", YouTubeSearch.Text(MiniJson.Get(header, "title")));
    }

    [Fact]
    public void ParsesVideoRenderersInPageOrderWithoutAdsChannelsOrDuplicates()
    {
        var results = YouTubeSearch.ParseResults(Html);
        Assert.Equal(new[]
        {
            "DuneOff2021", "DuneReact01", "DunePart2Tr", "DuneFan0001", "DuneFull001", "DuneHonest1", "DuneTeas001", "DuneLive001"
        }, results.Select(r => r.VideoId).ToArray());

        var first = results[0];
        Assert.Equal("DUNE | Official Trailer", first.Title);
        Assert.Equal("Warner Bros. Pictures", first.Channel);
        Assert.True(first.Verified);
        Assert.Equal(188, first.LengthSeconds);
        Assert.Equal("3:08", first.LengthText);
        Assert.Equal(11468297, first.Views);
        Assert.Equal("https://www.youtube.com/watch?v=DuneOff2021", first.Url);
        Assert.True(first.InSearch);

        Assert.False(results[1].Verified);
        Assert.Equal(2 * 3600 + 35 * 60 + 10, results[4].LengthSeconds);
        Assert.Equal(0, results.Last().LengthSeconds); // live: no length
    }

    [Fact]
    public void StopsAfterTheRequestedNumberOfResults()
    {
        Assert.Equal(3, YouTubeSearch.ParseResults(Html, 3).Count);
    }

    [Theory]
    [InlineData("3:08", 188)]
    [InlineData("0:45", 45)]
    [InlineData("1:02:30", 3750)]
    [InlineData("", 0)]
    [InlineData("LIVE", 0)]
    [InlineData(null, 0)]
    public void ParsesLengths(string text, int seconds) => Assert.Equal(seconds, YouTubeSearch.ParseLength(text));

    [Theory]
    [InlineData("11,468,297 views", 11468297)]
    [InlineData("1 view", 1)]
    [InlineData("No views", 0)]
    [InlineData("1.2M views", 1200000)]
    [InlineData("950K views", 950000)]
    [InlineData("1,204 watching", 1204)]
    [InlineData("", -1)]
    [InlineData("hello", -1)]
    public void ParsesViewCounts(string text, long views) => Assert.Equal(views, YouTubeSearch.ParseViews(text));

    [Fact]
    public void ReturnsNothingWhenThePageHasNoDataOrIsBroken()
    {
        Assert.Empty(YouTubeSearch.ParseResults("<html>consent page</html>"));
        Assert.Empty(YouTubeSearch.ParseResults(null));
        Assert.Empty(YouTubeSearch.ParseResults("<script>var ytInitialData = {\"contents\": [1,2,</script>"));
    }

    [Fact]
    public void MiniJsonReadsEscapesNumbersAndNesting()
    {
        var o = MiniJson.TryParse("{\"a\":\"x\\\"y\\u00e9\\n\",\"b\":[1,2.5,-3e2,true,false,null],\"c\":{\"d\":{}}}");
        Assert.Equal("x\"yé\n", MiniJson.Str(o, "a"));
        var b = MiniJson.List(o, "b");
        Assert.Equal(new object[] { 1.0, 2.5, -300.0, true, false, null }, b.ToArray());
        Assert.NotNull(MiniJson.Get(MiniJson.Get(o, "c"), "d"));
    }
}
