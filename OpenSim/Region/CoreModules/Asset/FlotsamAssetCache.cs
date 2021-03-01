/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.IO;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using log4net;
using Nini.Config;
using Mono.Addins;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Monitoring;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Server.Base;
using OpenSim.Services.Interfaces;


namespace OpenSim.Region.CoreModules.Asset
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "FlotsamAssetCache")]
    public class FlotsamAssetCache : ISharedRegionModule, IAssetCache, IAssetService
    {
        private struct WriteAssetInfo
        {
            public string filename;
            public AssetBase asset;
            public bool replace;
        }

        private static readonly ILog _log = LogManager.GetLogger( MethodBase.GetCurrentMethod().DeclaringType);

        private bool _Enabled;
        private bool _timerRunning;
        private bool _cleanupRunning;

        private const string _ModuleName = "FlotsamAssetCache";
        private string _CacheDirectory = "assetcache";
        private string _assetLoader;
        private string _assetLoaderArgs;

        private readonly char[] _InvalidChars;

        private int _LogLevel = 0;
        private ulong _HitRateDisplay = 100; // How often to display hit statistics, given in requests

        private ulong _Requests;
        private ulong _RequestsForInprogress;
        private ulong _DiskHits;
        private ulong _MemoryHits;
        private ulong _weakRefHits;

        private static readonly HashSet<string> _CurrentlyWriting = new HashSet<string>();
        private static ObjectJobEngine _assetFileWriteWorker = null;
        private static HashSet<string> _defaultAssets = new HashSet<string>();

        private bool _FileCacheEnabled = true;

        private ExpiringCacheOS<string, AssetBase> _MemoryCache;
        private bool _MemoryCacheEnabled = false;

        private ExpiringKey<string> _negativeCache;
        private bool _negativeCacheEnabled = true;

        // Expiration is expressed in hours.
        private double _MemoryExpiration = 0.016;
        private const double _DefaultFileExpiration = 48;
        // Negative cache is in seconds
        private int _negativeExpiration = 120;
        private TimeSpan _FileExpiration = TimeSpan.FromHours(_DefaultFileExpiration);
        private TimeSpan _FileExpirationCleanupTimer = TimeSpan.FromHours(1.0);

        private static int _CacheDirectoryTiers = 1;
        private static int _CacheDirectoryTierLen = 3;
        private static int _CacheWarnAt = 30000;

        private System.Timers.Timer _CacheCleanTimer;

        private IAssetService _AssetService;
        private readonly List<Scene> _Scenes = new List<Scene>();
        private readonly object timerLock = new object();

        private Dictionary<string,WeakReference> weakAssetReferences = new Dictionary<string, WeakReference>();
        private readonly object weakAssetReferencesLock = new object();
        private bool _updateFileTimeOnCacheHit = false;

        public FlotsamAssetCache()
        {
            List<char> invalidChars = new List<char>();
            invalidChars.AddRange(Path.GetInvalidPathChars());
            invalidChars.AddRange(Path.GetInvalidFileNameChars());
            _InvalidChars = invalidChars.ToArray();
        }

        public Type ReplaceableInterface => null;

        public string Name => _ModuleName;

        public void Initialise(IConfigSource source)
        {
            IConfig moduleConfig = source.Configs["Modules"];

            if (moduleConfig != null)
            {
                string name = moduleConfig.GetString("AssetCaching", string.Empty);

                if (name == Name)
                {
                    _negativeCache = new ExpiringKey<string>(2000);
                    _Enabled = true;

                    _log.InfoFormat("[FLOTSAM ASSET CACHE]: {0} enabled", this.Name);

                    IConfig assetConfig = source.Configs["AssetCache"];
                    if (assetConfig == null)
                    {
                        _log.Debug(
                           "[FLOTSAM ASSET CACHE]: AssetCache section missing from config (not copied config-include/FlotsamCache.ini.example?  Using defaults.");
                    }
                    else
                    {
                        _FileCacheEnabled = assetConfig.GetBoolean("FileCacheEnabled", _FileCacheEnabled);
                        _CacheDirectory = assetConfig.GetString("CacheDirectory", _CacheDirectory);
                        _CacheDirectory = Path.GetFullPath(_CacheDirectory);

                        _MemoryCacheEnabled = assetConfig.GetBoolean("MemoryCacheEnabled", _MemoryCacheEnabled);
                        _MemoryExpiration = assetConfig.GetDouble("MemoryCacheTimeout", _MemoryExpiration);
                        _MemoryExpiration *= 3600.0; // config in hours to seconds

                        _negativeCacheEnabled = assetConfig.GetBoolean("NegativeCacheEnabled", _negativeCacheEnabled);
                        _negativeExpiration = assetConfig.GetInt("NegativeCacheTimeout", _negativeExpiration);

                        _updateFileTimeOnCacheHit = assetConfig.GetBoolean("UpdateFileTimeOnCacheHit", _updateFileTimeOnCacheHit);
                        _updateFileTimeOnCacheHit &= _FileCacheEnabled;

                        _LogLevel = assetConfig.GetInt("LogLevel", _LogLevel);
                        _HitRateDisplay = (ulong)assetConfig.GetLong("HitRateDisplay", (long)_HitRateDisplay);

                        _FileExpiration = TimeSpan.FromHours(assetConfig.GetDouble("FileCacheTimeout", _DefaultFileExpiration));
                        _FileExpirationCleanupTimer = TimeSpan.FromHours(
                                assetConfig.GetDouble("FileCleanupTimer", _FileExpirationCleanupTimer.TotalHours));

                        _CacheDirectoryTiers = assetConfig.GetInt("CacheDirectoryTiers", _CacheDirectoryTiers);
                        _CacheDirectoryTierLen = assetConfig.GetInt("CacheDirectoryTierLength", _CacheDirectoryTierLen);

                        _CacheWarnAt = assetConfig.GetInt("CacheWarnAt", _CacheWarnAt);
                    }

                    if(_MemoryCacheEnabled)
                        _MemoryCache = new ExpiringCacheOS<string, AssetBase>((int)_MemoryExpiration * 500);

                    _log.InfoFormat("[FLOTSAM ASSET CACHE]: Cache Directory {0}", _CacheDirectory);

                    if (_CacheDirectoryTiers < 1)
                        _CacheDirectoryTiers = 1;
                    else if (_CacheDirectoryTiers > 3)
                        _CacheDirectoryTiers = 3;

                    if (_CacheDirectoryTierLen < 1)
                        _CacheDirectoryTierLen = 1;
                    else if (_CacheDirectoryTierLen > 4)
                        _CacheDirectoryTierLen = 4;

                    _negativeExpiration *= 1000;

                    assetConfig = source.Configs["AssetService"];
                    if(assetConfig != null)
                    {
                        _assetLoader = assetConfig.GetString("DefaultAssetLoader", string.Empty);
                        _assetLoaderArgs = assetConfig.GetString("AssetLoaderArgs", string.Empty);
                        if (string.IsNullOrWhiteSpace(_assetLoaderArgs))
                            _assetLoader = string.Empty;
                    }

                    MainConsole.Instance.Commands.AddCommand("Assets", true, "fcache status", "fcache status", "Display cache status", HandleConsoleCommand);
                    MainConsole.Instance.Commands.AddCommand("Assets", true, "fcache clear",  "fcache clear [file] [memory]", "Remove all assets in the cache.  If file or memory is specified then only this cache is cleared.", HandleConsoleCommand);
                    MainConsole.Instance.Commands.AddCommand("Assets", true, "fcache assets", "fcache assets", "Attempt a deep scan and cache of all assets in all scenes", HandleConsoleCommand);
                    MainConsole.Instance.Commands.AddCommand("Assets", true, "fcache expire", "fcache expire <datetime(mm/dd/YYYY)>", "Purge cached assets older than the specified date/time", HandleConsoleCommand);
                    if (!string.IsNullOrWhiteSpace(_assetLoader))
                    {
                        MainConsole.Instance.Commands.AddCommand("Assets", true, "fcache cachedefaultassets", "fcache cachedefaultassets", "loads local default assets to cache. This may override grid ones. use with care", HandleConsoleCommand);
                        MainConsole.Instance.Commands.AddCommand("Assets", true, "fcache deletedefaultassets", "fcache deletedefaultassets", "deletes default local assets from cache so they can be refreshed from grid. use with care", HandleConsoleCommand);
                    }
                }
            }
        }

        public void PostInitialise()
        {
        }

        public void Close()
        {
            if(_Scenes.Count <= 0)
            {
                lock (timerLock)
                {
                    _cleanupRunning = false;
                    if (_timerRunning)
                    {
                        _timerRunning = false;
                        _CacheCleanTimer.Stop();
                        _CacheCleanTimer.Close();
                    }
                    if (_assetFileWriteWorker != null)
                    {
                        _assetFileWriteWorker.Dispose();
                        _assetFileWriteWorker = null;
                    }
                }
            }
        }

        public void AddRegion(Scene scene)
        {
            if (_Enabled)
            {
                scene.RegisterModuleInterface<IAssetCache>(this);
                _Scenes.Add(scene);
            }
        }

        public void RemoveRegion(Scene scene)
        {
            if (_Enabled)
            {
                scene.UnregisterModuleInterface<IAssetCache>(this);
                _Scenes.Remove(scene);
                lock(timerLock)
                {
                    if(_Scenes.Count <= 0)
                    {
                        _cleanupRunning = false;
                        if (_timerRunning)
                        {
                            _timerRunning = false;
                            _CacheCleanTimer.Stop();
                            _CacheCleanTimer.Close();
                        }
                        if (_assetFileWriteWorker != null)
                        {
                            _assetFileWriteWorker.Dispose();
                            _assetFileWriteWorker = null;
                        }
                    }
                }
            }
        }

        public void RegionLoaded(Scene scene)
        {
            if (_Enabled)
            {
                if(_AssetService == null)
                    _AssetService = scene.RequestModuleInterface<IAssetService>();
                lock(timerLock)
                {
                    if(!_timerRunning)
                    {
                        if (_FileCacheEnabled && _FileExpiration > TimeSpan.Zero && _FileExpirationCleanupTimer > TimeSpan.Zero)
                        {
                            _CacheCleanTimer = new System.Timers.Timer(_FileExpirationCleanupTimer.TotalMilliseconds)
                            {
                                AutoReset = false
                            };
                            _CacheCleanTimer.Elapsed += CleanupExpiredFiles;
                            _CacheCleanTimer.Start();
                            _timerRunning = true;
                        }
                    }

                    if (_FileCacheEnabled && _assetFileWriteWorker == null)
                    {
                        _assetFileWriteWorker = new ObjectJobEngine(ProcessWrites, "FloatsamCacheWriter", 1000 , 1);
                    }

                    if(!string.IsNullOrWhiteSpace(_assetLoader) && scene.RegionInfo.RegionID == _Scenes[0].RegionInfo.RegionID)
                    {
                        IAssetLoader assetLoader = ServerUtils.LoadPlugin<IAssetLoader>(_assetLoader, new object[] { });
                        if (assetLoader != null)
                        {
                            HashSet<string> ids = new HashSet<string>();
                            assetLoader.ForEachDefaultXmlAsset(
                                _assetLoaderArgs,
                                delegate (AssetBase a)
                                {
                                    Cache(a, true);
                                    ids.Add(a.ID);
                                });
                            _defaultAssets = ids;
                        }
                    }
                }
            }
        }

        private void ProcessWrites(object o)
        {
            try
            {
                WriteAssetInfo wai = (WriteAssetInfo)o;
                WriteFileCache(wai.filename,wai.asset,wai.replace);
                wai.asset = null;
                Thread.Yield();
            }
            catch{ }
        }

        ////////////////////////////////////////////////////////////
        // IAssetCache
        //
        private void UpdateWeakReference(string key, AssetBase asset)
        {
            lock(weakAssetReferencesLock)
            {
                if(weakAssetReferences.TryGetValue(key , out WeakReference aref))
                    aref.Target = asset;
                else
                    weakAssetReferences[key] = new WeakReference(asset);
            }
        }

        private void UpdateMemoryCache(string key, AssetBase asset)
        {
            _MemoryCache.AddOrUpdate(key, asset, _MemoryExpiration);
        }

        private void UpdateFileCache(string key, AssetBase asset, bool replace = false)
        {
            if(_assetFileWriteWorker == null)
                return;

            string filename = GetFileName(key);

            try
            {
                // Once we start writing, make sure we flag that we're writing
                // that object to the cache so that we don't try to write the
                // same file multiple times.
                lock (_CurrentlyWriting)
                {
                    if (_CurrentlyWriting.Contains(filename))
                        return;
                    else
                        _CurrentlyWriting.Add(filename);
                }

                if (_assetFileWriteWorker != null)
                {
                    WriteAssetInfo wai = new WriteAssetInfo()
                    {
                        filename = filename,
                        asset = asset,
                        replace = replace
                    };
                    _assetFileWriteWorker.Enqueue(wai);
                }
            }
            catch (Exception e)
            {
                _log.ErrorFormat(
                    "[FLOTSAM ASSET CACHE]: Failed to update cache for asset {0}.  Exception {1} {2}",
                    asset.ID, e.Message, e.StackTrace);
            }
        }

        public void Cache(AssetBase asset, bool replace = false)
        {
            // TODO: Spawn this off to some seperate thread to do the actual writing
            if (asset != null)
            {
                //_log.DebugFormat("[FLOTSAM ASSET CACHE]: Caching asset with id {0}", asset.ID);
                UpdateWeakReference(asset.ID, asset);

                if (_MemoryCacheEnabled)
                    UpdateMemoryCache(asset.ID, asset);

                if (_FileCacheEnabled)
                    UpdateFileCache(asset.ID, asset, replace);

                if (_negativeCacheEnabled)
                    _negativeCache.Remove(asset.ID);
            }
        }

        public void CacheNegative(string id)
        {
            if (_negativeCacheEnabled)
            {
                _negativeCache.Add(id, _negativeExpiration);
            }
        }

        /// <summary>
        /// Updates the cached file with the current time.
        /// </summary>
        /// <param name="filename">Filename.</param>
        /// <returns><c>true</c>, if the update was successful, false otherwise.</returns>
        private static bool UpdateFileLastAccessTime(string filename)
        {
            try
            {
                File.SetLastAccessTime(filename, DateTime.Now);
                return true;
            }
            catch (FileNotFoundException)
            {
                return false;
            }
            catch
            {
                return true; // ignore other errors
            }
        }

        private AssetBase GetFromWeakReference(string id)
        {
            AssetBase asset = null;

            lock(weakAssetReferencesLock)
            {
                if (weakAssetReferences.TryGetValue(id, out WeakReference aref))
                {
                    asset = aref.Target as AssetBase;
                    if(asset == null)
                        weakAssetReferences.Remove(id);
                    else
                        _weakRefHits++;
                }
            }
            return asset;
        }

        /// <summary>
        /// Try to get an asset from the in-memory cache.
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
        private AssetBase GetFromMemoryCache(string id)
        {
            if (_MemoryCache.TryGetValue(id, out AssetBase asset))
            {
                _MemoryHits++;
                return asset;
            }
            return null;
        }

        private bool CheckFromMemoryCache(string id)
        {
            return _MemoryCache.Contains(id);
        }

        /// <summary>
        /// Try to get an asset from the file cache.
        /// </summary>
        /// <param name="id"></param>
        /// <returns>An asset retrieved from the file cache.  null if there was a problem retrieving an asset.</returns>
        private AssetBase GetFromFileCache(string id)
        {
            string filename = GetFileName(id);

            // Track how often we have the problem that an asset is requested while
            // it is still being downloaded by a previous request.
            if (_CurrentlyWriting.Contains(filename))
            {
                _RequestsForInprogress++;
                return null;
            }

            AssetBase asset = null;

            try
            {
                using (FileStream stream = File.Open(filename, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (stream.Length == 0) // Empty file will trigger exception below
                        return null;
                    BinaryFormatter bformatter = new BinaryFormatter();

                    asset = (AssetBase)bformatter.Deserialize(stream);

                    _DiskHits++;
                }
            }
            catch (FileNotFoundException)
            {
            }
            catch (DirectoryNotFoundException)
            {
            }
            catch (System.Runtime.Serialization.SerializationException e)
            {
                _log.WarnFormat(
                    "[FLOTSAM ASSET CACHE]: Failed to get file {0} for asset {1}.  Exception {2} {3}",
                    filename, id, e.Message, e.StackTrace);

                // If there was a problem deserializing the asset, the asset may
                // either be corrupted OR was serialized under an old format
                // {different version of AssetBase} -- we should attempt to
                // delete it and re-cache
                File.Delete(filename);
            }
            catch (Exception e)
            {
                _log.WarnFormat(
                    "[FLOTSAM ASSET CACHE]: Failed to get file {0} for asset {1}.  Exception {2} {3}",
                    filename, id, e.Message, e.StackTrace);
            }

        return asset;
        }

        private bool CheckFromFileCache(string id)
        {
            try
            {
                return File.Exists(GetFileName(id));
            }
            catch
            {
            }
            return false;
        }

        // For IAssetService
        public AssetBase Get(string id)
        {
            Get(id, out AssetBase asset);
            return asset;
        }

        public AssetBase Get(string id, string ForeignAssetService, bool dummy)
        {
            return null;
        }

        public bool Get(string id, out AssetBase asset)
        {
            asset = null;

            _Requests++;

            if (_negativeCache.ContainsKey(id))
                return false;

            asset = GetFromWeakReference(id);
            if (asset != null)
            {
                if(_updateFileTimeOnCacheHit)
                {
                    string filename = GetFileName(id);
                    UpdateFileLastAccessTime(filename);
                }
                if (_MemoryCacheEnabled)
                    UpdateMemoryCache(id, asset);
                return true;
            }

            if (_MemoryCacheEnabled)
            {
                asset = GetFromMemoryCache(id);
                if(asset != null)
                {
                    UpdateWeakReference(id,asset);
                    if (_updateFileTimeOnCacheHit)
                    {
                        string filename = GetFileName(id);
                        UpdateFileLastAccessTime(filename);
                    }
                    return true;
                }
            }

            if (_FileCacheEnabled)
            {
                asset = GetFromFileCache(id);
                if(asset != null)
                {
                    UpdateWeakReference(id,asset);
                    if (_MemoryCacheEnabled)
                        UpdateMemoryCache(id, asset);
                }
            }
            return true;
        }

        public bool Check(string id)
        {
            if(GetFromWeakReference(id) != null)
                return true;

            if (_MemoryCacheEnabled && CheckFromMemoryCache(id))
                return true;

            if (_FileCacheEnabled && CheckFromFileCache(id))
                return true;
            return false;
        }

        // does not check negative cache
        public AssetBase GetCached(string id)
        {
            AssetBase asset = null;

            _Requests++;

            asset = GetFromWeakReference(id);
            if (asset != null)
            {
                if (_updateFileTimeOnCacheHit)
                {
                    string filename = GetFileName(id);
                    UpdateFileLastAccessTime(filename);
                }
                if (_MemoryCacheEnabled)
                    UpdateMemoryCache(id, asset);
                return asset;
            }

            if (_MemoryCacheEnabled)
            {
                asset = GetFromMemoryCache(id);
                if (asset != null)
                {
                    UpdateWeakReference(id, asset);
                    if (_updateFileTimeOnCacheHit)
                    {
                        string filename = GetFileName(id);
                        UpdateFileLastAccessTime(filename);
                    }
                    return asset;
                }
            }

            if (_FileCacheEnabled)
            {
                asset = GetFromFileCache(id);
                if (asset != null)
                {
                    UpdateWeakReference(id, asset);
                    if (_MemoryCacheEnabled)
                        UpdateMemoryCache(id, asset);
                }
            }
            return asset;
        }

        public void Expire(string id)
        {
            if (_LogLevel >= 2)
                _log.DebugFormat("[FLOTSAM ASSET CACHE]: Expiring Asset {0}", id);

            try
            {
                lock (weakAssetReferencesLock)
                    weakAssetReferences.Remove(id);

                if (_MemoryCacheEnabled)
                    _MemoryCache.Remove(id);

                if (_FileCacheEnabled)
                {
                    string filename = GetFileName(id);
                    File.Delete(filename);
                }
            }
            catch (Exception e)
            {
                if (_LogLevel >= 2)
                    _log.WarnFormat("[FLOTSAM ASSET CACHE]: Failed to expire cached file {0}.  Exception {1} {2}",
                        id, e.Message, e.StackTrace);
            }
        }

        public void Clear()
        {
            if (_LogLevel >= 2)
                _log.Debug("[FLOTSAM ASSET CACHE]: Clearing caches.");

            if (_FileCacheEnabled && Directory.Exists(_CacheDirectory))
            {
                foreach (string dir in Directory.GetDirectories(_CacheDirectory))
                {
                    try
                    {
                        Directory.Delete(dir, true);
                    }
                    catch { }
                }
            }

            if (_MemoryCacheEnabled)
            {
                _MemoryCache.Dispose();
                _MemoryCache = new ExpiringCacheOS<string, AssetBase>((int)_MemoryExpiration * 500);
            }
            if (_negativeCacheEnabled)
            {
                _negativeCache.Dispose();
                _negativeCache = new ExpiringKey<string>(2000);
            }

            lock (weakAssetReferencesLock)
                weakAssetReferences = new Dictionary<string, WeakReference>();
        }

        private async void CleanupExpiredFiles(object source, ElapsedEventArgs e)
        {
            lock (timerLock)
            {
                if (!_timerRunning || _cleanupRunning || !Directory.Exists(_CacheDirectory))
                    return;
                _cleanupRunning = true;
            }

            // Purge all files last accessed prior to this point
            await DoCleanExpiredFiles(DateTime.Now - _FileExpiration).ConfigureAwait(false);
        }

        private async Task DoCleanExpiredFiles(DateTime purgeLine)
        {
            long heap = 0;
            //if (_LogLevel >= 2)
            {
                _log.InfoFormat("[FLOTSAM ASSET CACHE]: Start background expiring files older than {0}.", purgeLine);
                heap = GC.GetTotalMemory(false);
            }

            // An asset cache may contain local non-temporary assets that are not in the asset service.  Therefore,
            // before cleaning up expired files we must scan the objects in the scene to make sure that we retain
            // such local assets if they have not been recently accessed.
            Dictionary<UUID,sbyte> gids = await gatherSceneAssets().ConfigureAwait(false);

            int cooldown = 0;
            _log.Info("[FLOTSAM ASSET CACHE] start asset files expire");
            foreach (string subdir in Directory.GetDirectories(_CacheDirectory))
            {
                if(!_cleanupRunning)
                    break;
                cooldown = await CleanExpiredFiles(subdir, gids, purgeLine, cooldown);
                if (++cooldown >= 10)
                {
                    await Task.Delay(100).ConfigureAwait(false);
                    cooldown = 0;
                }
            }

            gids = null;

            lock (timerLock)
            {
                if (_timerRunning)
                    _CacheCleanTimer.Start();
                _cleanupRunning = false;
            }
            //if (_LogLevel >= 2)
            {
                heap = GC.GetTotalMemory(false) - heap;
                double fheap = Math.Round((double)(heap / (1024 * 1024)), 3);
                _log.InfoFormat("[FLOTSAM ASSET CACHE]: Finished expiring files, heap delta: {0}MB.", fheap);
            }
        }

        /// <summary>
        /// Recurses through specified directory checking for asset files last
        /// accessed prior to the specified purge line and deletes them.  Also
        /// removes empty tier directories.
        /// </summary>
        /// <param name="dir"></param>
        /// <param name="purgeLine"></param>
        private async Task<int> CleanExpiredFiles(string dir, Dictionary<UUID, sbyte> gids, DateTime purgeLine, int cooldown)
        {
            try
            {
                if (!_cleanupRunning)
                    return cooldown;

                int dirSize = 0;

                // Recurse into lower tiers
                foreach (string subdir in Directory.GetDirectories(dir))
                {
                    if (!_cleanupRunning)
                        return cooldown;

                    ++dirSize;
                    cooldown = await CleanExpiredFiles(subdir, gids, purgeLine, cooldown);
                    if (++cooldown > 10)
                    {
                        await Task.Delay(100).ConfigureAwait(false);
                        cooldown = 0;
                    }
                }

                foreach (string file in Directory.GetFiles(dir))
                {
                    if (!_cleanupRunning)
                        return cooldown;

                    ++dirSize;
                    string id = Path.GetFileName(file);
                    if (string.IsNullOrEmpty(id))
                        continue; //??

                    if (_defaultAssets.Contains(id) ||UUID.TryParse(id, out UUID uid) && gids.ContainsKey(uid))
                    {
                        ++cooldown;
                        continue;
                    }

                    if (File.GetLastAccessTime(file) < purgeLine)
                    {
                        try
                        {
                            File.Delete(file);
                            lock (weakAssetReferencesLock)
                                weakAssetReferences.Remove(id);
                        }
                        catch { }
                        cooldown += 5;
                        --dirSize;
                    }

                    if (++cooldown >= 20)
                    {
                        await Task.Delay(100).ConfigureAwait(false);
                        cooldown = 0;
                    }
                }

                // Check if a tier directory is empty, if so, delete it
                if (_cleanupRunning && dirSize == 0)
                {
                    try
                    {
                        Directory.Delete(dir);
                    }
                    catch { }

                    cooldown += 5;
                    if (cooldown >= 20)
                    {
                        await Task.Delay(100).ConfigureAwait(false);
                        cooldown = 0;
                    }
                }
                else if (dirSize >= _CacheWarnAt)
                {
                    _log.WarnFormat(
                        "[FLOTSAM ASSET CACHE]: Cache folder exceeded CacheWarnAt limit {0} {1}.  Suggest increasing tiers, tier length, or reducing cache expiration",
                        dir, dirSize);
                }
            }
            catch (DirectoryNotFoundException)
            {
                // If we get here, another node on the same box has
                // already removed the directory. Continue with next.
            }
            catch (Exception e)
            {
                _log.WarnFormat("[FLOTSAM ASSET CACHE]: Could not complete clean of expired files in {0}, exception {1}", dir, e.Message);
            }
            return cooldown;
        }

        /// <summary>
        /// Determines the filename for an AssetID stored in the file cache
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
        private string GetFileName(string id)
        {
            StringBuilder sb = osStringBuilderCache.Acquire();
            int indx = id.IndexOfAny(_InvalidChars);
            if (indx >= 0)
            {
                sb.Append(id);
                int sublen = id.Length - indx;
                for(int i = 0; i < _InvalidChars.Length; ++i)
                {
                    sb.Replace(_InvalidChars[i], '_', indx, sublen);
                }
                id = sb.ToString();
                sb.Clear();
            }
            if(_CacheDirectoryTiers == 1)
            {
                sb.Append(id.Substring(0, _CacheDirectoryTierLen));
                sb.Append(Path.DirectorySeparatorChar);
            }
            else
            {
                for (int p = 0; p < _CacheDirectoryTiers * _CacheDirectoryTierLen; p += _CacheDirectoryTierLen)
                {
                    sb.Append(id.Substring(p, _CacheDirectoryTierLen));
                    sb.Append(Path.DirectorySeparatorChar);
                }
            }
            sb.Append(id);

            return Path.Combine(_CacheDirectory, osStringBuilderCache.GetStringAndRelease(sb));
        }

        /// <summary>
        /// Writes a file to the file cache, creating any necessary
        /// tier directories along the way
        /// </summary>
        /// <param name="filename"></param>
        /// <param name="asset"></param>
        private static void WriteFileCache(string filename, AssetBase asset, bool replace)
        {
            try
            {
                // If the file is already cached, don't cache it, just touch it so access time is updated
                if (!replace && File.Exists(filename))
                {
                    UpdateFileLastAccessTime(filename);
                    return;
                }

                string directory = Path.GetDirectoryName(filename);
                string tempname = Path.Combine(directory, Path.GetRandomFileName());
                try
                {
                    if (!Directory.Exists(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    using (Stream stream = File.Open(tempname, FileMode.Create))
                    {
                        BinaryFormatter bformatter = new BinaryFormatter();
                        bformatter.Serialize(stream, asset);
                        stream.Flush();
                    }
                }
                catch (IOException e)
                {
                    _log.WarnFormat(
                        "[FLOTSAM ASSET CACHE]: Failed to write asset {0} to temporary location {1} (final {2}) on cache in {3}.  Exception {4} {5}.",
                        asset.ID, tempname, filename, directory, e.Message, e.StackTrace);

                    return;
                }
                catch (UnauthorizedAccessException)
                {
                }

                try
                {
                    if(replace)
                        File.Delete(filename);
                    File.Move(tempname, filename);
                }
                catch
                {
                    try
                    {
                        File.Delete(tempname);
                    }
                    catch{ }
                    // If we see an IOException here it's likely that some other competing thread has written the
                    // cache file first, so ignore.  Other IOException errors (e.g. filesystem full) should be
                    // signally by the earlier temporary file writing code.
                }
            }
            finally
            {
                // Even if the write fails with an exception, we need to make sure
                // that we release the lock on that file, otherwise it'll never get
                // cached
                lock (_CurrentlyWriting)
                {
                    _CurrentlyWriting.Remove(filename);
                }
            }
        }

        /// <summary>
        /// Scan through the file cache, and return number of assets currently cached.
        /// </summary>
        /// <param name="dir"></param>
        /// <returns></returns>
        private int GetFileCacheCount(string dir)
        {
            try
            {
                int count = 0;
                int cooldown = 0;
                foreach (string subdir in Directory.GetDirectories(dir))
                {
                    count += GetFileCacheCount(subdir);
                    ++cooldown;
                    if(cooldown > 50)
                    {
                        Thread.Sleep(100);
                        cooldown = 0;
                    }
                }
                return count + Directory.GetFiles(dir).Length;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// This notes the last time the Region had a deep asset scan performed on it.
        /// </summary>
        /// <param name="regionID"></param>
        private void StampRegionStatusFile(UUID regionID)
        {
            string RegionCacheStatusFile = Path.Combine(_CacheDirectory, "RegionStatus_" + regionID.ToString() + ".fac");

            try
            {
                if (File.Exists(RegionCacheStatusFile))
                {
                    File.SetLastWriteTime(RegionCacheStatusFile, DateTime.Now);
                }
                else
                {
                    File.WriteAllText(
                        RegionCacheStatusFile,
                        "Please do not delete this file unless you are manually clearing your Flotsam Asset Cache.");
                }
            }
            catch (Exception e)
            {
                _log.WarnFormat("[FLOTSAM ASSET CACHE]: Could not stamp region status file for region {0}.  Exception {1}",
                        regionID, e. Message);
            }
        }

        /// <summary>
        /// Iterates through all Scenes, doing a deep scan through assets
        /// to update the access time of all assets present in the scene or referenced by assets
        /// in the scene.
        /// </summary>
        /// <param name="tryGetUncached">
        /// If true, then assets scanned which are not found in cache are added to the cache.
        /// </param>
        /// <returns>Number of distinct asset references found in the scene.</returns>
        private async Task<int> TouchAllSceneAssets(bool tryGetUncached)
        {
            _log.Info("[FLOTSAM ASSET CACHE] start touch files of assets in use");

            Dictionary<UUID,sbyte> gatheredids = await gatherSceneAssets();

            int cooldown = 0;
            foreach(UUID id in gatheredids.Keys)
            {
                if (!_cleanupRunning)
                    break;

                string idstr = id.ToString();
                if (!UpdateFileLastAccessTime(GetFileName(idstr)) && tryGetUncached)
                {
                    cooldown += 5;
                    _AssetService.Get(idstr);
                }
                if (++cooldown > 50)
                {
                    Thread.Sleep(50);
                    cooldown = 0;
                }
            }
            return gatheredids.Count;
        }

        private async Task<Dictionary<UUID, sbyte>> gatherSceneAssets()
        {
            _log.Info("[FLOTSAM ASSET CACHE] gather assets in use");

            Dictionary<UUID, sbyte> gatheredids = new Dictionary<UUID, sbyte>();
            UuidGatherer gatherer = new UuidGatherer(_AssetService, gatheredids);

            int cooldown = 0;
            foreach (Scene s in _Scenes)
            {
                gatherer.AddGathered(s.RegionInfo.RegionSettings.TerrainTexture1, (sbyte)AssetType.Texture);
                gatherer.AddGathered(s.RegionInfo.RegionSettings.TerrainTexture2, (sbyte)AssetType.Texture);
                gatherer.AddGathered(s.RegionInfo.RegionSettings.TerrainTexture3, (sbyte)AssetType.Texture);
                gatherer.AddGathered(s.RegionInfo.RegionSettings.TerrainTexture4, (sbyte)AssetType.Texture);
                gatherer.AddGathered(s.RegionInfo.RegionSettings.TerrainImageID, (sbyte)AssetType.Texture);

                if (s.RegionEnvironment != null)
                    s.RegionEnvironment.GatherAssets(gatheredids);

                if (s.LandChannel != null)
                {
                    List<ILandObject> landObjects = s.LandChannel.AllParcels();
                    foreach (ILandObject lo in landObjects)
                    {
                        if (lo.LandData != null && lo.LandData.Environment != null)
                            lo.LandData.Environment.GatherAssets(gatheredids);
                    }
                }

                EntityBase[] entities = s.Entities.GetEntities();
                for (int i = 0; i < entities.Length; ++i)
                {
                    if (!_cleanupRunning)
                        break;

                    EntityBase entity = entities[i];
                    if (entity is SceneObjectGroup)
                    {
                        SceneObjectGroup e = entity as SceneObjectGroup;
                        if (e.IsDeleted)
                            continue;

                        gatherer.AddForInspection(e);
                        while (gatherer.GatherNext())
                        {
                            if (++cooldown > 50)
                            {
                                await Task.Delay(60).ConfigureAwait(false);
                                cooldown = 0;
                            }
                        }
                        if (++cooldown > 25)
                        {
                            await Task.Delay(60).ConfigureAwait(false);
                            cooldown = 0;
                        }
                    }
                }
                entities = null;
                if (!_cleanupRunning)
                    break;

                StampRegionStatusFile(s.RegionInfo.RegionID);
            }

            gatherer.GatherAll();

            gatherer.FailedUUIDs.Clear();
            gatherer.UncertainAssetsUUIDs.Clear();
            gatherer = null;

            _log.InfoFormat("[FLOTSAM ASSET CACHE]     found {0} possible assets in use (including {1} default assets)",
                    gatheredids.Count + _defaultAssets.Count, _defaultAssets.Count);
            return gatheredids;
        }

        /// <summary>
        /// Deletes all cache contents
        /// </summary>
        private void ClearFileCache()
        {
            if(!Directory.Exists(_CacheDirectory))
                return;

            foreach (string dir in Directory.GetDirectories(_CacheDirectory))
            {
                try
                {
                    Directory.Delete(dir, true);
                }
                catch (Exception e)
                {
                    _log.WarnFormat(
                        "[FLOTSAM ASSET CACHE]: Couldn't clear asset cache directory {0} from {1}.  Exception {2} {3}",
                        dir, _CacheDirectory, e.Message, e.StackTrace);
                }
            }

            foreach (string file in Directory.GetFiles(_CacheDirectory))
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception e)
                {
                    _log.WarnFormat(
                        "[FLOTSAM ASSET CACHE]: Couldn't clear asset cache file {0} from {1}.  Exception {1} {2}",
                        file, _CacheDirectory, e.Message, e.StackTrace);
                }
            }
        }

        private List<string> GenerateCacheHitReport()
        {
            List<string> outputLines = new List<string>();

            double invReq = 100.0 / _Requests;

            double weakHitRate = _weakRefHits * invReq;
            int weakEntriesAlive = 0;
            lock(weakAssetReferencesLock)
            {
                foreach(WeakReference aref in weakAssetReferences.Values)
                {
                    if (aref.IsAlive)
                        ++weakEntriesAlive;
                }
            }
            int weakEntries = weakAssetReferences.Count;

            double fileHitRate = _DiskHits * invReq;
            double TotalHitRate = weakHitRate + fileHitRate;

            outputLines.Add(
                string.Format("Total requests: {0}", _Requests));
            outputLines.Add(
                string.Format("unCollected Hit Rate: {0}% ({1} entries {2} alive)", weakHitRate.ToString("0.00"),weakEntries, weakEntriesAlive));
            outputLines.Add(
                string.Format("File Hit Rate: {0}%", fileHitRate.ToString("0.00")));

            if (_MemoryCacheEnabled)
            {
                double HitRate = _MemoryHits * invReq;
                outputLines.Add(
                    string.Format("Memory Hit Rate: {0}%", HitRate.ToString("0.00")));

                TotalHitRate += HitRate;
            }
            outputLines.Add(
                string.Format("Total Hit Rate: {0}%", TotalHitRate.ToString("0.00")));

            outputLines.Add(
                string.Format(
                    "Requests overlap during file writing: {0}", _RequestsForInprogress));

            return outputLines;
        }

        #region Console Commands
        private void HandleConsoleCommand(string module, string[] cmdparams)
        {
            ICommandConsole con = MainConsole.Instance;

            if (cmdparams.Length >= 2)
            {
                string cmd = cmdparams[1];

                switch (cmd)
                {
                    case "status":
                    {
                        WorkManager.RunInThreadPool(delegate
                        {
                            if (_MemoryCacheEnabled)
                                con.Output("[FLOTSAM ASSET CACHE] Memory Cache: {0} assets", _MemoryCache.Count);
                            else
                                con.Output("[FLOTSAM ASSET CACHE] Memory cache disabled");

                            if (_FileCacheEnabled)
                            {
                                bool doingscan;
                                lock (timerLock)
                                {
                                    doingscan = _cleanupRunning;
                                }
                                if(doingscan)
                                {
                                    con.Output("[FLOTSAM ASSET CACHE] a deep scan is in progress, skipping file cache assets count");
                                }
                                else
                                {
                                    con.Output("[FLOTSAM ASSET CACHE] counting file cache assets");
                                    int fileCount = GetFileCacheCount(_CacheDirectory);
                                    con.Output("[FLOTSAM ASSET CACHE]   File Cache: {0} assets", fileCount);
                                }
                            }
                            else
                            {
                                con.Output("[FLOTSAM ASSET CACHE] File cache disabled");
                            }

                            GenerateCacheHitReport().ForEach(l => con.Output(l));

                            if (_FileCacheEnabled)
                            {
                                con.Output("[FLOTSAM ASSET CACHE] Deep scans have previously been performed on the following regions:");

                                foreach (string s in Directory.GetFiles(_CacheDirectory, "*.fac"))
                                {
                                    int start = s.IndexOf('_');
                                    int end = s.IndexOf('.');
                                    if(start > 0 && end > 0)
                                    {
                                        string RegionID = s.Substring(start + 1, end - start);
                                        DateTime RegionDeepScanTMStamp = File.GetLastWriteTime(s);
                                        con.Output("[FLOTSAM ASSET CACHE] Region: {0}, {1}", RegionID, RegionDeepScanTMStamp.ToString("MM/dd/yyyy hh:mm:ss"));
                                    }
                                }
                            }
                        }, null, "CacheStatus", false);

                        break;
                    }
                    case "clear":
                        if (cmdparams.Length < 2)
                        {
                            con.Output("Usage is fcache clear [file] [memory]");
                            break;
                        }

                        bool clearMemory = false, clearFile = false;

                        if (cmdparams.Length == 2)
                        {
                            clearMemory = true;
                            clearFile = true;
                        }
                        foreach (string s in cmdparams)
                        {
                            if (s.ToLower() == "memory")
                                clearMemory = true;
                            else if (s.ToLower() == "file")
                                clearFile = true;
                        }

                        if (clearMemory)
                        {
                            if (_MemoryCacheEnabled)
                            {
                                _MemoryCache.Clear();
                                con.Output("Memory cache cleared.");
                            }
                            else
                            {
                                con.Output("Memory cache not enabled.");
                            }
                        }

                        if (clearFile)
                        {
                            if (_FileCacheEnabled)
                            {
                                ClearFileCache();
                                con.Output("File cache cleared.");
                            }
                            else
                            {
                                con.Output("File cache not enabled.");
                            }
                        }

                        break;

                    case "assets":
                        lock (timerLock)
                        {
                            if (_cleanupRunning)
                            {
                                con.Output("Flotsam assets check already running");
                                return;
                            }
                            _cleanupRunning = true;
                        }

                        con.Output("Flotsam Ensuring assets are cached for all scenes.");

                        WorkManager.RunInThreadPool(async delegate
                        {
                            bool wasRunning= false;
                            lock(timerLock)
                            {
                                if(_timerRunning)
                                {
                                    _CacheCleanTimer.Stop();
                                    _timerRunning = false;
                                    wasRunning = true;
                                }
                            }

                            if (wasRunning)
                                await Task.Delay(100).ConfigureAwait(false);

                            int assetReferenceTotal = await TouchAllSceneAssets(true).ConfigureAwait(false);

                            lock(timerLock)
                            {
                                if(wasRunning)
                                {
                                    _CacheCleanTimer.Start();
                                    _timerRunning = true;
                                }
                                _cleanupRunning = false;
                            }
                            con.Output("Completed check with {0} assets.", assetReferenceTotal);
                        }, null, "TouchAllSceneAssets", false);

                        break;

                    case "expire":
                        lock (timerLock)
                        {
                            if (_cleanupRunning)
                            {
                                con.Output("Flotsam assets check already running");
                                return;
                            }
                            _cleanupRunning = true;
                        }

                        if (cmdparams.Length < 3)
                        {
                            con.Output("Invalid parameters for Expire, please specify a valid date & time");
                            break;
                        }

                        string s_expirationDate = "";
                        DateTime expirationDate;

                        if (cmdparams.Length > 3)
                        {
                            s_expirationDate = string.Join(" ", cmdparams, 2, cmdparams.Length - 2);
                        }
                        else
                        {
                            s_expirationDate = cmdparams[2];
                        }

                        if (!DateTime.TryParse(s_expirationDate, out expirationDate))
                        {
                            con.Output("{0} is not a valid date & time", cmd);
                            break;
                        }
                        if (expirationDate >= DateTime.Now)
                        {
                            con.Output("{0} date & time must be in past", cmd);
                            break;
                        }
                        if (_FileCacheEnabled)
                        {
                            WorkManager.RunInThreadPool(async delegate
                            {
                                bool wasRunning = false;
                                lock (timerLock)
                                {
                                    if (_timerRunning)
                                    {
                                        _CacheCleanTimer.Stop();
                                        _timerRunning = false;
                                        wasRunning = true;
                                    }
                                }

                                if(wasRunning)
                                    await Task.Delay(100).ConfigureAwait(false);

                                await DoCleanExpiredFiles(expirationDate).ConfigureAwait(false);

                                lock (timerLock)
                                {
                                    if (wasRunning)
                                    {
                                        _CacheCleanTimer.Start();
                                        _timerRunning = true;
                                    }
                                    _cleanupRunning = false;
                                }
                            }, null, "TouchAllSceneAssets", false);
                        }
                        else
                            con.Output("File cache not active, not clearing.");

                        break;
                    case "cachedefaultassets":
                        HandleLoadDefaultAssets();
                        break;
                    case "deletedefaultassets":
                        HandleDeleteDefaultAssets();
                        break;
                    default:
                        con.Output("Unknown command {0}", cmd);
                        break;
                }
            }
            else if (cmdparams.Length == 1)
            {
                con.Output("fcache assets - Attempt a deep cache of all assets in all scenes");
                con.Output("fcache expire <datetime> - Purge assets older than the specified date & time");
                con.Output("fcache clear [file] [memory] - Remove cached assets");
                con.Output("fcache status - Display cache status");
                con.Output("fcache cachedefaultassets - loads default assets to cache replacing existent ones, this may override grid assets. Use with care");
                con.Output("fcache deletedefaultassets - deletes default local assets from cache so they can be refreshed from grid");
            }
        }

        #endregion

        #region IAssetService Members

        public AssetMetadata GetMetadata(string id)
        {
            Get(id, out AssetBase asset);
            if (asset == null)
                return null;
            return asset.Metadata;
        }

        public byte[] GetData(string id)
        {
            Get(id, out AssetBase asset);
            if (asset == null)
                return null;
            return asset.Data;
        }

        public bool Get(string id, object sender, AssetRetrieved handler)
        {
            if (!Get(id, out AssetBase asset))
                return false;
            handler(id, sender, asset);
            return true;
        }

        public bool[] AssetsExist(string[] ids)
        {
            bool[] exist = new bool[ids.Length];

            for (int i = 0; i < ids.Length; i++)
            {
                exist[i] = Check(ids[i]);
            }

            return exist;
        }

        public string Store(AssetBase asset)
        {
            if (asset.FullID == UUID.Zero)
            {
                asset.FullID = UUID.Random();
            }

            Cache(asset);

            return asset.ID;
        }

        public bool UpdateContent(string id, byte[] data)
        {
            if (!Get(id, out AssetBase asset))
                return false;
            asset.Data = data;
            Cache(asset, true);
            return true;
        }

        public bool Delete(string id)
        {
            Expire(id);
            return true;
        }

        private void HandleLoadDefaultAssets()
        {
            if (string.IsNullOrWhiteSpace(_assetLoader))
            {
                _log.Info("[FLOTSAM ASSET CACHE] default assets loader not defined");
                return;
            }

            IAssetLoader assetLoader = ServerUtils.LoadPlugin<IAssetLoader>(_assetLoader, new object[] { });
            if (assetLoader == null)
            {
                _log.Info("[FLOTSAM ASSET CACHE] default assets loader not found");
                return;
            }

            _log.Info("[FLOTSAM ASSET CACHE] start loading local default assets");

            int count = 0;
            HashSet<string> ids = new HashSet<string>();
            assetLoader.ForEachDefaultXmlAsset(
                    _assetLoaderArgs,
                    delegate (AssetBase a)
                    {
                        Cache(a, true);
                        ids.Add(a.ID);
                        ++count;
                    });
            _defaultAssets = ids;
            _log.InfoFormat("[FLOTSAM ASSET CACHE] loaded {0} local default assets", count);
        }

        private void HandleDeleteDefaultAssets()
        {
            if (string.IsNullOrWhiteSpace(_assetLoader))
            {
                _log.Info("[FLOTSAM ASSET CACHE] default assets loader not defined");
                return;
            }

            IAssetLoader assetLoader = ServerUtils.LoadPlugin<IAssetLoader>(_assetLoader, new object[] { });
            if (assetLoader == null)
            {
                _log.Info("[FLOTSAM ASSET CACHE] default assets loader not found");
                return;
            }

            _log.Info("[FLOTSAM ASSET CACHE] started deleting local default assets");
            int count = 0;
            assetLoader.ForEachDefaultXmlAsset(
                    _assetLoaderArgs,
                    delegate (AssetBase a)
                    {
                        Expire(a.ID);
                        ++count;
                    });
            _defaultAssets = new HashSet<string>();
            _log.InfoFormat("[FLOTSAM ASSET CACHE] deleted {0} local default assets", count);
        }
        #endregion
    }
}
