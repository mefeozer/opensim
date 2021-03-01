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
using System.Reflection;
using OpenMetaverse;

namespace OpenSim.Framework
{
    public class EstateSettings
    {
        // private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public delegate void SaveDelegate(EstateSettings rs);

        public event SaveDelegate OnSave;

        // Only the client uses these
        //
        private uint _EstateID = 0;
        public uint EstateID
        {
            get => _EstateID;
            set => _EstateID = value;
        }

        private string _EstateName = "My Estate";
        public string EstateName
        {
            get => _EstateName;
            set => _EstateName = value;
        }

        private bool _AllowLandmark = true;
        public bool AllowLandmark
        {
            get => _AllowLandmark;
            set => _AllowLandmark = value;
        }

        private bool _AllowParcelChanges = true;
        public bool AllowParcelChanges
        {
            get => _AllowParcelChanges;
            set => _AllowParcelChanges = value;
        }

        private bool _AllowSetHome = true;
        public bool AllowSetHome
        {
            get => _AllowSetHome;
            set => _AllowSetHome = value;
        }

        private uint _ParentEstateID = 1;
        public uint ParentEstateID
        {
            get => _ParentEstateID;
            set => _ParentEstateID = value;
        }

        private float _BillableFactor = 0.0f;
        public float BillableFactor
        {
            get => _BillableFactor;
            set => _BillableFactor = value;
        }

        private int _PricePerMeter = 1;
        public int PricePerMeter
        {
            get => _PricePerMeter;
            set => _PricePerMeter = value;
        }

        private int _RedirectGridX = 0;
        public int RedirectGridX
        {
            get => _RedirectGridX;
            set => _RedirectGridX = value;
        }

        private int _RedirectGridY = 0;
        public int RedirectGridY
        {
            get => _RedirectGridY;
            set => _RedirectGridY = value;
        }

        // Used by the sim
        //
        private bool _UseGlobalTime = false;
        public bool UseGlobalTime
        {
            get => _UseGlobalTime;
            //set { _UseGlobalTime = value; }
            set => _UseGlobalTime = false;
        }

        private bool _FixedSun = false;
        public bool FixedSun
        {
            get => _FixedSun;
            // set { _FixedSun = value; }
            set => _FixedSun = false;
        }

        private double _SunPosition = 0.0;
        public double SunPosition
        {
            get => _SunPosition;
            //set { _SunPosition = value; }
            set => _SunPosition = 0;
        }

        private bool _AllowVoice = true;
        public bool AllowVoice
        {
            get => _AllowVoice;
            set => _AllowVoice = value;
        }

        private bool _AllowDirectTeleport = true;
        public bool AllowDirectTeleport
        {
            get => _AllowDirectTeleport;
            set => _AllowDirectTeleport = value;
        }

        private bool _DenyAnonymous = false;
        public bool DenyAnonymous
        {
            get => DoDenyAnonymous && _DenyAnonymous;
            set => _DenyAnonymous = value;
        }

        // no longer in used, may be reassigned
        private bool _DenyIdentified = false;
        public bool DenyIdentified
        {
            get => _DenyIdentified;
            set => _DenyIdentified = value;
        }

        // no longer in used, may be reassigned
        private bool _DenyTransacted = false;
        public bool DenyTransacted
        {
            get => _DenyTransacted;
            set => _DenyTransacted = value;
        }

        private bool _AbuseEmailToEstateOwner = false;
        public bool AbuseEmailToEstateOwner
        {
            get => _AbuseEmailToEstateOwner;
            set => _AbuseEmailToEstateOwner = value;
        }

        private bool _BlockDwell = false;
        public bool BlockDwell
        {
            get => _BlockDwell;
            set => _BlockDwell = value;
        }

        private bool _EstateSkipScripts = false;
        public bool EstateSkipScripts
        {
            get => _EstateSkipScripts;
            set => _EstateSkipScripts = value;
        }

        private bool _ResetHomeOnTeleport = false;
        public bool ResetHomeOnTeleport
        {
            get => _ResetHomeOnTeleport;
            set => _ResetHomeOnTeleport = value;
        }

        private bool _TaxFree = false;
        public bool TaxFree // this is now !AllowAccessOverride, keeping same name to reuse DB entries
        {
            get => _TaxFree;
            set => _TaxFree = value;
        }

        private bool _PublicAccess = true;
        public bool PublicAccess
        {
            get => _PublicAccess;
            set => _PublicAccess = value;
        }

        private string _AbuseEmail = string.Empty;

        public string AbuseEmail
        {
            get => _AbuseEmail;
            set => _AbuseEmail= value;
        }

        private UUID _EstateOwner = UUID.Zero;
        public UUID EstateOwner
        {
            get => _EstateOwner;
            set => _EstateOwner = value;
        }

        private bool _DenyMinors = false;
        public bool DenyMinors
        {
            get => DoDenyMinors && _DenyMinors;
            set => _DenyMinors = value;
        }

        private bool _AllowEnviromentOverride = false; //keep the mispell so not to go change the dbs
        public bool AllowEnvironmentOverride
        {
            get => _AllowEnviromentOverride;
            set => _AllowEnviromentOverride = value;
        }

        // All those lists...
        //
        private List<UUID> l_EstateManagers = new List<UUID>();

        public UUID[] EstateManagers
        {
            get => l_EstateManagers.ToArray();
            set => l_EstateManagers = new List<UUID>(value);
        }

        private List<EstateBan> l_EstateBans = new List<EstateBan>();

        public EstateBan[] EstateBans
        {
            get => l_EstateBans.ToArray();
            set => l_EstateBans = new List<EstateBan>(value);
        }

        private List<UUID> l_EstateAccess = new List<UUID>();
        public UUID[] EstateAccess
        {
            get => l_EstateAccess.ToArray();
            set => l_EstateAccess = new List<UUID>(value);
        }

        private List<UUID> l_EstateGroups = new List<UUID>();
        public UUID[] EstateGroups
        {
            get => l_EstateGroups.ToArray();
            set => l_EstateGroups = new List<UUID>(value);
        }

        public bool DoDenyMinors = true;
        public bool DoDenyAnonymous = true;

        public EstateSettings()
        {
        }

        public void Save()
        {
            if (OnSave != null)
                OnSave(this);
        }

        public int EstateUsersCount()
        {
            return l_EstateAccess.Count;
        }

        public void AddEstateUser(UUID avatarID)
        {
            if (avatarID == UUID.Zero)
                return;
            if (!l_EstateAccess.Contains(avatarID) &&
                    l_EstateAccess.Count < (int)Constants.EstateAccessLimits.AllowedAccess)
                l_EstateAccess.Add(avatarID);
        }

        public void RemoveEstateUser(UUID avatarID)
        {
            if (l_EstateAccess.Contains(avatarID))
                l_EstateAccess.Remove(avatarID);
        }

        public int EstateGroupsCount()
        {
            return l_EstateGroups.Count;
        }

        public void AddEstateGroup(UUID avatarID)
        {
            if (avatarID == UUID.Zero)
                return;
            if (!l_EstateGroups.Contains(avatarID) &&
                    l_EstateGroups.Count < (int)Constants.EstateAccessLimits.AllowedGroups)
                l_EstateGroups.Add(avatarID);
        }

        public void RemoveEstateGroup(UUID avatarID)
        {
            if (l_EstateGroups.Contains(avatarID))
                l_EstateGroups.Remove(avatarID);
        }

        public int EstateManagersCount()
        {
            return l_EstateManagers.Count;
        }

        public void AddEstateManager(UUID avatarID)
        {
            if (avatarID == UUID.Zero)
                return;
            if (!l_EstateManagers.Contains(avatarID) &&
                l_EstateManagers.Count < (int)Constants.EstateAccessLimits.EstateManagers)
                l_EstateManagers.Add(avatarID);
        }

        public void RemoveEstateManager(UUID avatarID)
        {
            if (l_EstateManagers.Contains(avatarID))
                l_EstateManagers.Remove(avatarID);
        }

        public bool IsEstateManagerOrOwner(UUID avatarID)
        {
            if (IsEstateOwner(avatarID))
                return true;

            return l_EstateManagers.Contains(avatarID);
        }

        public bool IsEstateOwner(UUID avatarID)
        {
            if (avatarID == _EstateOwner)
                return true;

            return false;
        }

        public bool IsBanned(UUID avatarID)
        {
            if (!IsEstateManagerOrOwner(avatarID))
            {
                foreach (EstateBan ban in l_EstateBans)
                    if (ban.BannedUserID == avatarID)
                        return true;
            }
            return false;
        }

        public bool IsBanned(UUID avatarID, int userFlags)
        {
            if (!IsEstateManagerOrOwner(avatarID))
            {
                foreach (EstateBan ban in l_EstateBans)
                if (ban.BannedUserID == avatarID)
                    return true;

                if (!HasAccess(avatarID))
                {
                    if (DenyMinors)
                    {
                        if ((userFlags & 32) == 0)
                        {
                            return true;
                        }
                    }
                    if (DenyAnonymous)
                    {
                        if ((userFlags & 4) == 0)
                        {
                            return true;
                        }
                    }
                }
            }
            return false;
        }

        public int EstateBansCount()
        {
            return l_EstateBans.Count;
        }

        public void AddBan(EstateBan ban)
        {
            if (ban == null)
                return;
            if (!IsBanned(ban.BannedUserID, 32) &&
                l_EstateBans.Count < (int)Constants.EstateAccessLimits.EstateBans) //Ignore age-based bans
                l_EstateBans.Add(ban);
        }

        public void ClearBans()
        {
            l_EstateBans.Clear();
        }

        public void RemoveBan(UUID avatarID)
        {
            foreach (EstateBan ban in new List<EstateBan>(l_EstateBans))
                if (ban.BannedUserID == avatarID)
                    l_EstateBans.Remove(ban);
        }

        public bool HasAccess(UUID user)
        {
            if (IsEstateManagerOrOwner(user))
                return true;

            return l_EstateAccess.Contains(user);
        }

        public void SetFromFlags(ulong regionFlags)
        {
            ResetHomeOnTeleport = (regionFlags & (ulong)OpenMetaverse.RegionFlags.ResetHomeOnTeleport) == (ulong)OpenMetaverse.RegionFlags.ResetHomeOnTeleport;
            BlockDwell = (regionFlags & (ulong)OpenMetaverse.RegionFlags.BlockDwell) == (ulong)OpenMetaverse.RegionFlags.BlockDwell;
            AllowLandmark = (regionFlags & (ulong)OpenMetaverse.RegionFlags.AllowLandmark) == (ulong)OpenMetaverse.RegionFlags.AllowLandmark;
            AllowParcelChanges = (regionFlags & (ulong)OpenMetaverse.RegionFlags.AllowParcelChanges) == (ulong)OpenMetaverse.RegionFlags.AllowParcelChanges;
            AllowSetHome = (regionFlags & (ulong)OpenMetaverse.RegionFlags.AllowSetHome) == (ulong)OpenMetaverse.RegionFlags.AllowSetHome;
        }

        public bool GroupAccess(UUID groupID)
        {
            return l_EstateGroups.Contains(groupID);
        }

        public Dictionary<string, object> ToMap()
        {
            Dictionary<string, object> map = new Dictionary<string, object>();
            PropertyInfo[] properties = this.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
            foreach (PropertyInfo p in properties)
            {
                // EstateBans is a complex type, let's treat it as special
                if (p.Name == "EstateBans")
                    continue;

                object value = p.GetValue(this, null);
                if (value != null)
                {
                    if (p.PropertyType.IsArray) // of UUIDs
                    {
                        if (((Array)value).Length > 0)
                        {
                            string[] args = new string[((Array)value).Length];
                            int index = 0;
                            foreach (object o in (Array)value)
                                args[index++] = o.ToString();
                            map[p.Name] = string.Join(",", args);
                        }
                    }
                    else // simple types
                        map[p.Name] = value;
                }
            }

            // EstateBans are special
            if (EstateBans.Length > 0)
            {
                Dictionary<string, object> bans = new Dictionary<string, object>();
                int i = 0;
                foreach (EstateBan ban in EstateBans)
                    bans["ban" + i++] = ban.ToMap();
                map["EstateBans"] = bans;
            }

            return map;
        }

        /// <summary>
        /// For debugging
        /// </summary>
        /// <returns></returns>
        public override string ToString()
        {
            Dictionary<string, object> map = ToMap();
            string result = string.Empty;

            foreach (KeyValuePair<string, object> kvp in map)
            {
                if (kvp.Key == "EstateBans")
                {
                    result += "EstateBans:" + Environment.NewLine;
                    foreach (KeyValuePair<string, object> ban in (Dictionary<string, object>)kvp.Value)
                        result += ban.Value.ToString();
                }
                else
                    result += string.Format("{0}: {1} {2}", kvp.Key, kvp.Value.ToString(), Environment.NewLine);
            }

            return result;
        }

        public EstateSettings(Dictionary<string, object> map)
        {
            foreach (KeyValuePair<string, object> kvp in map)
            {
                PropertyInfo p = this.GetType().GetProperty(kvp.Key, BindingFlags.Public | BindingFlags.Instance);
                if (p == null)
                    continue;

                // EstateBans is a complex type, let's treat it as special
                if (p.Name == "EstateBans")
                    continue;

                if (p.PropertyType.IsArray)
                {
                    string[] elements = ((string)map[p.Name]).Split(new char[] { ',' });
                    UUID[] uuids = new UUID[elements.Length];
                    int i = 0;
                    foreach (string e in elements)
                        uuids[i++] = new UUID(e);
                    p.SetValue(this, uuids, null);
                }
                else
                {
                    object value = p.GetValue(this, null);
                    if (value is string)
                        p.SetValue(this, map[p.Name], null);
                    else if (value is uint)
                        p.SetValue(this, uint.Parse((string)map[p.Name]), null);
                    else if (value is bool)
                        p.SetValue(this, bool.Parse((string)map[p.Name]), null);
                    else if (value is UUID)
                        p.SetValue(this, UUID.Parse((string)map[p.Name]), null);
                }
            }

            // EstateBans are special
            if (map.ContainsKey("EstateBans"))
            {               
                if(map["EstateBans"] is string)
                {
                    // JSON encoded bans map
                    Dictionary<string, EstateBan> bdata = new Dictionary<string, EstateBan>();
                    try
                    {
                        // bypass libovm, we dont need even more useless high level maps
                        // this should only be called once.. but no problem, i hope
                        // (other uses may need more..)
                        LitJson.JsonMapper.RegisterImporter<string, UUID>((input) => new UUID(input));
                        bdata = LitJson.JsonMapper.ToObject<Dictionary<string,EstateBan>>((string)map["EstateBans"]);
                    }
 //                   catch(Exception e)
                    catch
                    {
                        return;
                    }
                    EstateBan[] jbans = new EstateBan[bdata.Count];
                    bdata.Values.CopyTo(jbans,0);

                    PropertyInfo jbansProperty = this.GetType().GetProperty("EstateBans", BindingFlags.Public | BindingFlags.Instance);
                    jbansProperty.SetValue(this, jbans, null);
                }
                else
                {
                    var banData = ((Dictionary<string, object>)map["EstateBans"]).Values;
                    EstateBan[] bans = new EstateBan[banData.Count];

                    int b = 0;
                    foreach (Dictionary<string, object> ban in banData)
                        bans[b++] = new EstateBan(ban);
                    PropertyInfo bansProperty = this.GetType().GetProperty("EstateBans", BindingFlags.Public | BindingFlags.Instance);
                    bansProperty.SetValue(this, bans, null);
                 }
            }
        }
    }
}
