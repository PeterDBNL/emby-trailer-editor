using System;
using TrailerEditor;
using Xunit;

public class TrailerUrlsTests
{
    private const string Watch = "https://www.youtube.com/watch?v=sF7HxngbWLM";

    [Theory]
    [InlineData("sF7HxngbWLM")]
    [InlineData("https://www.youtube.com/watch?v=sF7HxngbWLM")]
    [InlineData("https://youtube.com/watch?feature=share&v=sF7HxngbWLM&t=10")]
    [InlineData("https://m.youtube.com/watch?v=sF7HxngbWLM")]
    [InlineData("https://youtu.be/sF7HxngbWLM?si=abc")]
    [InlineData("youtu.be/sF7HxngbWLM")]
    [InlineData("https://www.youtube.com/embed/sF7HxngbWLM")]
    [InlineData("https://www.youtube-nocookie.com/embed/sF7HxngbWLM")]
    [InlineData("https://www.youtube.com/shorts/sF7HxngbWLM")]
    [InlineData("  https://www.youtube.com/watch?v=sF7HxngbWLM  ")]
    public void YouTubeLinksBecomeWatchUrls(string input)
    {
        Assert.Equal(Watch, TrailerUrls.NormalizeOne(input.Trim()));
    }

    [Fact]
    public void OtherLinksAreKept()
    {
        Assert.Equal("https://vimeo.com/123456", TrailerUrls.NormalizeOne("https://vimeo.com/123456"));
        Assert.Equal("http://example.com/trailer.mp4", TrailerUrls.NormalizeOne("http://example.com/trailer.mp4"));
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("ftp://example.com/file.mp4")]
    [InlineData("javascript:alert(1)")]
    [InlineData("localhost")]
    public void InvalidLinksAreRejected(string input)
    {
        Assert.Throws<ArgumentException>(() => TrailerUrls.NormalizeOne(input));
    }

    [Fact]
    public void NormalizeSkipsBlanksAndDuplicates()
    {
        var result = TrailerUrls.Normalize(new[] { "", "  ", "sF7HxngbWLM", "https://youtu.be/sF7HxngbWLM", null, "https://vimeo.com/1" });
        Assert.Equal(new[] { Watch, "https://vimeo.com/1" }, result);
    }
}
