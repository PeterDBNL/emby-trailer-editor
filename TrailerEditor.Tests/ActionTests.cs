using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using TrailerEditor;
using TrailerEditor.Actions;
using TrailerEditor.Configuration;
using TrailerEditor.Search;
using Xunit;

public class ActionTests
{
    private const string Official = "DuneOff2021";
    private static string Url(string id) => "https://www.youtube.com/watch?v=" + id;

    private readonly TestEnv _env = new TestEnv();
    private readonly FakeHttp _http = new FakeHttp();
    private readonly FakeChecker _checker = new FakeChecker();
    private readonly RejectedStore _rejected;
    private readonly TrailerManager _manager;
    private readonly ActionLog _log;
    private readonly TrailerActions _actions;
    private readonly System.Collections.Generic.HashSet<string> _broken = new System.Collections.Generic.HashSet<string>();

    public ActionTests()
    {
        _rejected = _env.NewRejectedStore();
        _manager = _env.NewManager(_rejected);
        _log = _env.NewActionLog();
        _http.Handler = url => url.Contains("youtube.com/results") ? TestEnv.Fixture("youtube-results.html") : throw new InvalidOperationException(url);
        var finder = new TrailerFinder(_http, () => _checker, _rejected, id => _broken.Contains(id), _env.Logger);
        _actions = new TrailerActions(_manager, finder, _log, () => _env.Plugin.Configuration, () => "nl", id => _broken.Contains(id), _env.Logger);
    }

    private Movie NewMovie(params string[] trailers) =>
        new Movie { Name = "Dune", InternalId = 42, Id = Guid.NewGuid(), ProductionYear = 2021, RemoteTrailers = trailers };

    private string Key(BaseItem item) => TrailerManager.Key(item);

    // ---- Finding ----

    [Fact]
    public async Task FindAndApplyPutsTheBestTrailerFirstPinsItAndLogsIt()
    {
        var movie = NewMovie(Url("EXISTINGAAA"), Url("EXISTINGBBB"), Url("EXISTINGCCC"));

        var result = await _actions.FindAsync(movie, true, ActionSources.Collection, CancellationToken.None);

        Assert.True(result.Found);
        Assert.True(result.Applied);
        Assert.Equal(new[] { Url(Official), Url("EXISTINGAAA"), Url("EXISTINGBBB") }, movie.RemoteTrailers);
        Assert.Equal(movie.RemoteTrailers, result.TrailerUrls);
        Assert.True(_manager.IsPinned(movie));
        Assert.Equal(movie.RemoteTrailers, _manager.GetOverride(movie).Urls);

        var entry = _log.Recent(5).Single();
        Assert.Equal("find", entry.Action);
        Assert.Equal("collection", entry.Source);
        Assert.Equal("ok", entry.Outcome);
        Assert.Equal(Url(Official), entry.ChosenUrl);
        Assert.Equal("42", entry.ItemId);
        Assert.Equal(2021, entry.Year);
        Assert.Equal("Dune", entry.ItemName);
    }

    [Fact]
    public async Task ApplyDropsKnownBrokenLinksAndRemembersThem()
    {
        var movie = NewMovie(Url("BROKENAAAAA"), Url("GOODAAAAAAA"));
        _broken.Add("BROKENAAAAA");

        await _actions.FindAsync(movie, true, ActionSources.Api, CancellationToken.None);

        Assert.Equal(new[] { Url(Official), Url("GOODAAAAAAA") }, movie.RemoteTrailers);
        Assert.True(_rejected.IsRejected(Key(movie), "BROKENAAAAA"));
        Assert.Equal("broken", _rejected.Get(Key(movie)).Single().Reason);
    }

    [Fact]
    public async Task ApplyDoesNotDuplicateALinkTheItemAlreadyHas()
    {
        var movie = NewMovie("https://youtu.be/" + Official);
        await _actions.FindAsync(movie, true, ActionSources.Api, CancellationToken.None);
        Assert.Equal(new[] { Url(Official) }, movie.RemoteTrailers);
    }

    [Fact]
    public async Task WithoutApplyNothingChangesAndNothingIsLogged()
    {
        var movie = NewMovie(Url("EXISTINGAAA"));
        var result = await _actions.FindAsync(movie, false, ActionSources.Editor, CancellationToken.None);

        Assert.True(result.Found);
        Assert.False(result.Applied);
        Assert.Equal(new[] { Url("EXISTINGAAA") }, movie.RemoteTrailers);
        Assert.False(_manager.IsPinned(movie));
        Assert.Empty(_log.Recent(5));
    }

    [Fact]
    public async Task NoMatchLeavesTheItemAloneAndIsLogged()
    {
        var movie = NewMovie(Url("EXISTINGAAA"));
        movie.Name = "Some Unknown Film";

        var result = await _actions.FindAsync(movie, true, ActionSources.Collection, CancellationToken.None);

        Assert.False(result.Found);
        Assert.Equal(new[] { Url("EXISTINGAAA") }, movie.RemoteTrailers);
        Assert.Equal("no match", _log.Recent(1).Single().Outcome);
    }

    [Fact]
    public async Task AFailingSearchIsLoggedAsFailed()
    {
        _http.Handler = url => throw new InvalidOperationException("boom");
        var movie = NewMovie();
        // The finder swallows network errors; make the checker blow up instead.
        _http.Handler = url => TestEnv.Fixture("youtube-results.html");
        _checker.Answer = url => throw new InvalidOperationException("checker exploded");

        var result = await _actions.FindAsync(movie, true, ActionSources.Collection, CancellationToken.None);

        Assert.False(result.Found);
        Assert.Contains("failed", result.Message);
        Assert.Equal("failed", _log.Recent(1).Single().Outcome);
        Assert.Empty(movie.RemoteTrailers);
    }

    [Fact]
    public void BuildsTheSearchRequestFromTheItem()
    {
        _env.Plugin.Configuration.TmdbApiKey = "  mykey ";
        var movie = NewMovie();
        movie.OriginalTitle = "Dune Original";
        movie.SetProviderId(MetadataProviders.Tmdb, "438631");
        movie.SetProviderId(MetadataProviders.Imdb, "tt1160419");

        var r = _actions.BuildRequest(movie);

        Assert.Equal("Dune", r.Name);
        Assert.Equal("Dune Original", r.OriginalTitle);
        Assert.Equal(2021, r.Year);
        Assert.False(r.IsSeries);
        Assert.Equal("438631", r.TmdbId);
        Assert.Equal("tt1160419", r.ImdbId);
        Assert.Equal("nl", r.Language);
        Assert.Equal("mykey", r.TmdbApiKey);
        Assert.True(_actions.BuildRequest(new Series { Name = "X", Id = Guid.NewGuid() }).IsSeries);
    }

    // ---- Deleting ----

    [Fact]
    public async Task DeleteRejectsAllLinksClearsThemAndUnpins()
    {
        var movie = NewMovie(Url(Official), "https://youtu.be/OTHERAAAAAA");
        _manager.SetTrailers(movie, movie.RemoteTrailers, true);
        Assert.True(_manager.IsPinned(movie));

        var result = _actions.Delete(movie, ActionSources.Collection);

        Assert.True(result.Ok);
        Assert.Equal(2, result.Removed);
        Assert.Empty(movie.RemoteTrailers);
        Assert.False(_manager.IsPinned(movie));
        Assert.True(_rejected.IsRejected(Key(movie), Official));
        Assert.True(_rejected.IsRejected(Key(movie), "OTHERAAAAAA"));
        Assert.All(_rejected.Get(Key(movie)), l => Assert.Equal("deleted", l.Reason));
        var entry = _log.Recent(1).Single();
        Assert.Equal("delete", entry.Action);
        Assert.Equal("ok", entry.Outcome);

        // And a following search must not bring the deleted trailer back.
        var found = await _actions.FindAsync(movie, true, ActionSources.Collection, CancellationToken.None);
        Assert.True(found.Found);
        Assert.NotEqual(Official, found.Chosen.VideoId);
        Assert.DoesNotContain(movie.RemoteTrailers, u => u.EndsWith(Official));
    }

    [Fact]
    public void DeletingWhenThereAreNoTrailersSaysSo()
    {
        var result = _actions.Delete(NewMovie(), ActionSources.Api);
        Assert.True(result.Ok);
        Assert.Equal(0, result.Removed);
        Assert.Contains("no trailer", result.Message);
    }

    [Fact]
    public async Task RunAsyncDispatchesByActionName()
    {
        var movie = NewMovie(Url("EXISTINGAAA"));
        await _actions.RunAsync("find", movie, ActionSources.Collection, CancellationToken.None);
        Assert.Equal(Url(Official), movie.RemoteTrailers[0]);
        await _actions.RunAsync("delete", movie, ActionSources.Collection, CancellationToken.None);
        Assert.Empty(movie.RemoteTrailers);
        Assert.Equal(new[] { "delete", "find" }, _log.Recent(5).Select(r => r.Action).ToArray());
    }

    // ---- Enforcement when an item is updated ----

    [Fact]
    public void ARefreshThatBringsBackARejectedLinkIsUndone()
    {
        var movie = NewMovie(Url("KEEPAAAAAAA"), Url("REJECTEDAAA"));
        _rejected.Reject(Key(movie), movie.Name, new[] { Url("REJECTEDAAA") }, "deleted");

        Assert.True(_manager.ReapplyIfChanged(movie));
        Assert.Equal(new[] { Url("KEEPAAAAAAA") }, movie.RemoteTrailers);
        Assert.False(_manager.ReapplyIfChanged(movie)); // nothing left to do
    }

    [Fact]
    public void AllLinksRejectedMeansNoLinks()
    {
        var movie = NewMovie("https://youtu.be/REJECTEDAAA");
        _rejected.Reject(Key(movie), movie.Name, new[] { Url("REJECTEDAAA") }, "deleted");
        Assert.True(_manager.ReapplyIfChanged(movie));
        Assert.Empty(movie.RemoteTrailers);
    }

    [Fact]
    public void ARejectedLinkIsRemovedEvenWhenPinningIsOff()
    {
        _env.Plugin.Configuration.PinEditedTrailers = false;
        var movie = NewMovie(Url("REJECTEDAAA"), Url("KEEPAAAAAAA"));
        _rejected.Reject(Key(movie), movie.Name, new[] { Url("REJECTEDAAA") }, "deleted");
        Assert.True(_manager.ReapplyIfChanged(movie));
        Assert.Equal(new[] { Url("KEEPAAAAAAA") }, movie.RemoteTrailers);
    }

    [Fact]
    public void ApinCannotResurrectARejectedLink()
    {
        var movie = NewMovie(Url("KEEPAAAAAAA"), Url("REJECTEDAAA"));
        _manager.SetTrailers(movie, movie.RemoteTrailers, true);
        _rejected.Reject(Key(movie), movie.Name, new[] { Url("REJECTEDAAA") }, "deleted");

        // A refresh replaced the links with something else.
        movie.RemoteTrailers = new[] { Url("FROMREFRESHA") };

        Assert.True(_manager.ReapplyIfChanged(movie));
        Assert.Equal(new[] { Url("KEEPAAAAAAA") }, movie.RemoteTrailers);
    }

    [Fact]
    public void PinsOfItemsWithoutRejectedLinksStillWork()
    {
        var movie = NewMovie(Url("KEEPAAAAAAA"));
        _manager.SetTrailers(movie, movie.RemoteTrailers, true);
        movie.RemoteTrailers = new[] { Url("FROMREFRESHA") };
        Assert.True(_manager.ReapplyIfChanged(movie));
        Assert.Equal(new[] { Url("KEEPAAAAAAA") }, movie.RemoteTrailers);
    }

    // ---- Editor saves ----

    [Fact]
    public void SavingFromTheEditorRejectsRemovedLinksAndAllowsReaddedOnes()
    {
        var movie = NewMovie("https://youtu.be/AAAAAAAAAAA", Url("BBBBBBBBBBB"));
        _rejected.Reject(Key(movie), movie.Name, new[] { Url("CCCCCCCCCCC") }, "deleted");

        var saved = _manager.SaveEdited(movie, new[] { Url("BBBBBBBBBBB"), Url("CCCCCCCCCCC"), "https://example.com/trailer.mp4" }, true);

        Assert.Equal(new[] { Url("BBBBBBBBBBB"), Url("CCCCCCCCCCC"), "https://example.com/trailer.mp4" }, saved);
        Assert.True(_rejected.IsRejected(Key(movie), "AAAAAAAAAAA"));
        Assert.Equal("removed in editor", _rejected.Get(Key(movie)).Single().Reason);
        Assert.False(_rejected.IsRejected(Key(movie), "BBBBBBBBBBB"));
        Assert.False(_rejected.IsRejected(Key(movie), "CCCCCCCCCCC")); // saving it again is an explicit choice
        Assert.True(_manager.IsPinned(movie));
    }

    [Fact]
    public void ClearingAllLinksInTheEditorRejectsThemAll()
    {
        var movie = NewMovie(Url("AAAAAAAAAAA"), Url("BBBBBBBBBBB"));
        _manager.SaveEdited(movie, new string[0], true);
        Assert.Empty(movie.RemoteTrailers);
        Assert.Equal(2, _rejected.Get(Key(movie)).Count);
    }

    [Fact]
    public void AnInvalidLinkInTheEditorChangesNothing()
    {
        var movie = NewMovie(Url("AAAAAAAAAAA"));
        Assert.Throws<ArgumentException>(() => _manager.SaveEdited(movie, new[] { "not a link" }, true));
        Assert.Equal(new[] { Url("AAAAAAAAAAA") }, movie.RemoteTrailers);
        Assert.Empty(_rejected.Get(Key(movie)));
    }
}
