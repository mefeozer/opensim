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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.RegularExpressions;

using Nini.Config;
using Mono.Addins;

using OpenMetaverse;

using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

// using log4net;
// using System.Reflection;


/*****************************************************
 *
 * WorldCommModule
 *
 *
 * Holding place for world comms - basically llListen
 * function implementation.
 *
 * lLListen(integer channel, string name, key id, string msg)
 * The name, id, and msg arguments specify the filtering
 * criteria. You can pass the empty string
 * (or NULL_KEY for id) for these to set a completely
 * open filter; this causes the listen() event handler to be
 * invoked for all chat on the channel. To listen only
 * for chat spoken by a specific object or avatar,
 * specify the name and/or id arguments. To listen
 * only for a specific command, specify the
 * (case-sensitive) msg argument. If msg is not empty,
 * listener will only hear strings which are exactly equal
 * to msg. You can also use all the arguments to establish
 * the most restrictive filtering criteria.
 *
 * It might be useful for each listener to maintain a message
 * digest, with a list of recent messages by UUID.  This can
 * be used to prevent in-world repeater loops.  However, the
 * linden functions do not have this capability, so for now
 * thats the way it works.
 * Instead it blocks messages originating from the same prim.
 * (not Object!)
 *
 * For LSL compliance, note the following:
 * (Tested again 1.21.1 on May 2, 2008)
 * 1. 'id' has to be parsed into a UUID. None-UUID keys are
 *    to be replaced by the ZeroID key. (Well, TryParse does
 *    that for us.
 * 2. Setting up an listen event from the same script, with the
 *    same filter settings (including step 1), returns the same
 *    handle as the original filter.
 * 3. (TODO) handles should be script-local. Starting from 1.
 *    Might be actually easier to map the global handle into
 *    script-local handle in the ScriptEngine. Not sure if its
 *    worth the effort tho.
 *
 * **************************************************/

namespace OpenSim.Region.CoreModules.Scripting.WorldComm
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "WorldCommModule")]
    public class WorldCommModule : IWorldComm, INonSharedRegionModule
    {
        // private static readonly ILog _log =
        //     LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private const int DEBUG_CHANNEL = 0x7fffffff;

        private ListenerManager _listenerManager;
        private ConcurrentQueue<ListenerInfo> _pending;
        private Scene _scene;
        private int _whisperdistance = 10;
        private int _saydistance = 20;
        private int _shoutdistance = 100;

        #region INonSharedRegionModule Members

        public void Initialise(IConfigSource config)
        {
            // wrap this in a try block so that defaults will work if
            // the config file doesn't specify otherwise.
            int maxlisteners = 1000;
            int maxhandles = 65;
            try
            {
                _whisperdistance = config.Configs["Chat"].GetInt(
                        "whisper_distance", _whisperdistance);
                _saydistance = config.Configs["Chat"].GetInt(
                        "say_distance", _saydistance);
                _shoutdistance = config.Configs["Chat"].GetInt(
                        "shout_distance", _shoutdistance);
                maxlisteners = config.Configs["LL-Functions"].GetInt(
                        "max_listens_per_region", maxlisteners);
                maxhandles = config.Configs["LL-Functions"].GetInt(
                        "max_listens_per_script", maxhandles);
            }
            catch (Exception)
            {
            }

            _whisperdistance *= _whisperdistance;
            _saydistance *= _saydistance;
            _shoutdistance *= _shoutdistance;

            if (maxlisteners < 1)
                maxlisteners = int.MaxValue;
            if (maxhandles < 1)
                maxhandles = int.MaxValue;

            if (maxlisteners < maxhandles)
                maxlisteners = maxhandles;

            _listenerManager = new ListenerManager(maxlisteners, maxhandles);
            _pending = new ConcurrentQueue<ListenerInfo>();
        }

        public void PostInitialise()
        {
        }

        public void AddRegion(Scene scene)
        {
            _scene = scene;
            _scene.RegisterModuleInterface<IWorldComm>(this);
            _scene.EventManager.OnChatFromClient += DeliverClientMessage;
            _scene.EventManager.OnChatBroadcast += DeliverClientMessage;
        }

        public void RegionLoaded(Scene scene) { }

        public void RemoveRegion(Scene scene)
        {
            if (scene != _scene)
                return;

            _scene.UnregisterModuleInterface<IWorldComm>(this);
            _scene.EventManager.OnChatBroadcast -= DeliverClientMessage;
            _scene.EventManager.OnChatBroadcast -= DeliverClientMessage;
        }

        public void Close()
        {
        }

        public string Name => "WorldCommModule";

        public Type ReplaceableInterface => null;

        #endregion

        #region IWorldComm Members

        public int ListenerCount => _listenerManager.ListenerCount;

        /// <summary>
        /// Create a listen event callback with the specified filters.
        /// The parameters localID,itemID are needed to uniquely identify
        /// the script during 'peek' time. Parameter hostID is needed to
        /// determine the position of the script.
        /// </summary>
        /// <param name="localID">localID of the script engine</param>
        /// <param name="itemID">UUID of the script engine</param>
        /// <param name="hostID">UUID of the SceneObjectPart</param>
        /// <param name="channel">channel to listen on</param>
        /// <param name="name">name to filter on</param>
        /// <param name="id">
        /// key to filter on (user given, could be totally faked)
        /// </param>
        /// <param name="msg">msg to filter on</param>
        /// <returns>number of the scripts handle</returns>
        public int Listen(uint localID, UUID itemID, UUID hostID, int channel,
                string name, UUID id, string msg)
        {
            return _listenerManager.AddListener(localID, itemID, hostID,
                channel, name, id, msg);
        }

        /// <summary>
        /// Create a listen event callback with the specified filters.
        /// The parameters localID,itemID are needed to uniquely identify
        /// the script during 'peek' time. Parameter hostID is needed to
        /// determine the position of the script.
        /// </summary>
        /// <param name="localID">localID of the script engine</param>
        /// <param name="itemID">UUID of the script engine</param>
        /// <param name="hostID">UUID of the SceneObjectPart</param>
        /// <param name="channel">channel to listen on</param>
        /// <param name="name">name to filter on</param>
        /// <param name="id">
        /// key to filter on (user given, could be totally faked)
        /// </param>
        /// <param name="msg">msg to filter on</param>
        /// <param name="regexBitfield">
        /// Bitfield indicating which strings should be processed as regex.
        /// </param>
        /// <returns>number of the scripts handle</returns>
        public int Listen(uint localID, UUID itemID, UUID hostID, int channel,
                string name, UUID id, string msg, int regexBitfield)
        {
            return _listenerManager.AddListener(localID, itemID, hostID,
                    channel, name, id, msg, regexBitfield);
        }

        /// <summary>
        /// Sets the listen event with handle as active (active = TRUE) or inactive (active = FALSE).
        /// The handle used is returned from Listen()
        /// </summary>
        /// <param name="itemID">UUID of the script engine</param>
        /// <param name="handle">handle returned by Listen()</param>
        /// <param name="active">temp. activate or deactivate the Listen()</param>
        public void ListenControl(UUID itemID, int handle, int active)
        {
            if (active == 1)
                _listenerManager.Activate(itemID, handle);
            else if (active == 0)
                _listenerManager.Dectivate(itemID, handle);
        }

        /// <summary>
        /// Removes the listen event callback with handle
        /// </summary>
        /// <param name="itemID">UUID of the script engine</param>
        /// <param name="handle">handle returned by Listen()</param>
        public void ListenRemove(UUID itemID, int handle)
        {
            _listenerManager.Remove(itemID, handle);
        }

        /// <summary>
        /// Removes all listen event callbacks for the given itemID
        /// (script engine)
        /// </summary>
        /// <param name="itemID">UUID of the script engine</param>
        public void DeleteListener(UUID itemID)
        {
            _listenerManager.DeleteListener(itemID);
        }


        protected static Vector3 CenterOfRegion = new Vector3(128, 128, 20);

        public void DeliverMessage(ChatTypeEnum type, int channel, string name, UUID id, string msg)
        {
            Vector3 position;
            SceneObjectPart source;
            ScenePresence avatar;

            if ((source = _scene.GetSceneObjectPart(id)) != null)
                position = source.AbsolutePosition;
            else if ((avatar = _scene.GetScenePresence(id)) != null)
                position = avatar.AbsolutePosition;
            else if (ChatTypeEnum.Region == type)
                position = CenterOfRegion;
            else
                return;

            DeliverMessage(type, channel, name, id, msg, position);
        }

        /// <summary>
        /// This method scans over the objects which registered an interest in listen callbacks.
        /// For everyone it finds, it checks if it fits the given filter. If it does,  then
        /// enqueue the message for delivery to the objects listen event handler.
        /// The enqueued ListenerInfo no longer has filter values, but the actually trigged values.
        /// Objects that do an llSay have their messages delivered here and for nearby avatars,
        /// the OnChatFromClient event is used.
        /// </summary>
        /// <param name="type">type of delvery (whisper,say,shout or regionwide)</param>
        /// <param name="channel">channel to sent on</param>
        /// <param name="name">name of sender (object or avatar)</param>
        /// <param name="id">key of sender (object or avatar)</param>
        /// <param name="msg">msg to sent</param>
        public void DeliverMessage(ChatTypeEnum type, int channel,
                string name, UUID id, string msg, Vector3 position)
        {
            // _log.DebugFormat("[WorldComm] got[2] type {0}, channel {1}, name {2}, id {3}, msg {4}",
            //                   type, channel, name, id, msg);

            // validate type and set range
            float maxDistanceSQ;
            switch (type)
            {
                case ChatTypeEnum.Whisper:
                    maxDistanceSQ = _whisperdistance;
                    break;

                case ChatTypeEnum.Say:
                    maxDistanceSQ = _saydistance;
                    break;

                case ChatTypeEnum.Shout:
                    maxDistanceSQ = _shoutdistance;
                    break;

                case ChatTypeEnum.Region:
                    maxDistanceSQ = -1f;
                    break;

                default:
                    return;
            }

            // Determine which listen event filters match the given set of arguments, this results
            // in a limited set of listeners, each belonging a host. If the host is in range, add them
            // to the pending queue.

            UUID hostID;
            foreach (ListenerInfo li in _listenerManager.GetListeners(UUID.Zero, channel, name, id, msg))
            {
                hostID = li.GetHostID();
                // Dont process if this message is from yourself!
                if (id == hostID)
                    continue;

                if(maxDistanceSQ < 0)
                {
                    QueueMessage(new ListenerInfo(li, name, id, msg));
                    continue;
                }

                SceneObjectPart sPart = _scene.GetSceneObjectPart(hostID);
                if (sPart == null)
                    continue;

                if(maxDistanceSQ > Vector3.DistanceSquared(sPart.AbsolutePosition, position))
                    QueueMessage(new ListenerInfo(li, name, id, msg));
            }
        }

        /// <summary>
        /// Delivers the message to a scene entity.
        /// </summary>
        /// <param name='target'>
        /// Target.
        /// </param>
        /// <param name='channel'>
        /// Channel.
        /// </param>
        /// <param name='name'>
        /// Name.
        /// </param>
        /// <param name='id'>
        /// Identifier.
        /// </param>
        /// <param name='msg'>
        /// Message.
        /// </param>
        public void DeliverMessageTo(UUID target, int channel, Vector3 pos, string name, UUID id, string msg)
        {
            if (channel == DEBUG_CHANNEL)
                return;

            if(target == UUID.Zero)
                return;

            // Is target an avatar?
            ScenePresence sp = _scene.GetScenePresence(target);
            if (sp != null)
            {
                 // Send message to avatar
                if (channel == 0)
                {
                   // Channel 0 goes to viewer ONLY
                    _scene.SimChat(Utils.StringToBytes(msg), ChatTypeEnum.Direct, 0, pos, name, id, target, false, false);
                    return;
                }

                // for now messages to prims don't cross regions
                if(sp.IsChildAgent)
                    return;

                List<SceneObjectGroup> attachments = sp.GetAttachments();

                if (attachments.Count == 0)
                    return;

                // Get uuid of attachments
                List<UUID> targets = new List<UUID>();
                foreach (SceneObjectGroup sog in attachments)
                {
                    if (!sog.IsDeleted)
                    {
                        SceneObjectPart[] parts = sog.Parts;
                        foreach(SceneObjectPart p in parts)
                            targets.Add(p.UUID);
                    }
                }

                foreach (ListenerInfo li in _listenerManager.GetListeners(UUID.Zero, channel, name, id, msg))
                {
                    UUID liHostID = li.GetHostID();
                    if (liHostID.Equals(id))
                        continue;
                    if (_scene.GetSceneObjectPart(liHostID) == null)
                        continue;

                    if (targets.Contains(liHostID))
                        QueueMessage(new ListenerInfo(li, name, id, msg));
                }

                return;
            }

            SceneObjectPart part = _scene.GetSceneObjectPart(target);
            if (part == null) // Not even an object
                return; // No error

            foreach (ListenerInfo li in _listenerManager.GetListeners(UUID.Zero, channel, name, id, msg))
            {
                UUID liHostID = li.GetHostID();
                // Dont process if this message is from yourself!
                if (liHostID.Equals(id))
                    continue;
                if (!liHostID.Equals(target))
                    continue;
                if (_scene.GetSceneObjectPart(liHostID) == null)
                    continue;

                QueueMessage(new ListenerInfo(li, name, id, msg));
            }
        }

        protected void QueueMessage(ListenerInfo li)
        {
            _pending.Enqueue(li);
        }

        /// <summary>
        /// Are there any listen events ready to be dispatched?
        /// </summary>
        /// <returns>boolean indication</returns>
        public bool HasMessages()
        {
            return _pending.Count > 0;
        }

        /// <summary>
        /// Pop the first availlable listen event from the queue
        /// </summary>
        /// <returns>ListenerInfo with filter filled in</returns>
        public IWorldCommListenerInfo GetNextMessage()
        {
            _pending.TryDequeue(out ListenerInfo li);
            return li;
        }

        #endregion

        /********************************************************************
         *
         * Listener Stuff
         *
         * *****************************************************************/

        private void DeliverClientMessage(object sender, OSChatMessage e)
        {
            if (null != e.Sender)
            {
                DeliverMessage(e.Type, e.Channel, e.Sender.Name,
                        e.Sender.AgentId, e.Message, e.Position);
            }
            else
            {
                DeliverMessage(e.Type, e.Channel, e.From, UUID.Zero,
                        e.Message, e.Position);
            }
        }

        public object[] GetSerializationData(UUID itemID)
        {
            return _listenerManager.GetSerializationData(itemID);
        }

        public void CreateFromData(uint localID, UUID itemID, UUID hostID,
                object[] data)
        {
            _listenerManager.AddFromData(localID, itemID, hostID, data);
        }
    }

    public class ListenerManager
    {
        private readonly object mainLock = new object();
        private readonly Dictionary<int, List<ListenerInfo>> _listenersByChannel = new Dictionary<int, List<ListenerInfo>>();
        private readonly int _maxlisteners;
        private readonly int _maxhandles;
        private int _curlisteners;

        /// <summary>
        /// Total number of listeners
        /// </summary>
        public int ListenerCount
        {
            get
            {
                lock (mainLock)
                    return _listenersByChannel.Count;
            }
        }

        public ListenerManager(int maxlisteners, int maxhandles)
        {
            _maxlisteners = maxlisteners;
            _maxhandles = maxhandles;
            _curlisteners = 0;
        }

        public int AddListener(uint localID, UUID itemID, UUID hostID,
                int channel, string name, UUID id, string msg)
        {
            return AddListener(localID, itemID, hostID, channel, name, id,
                    msg, 0);
        }

        public int AddListener(uint localID, UUID itemID, UUID hostID,
                int channel, string name, UUID id, string msg,
                int regexBitfield)
        {
            // do we already have a match on this particular filter event?
            List<ListenerInfo> coll = GetListeners(itemID, channel, name, id, msg);

            if (coll.Count > 0)
            {
                // special case, called with same filter settings, return same
                // handle (2008-05-02, tested on 1.21.1 server, still holds)
                return coll[0].GetHandle();
            }

            lock (mainLock)
            {
                if (_curlisteners < _maxlisteners)
                {
                    int newHandle = GetNewHandle(itemID);

                    if (newHandle > 0)
                    {
                        ListenerInfo li = new ListenerInfo(newHandle, localID,
                                itemID, hostID, channel, name, id, msg,
                                regexBitfield);

                        if (!_listenersByChannel.TryGetValue(channel, out List<ListenerInfo> listeners))
                        {
                            listeners = new List<ListenerInfo>();
                            _listenersByChannel.Add(channel, listeners);
                        }
                        listeners.Add(li);
                        _curlisteners++;

                        return newHandle;
                    }
                }
            }
            return -1;
        }

        public void Remove(UUID itemID, int handle)
        {
            lock (mainLock)
            {
                foreach (KeyValuePair<int, List<ListenerInfo>> lis in _listenersByChannel)
                {
                    foreach (ListenerInfo li in lis.Value)
                    {
                        if (handle == li.GetHandle() && itemID == li.GetItemID())
                        {
                            lis.Value.Remove(li);
                            _curlisteners--;
                            if (lis.Value.Count == 0)
                                _listenersByChannel.Remove(lis.Key); // bailing of loop so this does not smoke
                            // there should be only one, so we bail out early
                            return;
                        }
                    }
                }
            }
        }

        public void DeleteListener(UUID itemID)
        {
            List<int> emptyChannels = new List<int>();
            List<ListenerInfo> removedListeners = new List<ListenerInfo>();

            lock (mainLock)
            {
                foreach (KeyValuePair<int, List<ListenerInfo>> lis in _listenersByChannel)
                {
                    foreach (ListenerInfo li in lis.Value)
                    {
                        if (itemID == li.GetItemID())
                            removedListeners.Add(li);
                    }

                    foreach (ListenerInfo li in removedListeners)
                    {
                        lis.Value.Remove(li);
                        _curlisteners--;
                    }

                    removedListeners.Clear();
                    if (lis.Value.Count == 0)
                        emptyChannels.Add(lis.Key);
                }
                foreach (int channel in emptyChannels)
                {
                    _listenersByChannel.Remove(channel);
                }
            }
        }

        public void Activate(UUID itemID, int handle)
        {
            lock (mainLock)
            {
                foreach (KeyValuePair<int, List<ListenerInfo>> lis in _listenersByChannel)
                {
                    foreach (ListenerInfo li in lis.Value)
                    {
                        if (handle == li.GetHandle() && itemID == li.GetItemID())
                        {
                            li.Activate();
                            return;
                        }
                    }
                }
            }
        }

        public void Dectivate(UUID itemID, int handle)
        {
            lock (mainLock)
            {
                foreach (KeyValuePair<int, List<ListenerInfo>> lis in _listenersByChannel)
                {
                    foreach (ListenerInfo li in lis.Value)
                    {
                        if (handle == li.GetHandle() && itemID == li.GetItemID())
                        {
                            li.Deactivate();
                            return;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// non-locked access, since its always called in the context of the
        /// lock
        /// </summary>
        /// <param name="itemID"></param>
        /// <returns></returns>
        private int GetNewHandle(UUID itemID)
        {
            List<int> handles = new List<int>();

            // build a list of used keys for this specific itemID...
            foreach (KeyValuePair<int, List<ListenerInfo>> lis in _listenersByChannel)
            {
                foreach (ListenerInfo li in lis.Value)
                {
                    if (itemID == li.GetItemID())
                        handles.Add(li.GetHandle());
                }
            }

            if(handles.Count >= _maxhandles)
                return -1;

            // Note: 0 is NOT a valid handle for llListen() to return
            for (int i = 1; i <= _maxhandles; i++)
            {
                if (!handles.Contains(i))
                    return i;
            }

            return -1;
        }

        /// These are duplicated from ScriptBaseClass
        /// http://opensimulator.org/mantis/view.php?id=6106#c21945
        #region Constants for the bitfield parameter of osListenRegex

        /// <summary>
        /// process name parameter as regex
        /// </summary>
        public const int OS_LISTEN_REGEX_NAME = 0x1;

        /// <summary>
        /// process message parameter as regex
        /// </summary>
        public const int OS_LISTEN_REGEX_MESSAGE = 0x2;

        #endregion

        /// <summary>
        /// Get listeners matching the input parameters.
        /// </summary>
        /// <remarks>
        /// Theres probably a more clever and efficient way to do this, maybe
        /// with regex.
        /// </remarks>
        /// <param name="itemID"></param>
        /// <param name="channel"></param>
        /// <param name="name"></param>
        /// <param name="id"></param>
        /// <param name="msg"></param>
        /// <returns></returns>
        public List<ListenerInfo> GetListeners(UUID itemID, int channel,
                string name, UUID id, string msg)
        {
            List<ListenerInfo> collection = new List<ListenerInfo>();

            lock (mainLock)
            {
                List<ListenerInfo> listeners;
                if (!_listenersByChannel.TryGetValue(channel, out listeners))
                {
                    return collection;
                }

                bool itemIDNotZero = itemID != UUID.Zero;
                foreach (ListenerInfo li in listeners)
                {
                    if (!li.IsActive())
                        continue;

                    if (itemIDNotZero && itemID != li.GetItemID())
                        continue;

                    if (li.GetID() != UUID.Zero && id != li.GetID())
                        continue;

                    if (li.GetName().Length > 0)
                    {
                        if((li.RegexBitfield & OS_LISTEN_REGEX_NAME) == OS_LISTEN_REGEX_NAME)
                        {
                            if (!Regex.IsMatch(name, li.GetName()))
                                continue;
                        }
                        else
                        {
                            if (!li.GetName().Equals(name))
                                continue;
                        }
                    }

                    if (li.GetMessage().Length > 0)
                    {
                        if((li.RegexBitfield & OS_LISTEN_REGEX_MESSAGE) == OS_LISTEN_REGEX_MESSAGE)
                        {
                            if(!Regex.IsMatch(msg, li.GetMessage()))
                                continue;
                        }
                        else
                        {
                            if(!li.GetMessage().Equals(msg))
                                continue;
                        }
                    }
                    collection.Add(li);
                }
            }
            return collection;
        }

        public object[] GetSerializationData(UUID itemID)
        {
            List<object> data = new List<object>();

            lock (mainLock)
            {
                foreach (List<ListenerInfo> list in _listenersByChannel.Values)
                {
                    foreach (ListenerInfo l in list)
                    {
                        if (l.GetItemID() == itemID)
                            data.AddRange(l.GetSerializationData());
                    }
                }
            }
            return (object[])data.ToArray();
        }

        public void AddFromData(uint localID, UUID itemID, UUID hostID,
                object[] data)
        {
            int idx = 0;
            object[] item = new object[6];
            int dataItemLength = 6;

            while (idx < data.Length)
            {
                dataItemLength = idx + 7 == data.Length || idx + 7 < data.Length && data[idx + 7] is bool ? 7 : 6;
                item = new object[dataItemLength];
                Array.Copy(data, idx, item, 0, dataItemLength);

                ListenerInfo info =
                        ListenerInfo.FromData(localID, itemID, hostID, item);

                lock (mainLock)
                {
                    if (!_listenersByChannel.ContainsKey((int)item[2]))
                    {
                        _listenersByChannel.Add((int)item[2], new List<ListenerInfo>());
                    }
                    _listenersByChannel[(int)item[2]].Add(info);
                }

                idx += dataItemLength;
            }
        }
    }

    public class ListenerInfo : IWorldCommListenerInfo
    {
        /// <summary>
        /// Listener is active or not
        /// </summary>
        private bool _active;

        /// <summary>
        /// Assigned handle of this listener
        /// </summary>
        private int _handle;

        /// <summary>
        /// Local ID from script engine
        /// </summary>
        private uint _localID;

        /// <summary>
        /// ID of the host script engine
        /// </summary>
        private UUID _itemID;

        /// <summary>
        /// ID of the host/scene part
        /// </summary>
        private UUID _hostID;

        /// <summary>
        /// Channel
        /// </summary>
        private int _channel;

        /// <summary>
        /// ID to filter messages from
        /// </summary>
        private UUID _id;

        /// <summary>
        /// Object name to filter messages from
        /// </summary>
        private string _name;

        /// <summary>
        /// The message
        /// </summary>
        private string _message;

        public ListenerInfo(int handle, uint localID, UUID ItemID,
                UUID hostID, int channel, string name, UUID id,
                string message)
        {
            Initialise(handle, localID, ItemID, hostID, channel, name, id,
                    message, 0);
        }

        public ListenerInfo(int handle, uint localID, UUID ItemID,
                UUID hostID, int channel, string name, UUID id,
                string message, int regexBitfield)
        {
            Initialise(handle, localID, ItemID, hostID, channel, name, id,
                    message, regexBitfield);
        }

        public ListenerInfo(ListenerInfo li, string name, UUID id,
                string message)
        {
            Initialise(li._handle, li._localID, li._itemID, li._hostID,
                    li._channel, name, id, message, 0);
        }

        public ListenerInfo(ListenerInfo li, string name, UUID id,
                string message, int regexBitfield)
        {
            Initialise(li._handle, li._localID, li._itemID, li._hostID,
                    li._channel, name, id, message, regexBitfield);
        }

        private void Initialise(int handle, uint localID, UUID ItemID,
                UUID hostID, int channel, string name, UUID id,
                string message, int regexBitfield)
        {
            _active = true;
            _handle = handle;
            _localID = localID;
            _itemID = ItemID;
            _hostID = hostID;
            _channel = channel;
            _name = name;
            _id = id;
            _message = message;
            RegexBitfield = regexBitfield;
        }

        public object[] GetSerializationData()
        {
            object[] data = new object[7];

            data[0] = _active;
            data[1] = _handle;
            data[2] = _channel;
            data[3] = _name;
            data[4] = _id;
            data[5] = _message;
            data[6] = RegexBitfield;

            return data;
        }

        public static ListenerInfo FromData(uint localID, UUID ItemID,
                UUID hostID, object[] data)
        {
            ListenerInfo linfo = new ListenerInfo((int)data[1], localID,
                    ItemID, hostID, (int)data[2], (string)data[3],
                    (UUID)data[4], (string)data[5])
            {
                _active = (bool)data[0]
            };
            if (data.Length >= 7)
            {
                linfo.RegexBitfield = (int)data[6];
            }

            return linfo;
        }

        public UUID GetItemID()
        {
            return _itemID;
        }

        public UUID GetHostID()
        {
            return _hostID;
        }

        public int GetChannel()
        {
            return _channel;
        }

        public uint GetLocalID()
        {
            return _localID;
        }

        public int GetHandle()
        {
            return _handle;
        }

        public string GetMessage()
        {
            return _message;
        }

        public string GetName()
        {
            return _name;
        }

        public bool IsActive()
        {
            return _active;
        }

        public void Deactivate()
        {
            _active = false;
        }

        public void Activate()
        {
            _active = true;
        }

        public UUID GetID()
        {
            return _id;
        }

        public int RegexBitfield { get; private set; }
    }
}
