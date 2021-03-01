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
using log4net;
using OpenMetaverse;
using System.Text;
using Npgsql;

namespace OpenSim.Data.PGSQL
{
    public class PGSQLGenericTableHandler<T> : PGSqlFramework where T : class, new()
    {
        private static readonly ILog _log =
            LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        protected string _ConnectionString;
        protected PGSQLManager _database; //used for parameter type translation
        protected Dictionary<string, FieldInfo> _Fields =
                new Dictionary<string, FieldInfo>();

        protected Dictionary<string, string> _FieldTypes = new Dictionary<string, string>();

        protected List<string> _ColumnNames = null;
        protected string _Realm;
        protected FieldInfo _DataField = null;

        protected virtual Assembly Assembly => GetType().Assembly;

        public PGSQLGenericTableHandler(string connectionString,
                string realm, string storeName)
            : base(connectionString)
        {
            _Realm = realm;

            _ConnectionString = connectionString;

            if (!string.IsNullOrEmpty(storeName))
            {
                using (NpgsqlConnection conn = new NpgsqlConnection(_ConnectionString))
                {
                    conn.Open();
                    Migration m = new Migration(conn, GetType().Assembly, storeName);
                    m.Update();
                }

            }
            _database = new PGSQLManager(_ConnectionString);

            Type t = typeof(T);
            FieldInfo[] fields = t.GetFields(BindingFlags.Public |
                                             BindingFlags.Instance |
                                             BindingFlags.DeclaredOnly);

            LoadFieldTypes();

            if (fields.Length == 0)
                return;

            foreach (FieldInfo f in fields)
            {
                if (f.Name != "Data")
                    _Fields[f.Name] = f;
                else
                    _DataField = f;
            }

        }

        private void LoadFieldTypes()
        {
            _FieldTypes = new Dictionary<string, string>();

            string query = string.Format(@"select column_name,data_type
                        from INFORMATION_SCHEMA.COLUMNS
                       where table_name = lower('{0}');

                ", _Realm);
            using (NpgsqlConnection conn = new NpgsqlConnection(_ConnectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(query, conn))
            {
                conn.Open();
                using (NpgsqlDataReader rdr = cmd.ExecuteReader())
                {
                    while (rdr.Read())
                    {
                        // query produces 0 to many rows of single column, so always add the first item in each row
                        _FieldTypes.Add((string)rdr[0], (string)rdr[1]);
                    }
                }
            }
        }

        private void CheckColumnNames(NpgsqlDataReader reader)
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

        // TODO GET CONSTRAINTS FROM POSTGRESQL
        private List<string> GetConstraints()
        {
            List<string> constraints = new List<string>();
            string query = string.Format(@"select
                    a.attname as column_name
                from
                    pg_class t,
                    pg_class i,
                    pg_index ix,
                    pg_attribute a
                where
                    t.oid = ix.indrelid
                    and i.oid = ix.indexrelid
                    and a.attrelid = t.oid
                    and a.attnum = ANY(ix.indkey)
                    and t.relkind = 'r'
                    and ix.indisunique = true
                    and t.relname = lower('{0}')
            ;", _Realm);

            using (NpgsqlConnection conn = new NpgsqlConnection(_ConnectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(query, conn))
            {
                conn.Open();
                using (NpgsqlDataReader rdr = cmd.ExecuteReader())
                {
                    while (rdr.Read())
                    {
                        // query produces 0 to many rows of single column, so always add the first item in each row
                        constraints.Add((string)rdr[0]);
                    }
                }
                return constraints;
            }
        }

        public virtual T[] Get(string field, string key)
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(_ConnectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand())
            {
                if ( _FieldTypes.ContainsKey(field) )
                    cmd.Parameters.Add(_database.CreateParameter(field, key, _FieldTypes[field]));
                else
                    cmd.Parameters.Add(_database.CreateParameter(field, key));

                string query = string.Format("SELECT * FROM {0} WHERE \"{1}\" = :{1}", _Realm, field, field);

                cmd.Connection = conn;
                cmd.CommandText = query;
                conn.Open();
                return DoQuery(cmd);
            }
        }

        public virtual T[] Get(string field, string[] keys)
        {

            int flen = keys.Length;
            if(flen == 0)
                return new T[0];

            int flast = flen - 1;
            StringBuilder sb = new StringBuilder(1024);
            sb.AppendFormat("select * from {0} where {1} IN ('", _Realm, field);

            using (NpgsqlConnection conn = new NpgsqlConnection(_ConnectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand())
            {

                for (int i = 0 ; i < flen ; i++)
                {
                    sb.Append(keys[i]);
                    if(i < flast)
                        sb.Append("','");
                    else
                        sb.Append("')");
                }

                string query = sb.ToString();

                cmd.Connection = conn;
                cmd.CommandText = query;
                conn.Open();
                return DoQuery(cmd);
            }
        }

        public virtual T[] Get(string[] fields, string[] keys)
        {
            if (fields.Length != keys.Length)
                return new T[0];

            List<string> terms = new List<string>();

            using (NpgsqlConnection conn = new NpgsqlConnection(_ConnectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand())
            {

                for (int i = 0; i < fields.Length; i++)
                {
                    if ( _FieldTypes.ContainsKey(fields[i]) )
                        cmd.Parameters.Add(_database.CreateParameter(fields[i], keys[i], _FieldTypes[fields[i]]));
                    else
                        cmd.Parameters.Add(_database.CreateParameter(fields[i], keys[i]));

                    terms.Add(" \"" + fields[i] + "\" = :" + fields[i]);
                }

                string where = string.Join(" AND ", terms.ToArray());

                string query = string.Format("SELECT * FROM {0} WHERE {1}",
                        _Realm, where);

                cmd.Connection = conn;
                cmd.CommandText = query;
                conn.Open();
                return DoQuery(cmd);
            }
        }

        protected T[] DoQuery(NpgsqlCommand cmd)
        {
            List<T> result = new List<T>();
            if (cmd.Connection == null)
            {
                cmd.Connection = new NpgsqlConnection(_connectionString);
            }
            if (cmd.Connection.State == ConnectionState.Closed)
            {
                cmd.Connection.Open();
            }
            using (NpgsqlDataReader reader = cmd.ExecuteReader())
            {
                if (reader == null)
                    return new T[0];

                CheckColumnNames(reader);

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
                return result.ToArray();
            }
        }

        public virtual T[] Get(string where)
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(_ConnectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand())
            {

                string query = string.Format("SELECT * FROM {0} WHERE {1}",
                        _Realm, where);
                cmd.Connection = conn;
                cmd.CommandText = query;
                //_log.WarnFormat("[PGSQLGenericTable]: SELECT {0} WHERE {1}", _Realm, where);

                conn.Open();
                return DoQuery(cmd);
            }
        }

        public virtual T[] Get(string where, NpgsqlParameter parameter)
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(_ConnectionString))
                using (NpgsqlCommand cmd = new NpgsqlCommand())
            {

                string query = string.Format("SELECT * FROM {0} WHERE {1}",
                                             _Realm, where);
                cmd.Connection = conn;
                cmd.CommandText = query;
                //_log.WarnFormat("[PGSQLGenericTable]: SELECT {0} WHERE {1}", _Realm, where);

                cmd.Parameters.Add(parameter);

                conn.Open();
                return DoQuery(cmd);
            }
        }

        public virtual bool Store(T row)
        {
            List<string> constraintFields = GetConstraints();
            List<KeyValuePair<string, string>> constraints = new List<KeyValuePair<string, string>>();

            using (NpgsqlConnection conn = new NpgsqlConnection(_ConnectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand())
            {

                StringBuilder query = new StringBuilder();
                List<string> names = new List<string>();
                List<string> values = new List<string>();

                foreach (FieldInfo fi in _Fields.Values)
                {
                    names.Add(fi.Name);
                    values.Add(":" + fi.Name);
                    // Temporarily return more information about what field is unexpectedly null for
                    // http://opensimulator.org/mantis/view.php?id=5403.  This might be due to a bug in the
                    // InventoryTransferModule or we may be required to substitute a DBNull here.
                    if (fi.GetValue(row) == null)
                        throw new NullReferenceException(
                            string.Format(
                                "[PGSQL GENERIC TABLE HANDLER]: Trying to store field {0} for {1} which is unexpectedly null",
                                fi.Name, row));

                    if (constraintFields.Count > 0 && constraintFields.Contains(fi.Name))
                    {
                        constraints.Add(new KeyValuePair<string, string>(fi.Name, fi.GetValue(row).ToString() ));
                    }
                    if (_FieldTypes.ContainsKey(fi.Name))
                        cmd.Parameters.Add(_database.CreateParameter(fi.Name, fi.GetValue(row), _FieldTypes[fi.Name]));
                    else
                        cmd.Parameters.Add(_database.CreateParameter(fi.Name, fi.GetValue(row)));
                }

                if (_DataField != null)
                {
                    Dictionary<string, string> data =
                            (Dictionary<string, string>)_DataField.GetValue(row);

                    foreach (KeyValuePair<string, string> kvp in data)
                    {
                        if (constraintFields.Count > 0 && constraintFields.Contains(kvp.Key))
                        {
                            constraints.Add(new KeyValuePair<string, string>(kvp.Key, kvp.Key));
                        }
                        names.Add(kvp.Key);
                        values.Add(":" + kvp.Key);

                        if (_FieldTypes.ContainsKey(kvp.Key))
                            cmd.Parameters.Add(_database.CreateParameter("" + kvp.Key, kvp.Value, _FieldTypes[kvp.Key]));
                        else
                            cmd.Parameters.Add(_database.CreateParameter("" + kvp.Key, kvp.Value));
                    }

                }

                query.AppendFormat("UPDATE {0} SET ", _Realm);
                int i = 0;
                for (i = 0; i < names.Count - 1; i++)
                {
                    query.AppendFormat("\"{0}\" = {1}, ", names[i], values[i]);
                }
                query.AppendFormat("\"{0}\" = {1} ", names[i], values[i]);
                if (constraints.Count > 0)
                {
                    List<string> terms = new List<string>();
                    for (int j = 0; j < constraints.Count; j++)
                    {
                        terms.Add(string.Format(" \"{0}\" = :{0}", constraints[j].Key));
                    }
                    string where = string.Join(" AND ", terms.ToArray());
                    query.AppendFormat(" WHERE {0} ", where);

                }
                cmd.Connection = conn;
                cmd.CommandText = query.ToString();

                conn.Open();
                if (cmd.ExecuteNonQuery() > 0)
                {
                    //_log.WarnFormat("[PGSQLGenericTable]: Updating {0}", _Realm);
                    return true;
                }
                else
                {
                    // assume record has not yet been inserted

                    query = new StringBuilder();
                    query.AppendFormat("INSERT INTO {0} (\"", _Realm);
                    query.Append(string.Join("\",\"", names.ToArray()));
                    query.Append("\") values (" + string.Join(",", values.ToArray()) + ")");
                    cmd.Connection = conn;
                    cmd.CommandText = query.ToString();

                    // _log.WarnFormat("[PGSQLGenericTable]: Inserting into {0} sql {1}", _Realm, cmd.CommandText);

                    if (conn.State != ConnectionState.Open)
                        conn.Open();
                    if (cmd.ExecuteNonQuery() > 0)
                        return true;
                }

                return false;
            }
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

            using (NpgsqlConnection conn = new NpgsqlConnection(_ConnectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand())
            {
                for (int i = 0; i < fields.Length; i++)
                {
                    if (_FieldTypes.ContainsKey(fields[i]))
                        cmd.Parameters.Add(_database.CreateParameter(fields[i], keys[i], _FieldTypes[fields[i]]));
                    else
                        cmd.Parameters.Add(_database.CreateParameter(fields[i], keys[i]));

                    terms.Add(" \"" + fields[i] + "\" = :" + fields[i]);
                }

                string where = string.Join(" AND ", terms.ToArray());

                string query = string.Format("DELETE FROM {0} WHERE {1}", _Realm, where);

                cmd.Connection = conn;
                cmd.CommandText = query;
                conn.Open();

                if (cmd.ExecuteNonQuery() > 0)
                {
                    //_log.Warn("[PGSQLGenericTable]: " + deleteCommand);
                    return true;
                }
                return false;
            }
        }
        public long GetCount(string field, string key)
        {
            return GetCount(new string[] { field }, new string[] { key });
        }

        public long GetCount(string[] fields, string[] keys)
        {
            if (fields.Length != keys.Length)
                return 0;

            List<string> terms = new List<string>();

            using (NpgsqlCommand cmd = new NpgsqlCommand())
            {
                for (int i = 0; i < fields.Length; i++)
                {
                    cmd.Parameters.AddWithValue(fields[i], keys[i]);
                    terms.Add("\"" + fields[i] + "\" = :" + fields[i]);
                }

                string where = string.Join(" and ", terms.ToArray());

                string query = string.Format("select count(*) from {0} where {1}",
                                             _Realm, where);

                cmd.CommandText = query;

                object result = DoQueryScalar(cmd);

                return Convert.ToInt64(result);
            }
        }

        public long GetCount(string where)
        {
            using (NpgsqlCommand cmd = new NpgsqlCommand())
            {
                string query = string.Format("select count(*) from {0} where {1}",
                                             _Realm, where);

                cmd.CommandText = query;

                object result = DoQueryScalar(cmd);

                return Convert.ToInt64(result);
            }
        }

        public object DoQueryScalar(NpgsqlCommand cmd)
        {
            using (NpgsqlConnection dbcon = new NpgsqlConnection(_ConnectionString))
            {
                dbcon.Open();
                cmd.Connection = dbcon;

                return cmd.ExecuteScalar();
            }
        }
    }
}
