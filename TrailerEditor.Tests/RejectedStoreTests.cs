using System.Linq;
using TrailerEditor.Actions;
using Xunit;

public class RejectedStoreTests
{
    private readonly TestEnv _env = new TestEnv();

    [Fact]
    public void RejectsByVideoIdWhateverTheLinkLooksLike()
    {
        var store = _env.NewRejectedStore();
        var added = store.Reject("item", "Dune", new[] { "https://youtu.be/AAAAAAAAAAA", "https://www.youtube.com/watch?v=AAAAAAAAAAA&t=5", "BBBBBBBBBBB", "https://vimeo.com/123", "nonsense" }, "deleted");

        Assert.Equal(2, added);
        Assert.True(store.IsRejected("item", "AAAAAAAAAAA"));
        Assert.True(store.IsRejectedUrl("item", "https://www.youtube.com/embed/BBBBBBBBBBB"));
        Assert.False(store.IsRejected("other", "AAAAAAAAAAA"));
        Assert.False(store.IsRejectedUrl("item", "https://vimeo.com/123"));
        Assert.False(store.IsRejected("item", null));
        var links = store.Get("item");
        Assert.Equal(new[] { "AAAAAAAAAAA", "BBBBBBBBBBB" }, links.Select(l => l.VideoId).OrderBy(x => x).ToArray());
        Assert.All(links, l => Assert.Equal("deleted", l.Reason));
        Assert.Equal("https://www.youtube.com/watch?v=AAAAAAAAAAA", links.Single(l => l.VideoId == "AAAAAAAAAAA").Url);
    }

    [Fact]
    public void IsSavedToDiskAndReadBackByANewInstance()
    {
        var store = _env.NewRejectedStore();
        store.Reject("item", "Dune", new[] { "AAAAAAAAAAA" }, RejectedStore.ReasonRemovedInEditor);
        store.Reject("item2", "Other", new[] { "CCCCCCCCCCC" }, RejectedStore.ReasonDeleted);

        Assert.True(System.IO.File.Exists(System.IO.Path.Combine(_env.Dir, "TrailerEditor.rejected.json")));
        var again = _env.NewRejectedStore();
        Assert.True(again.IsRejected("item", "AAAAAAAAAAA"));
        Assert.Equal("removed in editor", again.Get("item").Single().Reason);
        Assert.True(again.IsRejected("item2", "CCCCCCCCCCC"));
    }

    [Fact]
    public void AllowTakesAVideoOffTheListAndIsSaved()
    {
        var store = _env.NewRejectedStore();
        store.Reject("item", "Dune", new[] { "AAAAAAAAAAA", "BBBBBBBBBBB" }, "deleted");
        Assert.True(store.Allow("item", "AAAAAAAAAAA"));
        Assert.False(store.Allow("item", "AAAAAAAAAAA"));
        Assert.False(store.Allow("nobody", "AAAAAAAAAAA"));

        var again = _env.NewRejectedStore();
        Assert.False(again.IsRejected("item", "AAAAAAAAAAA"));
        Assert.True(again.IsRejected("item", "BBBBBBBBBBB"));
        again.Allow("item", "BBBBBBBBBBB");
        Assert.Empty(_env.NewRejectedStore().Get("item"));
    }

    [Fact]
    public void RejectingTheSameVideoTwiceKeepsOneEntry()
    {
        var store = _env.NewRejectedStore();
        store.Reject("item", "Dune", new[] { "AAAAAAAAAAA" }, "deleted");
        Assert.Equal(0, store.Reject("item", "Dune", new[] { "https://youtu.be/AAAAAAAAAAA" }, "removed in editor"));
        Assert.Single(store.Get("item"));
    }

    [Fact]
    public void ActionLogKeepsTheLast200NewestFirstAndSurvivesARestart()
    {
        var log = _env.NewActionLog();
        for (var i = 0; i < 230; i++)
        {
            log.Add(new ActionRecord { ItemName = "T" + i, Action = "find", Outcome = "ok" });
        }

        var recent = log.Recent(500);
        Assert.Equal(200, recent.Count);
        Assert.Equal("T229", recent[0].ItemName);
        Assert.Equal("T30", recent.Last().ItemName);
        Assert.Equal(5, log.Recent(5).Count);

        var again = _env.NewActionLog().Recent(500);
        Assert.Equal(200, again.Count);
        Assert.Equal("T229", again[0].ItemName);
        Assert.NotEqual(default(System.DateTime), again[0].TimeUtc);
    }
}
