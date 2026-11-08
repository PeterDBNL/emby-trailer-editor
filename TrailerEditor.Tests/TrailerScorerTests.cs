using System.Collections.Generic;
using System.Linq;
using TrailerEditor.Search;
using Xunit;

public class TrailerScorerTests
{
    private static readonly ScoreTarget Dune = new ScoreTarget { Name = "Dune", Year = 2021, Language = "en" };

    private static Candidate Yt(string title, string channel = "Some Channel", bool verified = false, int seconds = 150, long views = 100000) =>
        new Candidate { VideoId = "xxxxxxxxxxx", Title = title, Channel = channel, Verified = verified, LengthSeconds = seconds, Views = views, InSearch = true };

    private static double Score(ScoreTarget item, Candidate c)
    {
        TrailerScorer.Score(item, c);
        return c.Score;
    }

    [Fact]
    public void OfficialTrailerBeatsReactionFanMadeReviewAndOthers()
    {
        var official = Yt("DUNE | Official Trailer", "Warner Bros. Pictures", true, 188, 11000000);
        var bad = new[]
        {
            "Dune Trailer REACTION!", "Dune (2021) - Fan Made Trailer", "Dune trailer review", "Honest Trailers | Dune",
            "Dune 2021 FULL MOVIE", "Dune trailer explained", "Dune official trailer breakdown", "Dune Concept Trailer",
            "Dune trailer parody", "Dune behind the scenes", "Dune trailer music video", "Dune Trailer Edit"
        };
        var best = Score(Dune, official);
        Assert.True(TrailerScorer.IsMatch(official));
        foreach (var title in bad)
        {
            var c = Yt(title, "Someone", true, 188, 11000000);
            Assert.True(Score(Dune, c) < best, title);
            Assert.False(TrailerScorer.IsMatch(c), title + " scored " + c.Score);
        }
    }

    [Fact]
    public void WrongYearAndWrongTitleArePenalised()
    {
        var right = Yt("Dune (2021) Official Trailer");
        var wrongYear = Yt("Dune (1984) Official Trailer");
        var wrongTitle = Yt("Dune Messiah Official Trailer 2030 Hobbit");
        Assert.True(Score(Dune, right) > Score(Dune, wrongYear) + 20);
        Assert.True(right.Reasons.Contains("year 2021"));
        Assert.Contains("different year", wrongYear.Reasons);
        Assert.True(Score(Dune, right) > Score(Dune, wrongTitle));

        var unrelated = Yt("Interstellar Official Trailer");
        TrailerScorer.Score(Dune, unrelated);
        Assert.False(TrailerScorer.IsMatch(unrelated));
        Assert.Contains("title doesn't match", unrelated.Reasons);
    }

    [Fact]
    public void SequelWithTheSameStartIsRankedBelowTheOriginal()
    {
        var original = Yt("Dune | Official Trailer", "WB", true, 188, 10000000);
        var sequel = Yt("Dune: Part Two | Official Trailer", "WB", true, 188, 10000000);
        Assert.True(Score(Dune, original) > Score(Dune, sequel));
    }

    [Fact]
    public void LongVideosAreStronglyPenalisedAndShortOnesToo()
    {
        var normal = Score(Dune, Yt("Dune Official Trailer", seconds: 150));
        var sixtyMin = Score(Dune, Yt("Dune Official Trailer", seconds: 3600));
        var eight = Score(Dune, Yt("Dune Official Trailer", seconds: 480));
        var tiny = Score(Dune, Yt("Dune Official Trailer", seconds: 20));
        Assert.True(normal - eight >= 30);
        Assert.True(eight > sixtyMin);
        Assert.True(normal - tiny >= 15);
        Assert.False(TrailerScorer.IsMatch(Yt("Dune Official Trailer", seconds: 3600, views: 10)));
    }

    [Fact]
    public void TmdbOfficialTrailerInYourLanguageIsFavoured()
    {
        var item = new ScoreTarget { Name = "Dune", Year = 2021, Language = "nl" };
        Candidate Tmdb(bool official, string lang, string type = "Trailer") => new Candidate
        {
            VideoId = "yyyyyyyyyyy", Title = "Official Trailer", InTmdb = true, Official = official, Language = lang, Type = type, Size = 1080, Source = "tmdb"
        };

        var nlOfficial = Tmdb(true, "nl");
        var enOfficial = Tmdb(true, "en");
        var enUnofficial = Tmdb(false, "en");
        var teaser = Tmdb(false, "en", "Teaser");
        Assert.True(Score(item, nlOfficial) > Score(item, enOfficial));
        Assert.True(Score(item, enOfficial) > Score(item, enUnofficial));
        Assert.True(Score(item, enUnofficial) > Score(item, teaser) - 0.1);
        Assert.True(TrailerScorer.IsMatch(enOfficial));

        // A TMDb entry beats an unremarkable search result.
        var search = Yt("Dune (2021) Official Trailer", "Warner Bros. Pictures", true, 188, 1000000);
        Assert.True(Score(item, enOfficial) > Score(item, Yt("Dune Official Trailer")));
        Assert.True(Score(item, nlOfficial) > Score(item, Yt("Dune Official Trailer")));
    }

    [Fact]
    public void WordsThatAreInTheTitleItselfAreNotPenalised()
    {
        var interview = new ScoreTarget { Name = "The Interview", Year = 2014 };
        var c = Yt("The Interview (2014) Official Trailer");
        Assert.True(Score(interview, c) > 50);
        Assert.True(TrailerScorer.IsMatch(c));
        Assert.DoesNotContain(c.Reasons, r => r.Contains("interview"));
    }

    [Fact]
    public void ChannelNamesAndStudioSuffixesDoNotHurtTheTitleMatch()
    {
        var c = Yt("Dune | Official Trailer | Warner Bros. Pictures", "Warner Bros. Pictures");
        var plain = Yt("Dune | Official Trailer", "Warner Bros. Pictures");
        Assert.True(System.Math.Abs(Score(Dune, c) - Score(Dune, plain)) < 1);
    }

    [Fact]
    public void OriginalTitleCanMatch()
    {
        var item = new ScoreTarget { Name = "De Duinplaneet", OriginalTitle = "Dune", Year = 2021 };
        var c = Yt("Dune (2021) Official Trailer");
        TrailerScorer.Score(item, c);
        Assert.True(TrailerScorer.IsMatch(c));
    }

    [Fact]
    public void MissingTrailerWordCostsPoints()
    {
        var with = Score(Dune, Yt("Dune 2021 Trailer"));
        var without = Score(Dune, Yt("Dune 2021"));
        Assert.True(with - without >= 35);
    }

    [Fact]
    public void FoldRemovesAccentsAndPunctuation()
    {
        Assert.Equal("amelie le fabuleux destin", TrailerScorer.Fold("Amélie: Le Fabuleux  Destin!"));
        Assert.Equal(new List<string> { "dune" }, TrailerScorer.Tokens("DUNE (Official Trailer) HD 4K", null));
    }
}
