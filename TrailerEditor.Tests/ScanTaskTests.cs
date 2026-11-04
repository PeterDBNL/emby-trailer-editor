using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
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
        new EntryPoint(_library.Object, logManager.Object, paths.Object, new Mock<IJsonSerializer>().Object, new Mock<MediaBrowser.Controller.Session.ISessionManager>().Object, new Mock<MediaBrowser.Model.Tasks.ITaskManager>().Object);
        EntryPoint.Checker = _checker;

        _checker.Answer = url => url.Contains("blockblock1")
            ? new LinkCheckResult { Status = YouTubeChecker.Error, Message = "Age-restricted." }
            : new LinkCheckResult { Status = YouTubeChecker.Ok, Message = "No problems found." };

        ScanTask = new TrailerScanTask(_library.Object, logManager.Object);
    }

    private TrailerScanTask ScanTask { get; }

    private static Movie MovieWith(long id, string name, params string[] trailers) =>
        new Movie { Name = name, InternalId = id, ProductionYear = 2000, RemoteTrailers = trailers };

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
}
