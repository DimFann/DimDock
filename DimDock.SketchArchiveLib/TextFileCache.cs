#pragma warning disable IDE0063 // Use simple 'using' statement

using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;

namespace DimDock.SketchArchiveLib
{
    /// <summary>
    /// Keeps the contents of local text files in memory.
    /// </summary>
    public class TextFileCache
    {
        private readonly string _path;
        private readonly string _searchPattern;
        private readonly object _lock;
        private readonly ILogger _logger;

        private Dictionary<string, string> _cache = new();
        public ReadOnlyDictionary<string, string> Cache
        {
            get
            {
                lock (_lock)
                {
                    return new ReadOnlyDictionary<string,string>(_cache);
                }
            }
        }

        public TextFileCache(string path, string searchPattern = "*.*", ILogger logger = null)
        {
            if (!Directory.Exists(path))
                throw new Exception($"Directory does not exist, {path}");

            _path = path;
            _searchPattern = string.IsNullOrWhiteSpace(searchPattern) ? "*.*" : searchPattern;
            _logger = logger;
            _lock = new object();

            RefreshAll();
        }

        /// <summary>
        /// Refresh a specific file by fileName.
        /// </summary>
        /// <param name="fileName"></param>
        public void Refresh(string fileName)
        {
            try
            {
                lock(_lock)
                {
                    if(File.Exists(fileName))
                    {
                        using(StreamReader reader = new(fileName))
                        {
                            string contents = reader.ReadToEnd();
                            if (_cache.ContainsKey(fileName))
                                _cache[fileName] = contents;   
                            else
                                _cache.Add(fileName, contents);
                        }
                    }
                    else
                    {
                        if (_cache.ContainsKey(fileName))
                            _cache.Remove(fileName);
                    }
                }
            }
            catch(Exception ex)
            {
                _logger?.LogError("Failed to refresh file.", ex);
            }
        }

        /// <summary>
        /// Refresh all files.
        /// </summary>
        public void RefreshAll()
        {
            try
            {
                if (Directory.Exists(_path)){
                    string[] files = Directory.GetFiles(_path, _searchPattern);
                    Dictionary<string, string> newCache = new();
                    foreach (string file in files)
                    {
                        string fileName = Path.GetFileName(file);
                        if (_cache.ContainsKey(fileName))
                            continue;

                        using (StreamReader reader = new(file))
                        {
                            string contents = reader.ReadToEnd();
                            newCache.Add(fileName, contents);
                        }
                    }
                    lock(_lock)
                    {
                        _cache.Clear();
                        _cache = newCache;
                    }
                }
            }
            catch(Exception ex)
            {
                _logger?.LogError("Failed to refresh files.", ex);
            }
        }
    }
}
