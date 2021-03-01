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
using System.Text;
using System.Xml;
using System.Collections.Generic;
using System.Collections;
using System.Reflection;
using OpenMetaverse;
using log4net;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using PermissionMask = OpenSim.Framework.PermissionMask;

namespace OpenSim.Region.Framework.Scenes
{
    public class SceneObjectPartInventory : IEntityInventory , IDisposable
    {
        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private byte[] _inventoryFileData = new byte[0];
        private byte[] _inventoryFileNameBytes = new byte[0];
        private string _inventoryFileName = "";
        private uint _inventoryFileNameSerial = 0;
        private bool _inventoryPrivileged = false;
        private readonly object _inventoryFileLock = new object();

        private readonly Dictionary<UUID, ArrayList> _scriptErrors = new Dictionary<UUID, ArrayList>();

        /// <value>
        /// The part to which the inventory belongs.
        /// </value>
        private readonly SceneObjectPart _part;

        /// <summary>
        /// Serial count for inventory file , used to tell if inventory has changed
        /// no need for this to be part of Database backup
        /// </summary>
        protected uint _inventorySerial = 0;

        /// <summary>
        /// Holds in memory prim inventory
        /// </summary>
        protected TaskInventoryDictionary _items = new TaskInventoryDictionary();
        protected Dictionary<UUID, TaskInventoryItem> _scripts = null;
        /// <summary>
        /// Tracks whether inventory has changed since the last persistent backup
        /// </summary>
        internal bool HasInventoryChanged;

        /// <value>
        /// Inventory serial number
        /// </value>
        protected internal uint Serial
        {
            get => _inventorySerial;
            set => _inventorySerial = value;
        }

        /// <value>
        /// Raw inventory data
        /// </value>
        protected internal TaskInventoryDictionary Items
        {
            get => _items;
            set
            {
                _items = value;
                _inventorySerial++;
                gatherScriptsAndQueryStates();
            }
        }

        public int Count
        {
            get
            {
                _items.LockItemsForRead(true);
                int c = _items.Count;
                _items.LockItemsForRead(false);
                return c;
            }
        }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="part">
        /// A <see cref="SceneObjectPart"/>
        /// </param>
        public SceneObjectPartInventory(SceneObjectPart part)
        {
            _part = part;
        }

        ~SceneObjectPartInventory()
        {
            Dispose(false);
        }

        private bool disposed = false;
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected void Dispose(bool disposing)
        {
            // Check to see if Dispose has already been called.
            if (!disposed)
            {
                if (_items != null)
                {
                    _items.Dispose();
                    _items = null;
                }
                disposed = true;
            }
        }

        /// <summary>
        /// Force the task inventory of this prim to persist at the next update sweep
        /// </summary>
        public void ForceInventoryPersistence()
        {
            HasInventoryChanged = true;
        }

        /// <summary>
        /// Reset UUIDs for all the items in the prim's inventory.
        /// </summary>
        /// <remarks>
        /// This involves either generating
        /// new ones or setting existing UUIDs to the correct parent UUIDs.
        ///
        /// If this method is called and there are inventory items, then we regard the inventory as having changed.
        /// </remarks>
        public void ResetInventoryIDs()
        {
            if (_part == null)
                return;

            _items.LockItemsForWrite(true);
            if (_items.Count == 0)
            {
                _items.LockItemsForWrite(false);
                return;
            }

            UUID partID = _part.UUID;
            IList<TaskInventoryItem> items = new List<TaskInventoryItem>(_items.Values);
            _items.Clear();
            if(_scripts == null)
            {
                for (int i = 0; i < items.Count; ++i)
                {
                    TaskInventoryItem item = items[i];
                    item.ResetIDs(partID);
                    _items.Add(item.ItemID, item);
                }
            }
            else
            {
                _scripts.Clear();
                for (int i = 0; i < items.Count; ++i)
                {
                    TaskInventoryItem item = items[i];
                    item.ResetIDs(partID);
                    _items.Add(item.ItemID, item);
                    if (item.InvType == (int)InventoryType.LSL)
                        _scripts.Add(item.ItemID, item);
                }
            }
            _inventorySerial++;
            _items.LockItemsForWrite(false);
        }

        public void ResetObjectID()
        {
            if (_part == null)
                return;

            UUID partID = _part.UUID;

            _items.LockItemsForWrite(true);

            if (_items.Count == 0)
            {
                _items.LockItemsForWrite(false);
                return;
            }
            foreach(TaskInventoryItem item in _items.Values)
            {
                item.ParentPartID = partID;
                item.ParentID = partID;
            }
            _inventorySerial++;
            _items.LockItemsForWrite(false);
        }

        /// <summary>
        /// Change every item in this inventory to a new owner.
        /// </summary>
        /// <param name="ownerId"></param>
        public void ChangeInventoryOwner(UUID ownerId)
        {
            if(_part == null)
                return;

            _items.LockItemsForWrite(true);
            if (_items.Count == 0)
            {
                _items.LockItemsForWrite(false);
                return;
            }

            foreach (TaskInventoryItem item in _items.Values)
            {
                if (ownerId != item.OwnerID)
                    item.LastOwnerID = item.OwnerID;

                item.OwnerID = ownerId;
                item.PermsMask = 0;
                item.PermsGranter = UUID.Zero;
                item.OwnerChanged = true;
            }

            HasInventoryChanged = true;
            _part.ParentGroup.HasGroupChanged = true;
            _inventorySerial++;
            _items.LockItemsForWrite(false);
        }

        /// <summary>
        /// Change every item in this inventory to a new group.
        /// </summary>
        /// <param name="groupID"></param>
        public void ChangeInventoryGroup(UUID groupID)
        {
            if(_part == null)
                return;

            _items.LockItemsForWrite(true);
            if (_items.Count == 0)
            {
                _items.LockItemsForWrite(false);
                return;
            }
            _inventorySerial++;
            // Don't let this set the HasGroupChanged flag for attachments
            // as this happens during rez and we don't want a new asset
            // for each attachment each time
            if (!_part.ParentGroup.IsAttachment)
            {
                HasInventoryChanged = true;
                _part.ParentGroup.HasGroupChanged = true;
            }

            foreach (TaskInventoryItem item in _items.Values)
                    item.GroupID = groupID;

            _items.LockItemsForWrite(false);
        }

        private void gatherScriptsAndQueryStates()
        {
            _items.LockItemsForWrite(true);
            _scripts = new Dictionary<UUID, TaskInventoryItem>();
            foreach (TaskInventoryItem item in _items.Values)
            {
                if (item.InvType == (int)InventoryType.LSL)
                    _scripts[item.ItemID] = item;
            }
            if (_scripts.Count == 0)
            {
                _items.LockItemsForWrite(false);
                _scripts = null;
                return;
            }
            _items.LockItemsForWrite(false);

            if (_part.ParentGroup == null || _part.ParentGroup.Scene == null)
                return;

            IScriptModule[] scriptEngines = _part.ParentGroup.Scene.RequestModuleInterfaces<IScriptModule>();
            if (scriptEngines.Length == 0) // No engine at all
                return;

            bool running;

            _items.LockItemsForRead(true);

            foreach (TaskInventoryItem item in _scripts.Values)
            {
                //running = false;
                foreach (IScriptModule e in scriptEngines)
                {
                    if (e.HasScript(item.ItemID, out running))
                    {
                        item.ScriptRunning = running;
                        break;
                    }
                }
                //item.ScriptRunning = running;
            }

            _items.LockItemsForRead(false);
        }

        public bool TryGetScriptInstanceRunning(UUID itemId, out bool running)
        {
            running = false;

            TaskInventoryItem item = GetInventoryItem(itemId);

            if (item == null)
                return false;

            return TryGetScriptInstanceRunning(_part.ParentGroup.Scene, item, out running);
        }

        public static bool TryGetScriptInstanceRunning(Scene scene, TaskInventoryItem item, out bool running)
        {
            running = false;

            if (item.InvType != (int)InventoryType.LSL)
                return false;

            IScriptModule[] scriptEngines = scene.RequestModuleInterfaces<IScriptModule>();
            if (scriptEngines.Length == 0) // No engine at all
                return false;

            foreach (IScriptModule e in scriptEngines)
            {
                if (e.HasScript(item.ItemID, out running))
                    return true;
            }

            return false;
        }

        public int CreateScriptInstances(int startParam, bool postOnRez, string engine, int stateSource)
        {
            _items.LockItemsForRead(true);
            if(_scripts == null || _scripts.Count == 0)
            {
                _items.LockItemsForRead(false);
                return 0;
            }
            List<TaskInventoryItem> scripts = new List<TaskInventoryItem>(_scripts.Values);
            _items.LockItemsForRead(false);

            int scriptsValidForStarting = 0;
            for (int i = 0; i < scripts.Count; ++i)
            {
                if (CreateScriptInstance(scripts[i], startParam, postOnRez, engine, stateSource))
                    scriptsValidForStarting++;
            }
            return scriptsValidForStarting;
        }

        public ArrayList GetScriptErrors(UUID itemID)
        {
            IScriptModule[] scriptEngines = _part.ParentGroup.Scene.RequestModuleInterfaces<IScriptModule>();
            ArrayList ret = new ArrayList();
            foreach (IScriptModule e in scriptEngines)
            {
                if (e != null)
                {
                    ArrayList errors = e.GetScriptErrors(itemID);
                    foreach (object line in errors)
                        ret.Add(line);
                }
            }

            return ret;
        }

        /// <summary>
        /// Stop and remove all the scripts in this prim.
        /// </summary>
        /// <param name="sceneObjectBeingDeleted">
        /// Should be true if these scripts are being removed because the scene
        /// object is being deleted.  This will prevent spurious updates to the client.
        /// </param>
        public void RemoveScriptInstances(bool sceneObjectBeingDeleted)
        {
            _items.LockItemsForRead(true);
            if (_scripts == null || _scripts.Count == 0)
            {
                _items.LockItemsForRead(false);
                return;
            }
            List<TaskInventoryItem> scripts = new List<TaskInventoryItem>(_scripts.Values);
            _items.LockItemsForRead(false);

            foreach (TaskInventoryItem item in scripts)
            {
                RemoveScriptInstance(item.ItemID, sceneObjectBeingDeleted);
                _part.RemoveScriptEvents(item.ItemID);
            }
        }

        /// <summary>
        /// Stop all the scripts in this prim.
        /// </summary>
        public void StopScriptInstances()
        {
            _items.LockItemsForRead(true);
            if (_scripts == null || _scripts.Count == 0)
            {
                _items.LockItemsForRead(false);
                return;
            }
            List<TaskInventoryItem> scripts = new List<TaskInventoryItem>(_scripts.Values);
            _items.LockItemsForRead(false);

            foreach (TaskInventoryItem item in scripts)
                StopScriptInstance(item);
        }

        /// <summary>
        /// Start a script which is in this prim's inventory.
        /// </summary>
        /// <param name="item"></param>
        /// <returns>true if the script instance was created, false otherwise</returns>
        public bool CreateScriptInstance(TaskInventoryItem item, int startParam, bool postOnRez, string engine, int stateSource)
        {
            //_log.DebugFormat("[PRIM INVENTORY]: Starting script {0} {1} in prim {2} {3} in {4}",
            //    item.Name, item.ItemID, _part.Name, _part.UUID, _part.ParentGroup.Scene.RegionInfo.RegionName);

            if (!_part.ParentGroup.Scene.Permissions.CanRunScript(item, _part))
            {
                StoreScriptError(item.ItemID, "no permission");
                return false;
            }

            _part.AddFlag(PrimFlags.Scripted);

            if (_part.ParentGroup.Scene.RegionInfo.RegionSettings.DisableScripts)
                return false;

            UUID itemID = item.ItemID;

            _items.LockItemsForRead(true);
            if (!_items.TryGetValue(item.ItemID, out TaskInventoryItem it))
            {
                _items.LockItemsForRead(false);

                StoreScriptError(itemID, string.Format("TaskItem ID {0} could not be found", item.ItemID));
                _log.ErrorFormat(
                    "[PRIM INVENTORY]: Couldn't start script {0}, {1} at {2} in {3} since taskitem ID {4} could not be found",
                    item.Name, item.ItemID, _part.AbsolutePosition,
                    _part.ParentGroup.Scene.RegionInfo.RegionName, item.ItemID);
                return false;
            }
            _items.LockItemsForRead(false);

            if (stateSource == 2 && _part.ParentGroup.Scene._trustBinaries)
            {
                // Prim crossing
                _items.LockItemsForWrite(true);
                it.PermsMask = 0;
                it.PermsGranter = UUID.Zero;
                _items.LockItemsForWrite(false);

                _part.ParentGroup.Scene.EventManager.TriggerRezScript(
                    _part.LocalId, itemID, string.Empty, startParam, postOnRez, engine, stateSource);
                StoreScriptErrors(itemID, null);
                _part.ParentGroup.AddActiveScriptCount(1);
                _part.ScheduleFullUpdate();
                return true;
            }

            AssetBase asset = _part.ParentGroup.Scene.AssetService.Get(item.AssetID.ToString());
            if (asset == null)
            {
                StoreScriptError(itemID, string.Format("asset ID {0} could not be found", item.AssetID));
                _log.ErrorFormat(
                    "[PRIM INVENTORY]: Couldn't start script {0}, {1} at {2} in {3} since asset ID {4} could not be found",
                    item.Name, item.ItemID, _part.AbsolutePosition,
                    _part.ParentGroup.Scene.RegionInfo.RegionName, item.AssetID);

                return false;
            }

            if (_part.ParentGroup._savedScriptState != null)
                item.OldItemID = RestoreSavedScriptState(item.LoadedItemID, item.OldItemID, itemID);

            _items.LockItemsForWrite(true);
            it.OldItemID = item.OldItemID;
            it.PermsMask = 0;
            it.PermsGranter = UUID.Zero;
            _items.LockItemsForWrite(false);

            string script = Utils.BytesToString(asset.Data);
            _part.ParentGroup.Scene.EventManager.TriggerRezScript(
                _part.LocalId, itemID, script, startParam, postOnRez, engine, stateSource);
            StoreScriptErrors(itemID, null);
            if (!item.ScriptRunning)
                _part.ParentGroup.Scene.EventManager.TriggerStopScript(_part.LocalId, itemID);
            _part.ParentGroup.AddActiveScriptCount(1);
            _part.ScheduleFullUpdate();

            return true;
        }

        private UUID RestoreSavedScriptState(UUID loadedID, UUID oldID, UUID newID)
        {
            //_log.DebugFormat(
            //    "[PRIM INVENTORY]: Restoring scripted state for item {0}, oldID {1}, loadedID {2}",
            //     newID, oldID, loadedID);
            IScriptModule[] scriptEngines = _part.ParentGroup.Scene.RequestModuleInterfaces<IScriptModule>();
            if (scriptEngines.Length == 0) // No engine at all
                return oldID;

            UUID stateID = oldID;
            if (!_part.ParentGroup._savedScriptState.ContainsKey(oldID))
                stateID = loadedID;
            if (_part.ParentGroup._savedScriptState.ContainsKey(stateID))
            {
                XmlDocument doc = new XmlDocument();
                doc.LoadXml(_part.ParentGroup._savedScriptState[stateID]);

                ////////// CRUFT WARNING ///////////////////////////////////
                //
                // Old objects will have <ScriptState><State> ...
                // This format is XEngine ONLY
                //
                // New objects have <State Engine="...." ...><ScriptState>...
                // This can be passed to any engine
                //
                XmlNode n = doc.SelectSingleNode("ScriptState");
                if (n != null) // Old format data
                {
                    XmlDocument newDoc = new XmlDocument();

                    XmlElement rootN = newDoc.CreateElement("", "State", "");
                    XmlAttribute uuidA = newDoc.CreateAttribute("", "UUID", "");
                    uuidA.Value = stateID.ToString();
                    rootN.Attributes.Append(uuidA);
                    XmlAttribute engineA = newDoc.CreateAttribute("", "Engine", "");
                    engineA.Value = "XEngine";
                    rootN.Attributes.Append(engineA);

                    newDoc.AppendChild(rootN);

                    XmlNode stateN = newDoc.ImportNode(n, true);
                    rootN.AppendChild(stateN);

                    // This created document has only the minimun data
                    // necessary for XEngine to parse it successfully

                    //_log.DebugFormat("[PRIM INVENTORY]: Adding legacy state {0} in {1}", stateID, newID);

                    _part.ParentGroup._savedScriptState[stateID] = newDoc.OuterXml;
                }

                foreach (IScriptModule e in scriptEngines)
                {
                    if (e != null)
                    {
                        if (e.SetXMLState(newID, _part.ParentGroup._savedScriptState[stateID]))
                            break;
                    }
                }

                _part.ParentGroup._savedScriptState.Remove(stateID);
            }

            return stateID;
        }

        /// <summary>
        /// Start a script which is in this prim's inventory.
        /// Some processing may occur in the background, but this routine returns asap.
        /// </summary>
        /// <param name="itemId">
        /// A <see cref="UUID"/>
        /// </param>
        public bool CreateScriptInstance(UUID itemId, int startParam, bool postOnRez, string engine, int stateSource)
        {
            lock (_scriptErrors)
            {
                // Indicate to CreateScriptInstanceInternal() we don't want it to wait for completion
                _scriptErrors.Remove(itemId);
            }
            CreateScriptInstanceInternal(itemId, startParam, postOnRez, engine, stateSource);
            return true;
        }

        private void CreateScriptInstanceInternal(UUID itemId, int startParam, bool postOnRez, string engine, int stateSource)
        {
            _items.LockItemsForRead(true);

            if (_items.TryGetValue(itemId, out TaskInventoryItem it))
            {
                _items.LockItemsForRead(false);
                CreateScriptInstance(it, startParam, postOnRez, engine, stateSource);
            }
            else
            {
                _items.LockItemsForRead(false);
                string msg = string.Format("couldn't be found for prim {0}, {1} at {2} in {3}", _part.Name, _part.UUID,
                    _part.AbsolutePosition, _part.ParentGroup.Scene.RegionInfo.RegionName);
                StoreScriptError(itemId, msg);
                _log.ErrorFormat(
                    "[PRIM INVENTORY]: " +
                    "Couldn't start script with ID {0} since it {1}", itemId, msg);
            }
        }

        /// <summary>
        /// Start a script which is in this prim's inventory and return any compilation error messages.
        /// </summary>
        /// <param name="itemId">
        /// A <see cref="UUID"/>
        /// </param>
        public ArrayList CreateScriptInstanceEr(UUID itemId, int startParam, bool postOnRez, string engine, int stateSource)
        {
            ArrayList errors;

            // Indicate to CreateScriptInstanceInternal() we want it to
            // post any compilation/loading error messages
            lock (_scriptErrors)
            {
                _scriptErrors[itemId] = null;
            }

            // Perform compilation/loading
            CreateScriptInstanceInternal(itemId, startParam, postOnRez, engine, stateSource);

            // Wait for and retrieve any errors
            lock (_scriptErrors)
            {
                while ((errors = _scriptErrors[itemId]) == null)
                {
                    if (!System.Threading.Monitor.Wait(_scriptErrors, 15000))
                    {
                        _log.ErrorFormat(
                            "[PRIM INVENTORY]: " +
                            "timedout waiting for script {0} errors", itemId);
                        errors = _scriptErrors[itemId];
                        if (errors == null)
                        {
                            errors = new ArrayList(1);
                            errors.Add("timedout waiting for errors");
                        }
                        break;
                    }
                }
                _scriptErrors.Remove(itemId);
            }
            return errors;
        }

        // Signal to CreateScriptInstanceEr() that compilation/loading is complete
        private void StoreScriptErrors(UUID itemId, ArrayList errors)
        {
            lock (_scriptErrors)
            {
                // If compilation/loading initiated via CreateScriptInstance(),
                // it does not want the errors, so just get out
                if (!_scriptErrors.ContainsKey(itemId))
                {
                    return;
                }

                // Initiated via CreateScriptInstanceEr(), if we know what the
                // errors are, save them and wake CreateScriptInstanceEr().
                if (errors != null)
                {
                    _scriptErrors[itemId] = errors;
                    System.Threading.Monitor.PulseAll(_scriptErrors);
                    return;
                }
            }

            // Initiated via CreateScriptInstanceEr() but we don't know what
            // the errors are yet, so retrieve them from the script engine.
            // This may involve some waiting internal to GetScriptErrors().
            errors = GetScriptErrors(itemId);

            // Get a default non-null value to indicate success.
            if (errors == null)
            {
                errors = new ArrayList();
            }

            // Post to CreateScriptInstanceEr() and wake it up
            lock (_scriptErrors)
            {
                _scriptErrors[itemId] = errors;
                System.Threading.Monitor.PulseAll(_scriptErrors);
            }
        }

        // Like StoreScriptErrors(), but just posts a single string message
        private void StoreScriptError(UUID itemId, string message)
        {
            ArrayList errors = new ArrayList(1);
            errors.Add(message);
            StoreScriptErrors(itemId, errors);
        }

        /// <summary>
        /// Stop and remove a script which is in this prim's inventory.
        /// </summary>
        /// <param name="itemId"></param>
        /// <param name="sceneObjectBeingDeleted">
        /// Should be true if this script is being removed because the scene
        /// object is being deleted.  This will prevent spurious updates to the client.
        /// </param>
        public void RemoveScriptInstance(UUID itemId, bool sceneObjectBeingDeleted)
        {
            if (_items.ContainsKey(itemId))
            {
                if (!sceneObjectBeingDeleted)
                    _part.RemoveScriptEvents(itemId);

                _part.ParentGroup.Scene.EventManager.TriggerRemoveScript(_part.LocalId, itemId);
                _part.ParentGroup.AddActiveScriptCount(-1);
            }
            else
            {
                _log.WarnFormat(
                    "[PRIM INVENTORY]: " +
                    "Couldn't stop script with ID {0} since it couldn't be found for prim {1}, {2} at {3} in {4}",
                    itemId, _part.Name, _part.UUID,
                    _part.AbsolutePosition, _part.ParentGroup.Scene.RegionInfo.RegionName);
            }
        }

        /// <summary>
        /// Stop a script which is in this prim's inventory.
        /// </summary>
        /// <param name="itemId"></param>
        /// <param name="sceneObjectBeingDeleted">
        /// Should be true if this script is being removed because the scene
        /// object is being deleted.  This will prevent spurious updates to the client.
        /// </param>
        public void StopScriptInstance(UUID itemId)
        {
            _items.LockItemsForRead(true);
            _items.TryGetValue(itemId, out TaskInventoryItem scriptItem);
            _items.LockItemsForRead(false);

            if (scriptItem != null)
            {
                StopScriptInstance(scriptItem);
            }
            else
            {
                _log.WarnFormat(
                    "[PRIM INVENTORY]: " +
                    "Couldn't stop script with ID {0} since it couldn't be found for prim {1}, {2} at {3} in {4}",
                    itemId, _part.Name, _part.UUID,
                    _part.AbsolutePosition, _part.ParentGroup.Scene.RegionInfo.RegionName);
            }
        }

        /// <summary>
        /// Stop a script which is in this prim's inventory.
        /// </summary>
        /// <param name="itemId"></param>
        /// <param name="sceneObjectBeingDeleted">
        /// Should be true if this script is being removed because the scene
        /// object is being deleted.  This will prevent spurious updates to the client.
        /// </param>
        public void StopScriptInstance(TaskInventoryItem item)
        {
            if (_part.ParentGroup.Scene != null)
                _part.ParentGroup.Scene.EventManager.TriggerStopScript(_part.LocalId, item.ItemID);

            // At the moment, even stopped scripts are counted as active, which is probably wrong.
//            _part.ParentGroup.AddActiveScriptCount(-1);
        }

        public void SendReleaseScriptsControl()
        {
            _items.LockItemsForRead(true);
            if (_scripts == null || _scripts.Count == 0)
            {
                _items.LockItemsForRead(false);
                return;
            }

            List<UUID> grants = new List<UUID>();
            List<UUID> items = new List<UUID>();

            foreach (TaskInventoryItem item in _scripts.Values)
            {
                if (((item.PermsMask & 4) == 0))
                    continue;
                grants.Add(item.PermsGranter);
                items.Add(item.ItemID);
            }
            _items.LockItemsForRead(false);

            if (grants.Count > 0)
            {
                for (int i = 0; i < grants.Count; ++i)
                {
                    ScenePresence presence = _part.ParentGroup.Scene.GetScenePresence(grants[i]);
                    if (presence != null && !presence.IsDeleted && presence.ParentPart != _part) // last check mb needed for vehicle crossing ???
                        presence.UnRegisterControlEventsToScript(_part.LocalId, items[i]);
                }
            }
        }

        public void RemoveScriptsPermissions(int permissions)
        {
            _items.LockItemsForWrite(true);
            if (_scripts == null || _scripts.Count == 0)
            {
                _items.LockItemsForWrite(false);
                return;
            }

            bool removeControl = ((permissions & 4) != 0); //takecontrol
            List<UUID> grants = new List<UUID>();
            List<UUID> items = new List<UUID>();

            permissions = ~permissions;
            foreach (TaskInventoryItem item in _scripts.Values)
            {
                int curmask = item.PermsMask;
                UUID curGrant = item.PermsGranter;
                if (removeControl && ((curmask & 4) != 0))
                {
                    grants.Add(curGrant);
                    items.Add(item.ItemID);
                }
                curmask &= permissions;
                item.PermsMask = curmask;
                if(curmask == 0)
                    item.PermsGranter = UUID.Zero;
            }
            _items.LockItemsForWrite(false);

            if(grants.Count > 0)
            {
                for(int i = 0; i< grants.Count;++i)
                {
                    ScenePresence presence = _part.ParentGroup.Scene.GetScenePresence(grants[i]);
                    if (presence != null && !presence.IsDeleted)
                        presence.UnRegisterControlEventsToScript(_part.LocalId, items[i]);
                }
            }
        }

        public void RemoveScriptsPermissions(ScenePresence sp, int permissions)
        {
            _items.LockItemsForWrite(true);
            if (_scripts == null || _scripts.Count == 0)
            {
                _items.LockItemsForWrite(false);
                return;
            }

            bool removeControl = ((permissions & 4) != 0); //takecontrol
            UUID grant = sp.UUID;
            List<UUID> items = new List<UUID>();

            permissions = ~permissions;
            foreach (TaskInventoryItem item in _scripts.Values)
            {
                    if(grant != item.PermsGranter)
                        continue;
                    int curmask = item.PermsMask;
                    if (removeControl && ((curmask & 4) != 0))
                        items.Add(item.ItemID);
                    curmask &= permissions;
                    item.PermsMask = curmask;
                    if(curmask == 0)
                        item.PermsGranter = UUID.Zero;
            }
            _items.LockItemsForWrite(false);

            if(items.Count > 0)
            {
                for(int i = 0; i < items.Count; ++i)
                {
                    if (!sp.IsDeleted)
                        sp.UnRegisterControlEventsToScript(_part.LocalId, items[i]);
                }
            }
        }

        /// <summary>
        /// Check if the inventory holds an item with a given name.
        /// </summary>
        /// <param name="name"></param>
        /// <returns></returns>
        private bool InventoryContainsName(string name)
        {
            _items.LockItemsForRead(true);
            foreach (TaskInventoryItem item in _items.Values)
            {
                if (item.Name == name)
                {
                    _items.LockItemsForRead(false);
                    return true;
                }
            }
            _items.LockItemsForRead(false);
            return false;
        }

        /// <summary>
        /// For a given item name, return that name if it is available.  Otherwise, return the next available
        /// similar name (which is currently the original name with the next available numeric suffix).
        /// </summary>
        /// <param name="name"></param>
        /// <returns></returns>
        private string FindAvailableInventoryName(string name)
        {
            if (!InventoryContainsName(name))
                return name;

            int suffix=1;
            while (suffix < 256)
            {
                string tryName= string.Format("{0} {1}", name, suffix);
                if (!InventoryContainsName(tryName))
                    return tryName;
                suffix++;
            }
            return string.Empty;
        }

        /// <summary>
        /// Add an item to this prim's inventory.  If an item with the same name already exists, then an alternative
        /// name is chosen.
        /// </summary>
        /// <param name="item"></param>
        public void AddInventoryItem(TaskInventoryItem item, bool allowedDrop)
        {
            AddInventoryItem(item.Name, item, allowedDrop);
        }

        /// <summary>
        /// Add an item to this prim's inventory.  If an item with the same name already exists, it is replaced.
        /// </summary>
        /// <param name="item"></param>
        public void AddInventoryItemExclusive(TaskInventoryItem item, bool allowedDrop)
        {
            _items.LockItemsForRead(true);
            List<TaskInventoryItem> il = new List<TaskInventoryItem>(_items.Values);
            _items.LockItemsForRead(false);
            foreach (TaskInventoryItem i in il)
            {
                if (i.Name == item.Name)
                {
                    if (i.InvType == (int)InventoryType.LSL)
                        RemoveScriptInstance(i.ItemID, false);

                    RemoveInventoryItem(i.ItemID);
                    break;
                }
            }

            AddInventoryItem(item.Name, item, allowedDrop);
        }

        /// <summary>
        /// Add an item to this prim's inventory.
        /// </summary>
        /// <param name="name">The name that the new item should have.</param>
        /// <param name="item">
        /// The item itself.  The name within this structure is ignored in favour of the name
        /// given in this method's arguments
        /// </param>
        /// <param name="allowedDrop">
        /// Item was only added to inventory because AllowedDrop is set
        /// </param>
        protected void AddInventoryItem(string name, TaskInventoryItem item, bool allowedDrop)
        {
            name = FindAvailableInventoryName(name);
            if (string.IsNullOrEmpty(name))
                return;

            item.ParentID = _part.UUID;
            item.ParentPartID = _part.UUID;
            item.Name = name;
            item.GroupID = _part.GroupID;

            _items.LockItemsForWrite(true);

            _items.Add(item.ItemID, item);
            if (item.InvType == (int)InventoryType.LSL)
            {
                if (_scripts == null)
                    _scripts = new Dictionary<UUID, TaskInventoryItem>();
                _scripts.Add(item.ItemID, item);
            }

            _items.LockItemsForWrite(false);

            if (allowedDrop)
                _part.TriggerScriptChangedEvent(Changed.ALLOWED_DROP, item.ItemID);
            else
                _part.TriggerScriptChangedEvent(Changed.INVENTORY);

            _part.AggregateInnerPerms();
            _inventorySerial++;
            HasInventoryChanged = true;
            _part.ParentGroup.HasGroupChanged = true;
        }

        /// <summary>
        /// Restore a whole collection of items to the prim's inventory at once.
        /// We assume that the items already have all their fields correctly filled out.
        /// The items are not flagged for persistence to the database, since they are being restored
        /// from persistence rather than being newly added.
        /// </summary>
        /// <param name="items"></param>
        public void RestoreInventoryItems(ICollection<TaskInventoryItem> items)
        {
            _items.LockItemsForWrite(true);

            foreach (TaskInventoryItem item in items)
            {
                _items.Add(item.ItemID, item);
                if (item.InvType == (int)InventoryType.LSL)
                {
                    if (_scripts == null)
                        _scripts = new Dictionary<UUID, TaskInventoryItem>();
                    _scripts.Add(item.ItemID, item);
                }
            }
            _items.LockItemsForWrite(false);
            _part.AggregateInnerPerms();
            _inventorySerial++;
        }

        /// <summary>
        /// Returns an existing inventory item.  Returns the original, so any changes will be live.
        /// </summary>
        /// <param name="itemID"></param>
        /// <returns>null if the item does not exist</returns>
        public TaskInventoryItem GetInventoryItem(UUID itemId)
        {
            TaskInventoryItem item;
            _items.LockItemsForRead(true);
            _items.TryGetValue(itemId, out item);
            _items.LockItemsForRead(false);
            return item;
        }

        public TaskInventoryItem GetInventoryItem(string name)
        {
            _items.LockItemsForRead(true);
            foreach (TaskInventoryItem item in _items.Values)
            {
                if (item.Name == name)
                {
                    _items.LockItemsForRead(false);
                    return item;
                }
            }
            _items.LockItemsForRead(false);

            return null;
        }

        public List<TaskInventoryItem> GetInventoryItems(string name)
        {
            List<TaskInventoryItem> items = new List<TaskInventoryItem>();

            _items.LockItemsForRead(true);

            foreach (TaskInventoryItem item in _items.Values)
            {
                if (item.Name == name)
                    items.Add(item);
            }

            _items.LockItemsForRead(false);

            return items;
        }

        public bool GetRezReadySceneObjects(TaskInventoryItem item, out List<SceneObjectGroup> objlist, out List<Vector3> veclist, out Vector3 bbox, out float offsetHeight)
        {
            AssetBase rezAsset = _part.ParentGroup.Scene.AssetService.Get(item.AssetID.ToString());

            if (null == rezAsset)
            {
                _log.WarnFormat(
                    "[PRIM INVENTORY]: Could not find asset {0} for inventory item {1} in {2}",
                    item.AssetID, item.Name, _part.Name);
                objlist = null;
                veclist = null;
                bbox = Vector3.Zero;
                offsetHeight = 0;
                return false;
            }

            bool single = _part.ParentGroup.Scene.GetObjectsToRez(rezAsset.Data, false, out objlist, out veclist, out bbox, out offsetHeight);

            for (int i = 0; i < objlist.Count; i++)
            {
                SceneObjectGroup group = objlist[i];
/*
                group.RootPart.AttachPoint = group.RootPart.Shape.State;
                group.RootPart.AttachedPos = group.AbsolutePosition;
                group.RootPart.AttachRotation = group.GroupRotation;
*/
                group.ResetIDs();

                SceneObjectPart rootPart = group.GetPart(group.UUID);

                // Since renaming the item in the inventory does not affect the name stored
                // in the serialization, transfer the correct name from the inventory to the
                // object itself before we rez.
                // Only do these for the first object if we are rezzing a coalescence.
                // nahh dont mess with coalescence objects,
                // the name in inventory can be change for inventory purpuses only
                if (objlist.Count == 1)
                {
                    rootPart.Name = item.Name;
                    rootPart.Description = item.Description;
                }
/* reverted to old code till part.ApplyPermissionsOnRez is better reviewed/fixed
                group.SetGroup(_part.GroupID, null);

                foreach (SceneObjectPart part in group.Parts)
                {
                    // Convert between InventoryItem classes. You can never have too many similar but slightly different classes :)
                    InventoryItemBase dest = new InventoryItemBase(item.ItemID, item.OwnerID);
                    dest.BasePermissions = item.BasePermissions;
                    dest.CurrentPermissions = item.CurrentPermissions;
                    dest.EveryOnePermissions = item.EveryonePermissions;
                    dest.GroupPermissions = item.GroupPermissions;
                    dest.NextPermissions = item.NextPermissions;
                    dest.Flags = item.Flags;

                    part.ApplyPermissionsOnRez(dest, false, _part.ParentGroup.Scene);
                }
*/
// old code start
                SceneObjectPart[] partList = group.Parts;

                group.SetGroup(_part.GroupID, null);

                if ((rootPart.OwnerID != item.OwnerID) || (item.CurrentPermissions & (uint)PermissionMask.Slam) != 0 || (item.Flags & (uint)InventoryItemFlags.ObjectSlamPerm) != 0)
                {
                    if (_part.ParentGroup.Scene.Permissions.PropagatePermissions())
                    {
                        foreach (SceneObjectPart part in partList)
                        {
                            if ((item.Flags & (uint)InventoryItemFlags.ObjectOverwriteEveryone) != 0)
                                part.EveryoneMask = item.EveryonePermissions;
                            if ((item.Flags & (uint)InventoryItemFlags.ObjectOverwriteNextOwner) != 0)
                                part.NextOwnerMask = item.NextPermissions;
                            if ((item.Flags & (uint)InventoryItemFlags.ObjectOverwriteGroup) != 0)
                                part.GroupMask = item.GroupPermissions;
                        }

                        group.ApplyNextOwnerPermissions();
                    }
                }

                foreach (SceneObjectPart part in partList)
                {
                    if ((part.OwnerID != item.OwnerID) || (item.CurrentPermissions & (uint)PermissionMask.Slam) != 0 || (item.Flags & (uint)InventoryItemFlags.ObjectSlamPerm) != 0)
                    {
                        if(part.GroupID != part.OwnerID)
                            part.LastOwnerID = part.OwnerID;
                        part.OwnerID = item.OwnerID;
                        part.Inventory.ChangeInventoryOwner(item.OwnerID);
                    }

                    if ((item.Flags & (uint)InventoryItemFlags.ObjectOverwriteEveryone) != 0)
                        part.EveryoneMask = item.EveryonePermissions;
                    if ((item.Flags & (uint)InventoryItemFlags.ObjectOverwriteNextOwner) != 0)
                        part.NextOwnerMask = item.NextPermissions;
                    if ((item.Flags & (uint)InventoryItemFlags.ObjectOverwriteGroup) != 0)
                        part.GroupMask = item.GroupPermissions;
                }
// old code end
                rootPart.TrimPermissions();
                group.InvalidateDeepEffectivePerms();
            }

            return true;
        }

        /// <summary>
        /// Update an existing inventory item.
        /// </summary>
        /// <param name="item">The updated item.  An item with the same id must already exist
        /// in this prim's inventory.</param>
        /// <returns>false if the item did not exist, true if the update occurred successfully</returns>
        public bool UpdateInventoryItem(TaskInventoryItem item)
        {
            return UpdateInventoryItem(item, true, true);
        }

        public bool UpdateInventoryItem(TaskInventoryItem item, bool fireScriptEvents)
        {
            return UpdateInventoryItem(item, fireScriptEvents, true);
        }

        public bool UpdateInventoryItem(TaskInventoryItem item, bool fireScriptEvents, bool considerChanged)
        {
            _items.LockItemsForWrite(true);

            if (_items.ContainsKey(item.ItemID))
            {
                //_log.DebugFormat("[PRIM INVENTORY]: Updating item {0} in {1}", item.Name, _part.Name);

                item.ParentID = _part.UUID;
                item.ParentPartID = _part.UUID;

                // If group permissions have been set on, check that the groupID is up to date in case it has
                // changed since permissions were last set.
                if (item.GroupPermissions != (uint)PermissionMask.None)
                    item.GroupID = _part.GroupID;

                if(item.OwnerID == UUID.Zero) // viewer to internal enconding of group owned
                    item.OwnerID = item.GroupID; 

                if (item.AssetID == UUID.Zero)
                    item.AssetID = _items[item.ItemID].AssetID;

                _items[item.ItemID] = item;
                if(item.InvType == (int)InventoryType.LSL)
                {
                    if(_scripts == null)
                        _scripts = new Dictionary<UUID, TaskInventoryItem>();
                    _scripts[item.ItemID] = item;
                }

                _inventorySerial++;
                if (fireScriptEvents)
                    _part.TriggerScriptChangedEvent(Changed.INVENTORY);

                if (considerChanged)
                {
                    _part.ParentGroup.InvalidateDeepEffectivePerms();
                    HasInventoryChanged = true;
                    _part.ParentGroup.HasGroupChanged = true;
                }
                _items.LockItemsForWrite(false);

                return true;
            }
            else
            {
                _log.ErrorFormat(
                    "[PRIM INVENTORY]: " +
                    "Tried to retrieve item ID {0} from prim {1}, {2} at {3} in {4} but the item does not exist in this inventory",
                    item.ItemID, _part.Name, _part.UUID,
                    _part.AbsolutePosition, _part.ParentGroup.Scene.RegionInfo.RegionName);
            }
            _items.LockItemsForWrite(false);

            return false;
        }

        /// <summary>
        /// Remove an item from this prim's inventory
        /// </summary>
        /// <param name="itemID"></param>
        /// <returns>Numeric asset type of the item removed.  Returns -1 if the item did not exist
        /// in this prim's inventory.</returns>
        public int RemoveInventoryItem(UUID itemID)
        {
            _items.LockItemsForRead(true);

            if (_items.ContainsKey(itemID))
            {
                int type = _items[itemID].InvType;
                _items.LockItemsForRead(false);
                if (type == (int)InventoryType.LSL) // Script
                {
                    _part.ParentGroup.Scene.EventManager.TriggerRemoveScript(_part.LocalId, itemID);
                }
                _items.LockItemsForWrite(true);
                _items.Remove(itemID);
                if(_scripts != null)
                {
                    _scripts.Remove(itemID);
                    if(_scripts.Count == 0)
                        _scripts = null;
                }
                if (_scripts == null)
                {
                    _part.RemFlag(PrimFlags.Scripted);
                }
                _inventorySerial++;
                _items.LockItemsForWrite(false);

                _part.ParentGroup.InvalidateDeepEffectivePerms();


                HasInventoryChanged = true;
                _part.ParentGroup.HasGroupChanged = true;
                _part.ScheduleFullUpdate();

                _part.TriggerScriptChangedEvent(Changed.INVENTORY);
                return type;
            }
            else
            {
                _items.LockItemsForRead(false);
                _log.ErrorFormat(
                    "[PRIM INVENTORY]: " +
                    "Tried to remove item ID {0} from prim {1}, {2} but the item does not exist in this inventory",
                    itemID, _part.Name, _part.UUID);
            }

            return -1;
        }


        /// <summary>
        /// Serialize all the metadata for the items in this prim's inventory ready for sending to the client
        /// </summary>
        /// <param name="xferManager"></param>
        public void RequestInventoryFile(IClientAPI client, IXfer xferManager)
        {
            lock (_inventoryFileLock)
            {
                bool changed = false;

                _items.LockItemsForRead(true);

                if (_inventorySerial == 0) // No inventory
                {
                    _items.LockItemsForRead(false);
                    client.SendTaskInventory(_part.UUID, 0, new byte[0]);
                    return;
                }

                if (_items.Count == 0) // No inventory
                {
                    _items.LockItemsForRead(false);
                    client.SendTaskInventory(_part.UUID, 0, new byte[0]);
                    return;
                }

                if (_inventoryFileNameSerial != _inventorySerial)
                {
                    _inventoryFileNameSerial = _inventorySerial;
                    changed = true;
                }

                _items.LockItemsForRead(false);

                if (_inventoryFileData.Length < 2)
                    changed = true;

                bool includeAssets = false;
                if (_part.ParentGroup.Scene.Permissions.CanEditObjectInventory(_part.UUID, client.AgentId))
                    includeAssets = true;

                if (_inventoryPrivileged != includeAssets)
                    changed = true;

                if (!changed)
                {
                    xferManager.AddNewFile(_inventoryFileName, _inventoryFileData);
                    client.SendTaskInventory(_part.UUID, (short)_inventoryFileNameSerial,
                            _inventoryFileNameBytes);

                    return;
                }

                _inventoryPrivileged = includeAssets;

                InventoryStringBuilder invString = new InventoryStringBuilder(_part.UUID, UUID.Zero);

                _items.LockItemsForRead(true);

                foreach (TaskInventoryItem item in _items.Values)
                {
                    UUID ownerID = item.OwnerID;
                    UUID groupID = item.GroupID;
                    uint everyoneMask = item.EveryonePermissions;
                    uint baseMask = item.BasePermissions;
                    uint ownerMask = item.CurrentPermissions;
                    uint groupMask = item.GroupPermissions;

                    invString.AddItemStart();
                    invString.AddNameValueLine("ite_id", item.ItemID.ToString());
                    invString.AddNameValueLine("parent_id", _part.UUID.ToString());

                    invString.AddPermissionsStart();

                    invString.AddNameValueLine("base_mask", Utils.UIntToHexString(baseMask));
                    invString.AddNameValueLine("owner_mask", Utils.UIntToHexString(ownerMask));
                    invString.AddNameValueLine("group_mask", Utils.UIntToHexString(groupMask));
                    invString.AddNameValueLine("everyone_mask", Utils.UIntToHexString(everyoneMask));
                    invString.AddNameValueLine("next_owner_mask", Utils.UIntToHexString(item.NextPermissions));

                    invString.AddNameValueLine("creator_id", item.CreatorID.ToString());

                    invString.AddNameValueLine("last_owner_id", item.LastOwnerID.ToString());

                    invString.AddNameValueLine("group_id",groupID.ToString());
                    if(groupID != UUID.Zero && ownerID == groupID)
                    {
                        invString.AddNameValueLine("owner_id", UUID.Zero.ToString());
                        invString.AddNameValueLine("group_owned","1");
                    }
                    else
                    {
                        invString.AddNameValueLine("owner_id", ownerID.ToString());
                        invString.AddNameValueLine("group_owned","0");
                    }

                    invString.AddSectionEnd();

                    if (includeAssets)
                        invString.AddNameValueLine("asset_id", item.AssetID.ToString());
                    else
                        invString.AddNameValueLine("asset_id", UUID.Zero.ToString());
                    invString.AddNameValueLine("type", Utils.AssetTypeToString((AssetType)item.Type));
                    invString.AddNameValueLine("inv_type", Utils.InventoryTypeToString((InventoryType)item.InvType));
                    invString.AddNameValueLine("flags", Utils.UIntToHexString(item.Flags));

                    invString.AddSaleStart();
                    invString.AddNameValueLine("sale_type", "not");
                    invString.AddNameValueLine("sale_price", "0");
                    invString.AddSectionEnd();

                    invString.AddNameValueLine("name", item.Name + "|");
                    invString.AddNameValueLine("desc", item.Description + "|");

                    invString.AddNameValueLine("creation_date", item.CreationDate.ToString());
                    invString.AddSectionEnd();
                }

                _items.LockItemsForRead(false);

                _inventoryFileData = Utils.StringToBytes(invString.GetString());

                if (_inventoryFileData.Length > 2)
                {
                    _inventoryFileName = "inventory_" + UUID.Random().ToString() + ".tmp";
                    _inventoryFileNameBytes = Util.StringToBytes256(_inventoryFileName);
                    xferManager.AddNewFile(_inventoryFileName, _inventoryFileData);
                    client.SendTaskInventory(_part.UUID, (short)_inventoryFileNameSerial,_inventoryFileNameBytes);
                    return;
                }

                client.SendTaskInventory(_part.UUID, 0, new byte[0]);
            }
        }

        /// <summary>
        /// Process inventory backup
        /// </summary>
        /// <param name="datastore"></param>
        public void ProcessInventoryBackup(ISimulationDataService datastore)
        {
// Removed this because linking will cause an immediate delete of the new
// child prim from the database and the subsequent storing of the prim sees
// the inventory of it as unchanged and doesn't store it at all. The overhead
// of storing prim inventory needlessly is much less than the aggravation
// of prim inventory loss.
//            if (HasInventoryChanged)
//            {
                _items.LockItemsForRead(true);
                ICollection<TaskInventoryItem> itemsvalues = _items.Values;
                HasInventoryChanged = false;
                _items.LockItemsForRead(false);
                try
                {
                    datastore.StorePrimInventory(_part.UUID, itemsvalues);
                }
                catch {}
//            }
        }

        public class InventoryStringBuilder
        {
            private readonly StringBuilder BuildString = new StringBuilder(1024);

            public InventoryStringBuilder(UUID folderID, UUID parentID)
            {
                BuildString.Append("\tinv_object\t0\n\t{\n");
                AddNameValueLine("obj_id", folderID.ToString());
                AddNameValueLine("parent_id", parentID.ToString());
                AddNameValueLine("type", "category");
                AddNameValueLine("name", "Contents|\n\t}");
            }

            public void AddItemStart()
            {
                BuildString.Append("\tinv_item\t0\n\t{\n");
            }

            public void AddPermissionsStart()
            {
                BuildString.Append("\tpermissions 0\n\t{\n");
            }

            public void AddSaleStart()
            {
                BuildString.Append("\tsale_info\t0\n\t{\n");
            }

            protected void AddSectionStart()
            {
                BuildString.Append("\t{\n");
            }

            public void AddSectionEnd()
            {
                BuildString.Append("\t}\n");
            }

            public void AddLine(string addLine)
            {
                BuildString.Append(addLine);
            }

            public void AddNameValueLine(string name, string value)
            {
                BuildString.Append("\t\t");
                BuildString.Append(name);
                BuildString.Append("\t");
                BuildString.Append(value);
                BuildString.Append("\n");
            }

            public string GetString()
            {
                return BuildString.ToString();
            }

            public void Close()
            {
            }
        }

        public void AggregateInnerPerms(ref uint owner, ref uint group, ref uint everyone)
        {
            foreach (TaskInventoryItem item in _items.Values)
            {
                if(item.InvType == (sbyte)InventoryType.Landmark)
                    continue;
                owner &= item.CurrentPermissions;
                group &= item.GroupPermissions;
                everyone &= item.EveryonePermissions;
            }
        }

        public uint MaskEffectivePermissions()
        {
            // used to propagate permissions restrictions outwards
            // Modify does not propagate outwards. 
            uint mask=0x7fffffff;
            
            foreach (TaskInventoryItem item in _items.Values)
            {
                if(item.InvType == (sbyte)InventoryType.Landmark)
                    continue;

                // apply current to normal permission bits
                uint newperms = item.CurrentPermissions;

                if ((newperms & (uint)PermissionMask.Copy) == 0)
                    mask &= ~(uint)PermissionMask.Copy;
                if ((newperms & (uint)PermissionMask.Transfer) == 0)
                    mask &= ~(uint)PermissionMask.Transfer;
                if ((newperms & (uint)PermissionMask.Export) == 0)
                    mask &= ~((uint)PermissionMask.Export);
               
                // apply next owner restricted by current to folded bits 
                newperms &= item.NextPermissions;

                if ((newperms & (uint)PermissionMask.Copy) == 0)
                   mask &= ~((uint)PermissionMask.FoldedCopy);
                if ((newperms & (uint)PermissionMask.Transfer) == 0)
                    mask &= ~((uint)PermissionMask.FoldedTransfer);
                if ((newperms & (uint)PermissionMask.Export) == 0)
                    mask &= ~((uint)PermissionMask.FoldedExport);

            }
            return mask;
        }

        public void ApplyNextOwnerPermissions()
        {
            foreach (TaskInventoryItem item in _items.Values)
            {
                item.CurrentPermissions &= item.NextPermissions;
                item.BasePermissions &= item.NextPermissions;
                item.EveryonePermissions &= item.NextPermissions;
                item.OwnerChanged = true;
                item.PermsMask = 0;
                item.PermsGranter = UUID.Zero;
            }
        }

        public void ApplyGodPermissions(uint perms)
        {
            foreach (TaskInventoryItem item in _items.Values)
            {
                item.CurrentPermissions = perms;
                item.BasePermissions = perms;
            }

            _inventorySerial++;
            HasInventoryChanged = true;
        }

        /// <summary>
        /// Returns true if this part inventory contains any scripts.  False otherwise.
        /// </summary>
        /// <returns></returns>
        public bool ContainsScripts()
        {
            _items.LockItemsForRead(true);
            bool res = (_scripts != null && _scripts.Count >0);
            _items.LockItemsForRead(false);

            return res;
        }

        /// <summary>
        /// Returns the count of scripts in this parts inventory.
        /// </summary>
        /// <returns></returns>
        public int ScriptCount()
        {
            int count = 0;
            _items.LockItemsForRead(true);
            if(_scripts != null)
                count = _scripts.Count;
            _items.LockItemsForRead(false);
            return count;
        }
        /// <summary>
        /// Returns the count of running scripts in this parts inventory.
        /// </summary>
        /// <returns></returns>
        public int RunningScriptCount()
        {
            IScriptModule[] scriptEngines = _part.ParentGroup.Scene.RequestModuleInterfaces<IScriptModule>();
            if (scriptEngines.Length == 0)
                return 0;

            int count = 0;
            _items.LockItemsForRead(true);
            if (_scripts == null || _scripts.Count == 0)
            {
                _items.LockItemsForRead(false);
                return 0;
            }
            List<TaskInventoryItem> scripts = new List<TaskInventoryItem>(_scripts.Values);
            _items.LockItemsForRead(false);

            foreach (TaskInventoryItem item in scripts)
            {
                foreach (IScriptModule engine in scriptEngines)
                {
                    if (engine != null)
                    {
                        if (engine.HasScript(item.ItemID, out bool running))
                        {
                            if(running)
                                count++;
                            break;
                        }
                    }
                }
            }
            return count;
        }

        public List<UUID> GetInventoryList()
        {
            _items.LockItemsForRead(true);

            List<UUID> ret = new List<UUID>(_items.Count);
            foreach (TaskInventoryItem item in _items.Values)
                ret.Add(item.ItemID);

            _items.LockItemsForRead(false);
            return ret;
        }

        public List<TaskInventoryItem> GetInventoryItems()
        {
            _items.LockItemsForRead(true);
            List<TaskInventoryItem> ret = new List<TaskInventoryItem>(_items.Values);
            _items.LockItemsForRead(false);

            return ret;
        }

        public List<TaskInventoryItem> GetInventoryItems(InventoryType type)
        {
            _items.LockItemsForRead(true);

            List<TaskInventoryItem> ret = new List<TaskInventoryItem>(_items.Count);
            foreach (TaskInventoryItem item in _items.Values)
                if (item.InvType == (int)type)
                    ret.Add(item);

            _items.LockItemsForRead(false);
            return ret;
        }

        public Dictionary<UUID, string> GetScriptStates()
        {
            return GetScriptStates(false);
        }

        public Dictionary<UUID, string> GetScriptStates(bool oldIDs)
        {
            Dictionary<UUID, string> ret = new Dictionary<UUID, string>();

            if (_part.ParentGroup.Scene == null) // Group not in a scene
                return ret;

            IScriptModule[] scriptEngines = _part.ParentGroup.Scene.RequestModuleInterfaces<IScriptModule>();
            if (scriptEngines.Length == 0) // No engine at all
                return ret;

            _items.LockItemsForRead(true);
            if (_scripts == null || _scripts.Count == 0)
            {
                _items.LockItemsForRead(false);
                return ret;
            }
            List<TaskInventoryItem> scripts = new List<TaskInventoryItem>(_scripts.Values);
            _items.LockItemsForRead(false);

            foreach (TaskInventoryItem item in scripts)
            {
                foreach (IScriptModule e in scriptEngines)
                {
                    if (e != null)
                    {
                        //_log.DebugFormat(
                        //    "[PRIM INVENTORY]: Getting script state from engine {0} for {1} in part {2} in group {3} in {4}",
                        //    e.Name, item.Name, _part.Name, _part.ParentGroup.Name, _part.ParentGroup.Scene.Name);

                        string n = e.GetXMLState(item.ItemID);
                        if (!string.IsNullOrEmpty(n))
                        {
                            if (oldIDs)
                            {
                                if (!ret.ContainsKey(item.OldItemID))
                                    ret[item.OldItemID] = n;
                            }
                            else
                            {
                                if (!ret.ContainsKey(item.ItemID))
                                    ret[item.ItemID] = n;
                            }
                            break;
                        }
                    }
                }
            }
            return ret;
        }

        public void ResumeScripts()
        {
            IScriptModule[] scriptEngines = _part.ParentGroup.Scene.RequestModuleInterfaces<IScriptModule>();
            if (scriptEngines.Length == 0)
                return;

            _items.LockItemsForRead(true);
            if (_scripts == null || _scripts.Count == 0)
            {
                _items.LockItemsForRead(false);
                return;
            }
            List<TaskInventoryItem> scripts = new List<TaskInventoryItem>(_scripts.Values);
            _items.LockItemsForRead(false);

            foreach (TaskInventoryItem item in scripts)
            {
                foreach (IScriptModule engine in scriptEngines)
                {
                    if (engine != null)
                    {
                        //_log.DebugFormat(
                        //    "[PRIM INVENTORY]: Resuming script {0} {1} for {2}, OwnerChanged {3}",
                        //     item.Name, item.ItemID, item.OwnerID, item.OwnerChanged);

                        if(!engine.ResumeScript(item.ItemID))
                            continue;

                        if (item.OwnerChanged)
                            engine.PostScriptEvent(item.ItemID, "changed", new object[] { (int)Changed.OWNER });

                        item.OwnerChanged = false;
                    }
                }
            }
        }
    }
}
