using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;

namespace TrailerEditor.Actions
{
    public static class ActionKinds
    {
        public const string Find = "find";
        public const string Delete = "delete";
    }

    public static class ActionOutcomes
    {
        public const string Ok = "ok";
        public const string Failed = "failed";
        public const string NoMatch = "no match";
    }

    public static class ActionSources
    {
        public const string Collection = "collection";
        public const string Editor = "editor";
        public const string Api = "api";
    }

    public class ActionRecord
    {
        public DateTime TimeUtc { get; set; }

        /// <summary>The item's numeric id, as used by the Trailer Editor page.</summary>
        public string ItemId { get; set; }

        public string ItemName { get; set; }
        public int? Year { get; set; }
        public string Type { get; set; }

        /// <summary>find or delete.</summary>
        public string Action { get; set; }

        /// <summary>collection, editor or api.</summary>
        public string Source { get; set; }

        /// <summary>ok, failed or no match.</summary>
        public string Outcome { get; set; }

        public string Message { get; set; }
        public string ChosenUrl { get; set; }
    }

    public class ActionLogData
    {
        public List<ActionRecord> Items { get; set; } = new List<ActionRecord>();
    }

    /// <summary>The most recent trailer actions (kept in memory and in TrailerEditor.actions.json).</summary>
    public class ActionLog
    {
        public const int Capacity = 200;

        private readonly object _lock = new object();
        private readonly IJsonSerializer _json;
        private readonly ILogger _logger;
        private readonly string _path;
        private List<ActionRecord> _items;

        public ActionLog(IApplicationPaths paths, IJsonSerializer json, ILogger logger)
        {
            _json = json;
            _logger = logger;
            _path = Path.Combine(paths.PluginConfigurationsPath, "TrailerEditor.actions.json");
        }

        public void Add(ActionRecord record)
        {
            lock (_lock)
            {
                Load();
                if (record.TimeUtc == default(DateTime))
                {
                    record.TimeUtc = DateTime.UtcNow;
                }

                _items.Add(record);
                if (_items.Count > Capacity)
                {
                    _items.RemoveRange(0, _items.Count - Capacity);
                }

                try
                {
                    _json.SerializeToFile(new ActionLogData { Items = _items }, _path);
                }
                catch (Exception ex)
                {
                    _logger.ErrorException("Trailer Editor: couldn't save the action log to {0}", ex, _path);
                }
            }
        }

        /// <summary>Newest first.</summary>
        public List<ActionRecord> Recent(int limit)
        {
            lock (_lock)
            {
                Load();
                return Enumerable.Reverse(_items).Take(Math.Max(1, limit)).ToList();
            }
        }

        private void Load()
        {
            if (_items != null)
            {
                return;
            }

            _items = new List<ActionRecord>();
            try
            {
                if (File.Exists(_path))
                {
                    _items = _json.DeserializeFromFile<ActionLogData>(_path)?.Items ?? new List<ActionRecord>();
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Trailer Editor: couldn't read the action log from {0}", ex, _path);
            }
        }
    }
}
