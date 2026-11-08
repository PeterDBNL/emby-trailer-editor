using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Moq;
using TrailerEditor.Actions;
using Xunit;

public class CollectionActionsTests
{
    private class RecordingActions : IItemActions
    {
        public List<(string Action, BaseItem Item, string Source)> Calls { get; } = new List<(string, BaseItem, string)>();
        public Func<string, BaseItem, Task> Behaviour { get; set; } = (a, i) => Task.CompletedTask;

        public async Task RunAsync(string action, BaseItem item, string source, CancellationToken ct)
        {
            Calls.Add((action, item, source));
            await Behaviour(action, item);
        }
    }

    private readonly TestEnv _env = new TestEnv();
    private readonly Mock<ICollectionManager> _collections = new Mock<ICollectionManager>();
    private readonly RecordingActions _actions = new RecordingActions();
    private readonly Dictionary<long, BaseItem> _items = new Dictionary<long, BaseItem>();
    private readonly List<BoxSet> _existing = new List<BoxSet>();
    private readonly Dictionary<long, List<long>> _members = new Dictionary<long, List<long>>();
    private bool _enabled = true;
    private long _nextId = 1000;

    private readonly BoxSet _findBox = new BoxSet { Name = CollectionActions.FindName, InternalId = 500 };
    private readonly BoxSet _deleteBox = new BoxSet { Name = CollectionActions.DeleteName, InternalId = 501 };

    public CollectionActionsTests()
    {
        _env.Library.Setup(l => l.GetItemById(It.IsAny<long>())).Returns<long>(id => _items.TryGetValue(id, out var item) ? item : null);
        _env.Library.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns<InternalItemsQuery>(q =>
        {
            if (q.CollectionIds != null && q.CollectionIds.Length > 0)
            {
                return _members.TryGetValue(q.CollectionIds[0], out var ids)
                    ? ids.Select(id => _items[id]).ToArray()
                    : new BaseItem[0];
            }

            return _existing.Where(b => q.Name == null || b.Name == q.Name).Cast<BaseItem>().ToArray();
        });
        _collections.Setup(c => c.CreateCollection(It.IsAny<CollectionCreationOptions>())).Returns<CollectionCreationOptions>(o =>
        {
            var box = o.Name == CollectionActions.FindName ? _findBox : _deleteBox;
            Register(box);
            return Task.FromResult(box);
        });
        _collections.Setup(c => c.RemoveFromCollection(It.IsAny<BoxSet>(), It.IsAny<long[]>())).Callback<BoxSet, long[]>((box, ids) =>
        {
            if (_members.TryGetValue(box.InternalId, out var list))
            {
                list.RemoveAll(ids.Contains);
            }
        });
    }

    private CollectionActions NewSubject() =>
        new CollectionActions(_collections.Object, _env.Library.Object, _actions, () => _enabled, _env.Logger, TimeSpan.Zero, TimeSpan.FromMilliseconds(50));

    private void Register(BoxSet box)
    {
        _items[box.InternalId] = box;
        _members[box.InternalId] = new List<long>();
    }

    private T Add<T>(T item) where T : BaseItem
    {
        item.InternalId = _nextId++;
        item.Id = Guid.NewGuid();
        _items[item.InternalId] = item;
        return item;
    }

    private void AddTo(BoxSet box, BaseItem item) => _members[box.InternalId].Add(item.InternalId);

    private void Raise(BoxSet box, params BaseItem[] items)
    {
        foreach (var item in items)
        {
            AddTo(box, item);
        }

        _collections.Raise(c => c.ItemsAddedToCollection += null, _collections.Object,
            new CollectionModifiedEventArgs { Collection = box, ItemsChanged = items.Select(i => i.InternalId).ToList() });
    }

    [Fact]
    public async Task CreatesBothCollectionsWhenTheyDontExist()
    {
        var subject = NewSubject();
        Assert.True(await subject.EnsureCollectionsAsync());

        _collections.Verify(c => c.CreateCollection(It.Is<CollectionCreationOptions>(o => o.Name == "Trailer Editor: Find trailer")), Times.Once);
        _collections.Verify(c => c.CreateCollection(It.Is<CollectionCreationOptions>(o => o.Name == "Trailer Editor: Delete trailer")), Times.Once);
        var status = subject.GetStatus();
        Assert.True(status.Enabled && status.FindExists && status.DeleteExists);

        // A second call finds them and creates nothing more.
        await subject.EnsureCollectionsAsync();
        _collections.Verify(c => c.CreateCollection(It.IsAny<CollectionCreationOptions>()), Times.Exactly(2));
    }

    [Fact]
    public async Task UsesExistingCollectionsFoundByName()
    {
        _existing.Add(_findBox);
        _existing.Add(_deleteBox);
        Register(_findBox);
        Register(_deleteBox);

        var subject = NewSubject();
        Assert.True(await subject.EnsureCollectionsAsync());

        _collections.Verify(c => c.CreateCollection(It.IsAny<CollectionCreationOptions>()), Times.Never);
        Assert.True(subject.GetStatus().FindExists);
    }

    [Fact]
    public async Task DoesNothingWhenTheSettingIsOff()
    {
        _enabled = false;
        var subject = NewSubject();
        Assert.True(await subject.EnsureCollectionsAsync());
        _collections.Verify(c => c.CreateCollection(It.IsAny<CollectionCreationOptions>()), Times.Never);
        Assert.False(subject.GetStatus().Enabled);
        Assert.Equal(0, subject.PollOnce());

        Register(_findBox);
        subject.Subscribe();
        Raise(_findBox, Add(new Movie { Name = "Dune" }));
        Assert.Equal(0, await subject.ProcessQueueAsync());
        Assert.Empty(_actions.Calls);
    }

    [Fact]
    public async Task AddingAMovieQueuesTheActionAndRemovesItFromTheCollection()
    {
        Register(_findBox);
        Register(_deleteBox);
        var movie = Add(new Movie { Name = "Dune" });
        var subject = NewSubject();
        subject.Subscribe();

        Raise(_findBox, movie);
        Assert.Empty(_actions.Calls); // queued, not run on the event thread
        Assert.Equal(1, await subject.ProcessQueueAsync());

        Assert.Equal(new[] { ("find", (BaseItem)movie, "collection") }, _actions.Calls.ToArray());
        _collections.Verify(c => c.RemoveFromCollection(_findBox, It.Is<long[]>(ids => ids.SequenceEqual(new[] { movie.InternalId }))), Times.Once);
        Assert.Empty(_members[_findBox.InternalId]);
    }

    [Fact]
    public async Task TheDeleteCollectionRunsTheDeleteAction()
    {
        Register(_findBox);
        Register(_deleteBox);
        var show = Add(new Series { Name = "Show" });
        var subject = NewSubject();
        subject.Subscribe();

        Raise(_deleteBox, show);
        await subject.ProcessQueueAsync();

        Assert.Equal("delete", _actions.Calls.Single().Action);
        Assert.Same(show, _actions.Calls.Single().Item);
    }

    [Fact]
    public async Task SeasonsAndEpisodesActOnTheirSeries()
    {
        Register(_findBox);
        var series = Add(new Series { Name = "Show" });
        var season = Add(new Season { Name = "Season 1" });
        var episode = Add(new Episode { Name = "Pilot" });
        // Parents are looked up through the library manager.
        MediaBrowser.Controller.Entities.BaseItem.LibraryManager = _env.Library.Object;
        season.ParentId = series.InternalId;
        episode.ParentId = season.InternalId;
        episode.SeriesId = series.InternalId;
        var subject = NewSubject();
        subject.Subscribe();

        Raise(_findBox, season, episode);
        await subject.ProcessQueueAsync();

        Assert.Equal(2, _actions.Calls.Count);
        Assert.All(_actions.Calls, c => Assert.Same(series, c.Item));
        _collections.Verify(c => c.RemoveFromCollection(_findBox, It.Is<long[]>(ids => ids.Single() == season.InternalId)), Times.Once);
        _collections.Verify(c => c.RemoveFromCollection(_findBox, It.Is<long[]>(ids => ids.Single() == episode.InternalId)), Times.Once);
    }

    [Fact]
    public async Task OtherKindsOfItemsAreRemovedAndIgnored()
    {
        Register(_findBox);
        var photo = Add(new MediaBrowser.Controller.Entities.Audio.Audio { Name = "Song" });
        var subject = NewSubject();
        subject.Subscribe();

        Raise(_findBox, photo);
        await subject.ProcessQueueAsync();

        Assert.Empty(_actions.Calls);
        _collections.Verify(c => c.RemoveFromCollection(_findBox, It.IsAny<long[]>()), Times.Once);
    }

    [Fact]
    public async Task PollingFindsTitlesWhoseEventWasMissed()
    {
        Register(_findBox);
        Register(_deleteBox);
        var subject = NewSubject();
        await subject.EnsureCollectionsAsync(); // remembers the ids (both exist already in the fake)
        var movie = Add(new Movie { Name = "Dune" });
        var other = Add(new Movie { Name = "Heat" });
        AddTo(_findBox, movie);
        AddTo(_deleteBox, other);

        Assert.Equal(2, subject.PollOnce());
        // A second poll before processing does not queue them twice.
        subject.PollOnce();
        Assert.Equal(2, await subject.ProcessQueueAsync());

        Assert.Equal(new[] { "delete", "find" }, _actions.Calls.Select(c => c.Action).OrderBy(x => x).ToArray());
        Assert.Empty(_members[_findBox.InternalId]);
        Assert.Empty(_members[_deleteBox.InternalId]);
    }

    [Fact]
    public async Task TheSameTitleAddedTwiceIsOnlyHandledOnce()
    {
        Register(_findBox);
        var movie = Add(new Movie { Name = "Dune" });
        var subject = NewSubject();
        subject.Subscribe();

        Raise(_findBox, movie);
        Raise(_findBox, movie);
        Assert.Equal(1, await subject.ProcessQueueAsync());
        Assert.Single(_actions.Calls);
    }

    [Fact]
    public async Task ItemsAreHandledOneAfterTheOtherAndAFailureDoesNotStopTheRest()
    {
        Register(_findBox);
        var a = Add(new Movie { Name = "A" });
        var b = Add(new Movie { Name = "B" });
        var running = 0;
        var maxRunning = 0;
        _actions.Behaviour = async (action, item) =>
        {
            maxRunning = Math.Max(maxRunning, Interlocked.Increment(ref running));
            await Task.Delay(20);
            Interlocked.Decrement(ref running);
            if (item == a)
            {
                throw new InvalidOperationException("boom");
            }
        };
        var subject = NewSubject();
        subject.Subscribe();

        Raise(_findBox, a, b);
        Assert.Equal(2, await subject.ProcessQueueAsync());

        Assert.Equal(1, maxRunning);
        Assert.Equal(2, _actions.Calls.Count);
    }

    [Fact]
    public async Task TheBackgroundLoopCreatesCollectionsAndHandlesEvents()
    {
        var movie = Add(new Movie { Name = "Dune" });
        var subject = NewSubject();
        subject.Start();
        try
        {
            for (var i = 0; i < 100 && !subject.GetStatus().FindExists; i++)
            {
                await Task.Delay(20);
            }

            Assert.True(subject.GetStatus().FindExists);
            Raise(_findBox, movie);
            for (var i = 0; i < 100 && _actions.Calls.Count == 0; i++)
            {
                await Task.Delay(20);
            }

            Assert.Single(_actions.Calls);
        }
        finally
        {
            subject.Dispose();
        }
    }
}
