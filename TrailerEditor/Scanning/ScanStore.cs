using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;

namespace TrailerEditor.Scanning
{
    /// <summary>Keeps the last scan's results in memory and in a JSON file next to the plugin config.</summary>
    public class ScanStore
    {
        private readonly object _lock = new object();
        private readonly IJsonSerializer _json;
        private readonly ILogger _logger;
        private readonly string _path;
        private ScanResults _results;

        public ScanStore(IApplicationPaths paths, IJsonSerializer json, ILogger logger)
        {
            _json = json;
            _logger = logger;
            _path = Path.Combine(paths.PluginConfigurationsPath, "TrailerEditor.scan.json");
        }

        public ScanResults Current
        {
            get
            {
                lock (_lock)
                {
                    if (_results == null)
                    {
                        _results = Load();
                    }

                    return _results;
                }
            }
        }

        public void Save(ScanResults results)
        {
            lock (_lock)
            {
                _results = results;
                try
                {
                    _json.SerializeToFile(results, _path);
                }
                catch (Exception ex)
                {
                    _logger.ErrorException("Trailer Editor: couldn't save scan results to {0}", ex, _path);
                }
            }
        }

        /// <summary>Drops an item's issues after it was edited; the next scan re-checks it.</summary>
        public void RemoveIssuesFor(string itemId)
        {
            lock (_lock)
            {
                var results = Current;
                if (results.Issues.RemoveAll(i => i.ItemId == itemId) > 0)
                {
                    Save(results);
                }
            }
        }

        public List<ScanIssue> GetIssues()
        {
            lock (_lock)
            {
                return Current.Issues.ToList();
            }
        }

        private ScanResults Load()
        {
            try
            {
                if (File.Exists(_path))
                {
                    var loaded = _json.DeserializeFromFile<ScanResults>(_path);
                    if (loaded != null)
                    {
                        loaded.Issues = loaded.Issues ?? new List<ScanIssue>();
                        loaded.LinkCache = loaded.LinkCache ?? new List<CachedLinkResult>();
                        return loaded;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Trailer Editor: couldn't read scan results from {0}", ex, _path);
            }

            return new ScanResults();
        }
    }
}
