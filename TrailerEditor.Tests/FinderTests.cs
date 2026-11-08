using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TrailerEditor;
using TrailerEditor.Actions;
using TrailerEditor.Search;
using Xunit;

public class FinderTests
{
    private const string Official = "DuneOff2021";
    private static readonly string Html = TestEnv.Fixture("youtube-results.html");

    private readonly TestEnv _env = new TestEnv();
    private readonly FakeHttp _http = new FakeHttp();
    private readonly FakeChecker _checker = new FakeChecker();
    private readonly RejectedStore _rejected;
    private readonly System.Collections.Generic.HashSet<string> _broken = new System.Collections.Generic.HashSet<string>();

    public FinderTests()
    {
        _rejected = _env.NewRejectedStore();
        _http.Handler = url => url.Contains("youtube.com/results") ? Html : throw new InvalidOperationException("unexpected " + url);
    }

    private TrailerFinder NewFinder() => new TrailerFinder(_http, () => _checker, _rejected, id => _broken.Contains(id), _env.Logger);

    private static FindRequest Dune(string tmdbKey = null) =>
        new FindRequest { ItemKey = "item1", Name = "Dune", Year = 2021, Language = "nl", TmdbApiKey = tmdbKey };

    [Fact]
    public async Task PicksTheOfficialTrailerFromYouTubeSearch()
    {
        var result = await NewFinder().FindAsync(Dune(), CancellationToken.None);

        Assert.True(result.Found);
        Assert.Equal(Official, result.Chosen.VideoId);
        Assert.Equal("youtube", result.Chosen.Source);
        Assert.Equal(Official, result.Candidates[0].VideoId);
        Assert.InRange(result.Candidates.Count, 2, TrailerFinder.MaxCandidatesShown);
        Assert.All(result.Candidates, c => Assert.True(c.Score >= TrailerScorer.MinimumScore));
        Assert.DoesNotContain(result.Candidates, c => c.VideoId == "DuneReact01" || c.VideoId == "DuneFan0001" || c.VideoId == "DuneFull001" || c.VideoId == "DuneHonest1");
        Assert.Equal(new[] { "https://www.youtube.com/watch?v=" + Official }, _checker.Calls.ToArray());
        Assert.Contains("hl=en&gl=US", _http.Requests[0]);
        Assert.Contains("search_query=Dune%202021%20trailer", _http.Requests[0]);
        Assert.Single(_http.Requests);
        Assert.Contains("Best match", result.Message);
    }

    [Fact]
    public async Task SearchesTheOriginalTitleToo()
    {
        var req = Dune();
        req.Name = "De Duinplaneet";
        req.OriginalTitle = "Dune";
        var result = await NewFinder().FindAsync(req, CancellationToken.None);
        Assert.Equal(2, _http.Requests.Count);
        Assert.Contains("De%20Duinplaneet", _http.Requests[0]);
        Assert.Contains("search_query=Dune%202021%20trailer", _http.Requests[1]);
        Assert.Equal(Official, result.Chosen.VideoId);
    }

    [Fact]
    public async Task SkipsBrokenLinksAndTakesTheNextBestOne()
    {
        _checker.Answer = url => url.EndsWith(Official)
            ? new LinkCheckResult { Status = YouTubeChecker.Error, Message = "Embedding disabled" }
            : new LinkCheckResult { Status = YouTubeChecker.Ok };

        var result = await NewFinder().FindAsync(Dune(), CancellationToken.None);

        Assert.True(result.Found);
        Assert.NotEqual(Official, result.Chosen.VideoId);
        Assert.Equal(2, _checker.Calls.Count);
        Assert.DoesNotContain(result.Candidates, c => c.VideoId == Official);
        Assert.Equal(YouTubeChecker.Ok, result.Chosen.Status);
    }

    [Fact]
    public async Task NeverOffersRejectedOrKnownBrokenVideos()
    {
        _rejected.Reject("item1", "Dune", new[] { "https://youtu.be/" + Official }, RejectedStore.ReasonDeleted);
        _broken.Add("DunePart2Tr");

        var result = await NewFinder().FindAsync(Dune(), CancellationToken.None);

        Assert.True(result.Found);
        Assert.DoesNotContain(result.Candidates, c => c.VideoId == Official || c.VideoId == "DunePart2Tr");
        Assert.DoesNotContain(_checker.Calls, u => u.EndsWith(Official) || u.EndsWith("DunePart2Tr"));

        // Rejected for another item only, so it is fine here.
        var other = Dune();
        other.ItemKey = "item2";
        Assert.Equal(Official, (await NewFinder().FindAsync(other, CancellationToken.None)).Chosen.VideoId);
    }

    [Fact]
    public async Task PrefersAVideoThatPlaysOverOneThatIsUnclear()
    {
        var first = true;
        _checker.Answer = url =>
        {
            var unknown = first;
            first = false;
            return new LinkCheckResult { Status = unknown ? YouTubeChecker.Unknown : YouTubeChecker.Ok };
        };

        var result = await NewFinder().FindAsync(Dune(), CancellationToken.None);
        Assert.NotEqual(Official, result.Chosen.VideoId);
        Assert.Equal(YouTubeChecker.Ok, result.Chosen.Status);
        Assert.Equal(result.Chosen.VideoId, result.Candidates[0].VideoId);

        _checker.Answer = url => new LinkCheckResult { Status = YouTubeChecker.Unknown, RateLimited = true };
        result = await NewFinder().FindAsync(Dune(), CancellationToken.None);
        Assert.Equal(Official, result.Chosen.VideoId); // unclear is acceptable when nothing better is known
    }

    [Fact]
    public async Task ReportsWhenNothingFitsOrYouTubeCantBeRead()
    {
        _http.Handler = url => "<html>no data</html>";
        var result = await NewFinder().FindAsync(Dune(), CancellationToken.None);
        Assert.False(result.Found);
        Assert.Contains("layout", result.Message);

        _http.Handler = url => throw new System.Net.Http.HttpRequestException("HTTP 429");
        result = await NewFinder().FindAsync(Dune(), CancellationToken.None);
        Assert.False(result.Found);
        Assert.Contains("couldn't be read", result.Message);

        _http.Handler = url => Html;
        var other = Dune();
        other.Name = "Totally Different Film";
        Assert.False((await NewFinder().FindAsync(other, CancellationToken.None)).Found);

        _checker.Answer = url => new LinkCheckResult { Status = YouTubeChecker.Error };
        result = await NewFinder().FindAsync(Dune(), CancellationToken.None);
        Assert.False(result.Found);
        Assert.Contains("unavailable", result.Message);
        Assert.Empty(result.Candidates.Where(c => c.Status == YouTubeChecker.Error));
    }

    private const string TmdbVideos = @"{""id"":438631,""results"":[
        {""iso_639_1"":""nl"",""iso_3166_1"":""NL"",""name"":""Officiele trailer"",""key"":""TmdbTrail01"",""site"":""YouTube"",""size"":1080,""type"":""Trailer"",""official"":true,""published_at"":""2021-07-22T15:00:11.000Z""},
        {""iso_639_1"":""en"",""name"":""Featurette"",""key"":""TmdbFeat001"",""site"":""YouTube"",""size"":1080,""type"":""Featurette"",""official"":true},
        {""iso_639_1"":""en"",""name"":""Vimeo thing"",""key"":""abc"",""site"":""Vimeo"",""size"":1080,""type"":""Trailer"",""official"":true},
        {""iso_639_1"":""en"",""name"":""Official Trailer"",""key"":""DuneOff2021"",""site"":""YouTube"",""size"":1080,""type"":""Trailer"",""official"":true}]}";

    [Fact]
    public async Task TmdbCandidatesComeFirstAndMergeWithSearchResults()
    {
        _http.Handler = url =>
        {
            if (url.StartsWith("https://api.themoviedb.org/3/movie/438631/videos")) return TmdbVideos;
            return url.Contains("youtube.com/results") ? Html : throw new InvalidOperationException(url);
        };
        var req = Dune("secretkey");
        req.TmdbId = "438631";

        var result = await NewFinder().FindAsync(req, CancellationToken.None);

        Assert.True(result.Found);
        var tmdbUrl = _http.Requests.First(u => u.Contains("themoviedb"));
        Assert.Contains("api_key=secretkey", tmdbUrl);
        Assert.Contains("include_video_language=nl%2Cen%2Cnull", tmdbUrl);
        Assert.DoesNotContain(_http.Requests, u => u.Contains("/search/"));
        // The video TMDb and YouTube both list has the most evidence; the Dutch TMDb-only one is the runner-up.
        Assert.Equal(Official, result.Chosen.VideoId);
        Assert.Equal("tmdb", result.Chosen.Source);
        Assert.Equal("TmdbTrail01", result.Candidates[1].VideoId);
        Assert.DoesNotContain(result.Candidates, c => c.VideoId == "TmdbFeat001" || c.VideoId == "abc");

        // Same video from TMDb and the search page is one candidate with both kinds of information.
        var merged = result.Candidates.Single(c => c.VideoId == Official);
        Assert.True(merged.InTmdb && merged.InSearch);
        Assert.Equal("Warner Bros. Pictures", merged.Channel);
        Assert.Equal(1, result.Candidates.Count(c => c.VideoId == Official));
    }

    [Fact]
    public async Task LooksUpTheTmdbIdWhenTheItemHasNone()
    {
        _http.Handler = url =>
        {
            if (url.Contains("/search/tv")) return @"{""results"":[{""id"":1399}]}";
            if (url.Contains("/tv/1399/videos")) return @"{""results"":[{""iso_639_1"":""en"",""name"":""Official Trailer"",""key"":""TmdbShow001"",""site"":""YouTube"",""size"":1080,""type"":""Trailer"",""official"":true}]}";
            return url.Contains("youtube.com/results") ? "<html></html>" : throw new InvalidOperationException(url);
        };
        var req = new FindRequest { ItemKey = "s", Name = "Game of Thrones", Year = 2011, IsSeries = true, TmdbApiKey = "k", Language = "en" };

        var result = await NewFinder().FindAsync(req, CancellationToken.None);

        Assert.Equal("TmdbShow001", result.Chosen.VideoId);
        var search = _http.Requests.First(u => u.Contains("/search/tv"));
        Assert.Contains("first_air_date_year=2011", search);
        Assert.Contains("query=Game%20of%20Thrones", search);
    }

    [Fact]
    public async Task UsesImdbIdWhenThereIsNoTmdbId()
    {
        _http.Handler = url =>
        {
            if (url.Contains("/find/tt1160419")) return @"{""movie_results"":[{""id"":438631}],""tv_results"":[]}";
            if (url.Contains("/movie/438631/videos")) return TmdbVideos.Replace("DuneOff2021", "TmdbOther01");
            return Html;
        };
        var req = Dune("k");
        req.ImdbId = "tt1160419";
        var result = await NewFinder().FindAsync(req, CancellationToken.None);
        Assert.Contains(result.Candidates, c => c.VideoId == "TmdbTrail01");
        Assert.DoesNotContain(_http.Requests, u => u.Contains("/search/"));
    }

    [Fact]
    public async Task StillWorksWhenTmdbFailsAndHidesTheKeyFromMessages()
    {
        _http.Handler = url => url.Contains("themoviedb") ? throw new System.Net.Http.HttpRequestException("HTTP 401") : Html;
        var result = await NewFinder().FindAsync(Dune("secretkey"), CancellationToken.None);
        Assert.True(result.Found);
        Assert.Equal(Official, result.Chosen.VideoId);

        _http.Handler = url => url.Contains("themoviedb") ? throw new System.Net.Http.HttpRequestException("HTTP 401") : "<html></html>";
        result = await NewFinder().FindAsync(Dune("secretkey"), CancellationToken.None);
        Assert.DoesNotContain("secretkey", result.Message);
        Assert.Contains("TMDb", result.Message);
    }

    [Fact]
    public async Task WithoutAnApiKeyTmdbIsNotContacted()
    {
        await NewFinder().FindAsync(Dune(""), CancellationToken.None);
        Assert.DoesNotContain(_http.Requests, u => u.Contains("themoviedb"));
    }

    [Fact]
    public void ParsesTmdbVideosAndSearchAnswers()
    {
        var videos = TmdbClient.ParseVideos(TmdbVideos);
        Assert.Equal(new[] { "TmdbTrail01", Official }, videos.Select(v => v.VideoId).ToArray());
        Assert.True(videos[0].Official);
        Assert.Equal("nl", videos[0].Language);
        Assert.Equal(2021, videos[0].Published.Value.Year);
        Assert.Null(TmdbClient.ParseSearchResult("{\"results\":[]}"));
        Assert.Equal("5", TmdbClient.ParseSearchResult("{\"results\":[{\"id\":5},{\"id\":6}]}"));
        Assert.Empty(TmdbClient.ParseVideos("not json"));
    }
}
