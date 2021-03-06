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
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Net;
using System.Threading;

using log4net;
using Nini.Config;

using OpenMetaverse;
using Mono.Addins;

using OpenSim.Framework;
using OpenSim.Framework.Console;
using OpenSim.Region.CoreModules.Framework.InterfaceCommander;
using OpenSim.Region.CoreModules.World.Terrain.FileLoaders;
using OpenSim.Region.CoreModules.World.Terrain.Modifiers;
using OpenSim.Region.CoreModules.World.Terrain.FloodBrushes;
using OpenSim.Region.CoreModules.World.Terrain.PaintBrushes;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

namespace OpenSim.Region.CoreModules.World.Terrain
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "TerrainModule")]
    public class TerrainModule : INonSharedRegionModule, ICommandableModule, ITerrainModule
    {
        #region StandardTerrainEffects enum

        /// <summary>
        /// A standard set of terrain brushes and effects recognised by viewers
        /// </summary>
        public enum StandardTerrainEffects : byte
        {
            Flatten = 0,
            Raise = 1,
            Lower = 2,
            Smooth = 3,
            Noise = 4,
            Revert = 5,
        }

        #endregion

        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

#pragma warning disable 414
        private static readonly string LogHeader = "[TERRAIN MODULE]";
#pragma warning restore 414

        private readonly Commander _commander = new Commander("terrain");
        private readonly Dictionary<string, ITerrainLoader> _loaders = new Dictionary<string, ITerrainLoader>();
        private readonly Dictionary<StandardTerrainEffects, ITerrainFloodEffect> _floodeffects =
            new Dictionary<StandardTerrainEffects, ITerrainFloodEffect>();
        private readonly Dictionary<StandardTerrainEffects, ITerrainPaintableEffect> _painteffects =
            new Dictionary<StandardTerrainEffects, ITerrainPaintableEffect>();
        private readonly Dictionary<string, ITerrainModifier> _modifyOperations = new Dictionary<string, ITerrainModifier>();
        private Dictionary<string, ITerrainEffect> _plugineffects;
        private ITerrainChannel _channel;
        private ITerrainChannel _baked;
        private Scene _scene;
        private volatile bool _tainted;

        private string _InitialTerrain = "pinhead-island";

        // If true, send terrain patch updates to clients based on their view distance
        private bool _sendTerrainUpdatesByViewDistance = true;

        // Class to keep the per client collection of terrain patches that must be sent.
        // A patch is set to 'true' meaning it should be sent to the client. Once the
        //    patch packet is queued to the client, the bit for that patch is set to 'false'.
        private class PatchUpdates
        {
            private readonly BitArray updated;    // for each patch, whether it needs to be sent to this client
            private int updateCount;    // number of patches that need to be sent
            public readonly ScenePresence Presence;   // a reference to the client to send to
            public bool sendAll;
            public int sendAllcurrentX;
            public int sendAllcurrentY;
            private readonly int xsize;
            private readonly int ysize;

            public PatchUpdates(TerrainData terrData, ScenePresence pPresence)
            {
                xsize = terrData.SizeX / Constants.TerrainPatchSize;
                ysize = terrData.SizeY / Constants.TerrainPatchSize;
                updated = new BitArray(xsize * ysize, true);
                updateCount = xsize * ysize;
                Presence = pPresence;
                // Initially, send all patches to the client
                sendAll = true;
                sendAllcurrentX = 0;
                sendAllcurrentY = 0;
            }

            public PatchUpdates(TerrainData terrData, ScenePresence pPresence, bool defaultState)
            {
                xsize = terrData.SizeX / Constants.TerrainPatchSize;
                ysize = terrData.SizeY / Constants.TerrainPatchSize;
                updated = new BitArray(xsize * ysize, true);
                updateCount = defaultState ? xsize * ysize : 0;
                Presence = pPresence;
                sendAll = defaultState;
                sendAllcurrentX = 0;
                sendAllcurrentY = 0;
            }

            // Returns 'true' if there are any patches marked for sending
            [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
            public bool HasUpdates()
            {
                return updateCount > 0;
            }

            [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
            public void SetByXY(int x, int y, bool state)
            {
                SetByPatch(x / Constants.TerrainPatchSize , y / Constants.TerrainPatchSize, state);
            }

            [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
            public bool GetByPatch(int patchX, int patchY)
            {
                return updated[patchX + xsize * patchY];
            }

            [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
            public bool GetByPatch(int indx)
            {
                return updated[indx];
            }

            [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
            public bool GetByPatchAndClear(int patchX, int patchY)
            {
                int indx = patchX + xsize * patchY;
                if(updated[indx])
                {
                    updated[indx] = false;
                    --updateCount;
                    return true;
                }
                return false;
            }

            [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
            public void SetByPatch(int patchX, int patchY, bool state)
            {
                int indx = patchX + xsize * patchY;
                bool prevState = updated[indx];
                updated[indx] = state;
                if (state)
                {
                    if (!prevState)
                        ++updateCount;
                }
                else
                {
                    if (prevState)
                        --updateCount;
                }
            }

            [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
            public void SetTrueByPatch(int patchX, int patchY)
            {
                int indx = patchX + xsize * patchY;
                if (!updated[indx])
                {
                    updated[indx] = true;
                    ++updateCount;
                }
            }

            [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
            public void SetTrueByPatch(int indx)
            {
                if (!updated[indx])
                {
                    updated[indx] = true;
                    ++updateCount;
                }
            }

            [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
            public void SetFalseByPatch(int patchX, int patchY)
            {
                int indx = patchX + xsize * patchY;
                if (updated[indx])
                {
                    updated[indx] = false;
                    --updateCount;
                }
            }

            [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
            public void SetFalseByPatch(int indx)
            {
                if (updated[indx])
                {
                    updated[indx] = false;
                    --updateCount;
                }
            }

            public void SetAll(bool state)
            {
                updated.SetAll(state);

                if (state)
                {
                    sendAll = true;
                    updateCount = xsize * ysize;
                }
                else updateCount = 0;

                sendAllcurrentX = 0;
                sendAllcurrentY = 0;
            }

            // Logically OR's the terrain data's patch taint map into this client's update map.
            public void SetAll(TerrainData terrData)
            {
                if (xsize != terrData.SizeX / Constants.TerrainPatchSize
                    || ysize != terrData.SizeY / Constants.TerrainPatchSize)
                {
                    throw new Exception(
                        string.Format("{0} PatchUpdates.SetAll: patch array not same size as terrain. arr=<{1},{2}>, terr=<{3},{4}>",
                                LogHeader, xsize, ysize,
                                terrData.SizeX / Constants.TerrainPatchSize, terrData.SizeY / Constants.TerrainPatchSize)
                    );
                }

                for (int indx = 0; indx < updated.Length; ++indx)
                {
                    if (terrData.IsTaintedAtPatch(indx))
                        SetTrueByPatch(indx);
                }
            }
        }

        // The flags of which terrain patches to send for each of the ScenePresence's
        private readonly Dictionary<UUID, PatchUpdates> _perClientPatchUpdates = new Dictionary<UUID, PatchUpdates>();

        /// <summary>
        /// Human readable list of terrain file extensions that are supported.
        /// </summary>
        private string _supportedFileExtensions = "";

        //For terrain save-tile file extensions
        private string _supportFileExtensionsForTileSave = "";

        #region ICommandableModule Members

        public ICommander CommandInterface => _commander;

        #endregion

        #region INonSharedRegionModule Members

        /// <summary>
        /// Creates and initialises a terrain module for a region
        /// </summary>
        /// <param name="scene">Region initialising</param>
        /// <param name="config">Config for the region</param>
        public void Initialise(IConfigSource config)
        {
            IConfig terrainConfig = config.Configs["Terrain"];
            if (terrainConfig != null)
            {
                _InitialTerrain = terrainConfig.GetString("InitialTerrain", _InitialTerrain);
                _sendTerrainUpdatesByViewDistance =
                    terrainConfig.GetBoolean(
                        "SendTerrainUpdatesByViewDistance",_sendTerrainUpdatesByViewDistance);
            }
        }

        public void AddRegion(Scene scene)
        {
            _scene = scene;

            // Install terrain module in the simulator
            lock(_scene)
            {
                if(_scene.Bakedmap != null)
                {
                    _baked = _scene.Bakedmap;
                }
                if (_scene.Heightmap == null)
                {
                    if(_baked != null)
                        _channel = _baked.MakeCopy();
                    else
                        _channel = new TerrainChannel(_InitialTerrain,
                                                (int)_scene.RegionInfo.RegionSizeX,
                                                (int)_scene.RegionInfo.RegionSizeY,
                                                (int)_scene.RegionInfo.RegionSizeZ);
                    _scene.Heightmap = _channel;
                }
                else
                {
                    _channel = _scene.Heightmap;
                }
                if(_baked == null)
                    UpdateBakedMap();

                _scene.RegisterModuleInterface<ITerrainModule>(this);
                _scene.EventManager.OnNewClient += EventManager_OnNewClient;
                _scene.EventManager.OnClientClosed += EventManager_OnClientClosed;
                _scene.EventManager.OnPluginConsole += EventManager_OnPluginConsole;
                _scene.EventManager.OnTerrainTick += EventManager_OnTerrainTick;
                _scene.EventManager.OnTerrainCheckUpdates += EventManager_TerrainCheckUpdates;
            }

            InstallDefaultEffects();
            LoadPlugins();

            // Generate user-readable extensions list
            string supportedFilesSeparator = "";
            string supportedFilesSeparatorForTileSave = "";

            _supportFileExtensionsForTileSave = "";
            foreach(KeyValuePair<string, ITerrainLoader> loader in _loaders)
            {
                _supportedFileExtensions += supportedFilesSeparator + loader.Key + " (" + loader.Value + ")";
                supportedFilesSeparator = ", ";

                //For terrain save-tile file extensions
                if (loader.Value.SupportsTileSave() == true)
                {
                    _supportFileExtensionsForTileSave += supportedFilesSeparatorForTileSave + loader.Key + " (" + loader.Value + ")";
                    supportedFilesSeparatorForTileSave = ", ";
                }
            }
        }

        public void RegionLoaded(Scene scene)
        {
            //Do this here to give file loaders time to initialize and
            //register their supported file extensions and file formats.
            InstallInterfaces();
        }

        public void RemoveRegion(Scene scene)
        {
            lock(_scene)
            {
                // remove the commands
                _scene.UnregisterModuleCommander(_commander.Name);
                // remove the event-handlers

                _scene.EventManager.OnTerrainCheckUpdates -= EventManager_TerrainCheckUpdates;
                _scene.EventManager.OnTerrainTick -= EventManager_OnTerrainTick;
                _scene.EventManager.OnPluginConsole -= EventManager_OnPluginConsole;
                _scene.EventManager.OnClientClosed -= EventManager_OnClientClosed;
                _scene.EventManager.OnNewClient -= EventManager_OnNewClient;
                // remove the interface
                _scene.UnregisterModuleInterface<ITerrainModule>(this);
            }
        }

        public void Close()
        {
        }

        public Type ReplaceableInterface => null;

        public string Name => "TerrainModule";

        #endregion

        #region ITerrainModule Members

        public void UndoTerrain(ITerrainChannel channel)
        {
            _channel = channel;
        }

        /// <summary>
        /// Loads a terrain file from disk and installs it in the scene.
        /// </summary>
        /// <param name="filename">Filename to terrain file. Type is determined by extension.</param>
        public void LoadFromFile(string filename)
        {
            foreach(KeyValuePair<string, ITerrainLoader> loader in _loaders)
            {
                if (filename.EndsWith(loader.Key))
                {
                    lock(_scene)
                    {
                        try
                        {
                            ITerrainChannel channel = loader.Value.LoadFile(filename);
                            if (channel.Width != _scene.RegionInfo.RegionSizeX || channel.Height != _scene.RegionInfo.RegionSizeY)
                            {
                                // TerrainChannel expects a RegionSize x RegionSize map, currently
                                throw new ArgumentException(string.Format("wrong size, use a file with size {0} x {1}",
                                                                          _scene.RegionInfo.RegionSizeX, _scene.RegionInfo.RegionSizeY));
                            }
                            _log.DebugFormat("[TERRAIN]: Loaded terrain, wd/ht: {0}/{1}", channel.Width, channel.Height);
                            _scene.Heightmap = channel;
                            _channel = channel;
                            UpdateBakedMap();
                        }
                        catch(NotImplementedException)
                        {
                            _log.Error("[TERRAIN]: Unable to load heightmap, the " + loader.Value +
                                        " parser does not support file loading. (May be save only)");
                            throw new TerrainException(string.Format("unable to load heightmap: parser {0} does not support loading", loader.Value));
                        }
                        catch(FileNotFoundException)
                        {
                            _log.Error(
                                "[TERRAIN]: Unable to load heightmap, file not found. (A directory permissions error may also cause this)");
                            throw new TerrainException(
                                string.Format("unable to load heightmap: file {0} not found (or permissions do not allow access", filename));
                        }
                        catch(ArgumentException e)
                        {
                            _log.ErrorFormat("[TERRAIN]: Unable to load heightmap: {0}", e.Message);
                            throw new TerrainException(
                                string.Format("Unable to load heightmap: {0}", e.Message));
                        }
                    }
                    _log.Info("[TERRAIN]: File (" + filename + ") loaded successfully");
                    return;
                }
            }

            _log.Error("[TERRAIN]: Unable to load heightmap, no file loader available for that format.");
            throw new TerrainException(string.Format("unable to load heightmap from file {0}: no loader available for that format", filename));
        }

        /// <summary>
        /// Saves the current heightmap to a specified file.
        /// </summary>
        /// <param name="filename">The destination filename</param>
        public void SaveToFile(string filename)
        {
            try
            {
                foreach(KeyValuePair<string, ITerrainLoader> loader in _loaders)
                {
                    if (filename.EndsWith(loader.Key))
                    {
                        loader.Value.SaveFile(filename, _channel);
                        _log.InfoFormat("[TERRAIN]: Saved terrain from {0} to {1}", _scene.RegionInfo.RegionName, filename);
                        return;
                    }
                }
            }
            catch(IOException ioe)
            {
                _log.Error(string.Format("[TERRAIN]: Unable to save to {0}, {1}", filename, ioe.Message));
            }

            _log.ErrorFormat(
                "[TERRAIN]: Could not save terrain from {0} to {1}.  Valid file extensions are {2}",
                _scene.RegionInfo.RegionName, filename, _supportedFileExtensions);
        }

        /// <summary>
        /// Loads a terrain file from the specified URI
        /// </summary>
        /// <param name="filename">The name of the terrain to load</param>
        /// <param name="pathToTerrainHeightmap">The URI to the terrain height map</param>
        public void LoadFromStream(string filename, Uri pathToTerrainHeightmap)
        {
            LoadFromStream(filename, URIFetch(pathToTerrainHeightmap));
        }

        public void LoadFromStream(string filename, Stream stream)
        {
            LoadFromStream(filename, Vector3.Zero, 0f, Vector2.Zero, stream);
        }

        /// <summary>
        /// Loads a terrain file from a stream and installs it in the scene.
        /// </summary>
        /// <param name="filename">Filename to terrain file. Type is determined by extension.</param>
        /// <param name="stream"></param>
        public void LoadFromStream(string filename, Vector3 displacement,
                                float radianRotation, Vector2 rotationDisplacement, Stream stream)
        {
            foreach(KeyValuePair<string, ITerrainLoader> loader in _loaders)
            {
                if (filename.EndsWith(loader.Key))
                {
                    lock(_scene)
                    {
                        try
                        {
                            ITerrainChannel channel = loader.Value.LoadStream(stream);
                            _channel.Merge(channel, displacement, radianRotation, rotationDisplacement);
                            UpdateBakedMap();
                        }
                        catch(NotImplementedException)
                        {
                            _log.Error("[TERRAIN]: Unable to load heightmap, the " + loader.Value +
                                        " parser does not support file loading. (May be save only)");
                            throw new TerrainException(string.Format("unable to load heightmap: parser {0} does not support loading", loader.Value));
                        }
                    }

                    _log.Info("[TERRAIN]: File (" + filename + ") loaded successfully");
                    return;
                }
            }
            _log.Error("[TERRAIN]: Unable to load heightmap, no file loader available for that format.");
            throw new TerrainException(string.Format("unable to load heightmap from file {0}: no loader available for that format", filename));
        }

        public void LoadFromStream(string filename, Vector3 displacement,
                                    float rotationDegrees, Vector2 boundingOrigin, Vector2 boundingSize, Stream stream)
        {
            foreach (KeyValuePair<string, ITerrainLoader> loader in _loaders)
            {
                if (filename.EndsWith(loader.Key))
                {
                    lock (_scene)
                    {
                        try
                        {
                            ITerrainChannel channel = loader.Value.LoadStream(stream);
                            _channel.MergeWithBounding(channel, displacement, rotationDegrees, boundingOrigin, boundingSize);
                            UpdateBakedMap();
                        }
                        catch (NotImplementedException)
                        {
                            _log.Error("[TERRAIN]: Unable to load heightmap, the " + loader.Value +
                                        " parser does not support file loading. (May be save only)");
                            throw new TerrainException(string.Format("unable to load heightmap: parser {0} does not support loading", loader.Value));
                        }
                    }

                    _log.Info("[TERRAIN]: File (" + filename + ") loaded successfully");
                    return;
                }
            }
            _log.Error("[TERRAIN]: Unable to load heightmap, no file loader available for that format.");
            throw new TerrainException(string.Format("unable to load heightmap from file {0}: no loader available for that format", filename));
        }

        private static Stream URIFetch(Uri uri)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(uri);

            // request.Credentials = credentials;

            request.ContentLength = 0;
            request.KeepAlive = false;

            WebResponse response = request.GetResponse();
            Stream file = response.GetResponseStream();

            if (response.ContentLength == 0)
                throw new Exception(string.Format("{0} returned an empty file", uri.ToString()));

            // return new BufferedStream(file, (int) response.ContentLength);
            return new BufferedStream(file, 1000000);
        }

        /// <summary>
        /// Modify Land
        /// </summary>
        /// <param name="pos">Land-position (X,Y,0)</param>
        /// <param name="size">The size of the brush (0=small, 1=medium, 2=large)</param>
        /// <param name="action">0=LAND_LEVEL, 1=LAND_RAISE, 2=LAND_LOWER, 3=LAND_SMOOTH, 4=LAND_NOISE, 5=LAND_REVERT</param>
        /// <param name="agentId">UUID of script-owner</param>
        public void ModifyTerrain(UUID user, Vector3 pos, byte size, byte action)
        {
            float duration;
            float brushSize;
            if (size > 2)
            {
                size = 3;
                brushSize = 4.0f;
            }
            else
            {
                size++;
                brushSize = size;
            }

            switch((StandardTerrainEffects)action)
            {
                case StandardTerrainEffects.Flatten:
                    duration = 7.29f * size * size;
                    break;
                case StandardTerrainEffects.Smooth:
                case StandardTerrainEffects.Revert:
                    duration = 0.06f * size * size;
                    break;
                case StandardTerrainEffects.Noise:
                    duration = 0.46f * size * size;
                    break;
                default:
                    duration = 0.25f;
                    break;
            }

            client_OnModifyTerrain(user, pos.Z, duration, brushSize, action, pos.Y, pos.X, pos.Y, pos.X, -1);
        }

        /// <summary>
        /// Saves the current heightmap to a specified stream.
        /// </summary>
        /// <param name="filename">The destination filename.  Used here only to identify the image type</param>
        /// <param name="stream"></param>
        public void SaveToStream(string filename, Stream stream)
        {
            try
            {
                foreach(KeyValuePair<string, ITerrainLoader> loader in _loaders)
                {
                    if (filename.EndsWith(loader.Key))
                    {
                        loader.Value.SaveStream(stream, _channel);
                        return;
                    }
                }
            }
            catch(NotImplementedException)
            {
                _log.Error("Unable to save to " + filename + ", saving of this file format has not been implemented.");
                throw new TerrainException(string.Format("Unable to save heightmap: saving of this file format not implemented"));
            }
        }

        // Someone diddled terrain outside the normal code paths. Set the taintedness for all clients.
        // ITerrainModule.TaintTerrain()
        public void TaintTerrain ()
        {
            lock (_perClientPatchUpdates)
            {
                // Set the flags for all clients so the tainted patches will be sent out
                foreach (PatchUpdates pups in _perClientPatchUpdates.Values)
                {
                    pups.SetAll(_scene.Heightmap.GetTerrainData());
                }
            }
        }

        // ITerrainModule.PushTerrain()
        public void PushTerrain(IClientAPI pClient)
        {
            if (_sendTerrainUpdatesByViewDistance)
            {
                ScenePresence presence = _scene.GetScenePresence(pClient.AgentId);
                if (presence != null)
                {
                    lock (_perClientPatchUpdates)
                    {
                        PatchUpdates pups;
                        if (!_perClientPatchUpdates.TryGetValue(pClient.AgentId, out pups))
                        {
                            // There is a ScenePresence without a send patch map. Create one.
                            pups = new PatchUpdates(_scene.Heightmap.GetTerrainData(), presence);
                            _perClientPatchUpdates.Add(presence.UUID, pups);
                        }
                        else
                          pups.SetAll(true);
                    }
                }
            }
            else
            {
                // The traditional way is to call into the protocol stack to send them all.
                pClient.SendLayerData();
            }
        }

        #region Plugin Loading Methods

        private void LoadPlugins()
        {
            _plugineffects = new Dictionary<string, ITerrainEffect>();
            LoadPlugins(Assembly.GetCallingAssembly());
            string plugineffectsPath = "Terrain";

            // Load the files in the Terrain/ dir
            if (!Directory.Exists(plugineffectsPath))
                return;

            string[] files = Directory.GetFiles(plugineffectsPath);
            foreach(string file in files)
            {
                _log.Info("Loading effects in " + file);
                try
                {
                    Assembly library = Assembly.LoadFrom(file);
                    LoadPlugins(library);
                }
                catch(BadImageFormatException)
                {
                }
            }
        }

        private void LoadPlugins(Assembly library)
        {
            foreach(Type pluginType in library.GetTypes())
            {
                try
                {
                    if (pluginType.IsAbstract || pluginType.IsNotPublic)
                        continue;

                    string typeName = pluginType.Name;

                    if (pluginType.GetInterface("ITerrainEffect", false) != null)
                    {
                        ITerrainEffect terEffect = (ITerrainEffect)Activator.CreateInstance(library.GetType(pluginType.ToString()));

                        InstallPlugin(typeName, terEffect);
                    }
                    else if (pluginType.GetInterface("ITerrainLoader", false) != null)
                    {
                        ITerrainLoader terLoader = (ITerrainLoader)Activator.CreateInstance(library.GetType(pluginType.ToString()));
                        _loaders[terLoader.FileExtension] = terLoader;
                        _log.Info("L ... " + typeName);
                    }
                }
                catch(AmbiguousMatchException)
                {
                }
            }
        }

        public void InstallPlugin(string pluginName, ITerrainEffect effect)
        {
            lock(_plugineffects)
            {
                if (!_plugineffects.ContainsKey(pluginName))
                {
                    _plugineffects.Add(pluginName, effect);
                    _log.Info("E ... " + pluginName);
                }
                else
                {
                    _plugineffects[pluginName] = effect;
                    _log.Info("E ... " + pluginName + " (Replaced)");
                }
            }
        }

        #endregion

        #endregion

        /// <summary>
        /// Installs into terrain module the standard suite of brushes
        /// </summary>
        private void InstallDefaultEffects()
        {
            // Draggable Paint Brush Effects
            _painteffects[StandardTerrainEffects.Raise] = new RaiseSphere();
            _painteffects[StandardTerrainEffects.Lower] = new LowerSphere();
            _painteffects[StandardTerrainEffects.Smooth] = new SmoothSphere();
            _painteffects[StandardTerrainEffects.Noise] = new NoiseSphere();
            _painteffects[StandardTerrainEffects.Flatten] = new FlattenSphere();
            _painteffects[StandardTerrainEffects.Revert] = new RevertSphere(_baked);

            // Area of effect selection effects
            _floodeffects[StandardTerrainEffects.Raise] = new RaiseArea();
            _floodeffects[StandardTerrainEffects.Lower] = new LowerArea();
            _floodeffects[StandardTerrainEffects.Smooth] = new SmoothArea();
            _floodeffects[StandardTerrainEffects.Noise] = new NoiseArea();
            _floodeffects[StandardTerrainEffects.Flatten] = new FlattenArea();
            _floodeffects[StandardTerrainEffects.Revert] = new RevertArea(_baked);

            // Terrain Modifier operations

            _modifyOperations["min"] = new MinModifier(this);
            _modifyOperations["max"] = new MaxModifier(this);
            _modifyOperations["raise"] = new RaiseModifier(this);
            _modifyOperations["lower"] = new LowerModifier(this);
            _modifyOperations["fill"] = new FillModifier(this);
            _modifyOperations["smooth"] = new SmoothModifier(this);
            _modifyOperations["noise"] = new NoiseModifier(this);

            // Filesystem load/save loaders
            _loaders[".r32"] = new RAW32();
            _loaders[".f32"] = _loaders[".r32"];
            _loaders[".ter"] = new Terragen();
            _loaders[".raw"] = new LLRAW();
            _loaders[".jpg"] = new JPEG();
            _loaders[".jpeg"] = _loaders[".jpg"];
            _loaders[".bmp"] = new BMP();
            _loaders[".png"] = new PNG();
            _loaders[".gif"] = new GIF();
            _loaders[".tif"] = new TIFF();
            _loaders[".tiff"] = _loaders[".tif"];
        }

        /// <summary>
        /// Saves the current state of the region into the baked map buffer.

        /// </summary>
        public void UpdateBakedMap()
        {
            _baked = _channel.MakeCopy();
            _painteffects[StandardTerrainEffects.Revert] = new RevertSphere(_baked);
            _floodeffects[StandardTerrainEffects.Revert] = new RevertArea(_baked);
            _scene.Bakedmap = _baked;
            _scene.SaveBakedTerrain();
        }

        /// <summary>
        /// Loads a tile from a larger terrain file and installs it into the region.
        /// </summary>
        /// <param name="filename">The terrain file to load</param>
        /// <param name="fileWidth">The width of the file in units</param>
        /// <param name="fileHeight">The height of the file in units</param>
        /// <param name="fileStartX">Where to begin our slice</param>
        /// <param name="fileStartY">Where to begin our slice</param>
        public void LoadFromFile(string filename, int fileWidth, int fileHeight, int fileStartX, int fileStartY)
        {
            int offsetX = (int)_scene.RegionInfo.RegionLocX - fileStartX;
            int offsetY = (int)_scene.RegionInfo.RegionLocY - fileStartY;

            if (offsetX >= 0 && offsetX < fileWidth && offsetY >= 0 && offsetY < fileHeight)
            {
                // this region is included in the tile request
                foreach(KeyValuePair<string, ITerrainLoader> loader in _loaders)
                {
                    if (filename.EndsWith(loader.Key))
                    {
                        lock(_scene)
                        {
                            ITerrainChannel channel = loader.Value.LoadFile(filename, offsetX, offsetY,
                                                                            fileWidth, fileHeight,
                                                                            (int) _scene.RegionInfo.RegionSizeX,
                                                                            (int) _scene.RegionInfo.RegionSizeY);
                            _scene.Heightmap = channel;
                            _channel = channel;
                            UpdateBakedMap();
                        }

                        return;
                    }
                }
            }
        }

        /// <summary>
        /// Save a number of map tiles to a single big image file.
        /// </summary>
        /// <remarks>
        /// If the image file already exists then the tiles saved will replace those already in the file - other tiles
        /// will be untouched.
        /// </remarks>
        /// <param name="filename">The terrain file to save</param>
        /// <param name="fileWidth">The number of tiles to save along the X axis.</param>
        /// <param name="fileHeight">The number of tiles to save along the Y axis.</param>
        /// <param name="fileStartX">The map x co-ordinate at which to begin the save.</param>
        /// <param name="fileStartY">The may y co-ordinate at which to begin the save.</param>
        public void SaveToFile(string filename, int fileWidth, int fileHeight, int fileStartX, int fileStartY)
        {
            int offsetX = (int)_scene.RegionInfo.RegionLocX - fileStartX;
            int offsetY = (int)_scene.RegionInfo.RegionLocY - fileStartY;

            if (offsetX < 0 || offsetX >= fileWidth || offsetY < 0 || offsetY >= fileHeight)
            {
                MainConsole.Instance.Output(
                    "ERROR: file width + minimum X tile and file height + minimum Y tile must incorporate the current region at ({0},{1}).  File width {2} from {3} and file height {4} from {5} does not.",
                    _scene.RegionInfo.RegionLocX, _scene.RegionInfo.RegionLocY, fileWidth, fileStartX, fileHeight, fileStartY);

                return;
            }

            // this region is included in the tile request
            foreach(KeyValuePair<string, ITerrainLoader> loader in _loaders)
            {
                if (filename.EndsWith(loader.Key) && loader.Value.SupportsTileSave())
                {
                    lock(_scene)
                    {
                        loader.Value.SaveFile(_channel, filename, offsetX, offsetY,
                                              fileWidth, fileHeight,
                                              (int)_scene.RegionInfo.RegionSizeX,
                                              (int)_scene.RegionInfo.RegionSizeY);

                        MainConsole.Instance.Output(
                            "Saved terrain from ({0},{1}) to ({2},{3}) from {4} to {5}",
                            fileStartX, fileStartY, fileStartX + fileWidth - 1, fileStartY + fileHeight - 1,
                            _scene.RegionInfo.RegionName, filename);
                    }

                    return;
                }
            }

            MainConsole.Instance.Output(
                "ERROR: Could not save terrain from {0} to {1}.  Valid file extensions are {2}",
                _scene.RegionInfo.RegionName, filename, _supportFileExtensionsForTileSave);
        }


        /// <summary>
        /// This is used to check to see of any of the terrain is tainted and, if so, schedule
        /// updates for all the presences.
        /// This also checks to see if there are updates that need to be sent for each presence.
        /// This is where the logic is to send terrain updates to clients.
        /// </summary>
        /// doing it async, since currently this is 2 heavy for heartbeat
        private void EventManager_TerrainCheckUpdates()
        {
            Util.FireAndForget(
                EventManager_TerrainCheckUpdatesAsync);
        }

        readonly object TerrainCheckUpdatesLock = new object();

        private void EventManager_TerrainCheckUpdatesAsync(object o)
        {
            // dont overlap execution
            if(Monitor.TryEnter(TerrainCheckUpdatesLock))
            {
                TerrainData terrData = _channel.GetTerrainData();
                bool shouldTaint = false;

                int sx = terrData.SizeX / Constants.TerrainPatchSize;
                for (int y = 0, py = 0; y < terrData.SizeY / Constants.TerrainPatchSize; y++, py += sx)
                {
                    for (int x = 0; x < sx; x++)
                    {
                        if (terrData.IsTaintedAtPatchWithClear(x + py))
                        {
                            // Found a patch that was modified. Push this flag into the clients.
                            SendToClients(terrData, x, y);
                            shouldTaint = true;
                        }
                    }
                }

                // This event also causes changes to be sent to the clients
                CheckSendingPatchesToClients();

                // If things changes, generate some events
                if (shouldTaint)
                {
                    _scene.EventManager.TriggerTerrainTainted();
                    _tainted = true;
                }
                Monitor.Exit(TerrainCheckUpdatesLock);
            }
        }

        /// <summary>
        /// Performs updates to the region periodically, synchronising physics and other heightmap aware sections
        /// Called infrequently (like every 5 seconds or so). Best used for storing terrain.
        /// </summary>
        private void EventManager_OnTerrainTick()
        {
            if (_tainted)
            {
                _tainted = false;
                _scene.PhysicsScene.SetTerrain(_channel.GetFloatsSerialised());
                _scene.SaveTerrain();

                // Clients who look at the map will never see changes after they looked at the map, so i've commented this out.
                //_scene.CreateTerrainTexture(true);
            }
        }

        /// <summary>
        /// Processes commandline input. Do not call directly.
        /// </summary>
        /// <param name="args">Commandline arguments</param>
        private void EventManager_OnPluginConsole(string[] args)
        {
            if (args[0] == "terrain")
            {
                if (args.Length == 1)
                {
                    _commander.ProcessConsoleCommand("help", new string[0]);
                    return;
                }

                string[] tmpArgs = new string[args.Length - 2];
                int i;
                for(i = 2; i < args.Length; i++)
                    tmpArgs[i - 2] = args[i];

                _commander.ProcessConsoleCommand(args[1], tmpArgs);
            }
        }

        /// <summary>
        /// Installs terrain brush hook to IClientAPI
        /// </summary>
        /// <param name="client"></param>
        private void EventManager_OnNewClient(IClientAPI client)
        {
            client.OnModifyTerrain += client_OnModifyTerrain;
            client.OnBakeTerrain += client_OnBakeTerrain;
            client.OnLandUndo += client_OnLandUndo;
            client.OnUnackedTerrain += client_OnUnackedTerrain;
        }

        /// <summary>
        /// Installs terrain brush hook to IClientAPI
        /// </summary>
        /// <param name="client"></param>
        private void EventManager_OnClientClosed(UUID client, Scene scene)
        {
            ScenePresence presence = scene.GetScenePresence(client);
            if (presence != null)
            {
                presence.ControllingClient.OnModifyTerrain -= client_OnModifyTerrain;
                presence.ControllingClient.OnBakeTerrain -= client_OnBakeTerrain;
                presence.ControllingClient.OnLandUndo -= client_OnLandUndo;
                presence.ControllingClient.OnUnackedTerrain -= client_OnUnackedTerrain;
            }
            lock (_perClientPatchUpdates)
                _perClientPatchUpdates.Remove(client);
        }

        /// <summary>
        /// Scan over changes in the terrain and limit height changes. This enforces the
        ///     non-estate owner limits on rate of terrain editting.
        /// Returns 'true' if any heights were limited.
        /// </summary>
        private bool EnforceEstateLimits()
        {
            TerrainData terrData = _channel.GetTerrainData();

            bool wasLimited = false;
            for (int x = 0; x < terrData.SizeX; x += Constants.TerrainPatchSize)
            {
                for (int y = 0; y < terrData.SizeY; y += Constants.TerrainPatchSize)
                {
                    if (terrData.IsTaintedAt(x, y))
                    {
                        // If we should respect the estate settings then
                        //     fixup and height deltas that don't respect them.
                        // Note that LimitChannelChanges() modifies the TerrainChannel with the limited height values.
                        wasLimited |= LimitChannelChanges(terrData, x, y);
                    }
                }
            }
            return wasLimited;
        }

        private bool EnforceEstateLimits(int startX, int startY, int endX, int endY)
        {
            TerrainData terrData = _channel.GetTerrainData();

            bool wasLimited = false;
            for (int x = startX; x <= endX; x += Constants.TerrainPatchSize)
            {
                for (int y = startX; y <= endY; y += Constants.TerrainPatchSize)
                {
                    if (terrData.IsTaintedAt(x, y))
                    {
                        // If we should respect the estate settings then
                        //     fixup and height deltas that don't respect them.
                        // Note that LimitChannelChanges() modifies the TerrainChannel with the limited height values.
                        wasLimited |= LimitChannelChanges(terrData, x, y);
                    }
                }
            }
            return wasLimited;
        }

        /// <summary>
        /// Checks to see height deltas in the tainted terrain patch at xStart ,yStart
        /// are all within the current estate limits
        /// <returns>true if changes were limited, false otherwise</returns>
        /// </summary>
        private bool LimitChannelChanges(TerrainData terrData, int xStart, int yStart)
        {
            bool changesLimited = false;
            float minDelta = (float)_scene.RegionInfo.RegionSettings.TerrainLowerLimit;
            float maxDelta = (float)_scene.RegionInfo.RegionSettings.TerrainRaiseLimit;

            // loop through the height map for this patch and compare it against
            // the baked map
            for (int x = xStart; x < xStart + Constants.TerrainPatchSize; x++)
            {
                for(int y = yStart; y < yStart + Constants.TerrainPatchSize; y++)
                {
                    float requestedHeight = terrData[x, y];
                    float bakedHeight = (float)_baked[x, y];
                    float requestedDelta = requestedHeight - bakedHeight;

                    if (requestedDelta > maxDelta)
                    {
                        terrData[x, y] = bakedHeight + maxDelta;
                        changesLimited = true;
                    }
                    else if (requestedDelta < minDelta)
                    {
                        terrData[x, y] = bakedHeight + minDelta; //as lower is a -ve delta
                        changesLimited = true;
                    }
                }
            }

            return changesLimited;
        }

        private void client_OnLandUndo(IClientAPI client)
        {
        }

        /// <summary>
        /// Sends a copy of the current terrain to the scenes clients
        /// </summary>
        /// <param name="serialised">A copy of the terrain as a 1D float array of size w*h</param>
        /// <param name="px">x patch coords</param>
        /// <param name="py">y patch coords</param>
        private void SendToClients(TerrainData terrData, int px, int py)
        {
            if (_sendTerrainUpdatesByViewDistance)
            {
                // Add that this patch needs to be sent to the accounting for each client.
                lock (_perClientPatchUpdates)
                {
                    _scene.ForEachScenePresence(presence =>
                        {
                            if (!_perClientPatchUpdates.TryGetValue(presence.UUID, out PatchUpdates thisClientUpdates))
                            {
                                // There is a ScenePresence without a send patch map. Create one. should not happen
                                thisClientUpdates = new PatchUpdates(terrData, presence, false);
                                _perClientPatchUpdates.Add(presence.UUID, thisClientUpdates);
                            }
                            thisClientUpdates.SetTrueByPatch(px, py);
                        }
                    );
                }
            }
            else
            {
                // Legacy update sending where the update is sent out as soon as noticed
                // We know the actual terrain data that is passed is ignored so this passes a dummy heightmap.
                //float[] heightMap = terrData.GetFloatsSerialized();
                int[] map = new int[]{px, py};
                _scene.ForEachClient(
                    delegate (IClientAPI controller)
                    {
                        controller.SendLayerData(map);
                    }
                );
            }
        }

        private class PatchesToSend : IComparable<PatchesToSend>
        {
            public readonly int PatchX;
            public readonly int PatchY;
            public readonly float Dist;
            public PatchesToSend(int pX, int pY, float pDist)
            {
                PatchX = pX;
                PatchY = pY;
                Dist = pDist;
            }
            public int CompareTo(PatchesToSend other)
            {
                return Dist.CompareTo(other.Dist);
            }
        }

        // Called each frame time to see if there are any patches to send to any of the
        //    ScenePresences.
        // Loop through all the per-client info and send any patches necessary.
        private void CheckSendingPatchesToClients()
        {
            lock (_perClientPatchUpdates)
            {
                foreach (PatchUpdates pups in _perClientPatchUpdates.Values)
                {
                    if(pups.Presence.IsDeleted || !pups.HasUpdates() || !pups.Presence.ControllingClient.CanSendLayerData())
                        continue;

                    if (_sendTerrainUpdatesByViewDistance)
                    {
                        // There is something that could be sent to this client.
                        List<PatchesToSend> toSend = GetModifiedPatchesInViewDistance(pups);
                        if (toSend.Count > 0)
                        {
                            // _log.DebugFormat("{0} CheckSendingPatchesToClient: sending {1} patches to {2} in region {3}",
                            //                     LogHeader, toSend.Count, pups.Presence.Name, _scene.RegionInfo.RegionName);
                            // Sort the patches to send by the distance from the presence
                            toSend.Sort();
                            int[] patchPieces = new int[toSend.Count * 2];
                            int pieceIndex = 0;
                            foreach (PatchesToSend pts in toSend)
                            {
                                patchPieces[pieceIndex++] = pts.PatchX;
                                patchPieces[pieceIndex++] = pts.PatchY;
                            }
                            pups.Presence.ControllingClient.SendLayerData(patchPieces);
                        }
                        if (pups.sendAll && toSend.Count < 1024)
                            SendAllModifiedPatchs(pups);
                    }
                    else
                        SendAllModifiedPatchs(pups);
                }
            }
        }
        private void SendAllModifiedPatchs(PatchUpdates pups)
        {
            if (!pups.sendAll) // sanity
                return;

            int limitX = (int)_scene.RegionInfo.RegionSizeX / Constants.TerrainPatchSize;
            int limitY = (int)_scene.RegionInfo.RegionSizeY / Constants.TerrainPatchSize;

            if (pups.sendAllcurrentX >= limitX && pups.sendAllcurrentY >= limitY)
            {
                pups.sendAll = false;
                pups.sendAllcurrentX = 0;
                pups.sendAllcurrentY = 0;
                return;
            }

            int npatchs = 0;
            List<PatchesToSend> patchs = new List<PatchesToSend>();
            int x = pups.sendAllcurrentX;
            int y = pups.sendAllcurrentY;
            // send it in the order viewer draws it
            // even if not best for memory scan
            for (; y < limitY; y++)
            {
                for (; x < limitX; x++)
                {
                    if (pups.GetByPatchAndClear(x, y))
                    {
                        patchs.Add(new PatchesToSend(x, y, 0));
                        if (++npatchs >= 128)
                        {
                            x++;
                            break;
                        }
                    }
                }
                if (npatchs >= 128)
                    break;
                x = 0;
            }

            if (x >= limitX && y >= limitY)
            {
                pups.sendAll = false;
                pups.sendAllcurrentX = 0;
                pups.sendAllcurrentY = 0;
            }
            else
            {
                pups.sendAllcurrentX = x;
                pups.sendAllcurrentY = y;
            }

            npatchs = patchs.Count;
            if (npatchs > 0)
            {
                int[] patchPieces = new int[npatchs * 2];
                int pieceIndex = 0;
                foreach (PatchesToSend pts in patchs)
                {
                    patchPieces[pieceIndex++] = pts.PatchX;
                    patchPieces[pieceIndex++] = pts.PatchY;
                }
                pups.Presence.ControllingClient.SendLayerData(patchPieces);
            }
        }

        private List<PatchesToSend> GetModifiedPatchesInViewDistance(PatchUpdates pups)
        {
            List<PatchesToSend> ret = new List<PatchesToSend>();

            int npatchs = 0;

            ScenePresence presence = pups.Presence;
            if (presence == null)
                return ret;

            float minz = presence.AbsolutePosition.Z;
            if (presence.CameraPosition.Z < minz)
                minz = presence.CameraPosition.Z;

            // this limit should be max terrainheight + max draw
            if (minz > 1500f)
                return ret;

            int DrawDistance = (int)presence.DrawDistance;

            DrawDistance = DrawDistance / Constants.TerrainPatchSize;

            int testposX;
            int testposY;

            if (Math.Abs(presence.AbsolutePosition.X - presence.CameraPosition.X) > 30
                || Math.Abs(presence.AbsolutePosition.Y - presence.CameraPosition.Y) > 30)
            {
                testposX = (int)presence.CameraPosition.X / Constants.TerrainPatchSize;
                testposY = (int)presence.CameraPosition.Y / Constants.TerrainPatchSize;
            }
            else
            {
                testposX = (int)presence.AbsolutePosition.X / Constants.TerrainPatchSize;
                testposY = (int)presence.AbsolutePosition.Y / Constants.TerrainPatchSize;
            }
            int limitX = (int)_scene.RegionInfo.RegionSizeX / Constants.TerrainPatchSize;
            int limitY = (int)_scene.RegionInfo.RegionSizeY / Constants.TerrainPatchSize;

            // Compute the area of patches within our draw distance
            int startX = testposX - DrawDistance;
            if (startX < 0)
                startX = 0;
            else if (startX >= limitX)
                startX = limitX - 1;

            int startY = testposY - DrawDistance;
            if (startY < 0)
                startY = 0;
            else if (startY >= limitY)
                startY = limitY - 1;

            int endX = testposX + DrawDistance;
            if (endX < 0)
                endX = 0;
            else if (endX > limitX)
                endX = limitX;

            int endY = testposY + DrawDistance;
            if (endY < 0)
                endY = 0;
            else if (endY > limitY)
                endY = limitY;

            float distxsq;
            float distysq = 0;
            float distlimitsq;

            DrawDistance *= DrawDistance;

            for (int y = startY, py = startY * limitX; y < endY; y++, py += limitX)
            {
                distysq = y - testposY;
                distysq *= distysq;
                distlimitsq = DrawDistance - distysq;
                for (int x = startX; x < endX; x++)
                {
                    int indx = x + py;
                    if (pups.GetByPatch(indx))
                    {
                        distxsq = x - testposX;
                        distxsq *= distxsq;
                        if (distxsq < distlimitsq)
                        {
                            pups.SetFalseByPatch(x + py);
                            ret.Add(new PatchesToSend(x, y, distxsq + distysq));
                            if (npatchs++ > 1024)
                            {
                                y = endY;
                                x = endX;
                            }
                        }
                    }
                }
            }
            return ret;
        }

        private double NextModifyTerrainTime = double.MinValue;

        private void client_OnModifyTerrain(UUID user, float height, float seconds, float brushSize, byte action,
                                            float north, float west, float south, float east, int parcelLocalID)
        {
            double now = Util.GetTimeStamp();
            if(now < NextModifyTerrainTime)
                return;

            try
            {
                NextModifyTerrainTime = double.MaxValue; // block it

                //_log.DebugFormat("brushs {0} seconds {1} height {2}, parcel {3}", brushSize, seconds, height, parcelLocalID);
                bool god = _scene.Permissions.IsGod(user);
                bool allowed = false;
                if (north == south && east == west)
                {
                    if (_painteffects.ContainsKey((StandardTerrainEffects)action))
                    {
                        bool[,] allowMask = new bool[_channel.Width, _channel.Height];
                    
                        allowMask.Initialize();

                        int startX = (int)(west - brushSize + 0.5);
                        if (startX < 0)
                            startX = 0;

                        int startY = (int)(north - brushSize + 0.5);
                        if (startY < 0)
                            startY = 0;

                        int endX = (int)(west + brushSize + 0.5);
                        if (endX >= _channel.Width)
                            endX = _channel.Width - 1;
                        int endY = (int)(north + brushSize + 0.5);
                        if (endY >= _channel.Height)
                            endY = _channel.Height - 1;

                        int x, y;

                        for (x = startX; x <= endX; x++)
                        {
                            for (y = startY; y <= endY; y++)
                            {
                                if (_scene.Permissions.CanTerraformLand(user, new Vector3(x, y, -1)))
                                {
                                    allowMask[x, y] = true;
                                    allowed = true;
                                }
                            }
                        }
                        if (allowed)
                        {
                            StoreUndoState();
                            _painteffects[(StandardTerrainEffects) action].PaintEffect(
                                _channel, allowMask, west, south, height, brushSize, seconds,
                                startX, endX, startY, endY);

                            //block changes outside estate limits
                            if (!god)
                                EnforceEstateLimits(startX, endX, startY, endY);
                        }
                    }
                    else
                    {
                        _log.Debug("Unknown terrain brush type " + action);
                    }
                }
                else
                {
                    if (_floodeffects.ContainsKey((StandardTerrainEffects)action))
                    {
                        bool[,] fillArea = new bool[_channel.Width, _channel.Height];
                        fillArea.Initialize();

                        int startX = (int)west;
                        int startY = (int)south;
                        int endX = (int)east;
                        int endY = (int)north;

                        if (startX < 0)
                            startX = 0;
                        else if (startX >= _channel.Width)
                            startX = _channel.Width - 1;

                        if (endX < 0)
                            endX = 0;
                        else if (endX >= _channel.Width)
                            endX = _channel.Width - 1;

                        if (startY < 0)
                            startY = 0;
                        else if (startY >= _channel.Height)
                            startY = _channel.Height - 1;

                        if (endY < 0)
                            endY = 0;
                        else if (endY >= _channel.Height)
                            endY = _channel.Height - 1;

                        int x, y;
                        if (parcelLocalID == -1)
                        {
                            for (x = startX; x <= endX; x++)
                            {
                                for (y = startY; y <= endY; y++)
                                {
                                    if (_scene.Permissions.CanTerraformLand(user, new Vector3(x, y, -1)))
                                    {
                                        fillArea[x, y] = true;
                                        allowed = true;
                                    }
                                }
                            }
                        }
                        else
                        {
                            if (!_scene.Permissions.CanTerraformLand(user, new Vector3(-1, -1, parcelLocalID)))
                                return;

                            ILandObject parcel = _scene.LandChannel.GetLandObject(parcelLocalID);
                            if(parcel == null)
                                return;

                            bool[,] parcelmap = parcel.GetLandBitmap();
                            //ugly
                            for (x = startX; x <= endX; x++)
                            {
                                int px = x >> 2;
                                y = startY;
                                while( y <= endY)
                                {
                                    int py = y >> 2;
                                    bool inp = parcelmap[px, py];
                                    fillArea[x, y++] = inp;
                                    fillArea[x, y++] = inp;
                                    fillArea[x, y++] = inp;
                                    fillArea[x, y++] = inp;
                                }
                            }

                            allowed = true;
                        }

                        if (allowed)
                        {
                            StoreUndoState();
                            _floodeffects[(StandardTerrainEffects)action].FloodEffect(_channel, fillArea, height, seconds,
                                startX, endX, startY, endY);

                            //block changes outside estate limits
                            if (!god)
                                EnforceEstateLimits(startX, endX, startY, endY);
                        }
                    }
                    else
                    {
                        _log.Debug("Unknown terrain flood type " + action);
                    }
                }
            }
            finally
            {
                NextModifyTerrainTime = Util.GetTimeStamp() + 0.02; // 20ms cooldown
            }
        }

        private void client_OnBakeTerrain(IClientAPI remoteClient)
        {
            // Not a good permissions check (see client_OnModifyTerrain above), need to check the entire area.
            // for now check a point in the centre of the region

            if (_scene.Permissions.CanIssueEstateCommand(remoteClient.AgentId, true))
            {
                InterfaceBakeTerrain(null); //bake terrain does not use the passed in parameter
            }
        }

        protected void client_OnUnackedTerrain(IClientAPI client, int patchX, int patchY)
        {
            //_log.Debug("Terrain packet unacked, resending patch: " + patchX + " , " + patchY);
            // SendLayerData does not use the heightmap parameter. This kludge is so as to not change IClientAPI.
            client.SendLayerData(new int[]{patchX, patchY});
        }

        private void StoreUndoState()
        {
        }

        #region Console Commands

        private void InterfaceLoadFile(object[] args)
        {
            LoadFromFile((string) args[0]);
        }

        private void InterfaceLoadTileFile(object[] args)
        {
            LoadFromFile((string) args[0],
                         (int) args[1],
                         (int) args[2],
                         (int) args[3],
                         (int) args[4]);
        }

        private void InterfaceSaveFile(object[] args)
        {
            SaveToFile((string)args[0]);
        }

        private void InterfaceSaveTileFile(object[] args)
        {
            SaveToFile((string)args[0],
                         (int)args[1],
                         (int)args[2],
                         (int)args[3],
                         (int)args[4]);
        }

        private void InterfaceBakeTerrain(object[] args)
        {
            UpdateBakedMap();
        }

        private void InterfaceRevertTerrain(object[] args)
        {
            int x, y;
            for (x = 0; x < _channel.Width; x++)
                for (y = 0; y < _channel.Height; y++)
                    _channel[x, y] = _baked[x, y];

        }

        private void InterfaceFlipTerrain(object[] args)
        {
            string direction = (string)args[0];

            if (direction.ToLower().StartsWith("y"))
            {
                for (int x = 0; x < _channel.Width; x++)
                {
                    for (int y = 0; y < _channel.Height / 2; y++)
                    {
                        float height = _channel[x, y];
                        float flippedHeight = _channel[x, _channel.Height - 1 - y];
                        _channel[x, y] = flippedHeight;
                        _channel[x, _channel.Height - 1 - y] = height;

                    }
                }
            }
            else if (direction.ToLower().StartsWith("x"))
            {
                for (int y = 0; y < _channel.Height; y++)
                {
                    for (int x = 0; x < _channel.Width / 2; x++)
                    {
                        float height = _channel[x, y];
                        float flippedHeight = _channel[_channel.Width - 1 - x, y];
                        _channel[x, y] = flippedHeight;
                        _channel[_channel.Width - 1 - x, y] = height;

                    }
                }
            }
            else
            {
                MainConsole.Instance.Output("ERROR: Unrecognised direction {0} - need x or y", direction);
            }
        }

        private void InterfaceRescaleTerrain(object[] args)
        {
            float desiredMin = (float)args[0];
            float desiredMax = (float)args[1];

            // determine desired scaling factor
            float desiredRange = desiredMax - desiredMin;
            //_log.InfoFormat("Desired {0}, {1} = {2}", new Object[] { desiredMin, desiredMax, desiredRange });

            if (desiredRange == 0d)
            {
                // delta is zero so flatten at requested height
                InterfaceFillTerrain(new object[] { args[1] });
            }
            else
            {
                //work out current heightmap range
                float currMin = float.MaxValue;
                float currMax = float.MinValue;

                int width = _channel.Width;
                int height = _channel.Height;

                for(int x = 0; x < width; x++)
                {
                    for(int y = 0; y < height; y++)
                    {
                        float currHeight = _channel[x, y];
                        if (currHeight < currMin)
                        {
                            currMin = currHeight;
                        }
                        else if (currHeight > currMax)
                        {
                            currMax = currHeight;
                        }
                    }
                }

                float currRange = currMax - currMin;
                float scale = desiredRange / currRange;

                //_log.InfoFormat("Current {0}, {1} = {2}", new Object[] { currMin, currMax, currRange });
                //_log.InfoFormat("Scale = {0}", scale);

                // scale the heightmap accordingly
                for(int x = 0; x < width; x++)
                {
                    for(int y = 0; y < height; y++)
                    {
                        float currHeight = _channel[x, y] - currMin;
                        _channel[x, y] = desiredMin + currHeight * scale;
                    }
                }

            }
        }

        private void InterfaceElevateTerrain(object[] args)
        {
            float val = (float)args[0];

            int x, y;
            for (x = 0; x < _channel.Width; x++)
                for (y = 0; y < _channel.Height; y++)
                    _channel[x, y] += val;
        }

        private void InterfaceMultiplyTerrain(object[] args)
        {
            int x, y;
            float val = (float)args[0];

            for (x = 0; x < _channel.Width; x++)
                for (y = 0; y < _channel.Height; y++)
                    _channel[x, y] *= val;
        }

        private void InterfaceLowerTerrain(object[] args)
        {
            int x, y;
            float val = (float)args[0];

            for (x = 0; x < _channel.Width; x++)
                for (y = 0; y < _channel.Height; y++)
                    _channel[x, y] -= val;
        }

        public void InterfaceFillTerrain(object[] args)
        {
            int x, y;
            float val = (float)args[0];

            for (x = 0; x < _channel.Width; x++)
                for (y = 0; y < _channel.Height; y++)
                    _channel[x, y] = val;
        }

        private void InterfaceMinTerrain(object[] args)
        {
            int x, y;
            float val = (float)args[0];
            for (x = 0; x < _channel.Width; x++)
            {
                for(y = 0; y < _channel.Height; y++)
                {
                    _channel[x, y] = Math.Max(val, _channel[x, y]);
                }
            }
        }

        private void InterfaceMaxTerrain(object[] args)
        {
            int x, y;
            float val = (float)args[0];
            for (x = 0; x < _channel.Width; x++)
            {
                for(y = 0; y < _channel.Height; y++)
                {
                    _channel[x, y] = Math.Min(val, _channel[x, y]);
                }
            }
        }

        private void InterfaceShow(object[] args)
        {
            Vector2 point;

            if (!ConsoleUtil.TryParseConsole2DVector((string)args[0], null, out point))
            {
                Console.WriteLine("ERROR: {0} is not a valid vector", args[0]);
                return;
            }

            double height = _channel[(int)point.X, (int)point.Y];

            Console.WriteLine("Terrain height at {0} is {1}", point, height);
        }

        private void InterfaceShowDebugStats(object[] args)
        {
            float max = float.MinValue;
            float min = float.MaxValue;
            double sum = 0;

            int x;
            for(x = 0; x < _channel.Width; x++)
            {
                int y;
                for(y = 0; y < _channel.Height; y++)
                {
                    sum += _channel[x, y];
                    if (max < _channel[x, y])
                        max = _channel[x, y];
                    if (min > _channel[x, y])
                        min = _channel[x, y];
                }
            }

            double avg = sum / (_channel.Height * _channel.Width);

            MainConsole.Instance.Output("Channel {0}x{1}", _channel.Width, _channel.Height);
            MainConsole.Instance.Output("max/min/avg/sum: {0}/{1}/{2}/{3}", max, min, avg, sum);
        }

        private void InterfaceRunPluginEffect(object[] args)
        {
            string firstArg = (string)args[0];

            if (firstArg == "list")
            {
                MainConsole.Instance.Output("List of loaded plugins");
                foreach(KeyValuePair<string, ITerrainEffect> kvp in _plugineffects)
                {
                    MainConsole.Instance.Output(kvp.Key);
                }
                return;
            }

            if (firstArg == "reload")
            {
                LoadPlugins();
                return;
            }

            if (_plugineffects.ContainsKey(firstArg))
            {
                _plugineffects[firstArg].RunEffect(_channel);
            }
            else
            {
                MainConsole.Instance.Output("WARNING: No such plugin effect {0} loaded.", firstArg);
            }
        }

        private void InstallInterfaces()
        {
            Command loadFromFileCommand =
                new Command("load", CommandIntentions.COMMAND_HAZARDOUS, InterfaceLoadFile, "Loads a terrain from a specified file.");
            loadFromFileCommand.AddArgument("filename",
                                            "The file you wish to load from, the file extension determines the loader to be used. Supported extensions include: " +
                                            _supportedFileExtensions, "String");

            Command saveToFileCommand =
                new Command("save", CommandIntentions.COMMAND_NON_HAZARDOUS, InterfaceSaveFile, "Saves the current heightmap to a specified file.");
            saveToFileCommand.AddArgument("filename",
                                          "The destination filename for your heightmap, the file extension determines the format to save in. Supported extensions include: " +
                                          _supportedFileExtensions, "String");

            Command loadFromTileCommand =
                new Command("load-tile", CommandIntentions.COMMAND_HAZARDOUS, InterfaceLoadTileFile, "Loads a terrain from a section of a larger file.");
            loadFromTileCommand.AddArgument("filename",
                                            "The file you wish to load from, the file extension determines the loader to be used. Supported extensions include: " +
                                            _supportedFileExtensions, "String");
            loadFromTileCommand.AddArgument("file width", "The width of the file in tiles", "Integer");
            loadFromTileCommand.AddArgument("file height", "The height of the file in tiles", "Integer");
            loadFromTileCommand.AddArgument("minimum X tile", "The X region coordinate of the first section on the file",
                                            "Integer");
            loadFromTileCommand.AddArgument("minimum Y tile", "The Y region coordinate of the first section on the file",
                                            "Integer");

            Command saveToTileCommand =
                new Command("save-tile", CommandIntentions.COMMAND_HAZARDOUS, InterfaceSaveTileFile, "Saves the current heightmap to the larger file.");
            saveToTileCommand.AddArgument("filename",
                                            "The file you wish to save to, the file extension determines the loader to be used. Supported extensions include: " +
                                            _supportFileExtensionsForTileSave, "String");
            saveToTileCommand.AddArgument("file width", "The width of the file in tiles", "Integer");
            saveToTileCommand.AddArgument("file height", "The height of the file in tiles", "Integer");
            saveToTileCommand.AddArgument("minimum X tile", "The X region coordinate of the first section on the file",
                                            "Integer");
            saveToTileCommand.AddArgument("minimum Y tile", "The Y region coordinate of the first tile on the file\n"
                                          + "= Example =\n"
                                          + "To save a PNG file for a set of map tiles 2 regions wide and 3 regions high from map co-ordinate (9910,10234)\n"
                                          + "        # terrain save-tile ST06.png 2 3 9910 10234\n",
                                          "Integer");

            // Terrain adjustments
            Command fillRegionCommand =
                new Command("fill", CommandIntentions.COMMAND_HAZARDOUS, InterfaceFillTerrain, "Fills the current heightmap with a specified value.");
            fillRegionCommand.AddArgument("value", "The numeric value of the height you wish to set your region to.",
                                          "Float");

            Command elevateCommand =
                new Command("elevate", CommandIntentions.COMMAND_HAZARDOUS, InterfaceElevateTerrain, "Raises the current heightmap by the specified amount.");
            elevateCommand.AddArgument("amount", "The amount of height to add to the terrain in meters.", "Float");

            Command lowerCommand =
                new Command("lower", CommandIntentions.COMMAND_HAZARDOUS, InterfaceLowerTerrain, "Lowers the current heightmap by the specified amount.");
            lowerCommand.AddArgument("amount", "The amount of height to remove from the terrain in meters.", "Float");

            Command multiplyCommand =
                new Command("multiply", CommandIntentions.COMMAND_HAZARDOUS, InterfaceMultiplyTerrain, "Multiplies the heightmap by the value specified.");
            multiplyCommand.AddArgument("value", "The value to multiply the heightmap by.", "Float");

            Command bakeRegionCommand =
                new Command("bake", CommandIntentions.COMMAND_HAZARDOUS, InterfaceBakeTerrain, "Saves the current terrain into the regions baked map.");
            Command revertRegionCommand =
                new Command("revert", CommandIntentions.COMMAND_HAZARDOUS, InterfaceRevertTerrain, "Loads the baked map terrain into the regions heightmap.");

            Command flipCommand =
                new Command("flip", CommandIntentions.COMMAND_HAZARDOUS, InterfaceFlipTerrain, "Flips the current terrain about the X or Y axis");
            flipCommand.AddArgument("direction", "[x|y] the direction to flip the terrain in", "String");

            Command rescaleCommand =
                new Command("rescale", CommandIntentions.COMMAND_HAZARDOUS, InterfaceRescaleTerrain, "Rescales the current terrain to fit between the given min and max heights");
            rescaleCommand.AddArgument("min", "min terrain height after rescaling", "Float");
            rescaleCommand.AddArgument("max", "max terrain height after rescaling", "Float");

            Command minCommand = new Command("min", CommandIntentions.COMMAND_HAZARDOUS, InterfaceMinTerrain, "Sets the minimum terrain height to the specified value.");
            minCommand.AddArgument("min", "terrain height to use as minimum", "Float");

            Command maxCommand = new Command("max", CommandIntentions.COMMAND_HAZARDOUS, InterfaceMaxTerrain, "Sets the maximum terrain height to the specified value.");
            maxCommand.AddArgument("min", "terrain height to use as maximum", "Float");


            // Debug
            Command showDebugStatsCommand =
                new Command("stats", CommandIntentions.COMMAND_STATISTICAL, InterfaceShowDebugStats,
                            "Shows some information about the regions heightmap for debugging purposes.");

            Command showCommand =
                new Command("show", CommandIntentions.COMMAND_NON_HAZARDOUS, InterfaceShow,
                            "Shows terrain height at a given co-ordinate.");
            showCommand.AddArgument("point", "point in <x>,<y> format with no spaces (e.g. 45,45)", "String");

             // Plugins
            Command pluginRunCommand =
                new Command("effect", CommandIntentions.COMMAND_HAZARDOUS, InterfaceRunPluginEffect, "Runs a specified plugin effect");
            pluginRunCommand.AddArgument("name", "The plugin effect you wish to run, or 'list' to see all plugins", "String");

            _commander.RegisterCommand("load", loadFromFileCommand);
            _commander.RegisterCommand("load-tile", loadFromTileCommand);
            _commander.RegisterCommand("save", saveToFileCommand);
            _commander.RegisterCommand("save-tile", saveToTileCommand);
            _commander.RegisterCommand("fill", fillRegionCommand);
            _commander.RegisterCommand("elevate", elevateCommand);
            _commander.RegisterCommand("lower", lowerCommand);
            _commander.RegisterCommand("multiply", multiplyCommand);
            _commander.RegisterCommand("bake", bakeRegionCommand);
            _commander.RegisterCommand("revert", revertRegionCommand);
            _commander.RegisterCommand("show", showCommand);
            _commander.RegisterCommand("stats", showDebugStatsCommand);
            _commander.RegisterCommand("effect", pluginRunCommand);
            _commander.RegisterCommand("flip", flipCommand);
            _commander.RegisterCommand("rescale", rescaleCommand);
            _commander.RegisterCommand("min", minCommand);
            _commander.RegisterCommand("max", maxCommand);

            // Add this to our scene so scripts can call these functions
            _scene.RegisterModuleCommander(_commander);

            // Add Modify command to Scene, since Command object requires fixed-length arglists
            _scene.AddCommand("Terrain", this, "terrain modify",
                               "terrain modify <operation> <value> [<area>] [<taper>]",
                               "Modifies the terrain as instructed." +
                               "\nEach operation can be limited to an area of effect:" +
                               "\n * -ell=x,y,rx[,ry] constrains the operation to an ellipse centred at x,y" +
                               "\n * -rec=x,y,dx[,dy] constrains the operation to a rectangle based at x,y" +
                               "\nEach operation can have its effect tapered based on distance from centre:" +
                               "\n * elliptical operations taper as cones" +
                               "\n * rectangular operations taper as pyramids"
                               ,
                               ModifyCommand);

        }

        public void ModifyCommand(string module, string[] cmd)
        {
            string result;
            Scene scene = SceneManager.Instance.CurrentScene;
            if (scene != null && scene != _scene)
            {
                result = string.Empty;
            }
            else if (cmd.Length > 2)
            {
                string operationType = cmd[2];


                ITerrainModifier operation;
                if (!_modifyOperations.TryGetValue(operationType, out operation))
                {
                    result = string.Format("Terrain Modify \"{0}\" not found.", operationType);
                }
                else if (cmd.Length > 3 && cmd[3] == "usage")
                {
                    result = "Usage: " + operation.GetUsage();
                }
                else
                {
                    result = operation.ModifyTerrain(_channel, cmd);
                }

                if (string.IsNullOrEmpty(result))
                {
                    result = "Modified terrain";
                    _log.DebugFormat("Performed terrain operation {0}", operationType);
                }
            }
            else
            {
                result = "Usage: <operation-name> <arg1> <arg2>...";
            }
            if (!string.IsNullOrEmpty(result))
            {
                MainConsole.Instance.Output(result);
            }
        }

#endregion

    }
}
