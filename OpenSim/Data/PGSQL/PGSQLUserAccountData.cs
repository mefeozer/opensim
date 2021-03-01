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
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ''AS IS'' AND ANY
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
using OpenMetaverse;
using Npgsql;
using log4net;
using System.Reflection;

namespace OpenSim.Data.PGSQL
{
    public class PGSQLUserAccountData : PGSQLGenericTableHandler<UserAccountData>,IUserAccountData
    {
        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);


        public PGSQLUserAccountData(string connectionString, string realm) :
            base(connectionString, realm, "UserAccount")
        {
        }

        /*
        private string _Realm;
        private List<string> _ColumnNames = null;
        private PGSQLManager _database;

        public PGSQLUserAccountData(string connectionString, string realm) :
            base(connectionString, realm, "UserAccount")
        {
            _Realm = realm;
            _ConnectionString = connectionString;
            _database = new PGSQLManager(connectionString);

            using (NpgsqlConnection conn = new NpgsqlConnection(_ConnectionString))
            {
                conn.Open();
                Migration m = new Migration(conn, GetType().Assembly, "UserAccount");
                m.Update();
            }
        }
        */
        /*
        public List<UserAccountData> Query(UUID principalID, UUID scopeID, string query)
        {
            return null;
        }
        */
        /*
        public override UserAccountData[] Get(string[] fields, string[] keys)
        {
            UserAccountData[] retUA = base.Get(fields,keys);

            if (retUA.Length > 0)
            {
                Dictionary<string, string> data = retUA[0].Data;
                Dictionary<string, string> data2 = new Dictionary<string, string>();

                foreach (KeyValuePair<string,string> chave in data)
                {
                    string s2 = chave.Key;

                    data2[s2] = chave.Value;

                    if (!_FieldTypes.ContainsKey(chave.Key))
                    {
                        string tipo = "";
                        _FieldTypes.TryGetValue(chave.Key, out tipo);
                        _FieldTypes.Add(s2, tipo);
                    }
                }
                foreach (KeyValuePair<string, string> chave in data2)
                {
                    if (!retUA[0].Data.ContainsKey(chave.Key))
                        retUA[0].Data.Add(chave.Key, chave.Value);
                }
            }

            return retUA;
        }
        */
        /*
        public UserAccountData Get(UUID principalID, UUID scopeID)
        {
            UserAccountData ret = new UserAccountData();
            ret.Data = new Dictionary<string, string>();

            string sql = string.Format(@"select * from {0} where ""PrincipalID"" = :principalID", _Realm);
            if (scopeID != UUID.Zero)
                sql += @" and ""ScopeID"" = :scopeID";

            using (NpgsqlConnection conn = new NpgsqlConnection(_ConnectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(sql, conn))
            {
                cmd.Parameters.Add(_database.CreateParameter("principalID", principalID));
                cmd.Parameters.Add(_database.CreateParameter("scopeID", scopeID));

                conn.Open();
                using (NpgsqlDataReader result = cmd.ExecuteReader())
                {
                    if (result.Read())
                    {
                        ret.PrincipalID = principalID;
                        UUID scope;
                        UUID.TryParse(result["scopeid"].ToString(), out scope);
                        ret.ScopeID = scope;

                        if (_ColumnNames == null)
                        {
                            _ColumnNames = new List<string>();

                            DataTable schemaTable = result.GetSchemaTable();
                            foreach (DataRow row in schemaTable.Rows)
                                _ColumnNames.Add(row["ColumnName"].ToString());
                        }

                        foreach (string s in _ColumnNames)
                        {
                            string s2 = s;
                            if (s2 == "uuid")
                                continue;
                            if (s2 == "scopeid")
                                continue;

                            ret.Data[s] = result[s].ToString();
                        }
                        return ret;
                    }
                }
            }
            return null;
        }


        public override bool Store(UserAccountData data)
        {
            if (data.Data.ContainsKey("PrincipalID"))
                data.Data.Remove("PrincipalID");
            if (data.Data.ContainsKey("ScopeID"))
                data.Data.Remove("ScopeID");

            string[] fields = new List<string>(data.Data.Keys).ToArray();

            using (NpgsqlConnection conn = new NpgsqlConnection(_ConnectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand())
            {
                _log.DebugFormat("[USER]: Try to update user {0} {1}", data.FirstName, data.LastName);

                StringBuilder updateBuilder = new StringBuilder();
                updateBuilder.AppendFormat("update {0} set ", _Realm);
                bool first = true;
                foreach (string field in fields)
                {
                    if (!first)
                        updateBuilder.Append(", ");
                    updateBuilder.AppendFormat("\"{0}\" = :{0}", field);

                    first = false;
                    if (_FieldTypes.ContainsKey(field))
                        cmd.Parameters.Add(_database.CreateParameter("" + field, data.Data[field], _FieldTypes[field]));
                    else
                        cmd.Parameters.Add(_database.CreateParameter("" + field, data.Data[field]));
                }

                updateBuilder.Append(" where \"PrincipalID\" = :principalID");

                if (data.ScopeID != UUID.Zero)
                    updateBuilder.Append(" and \"ScopeID\" = :scopeID");

                cmd.CommandText = updateBuilder.ToString();
                cmd.Connection = conn;
                cmd.Parameters.Add(_database.CreateParameter("principalID", data.PrincipalID));
                cmd.Parameters.Add(_database.CreateParameter("scopeID", data.ScopeID));

                _log.DebugFormat("[USER]: SQL update user {0} ", cmd.CommandText);

                conn.Open();

                _log.DebugFormat("[USER]: CON opened update user {0} ", cmd.CommandText);

                int conta = 0;
                try
                {
                    conta = cmd.ExecuteNonQuery();
                }
                catch (Exception e){
                    _log.ErrorFormat("[USER]: ERROR opened update user {0} ", e.Message);
                }


                if (conta < 1)
                {
                    _log.DebugFormat("[USER]: Try to insert user {0} {1}", data.FirstName, data.LastName);

                    StringBuilder insertBuilder = new StringBuilder();
                    insertBuilder.AppendFormat(@"insert into {0} (""PrincipalID"", ""ScopeID"", ""FirstName"", ""LastName"", """, _Realm);
                    insertBuilder.Append(String.Join(@""", """, fields));
                    insertBuilder.Append(@""") values (:principalID, :scopeID, :FirstName, :LastName, :");
                    insertBuilder.Append(String.Join(", :", fields));
                    insertBuilder.Append(");");

                    cmd.Parameters.Add(_database.CreateParameter("FirstName", data.FirstName));
                    cmd.Parameters.Add(_database.CreateParameter("LastName", data.LastName));

                    cmd.CommandText = insertBuilder.ToString();

                    if (cmd.ExecuteNonQuery() < 1)
                    {
                        return false;
                    }
                }
                else
                    _log.DebugFormat("[USER]: User {0} {1} exists", data.FirstName, data.LastName);
            }
            return true;
        }


        public bool Store(UserAccountData data, UUID principalID, string token)
        {
            return false;
        }


        public bool SetDataItem(UUID principalID, string item, string value)
        {
            string sql = string.Format(@"update {0} set {1} = :{1} where ""UUID"" = :UUID", _Realm, item);
            using (NpgsqlConnection conn = new NpgsqlConnection(_ConnectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(sql, conn))
            {
                if (_FieldTypes.ContainsKey(item))
                    cmd.Parameters.Add(_database.CreateParameter("" + item, value, _FieldTypes[item]));
                else
                    cmd.Parameters.Add(_database.CreateParameter("" + item, value));

                cmd.Parameters.Add(_database.CreateParameter("UUID", principalID));
                conn.Open();

                if (cmd.ExecuteNonQuery() > 0)
                    return true;
            }
            return false;
        }
        */
        /*
        public UserAccountData[] Get(string[] keys, string[] vals)
        {
            return null;
        }
        */

        public UserAccountData[] GetUsers(UUID scopeID, string query)
        {
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

            string sql = "";
            UUID scope_id;
            UUID.TryParse(scopeID.ToString(), out scope_id);

            using (NpgsqlConnection conn = new NpgsqlConnection(_ConnectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand())
            {
                if (words.Length == 1)
                {
                    sql = string.Format(@"select * from {0} where (""ScopeID""=:ScopeID or ""ScopeID""=:UUIDZero) and (""FirstName"" ilike :search or ""LastName"" ilike :search)", _Realm);
                    cmd.Parameters.Add(_database.CreateParameter("scopeID", (UUID)scope_id));
                    cmd.Parameters.Add (_database.CreateParameter("UUIDZero", (UUID)UUID.Zero));
                    cmd.Parameters.Add(_database.CreateParameter("search", "%" + words[0] + "%"));
                }
                else
                {
                    sql = string.Format(@"select * from {0} where (""ScopeID""=:ScopeID or ""ScopeID""=:UUIDZero) and (""FirstName"" ilike :searchFirst or ""LastName"" ilike :searchLast)", _Realm);
                    cmd.Parameters.Add(_database.CreateParameter("searchFirst", "%" + words[0] + "%"));
                    cmd.Parameters.Add(_database.CreateParameter("searchLast", "%" + words[1] + "%"));
                    cmd.Parameters.Add (_database.CreateParameter("UUIDZero", (UUID)UUID.Zero));
                    cmd.Parameters.Add(_database.CreateParameter("ScopeID", (UUID)scope_id));
                }
                cmd.Connection = conn;
                cmd.CommandText = sql;
                conn.Open();
                return DoQuery(cmd);
            }
        }

        public UserAccountData[] GetUsersWhere(UUID scopeID, string where)
        {
            return null;
        }
    }
}
