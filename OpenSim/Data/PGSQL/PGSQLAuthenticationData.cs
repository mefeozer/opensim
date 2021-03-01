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
using System.Collections.Generic;
using OpenMetaverse;
using System.Reflection;
using System.Text;
using System.Data;
using Npgsql;

namespace OpenSim.Data.PGSQL
{
    public class PGSQLAuthenticationData : IAuthenticationData
    {
        private readonly string _Realm;
        private List<string> _ColumnNames = null;
        private int _LastExpire = 0;
        private readonly string _ConnectionString;
        private readonly PGSQLManager _database;

        protected virtual Assembly Assembly => GetType().Assembly;

        public PGSQLAuthenticationData(string connectionString, string realm)
        {
            _Realm = realm;
            _ConnectionString = connectionString;
            using (NpgsqlConnection conn = new NpgsqlConnection(_ConnectionString))
            {
                conn.Open();
                Migration m = new Migration(conn, GetType().Assembly, "AuthStore");
                _database = new PGSQLManager(_ConnectionString);
                m.Update();
            }
        }

        public AuthenticationData Get(UUID principalID)
        {
            AuthenticationData ret = new AuthenticationData
            {
                Data = new Dictionary<string, object>()
            };

            string sql = string.Format("select * from {0} where uuid = :principalID", _Realm);

            using (NpgsqlConnection conn = new NpgsqlConnection(_ConnectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(sql, conn))
            {
                cmd.Parameters.Add(_database.CreateParameter("principalID", principalID));
                conn.Open();
                using (NpgsqlDataReader result = cmd.ExecuteReader())
                {
                    if (result.Read())
                    {
                        ret.PrincipalID = principalID;

                        if (_ColumnNames == null)
                        {
                            _ColumnNames = new List<string>();

                            DataTable schemaTable = result.GetSchemaTable();
                            foreach (DataRow row in schemaTable.Rows)
                                _ColumnNames.Add(row["ColumnName"].ToString());
                        }

                        foreach (string s in _ColumnNames)
                        {
                            if (s == "UUID"||s == "uuid")
                                continue;

                            ret.Data[s] = result[s].ToString();
                        }
                        return ret;
                    }
                }
            }
            return null;
        }

        public bool Store(AuthenticationData data)
        {
            if (data.Data.ContainsKey("UUID"))
                data.Data.Remove("UUID");
            if (data.Data.ContainsKey("uuid"))
                data.Data.Remove("uuid");

            /*
            Dictionary<string, object> oAuth = new Dictionary<string, object>();

            foreach (KeyValuePair<string, object> oDado in data.Data)
            {
                if (oDado.Key != oDado.Key.ToLower())
                {
                    oAuth.Add(oDado.Key.ToLower(), oDado.Value);
                }
            }
            foreach (KeyValuePair<string, object> oDado in data.Data)
            {
                if (!oAuth.ContainsKey(oDado.Key.ToLower())) {
                    oAuth.Add(oDado.Key.ToLower(), oDado.Value);
                }
            }
            */
            string[] fields = new List<string>(data.Data.Keys).ToArray();
            StringBuilder updateBuilder = new StringBuilder();

            using (NpgsqlConnection conn = new NpgsqlConnection(_ConnectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand())
            {
                updateBuilder.AppendFormat("update {0} set ", _Realm);

                bool first = true;
                foreach (string field in fields)
                {
                    if (!first)
                        updateBuilder.Append(", ");
                    updateBuilder.AppendFormat("\"{0}\" = :{0}",field);

                    first = false;

                    cmd.Parameters.Add(_database.CreateParameter("" + field, data.Data[field]));
                }

                updateBuilder.Append(" where uuid = :principalID");

                cmd.CommandText = updateBuilder.ToString();
                cmd.Connection = conn;
                cmd.Parameters.Add(_database.CreateParameter("principalID", data.PrincipalID));

                conn.Open();
                if (cmd.ExecuteNonQuery() < 1)
                {
                    StringBuilder insertBuilder = new StringBuilder();

                    insertBuilder.AppendFormat("insert into {0} (uuid, \"", _Realm);
                    insertBuilder.Append(string.Join("\", \"", fields));
                    insertBuilder.Append("\") values (:principalID, :");
                    insertBuilder.Append(string.Join(", :", fields));
                    insertBuilder.Append(")");

                    cmd.CommandText = insertBuilder.ToString();

                    if (cmd.ExecuteNonQuery() < 1)
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        public bool SetDataItem(UUID principalID, string item, string value)
        {
            string sql = string.Format("update {0} set {1} = :{1} where uuid = :UUID", _Realm, item);
            using (NpgsqlConnection conn = new NpgsqlConnection(_ConnectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(sql, conn))
            {
                cmd.Parameters.Add(_database.CreateParameter("" + item, value));
                conn.Open();
                if (cmd.ExecuteNonQuery() > 0)
                    return true;
            }
            return false;
        }

        public bool SetToken(UUID principalID, string token, int lifetime)
        {
            if (System.Environment.TickCount - _LastExpire > 30000)
                DoExpire();

            string sql = "insert into tokens (uuid, token, validity) values (:principalID, :token, :lifetime)";
            using (NpgsqlConnection conn = new NpgsqlConnection(_ConnectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(sql, conn))
            {
                cmd.Parameters.Add(_database.CreateParameter("principalID", principalID));
                cmd.Parameters.Add(_database.CreateParameter("token", token));
                cmd.Parameters.Add(_database.CreateParameter("lifetime", DateTime.Now.AddMinutes(lifetime)));
                conn.Open();

                if (cmd.ExecuteNonQuery() > 0)
                {
                    return true;
                }
            }
            return false;
        }

        public bool CheckToken(UUID principalID, string token, int lifetime)
        {
            if (System.Environment.TickCount - _LastExpire > 30000)
                DoExpire();

            DateTime validDate = DateTime.Now.AddMinutes(lifetime);
            string sql = "update tokens set validity = :validDate where uuid = :principalID and token = :token and validity > (CURRENT_DATE + CURRENT_TIME)";

            using (NpgsqlConnection conn = new NpgsqlConnection(_ConnectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(sql, conn))
            {
                cmd.Parameters.Add(_database.CreateParameter("principalID", principalID));
                cmd.Parameters.Add(_database.CreateParameter("token", token));
                cmd.Parameters.Add(_database.CreateParameter("validDate", validDate));
                conn.Open();

                if (cmd.ExecuteNonQuery() > 0)
                {
                    return true;
                }
            }
            return false;
        }

        private void DoExpire()
        {
            DateTime currentDateTime = DateTime.Now;
            string sql = "delete from tokens where validity < :currentDateTime";
            using (NpgsqlConnection conn = new NpgsqlConnection(_ConnectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(sql, conn))
            {
                conn.Open();
                cmd.Parameters.Add(_database.CreateParameter("currentDateTime", currentDateTime));
                cmd.ExecuteNonQuery();
            }
            _LastExpire = System.Environment.TickCount;
        }
    }
}
