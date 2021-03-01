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

using System.Collections.Generic;
using OpenMetaverse;

namespace OpenSim.Data.Null
{
    public class NullPresenceData : IPresenceData
    {
//        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public static NullPresenceData Instance;

        readonly Dictionary<UUID, PresenceData> _presenceData = new Dictionary<UUID, PresenceData>();

        public NullPresenceData(string connectionString, string realm)
        {
            if (Instance == null)
            {
                Instance = this;

                //Console.WriteLine("[XXX] NullRegionData constructor");
            }
        }

        public bool Store(PresenceData data)
        {
            if (Instance != this)
                return Instance.Store(data);

//            _log.DebugFormat("[NULL PRESENCE DATA]: Storing presence {0}", data.UserID);
//            Console.WriteLine("HOME for " + data.UserID + " is " + (data.Data.ContainsKey("HomeRegionID") ? data.Data["HomeRegionID"] : "Not found"));

            _presenceData[data.SessionID] = data;
            return true;
        }

        public PresenceData Get(UUID sessionID)
        {
            if (Instance != this)
                return Instance.Get(sessionID);

            if (_presenceData.ContainsKey(sessionID))
            {
                return _presenceData[sessionID];
            }

            return null;
        }

        public void LogoutRegionAgents(UUID regionID)
        {
            if (Instance != this)
            {
                Instance.LogoutRegionAgents(regionID);
                return;
            }

            List<UUID> toBeDeleted = new List<UUID>();
            foreach (KeyValuePair<UUID, PresenceData> kvp in _presenceData)
                if (kvp.Value.RegionID == regionID)
                    toBeDeleted.Add(kvp.Key);

            foreach (UUID u in toBeDeleted)
                _presenceData.Remove(u);
        }

        public bool ReportAgent(UUID sessionID, UUID regionID)
        {
            if (Instance != this)
                return Instance.ReportAgent(sessionID, regionID);

            if (_presenceData.ContainsKey(sessionID))
            {
                _presenceData[sessionID].RegionID = regionID;
                return true;
            }

            return false;
        }

        public PresenceData[] Get(string field, string data)
        {
            if (Instance != this)
                return Instance.Get(field, data);

//            _log.DebugFormat(
//                "[NULL PRESENCE DATA]: Getting presence data for field {0} with parameter {1}", field, data);

            List<PresenceData> presences = new List<PresenceData>();
            if (field == "UserID")
            {
                foreach (PresenceData p in _presenceData.Values)
                {
                    if (p.UserID == data)
                    {
                        presences.Add(p);
//                        Console.WriteLine("HOME for " + p.UserID + " is " + (p.Data.ContainsKey("HomeRegionID") ? p.Data["HomeRegionID"] : "Not found"));
                    }
                }

                return presences.ToArray();
            }
            else if (field == "SessionID")
            {
                UUID session = UUID.Zero;
                if (!UUID.TryParse(data, out session))
                    return presences.ToArray();

                if (_presenceData.ContainsKey(session))
                {
                    presences.Add(_presenceData[session]);
                    return presences.ToArray();
                }
            }
            else if (field == "RegionID")
            {
                UUID region = UUID.Zero;
                if (!UUID.TryParse(data, out region))
                    return presences.ToArray();
                foreach (PresenceData p in _presenceData.Values)
                    if (p.RegionID == region)
                        presences.Add(p);
                return presences.ToArray();
            }
            else
            {
                foreach (PresenceData p in _presenceData.Values)
                {
                    if (p.Data.ContainsKey(field) && p.Data[field] == data)
                        presences.Add(p);
                }
                return presences.ToArray();
            }

            return presences.ToArray();
        }


        public bool Delete(string field, string data)
        {
//            _log.DebugFormat(
//                "[NULL PRESENCE DATA]: Deleting presence data for field {0} with parameter {1}", field, data);

            if (Instance != this)
                return Instance.Delete(field, data);

            List<UUID> presences = new List<UUID>();
            if (field == "UserID")
            {
                foreach (KeyValuePair<UUID, PresenceData> p in _presenceData)
                    if (p.Value.UserID == data)
                        presences.Add(p.Key);
            }
            else if (field == "SessionID")
            {
                UUID session = UUID.Zero;
                if (UUID.TryParse(data, out session))
                {
                    if (_presenceData.ContainsKey(session))
                    {
                        presences.Add(session);
                    }
                }
            }
            else if (field == "RegionID")
            {
                UUID region = UUID.Zero;
                if (UUID.TryParse(data, out region))
                {
                    foreach (KeyValuePair<UUID, PresenceData> p in _presenceData)
                        if (p.Value.RegionID == region)
                            presences.Add(p.Key);
                }
            }
            else
            {
                foreach (KeyValuePair<UUID, PresenceData> p in _presenceData)
                {
                    if (p.Value.Data.ContainsKey(field) && p.Value.Data[field] == data)
                        presences.Add(p.Key);
                }
            }

            foreach (UUID u in presences)
                _presenceData.Remove(u);

            if (presences.Count == 0)
                return false;

            return true;
        }

        public bool VerifyAgent(UUID agentId, UUID secureSessionID)
        {
            if (Instance != this)
                return Instance.VerifyAgent(agentId, secureSessionID);

            return false;
        }

    }
}
