using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using Moq;
using TrailerEditor;
using TrailerEditor.Scanning;
using Xunit;

public class ScanTaskTests
{
    private const string Ok1 = "https://www.youtube.com/watch?v=okokokokok1";
    private const string Ok2 = "https://youtu.be/okokokokok2";
    private const string Blocked = "https://www.youtube.com/watch?v=blockblock1";

    private class FakeChecker : YouTubeChecker
    {
        public List<string> Calls { get; } = new List<string>();
        public Func<string, LinkCheckResult> Answer { get; set; }

        public override Task<LinkCheckResult> CheckAsync(string input, bool checkWatchPage, CancellationToken cancellationToken)
        {
            Calls.Add(input);
            return Task.FromResult(Answer(input));
        }
    }

    private readonly Mock<ILibraryManager> _library = new Mock<ILibraryManager>();
    private readonly FakeChecker _checker = new FakeChecker();
    private BaseItem[] _items = new BaseItem[0];

    public ScanTaskTests()
    {
        var dir = Path.Combine(Path.GetTempPath(), "te-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(p => p.PluginConfigurationsPath).Returns(dir);
        paths.SetupGet(p => p.PluginsPath).Returns(dir);
        paths.SetupGet(p => p.ConfigurationDirectoryPath).Returns(dir);
        paths.SetupGet(p => p.DataPath).Returns(dir);
        paths.SetupGet(p => p.ProgramDataPath).Returns(dir);

        var logManager = new Mock<ILogManager>();
        logManager.Setup(l => l.GetLogger(It.IsAny<string>())).Returns(new Mock<ILogger>().Object);

        var xml = new Mock<IXmlSerializer>();
        xml.Setup(x => x.DeserializeFromFile(It.IsAny<Type>(), It.IsAny<string>()))
            .Returns(() => new TrailerEditor.Configuration.PluginConfiguration());
        var plugin = new Plugin(paths.Object, xml.Object);
        plugin.SetAttributes(Path.Combine(dir, "TrailerEditor.dll"), dir, new Version(1, 0));
        plugin.Configuration.ScanRequestDelayMs = 0;
        plugin.Configuration.ScanCheckLinks = true;
        plugin.Configuration.ScanReportNoTrailers = true;
        plugin.Configuration.ScanRecheckDays = 30;

        _library.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(() => _items);
        new EntryPoint(_library.Object, logManager.Object, paths.Object, new Mock<IJsonSerializer>().Object, new Mock<MediaBrowser.Controller.Session.ISessionManager>().Object, new Mock<MediaBrowser.Model.Tasks.ITaskManager>().Object, new Mock<MediaBrowser.Controller.Collections.ICollectionManager>().Object, new Mock<MediaBrowser.Controller.Configuration.IServerConfigurationManager>().Object);
        EntryPoint.Checker = _checker;

        _checker.Answer = url => url.Contains("blockblock1")
            ? new LinkCheckResult { Status = YouTubeChecker.Error, Message = "Age-restricted." }
            : new LinkCheckResult { Status = YouTubeChecker.Ok, Message = "No problems found." };

        ScanTask = new TrailerScanTask(_library.Object, logManager.Object);
    }

    private TrailerScanTask ScanTask { get; }

    private static Movie MovieWith(long id, string name, params string[] trailers) =>
        new Movie { Name = name, InternalId = id, ProductionYear = 2000, RemoteTrailers = trailers };

    private static Movie Sorted(long id, string sortName, params string[] trailers)
    {
        var m = MovieWith(id, sortName, trailers);
        SetSortName(m, sortName);
        return m;
    }

    // BaseItem.SortName's setter needs Emby's server configuration, which doesn't exist in tests.
    private static void SetSortName(BaseItem item, string sortName) =>
        typeof(BaseItem).GetField("_sortName", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).SetValue(item, sortName);

    private static Series ShowSorted(long id, string sortName, params string[] trailers) =>
        ShowWithSortName(new Series { Name = sortName, InternalId = id, ProductionYear = 2000, RemoteTrailers = trailers }, sortName);

    private static Series ShowWithSortName(Series show, string sortName)
    {
        SetSortName(show, sortName);
        return show;
    }

    private ScanResults RunScope(string types, string from, string to, CancellationToken token = default)
    {
        Assert.True(TrailerScanTask.TryRequestStart(new ScanScope(types, from, to)));
        return Run(token);
    }

    private static string[] Names(ScanResults r) => r.Issues.Select(i => i.ItemName).OrderBy(x => x).ToArray();

    private ScanResults Run(CancellationToken token = default)
    {
        ScanTask.Execute(token, new Progress<double>()).GetAwaiter().GetResult();
        return EntryPoint.Store.Current;
    }

    [Fact]
    public void FindsEachKindOfProblem()
    {
        _items = new BaseItem[]
        {
            MovieWith(1, "No Trailer"),
            MovieWith(2, "Good", Ok1, "https://vimeo.com/12345"),
            MovieWith(3, "Blocked", Blocked),
            MovieWith(4, "Broken", "not a link"),
            MovieWith(5, "Twice", Ok2, "https://www.youtube.com/watch?v=okokokokok2")
        };

        var r = Run();

        Assert.True(r.Completed);
        Assert.Equal(5, r.ItemsScanned);
        Assert.Equal(6, r.LinksTotal);
        var issues = r.Issues.Select(i => i.ItemName + ":" + i.Kind).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { "Blocked:Unplayable", "Broken:InvalidLink", "No Trailer:NoTrailers", "Twice:DuplicateLink" }, issues);
        Assert.Equal("Age-restricted.", r.Issues.Single(i => i.Kind == IssueKinds.Unplayable).Message);
        Assert.Equal("3", r.Issues.Single(i => i.Kind == IssueKinds.Unplayable).ItemId);

        // Vimeo isn't checked; the duplicate is only checked once.
        Assert.Equal(3, _checker.Calls.Count);
    }

    [Fact]
    public void SecondScanReusesEarlierResults()
    {
        _items = new BaseItem[] { MovieWith(1, "A", Ok1), MovieWith(2, "B", Blocked) };
        Run();
        _checker.Calls.Clear();

        var r = Run();

        Assert.Empty(_checker.Calls);
        Assert.Equal(2, r.LinksFromCache);
        Assert.Single(r.Issues, i => i.Kind == IssueKinds.Unplayable);
    }

    [Fact]
    public void RecheckDaysZeroChecksEverythingAgain()
    {
        Plugin.Instance.Configuration.ScanRecheckDays = 0;
        _items = new BaseItem[] { MovieWith(1, "A", Ok1) };
        Run();
        Run();
        Assert.Equal(2, _checker.Calls.Count);
    }

    [Fact]
    public void StopsCheckingWhenYouTubeKeepsFailing()
    {
        _checker.Answer = url => new LinkCheckResult { Status = YouTubeChecker.Unknown, Message = "Couldn't reach YouTube." };
        _items = Enumerable.Range(1, 40)
            .Select(i => (BaseItem)MovieWith(i, "M" + i, "https://www.youtube.com/watch?v=" + ("vid" + i).PadRight(11, 'x')))
            .ToArray();

        var r = Run();

        Assert.True(r.Completed);
        Assert.Equal(25, _checker.Calls.Count);
        Assert.Equal(40, r.LinksNotChecked);
        Assert.Contains("YouTube stopped answering", r.Note);
        Assert.Empty(r.Issues);
    }

    [Fact]
    public void CancelledScanIsSavedAsIncomplete()
    {
        _items = new BaseItem[] { MovieWith(1, "A", Ok1) };
        using (var cts = new CancellationTokenSource())
        {
            cts.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() => Run(cts.Token));
        }

        var r = EntryPoint.Store.Current;
        Assert.False(r.Completed);
        Assert.Contains("stopped after 0 of 1", r.Note);
    }

    [Fact]
    public void NoTrailerReportCanBeTurnedOff()
    {
        Plugin.Instance.Configuration.ScanReportNoTrailers = false;
        _items = new BaseItem[] { MovieWith(1, "Empty") };
        Assert.Empty(Run().Issues);
    }

    [Fact]
    public void PlaybackQueueListsScannedYouTubeLinksNotYetTested()
    {
        _items = new BaseItem[]
        {
            MovieWith(1, "A", Ok1, "https://vimeo.com/1"),
            MovieWith(2, "B", Blocked),                       // already broken: no browser test needed
            MovieWith(3, "C", Ok2),
            MovieWith(4, "D", "https://youtu.be/okokokokok2") // same video as C: tested once
        };
        Run();

        var queue = EntryPoint.Store.GetPlaybackQueue(10, 30).Select(q => q.VideoId).ToArray();
        Assert.Equal(new[] { "okokokokok1", "okokokokok2" }, queue);
        Assert.Equal(2, EntryPoint.Store.GetPlaybackCounts(30).Total);

        EntryPoint.Store.RecordPlayback("okokokokok1", YouTubeChecker.Ok, "Plays.", "browser");
        Assert.Equal(new[] { "okokokokok2" }, EntryPoint.Store.GetPlaybackQueue(10, 30).Select(q => q.VideoId).ToArray());

        // A test that couldn't decide is retried later, not straight away.
        EntryPoint.Store.RecordPlayback("okokokokok2", YouTubeChecker.Unknown, "No answer.", "browser");
        Assert.Empty(EntryPoint.Store.GetPlaybackQueue(10, 30));
        Assert.Equal(2, EntryPoint.Store.GetPlaybackCounts(30).Done);
    }

    [Fact]
    public void BlockedPlaybackShowsAsWontPlayForEveryTitleUsingTheVideo()
    {
        _items = new BaseItem[] { MovieWith(3, "C", Ok2), MovieWith(4, "D", "https://youtu.be/okokokokok2"), MovieWith(5, "E", Ok1) };
        Run();
        Assert.Empty(EntryPoint.Store.GetIssues());

        EntryPoint.Store.RecordPlayback("okokokokok2", YouTubeChecker.Error, "Blocked by a copyright owner.", "browser");

        var issues = EntryPoint.Store.GetIssues();
        Assert.Equal(new[] { "C", "D" }, issues.Select(i => i.ItemName).ToArray());
        Assert.All(issues, i => Assert.True(i.FromPlaybackTest));
        Assert.All(issues, i => Assert.Equal(IssueKinds.Unplayable, i.Kind));
        Assert.Equal(1, EntryPoint.Store.GetPlaybackCounts(30).Blocked);

        // A later run that plays fine (e.g. the block was lifted) clears it.
        EntryPoint.Store.RecordPlayback("okokokokok2", YouTubeChecker.Ok, "Plays.", "emby");
        Assert.Empty(EntryPoint.Store.GetIssues());
    }

    [Fact]
    public void PlaybackResultsSurviveTheNextScan()
    {
        _items = new BaseItem[] { MovieWith(3, "C", Ok2) };
        Run();
        EntryPoint.Store.RecordPlayback("okokokokok2", YouTubeChecker.Error, "Blocked.", "browser");

        Run();

        Assert.Single(EntryPoint.Store.GetIssues());
        Assert.Empty(EntryPoint.Store.GetPlaybackQueue(10, 30));
    }

    [Fact]
    public void PressingScanNowTwiceStartsOneScan()
    {
        Run(); // clears any earlier request
        Assert.True(TrailerScanTask.TryRequestStart());
        Assert.True(TrailerScanTask.IsStarting);
        Assert.False(TrailerScanTask.TryRequestStart());

        Run(); // Emby starts the task: no longer "starting", and nothing left running
        Assert.False(TrailerScanTask.IsStarting);
        Assert.Null(TrailerScanTask.InProgress);
        Assert.True(TrailerScanTask.TryRequestStart());
        Run();
    }

    [Fact]
    public void NoAutomaticScanByDefault()
    {
        var config = new TrailerEditor.Configuration.PluginConfiguration();
        Assert.Empty(ScanTask.GetDefaultTriggers());
        Assert.Empty(TrailerScanTask.TriggersFor(config));
        Assert.Null(TrailerScanTask.Describe(config));
        Assert.True(config.ScanBrowserPlaybackTest);
        Assert.True(config.ScanCheckAgeRestriction);
    }

    [Fact]
    public void ScheduleSettingsBecomeTriggers()
    {
        var config = new TrailerEditor.Configuration.PluginConfiguration { ScanSchedule = "Weekly", ScanScheduleDay = 3, ScanScheduleHour = 22 };
        var weekly = TrailerScanTask.TriggersFor(config).Single();
        Assert.Equal(MediaBrowser.Model.Tasks.TaskTriggerInfo.TriggerWeekly, weekly.Type);
        Assert.Equal(DayOfWeek.Wednesday, weekly.DayOfWeek);
        Assert.Equal(TimeSpan.FromHours(22).Ticks, weekly.TimeOfDayTicks);
        Assert.Equal("every Wednesday at 22:00", TrailerScanTask.Describe(config));

        config.ScanSchedule = "Daily";
        config.ScanScheduleHour = 4;
        var daily = TrailerScanTask.TriggersFor(config).Single();
        Assert.Equal(MediaBrowser.Model.Tasks.TaskTriggerInfo.TriggerDaily, daily.Type);
        Assert.Equal("every day at 04:00", TrailerScanTask.Describe(config));
    }

    [Fact]
    public void EditingAnItemClearsItsIssues()
    {
        _items = new BaseItem[] { MovieWith(7, "Empty"), MovieWith(8, "Other") };
        Run();
        Assert.Equal(2, EntryPoint.Store.GetIssues().Count);

        EntryPoint.Store.RemoveIssuesFor("7");

        Assert.Equal("Other", EntryPoint.Store.GetIssues().Single().ItemName);
    }

    [Fact]
    public void ScopeFiltersByTypeAndLetterRange()
    {
        _items = new BaseItem[]
        {
            Sorted(1, "Alpha"), Sorted(2, "bravo"), Sorted(3, "Charlie"), Sorted(4, "Zulu"),
            Sorted(5, "9 Lives"), Sorted(6, "  "), ShowSorted(7, "Bravo Show")
        };

        var all = Run();
        Assert.Equal(7, all.ItemsScanned);
        Assert.Null(ScanScope.Of(all).Describe());

        var movies = RunScope("Movie", "A", "C");
        Assert.Equal(3, movies.ItemsScanned);
        Assert.Equal("Movies, A–C", ScanScope.Of(movies).Describe());

        var shows = RunScope("Series", null, null);
        Assert.Equal(1, shows.ItemsScanned);

        // From after To is swapped; # is anything that isn't A-Z, and sorts first.
        var swapped = RunScope("", "C", "A");
        Assert.Equal(4, swapped.ItemsScanned);
        Assert.Equal("A", swapped.ScopeFrom);
        Assert.Equal("C", swapped.ScopeTo);
        var other = RunScope("Movie", "#", "#");
        Assert.Equal(2, other.ItemsScanned);
        Assert.Contains(other.Issues, i => i.Letter == "#");
        Assert.Equal("Movies, #", ScanScope.Of(other).Describe());
    }

    [Fact]
    public void ScopeIsOnlyUsedByTheNextRun()
    {
        _items = new BaseItem[] { Sorted(1, "Alpha"), Sorted(2, "Zulu") };
        Assert.Equal(1, RunScope("", "A", "A").ItemsScanned);
        Assert.Equal(2, Run().ItemsScanned);
    }

    [Fact]
    public void CompletedSegmentScanReplacesOnlyThatSegment()
    {
        _items = new BaseItem[] { Sorted(1, "Alpha"), Sorted(2, "Apple"), Sorted(3, "Zulu"), ShowSorted(4, "Alien") };
        var first = Run();
        Assert.Equal(4, first.Issues.Count);

        // Apple is gone from the library, Alpha now has a trailer.
        _items = new BaseItem[] { Sorted(1, "Alpha", Ok1), Sorted(3, "Zulu"), ShowSorted(4, "Alien") };
        var r = RunScope("Movie", "A", "B");

        Assert.True(r.Completed);
        Assert.Equal(1, r.ItemsScanned);
        // Alpha replaced (no problems now), Apple dropped, Zulu (other letters) and Alien (a show) kept.
        Assert.Equal(new[] { "Alien", "Zulu" }, Names(r));
        Assert.DoesNotContain(r.Links, l => l.ItemName == "Apple");
        Assert.Contains(r.Links, l => l.ItemName == "Alpha" && l.Letter == "A");
    }

    [Fact]
    public void StoppedScanOnlyReplacesTitlesItReached()
    {
        _items = new BaseItem[] { Sorted(1, "Alpha"), Sorted(2, "Bravo"), Sorted(3, "Charlie") };
        Run();

        // Alpha gets a trailer; the scan is stopped while checking the second title.
        _items = new BaseItem[] { Sorted(1, "Alpha", Ok1), Sorted(2, "Bravo", Ok2), Sorted(3, "Charlie", Ok1) };
        using (var cts = new CancellationTokenSource())
        {
            _checker.Answer = url =>
            {
                if (url.Contains("okokokokok2"))
                {
                    cts.Cancel();
                    throw new OperationCanceledException();
                }

                return new LinkCheckResult { Status = YouTubeChecker.Ok, Message = "No problems found." };
            };
            _checker.Calls.Clear();
            Assert.ThrowsAny<OperationCanceledException>(() => Run(cts.Token));
        }

        var r = EntryPoint.Store.Current;
        Assert.False(r.Completed);
        // Alpha finished: replaced. Bravo was cut off: its earlier result stays. Charlie never reached: kept.
        Assert.Equal(new[] { "Bravo", "Charlie" }, Names(r));
        Assert.DoesNotContain(r.Links, l => l.ItemName == "Bravo");
        Assert.True(r.PlaybackStopped);
    }

    [Fact]
    public void OldResultsWithoutLetterAreKeptByASegmentScanAndReplacedByAFullScan()
    {
        _items = new BaseItem[] { Sorted(1, "Alpha"), Sorted(2, "Zulu") };
        Run();
        foreach (var i in EntryPoint.Store.Current.Issues)
        {
            i.Letter = null; // as saved by an older version
        }

        _items = new BaseItem[] { Sorted(1, "Alpha", Ok1) };
        var segment = RunScope("", "A", "A");
        Assert.Equal(new[] { "Zulu" }, Names(segment));

        foreach (var i in segment.Issues)
        {
            i.Letter = null;
        }

        Assert.Empty(Run().Issues);
    }

    [Fact]
    public void StoppedPlaybackHidesTheQueueUntilContinuedOrANewScan()
    {
        _items = new BaseItem[] { Sorted(1, "Alpha", Ok1) };
        Run();
        Assert.Single(EntryPoint.Store.GetPlaybackQueue(10, 30));

        EntryPoint.Store.SetPlaybackStopped(true);
        Assert.Empty(EntryPoint.Store.GetPlaybackQueue(10, 30));
        Assert.Equal(1, EntryPoint.Store.GetPlaybackCounts(30).Total);

        EntryPoint.Store.SetPlaybackStopped(false);
        Assert.Single(EntryPoint.Store.GetPlaybackQueue(10, 30));

        EntryPoint.Store.SetPlaybackStopped(true);
        var r = Run();
        Assert.False(r.PlaybackStopped);
        Assert.Single(EntryPoint.Store.GetPlaybackQueue(10, 30));
    }

    [Fact]
    public void PlaybackQueueAndCountsOnlyCoverTheScope()
    {
        _items = new BaseItem[]
        {
            Sorted(1, "Alpha", Ok1), Sorted(2, "Zulu", Ok2), ShowSorted(3, "Alien", "https://www.youtube.com/watch?v=showshowsh1")
        };
        Run();
        Assert.Equal(3, EntryPoint.Store.GetPlaybackCounts(30).Total);

        RunScope("Movie", "A", "B");

        // Zulu and Alien were kept from the earlier scan but aren't part of this one.
        Assert.Equal(new[] { "okokokokok1" }, EntryPoint.Store.GetPlaybackQueue(10, 30).Select(q => q.VideoId).ToArray());
        Assert.Equal(1, EntryPoint.Store.GetPlaybackCounts(30).Total);
        Assert.Equal(3, EntryPoint.Store.Current.Links.Count);
    }

    private static Movie Added(long id, string sortName, double daysAgo, params string[] trailers)
    {
        var m = Sorted(id, sortName, trailers);
        m.DateCreated = DateTimeOffset.UtcNow.AddDays(-daysAgo);
        return m;
    }

    private ScanResults RunDays(string types, string from, string to, int? days)
    {
        Assert.True(TrailerScanTask.TryRequestStart(new ScanScope(types, from, to, days)));
        return Run();
    }

    [Fact]
    public void DateScopeSelectsOnlyRecentTitlesAndCombinesWithTypeAndLetter()
    {
        var show = ShowSorted(4, "Apple");
        show.DateCreated = DateTimeOffset.UtcNow.AddDays(-0.5);
        _items = new BaseItem[]
        {
            Added(1, "Alpha", 2), Added(2, "Bravo", 20), Added(3, "Zulu", 0.5), show, Sorted(5, "Unknown")
        };

        Assert.Equal(new[] { "Alpha", "Apple", "Zulu" }, Names(RunDays(null, null, null, 7)));
        Assert.Equal(3, EntryPoint.Store.Current.ItemsTotal);
        Assert.Equal(7, EntryPoint.Store.Current.ScopeAddedWithinDays);
        Assert.Equal("added in the last 7 days", ScanScope.Of(EntryPoint.Store.Current).Describe());

        // Earlier results of unscanned titles are kept, so check what this run scanned.
        Assert.Equal(1, RunDays("Movie", "A", "C", 7).ItemsTotal);
        Assert.Equal(2, RunDays(null, null, null, 1).ItemsTotal);
    }

    [Fact]
    public void DescribeUsesSingularDayAndClampsDays()
    {
        Assert.Equal("added in the last day", new ScanScope(null, null, null, 1).Describe());
        Assert.Equal("Movies, A–C, added in the last 7 days", new ScanScope("Movie", "A", "C", 7).Describe());
        Assert.Null(new ScanScope(null, null, null, 0).Describe());
        Assert.Equal(3650, new ScanScope(null, null, null, 99999).AddedWithinDays);
        Assert.Equal(0, new ScanScope(null, null, null, -4).AddedWithinDays);
    }

    [Fact]
    public void DateLimitedScanKeepsOlderTitlesResultsEvenWhenCompleted()
    {
        _items = new BaseItem[] { Added(1, "Alpha", 100), Added(2, "Bravo", 100, Ok1) };
        Run();
        Assert.Equal(new[] { "Alpha" }, Names(EntryPoint.Store.Current));

        // Alpha is old and not scanned; Charlie is new.
        _items = new BaseItem[] { Added(1, "Alpha", 100), Added(2, "Bravo", 100, Ok1), Added(3, "Charlie", 1) };
        var r = RunDays(null, null, null, 7);
        Assert.True(r.Completed);
        Assert.Equal(new[] { "Alpha", "Charlie" }, Names(r));
        Assert.Contains(r.Links, l => l.ItemName == "Bravo");

        // A letter-only completed scan still replaces its segment.
        _items = new BaseItem[] { Added(2, "Bravo", 100, Ok1), Added(3, "Charlie", 1) };
        Assert.Equal(new[] { "Charlie" }, Names(RunScope("", "A", "A")).Where(n => n == "Charlie").ToArray());
        Assert.DoesNotContain("Alpha", Names(EntryPoint.Store.Current));
    }

    [Fact]
    public void PlaybackQueueForDateScopeUsesAddedDateRelativeToScanStart()
    {
        _items = new BaseItem[] { Added(1, "Alpha", 2, Ok1), Added(2, "Bravo", 20, Ok2) };
        Run();
        Assert.Equal(2, EntryPoint.Store.GetPlaybackCounts(30).Total);
        Assert.All(EntryPoint.Store.Current.Links, l => Assert.NotNull(l.AddedUtc));

        RunDays(null, null, null, 7);
        Assert.Equal(new[] { "okokokokok1" }, EntryPoint.Store.GetPlaybackQueue(10, 30).Select(q => q.VideoId).ToArray());
        Assert.Equal(1, EntryPoint.Store.GetPlaybackCounts(30).Total);

        // A link kept from before this version has no date and is left out; one added just before the scan started stays in.
        var links = EntryPoint.Store.Current.Links;
        links.Add(new LinkRef { VideoId = "oldoldoldol1", Url = "https://youtu.be/oldoldoldol1", ItemId = "9", ItemName = "Old", Type = "Movie", Letter = "O" });
        links.First(l => l.VideoId == "okokokokok1").AddedUtc = EntryPoint.Store.Current.StartedUtc.Value.AddDays(-6.9);
        Assert.Equal(1, EntryPoint.Store.GetPlaybackCounts(30).Total);
        links.First(l => l.VideoId == "okokokokok1").AddedUtc = EntryPoint.Store.Current.StartedUtc.Value.AddDays(-7.1);
        Assert.Equal(0, EntryPoint.Store.GetPlaybackCounts(30).Total);
    }

    [Fact]
    public void ScheduledRunUsesTheConfiguredAddedWithinDays()
    {
        var config = Plugin.Instance.Configuration;
        _items = new BaseItem[] { Added(1, "Alpha", 1), Added(2, "Bravo", 10) };
        try
        {
            config.ScanScheduleAddedWithinDays = 3;
            var r = Run();
            Assert.Equal(new[] { "Alpha" }, Names(r));
            Assert.Equal(3, r.ScopeAddedWithinDays);

            // A pending manual scope wins over the setting.
            Assert.Equal(2, RunDays(null, null, null, null).ItemsTotal);

            config.ScanSchedule = "Daily";
            config.ScanScheduleHour = 3;
            Assert.Equal("every day at 03:00, titles added in the last 3 days", TrailerScanTask.Describe(config));
            config.ScanScheduleAddedWithinDays = 1;
            Assert.Equal("every day at 03:00, titles added in the last day", TrailerScanTask.Describe(config));
            config.ScanScheduleAddedWithinDays = 0;
            Assert.Equal("every day at 03:00", TrailerScanTask.Describe(config));
            Assert.Equal(2, Run().ItemsTotal);
        }
        finally
        {
            config.ScanScheduleAddedWithinDays = 0;
            config.ScanSchedule = "Off";
        }
    }
}
