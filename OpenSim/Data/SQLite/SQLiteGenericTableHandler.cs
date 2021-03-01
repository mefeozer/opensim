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
using System.Data;
using System.Reflection;
#if CSharpSqlite
    using Community.CsharpSqlite.Sqlite;
#else
using Mono.Data.Sqlite;
#endif
using OpenMetaverse;

namespace OpenSim.Data.SQLite
{
    public class SQLiteGenericTableHandler<T> : SQLiteFramework where T: class, new()
    {
//        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        protected Dictionary<string, FieldInfo> _Fields =
                new Dictionary<string, FieldInfo>();

        protected List<string> _ColumnNames = null;
        protected string _Realm;
        protected FieldInfo _DataField = null;

        protected static SqliteConnection _Connection;
        private static bool _initialized;

        protected virtual Assembly Assembly => GetType().Assembly;

        public SQLiteGenericTableHandler(string connectionString,
                string realm, string storeName) : base(connectionString)
        {
            _Realm = realm;

            if (!_initialized)
            {
                _Connection = new SqliteConnection(connectionString);
                //Console.WriteLine(string.Format("OPENING CONNECTION FOR {0} USING {1}", storeName, connectionString));
                _Connection.Open();

                if (!string.IsNullOrEmpty(storeName))
                {
                    //SqliteConnection newConnection =
                    //        (SqliteConnection)((ICloneable)_Connection).Clone();
                    //newConnection.Open();

                    //Migration m = new Migration(newConnection, Assembly, storeName);
                    Migration m = new Migration(_Connection, Assembly, storeName);
                    m.Update();
                    //newConnection.Close();
                    //newConnection.Dispose();
                }

                _initialized = true;
            }

            Type t = typeof(T);
            FieldInfo[] fields = t.GetFields(BindingFlags.Public |
                                             BindingFlags.Instance |
                                             BindingFlags.DeclaredOnly);

            if (fields.Length == 0)
                return;

            foreach (FieldInfo f in  fields)
            {
                if (f.Name != "Data")
                    _Fields[f.Name] = f;
                else
                    _DataField = f;
            }
        }

        private void CheckColumnNames(IDataReader reader)
        {
            if (_ColumnNames != null)
                return;

            _ColumnNames = new List<string>();

            DataTable schemaTable = reader.GetSchemaTable();
            foreach (DataRow row in schemaTable.Rows)
            {
                if (row["ColumnName"] != null &&
                        !_Fields.ContainsKey(row["ColumnName"].ToString()))
                    _ColumnNames.Add(row["ColumnName"].ToString());
            }
        }

        public virtual T[] Get(string field, string key)
        {
            return Get(new string[] { field }, new string[] { key });
        }

        public virtual T[] Get(string[] fields, string[] keys)
        {
            if (fields.Length != keys.Length)
                return new T[0];

            List<string> terms = new List<string>();

            using (SqliteCommand cmd = new SqliteCommand())
            {
                for (int i = 0 ; i < fields.Length ; i++)
                {
                    cmd.Parameters.Add(new SqliteParameter(":" + fields[i], keys[i]));
                    terms.Add("`" + fields[i] + "` = :" + fields[i]);
                }

                string where = string.Join(" and ", terms.ToArray());

                string query = string.Format("select * from {0} where {1}",
                        _Realm, where);

                cmd.CommandText = query;

                return DoQuery(cmd);
            }
        }

        protected T[] DoQuery(SqliteCommand cmd)
        {
            IDataReader reader = ExecuteReader(cmd, _Connection);
            if (reader == null)
                return new T[0];

            CheckColumnNames(reader);

            List<T> result = new List<T>();

            while (reader.Read())
            {
                T row = new T();

                foreach (string name in _Fields.Keys)
                {
                    if (_Fields[name].GetValue(row) is bool)
                    {
                        int v = Convert.ToInt32(reader[name]);
                        _Fields[name].SetValue(row, v != 0 ? true : false);
                    }
                    else if (_Fields[name].GetValue(row) is UUID)
                    {
                        UUID uuid = UUID.Zero;

                        UUID.TryParse(reader[name].ToString(), out uuid);
                        _Fields[name].SetValue(row, uuid);
                    }
                    else if (_Fields[name].GetValue(row) is int)
                    {
                        int v = Convert.ToInt32(reader[name]);
                        _Fields[name].SetValue(row, v);
                    }
                    else
                    {
                        _Fields[name].SetValue(row, reader[name]);
                    }
                }

                if (_DataField != null)
                {
                    Dictionary<string, string> data =
                            new Dictionary<string, string>();

                    foreach (string col in _ColumnNames)
                    {
                        data[col] = reader[col].ToString();
                        if (data[col] == null)
                            data[col] = string.Empty;
                    }

                    _DataField.SetValue(row, data);
                }

                result.Add(row);
            }

            //CloseCommand(cmd);

            return result.ToArray();
        }

        public virtual T[] Get(string where)
        {
            using (SqliteCommand cmd = new SqliteCommand())
            {
                string query = string.Format("select * from {0} where {1}",
                        _Realm, where);

                cmd.CommandText = query;

                return DoQuery(cmd);
            }
        }

        public virtual bool Store(T row)
        {
            using (SqliteCommand cmd = new SqliteCommand())
            {
                string query = "";
                List<string> names = new List<string>();
                List<string> values = new List<string>();

                foreach (FieldInfo fi in _Fields.Values)
                {
                    names.Add(fi.Name);
                    values.Add(":" + fi.Name);
                    cmd.Parameters.Add(new SqliteParameter(":" + fi.Name, fi.GetValue(row).ToString()));
                }

                if (_DataField != null)
                {
                    Dictionary<string, string> data =
                            (Dictionary<string, string>)_DataField.GetValue(row);

                    foreach (KeyValuePair<string, string> kvp in data)
                    {
                        names.Add(kvp.Key);
                        values.Add(":" + kvp.Key);
                        cmd.Parameters.Add(new SqliteParameter(":" + kvp.Key, kvp.Value));
                    }
                }

                query = string.Format("replace into {0} (`", _Realm) + string.Join("`,`", names.ToArray()) + "`) values (" + string.Join(",", values.ToArray()) + ")";

                cmd.CommandText = query;

                if (ExecuteNonQuery(cmd, _Connection) > 0)
                    return true;
            }

            return false;
        }

        public virtual bool Delete(string field, string key)
        {
            return Delete(new string[] { field }, new string[] { key });
        }

        public virtual bool Delete(string[] fields, string[] keys)
        {
            if (fields.Length != keys.Length)
                return false;

            List<string> terms = new List<string>();

            using (SqliteCommand cmd = new SqliteCommand())
            {
                for (int i = 0 ; i < fields.Length ; i++)
                {
                    cmd.Parameters.Add(new SqliteParameter(":" + fields[i], keys[i]));
                    terms.Add("`" + fields[i] + "` = :" + fields[i]);
                }

                string where = string.Join(" and ", terms.ToArray());

                string query = string.Format("delete from {0} where {1}", _Realm, where);

                cmd.CommandText = query;

                return ExecuteNonQuery(cmd, _Connection) > 0;
            }
        }
    }
}
