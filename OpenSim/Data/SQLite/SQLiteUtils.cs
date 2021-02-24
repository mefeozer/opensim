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
using System.Data;
#if CSharpSqlite
    using Community.CsharpSqlite.Sqlite;
#else
    using Mono.Data.Sqlite;
#endif

namespace OpenSim.Data.SQLite
{
    /// <summary>
    /// A base class for methods needed by all SQLite database classes
    /// </summary>
    public class SQLiteUtil
    {
        /***********************************************************************
         *
         *  Database Definition Helper Functions
         *
         *  This should be db agnostic as we define them in ADO.NET terms
         *
         **********************************************************************/

        /// <summary>
        ///
        /// </summary>
        /// <param name="dt"></param>
        /// <param name="name"></param>
        /// <param name="type"></param>
        public static void createCol(DataTable dt, string name, Type type)
        {
            DataColumn col = new DataColumn(name, type);
            dt.Columns.Add(col);
        }

        /***********************************************************************
         *
         *  SQL Statement Creation Functions
         *
         *  These functions create SQL statements for update, insert, and create.
         *  They can probably be factored later to have a db independant
         *  portion and a db specific portion
         *
         **********************************************************************/

        /// <summary>
        /// Create an insert command
        /// </summary>
        /// <param name="table">table name</param>
        /// <param name="dt">data table</param>
        /// <returns>the created command</returns>
        /// <remarks>
        /// This is subtle enough to deserve some commentary.
        /// Instead of doing *lots* and *lots of hardcoded strings
        /// for database definitions we'll use the fact that
        /// realistically all insert statements look like "insert
        /// into A(b, c) values(:b, :c) on the parameterized query
        /// front.  If we just have a list of b, c, etc... we can
        /// generate these strings instead of typing them out.
        /// </remarks>
        public static SqliteCommand createInsertCommand(string table, DataTable dt)
        {

            string[] cols = new string[dt.Columns.Count];
            for (int i = 0; i < dt.Columns.Count; i++)
            {
                DataColumn col = dt.Columns[i];
                cols[i] = col.ColumnName;
            }

            string sql = "insert into " + table + "(";
            sql += string.Join(", ", cols);
            // important, the first ':' needs to be here, the rest get added in the join
            sql += ") values (:";
            sql += string.Join(", :", cols);
            sql += ")";
            SqliteCommand cmd = new SqliteCommand(sql);

            // this provides the binding for all our parameters, so
            // much less code than it used to be
            foreach (DataColumn col in dt.Columns)
            {
                cmd.Parameters.Add(createSqliteParameter(col.ColumnName, col.DataType));
            }
            return cmd;
        }

        /// <summary>
        /// create an update command
        /// </summary>
        /// <param name="table">table name</param>
        /// <param name="pk"></param>
        /// <param name="dt"></param>
        /// <returns>the created command</returns>
        public static SqliteCommand createUpdateCommand(string table, string pk, DataTable dt)
        {
            string sql = "update " + table + " set ";
            string subsql = string.Empty;
            foreach (DataColumn col in dt.Columns)
            {
                if (subsql.Length > 0)
                {
                    // a map function would rock so much here
                    subsql += ", ";
                }
                subsql += col.ColumnName + "= :" + col.ColumnName;
            }
            sql += subsql;
            sql += " where " + pk;
            SqliteCommand cmd = new SqliteCommand(sql);

            // this provides the binding for all our parameters, so
            // much less code than it used to be

            foreach (DataColumn col in dt.Columns)
            {
                cmd.Parameters.Add(createSqliteParameter(col.ColumnName, col.DataType));
            }
            return cmd;
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="dt">Data Table</param>
        /// <returns></returns>
        public static string defineTable(DataTable dt)
        {
            string sql = "create table " + dt.TableName + "(";
            string subsql = string.Empty;
            foreach (DataColumn col in dt.Columns)
            {
                if (subsql.Length > 0)
                {
                    // a map function would rock so much here
                    subsql += ",\n";
                }
                subsql += col.ColumnName + " " + sqliteType(col.DataType);
                if (dt.PrimaryKey.Length > 0)
                {
                    if (col == dt.PrimaryKey[0])
                    {
                        subsql += " primary key";
                    }
                }
            }
            sql += subsql;
            sql += ")";
            return sql;
        }

        /***********************************************************************
         *
         *  Database Binding functions
         *
         *  These will be db specific due to typing, and minor differences
         *  in databases.
         *
         **********************************************************************/

        ///<summary>
        /// <para>
        /// This is a convenience function that collapses 5 repetitive
        /// lines for defining SqliteParameters to 2 parameters:
        /// column name and database type.
        /// </para>
        ///
        /// <para>
        /// It assumes certain conventions like :param as the param
        /// name to replace in parametrized queries, and that source
        /// version is always current version, both of which are fine
        /// for us.
        /// </para>
        ///</summary>
        /// <param name="name"></param>
        /// <param name="type"></param>
        ///<returns>a built sqlite parameter</returns>
        public static SqliteParameter createSqliteParameter(string name, Type type)
        {
            SqliteParameter param = new SqliteParameter
            {
                ParameterName = ":" + name,
                DbType = dbtypeFromType(type),
                SourceColumn = name,
                SourceVersion = DataRowVersion.Current
            };
            return param;
        }

        /***********************************************************************
         *
         *  Type conversion functions
         *
         **********************************************************************/

        /// <summary>
        /// Type conversion function
        /// </summary>
        /// <param name="type">a type</param>
        /// <returns>a DbType</returns>
        public static DbType dbtypeFromType(Type type)
        {
            if (type == typeof (string))
            {
                return DbType.String;
            }
            else if (type == typeof (int))
            {
                return DbType.Int32;
            }
            else if (type == typeof (uint))
            {
                return DbType.UInt32;
            }
            else if (type == typeof (long))
            {
                return DbType.Int64;
            }
            else if (type == typeof (ulong))
            {
                return DbType.UInt64;
            }
            else if (type == typeof (double))
            {
                return DbType.Double;
            }
            else if (type == typeof (bool))
            {
                return DbType.Boolean;
            }
            else if (type == typeof (byte[]))
            {
                return DbType.Binary;
            }
            else
            {
                return DbType.String;
            }
        }

        /// <summary>
        /// </summary>
        /// <param name="type">a Type</param>
        /// <returns>a string</returns>
        /// <remarks>this is something we'll need to implement for each db slightly differently.</remarks>
        public static string sqliteType(Type type)
        {
            if (type == typeof (string))
            {
                return "varchar(255)";
            }
            else if (type == typeof (int))
            {
                return "integer";
            }
            else if (type == typeof (uint))
            {
                return "integer";
            }
            else if (type == typeof (long))
            {
                return "varchar(255)";
            }
            else if (type == typeof (ulong))
            {
                return "varchar(255)";
            }
            else if (type == typeof (double))
            {
                return "float";
            }
            else if (type == typeof (bool))
            {
                return "integer";
            }
            else if (type == typeof (byte[]))
            {
                return "blob";
            }
            else
            {
                return "string";
            }
        }
    }
}
