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
using log4net;
using OpenMetaverse;

namespace OpenSim.Data.Null
{
    public class NullUserAccountData : IUserAccountData
    {
        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private readonly Dictionary<UUID, UserAccountData> _DataByUUID = new Dictionary<UUID, UserAccountData>();
        private readonly Dictionary<string, UserAccountData> _DataByName = new Dictionary<string, UserAccountData>();
        private readonly Dictionary<string, UserAccountData> _DataByEmail = new Dictionary<string, UserAccountData>();

        public NullUserAccountData(string connectionString, string realm)
        {
//            _log.DebugFormat(
//                "[NULL USER ACCOUNT DATA]: Initializing new NullUserAccountData with connectionString [{0}], realm [{1}]",
//                connectionString, realm);
        }

        /// <summary>
        /// Tries to implement the Get [] semantics, but it cuts corners like crazy.
        /// Specifically, it relies on the knowledge that the only Gets used are
        /// keyed on PrincipalID, Email, and FirstName+LastName.
        /// </summary>
        /// <param name="fields"></param>
        /// <param name="values"></param>
        /// <returns></returns>
        public UserAccountData[] Get(string[] fields, string[] values)
        {
//            if (_log.IsDebugEnabled)
//            {
//                _log.DebugFormat(
//                    "[NULL USER ACCOUNT DATA]: Called Get with fields [{0}], values [{1}]",
//                    string.Join(", ", fields), string.Join(", ", values));
//            }

            UserAccountData[] userAccounts = new UserAccountData[0];

            List<string> fieldsLst = new List<string>(fields);
            if (fieldsLst.Contains("PrincipalID"))
            {
                int i = fieldsLst.IndexOf("PrincipalID");
                UUID id = UUID.Zero;
                if (UUID.TryParse(values[i], out id))
                    if (_DataByUUID.ContainsKey(id))
                        userAccounts = new UserAccountData[] { _DataByUUID[id] };
            }
            else if (fieldsLst.Contains("FirstName") && fieldsLst.Contains("LastName"))
            {
                int findex = fieldsLst.IndexOf("FirstName");
                int lindex = fieldsLst.IndexOf("LastName");
                if (_DataByName.ContainsKey(values[findex] + " " + values[lindex]))
                {
                    userAccounts = new UserAccountData[] { _DataByName[values[findex] + " " + values[lindex]] };
                }
            }
            else if (fieldsLst.Contains("Email"))
            {
                int i = fieldsLst.IndexOf("Email");
                if (_DataByEmail.ContainsKey(values[i]))
                    userAccounts = new UserAccountData[] { _DataByEmail[values[i]] };
            }

//            if (_log.IsDebugEnabled)
//            {
//                StringBuilder sb = new StringBuilder();
//                foreach (UserAccountData uad in userAccounts)
//                    sb.AppendFormat("({0} {1} {2}) ", uad.FirstName, uad.LastName, uad.PrincipalID);
//
//                _log.DebugFormat(
//                    "[NULL USER ACCOUNT DATA]: Returning {0} user accounts out of {1}: [{2}]", userAccounts.Length, _DataByName.Count, sb);
//            }

            return userAccounts;
        }

        public bool Store(UserAccountData data)
        {
            if (data == null)
                return false;

            _log.DebugFormat(
                "[NULL USER ACCOUNT DATA]: Storing user account {0} {1} {2} {3}",
                data.FirstName, data.LastName, data.PrincipalID, this.GetHashCode());

            _DataByUUID[data.PrincipalID] = data;
            _DataByName[data.FirstName + " " + data.LastName] = data;
            if (data.Data.ContainsKey("Email") && data.Data["Email"] != null && !string.IsNullOrEmpty(data.Data["Email"]))
                _DataByEmail[data.Data["Email"]] = data;

//            _log.DebugFormat("_DataByUUID count is {0}, _DataByName count is {1}", _DataByUUID.Count, _DataByName.Count);

            return true;
        }

        public UserAccountData[] GetUsers(UUID scopeID, string query)
        {
//            _log.DebugFormat(
//                "[NULL USER ACCOUNT DATA]: Called GetUsers with scope [{0}], query [{1}]", scopeID, query);

            string[] words = query.Split(new char[] { ' ' });

            for (int i = 0; i < words.Length; i++)
            {
                if (words[i].Length < 3)
                {
                    if (i != words.Length - 1)
                        Array.Copy(words, i + 1, words, i, words.Length - i - 1);
                    Array.Resize(ref words, words.Length - 1);
                }
            }

            if (words.Length == 0)
                return new UserAccountData[0];

            if (words.Length > 2)
                return new UserAccountData[0];

            List<string> lst = new List<string>(_DataByName.Keys);
            if (words.Length == 1)
            {
                lst = lst.FindAll(delegate(string s) { return s.StartsWith(words[0]); });
            }
            else
            {
                lst = lst.FindAll(delegate(string s) { return s.Contains(words[0]) || s.Contains(words[1]); });
            }

            if (lst == null || lst != null && lst.Count == 0)
                return new UserAccountData[0];

            UserAccountData[] result = new UserAccountData[lst.Count];
            int n = 0;
            foreach (string key in lst)
                result[n++] = _DataByName[key];

            return result;
        }

        public bool Delete(string field, string val)
        {
            // Only delete by PrincipalID
            if (field.Equals("PrincipalID"))
            {
                UUID uuid = UUID.Zero;
                if (UUID.TryParse(val, out uuid) && _DataByUUID.ContainsKey(uuid))
                {
                    UserAccountData account = _DataByUUID[uuid];
                    _DataByUUID.Remove(uuid);
                    if (_DataByName.ContainsKey(account.FirstName + " " + account.LastName))
                        _DataByName.Remove(account.FirstName + " " + account.LastName);
                    if (account.Data.ContainsKey("Email") && !string.IsNullOrEmpty(account.Data["Email"]) && _DataByEmail.ContainsKey(account.Data["Email"]))
                        _DataByEmail.Remove(account.Data["Email"]);

                    return true;
                }
            }

            return false;
        }

        public UserAccountData[] GetUsersWhere(UUID scopeID, string where)
        {
            return null;
        }
    }
}
