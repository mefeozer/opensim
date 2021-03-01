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
using System.Net;
using System.Reflection;
using System.Threading;
using log4net;
using Nini.Config;
using Nwc.XmlRpc;
using OpenMetaverse;
using OpenSim.Framework.Monitoring;
using OpenSim.Framework.Servers;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using Mono.Addins;

/*****************************************************
 *
 * XMLRPCModule
 *
 * Module for accepting incoming communications from
 * external XMLRPC client and calling a remote data
 * procedure for a registered data channel/prim.
 *
 *
 * 1. On module load, open a listener port
 * 2. Attach an XMLRPC handler
 * 3. When a request is received:
 * 3.1 Parse into components: channel key, int, string
 * 3.2 Look up registered channel listeners
 * 3.3 Call the channel (prim) remote data method
 * 3.4 Capture the response (llRemoteDataReply)
 * 3.5 Return response to client caller
 * 3.6 If no response from llRemoteDataReply within
 *     RemoteReplyScriptTimeout, generate script timeout fault
 *
 * Prims in script must:
 * 1. Open a remote data channel
 * 1.1 Generate a channel ID
 * 1.2 Register primid,channelid pair with module
 * 2. Implement the remote data procedure handler
 *
 * llOpenRemoteDataChannel
 * llRemoteDataReply
 * remote_data(integer type, key channel, key messageid, string sender, integer ival, string sval)
 * llCloseRemoteDataChannel
 *
 * **************************************************/

namespace OpenSim.Region.CoreModules.Scripting.XMLRPC
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "XMLRPCModule")]
    public class XMLRPCModule : ISharedRegionModule, IXMLRPC
    {
        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private readonly string _name = "XMLRPCModule";

        // <channel id, RPCChannelInfo>
        private Dictionary<UUID, RPCChannelInfo> _openChannels;
        private Dictionary<UUID, SendRemoteDataRequest> _pendingSRDResponses;
        private int _remoteDataPort = 0;
        public int Port => _remoteDataPort;

        private Dictionary<UUID, RPCRequestInfo> _rpcPending;
        private Dictionary<UUID, RPCRequestInfo> _rpcPendingResponses;
        private readonly List<Scene> _scenes = new List<Scene>();
        private readonly int RemoteReplyScriptTimeout = 9000;
        private readonly int RemoteReplyScriptWait = 300;
        private readonly object XMLRPCListLock = new object();

        #region ISharedRegionModule Members

        public void Initialise(IConfigSource config)
        {
            // We need to create these early because the scripts might be calling
            // But since this gets called for every region, we need to make sure they
            // get called only one time (or we lose any open channels)
            _openChannels = new Dictionary<UUID, RPCChannelInfo>();
            _rpcPending = new Dictionary<UUID, RPCRequestInfo>();
            _rpcPendingResponses = new Dictionary<UUID, RPCRequestInfo>();
            _pendingSRDResponses = new Dictionary<UUID, SendRemoteDataRequest>();
            if (config.Configs["XMLRPC"] != null)
            {
                try
                {
                    _remoteDataPort = config.Configs["XMLRPC"].GetInt("XmlRpcPort", _remoteDataPort);
                }
                catch (Exception)
                {
                }
            }
        }

        public void PostInitialise()
        {
            if (IsEnabled())
            {
                // Start http server
                // Attach xmlrpc handlers
                //                _log.InfoFormat(
                //                    "[XML RPC MODULE]: Starting up XMLRPC Server on port {0} for llRemoteData commands.",
                //                    _remoteDataPort);

                IHttpServer httpServer = MainServer.GetHttpServer((uint)_remoteDataPort);
                httpServer.AddXmlRPCHandler("llRemoteData", XmlRpcRemoteData);
            }
        }

        public void AddRegion(Scene scene)
        {
            if (!IsEnabled())
                return;

            if (!_scenes.Contains(scene))
            {
                _scenes.Add(scene);

                scene.RegisterModuleInterface<IXMLRPC>(this);
            }
        }

        public void RegionLoaded(Scene scene)
        {
        }

        public void RemoveRegion(Scene scene)
        {
            if (!IsEnabled())
                return;

            if (_scenes.Contains(scene))
            {
                scene.UnregisterModuleInterface<IXMLRPC>(this);
                _scenes.Remove(scene);
            }
        }

        public void Close()
        {
        }

        public string Name => _name;

        public Type ReplaceableInterface => null;

        #endregion

        #region IXMLRPC Members

        public bool IsEnabled()
        {
            return _remoteDataPort > 0;
        }

        /**********************************************
         * OpenXMLRPCChannel
         *
         * Generate a UUID channel key and add it and
         * the prim id to dictionary <channelUUID, primUUID>
         *
         * A custom channel key can be proposed.
         * Otherwise, passing UUID.Zero will generate
         * and return a random channel
         *
         * First check if there is a channel assigned for
         * this itemID.  If there is, then someone called
         * llOpenRemoteDataChannel twice.  Just return the
         * original channel.  Other option is to delete the
         * current channel and assign a new one.
         *
         * ********************************************/

        public UUID OpenXMLRPCChannel(uint localID, UUID itemID, UUID channelID)
        {
            UUID newChannel = UUID.Zero;

            // This should no longer happen, but the check is reasonable anyway
            if (null == _openChannels)
            {
                _log.Warn("[XML RPC MODULE]: Attempt to open channel before initialization is complete");
                return newChannel;
            }

            //Is a dupe?
            foreach (RPCChannelInfo ci in _openChannels.Values)
            {
                if (ci.GetItemID().Equals(itemID))
                {
                    // return the original channel ID for this item
                    newChannel = ci.GetChannelID();
                    break;
                }
            }

            if (newChannel == UUID.Zero)
            {
                newChannel = channelID == UUID.Zero ? UUID.Random() : channelID;
                RPCChannelInfo rpcChanInfo = new RPCChannelInfo(localID, itemID, newChannel);
                lock (XMLRPCListLock)
                {
                    _openChannels.Add(newChannel, rpcChanInfo);
                }
            }

            return newChannel;
        }

        // Delete channels based on itemID
        // for when a script is deleted
        public void DeleteChannels(UUID itemID)
        {
            if (_openChannels != null)
            {
                ArrayList tmp = new ArrayList();

                lock (XMLRPCListLock)
                {
                    foreach (RPCChannelInfo li in _openChannels.Values)
                    {
                        if (li.GetItemID().Equals(itemID))
                        {
                            tmp.Add(itemID);
                        }
                    }

                    IEnumerator tmpEnumerator = tmp.GetEnumerator();
                    while (tmpEnumerator.MoveNext())
                        _openChannels.Remove((UUID) tmpEnumerator.Current);
                }
            }
        }

        /**********************************************
         * Remote Data Reply
         *
         * Response to RPC message
         *
         *********************************************/

        public void RemoteDataReply(string channel, string message_id, string sdata, int idata)
        {
            UUID message_key = new UUID(message_id);
            UUID channel_key = new UUID(channel);

            RPCRequestInfo rpcInfo = null;

            if (message_key == UUID.Zero)
            {
                foreach (RPCRequestInfo oneRpcInfo in _rpcPendingResponses.Values)
                    if (oneRpcInfo.GetChannelKey() == channel_key)
                        rpcInfo = oneRpcInfo;
            }
            else
            {
                _rpcPendingResponses.TryGetValue(message_key, out rpcInfo);
            }

            if (rpcInfo != null)
            {
                rpcInfo.SetStrRetval(sdata);
                rpcInfo.SetIntRetval(idata);
                rpcInfo.SetProcessed(true);
                _rpcPendingResponses.Remove(message_key);
            }
            else
            {
                _log.Warn("[XML RPC MODULE]: Channel or message_id not found");
            }
        }

        /**********************************************
         * CloseXMLRPCChannel
         *
         * Remove channel from dictionary
         *
         *********************************************/

        public void CloseXMLRPCChannel(UUID channelKey)
        {
            if (_openChannels.ContainsKey(channelKey))
                _openChannels.Remove(channelKey);
        }


        public bool hasRequests()
        {
            lock (XMLRPCListLock)
            {
                if (_rpcPending != null)
                    return _rpcPending.Count > 0;
                else
                    return false;
            }
        }

        public IXmlRpcRequestInfo GetNextCompletedRequest()
        {
            if (_rpcPending != null)
            {
                lock (XMLRPCListLock)
                {
                    foreach (UUID luid in _rpcPending.Keys)
                    {
                        RPCRequestInfo tmpReq;

                        if (_rpcPending.TryGetValue(luid, out tmpReq))
                        {
                            if (!tmpReq.IsProcessed()) return tmpReq;
                        }
                    }
                }
            }
            return null;
        }

        public void RemoveCompletedRequest(UUID id)
        {
            lock (XMLRPCListLock)
            {
                RPCRequestInfo tmp;
                if (_rpcPending.TryGetValue(id, out tmp))
                {
                    _rpcPending.Remove(id);
                    _rpcPendingResponses.Add(id, tmp);
                }
                else
                {
                    _log.Error("[XML RPC MODULE]: UNABLE TO REMOVE COMPLETED REQUEST");
                }
            }
        }

        public UUID SendRemoteData(uint localID, UUID itemID, string channel, string dest, int idata, string sdata)
        {
            SendRemoteDataRequest req = new SendRemoteDataRequest(
                localID, itemID, channel, dest, idata, sdata
                );
            _pendingSRDResponses.Add(req.GetReqID(), req);
            req.Process();
            return req.ReqID;
        }

        public IServiceRequest GetNextCompletedSRDRequest()
        {
            if (_pendingSRDResponses != null)
            {
                lock (XMLRPCListLock)
                {
                    foreach (UUID luid in _pendingSRDResponses.Keys)
                    {
                        SendRemoteDataRequest tmpReq;

                        if (_pendingSRDResponses.TryGetValue(luid, out tmpReq))
                        {
                            if (tmpReq.Finished)
                                return tmpReq;
                        }
                    }
                }
            }
            return null;
        }

        public void RemoveCompletedSRDRequest(UUID id)
        {
            lock (XMLRPCListLock)
            {
                SendRemoteDataRequest tmpReq;
                if (_pendingSRDResponses.TryGetValue(id, out tmpReq))
                {
                    _pendingSRDResponses.Remove(id);
                }
            }
        }

        public void CancelSRDRequests(UUID itemID)
        {
            if (_pendingSRDResponses != null)
            {
                lock (XMLRPCListLock)
                {
                    foreach (SendRemoteDataRequest li in _pendingSRDResponses.Values)
                    {
                        if (li.ItemID.Equals(itemID))
                            _pendingSRDResponses.Remove(li.GetReqID());
                    }
                }
            }
        }

        #endregion

        public XmlRpcResponse XmlRpcRemoteData(XmlRpcRequest request, IPEndPoint remoteClient)
        {
            XmlRpcResponse response = new XmlRpcResponse();

            Hashtable requestData = (Hashtable) request.Params[0];
            bool GoodXML = requestData.Contains("Channel") && requestData.Contains("IntValue") &&
                           requestData.Contains("StringValue");

            if (GoodXML)
            {
                UUID channel = new UUID((string) requestData["Channel"]);
                RPCChannelInfo rpcChanInfo;
                if (_openChannels.TryGetValue(channel, out rpcChanInfo))
                {
                    string intVal = Convert.ToInt32(requestData["IntValue"]).ToString();
                    string strVal = (string) requestData["StringValue"];

                    RPCRequestInfo rpcInfo;

                    lock (XMLRPCListLock)
                    {
                        rpcInfo =
                            new RPCRequestInfo(rpcChanInfo.GetLocalID(), rpcChanInfo.GetItemID(), channel, strVal,
                                               intVal);
                        _rpcPending.Add(rpcInfo.GetMessageID(), rpcInfo);
                    }

                    int timeoutCtr = 0;

                    while (!rpcInfo.IsProcessed() && timeoutCtr < RemoteReplyScriptTimeout)
                    {
                        Thread.Sleep(RemoteReplyScriptWait);
                        timeoutCtr += RemoteReplyScriptWait;
                    }
                    if (rpcInfo.IsProcessed())
                    {
                        Hashtable param = new Hashtable();
                        param["StringValue"] = rpcInfo.GetStrRetval();
                        param["IntValue"] = rpcInfo.GetIntRetval();

                        ArrayList parameters = new ArrayList();
                        parameters.Add(param);

                        response.Value = parameters;
                        rpcInfo = null;
                    }
                    else
                    {
                        response.SetFault(-1, "Script timeout");
                        rpcInfo = null;
                    }
                }
                else
                {
                    response.SetFault(-1, "Invalid channel");
                }
            }

            return response;
        }
    }

    public class RPCRequestInfo: IXmlRpcRequestInfo
    {
        private readonly UUID _ChannelKey;
        private readonly string _IntVal;
        private readonly UUID _ItemID;
        private readonly uint _localID;
        private readonly UUID _MessageID;
        private bool _processed;
        private int _respInt;
        private string _respStr;
        private readonly string _StrVal;

        public RPCRequestInfo(uint localID, UUID itemID, UUID channelKey, string strVal, string intVal)
        {
            _localID = localID;
            _StrVal = strVal;
            _IntVal = intVal;
            _ItemID = itemID;
            _ChannelKey = channelKey;
            _MessageID = UUID.Random();
            _processed = false;
            _respStr = string.Empty;
            _respInt = 0;
        }

        public bool IsProcessed()
        {
            return _processed;
        }

        public UUID GetChannelKey()
        {
            return _ChannelKey;
        }

        public void SetProcessed(bool processed)
        {
            _processed = processed;
        }

        public void SetStrRetval(string resp)
        {
            _respStr = resp;
        }

        public string GetStrRetval()
        {
            return _respStr;
        }

        public void SetIntRetval(int resp)
        {
            _respInt = resp;
        }

        public int GetIntRetval()
        {
            return _respInt;
        }

        public uint GetLocalID()
        {
            return _localID;
        }

        public UUID GetItemID()
        {
            return _ItemID;
        }

        public string GetStrVal()
        {
            return _StrVal;
        }

        public int GetIntValue()
        {
            return int.Parse(_IntVal);
        }

        public UUID GetMessageID()
        {
            return _MessageID;
        }
    }

    public class RPCChannelInfo
    {
        private readonly UUID _ChannelKey;
        private readonly UUID _itemID;
        private readonly uint _localID;

        public RPCChannelInfo(uint localID, UUID itemID, UUID channelID)
        {
            _ChannelKey = channelID;
            _localID = localID;
            _itemID = itemID;
        }

        public UUID GetItemID()
        {
            return _itemID;
        }

        public UUID GetChannelID()
        {
            return _ChannelKey;
        }

        public uint GetLocalID()
        {
            return _localID;
        }
    }

    public class SendRemoteDataRequest: IServiceRequest
    {
        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public string Channel;
        public string DestURL;
        private bool _finished;
        public bool Finished
        {
            get => _finished;
            set => _finished = value;
        }
        private Thread httpThread;
        public int Idata;
        private UUID _itemID;
        public UUID ItemID
        {
            get => _itemID;
            set => _itemID = value;
        }
        private uint _localID;
        public uint LocalID
        {
            get => _localID;
            set => _localID = value;
        }
        private UUID _reqID;
        public UUID ReqID
        {
            get => _reqID;
            set => _reqID = value;
        }
        public XmlRpcRequest Request;
        public int ResponseIdata;
        public string ResponseSdata;
        public string Sdata;

        public SendRemoteDataRequest(uint localID, UUID itemID, string channel, string dest, int idata, string sdata)
        {
            this.Channel = channel;
            DestURL = dest;
            this.Idata = idata;
            this.Sdata = sdata;
            ItemID = itemID;
            LocalID = localID;

            ReqID = UUID.Random();
        }

        public void Process()
        {
            _finished = false;
            httpThread = WorkManager.StartThread(SendRequest, "XMLRPCreqThread", ThreadPriority.Normal, true, false, null, int.MaxValue);
        }

        /*
         * TODO: More work on the response codes.  Right now
         * returning 200 for success or 499 for exception
         */

        public void SendRequest()
        {
            Hashtable param = new Hashtable();

            // Check if channel is an UUID
            // if not, use as method name
            UUID parseUID;
            string mName = "llRemoteData";
            if (!string.IsNullOrEmpty(Channel))
                if (!UUID.TryParse(Channel, out parseUID))
                    mName = Channel;
                else
                    param["Channel"] = Channel;

            param["StringValue"] = Sdata;
            param["IntValue"] = Convert.ToString(Idata);

            ArrayList parameters = new ArrayList();
            parameters.Add(param);
            XmlRpcRequest req = new XmlRpcRequest(mName, parameters);
            try
            {
                XmlRpcResponse resp = req.Send(DestURL, 30000);
                if (resp != null)
                {
                    Hashtable respParms;
                    if (resp.Value.GetType().Equals(typeof(Hashtable)))
                    {
                        respParms = (Hashtable) resp.Value;
                    }
                    else
                    {
                        ArrayList respData = (ArrayList) resp.Value;
                        respParms = (Hashtable) respData[0];
                    }
                    if (respParms != null)
                    {
                        if (respParms.Contains("StringValue"))
                        {
                            Sdata = (string) respParms["StringValue"];
                        }
                        if (respParms.Contains("IntValue"))
                        {
                            Idata = Convert.ToInt32(respParms["IntValue"]);
                        }
                        if (respParms.Contains("faultString"))
                        {
                            Sdata = (string) respParms["faultString"];
                        }
                        if (respParms.Contains("faultCode"))
                        {
                            Idata = Convert.ToInt32(respParms["faultCode"]);
                        }
                    }
                }
            }
            catch (Exception we)
            {
                Sdata = we.Message;
                _log.Warn("[SendRemoteDataRequest]: Request failed");
                _log.Warn(we.StackTrace);
            }
            finally
            {
                _finished = true;
                httpThread = null;
                Watchdog.RemoveThread();
            }
        }

        public void Stop()
        {
            try
            {
                if (httpThread != null)
                {
                    Watchdog.AbortThread(httpThread.ManagedThreadId);
                    httpThread = null;
                }
            }
            catch (Exception)
            {
            }
        }

        public UUID GetReqID()
        {
            return ReqID;
        }
    }
}