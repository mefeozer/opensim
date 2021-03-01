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
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Timers;
using System.Threading;
using System.Xml;
using System.Xml.Serialization;

using OpenMetaverse;
using log4net;
using Mono.Addins;
using Nini.Config;
using OpenSim.Framework;
using OpenSim.Region.CoreModules.Framework.InterfaceCommander;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

using Timer= System.Timers.Timer;

namespace OpenSim.Region.OptionalModules.World.TreePopulator
{
    /// <summary>
    /// Version 2.02 - Still hacky
    /// </summary>
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "TreePopulatorModule")]
    public class TreePopulatorModule : INonSharedRegionModule, ICommandableModule
    {
        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private readonly Commander _commander = new Commander("tree");
        private Scene _scene;

        [XmlRootAttribute(ElementName = "Copse", IsNullable = false)]
        public class Copse
        {
            public string _name;
            public bool _frozen;
            public Tree _tree_type;
            public int _tree_quantity;
            public float _treeline_low;
            public float _treeline_high;
            public Vector3 _seed_point;
            public double _range;
            public Vector3 _initial_scale;
            public Vector3 _maximu_scale;
            public Vector3 _rate;

            [XmlIgnore]
            public bool _planted;
            [XmlIgnore]
            public List<UUID> _trees;

            public Copse()
            {
            }

            public Copse(string fileName, bool planted)
            {
                Copse cp = (Copse)DeserializeObject(fileName);

                _name = cp._name;
                _frozen = cp._frozen;
                _tree_quantity = cp._tree_quantity;
                _treeline_high = cp._treeline_high;
                _treeline_low = cp._treeline_low;
                _range = cp._range;
                _tree_type = cp._tree_type;
                _seed_point = cp._seed_point;
                _initial_scale = cp._initial_scale;
                _maximu_scale = cp._maximu_scale;
                _initial_scale = cp._initial_scale;
                _rate = cp._rate;
                _planted = planted;
                _trees = new List<UUID>();
            }

            public Copse(string copsedef)
            {
                char[] delimiterChars = {':', ';'};
                string[] field = copsedef.Split(delimiterChars);

                _name = field[1].Trim();
                _frozen = copsedef[0] == 'F';
                _tree_quantity = int.Parse(field[2]);
                _treeline_high = float.Parse(field[3], Culture.NumberFormatInfo);
                _treeline_low = float.Parse(field[4], Culture.NumberFormatInfo);
                _range = double.Parse(field[5], Culture.NumberFormatInfo);
                _tree_type = (Tree) Enum.Parse(typeof(Tree),field[6]);
                _seed_point = Vector3.Parse(field[7]);
                _initial_scale = Vector3.Parse(field[8]);
                _maximu_scale = Vector3.Parse(field[9]);
                _rate = Vector3.Parse(field[10]);
                _planted = true;
                _trees = new List<UUID>();
            }

            public Copse(string name, int quantity, float high, float low, double range, Vector3 point, Tree type, Vector3 scale, Vector3 max_scale, Vector3 rate, List<UUID> trees)
            {
                _name = name;
                _frozen = false;
                _tree_quantity = quantity;
                _treeline_high = high;
                _treeline_low = low;
                _range = range;
                _tree_type = type;
                _seed_point = point;
                _initial_scale = scale;
                _maximu_scale = max_scale;
                _rate = rate;
                _planted = false;
                _trees = trees;
            }

            public override string ToString()
            {
                string frozen = _frozen ? "F" : "A";

                return string.Format("{0}TPM: {1}; {2}; {3:0.0}; {4:0.0}; {5:0.0}; {6}; {7:0.0}; {8:0.0}; {9:0.0}; {10:0.00};",
                    frozen,
                    _name,
                    _tree_quantity,
                    _treeline_high,
                    _treeline_low,
                    _range,
                    _tree_type,
                    _seed_point.ToString(),
                    _initial_scale.ToString(),
                    _maximu_scale.ToString(),
                    _rate.ToString());
            }
        }

        private List<Copse> _copses = new List<Copse>();
        private object mylock;
        private double _update_ms = 1000.0; // msec between updates
        private bool _active_trees = false;
        private bool _enabled = true; // original default
        private bool _allowGrow = true; // original default

        Timer CalculateTrees;

        #region ICommandableModule Members

        public ICommander CommandInterface => _commander;

        #endregion

        #region Region Module interface

        public void Initialise(IConfigSource config)
        {
            IConfig moduleConfig = config.Configs["Trees"];
            if (moduleConfig != null)
            {
                _enabled = moduleConfig.GetBoolean("enabled", _enabled);
                _active_trees = moduleConfig.GetBoolean("active_trees", _active_trees);
                _allowGrow = moduleConfig.GetBoolean("allowGrow", _allowGrow);
                _update_ms = moduleConfig.GetDouble("update_rate", _update_ms);
            }

            if(!_enabled)
                return;

            _copses =  new List<Copse>();
            mylock = new object();

            InstallCommands();

            _log.Debug("[TREES]: Initialised tree populator module");
        }

        public void AddRegion(Scene scene)
        {
            if(!_enabled)
                return;
            _scene = scene;
            _scene.RegisterModuleCommander(_commander);
            _scene.EventManager.OnPluginConsole += EventManager_OnPluginConsole;
            _scene.EventManager.OnPrimsLoaded += EventManager_OnPrimsLoaded;
        }

        public void RemoveRegion(Scene scene)
        {
            if(!_enabled)
                return;
            if(_active_trees && CalculateTrees != null)
            {
                CalculateTrees.Dispose();
                CalculateTrees = null;
            }
            _scene.EventManager.OnPluginConsole -= EventManager_OnPluginConsole;
            _scene.EventManager.OnPrimsLoaded -= EventManager_OnPrimsLoaded;
        }   

        public void RegionLoaded(Scene scene)
        {
        }

        public void Close()
        {
        }

        public string Name => "TreePopulatorModule";

        public Type ReplaceableInterface => null;

        #endregion

        //--------------------------------------------------------------

        private void EventManager_OnPrimsLoaded(Scene s)
        {
            ReloadCopse();
            if (_copses.Count > 0)
                _log.Info("[TREES]: Copses loaded" );

            if (_active_trees)
                activeizeTreeze(true);
        }

        #region ICommandableModule Members

        private void HandleTreeActive(object[] args)
        {
            if ((bool)args[0] && !_active_trees)
            {
                _log.InfoFormat("[TREES]: Activating Trees");
                _active_trees = true;
                activeizeTreeze(_active_trees);
            }
            else if (!(bool)args[0] && _active_trees)
            {
                _log.InfoFormat("[TREES]: Trees module is no longer active");
                _active_trees = false;
                activeizeTreeze(_active_trees);
            }
            else
            {
                _log.InfoFormat("[TREES]: Trees module is already in the required state");
            }
        }

        private void HandleTreeFreeze(object[] args)
        {
            string copsename = ((string)args[0]).Trim();
            bool freezeState = (bool) args[1];

            lock(mylock)
            {
                foreach (Copse cp in _copses)
                {
                    if (cp._name != copsename)
                        continue;

                    if(!cp._frozen && freezeState || cp._frozen && !freezeState)
                    {
                        cp._frozen = freezeState;
                        List<UUID> losttrees = new List<UUID>();
                        foreach (UUID tree in cp._trees)
                        {
                            SceneObjectGroup sog = _scene.GetSceneObjectGroup(tree);
                            if(sog != null && !sog.IsDeleted)
                            {
                                SceneObjectPart sop = sog.RootPart;
                                string name = sop.Name;
                                if(freezeState)
                                {
                                    if(name.StartsWith("FTPM"))
                                        continue;
                                    if(!name.StartsWith("ATPM"))
                                        continue;
                                    sop.Name = sop.Name.Replace("ATPM", "FTPM");
                                }
                                else
                                {
                                    if(name.StartsWith("ATPM"))
                                        continue;
                                    if(!name.StartsWith("FTPM"))
                                        continue;
                                    sop.Name = sop.Name.Replace("FTPM", "ATPM");
                                }
                                sop.ParentGroup.HasGroupChanged = true;
                                sog.ScheduleGroupForFullUpdate();
                            }
                            else
                               losttrees.Add(tree);
                        }
                        foreach (UUID tree in losttrees)
                            cp._trees.Remove(tree);

                        _log.InfoFormat("[TREES]: Activity for copse {0} is frozen {1}", copsename, freezeState);
                        return;
                    }
                    else
                    {
                        _log.InfoFormat("[TREES]: Copse {0} is already in the requested freeze state", copsename);
                        return;
                    }
                }
            }
            _log.InfoFormat("[TREES]: Copse {0} was not found - command failed", copsename);
        }

        private void HandleTreeLoad(object[] args)
        {
            Copse copse;

            _log.InfoFormat("[TREES]: Loading copse definition....");

            lock(mylock)
            {
                copse = new Copse((string)args[0], false);
                {
                    foreach (Copse cp in _copses)
                    {
                        if (cp._name == copse._name)
                        {
                            _log.InfoFormat("[TREES]: Copse: {0} is already defined - command failed", copse._name);
                            return;
                        }
                    }
                }
                _copses.Add(copse);
            }
            _log.InfoFormat("[TREES]: Loaded copse: {0}", copse.ToString());
        }

        private void HandleTreePlant(object[] args)
        {
            string copsename = ((string)args[0]).Trim();

            _log.InfoFormat("[TREES]: New tree planting for copse {0}", copsename);
            UUID uuid = _scene.RegionInfo.EstateSettings.EstateOwner;

            lock(mylock)
            {
                foreach (Copse copse in _copses)
                {
                    if (copse._name == copsename)
                    {
                        if (!copse._planted)
                        {
                            // The first tree for a copse is created here
                            CreateTree(uuid, copse, copse._seed_point, true);
                            copse._planted = true;
                            return;
                        }
                        else
                        {
                            _log.InfoFormat("[TREES]: Copse {0} has already been planted", copsename);
                            return;
                        }
                    }
                }
            }
            _log.InfoFormat("[TREES]: Copse {0} not found for planting", copsename);
        }

        private void HandleTreeRate(object[] args)
        {
            _update_ms = (double)args[0];
            if (_update_ms >= 1000.0)
            {
                if (_active_trees)
                {
                    activeizeTreeze(false);
                    activeizeTreeze(true);
                }
                _log.InfoFormat("[TREES]: Update rate set to {0} mSec", _update_ms);
            }
            else
            {
                _log.InfoFormat("[TREES]: minimum rate is 1000.0 mSec - command failed");
            }
        }

        private void HandleTreeReload(object[] args)
        {
            if (_active_trees)
            {
                CalculateTrees.Stop();
            }

            ReloadCopse();

            if (_active_trees)
            {
                CalculateTrees.Start();
            }
        }

        private void HandleTreeRemove(object[] args)
        {
            string copsename = ((string)args[0]).Trim();
            Copse copseIdentity = null;

            lock(mylock)
            {
                foreach (Copse cp in _copses)
                {
                    if (cp._name == copsename)
                    {
                        copseIdentity = cp;
                    }
                }

                if (copseIdentity != null)
                {
                    foreach (UUID tree in copseIdentity._trees)
                    {
                        if (_scene.Entities.ContainsKey(tree))
                        {
                            SceneObjectPart selectedTree = ((SceneObjectGroup)_scene.Entities[tree]).RootPart;
                            // Delete tree and alert clients (not silent)
                            _scene.DeleteSceneObject(selectedTree.ParentGroup, false);
                        }
                        else
                        {
                            _log.DebugFormat("[TREES]: Tree not in scene {0}", tree);
                        }
                    }
                    copseIdentity._trees = null;
                    _copses.Remove(copseIdentity);
                    _log.InfoFormat("[TREES]: Copse {0} has been removed", copsename);
                }
                else
                {
                    _log.InfoFormat("[TREES]: Copse {0} was not found - command failed", copsename);
                }
            }
        }

        private void HandleTreeStatistics(object[] args)
        {
            _log.InfoFormat("[TREES]: region {0}:", _scene.Name);
            _log.InfoFormat("[TREES]:    Activity State: {0};  Update Rate: {1}", _active_trees, _update_ms);
            foreach (Copse cp in _copses)
            {
                _log.InfoFormat("[TREES]:    Copse {0}; {1} trees; frozen {2}", cp._name, cp._trees.Count, cp._frozen);
            }
        }

        private void InstallCommands()
        {
            Command treeActiveCommand =
                new Command("active", CommandIntentions.COMMAND_HAZARDOUS, HandleTreeActive, "Change activity state for the trees module");
            treeActiveCommand.AddArgument("activeTF", "The required activity state", "Boolean");

            Command treeFreezeCommand =
                new Command("freeze", CommandIntentions.COMMAND_HAZARDOUS, HandleTreeFreeze, "Freeze/Unfreeze activity for a defined copse");
            treeFreezeCommand.AddArgument("copse", "The required copse", "String");
            treeFreezeCommand.AddArgument("freezeTF", "The required freeze state", "Boolean");

            Command treeLoadCommand =
                new Command("load", CommandIntentions.COMMAND_HAZARDOUS, HandleTreeLoad, "Load a copse definition from an xml file");
            treeLoadCommand.AddArgument("filename", "The (xml) file you wish to load", "String");

            Command treePlantCommand =
                new Command("plant", CommandIntentions.COMMAND_HAZARDOUS, HandleTreePlant, "Start the planting on a copse");
            treePlantCommand.AddArgument("copse", "The required copse", "String");

            Command treeRateCommand =
                new Command("rate", CommandIntentions.COMMAND_HAZARDOUS, HandleTreeRate, "Reset the tree update rate (mSec)");
            treeRateCommand.AddArgument("updateRate", "The required update rate (minimum 1000.0)", "Double");

            Command treeReloadCommand =
                new Command("reload", CommandIntentions.COMMAND_HAZARDOUS, HandleTreeReload, "Reload copses from the in-scene trees");

            Command treeRemoveCommand =
                new Command("remove", CommandIntentions.COMMAND_HAZARDOUS, HandleTreeRemove, "Remove a copse definition and all its in-scene trees");
            treeRemoveCommand.AddArgument("copse", "The required copse", "String");

            Command treeStatisticsCommand =
                new Command("statistics", CommandIntentions.COMMAND_STATISTICAL, HandleTreeStatistics, "Log statistics about the trees");

            _commander.RegisterCommand("active", treeActiveCommand);
            _commander.RegisterCommand("freeze", treeFreezeCommand);
            _commander.RegisterCommand("load", treeLoadCommand);
            _commander.RegisterCommand("plant", treePlantCommand);
            _commander.RegisterCommand("rate", treeRateCommand);
            _commander.RegisterCommand("reload", treeReloadCommand);
            _commander.RegisterCommand("remove", treeRemoveCommand);
            _commander.RegisterCommand("statistics", treeStatisticsCommand);
        }

        /// <summary>
        /// Processes commandline input. Do not call directly.
        /// </summary>
        /// <param name="args">Commandline arguments</param>
        private void EventManager_OnPluginConsole(string[] args)
        {
            if (args[0] == "tree")
            {
                if (args.Length == 1)
                {
                    _commander.ProcessConsoleCommand("help", new string[0]);
                    return;
                }

                string[] tmpArgs = new string[args.Length - 2];
                int i;
                for (i = 2; i < args.Length; i++)
                {
                    tmpArgs[i - 2] = args[i];
                }

                _commander.ProcessConsoleCommand(args[1], tmpArgs);
            }
        }
        #endregion

        #region IVegetationModule Members

        public SceneObjectGroup AddTree(
            UUID uuid, UUID groupID, Vector3 scale, Quaternion rotation, Vector3 position, Tree treeType, bool newTree)
        {
            PrimitiveBaseShape treeShape = new PrimitiveBaseShape
            {
                PathCurve = 16,
                PathEnd = 49900,
                PCode = newTree ? (byte)PCode.NewTree : (byte)PCode.Tree,
                Scale = scale,
                State = (byte)treeType
            };

            SceneObjectGroup sog = new SceneObjectGroup(uuid, position, rotation, treeShape);
            SceneObjectPart rootPart = sog.RootPart;

            rootPart.AddFlag(PrimFlags.Phantom);

            sog.SetGroup(groupID, null);
            _scene.AddNewSceneObject(sog, true, false);
            sog.IsSelected = false;
            rootPart.IsSelected = false;
            sog.InvalidateEffectivePerms();
            return sog;
        }

        #endregion

        //--------------------------------------------------------------

        #region Tree Utilities
        static public void SerializeObject(string fileName, object obj)
        {
            try
            {
                XmlSerializer xs = new XmlSerializer(typeof(Copse));

                using (XmlTextWriter writer = new XmlTextWriter(fileName, Util.UTF8))
                {
                    writer.Formatting = Formatting.Indented;
                    xs.Serialize(writer, obj);
                }
            }
            catch (SystemException ex)
            {
                throw new ApplicationException("Unexpected failure in Tree serialization", ex);
            }
        }

        static public object DeserializeObject(string fileName)
        {
            try
            {
                XmlSerializer xs = new XmlSerializer(typeof(Copse));

                using (FileStream fs = new FileStream(fileName, FileMode.Open, FileAccess.Read))
                    return xs.Deserialize(fs);
            }
            catch (SystemException ex)
            {
                throw new ApplicationException("Unexpected failure in Tree de-serialization", ex);
            }
        }

        private void ReloadCopse()
        {
            _copses = new List<Copse>();

            List<SceneObjectGroup> grps = _scene.GetSceneObjectGroups();
            foreach (SceneObjectGroup grp in grps)
            {
                if(grp.RootPart.Shape.PCode != (byte)PCode.NewTree && grp.RootPart.Shape.PCode != (byte)PCode.Tree)
                    continue;

                string grpname = grp.Name;
                if (grpname.Length > 5 && (grpname.Substring(0, 5) == "ATPM:" || grpname.Substring(0, 5) == "FTPM:"))
                {
                    // Create a new copse definition or add uuid to an existing definition
                    try
                    {
                        bool copsefound = false;
                        Copse grpcopse = new Copse(grpname);

                        lock(mylock)
                        {
                            foreach (Copse cp in _copses)
                            {
                                if (cp._name == grpcopse._name)
                                {
                                    copsefound = true;
                                    cp._trees.Add(grp.UUID);
                                    //_log.DebugFormat("[TREES]: Found tree {0}", grp.UUID);
                                }
                            }

                            if (!copsefound)
                            {
                                _log.InfoFormat("[TREES]: adding copse {0}", grpcopse._name);
                                grpcopse._trees.Add(grp.UUID);
                                _copses.Add(grpcopse);
                            }
                        }
                    }
                    catch
                    {
                        _log.InfoFormat("[TREES]: Ill formed copse definition {0} - ignoring", grp.Name);
                    }
                }
            }
        }
        #endregion

        private void activeizeTreeze(bool activeYN)
        {
            if (activeYN)
            {
                if(CalculateTrees == null)
                    CalculateTrees = new Timer(_update_ms);
                CalculateTrees.Elapsed += CalculateTrees_Elapsed;
                CalculateTrees.AutoReset = false;
                CalculateTrees.Start();
            }
            else
            {
                 CalculateTrees.Stop();
            }
        }

        private void growTrees()
        {
            if(!_allowGrow)
                return;

            foreach (Copse copse in _copses)
            {
                if (copse._frozen)
                    continue;

                if(copse._trees.Count == 0)
                    continue;

                float maxscale = copse._maximu_scale.Z;
                float ratescale = 1.0f;
                List<UUID> losttrees = new List<UUID>();
                foreach (UUID tree in copse._trees)
                {
                    SceneObjectGroup sog = _scene.GetSceneObjectGroup(tree);

                    if (sog != null && !sog.IsDeleted)
                    {
                        SceneObjectPart s_tree = sog.RootPart;
                        if (s_tree.Scale.Z < maxscale)
                        {
                            ratescale = (float)Util.RandomClass.NextDouble();
                            if(ratescale < 0.2f)
                                ratescale = 0.2f;
                            s_tree.Scale += copse._rate * ratescale;
                            sog.HasGroupChanged = true;
                            s_tree.ScheduleFullUpdate();
                        }
                    }
                    else
                        losttrees.Add(tree);
                }

                foreach (UUID tree in losttrees)
                    copse._trees.Remove(tree);
            }
        }

        private void seedTrees()
        {
            foreach (Copse copse in _copses)
            {
                if (copse._frozen)
                    continue;

                if(copse._trees.Count == 0)
                    return;

                bool low = copse._trees.Count < (int)(copse._tree_quantity * 0.8f);

                if (!low && Util.RandomClass.NextDouble() < 0.75)
                    return;

                int maxbirths =  (int)copse._tree_quantity - copse._trees.Count;
                if(maxbirths <= 1)
                    return;

                if(maxbirths > 20)
                    maxbirths = 20;

                float minscale = 0;
                if(!low && _allowGrow)
                    minscale = copse._maximu_scale.Z * 0.75f;;

                int i = 0;
                UUID[] current = copse._trees.ToArray();
                while(--maxbirths > 0)
                {
                    if(current.Length > 1)
                        i = Util.RandomClass.Next(current.Length -1);

                    UUID tree = current[i];
                    SceneObjectGroup sog = _scene.GetSceneObjectGroup(tree);

                    if (sog != null && !sog.IsDeleted)
                    {
                        SceneObjectPart s_tree = sog.RootPart;

                        // Tree has grown enough to seed if it has grown by at least 25% of seeded to full grown height
                        if (s_tree.Scale.Z > minscale)
                                SpawnChild(copse, s_tree, true);
                    }
                    else if(copse._trees.Contains(tree))
                        copse._trees.Remove(tree);
                }                   
            }
        }

        private void killTrees()
        {
            foreach (Copse copse in _copses)
            {
                if (copse._frozen)
                    continue;

                if (Util.RandomClass.NextDouble() < 0.25)
                    return;

                int maxbdeaths = copse._trees.Count - (int)(copse._tree_quantity * .98f) ;
                if(maxbdeaths < 1)
                    return;

                float odds;
                float scale = 1.0f / copse._maximu_scale.Z;

                int ntries = maxbdeaths * 4;
                while(ntries-- > 0 )
                {
                    int next = 0;
                    if (copse._trees.Count > 1)
                        next = Util.RandomClass.Next(copse._trees.Count - 1);
                    UUID tree = copse._trees[next];
                    SceneObjectGroup sog = _scene.GetSceneObjectGroup(tree);
                    if (sog != null && !sog.IsDeleted)
                    {
                        if(_allowGrow)
                        {
                            odds = sog.RootPart.Scale.Z * scale;
                            odds = odds * odds * odds;
                            odds *= (float)Util.RandomClass.NextDouble();
                        }
                        else
                        {
                            odds = (float)Util.RandomClass.NextDouble();
                            odds = odds * odds * odds;
                        }

                        if(odds > 0.9f)
                        {
                            _scene.DeleteSceneObject(sog, false);
                            if(maxbdeaths <= 0)
                                break;
                        }
                    }            
                    else
                    {
                        copse._trees.Remove(tree);
                        if(copse._trees.Count - (int)(copse._tree_quantity * .98f) <= 0 )
                            break;
                    }
                }
            }
        }

        private void SpawnChild(Copse copse, SceneObjectPart s_tree, bool low)
        {
            Vector3 position = new Vector3();
           
            float randX = copse._maximu_scale.X * 1.25f;
            float randY = copse._maximu_scale.Y * 1.25f;
            
            float r = (float)Util.RandomClass.NextDouble();
            randX *=  2.0f * r - 1.0f;
            position.X = s_tree.AbsolutePosition.X + (float)randX;
            
            r = (float)Util.RandomClass.NextDouble();
            randY *=  2.0f * r - 1.0f;
            position.Y = s_tree.AbsolutePosition.Y + (float)randY;

            if (position.X > _scene.RegionInfo.RegionSizeX - 1 || position.X <= 0 ||
                position.Y > _scene.RegionInfo.RegionSizeY - 1 || position.Y <= 0)
                return;

            randX = position.X - copse._seed_point.X;
            randX *= randX;
            randY = position.Y - copse._seed_point.Y;
            randY *= randY;
            randX += randY;

            if(randX > copse._range * copse._range)
                return;

            UUID uuid = _scene.RegionInfo.EstateSettings.EstateOwner;
            CreateTree(uuid, copse, position, low);
        }

        private void CreateTree(UUID uuid, Copse copse, Vector3 position, bool randomScale)
        {
            position.Z = (float)_scene.Heightmap[(int)position.X, (int)position.Y];
            if (position.Z < copse._treeline_low || position.Z > copse._treeline_high)
                return;

            Vector3 scale = copse._initial_scale;
            if(randomScale)
            {
                try
                {
                    float t;
                    float r = (float)Util.RandomClass.NextDouble();
                    r *= (float)Util.RandomClass.NextDouble();
                    r *= (float)Util.RandomClass.NextDouble();

                    t = copse._maximu_scale.X / copse._initial_scale.X;
                    if(t < 1.0)
                        t = 1 / t;
                    t = t * r + 1.0f;
                    scale.X *= t;

                    t = copse._maximu_scale.Y / copse._initial_scale.Y;
                    if(t < 1.0)
                        t = 1 / t;
                    t = t * r + 1.0f;
                    scale.Y *= t;

                    t = copse._maximu_scale.Z / copse._initial_scale.Z;
                    if(t < 1.0)
                        t = 1 / t;
                    t = t * r + 1.0f;
                    scale.Z *= t;
                }
                catch
                {
                    scale = copse._initial_scale;
                }
            }

            SceneObjectGroup tree = AddTree(uuid, UUID.Zero, scale, Quaternion.Identity, position, copse._tree_type, false);
            tree.Name = copse.ToString();
            copse._trees.Add(tree.UUID);
            tree.RootPart.ScheduleFullUpdate();
        }

        private void CalculateTrees_Elapsed(object sender, ElapsedEventArgs e)
        {
            if(!_scene.IsRunning)
                return;
            
            if(Monitor.TryEnter(mylock))
            {
                try
                {
                    if(_scene.LoginsEnabled )
                    {
                        growTrees();
                        seedTrees();
                        killTrees();
                    }
                }
                catch { }
                if(CalculateTrees != null)
                    CalculateTrees.Start();
                Monitor.Exit(mylock);
            }
        }
    }
}

