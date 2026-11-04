using System;
using TrailerEditor;
using Xunit;

public class TrailerUrlsTests
{
    private const string Watch = "https://www.youtube.com/watch?v=dQw4w9WgXcQ";

    [Theory]
    [InlineData("dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://youtube.com/watch?feature=share&v=dQw4w9WgXcQ&t=10")]
    [InlineData("https://m.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ?si=abc")]
    [InlineData("youtu.be/dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/embed/dQw4w9WgXcQ")]
    [InlineData("https://www.youtube-nocookie.com/embed/dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/shorts/dQw4w9WgXcQ")]
    [InlineData("  https://www.youtube.com/watch?v=dQw4w9WgXcQ  ")]
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
        var result = TrailerUrls.Normalize(new[] { "", "  ", "dQw4w9WgXcQ", "https://youtu.be/dQw4w9WgXcQ", null, "https://vimeo.com/1" });
        Assert.Equal(new[] { Watch, "https://vimeo.com/1" }, result);
    }
}
