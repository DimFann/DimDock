using Microsoft.Extensions.Logging;
using System.Text.Json;
using SketchArchiveLib.Google;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Timers;
using System.Diagnostics;

namespace DimDock.SketchArchiveLib.Google
{
    public class DriveMapItem
    {
        public string FolderId { get; set; }
        public string ResourceKey { get; set; }

        public DriveMapItem()
        {
        }

        public DriveMapItem(string folderId, string resourceKey)
        {
            FolderId = folderId;
            ResourceKey = resourceKey;
        }
    }

    /// <summary>
    /// Builds and maintains a map of folder path to GDrive ID.
    /// </summary>
    public class DriveMap
    {
        private Dictionary<string, DriveMapItem> _map;

        /// <summary>
        /// Immutable copy of internal map.
        /// </summary>
        public ReadOnlyDictionary<string, DriveMapItem> Map => new(_map);

        private readonly Timer _rebuildTimer;
        private readonly GDriveReader _driveReader;
        private readonly object _lock;
        private readonly ILogger _logger;
        private readonly int _apiDelayMs;
        private readonly string _rootFolderId;
        private readonly string _rootFolderResourceKey;
        private readonly string _cachedMap;
        private readonly bool _enableRefresh;

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="driveReader">Instance of GDriveReader.</param>
        /// <param name="rootFolderId">Root folder ID.</param>
        /// <param name="rootFolderResourceKey">Root folder resource key.</param>
        /// <param name="logger">Logger.</param>
        /// <param name="apiDelayMs">How log to wait between GDrive API calls.</param>
        /// <param name="refreshIntervalMinutes">How often the drive map should be rebuilt.</param>
        /// <exception cref="Exception"></exception>
        public DriveMap(
            GDriveReader driveReader,
            string rootFolderId,
            string rootFolderResourceKey,
            bool buildOnStart = true,
            bool enableRefresh = true,
            ILogger logger = null,
            int apiDelayMs = 500,
            int refreshIntervalMinutes = 1440)
        {
            if (refreshIntervalMinutes < 1)
                throw new Exception("Interval should be at least 1 minute, though a much greater value is recommended.");

            if (apiDelayMs < 100)
                throw new Exception("I'm not the police but it's your quota fam.");

            _lock = new object();
            _driveReader = driveReader;
            _logger = logger;
            _map = new();
            _rootFolderId = rootFolderId;
            _rootFolderResourceKey = rootFolderResourceKey;
            _apiDelayMs = apiDelayMs;
            _enableRefresh = enableRefresh;

            _rebuildTimer = new Timer();
            _rebuildTimer.Elapsed += OnElapsedRebuildTimer;
            _rebuildTimer.Interval = refreshIntervalMinutes * 60 * 1000;
            _rebuildTimer.AutoReset = true;

            if(buildOnStart)
                OnElapsedRebuildTimer(null, null);

            if(_enableRefresh)
                _rebuildTimer.Start();
        }

        /// <summary>
        /// Constructor, load from a file initially.
        /// </summary>
        /// <param name="cachedMap"></param>
        /// <param name="driveReader"></param>
        /// <param name="rootFolderId"></param>
        /// <param name="rootFolderResourceKey"></param>
        /// <param name="logger"></param>
        /// <param name="apiDelayMs"></param>
        /// <param name="refreshIntervalMinutes"></param>
        public DriveMap(
            string cachedMap,
            GDriveReader driveReader,
            string rootFolderId,
            string rootFolderResourceKey,
            bool enableRefresh = true,
            ILogger logger = null, 
            int apiDelayMs = 500,
            int refreshIntervalMinutes = 1440
        ):
            this(driveReader,rootFolderId,rootFolderResourceKey,false,enableRefresh,logger, apiDelayMs, refreshIntervalMinutes)
        {
            var map = LoadMap(cachedMap,logger);
            if(map != null)
            {
                lock(_lock)
                {
                    _map.Clear();
                    _map = map;
                    _cachedMap = cachedMap;
                }
            }
            else
            {
                // Load failed.
                OnElapsedRebuildTimer(null, null);
            }
        }

        /// <summary>
        /// Deconstructor.
        /// </summary>
        ~DriveMap()
        {
            _rebuildTimer?.Stop();
        }

        /// <summary>
        /// Get a GDrive folderId by a path string, e.g. 2020/2020-01
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        public (string Path, DriveMapItem Item) GetFolderId(string path)
        {
            lock(_lock)
            {
                if (_map.ContainsKey(path))
                    return (path,_map[path]);
                else
                    return default;
            }
        }

        /// <summary>
        /// Rebuild the in-memory map.
        /// </summary>
        public void Rebuild()
        {
            OnElapsedRebuildTimer(null, null);
        }

        private void OnElapsedRebuildTimer(object sender, ElapsedEventArgs e)
        {
            try
            {
                if(_enableRefresh)
                    _rebuildTimer.Stop();

                var newMap = BuildMap(_driveReader, _rootFolderId, _rootFolderResourceKey, _apiDelayMs, _logger);
                if(newMap != null)
                {
                    // Save the new map to the cache if a cache was used initially.
                    if (!string.IsNullOrWhiteSpace(_cachedMap))   
                        SaveMap(_cachedMap, newMap, _logger);

                    lock (_lock)
                    {
                        _map.Clear();
                        _map = newMap;
                    }
                }
            }
            catch(AggregateException ae)
            {
                _logger?.LogError("Failed to build drive map, {Message}", ae.Flatten().Message);
            }
            finally
            {
                if(_enableRefresh)
                    _rebuildTimer.Start();
            }
        }

        /// <summary>
        /// Build a map object.
        /// </summary>
        /// <param name="driveReader"></param>
        /// <param name="rootFolderId"></param>
        /// <param name="rootFolderResourcekey"></param>
        /// <param name="apiDelayMs"></param>
        /// <param name="logger"></param>
        /// <returns></returns>
        public static Dictionary<string, DriveMapItem> BuildMap(GDriveReader driveReader, string rootFolderId, string rootFolderResourcekey, int apiDelayMs, ILogger logger)
        {
            try
            {
                logger?.LogInformation("DriveMap: Building");
                (string Path, string FolderId, string ResourceKey)[] values = default;
                var task = Task.Run(async () => values = await GetAllPaths(driveReader,"", rootFolderId, rootFolderResourcekey,apiDelayMs,logger));
                task.Wait();

                Dictionary<string, DriveMapItem> map = new();
                foreach ((string Path, string FolderId, string ResourceKey) in values)
                {
                    map.Add(Path.TrimStart('/'), new DriveMapItem(FolderId,ResourceKey));
                }
                logger?.LogInformation("DriveMap: Building complete, have a nice day :)");

                return map;
            }
            catch (AggregateException ae)
            {
                logger?.LogError("Failed to build drive map, {Message}", ae.Flatten().Message);
                return null;
            }
        }

        /// <summary>
        /// Load map object from a file.
        /// </summary>
        /// <param name="cachedMap"></param>
        /// <param name="logger"></param>
        /// <returns></returns>
        public static Dictionary<string, DriveMapItem> LoadMap(string cachedMap, ILogger logger = null)
        {
            if (string.IsNullOrWhiteSpace(cachedMap) || !File.Exists(cachedMap))
                return null;

            using StreamReader reader = new(cachedMap);
            try
            {
                var value = reader.ReadToEnd();
                var map = JsonSerializer.Deserialize<Dictionary<string, DriveMapItem>>(value);

                return map ?? throw new Exception($"Deserialization failed\r\n{value}");
            }
            catch (Exception e)
            {
                logger.LogError("LoadMap failed, {Message}", e.Message);

                if (Debugger.IsAttached)
                    throw;
                else
                    return null;
            }
        }

        /// <summary>
        /// Save map object to a file.
        /// </summary>
        /// <param name="cachedMap"></param>
        /// <param name="driveMap"></param>
        /// <param name="logger"></param>
        public static void SaveMap(string cachedMap, Dictionary<string, DriveMapItem> driveMap, ILogger logger = null)
        {
            if (driveMap == null)
                return;
            else
            {
                try
                {
                    using FileStream fs = new(cachedMap, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
                    using StreamWriter writer = new(fs);

                    writer.BaseStream.Seek(0, SeekOrigin.Begin);
                    writer.BaseStream.SetLength(0);
                    writer.WriteLine(JsonSerializer.Serialize(driveMap,new JsonSerializerOptions()
                    {
                       WriteIndented = true 
                    }));
                }
                catch(Exception e)
                {
                    logger.LogError("SaveMap failed, {Message}", e.Message);

                    if (Debugger.IsAttached)
                        throw;
                }
            }
        }

        /// <summary>
        /// Recursive function, enumerate all items for a given ID. 
        /// For every folder call this function again. 
        /// If there are no items a leaf was found and return the parent.
        /// </summary>
        /// <param name="parentPath"></param>
        /// <param name="parentFolderId"></param>
        /// <param name="parentResourceKey"></param>
        /// <returns></returns>
        private static async Task<(string Path,string FolderId, string ResourceKey)[]> GetAllPaths(GDriveReader driveReader, string parentPath, string parentFolderId, string parentResourceKey, int apiDelayMs, ILogger logger = null)
        {
            // It's google so it's less being nice and more trying to not get rate limited.
            await Task.Delay(apiDelayMs);

            var files = await driveReader.GetFolderContentsAsync(parentFolderId, parentResourceKey);
            var folders = files.Files.Where(x => x.Folder);
            if(folders.Any()) // If there are folders this is a branch.
            {
                // TODO: Could probably do this in parallel
                List<(string Path, string FolderId, string ResourceKey)> values = new();
                foreach(var folder in folders)
                {
                    values.Add(($"{parentPath}/{folder.Name}", folder.ID, folder.ResourceKey));
                    values.AddRange(await GetAllPaths(driveReader,$"{parentPath}/{folder.Name}", folder.ID, folder.ResourceKey, apiDelayMs,logger));
                }
                return values.ToArray();
            }
            else // If there were no folders this is a leaf.
            {
                logger?.LogInformation("DriveMap: Found leaf, {parentPath}:{parentFolderId}:{parentResourceKey}", parentPath, parentFolderId, parentResourceKey);
                return Array.Empty<(string Path, string FolderId, string ResourceKey)>();
            }   
        }
    }
}
