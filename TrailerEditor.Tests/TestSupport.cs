using System;
using System.IO;
using System.Text.Json;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using Moq;
using TrailerEditor;
using TrailerEditor.Actions;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

/// <summary>Plugin, temp folder, logger and a JSON serializer backed by System.Text.Json, shared by the tests.</summary>
public class TestEnv
{
    public string Dir { get; }
    public Mock<IApplicationPaths> Paths { get; } = new Mock<IApplicationPaths>();
    public Mock<ILogManager> LogManager { get; } = new Mock<ILogManager>();
    public Mock<ILibraryManager> Library { get; } = new Mock<ILibraryManager>();
    public IJsonSerializer Json { get; }
    public Plugin Plugin { get; }
    public ILogger Logger => LogManager.Object.GetLogger("x");

    public TestEnv()
    {
        Dir = Path.Combine(Path.GetTempPath(), "te-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Dir);
        Paths.SetupGet(p => p.PluginConfigurationsPath).Returns(Dir);
        Paths.SetupGet(p => p.PluginsPath).Returns(Dir);
        Paths.SetupGet(p => p.ConfigurationDirectoryPath).Returns(Dir);
        Paths.SetupGet(p => p.DataPath).Returns(Dir);
        Paths.SetupGet(p => p.ProgramDataPath).Returns(Dir);
        LogManager.Setup(l => l.GetLogger(It.IsAny<string>())).Returns(new Mock<ILogger>().Object);

        var json = new Mock<IJsonSerializer>();
        json.Setup(j => j.SerializeToFile(It.IsAny<object>(), It.IsAny<string>()))
            .Callback<object, string>((o, path) => File.WriteAllText(path, JsonSerializer.Serialize(o, o.GetType())));
        json.Setup(j => j.DeserializeFromFile<RejectedData>(It.IsAny<string>()))
            .Returns<string>(path => JsonSerializer.Deserialize<RejectedData>(File.ReadAllText(path)));
        json.Setup(j => j.DeserializeFromFile<ActionLogData>(It.IsAny<string>()))
            .Returns<string>(path => JsonSerializer.Deserialize<ActionLogData>(File.ReadAllText(path)));
        Json = json.Object;

        var xml = new Mock<IXmlSerializer>();
        xml.Setup(x => x.DeserializeFromFile(It.IsAny<Type>(), It.IsAny<string>()))
            .Returns(() => new TrailerEditor.Configuration.PluginConfiguration());
        Plugin = new Plugin(Paths.Object, xml.Object);
        Plugin.SetAttributes(Path.Combine(Dir, "TrailerEditor.dll"), Dir, new Version(1, 0));
        Plugin.SetStartupInfo(path => Directory.CreateDirectory(path));
    }

    public RejectedStore NewRejectedStore() => new RejectedStore(Paths.Object, Json, Logger);

    public ActionLog NewActionLog() => new ActionLog(Paths.Object, Json, Logger);

    /// <summary>Items are "saved" by remembering them; BaseItem.UpdateToRepository needs a running server.</summary>
    public System.Collections.Generic.List<BaseItem> Saved { get; } = new System.Collections.Generic.List<BaseItem>();

    public TrailerManager NewManager(RejectedStore rejected) => new TrailerManager(Library.Object, Logger, rejected, item => Saved.Add(item));

    public static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
}

public class FakeChecker : TrailerEditor.YouTubeChecker
{
    public System.Collections.Generic.List<string> Calls { get; } = new System.Collections.Generic.List<string>();
    public Func<string, TrailerEditor.LinkCheckResult> Answer { get; set; } =
        url => new TrailerEditor.LinkCheckResult { Status = TrailerEditor.YouTubeChecker.Ok, Message = "No problems found.", Title = "Checked title", Channel = "Checked channel" };

    public override System.Threading.Tasks.Task<TrailerEditor.LinkCheckResult> CheckAsync(string input, bool checkWatchPage, System.Threading.CancellationToken cancellationToken)
    {
        Calls.Add(input);
        return System.Threading.Tasks.Task.FromResult(Answer(input));
    }
}

public class FakeHttp : TrailerEditor.Search.IHttpFetcher
{
    public System.Collections.Generic.List<string> Requests { get; } = new System.Collections.Generic.List<string>();

    /// <summary>Returns the page for a URL, or throws to simulate a network error.</summary>
    public Func<string, string> Handler { get; set; }

    public System.Threading.Tasks.Task<string> GetStringAsync(string url, System.Threading.CancellationToken cancellationToken)
    {
        Requests.Add(url);
        return System.Threading.Tasks.Task.FromResult(Handler(url));
    }
}
