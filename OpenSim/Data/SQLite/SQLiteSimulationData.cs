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
using System.Drawing;
using System.Reflection;
using log4net;
#if CSharpSqlite
    using Community.CsharpSqlite.Sqlite;
#else
using Mono.Data.Sqlite;
#endif
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

namespace OpenSim.Data.SQLite
{
    /// <summary>
    /// A RegionData Interface to the SQLite database
    /// </summary>
    public class SQLiteSimulationData : ISimulationDataStore
    {
        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly string LogHeader = "[REGION DB SQLITE]";

        private const string primSelect = "select * from prims";
        private const string shapeSelect = "select * from primshapes";
        private const string itemsSelect = "select * from primitems";
        private const string terrainSelect = "select * from terrain limit 1";
        private const string landSelect = "select * from land";
        private const string landAccessListSelect = "select distinct * from landaccesslist";
        private const string regionbanListSelect = "select * from regionban";
        private const string regionSettingsSelect = "select * from regionsettings";
        private const string regionWindlightSelect = "select * from regionwindlight";
        private const string regionEnvironmentSelect = "select * from regionenvironment";
        private const string regionSpawnPointsSelect = "select * from spawn_points";

        private DataSet ds;
        private SqliteDataAdapter primDa;
        private SqliteDataAdapter shapeDa;
        private SqliteDataAdapter itemsDa;
        private SqliteDataAdapter terrainDa;
        private SqliteDataAdapter landDa;
        private SqliteDataAdapter landAccessListDa;
        private SqliteDataAdapter regionSettingsDa;
        private SqliteDataAdapter regionWindlightDa;
        private SqliteDataAdapter regionEnvironmentDa;
        private SqliteDataAdapter regionSpawnPointsDa;

        private SqliteConnection _conn;
        private string _connectionString;

        protected virtual Assembly Assembly => GetType().Assembly;

        public SQLiteSimulationData()
        {
        }

        public SQLiteSimulationData(string connectionString)
        {
            Initialise(connectionString);
        }

        // Temporary attribute while this is experimental

        /***********************************************************************
         *
         *  Public Interface Functions
         *
         **********************************************************************/

        /// <summary>
        /// <list type="bullet">
        /// <item>Initialises RegionData Interface</item>
        /// <item>Loads and initialises a new SQLite connection and maintains it.</item>
        /// </list>
        /// </summary>
        /// <param name="connectionString">the connection string</param>
        public void Initialise(string connectionString)
        {
            try
            {
                if (Util.IsWindows())
                    Util.LoadArchSpecificWindowsDll("sqlite3.dll");

                _connectionString = connectionString;

                ds = new DataSet("Region");

                _log.Info("[SQLITE REGION DB]: Sqlite - connecting: " + connectionString);
                _conn = new SqliteConnection(_connectionString);
                _conn.Open();

                SqliteCommand primSelectCmd = new SqliteCommand(primSelect, _conn);
                primDa = new SqliteDataAdapter(primSelectCmd);

                SqliteCommand shapeSelectCmd = new SqliteCommand(shapeSelect, _conn);
                shapeDa = new SqliteDataAdapter(shapeSelectCmd);
                // SqliteCommandBuilder shapeCb = new SqliteCommandBuilder(shapeDa);

                SqliteCommand itemsSelectCmd = new SqliteCommand(itemsSelect, _conn);
                itemsDa = new SqliteDataAdapter(itemsSelectCmd);

                SqliteCommand terrainSelectCmd = new SqliteCommand(terrainSelect, _conn);
                terrainDa = new SqliteDataAdapter(terrainSelectCmd);

                SqliteCommand landSelectCmd = new SqliteCommand(landSelect, _conn);
                landDa = new SqliteDataAdapter(landSelectCmd);

                SqliteCommand landAccessListSelectCmd = new SqliteCommand(landAccessListSelect, _conn);
                landAccessListDa = new SqliteDataAdapter(landAccessListSelectCmd);

                SqliteCommand regionSettingsSelectCmd = new SqliteCommand(regionSettingsSelect, _conn);
                regionSettingsDa = new SqliteDataAdapter(regionSettingsSelectCmd);

                SqliteCommand regionWindlightSelectCmd = new SqliteCommand(regionWindlightSelect, _conn);
                regionWindlightDa = new SqliteDataAdapter(regionWindlightSelectCmd);

                SqliteCommand regionEnvironmentSelectCmd = new SqliteCommand(regionEnvironmentSelect, _conn);
                regionEnvironmentDa = new SqliteDataAdapter(regionEnvironmentSelectCmd);

                SqliteCommand regionSpawnPointsSelectCmd = new SqliteCommand(regionSpawnPointsSelect, _conn);
                regionSpawnPointsDa = new SqliteDataAdapter(regionSpawnPointsSelectCmd);

                // This actually does the roll forward assembly stuff
                Migration m = new Migration(_conn, Assembly, "RegionStore");
                m.Update();

                lock (ds)
                {
                    ds.Tables.Add(createPrimTable());
                    setupPrimCommands(primDa, _conn);

                    ds.Tables.Add(createShapeTable());
                    setupShapeCommands(shapeDa, _conn);

                    ds.Tables.Add(createItemsTable());
                    setupItemsCommands(itemsDa, _conn);

                    ds.Tables.Add(createTerrainTable());
                    setupTerrainCommands(terrainDa, _conn);

                    ds.Tables.Add(createLandTable());
                    setupLandCommands(landDa, _conn);

                    ds.Tables.Add(createLandAccessListTable());
                    setupLandAccessCommands(landAccessListDa, _conn);

                    ds.Tables.Add(createRegionSettingsTable());
                    setupRegionSettingsCommands(regionSettingsDa, _conn);

                    ds.Tables.Add(createRegionWindlightTable());
                    setupRegionWindlightCommands(regionWindlightDa, _conn);

                    ds.Tables.Add(createRegionEnvironmentTable());
                    setupRegionEnvironmentCommands(regionEnvironmentDa, _conn);

                    ds.Tables.Add(createRegionSpawnPointsTable());
                    setupRegionSpawnPointsCommands(regionSpawnPointsDa, _conn);

                    // WORKAROUND: This is a work around for sqlite on
                    // windows, which gets really unhappy with blob columns
                    // that have no sample data in them.  At some point we
                    // need to actually find a proper way to handle this.
                    try
                    {
                        primDa.Fill(ds.Tables["prims"]);
                    }
                    catch (Exception e)
                    {
                        _log.ErrorFormat("[SQLITE REGION DB]: Caught fill error on prims table :{0}", e.Message);
                    }

                    try
                    {
                        shapeDa.Fill(ds.Tables["primshapes"]);
                    }
                    catch (Exception e)
                    {
                        _log.ErrorFormat("[SQLITE REGION DB]: Caught fill error on primshapes table :{0}", e.Message);
                    }

                    try
                    {
                        itemsDa.Fill(ds.Tables["primitems"]);
                    }
                    catch (Exception e)
                    {
                        _log.ErrorFormat("[SQLITE REGION DB]: Caught fill error on primitems table :{0}", e.Message);
                    }

                    try
                    {
                        terrainDa.Fill(ds.Tables["terrain"]);
                    }
                    catch (Exception e)
                    {
                        _log.ErrorFormat("[SQLITE REGION DB]: Caught fill error on terrain table :{0}", e.Message);
                    }

                    try
                    {
                        landDa.Fill(ds.Tables["land"]);
                    }
                    catch (Exception e)
                    {
                        _log.ErrorFormat("[SQLITE REGION DB]: Caught fill error on land table :{0}", e.Message);
                    }

                    try
                    {
                        landAccessListDa.Fill(ds.Tables["landaccesslist"]);
                    }
                    catch (Exception e)
                    {
                        _log.ErrorFormat("[SQLITE REGION DB]: Caught fill error on landaccesslist table :{0}", e.Message);
                    }

                    try
                    {
                        regionSettingsDa.Fill(ds.Tables["regionsettings"]);
                    }
                    catch (Exception e)
                    {
                        _log.ErrorFormat("[SQLITE REGION DB]: Caught fill error on regionsettings table :{0}", e.Message);
                    }

                    try
                    {
                        regionWindlightDa.Fill(ds.Tables["regionwindlight"]);
                    }
                    catch (Exception e)
                    {
                        _log.ErrorFormat("[SQLITE REGION DB]: Caught fill error on regionwindlight table :{0}", e.Message);
                    }

                    try
                    {
                        regionEnvironmentDa.Fill(ds.Tables["regionenvironment"]);
                    }
                    catch (Exception e)
                    {
                        _log.ErrorFormat("[SQLITE REGION DB]: Caught fill error on regionenvironment table :{0}", e.Message);
                    }

                    try
                    {
                        regionSpawnPointsDa.Fill(ds.Tables["spawn_points"]);
                    }
                    catch (Exception e)
                    {
                        _log.ErrorFormat("[SQLITE REGION DB]: Caught fill error on spawn_points table :{0}", e.Message);
                    }

                    // We have to create a data set mapping for every table, otherwise the IDataAdaptor.Update() will not populate rows with values!
                    // Not sure exactly why this is - this kind of thing was not necessary before - justincc 20100409
                    // Possibly because we manually set up our own DataTables before connecting to the database
                    CreateDataSetMapping(primDa, "prims");
                    CreateDataSetMapping(shapeDa, "primshapes");
                    CreateDataSetMapping(itemsDa, "primitems");
                    CreateDataSetMapping(terrainDa, "terrain");
                    CreateDataSetMapping(landDa, "land");
                    CreateDataSetMapping(landAccessListDa, "landaccesslist");
                    CreateDataSetMapping(regionSettingsDa, "regionsettings");
                    CreateDataSetMapping(regionWindlightDa, "regionwindlight");
                    CreateDataSetMapping(regionEnvironmentDa, "regionenvironment");
                    CreateDataSetMapping(regionSpawnPointsDa, "spawn_points");
                }
            }
            catch (Exception e)
            {
                _log.ErrorFormat("[SQLITE REGION DB]: {0} - {1}", e.Message, e.StackTrace);
                Environment.Exit(23);
            }
            return;
        }

        public void Dispose()
        {
            if (_conn != null)
            {
                _conn.Close();
                _conn = null;
            }
            if (ds != null)
            {
                ds.Dispose();
                ds = null;
            }
            if (primDa != null)
            {
                primDa.Dispose();
                primDa = null;
            }
            if (shapeDa != null)
            {
                shapeDa.Dispose();
                shapeDa = null;
            }
            if (itemsDa != null)
            {
                itemsDa.Dispose();
                itemsDa = null;
            }
            if (terrainDa != null)
            {
                terrainDa.Dispose();
                terrainDa = null;
            }
            if (landDa != null)
            {
                landDa.Dispose();
                landDa = null;
            }
            if (landAccessListDa != null)
            {
                landAccessListDa.Dispose();
                landAccessListDa = null;
            }
            if (regionSettingsDa != null)
            {
                regionSettingsDa.Dispose();
                regionSettingsDa = null;
            }
            if (regionWindlightDa != null)
            {
                regionWindlightDa.Dispose();
                regionWindlightDa = null;
            }
            if (regionEnvironmentDa != null)
            {
                regionEnvironmentDa.Dispose();
                regionEnvironmentDa = null;
            }
            if (regionSpawnPointsDa != null)
            {
                regionSpawnPointsDa.Dispose();
                regionWindlightDa = null;
            }
        }

        public void StoreRegionSettings(RegionSettings rs)
        {
            lock (ds)
            {
                DataTable regionsettings = ds.Tables["regionsettings"];

                DataRow settingsRow = regionsettings.Rows.Find(rs.RegionUUID.ToString());
                if (settingsRow == null)
                {
                    settingsRow = regionsettings.NewRow();
                    fillRegionSettingsRow(settingsRow, rs);
                    regionsettings.Rows.Add(settingsRow);
                }
                else
                {
                    fillRegionSettingsRow(settingsRow, rs);
                }

                StoreSpawnPoints(rs);

                Commit();
            }

        }

        public void StoreSpawnPoints(RegionSettings rs)
        {
            lock (ds)
            {
                // DataTable spawnpoints = ds.Tables["spawn_points"];

                // remove region's spawnpoints
                using (
                    SqliteCommand cmd =
                        new SqliteCommand("delete from spawn_points where RegionID=:RegionID",
                                          _conn))
                {

                    cmd.Parameters.Add(new SqliteParameter(":RegionID", rs.RegionUUID.ToString()));
                    cmd.ExecuteNonQuery();
                }
            }

            foreach (SpawnPoint sp in rs.SpawnPoints())
            {
                using (SqliteCommand cmd = new SqliteCommand("insert into spawn_points(RegionID, Yaw, Pitch, Distance)" +
                                                              "values ( :RegionID, :Yaw, :Pitch, :Distance)", _conn))
                {
                    cmd.Parameters.Add(new SqliteParameter(":RegionID", rs.RegionUUID.ToString()));
                    cmd.Parameters.Add(new SqliteParameter(":Yaw", sp.Yaw));
                    cmd.Parameters.Add(new SqliteParameter(":Pitch", sp.Pitch));
                    cmd.Parameters.Add(new SqliteParameter(":Distance", sp.Distance));
                    cmd.ExecuteNonQuery();
                }
            }
        }

        #region Region Environment Settings
        public string LoadRegionEnvironmentSettings(UUID regionUUID)
        {
            lock (ds)
            {
                DataTable environmentTable = ds.Tables["regionenvironment"];
                DataRow row = environmentTable.Rows.Find(regionUUID.ToString());
                if (row == null)
                {
                    return string.Empty;
                }

                return (string)row["llsd_settings"];
            }
        }

        public void StoreRegionEnvironmentSettings(UUID regionUUID, string settings)
        {
            lock (ds)
            {
                DataTable environmentTable = ds.Tables["regionenvironment"];
                DataRow row = environmentTable.Rows.Find(regionUUID.ToString());

                if (row == null)
                {
                    row = environmentTable.NewRow();
                    row["region_id"] = regionUUID.ToString();
                    row["llsd_settings"] = settings;
                    environmentTable.Rows.Add(row);
                }
                else
                {
                    row["llsd_settings"] = settings;
                }

                regionEnvironmentDa.Update(ds, "regionenvironment");
            }
        }

        public void RemoveRegionEnvironmentSettings(UUID regionUUID)
        {
            lock (ds)
            {
                DataTable environmentTable = ds.Tables["regionenvironment"];
                DataRow row = environmentTable.Rows.Find(regionUUID.ToString());

                if (row != null)
                {
                    row.Delete();
                }

                regionEnvironmentDa.Update(ds, "regionenvironment");
            }
        }

        #endregion

        public RegionSettings LoadRegionSettings(UUID regionUUID)
        {
            lock (ds)
            {
                DataTable regionsettings = ds.Tables["regionsettings"];

                string searchExp = "regionUUID = '" + regionUUID.ToString() + "'";
                DataRow[] rawsettings = regionsettings.Select(searchExp);
                if (rawsettings.Length == 0)
                {
                    RegionSettings rs = new RegionSettings
                    {
                        RegionUUID = regionUUID
                    };
                    rs.OnSave += StoreRegionSettings;

                    StoreRegionSettings(rs);

                    return rs;
                }
                DataRow row = rawsettings[0];

                RegionSettings newSettings = buildRegionSettings(row);
                newSettings.OnSave += StoreRegionSettings;

                LoadSpawnPoints(newSettings);

                return newSettings;
            }
        }

        private void LoadSpawnPoints(RegionSettings rs)
        {
            rs.ClearSpawnPoints();

            DataTable spawnpoints = ds.Tables["spawn_points"];
            string byRegion = "RegionID = '" + rs.RegionUUID + "'";
            DataRow[] spForRegion = spawnpoints.Select(byRegion);

            foreach (DataRow spRow in spForRegion)
            {
                SpawnPoint sp = new SpawnPoint
                {
                    Pitch = (float)spRow["Pitch"],
                    Yaw = (float)spRow["Yaw"],
                    Distance = (float)spRow["Distance"]
                };

                rs.AddSpawnPoint(sp);
            }
        }

        /// <summary>
        /// Adds an object into region storage
        /// </summary>
        /// <param name="obj">the object</param>
        /// <param name="regionUUID">the region UUID</param>
        public void StoreObject(SceneObjectGroup obj, UUID regionUUID)
        {
            uint flags = obj.RootPart.GetEffectiveObjectFlags();

            // Eligibility check
            //
            if ((flags & (uint)PrimFlags.Temporary) != 0)
                return;
            if ((flags & (uint)PrimFlags.TemporaryOnRez) != 0)
                return;

            lock (ds)
            {
                foreach (SceneObjectPart prim in obj.Parts)
                {
//                    _log.Info("[REGION DB]: Adding obj: " + obj.UUID + " to region: " + regionUUID);
                    addPrim(prim, obj.UUID, regionUUID);
                }
                primDa.Update(ds, "prims");
                shapeDa.Update(ds, "primshapes");
                itemsDa.Update(ds, "primitems");
                ds.AcceptChanges();
            }

            // _log.Info("[Dump of prims]: " + ds.GetXml());
        }

        /// <summary>
        /// Removes an object from region storage
        /// </summary>
        /// <param name="obj">the object</param>
        /// <param name="regionUUID">the region UUID</param>
        public void RemoveObject(UUID obj, UUID regionUUID)
        {
//            _log.InfoFormat("[REGION DB]: Removing obj: {0} from region: {1}", obj.Guid, regionUUID);

            DataTable prims = ds.Tables["prims"];
            DataTable shapes = ds.Tables["primshapes"];

            string selectExp = "SceneGroupID = '" + obj + "' and RegionUUID = '" + regionUUID + "'";
            lock (ds)
            {
                DataRow[] primRows = prims.Select(selectExp);
                foreach (DataRow row in primRows)
                {
                    // Remove shape rows
                    UUID uuid = new UUID((string)row["UUID"]);
                    DataRow shapeRow = shapes.Rows.Find(uuid.ToString());
                    if (shapeRow != null)
                    {
                        shapeRow.Delete();
                    }

                    RemoveItems(uuid);

                    // Remove prim row
                    row.Delete();
                }
                Commit();
            }
        }

        /// <summary>
        /// Remove all persisted items of the given prim.
        /// The caller must acquire the necessrary synchronization locks and commit or rollback changes.
        /// </summary>
        /// <param name="uuid">The item UUID</param>
        private void RemoveItems(UUID uuid)
        {
            DataTable items = ds.Tables["primitems"];

            string sql = string.Format("primID = '{0}'", uuid);
            DataRow[] itemRows = items.Select(sql);

            foreach (DataRow itemRow in itemRows)
            {
                itemRow.Delete();
            }
        }

        /// <summary>
        /// Load persisted objects from region storage.
        /// </summary>
        /// <param name="regionUUID">The region UUID</param>
        /// <returns>List of loaded groups</returns>
        public List<SceneObjectGroup> LoadObjects(UUID regionUUID)
        {
            Dictionary<UUID, SceneObjectGroup> createdObjects = new Dictionary<UUID, SceneObjectGroup>();

            List<SceneObjectGroup> retvals = new List<SceneObjectGroup>();

            DataTable prims = ds.Tables["prims"];
            DataTable shapes = ds.Tables["primshapes"];

            string byRegion = "RegionUUID = '" + regionUUID + "'";

            lock (ds)
            {
                DataRow[] primsForRegion = prims.Select(byRegion);
//                _log.Info("[SQLITE REGION DB]: Loaded " + primsForRegion.Length + " prims for region: " + regionUUID);

                // First, create all groups
                foreach (DataRow primRow in primsForRegion)
                {
                    try
                    {
                        SceneObjectPart prim = null;

                        string uuid = (string)primRow["UUID"];
                        string objID = (string)primRow["SceneGroupID"];

                        if (uuid == objID) //is new SceneObjectGroup ?
                        {
                            prim = buildPrim(primRow);
                            DataRow shapeRow = shapes.Rows.Find(prim.UUID.ToString());
                            if (shapeRow != null)
                            {
                                prim.Shape = buildShape(shapeRow);
                            }
                            else
                            {
                                _log.Warn(
                                    "[SQLITE REGION DB]: No shape found for prim in storage, so setting default box shape");
                                prim.Shape = PrimitiveBaseShape.Default;
                            }

                            SceneObjectGroup group = new SceneObjectGroup(prim);

                            createdObjects.Add(group.UUID, group);
                            retvals.Add(group);
                            LoadItems(prim);


                        }
                    }
                    catch (Exception e)
                    {
                        _log.Error("[SQLITE REGION DB]: Failed create prim object in new group, exception and data follows");
                        _log.Error("[SQLITE REGION DB]: ", e);
                        foreach (DataColumn col in prims.Columns)
                        {
                            _log.Error("[SQLITE REGION DB]: Col: " + col.ColumnName + " => " + primRow[col]);
                        }
                    }
                }

                // Now fill the groups with part data
                foreach (DataRow primRow in primsForRegion)
                {
                    try
                    {
                        SceneObjectPart prim = null;

                        string uuid = (string)primRow["UUID"];
                        string objID = (string)primRow["SceneGroupID"];
                        if (uuid != objID) //is new SceneObjectGroup ?
                        {
                            prim = buildPrim(primRow);
                            DataRow shapeRow = shapes.Rows.Find(prim.UUID.ToString());
                            if (shapeRow != null)
                            {
                                prim.Shape = buildShape(shapeRow);
                            }
                            else
                            {
                                _log.Warn(
                                    "[SQLITE REGION DB]: No shape found for prim in storage, so setting default box shape");
                                prim.Shape = PrimitiveBaseShape.Default;
                            }

                            createdObjects[new UUID(objID)].AddPart(prim);
                            LoadItems(prim);
                        }
                    }
                    catch (Exception e)
                    {
                        _log.Error("[SQLITE REGION DB]: Failed create prim object in group, exception and data follows");
                        _log.Error("[SQLITE REGION DB]: ", e);
                        foreach (DataColumn col in prims.Columns)
                        {
                            _log.Error("[SQLITE REGION DB]: Col: " + col.ColumnName + " => " + primRow[col]);
                        }
                    }
                }
            }
            return retvals;
        }

        /// <summary>
        /// Load in a prim's persisted inventory.
        /// </summary>
        /// <param name="prim">the prim</param>
        private void LoadItems(SceneObjectPart prim)
        {
//            _log.DebugFormat("[SQLITE REGION DB]: Loading inventory for {0} {1}", prim.Name, prim.UUID);

            DataTable dbItems = ds.Tables["primitems"];
            string sql = string.Format("primID = '{0}'", prim.UUID.ToString());
            DataRow[] dbItemRows = dbItems.Select(sql);
            IList<TaskInventoryItem> inventory = new List<TaskInventoryItem>();

//            _log.DebugFormat("[SQLITE REGION DB]: Found {0} items for {1} {2}", dbItemRows.Length, prim.Name, prim.UUID);

            foreach (DataRow row in dbItemRows)
            {
                TaskInventoryItem item = buildItem(row);
                inventory.Add(item);

//                _log.DebugFormat("[SQLITE REGION DB]: Restored item {0} {1}", item.Name, item.ItemID);
            }

            prim.Inventory.RestoreInventoryItems(inventory);
        }

        // Legacy entry point for when terrain was always a 256x256 hieghtmap
        public void StoreTerrain(double[,] ter, UUID regionID)
        {
            StoreTerrain(new TerrainData(ter), regionID);
        }

        /// <summary>
        /// Store a terrain in region storage
        /// </summary>
        /// <param name="ter">terrain heightfield</param>
        /// <param name="regionID">region UUID</param>
        public void StoreTerrain(TerrainData terrData, UUID regionID)
        {
            lock (ds)
            {
                using (SqliteCommand cmd = new SqliteCommand("delete from terrain where RegionUUID=:RegionUUID", _conn))
                {
                    cmd.Parameters.Add(new SqliteParameter(":RegionUUID", regionID.ToString()));
                    cmd.ExecuteNonQuery();
                }

                // the following is an work around for .NET.  The perf
                // issues associated with it aren't as bad as you think.
                string sql = "insert into terrain(RegionUUID, Revision, Heightfield)" +
                             " values(:RegionUUID, :Revision, :Heightfield)";

                int terrainDBRevision;
                Array terrainDBblob;
                terrData.GetDatabaseBlob(out terrainDBRevision, out terrainDBblob);

                _log.DebugFormat("{0} Storing terrain format {1}", LogHeader, terrainDBRevision);

                using (SqliteCommand cmd = new SqliteCommand(sql, _conn))
                {
                    cmd.Parameters.Add(new SqliteParameter(":RegionUUID", regionID.ToString()));
                    cmd.Parameters.Add(new SqliteParameter(":Revision", terrainDBRevision));
                    cmd.Parameters.Add(new SqliteParameter(":Heightfield", terrainDBblob));
                    cmd.ExecuteNonQuery();
                }
            }
        }

        /// <summary>
        /// Store baked terrain in region storage
        /// </summary>
        /// <param name="ter">terrain heightfield</param>
        /// <param name="regionID">region UUID</param>
        public void StoreBakedTerrain(TerrainData terrData, UUID regionID)
        {
            lock (ds)
            {
                using (
                    SqliteCommand cmd = new SqliteCommand("delete from bakedterrain where RegionUUID=:RegionUUID", _conn))
                {
                    cmd.Parameters.Add(new SqliteParameter(":RegionUUID", regionID.ToString()));
                    cmd.ExecuteNonQuery();
                }

                // the following is an work around for .NET.  The perf
                // issues associated with it aren't as bad as you think.
                string sql = "insert into bakedterrain(RegionUUID, Revision, Heightfield)" +
                             " values(:RegionUUID, :Revision, :Heightfield)";

                int terrainDBRevision;
                Array terrainDBblob;
                terrData.GetDatabaseBlob(out terrainDBRevision, out terrainDBblob);

                _log.DebugFormat("{0} Storing bakedterrain format {1}", LogHeader, terrainDBRevision);

                using (SqliteCommand cmd = new SqliteCommand(sql, _conn))
                {
                    cmd.Parameters.Add(new SqliteParameter(":RegionUUID", regionID.ToString()));
                    cmd.Parameters.Add(new SqliteParameter(":Revision", terrainDBRevision));
                    cmd.Parameters.Add(new SqliteParameter(":Heightfield", terrainDBblob));
                    cmd.ExecuteNonQuery();
                }
            }
        }

        /// <summary>
        /// Load the latest terrain revision from region storage
        /// </summary>
        /// <param name="regionID">the region UUID</param>
        /// <returns>Heightfield data</returns>
        public double[,] LoadTerrain(UUID regionID)
        {
            double[,] ret = null;
            TerrainData terrData = LoadTerrain(regionID, (int)Constants.RegionSize, (int)Constants.RegionSize, (int)Constants.RegionHeight);
            if (terrData != null)
                ret = terrData.GetDoubles();
            return ret;
        }

        // Returns 'null' if region not found
        public TerrainData LoadTerrain(UUID regionID, int pSizeX, int pSizeY, int pSizeZ)
        {
            TerrainData terrData = null;

            lock (ds)
            {
                string sql = "select RegionUUID, Revision, Heightfield from terrain" +
                             " where RegionUUID=:RegionUUID order by Revision desc";

                using (SqliteCommand cmd = new SqliteCommand(sql, _conn))
                {
                    cmd.Parameters.Add(new SqliteParameter(":RegionUUID", regionID.ToString()));

                    using (IDataReader row = cmd.ExecuteReader())
                    {
                        int rev = 0;
                        if (row.Read())
                        {
                            rev = Convert.ToInt32(row["Revision"]);
                            byte[] blob = (byte[])row["Heightfield"];
                            terrData = TerrainData.CreateFromDatabaseBlobFactory(pSizeX, pSizeY, pSizeZ, rev, blob);
                        }
                        else
                        {
                            _log.Warn("[SQLITE REGION DB]: No terrain found for region");
                            return null;
                        }

                        _log.Debug("[SQLITE REGION DB]: Loaded terrain revision r" + rev.ToString());
                    }
                }
            }
            return terrData;
        }

        public TerrainData LoadBakedTerrain(UUID regionID, int pSizeX, int pSizeY, int pSizeZ)
        {
            TerrainData terrData = null;

            lock (ds)
            {
                string sql = "select RegionUUID, Revision, Heightfield from bakedterrain" +
                             " where RegionUUID=:RegionUUID";

                using (SqliteCommand cmd = new SqliteCommand(sql, _conn))
                {
                    cmd.Parameters.Add(new SqliteParameter(":RegionUUID", regionID.ToString()));

                    using (IDataReader row = cmd.ExecuteReader())
                    {
                        int rev = 0;
                        if (row.Read())
                        {
                            rev = Convert.ToInt32(row["Revision"]);
                            byte[] blob = (byte[])row["Heightfield"];
                            terrData = TerrainData.CreateFromDatabaseBlobFactory(pSizeX, pSizeY, pSizeZ, rev, blob);
                        }
                    }
                }
            }
            return terrData;
        }

        public void RemoveLandObject(UUID globalID)
        {
            lock (ds)
            {
                // Can't use blanket SQL statements when using SqlAdapters unless you re-read the data into the adapter
                // after you're done.
                // replaced below code with the SqliteAdapter version.
                //using (SqliteCommand cmd = new SqliteCommand("delete from land where UUID=:UUID", _conn))
                //{
                //    cmd.Parameters.Add(new SqliteParameter(":UUID", globalID.ToString()));
                //    cmd.ExecuteNonQuery();
                //}

                //using (SqliteCommand cmd = new SqliteCommand("delete from landaccesslist where LandUUID=:UUID", _conn))
                //{
                //   cmd.Parameters.Add(new SqliteParameter(":UUID", globalID.ToString()));
                //    cmd.ExecuteNonQuery();
                //}

                DataTable land = ds.Tables["land"];
                DataTable landaccesslist = ds.Tables["landaccesslist"];
                DataRow landRow = land.Rows.Find(globalID.ToString());
                if (landRow != null)
                {
                    landRow.Delete();
                }
                List<DataRow> rowsToDelete = new List<DataRow>();
                foreach (DataRow rowToCheck in landaccesslist.Rows)
                {
                    if (rowToCheck["LandUUID"].ToString() == globalID.ToString())
                        rowsToDelete.Add(rowToCheck);
                }
                for (int iter = 0; iter < rowsToDelete.Count; iter++)
                {
                    rowsToDelete[iter].Delete();
                }
                Commit();
            }
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="parcel"></param>
        public void StoreLandObject(ILandObject parcel)
        {
            lock (ds)
            {
                DataTable land = ds.Tables["land"];
                DataTable landaccesslist = ds.Tables["landaccesslist"];

                DataRow landRow = land.Rows.Find(parcel.LandData.GlobalID.ToString());
                if (landRow == null)
                {
                    landRow = land.NewRow();
                    fillLandRow(landRow, parcel.LandData, parcel.RegionUUID);
                    land.Rows.Add(landRow);
                }
                else
                {
                    fillLandRow(landRow, parcel.LandData, parcel.RegionUUID);
                }

                // I know this caused someone issues before, but OpenSim is unusable if we leave this stuff around
                //using (SqliteCommand cmd = new SqliteCommand("delete from landaccesslist where LandUUID=:LandUUID", _conn))
                //{
                //    cmd.Parameters.Add(new SqliteParameter(":LandUUID", parcel.LandData.GlobalID.ToString()));
                //    cmd.ExecuteNonQuery();

                //                }

                // This is the slower..  but more appropriate thing to do

                // We can't modify the table with direct queries before calling Commit() and re-filling them.
                List<DataRow> rowsToDelete = new List<DataRow>();
                foreach (DataRow rowToCheck in landaccesslist.Rows)
                {
                    if (rowToCheck["LandUUID"].ToString() == parcel.LandData.GlobalID.ToString())
                        rowsToDelete.Add(rowToCheck);
                }
                for (int iter = 0; iter < rowsToDelete.Count; ++iter)
                    rowsToDelete[iter].Delete();

                foreach (LandAccessEntry entry in parcel.LandData.ParcelAccessList)
                {
                    DataRow newAccessRow = landaccesslist.NewRow();
                    fillLandAccessRow(newAccessRow, entry, parcel.LandData.GlobalID);
                    landaccesslist.Rows.Add(newAccessRow);
                }
                Commit();
            }
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="regionUUID"></param>
        /// <returns></returns>
        public List<LandData> LoadLandObjects(UUID regionUUID)
        {
            List<LandData> landDataForRegion = new List<LandData>();
            lock (ds)
            {
                DataTable land = ds.Tables["land"];
                DataTable landaccesslist = ds.Tables["landaccesslist"];
                string searchExp = "RegionUUID = '" + regionUUID + "'";
                DataRow[] rawDataForRegion = land.Select(searchExp);
                foreach (DataRow rawDataLand in rawDataForRegion)
                {
                    LandData newLand = buildLandData(rawDataLand);
                    string accessListSearchExp = "LandUUID = '" + newLand.GlobalID + "'";
                    DataRow[] rawDataForLandAccessList = landaccesslist.Select(accessListSearchExp);
                    foreach (DataRow rawDataLandAccess in rawDataForLandAccessList)
                    {
                        newLand.ParcelAccessList.Add(buildLandAccessData(rawDataLandAccess));
                    }

                    landDataForRegion.Add(newLand);
                }
            }
            return landDataForRegion;
        }

        /// <summary>
        ///
        /// </summary>
        public void Commit()
        {
//            _log.Debug("[SQLITE]: Starting commit");
            //lock (ds) caller must lock
            {
                primDa.Update(ds, "prims");
                shapeDa.Update(ds, "primshapes");

                itemsDa.Update(ds, "primitems");

                terrainDa.Update(ds, "terrain");
                landDa.Update(ds, "land");
                landAccessListDa.Update(ds, "landaccesslist");
                try
                {
                    regionSettingsDa.Update(ds, "regionsettings");
                    regionWindlightDa.Update(ds, "regionwindlight");
                }
                catch (SqliteException SqlEx)
                {
                    throw new Exception(
                        "There was a SQL error or connection string configuration error when saving the region settings.  This could be a bug, it could also happen if ConnectionString is defined in the [DatabaseService] section of StandaloneCommon.ini in the config_include folder.  This could also happen if the config_include folder doesn't exist or if the OpenSim.ini [Architecture] section isn't set.  If this is your first time running OpenSimulator, please restart the simulator and bug a developer to fix this!",
                        SqlEx);
                }
                ds.AcceptChanges();
            }
        }

        /// <summary>
        /// See <see cref="Commit"/>
        /// </summary>
        public void Shutdown()
        {
            lock(ds)
                Commit();
        }

        /***********************************************************************
         *
         *  Database Definition Functions
         *
         *  This should be db agnostic as we define them in ADO.NET terms
         *
         **********************************************************************/

        protected void CreateDataSetMapping(IDataAdapter da, string tableName)
        {
            ITableMapping dbMapping = da.TableMappings.Add(tableName, tableName);
            foreach (DataColumn col in ds.Tables[tableName].Columns)
            {
                dbMapping.ColumnMappings.Add(col.ColumnName, col.ColumnName);
            }
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="dt"></param>
        /// <param name="name"></param>
        /// <param name="type"></param>
        private static void createCol(DataTable dt, string name, Type type)
        {
            DataColumn col = new DataColumn(name, type);
            dt.Columns.Add(col);
        }

        /// <summary>
        /// Creates the "terrain" table
        /// </summary>
        /// <returns>terrain table DataTable</returns>
        private static DataTable createTerrainTable()
        {
            DataTable terrain = new DataTable("terrain");

            createCol(terrain, "RegionUUID", typeof(string));
            createCol(terrain, "Revision", typeof(int));
            createCol(terrain, "Heightfield", typeof(byte[]));

            return terrain;
        }

        /// <summary>
        /// Creates the "prims" table
        /// </summary>
        /// <returns>prim table DataTable</returns>
        private static DataTable createPrimTable()
        {
            DataTable prims = new DataTable("prims");

            createCol(prims, "UUID", typeof(string));
            createCol(prims, "RegionUUID", typeof(string));
            createCol(prims, "CreationDate", typeof(int));
            createCol(prims, "Name", typeof(string));
            createCol(prims, "SceneGroupID", typeof(string));
            // various text fields
            createCol(prims, "Text", typeof(string));
            createCol(prims, "ColorR", typeof(int));
            createCol(prims, "ColorG", typeof(int));
            createCol(prims, "ColorB", typeof(int));
            createCol(prims, "ColorA", typeof(int));
            createCol(prims, "Description", typeof(string));
            createCol(prims, "SitName", typeof(string));
            createCol(prims, "TouchName", typeof(string));
            // permissions
            createCol(prims, "ObjectFlags", typeof(int));
            createCol(prims, "CreatorID", typeof(string));
            createCol(prims, "OwnerID", typeof(string));
            createCol(prims, "GroupID", typeof(string));
            createCol(prims, "LastOwnerID", typeof(string));
            createCol(prims, "RezzerID", typeof(string));
            createCol(prims, "OwnerMask", typeof(int));
            createCol(prims, "NextOwnerMask", typeof(int));
            createCol(prims, "GroupMask", typeof(int));
            createCol(prims, "EveryoneMask", typeof(int));
            createCol(prims, "BaseMask", typeof(int));
            // vectors
            createCol(prims, "PositionX", typeof(double));
            createCol(prims, "PositionY", typeof(double));
            createCol(prims, "PositionZ", typeof(double));
            createCol(prims, "GroupPositionX", typeof(double));
            createCol(prims, "GroupPositionY", typeof(double));
            createCol(prims, "GroupPositionZ", typeof(double));
            createCol(prims, "VelocityX", typeof(double));
            createCol(prims, "VelocityY", typeof(double));
            createCol(prims, "VelocityZ", typeof(double));
            createCol(prims, "AngularVelocityX", typeof(double));
            createCol(prims, "AngularVelocityY", typeof(double));
            createCol(prims, "AngularVelocityZ", typeof(double));
            createCol(prims, "AccelerationX", typeof(double));
            createCol(prims, "AccelerationY", typeof(double));
            createCol(prims, "AccelerationZ", typeof(double));
            // quaternions
            createCol(prims, "RotationX", typeof(double));
            createCol(prims, "RotationY", typeof(double));
            createCol(prims, "RotationZ", typeof(double));
            createCol(prims, "RotationW", typeof(double));

            // sit target
            createCol(prims, "SitTargetOffsetX", typeof(double));
            createCol(prims, "SitTargetOffsetY", typeof(double));
            createCol(prims, "SitTargetOffsetZ", typeof(double));

            createCol(prims, "SitTargetOrientW", typeof(double));
            createCol(prims, "SitTargetOrientX", typeof(double));
            createCol(prims, "SitTargetOrientY", typeof(double));
            createCol(prims, "SitTargetOrientZ", typeof(double));

            createCol(prims, "PayPrice", typeof(int));
            createCol(prims, "PayButton1", typeof(int));
            createCol(prims, "PayButton2", typeof(int));
            createCol(prims, "PayButton3", typeof(int));
            createCol(prims, "PayButton4", typeof(int));

            createCol(prims, "LoopedSound", typeof(string));
            createCol(prims, "LoopedSoundGain", typeof(double));
            createCol(prims, "TextureAnimation", typeof(string));
            createCol(prims, "ParticleSystem", typeof(string));

            createCol(prims, "CameraEyeOffsetX", typeof(double));
            createCol(prims, "CameraEyeOffsetY", typeof(double));
            createCol(prims, "CameraEyeOffsetZ", typeof(double));

            createCol(prims, "CameraAtOffsetX", typeof(double));
            createCol(prims, "CameraAtOffsetY", typeof(double));
            createCol(prims, "CameraAtOffsetZ", typeof(double));

            createCol(prims, "ForceMouselook", typeof(short));

            createCol(prims, "ScriptAccessPin", typeof(int));

            createCol(prims, "AllowedDrop", typeof(short));
            createCol(prims, "DieAtEdge", typeof(short));

            createCol(prims, "SalePrice", typeof(int));
            createCol(prims, "SaleType", typeof(short));

            // click action
            createCol(prims, "ClickAction", typeof(byte));

            createCol(prims, "Material", typeof(byte));

            createCol(prims, "CollisionSound", typeof(string));
            createCol(prims, "CollisionSoundVolume", typeof(double));

            createCol(prims, "VolumeDetect", typeof(short));

            createCol(prims, "MediaURL", typeof(string));

            createCol(prims, "AttachedPosX", typeof(double));
            createCol(prims, "AttachedPosY", typeof(double));
            createCol(prims, "AttachedPosZ", typeof(double));

            createCol(prims, "DynAttrs", typeof(string));

            createCol(prims, "PhysicsShapeType", typeof(byte));
            createCol(prims, "Density", typeof(double));
            createCol(prims, "GravityModifier", typeof(double));
            createCol(prims, "Friction", typeof(double));
            createCol(prims, "Restitution", typeof(double));

            createCol(prims, "KeyframeMotion", typeof(byte[]));

            createCol(prims, "PassTouches", typeof(bool));
            createCol(prims, "PassCollisions", typeof(bool));
            createCol(prims, "Vehicle", typeof(string));

            createCol(prims, "RotationAxisLocks", typeof(byte));

            createCol(prims, "PhysInertia", typeof(string));

            createCol(prims, "standtargetx", typeof(float));
            createCol(prims, "standtargety", typeof(float));
            createCol(prims, "standtargetz", typeof(float));
            createCol(prims, "sitactrange", typeof(float));

            createCol(prims, "pseudocrc", typeof(int));

            // Add in contraints
            prims.PrimaryKey = new DataColumn[] { prims.Columns["UUID"] };

            return prims;
        }

        /// <summary>
        /// Creates "primshapes" table
        /// </summary>
        /// <returns>shape table DataTable</returns>
        private static DataTable createShapeTable()
        {
            DataTable shapes = new DataTable("primshapes");
            createCol(shapes, "UUID", typeof(string));
            // shape is an enum
            createCol(shapes, "Shape", typeof(int));
            // vectors
            createCol(shapes, "ScaleX", typeof(double));
            createCol(shapes, "ScaleY", typeof(double));
            createCol(shapes, "ScaleZ", typeof(double));
            // paths
            createCol(shapes, "PCode", typeof(int));
            createCol(shapes, "PathBegin", typeof(int));
            createCol(shapes, "PathEnd", typeof(int));
            createCol(shapes, "PathScaleX", typeof(int));
            createCol(shapes, "PathScaleY", typeof(int));
            createCol(shapes, "PathShearX", typeof(int));
            createCol(shapes, "PathShearY", typeof(int));
            createCol(shapes, "PathSkew", typeof(int));
            createCol(shapes, "PathCurve", typeof(int));
            createCol(shapes, "PathRadiusOffset", typeof(int));
            createCol(shapes, "PathRevolutions", typeof(int));
            createCol(shapes, "PathTaperX", typeof(int));
            createCol(shapes, "PathTaperY", typeof(int));
            createCol(shapes, "PathTwist", typeof(int));
            createCol(shapes, "PathTwistBegin", typeof(int));
            // profile
            createCol(shapes, "ProfileBegin", typeof(int));
            createCol(shapes, "ProfileEnd", typeof(int));
            createCol(shapes, "ProfileCurve", typeof(int));
            createCol(shapes, "ProfileHollow", typeof(int));
            createCol(shapes, "State", typeof(int));
            createCol(shapes, "LastAttachPoint", typeof(int));
            // text TODO: this isn't right, but I'm not sure the right
            // way to specify this as a blob atm
            createCol(shapes, "Texture", typeof(byte[]));
            createCol(shapes, "ExtraParams", typeof(byte[]));
            createCol(shapes, "Media", typeof(string));

            shapes.PrimaryKey = new DataColumn[] { shapes.Columns["UUID"] };

            return shapes;
        }

        /// <summary>
        /// creates "primitems" table
        /// </summary>
        /// <returns>item table DataTable</returns>
        private static DataTable createItemsTable()
        {
            DataTable items = new DataTable("primitems");

            createCol(items, "itemID", typeof(string));
            createCol(items, "primID", typeof(string));
            createCol(items, "assetID", typeof(string));
            createCol(items, "parentFolderID", typeof(string));

            createCol(items, "invType", typeof(int));
            createCol(items, "assetType", typeof(int));

            createCol(items, "name", typeof(string));
            createCol(items, "description", typeof(string));

            createCol(items, "creationDate", typeof(long));
            createCol(items, "creatorID", typeof(string));
            createCol(items, "ownerID", typeof(string));
            createCol(items, "lastOwnerID", typeof(string));
            createCol(items, "groupID", typeof(string));

            createCol(items, "nextPermissions", typeof(uint));
            createCol(items, "currentPermissions", typeof(uint));
            createCol(items, "basePermissions", typeof(uint));
            createCol(items, "everyonePermissions", typeof(uint));
            createCol(items, "groupPermissions", typeof(uint));
            createCol(items, "flags", typeof(uint));

            items.PrimaryKey = new DataColumn[] { items.Columns["itemID"] };

            return items;
        }

        /// <summary>
        /// Creates "land" table
        /// </summary>
        /// <returns>land table DataTable</returns>
        private static DataTable createLandTable()
        {
            DataTable land = new DataTable("land");
            createCol(land, "UUID", typeof(string));
            createCol(land, "RegionUUID", typeof(string));
            createCol(land, "LocalLandID", typeof(uint));

            // Bitmap is a byte[512]
            createCol(land, "Bitmap", typeof(byte[]));

            createCol(land, "Name", typeof(string));
            createCol(land, "Desc", typeof(string));
            createCol(land, "OwnerUUID", typeof(string));
            createCol(land, "IsGroupOwned", typeof(string));
            createCol(land, "Area", typeof(int));
            createCol(land, "AuctionID", typeof(int)); //Unemplemented
            createCol(land, "Category", typeof(int)); //Enum OpenMetaverse.Parcel.ParcelCategory
            createCol(land, "ClaimDate", typeof(int));
            createCol(land, "ClaimPrice", typeof(int));
            createCol(land, "GroupUUID", typeof(string));
            createCol(land, "SalePrice", typeof(int));
            createCol(land, "LandStatus", typeof(int)); //Enum. OpenMetaverse.Parcel.ParcelStatus
            createCol(land, "LandFlags", typeof(uint));
            createCol(land, "LandingType", typeof(byte));
            createCol(land, "MediaAutoScale", typeof(byte));
            createCol(land, "MediaTextureUUID", typeof(string));
            createCol(land, "MediaURL", typeof(string));
            createCol(land, "MusicURL", typeof(string));
            createCol(land, "PassHours", typeof(double));
            createCol(land, "PassPrice", typeof(uint));
            createCol(land, "SnapshotUUID", typeof(string));
            createCol(land, "UserLocationX", typeof(double));
            createCol(land, "UserLocationY", typeof(double));
            createCol(land, "UserLocationZ", typeof(double));
            createCol(land, "UserLookAtX", typeof(double));
            createCol(land, "UserLookAtY", typeof(double));
            createCol(land, "UserLookAtZ", typeof(double));
            createCol(land, "AuthbuyerID", typeof(string));
            createCol(land, "OtherCleanTime", typeof(int));
            createCol(land, "Dwell", typeof(int));
            createCol(land, "MediaType", typeof(string));
            createCol(land, "MediaDescription", typeof(string));
            createCol(land, "MediaSize", typeof(string));
            createCol(land, "MediaLoop", typeof(bool));
            createCol(land, "ObscureMedia", typeof(bool));
            createCol(land, "ObscureMusic", typeof(bool));
            createCol(land, "SeeAVs", typeof(bool));
            createCol(land, "AnyAVSounds", typeof(bool));
            createCol(land, "GroupAVSounds", typeof(bool));
            createCol(land, "environment", typeof(string));

            land.PrimaryKey = new DataColumn[] { land.Columns["UUID"] };

            return land;
        }

        /// <summary>
        /// create "landaccesslist" table
        /// </summary>
        /// <returns>Landacceslist DataTable</returns>
        private static DataTable createLandAccessListTable()
        {
            DataTable landaccess = new DataTable("landaccesslist");
            createCol(landaccess, "LandUUID", typeof(string));
            createCol(landaccess, "AccessUUID", typeof(string));
            createCol(landaccess, "Flags", typeof(uint));

            return landaccess;
        }

        private static DataTable createRegionSettingsTable()
        {
            DataTable regionsettings = new DataTable("regionsettings");
            createCol(regionsettings, "regionUUID", typeof(string));
            createCol(regionsettings, "block_terraform", typeof(int));
            createCol(regionsettings, "block_fly", typeof(int));
            createCol(regionsettings, "allow_damage", typeof(int));
            createCol(regionsettings, "restrict_pushing", typeof(int));
            createCol(regionsettings, "allow_land_resell", typeof(int));
            createCol(regionsettings, "allow_land_join_divide", typeof(int));
            createCol(regionsettings, "block_show_in_search", typeof(int));
            createCol(regionsettings, "agent_limit", typeof(int));
            createCol(regionsettings, "object_bonus", typeof(double));
            createCol(regionsettings, "maturity", typeof(int));
            createCol(regionsettings, "disable_scripts", typeof(int));
            createCol(regionsettings, "disable_collisions", typeof(int));
            createCol(regionsettings, "disable_physics", typeof(int));
            createCol(regionsettings, "terrain_texture_1", typeof(string));
            createCol(regionsettings, "terrain_texture_2", typeof(string));
            createCol(regionsettings, "terrain_texture_3", typeof(string));
            createCol(regionsettings, "terrain_texture_4", typeof(string));
            createCol(regionsettings, "elevation_1_nw", typeof(double));
            createCol(regionsettings, "elevation_2_nw", typeof(double));
            createCol(regionsettings, "elevation_1_ne", typeof(double));
            createCol(regionsettings, "elevation_2_ne", typeof(double));
            createCol(regionsettings, "elevation_1_se", typeof(double));
            createCol(regionsettings, "elevation_2_se", typeof(double));
            createCol(regionsettings, "elevation_1_sw", typeof(double));
            createCol(regionsettings, "elevation_2_sw", typeof(double));
            createCol(regionsettings, "water_height", typeof(double));
            createCol(regionsettings, "terrain_raise_limit", typeof(double));
            createCol(regionsettings, "terrain_lower_limit", typeof(double));
            createCol(regionsettings, "use_estate_sun", typeof(int));
            createCol(regionsettings, "sandbox", typeof(int));
            createCol(regionsettings, "sunvectorx", typeof(double));
            createCol(regionsettings, "sunvectory", typeof(double));
            createCol(regionsettings, "sunvectorz", typeof(double));
            createCol(regionsettings, "fixed_sun", typeof(int));
            createCol(regionsettings, "sun_position", typeof(double));
            createCol(regionsettings, "covenant", typeof(string));
            createCol(regionsettings, "covenant_datetime", typeof(int));
            createCol(regionsettings, "map_tile_ID", typeof(string));
            createCol(regionsettings, "TelehubObject", typeof(string));
            createCol(regionsettings, "parcel_tile_ID", typeof(string));
            createCol(regionsettings, "block_search", typeof(bool));
            createCol(regionsettings, "casino", typeof(bool));
            createCol(regionsettings, "cacheID", typeof(string));
            regionsettings.PrimaryKey = new DataColumn[] { regionsettings.Columns["regionUUID"] };
            return regionsettings;
        }

        /// <summary>
        /// create "regionwindlight" table
        /// </summary>
        /// <returns>RegionWindlight DataTable</returns>
        private static DataTable createRegionWindlightTable()
        {
            DataTable regionwindlight = new DataTable("regionwindlight");
            createCol(regionwindlight, "region_id", typeof(string));
            createCol(regionwindlight, "water_color_r", typeof(double));
            createCol(regionwindlight, "water_color_g", typeof(double));
            createCol(regionwindlight, "water_color_b", typeof(double));
            createCol(regionwindlight, "water_color_i", typeof(double));
            createCol(regionwindlight, "water_fog_density_exponent", typeof(double));
            createCol(regionwindlight, "underwater_fog_modifier", typeof(double));
            createCol(regionwindlight, "reflection_wavelet_scale_1", typeof(double));
            createCol(regionwindlight, "reflection_wavelet_scale_2", typeof(double));
            createCol(regionwindlight, "reflection_wavelet_scale_3", typeof(double));
            createCol(regionwindlight, "fresnel_scale", typeof(double));
            createCol(regionwindlight, "fresnel_offset", typeof(double));
            createCol(regionwindlight, "refract_scale_above", typeof(double));
            createCol(regionwindlight, "refract_scale_below", typeof(double));
            createCol(regionwindlight, "blur_multiplier", typeof(double));
            createCol(regionwindlight, "big_wave_direction_x", typeof(double));
            createCol(regionwindlight, "big_wave_direction_y", typeof(double));
            createCol(regionwindlight, "little_wave_direction_x", typeof(double));
            createCol(regionwindlight, "little_wave_direction_y", typeof(double));
            createCol(regionwindlight, "normal_map_texture", typeof(string));
            createCol(regionwindlight, "horizon_r", typeof(double));
            createCol(regionwindlight, "horizon_g", typeof(double));
            createCol(regionwindlight, "horizon_b", typeof(double));
            createCol(regionwindlight, "horizon_i", typeof(double));
            createCol(regionwindlight, "haze_horizon", typeof(double));
            createCol(regionwindlight, "blue_density_r", typeof(double));
            createCol(regionwindlight, "blue_density_g", typeof(double));
            createCol(regionwindlight, "blue_density_b", typeof(double));
            createCol(regionwindlight, "blue_density_i", typeof(double));
            createCol(regionwindlight, "haze_density", typeof(double));
            createCol(regionwindlight, "density_multiplier", typeof(double));
            createCol(regionwindlight, "distance_multiplier", typeof(double));
            createCol(regionwindlight, "max_altitude", typeof(int));
            createCol(regionwindlight, "sun_moon_color_r", typeof(double));
            createCol(regionwindlight, "sun_moon_color_g", typeof(double));
            createCol(regionwindlight, "sun_moon_color_b", typeof(double));
            createCol(regionwindlight, "sun_moon_color_i", typeof(double));
            createCol(regionwindlight, "sun_moon_position", typeof(double));
            createCol(regionwindlight, "ambient_r", typeof(double));
            createCol(regionwindlight, "ambient_g", typeof(double));
            createCol(regionwindlight, "ambient_b", typeof(double));
            createCol(regionwindlight, "ambient_i", typeof(double));
            createCol(regionwindlight, "east_angle", typeof(double));
            createCol(regionwindlight, "sun_glow_focus", typeof(double));
            createCol(regionwindlight, "sun_glow_size", typeof(double));
            createCol(regionwindlight, "scene_gamma", typeof(double));
            createCol(regionwindlight, "star_brightness", typeof(double));
            createCol(regionwindlight, "cloud_color_r", typeof(double));
            createCol(regionwindlight, "cloud_color_g", typeof(double));
            createCol(regionwindlight, "cloud_color_b", typeof(double));
            createCol(regionwindlight, "cloud_color_i", typeof(double));
            createCol(regionwindlight, "cloud_x", typeof(double));
            createCol(regionwindlight, "cloud_y", typeof(double));
            createCol(regionwindlight, "cloud_density", typeof(double));
            createCol(regionwindlight, "cloud_coverage", typeof(double));
            createCol(regionwindlight, "cloud_scale", typeof(double));
            createCol(regionwindlight, "cloud_detail_x", typeof(double));
            createCol(regionwindlight, "cloud_detail_y", typeof(double));
            createCol(regionwindlight, "cloud_detail_density", typeof(double));
            createCol(regionwindlight, "cloud_scroll_x", typeof(double));
            createCol(regionwindlight, "cloud_scroll_x_lock", typeof(int));
            createCol(regionwindlight, "cloud_scroll_y", typeof(double));
            createCol(regionwindlight, "cloud_scroll_y_lock", typeof(int));
            createCol(regionwindlight, "draw_classic_clouds", typeof(int));

            regionwindlight.PrimaryKey = new DataColumn[] { regionwindlight.Columns["region_id"] };
            return regionwindlight;
        }

        private static DataTable createRegionEnvironmentTable()
        {
            DataTable regionEnvironment = new DataTable("regionenvironment");
            createCol(regionEnvironment, "region_id", typeof(string));
            createCol(regionEnvironment, "llsd_settings", typeof(string));

            regionEnvironment.PrimaryKey = new DataColumn[] { regionEnvironment.Columns["region_id"] };

            return regionEnvironment;
        }

        private static DataTable createRegionSpawnPointsTable()
        {
            DataTable spawn_points = new DataTable("spawn_points");
            createCol(spawn_points, "regionID", typeof(string));
            createCol(spawn_points, "Yaw", typeof(float));
            createCol(spawn_points, "Pitch", typeof(float));
            createCol(spawn_points, "Distance", typeof(float));

            return spawn_points;
        }

        /***********************************************************************
         *
         *  Convert between ADO.NET <=> OpenSim Objects
         *
         *  These should be database independant
         *
         **********************************************************************/

        /// <summary>
        ///
        /// </summary>
        /// <param name="row"></param>
        /// <returns></returns>
        private SceneObjectPart buildPrim(DataRow row)
        {
            // Code commented.  Uncomment to test the unit test inline.

            // The unit test mentions this commented code for the purposes
            // of debugging a unit test failure

            // SceneObjectGroup sog = new SceneObjectGroup();
            // SceneObjectPart sop = new SceneObjectPart();
            // sop.LocalId = 1;
            // sop.Name = "object1";
            // sop.Description = "object1";
            // sop.Text = "";
            // sop.SitName = "";
            // sop.TouchName = "";
            // sop.UUID = UUID.Random();
            // sop.Shape = PrimitiveBaseShape.Default;
            // sog.SetRootPart(sop);
            // Add breakpoint in above line.  Check sop fields.

            // TODO: this doesn't work yet because something more
            // interesting has to be done to actually get these values
            // back out.  Not enough time to figure it out yet.

            SceneObjectPart prim = new SceneObjectPart
            {
                UUID = new UUID((string)row["UUID"]),
                // explicit conversion of integers is required, which sort
                // of sucks.  No idea if there is a shortcut here or not.
                CreationDate = Convert.ToInt32(row["CreationDate"]),
                Name = row["Name"] == DBNull.Value ? string.Empty : (string)row["Name"],
                // various text fields
                Text = (string)row["Text"],
                Color = Color.FromArgb(Convert.ToInt32(row["ColorA"]),
                                        Convert.ToInt32(row["ColorR"]),
                                        Convert.ToInt32(row["ColorG"]),
                                        Convert.ToInt32(row["ColorB"])),
                Description = (string)row["Description"],
                SitName = (string)row["SitName"],
                TouchName = (string)row["TouchName"],
                // permissions
                Flags = (PrimFlags)Convert.ToUInt32(row["ObjectFlags"]),
                CreatorIdentification = (string)row["CreatorID"],
                OwnerID = new UUID((string)row["OwnerID"]),
                GroupID = new UUID((string)row["GroupID"]),
                LastOwnerID = new UUID((string)row["LastOwnerID"]),
                RezzerID = row["RezzerID"] == DBNull.Value ? UUID.Zero : new UUID((string)row["RezzerID"]),
                OwnerMask = Convert.ToUInt32(row["OwnerMask"]),
                NextOwnerMask = Convert.ToUInt32(row["NextOwnerMask"]),
                GroupMask = Convert.ToUInt32(row["GroupMask"]),
                EveryoneMask = Convert.ToUInt32(row["EveryoneMask"]),
                BaseMask = Convert.ToUInt32(row["BaseMask"]),
                // vectors
                OffsetPosition = new Vector3(
                Convert.ToSingle(row["PositionX"]),
                Convert.ToSingle(row["PositionY"]),
                Convert.ToSingle(row["PositionZ"])
                ),
                GroupPosition = new Vector3(
                Convert.ToSingle(row["GroupPositionX"]),
                Convert.ToSingle(row["GroupPositionY"]),
                Convert.ToSingle(row["GroupPositionZ"])
                ),
                Velocity = new Vector3(
                Convert.ToSingle(row["VelocityX"]),
                Convert.ToSingle(row["VelocityY"]),
                Convert.ToSingle(row["VelocityZ"])
                ),
                AngularVelocity = new Vector3(
                Convert.ToSingle(row["AngularVelocityX"]),
                Convert.ToSingle(row["AngularVelocityY"]),
                Convert.ToSingle(row["AngularVelocityZ"])
                ),
                Acceleration = new Vector3(
                Convert.ToSingle(row["AccelerationX"]),
                Convert.ToSingle(row["AccelerationY"]),
                Convert.ToSingle(row["AccelerationZ"])
                ),
                // quaternions
                RotationOffset = new Quaternion(
                Convert.ToSingle(row["RotationX"]),
                Convert.ToSingle(row["RotationY"]),
                Convert.ToSingle(row["RotationZ"]),
                Convert.ToSingle(row["RotationW"])
                ),

                SitTargetPositionLL = new Vector3(
                                                   Convert.ToSingle(row["SitTargetOffsetX"]),
                                                   Convert.ToSingle(row["SitTargetOffsetY"]),
                                                   Convert.ToSingle(row["SitTargetOffsetZ"])),
                SitTargetOrientationLL = new Quaternion(
                                                         Convert.ToSingle(row["SitTargetOrientX"]),
                                                         Convert.ToSingle(row["SitTargetOrientY"]),
                                                         Convert.ToSingle(row["SitTargetOrientZ"]),
                                                         Convert.ToSingle(row["SitTargetOrientW"])),

                StandOffset = new Vector3(
                            Convert.ToSingle(row["standtargetx"]),
                            Convert.ToSingle(row["standtargety"]),
                            Convert.ToSingle(row["standtargetz"])
                            ),

                SitActiveRange = Convert.ToSingle(row["sitactrange"]),

                ClickAction = Convert.ToByte(row["ClickAction"])
            };
            prim.PayPrice[0] = Convert.ToInt32(row["PayPrice"]);
            prim.PayPrice[1] = Convert.ToInt32(row["PayButton1"]);
            prim.PayPrice[2] = Convert.ToInt32(row["PayButton2"]);
            prim.PayPrice[3] = Convert.ToInt32(row["PayButton3"]);
            prim.PayPrice[4] = Convert.ToInt32(row["PayButton4"]);

            prim.Sound = new UUID(row["LoopedSound"].ToString());
            prim.SoundGain = Convert.ToSingle(row["LoopedSoundGain"]);
            if (prim.Sound != UUID.Zero)
                prim.SoundFlags = 1; // If it's persisted at all, it's looped
            else
                prim.SoundFlags = 0;

            if (!row.IsNull("TextureAnimation"))
                prim.TextureAnimation = Convert.FromBase64String(row["TextureAnimation"].ToString());
            if (!row.IsNull("ParticleSystem"))
                prim.ParticleSystem = Convert.FromBase64String(row["ParticleSystem"].ToString());

            prim.SetCameraEyeOffset(new Vector3(
                Convert.ToSingle(row["CameraEyeOffsetX"]),
                Convert.ToSingle(row["CameraEyeOffsetY"]),
                Convert.ToSingle(row["CameraEyeOffsetZ"])
                ));

            prim.SetCameraAtOffset(new Vector3(
                Convert.ToSingle(row["CameraAtOffsetX"]),
                Convert.ToSingle(row["CameraAtOffsetY"]),
                Convert.ToSingle(row["CameraAtOffsetZ"])
                ));

            if (Convert.ToInt16(row["ForceMouselook"]) != 0)
                prim.SetForceMouselook(true);

            prim.ScriptAccessPin = Convert.ToInt32(row["ScriptAccessPin"]);

            if (Convert.ToInt16(row["AllowedDrop"]) != 0)
                prim.AllowedDrop = true;

            if (Convert.ToInt16(row["DieAtEdge"]) != 0)
                prim.DIE_AT_EDGE = true;

            prim.SalePrice = Convert.ToInt32(row["SalePrice"]);
            prim.ObjectSaleType = Convert.ToByte(row["SaleType"]);

            prim.Material = Convert.ToByte(row["Material"]);

            prim.CollisionSound = new UUID(row["CollisionSound"].ToString());
            prim.CollisionSoundVolume = Convert.ToSingle(row["CollisionSoundVolume"]);

            if (Convert.ToInt16(row["VolumeDetect"]) != 0)
                prim.VolumeDetectActive = true;

            if (!(row["MediaURL"] is System.DBNull))
            {
//                _log.DebugFormat("[SQLITE]: MediaUrl type [{0}]", row["MediaURL"].GetType());
                prim.MediaUrl = (string)row["MediaURL"];
            }

            prim.AttachedPos = new Vector3(
                Convert.ToSingle(row["AttachedPosX"]),
                Convert.ToSingle(row["AttachedPosY"]),
                Convert.ToSingle(row["AttachedPosZ"])
                );

            if (!(row["DynAttrs"] is System.DBNull))
            {
                //_log.DebugFormat("[SQLITE]: DynAttrs type [{0}]", row["DynAttrs"].GetType());
                prim.DynAttrs = DAMap.FromXml((string)row["DynAttrs"]);
            }
            else
            {
                prim.DynAttrs = null;
            }

            prim.PhysicsShapeType = Convert.ToByte(row["PhysicsShapeType"]);
            prim.Density = Convert.ToSingle(row["Density"]);
            prim.GravityModifier = Convert.ToSingle(row["GravityModifier"]);
            prim.Friction = Convert.ToSingle(row["Friction"]);
            prim.Restitution = Convert.ToSingle(row["Restitution"]);


            if (!(row["KeyframeMotion"] is DBNull))
            {
                byte[] data = (byte[])row["KeyframeMotion"];
                if (data.Length > 0)
                    prim.KeyframeMotion = KeyframeMotion.FromData(null, data);
                else
                    prim.KeyframeMotion = null;
            }
            else
            {
                prim.KeyframeMotion = null;
            }

            prim.PassCollisions = Convert.ToBoolean(row["PassCollisions"]);
            prim.PassTouches = Convert.ToBoolean(row["PassTouches"]);
            prim.RotationAxisLocks = Convert.ToByte(row["RotationAxisLocks"]);

            SOPVehicle vehicle = null;
            if (!(row["Vehicle"] is DBNull) && !string.IsNullOrEmpty(row["Vehicle"].ToString()))
            {
                vehicle = SOPVehicle.FromXml2(row["Vehicle"].ToString());
                if (vehicle != null)
                    prim.VehicleParams = vehicle;
            }

            PhysicsInertiaData pdata = null;
            if (!(row["PhysInertia"] is DBNull) && !string.IsNullOrEmpty(row["PhysInertia"].ToString()))
                pdata = PhysicsInertiaData.FromXml2(row["PhysInertia"].ToString());
            prim.PhysicsInertia = pdata;

            int pseudocrc = Convert.ToInt32(row["pseudocrc"]);
            if(pseudocrc != 0)
                prim.PseudoCRC = pseudocrc;

            return prim;
        }

        /// <summary>
        /// Build a prim inventory item from the persisted data.
        /// </summary>
        /// <param name="row"></param>
        /// <returns></returns>
        private static TaskInventoryItem buildItem(DataRow row)
        {
            TaskInventoryItem taskItem = new TaskInventoryItem
            {
                ItemID = new UUID((string)row["itemID"]),
                ParentPartID = new UUID((string)row["primID"]),
                AssetID = new UUID((string)row["assetID"]),
                ParentID = new UUID((string)row["parentFolderID"]),

                InvType = Convert.ToInt32(row["invType"]),
                Type = Convert.ToInt32(row["assetType"]),

                Name = (string)row["name"],
                Description = (string)row["description"],
                CreationDate = Convert.ToUInt32(row["creationDate"]),
                CreatorIdentification = (string)row["creatorID"],
                OwnerID = new UUID((string)row["ownerID"]),
                LastOwnerID = new UUID((string)row["lastOwnerID"]),
                GroupID = new UUID((string)row["groupID"]),

                NextPermissions = Convert.ToUInt32(row["nextPermissions"]),
                CurrentPermissions = Convert.ToUInt32(row["currentPermissions"]),
                BasePermissions = Convert.ToUInt32(row["basePermissions"]),
                EveryonePermissions = Convert.ToUInt32(row["everyonePermissions"]),
                GroupPermissions = Convert.ToUInt32(row["groupPermissions"]),
                Flags = Convert.ToUInt32(row["flags"])
            };

            return taskItem;
        }

        /// <summary>
        /// Build a Land Data from the persisted data.
        /// </summary>
        /// <param name="row"></param>
        /// <returns></returns>
        private LandData buildLandData(DataRow row)
        {
            LandData newData = new LandData
            {
                GlobalID = new UUID((string)row["UUID"]),
                LocalID = Convert.ToInt32(row["LocalLandID"]),

                // Bitmap is a byte[512]
                Bitmap = (byte[])row["Bitmap"],

                Name = (string)row["Name"],
                Description = (string)row["Desc"],
                OwnerID = (UUID)(string)row["OwnerUUID"],
                IsGroupOwned = Convert.ToBoolean(row["IsGroupOwned"]),
                Area = Convert.ToInt32(row["Area"]),
                AuctionID = Convert.ToUInt32(row["AuctionID"]), //Unemplemented
                Category = (ParcelCategory)Convert.ToInt32(row["Category"]),
                //Enum OpenMetaverse.Parcel.ParcelCategory
                ClaimDate = Convert.ToInt32(row["ClaimDate"]),
                ClaimPrice = Convert.ToInt32(row["ClaimPrice"]),
                GroupID = new UUID((string)row["GroupUUID"]),
                SalePrice = Convert.ToInt32(row["SalePrice"]),
                Status = (ParcelStatus)Convert.ToInt32(row["LandStatus"]),
                //Enum. OpenMetaverse.Parcel.ParcelStatus
                Flags = Convert.ToUInt32(row["LandFlags"]),
                LandingType = (byte)row["LandingType"],
                MediaAutoScale = (byte)row["MediaAutoScale"],
                MediaID = new UUID((string)row["MediaTextureUUID"]),
                MediaURL = (string)row["MediaURL"],
                MusicURL = (string)row["MusicURL"],
                PassHours = Convert.ToSingle(row["PassHours"]),
                PassPrice = Convert.ToInt32(row["PassPrice"]),
                SnapshotID = (UUID)(string)row["SnapshotUUID"],
                Dwell = Convert.ToInt32(row["Dwell"]),
                MediaType = (string)row["MediaType"],
                MediaDescription = (string)row["MediaDescription"],
                MediaWidth = Convert.ToInt32(((string)row["MediaSize"]).Split(',')[0]),
                MediaHeight = Convert.ToInt32(((string)row["MediaSize"]).Split(',')[1]),
                MediaLoop = Convert.ToBoolean(row["MediaLoop"]),
                ObscureMedia = Convert.ToBoolean(row["ObscureMedia"]),
                ObscureMusic = Convert.ToBoolean(row["ObscureMusic"]),
                SeeAVs = Convert.ToBoolean(row["SeeAVs"]),
                AnyAVSounds = Convert.ToBoolean(row["AnyAVSounds"]),
                GroupAVSounds = Convert.ToBoolean(row["GroupAVSounds"])
            };

            try
            {
                newData.UserLocation =
                    new Vector3(Convert.ToSingle(row["UserLocationX"]), Convert.ToSingle(row["UserLocationY"]),
                                  Convert.ToSingle(row["UserLocationZ"]));
                newData.UserLookAt =
                    new Vector3(Convert.ToSingle(row["UserLookAtX"]), Convert.ToSingle(row["UserLookAtY"]),
                                  Convert.ToSingle(row["UserLookAtZ"]));

            }
            catch (InvalidCastException)
            {
                _log.ErrorFormat("[SQLITE REGION DB]: unable to get parcel telehub settings for {1}", newData.Name);
                newData.UserLocation = Vector3.Zero;
                newData.UserLookAt = Vector3.Zero;
            }
            newData.ParcelAccessList = new List<LandAccessEntry>();
            UUID authBuyerID = UUID.Zero;

            UUID.TryParse((string)row["AuthbuyerID"], out authBuyerID);

            newData.OtherCleanTime = Convert.ToInt32(row["OtherCleanTime"]);

            if (row["environment"] is DBNull)
            {
                newData.Environment = null;
                newData.EnvironmentVersion = -1;
            }
            else
            {
                string env = (string)row["environment"];
                if (string.IsNullOrEmpty(env))
                {
                    newData.Environment = null;
                    newData.EnvironmentVersion = -1;
                }
                else
                {
                    try
                    {
                        ViewerEnvironment VEnv = ViewerEnvironment.FromOSDString(env);
                        newData.Environment = VEnv;
                        newData.EnvironmentVersion = VEnv.version;
                    }
                    catch
                    {
                        newData.Environment = null;
                        newData.EnvironmentVersion = -1;
                    }
                }
            }


            return newData;
        }

        private RegionSettings buildRegionSettings(DataRow row)
        {
            RegionSettings newSettings = new RegionSettings
            {
                RegionUUID = new UUID((string)row["regionUUID"]),
                BlockTerraform = Convert.ToBoolean(row["block_terraform"]),
                AllowDamage = Convert.ToBoolean(row["allow_damage"]),
                BlockFly = Convert.ToBoolean(row["block_fly"]),
                RestrictPushing = Convert.ToBoolean(row["restrict_pushing"]),
                AllowLandResell = Convert.ToBoolean(row["allow_land_resell"]),
                AllowLandJoinDivide = Convert.ToBoolean(row["allow_land_join_divide"]),
                BlockShowInSearch = Convert.ToBoolean(row["block_show_in_search"]),
                AgentLimit = Convert.ToInt32(row["agent_limit"]),
                ObjectBonus = Convert.ToDouble(row["object_bonus"]),
                Maturity = Convert.ToInt32(row["maturity"]),
                DisableScripts = Convert.ToBoolean(row["disable_scripts"]),
                DisableCollisions = Convert.ToBoolean(row["disable_collisions"]),
                DisablePhysics = Convert.ToBoolean(row["disable_physics"]),
                TerrainTexture1 = new UUID((string)row["terrain_texture_1"]),
                TerrainTexture2 = new UUID((string)row["terrain_texture_2"]),
                TerrainTexture3 = new UUID((string)row["terrain_texture_3"]),
                TerrainTexture4 = new UUID((string)row["terrain_texture_4"]),
                Elevation1NW = Convert.ToDouble(row["elevation_1_nw"]),
                Elevation2NW = Convert.ToDouble(row["elevation_2_nw"]),
                Elevation1NE = Convert.ToDouble(row["elevation_1_ne"]),
                Elevation2NE = Convert.ToDouble(row["elevation_2_ne"]),
                Elevation1SE = Convert.ToDouble(row["elevation_1_se"]),
                Elevation2SE = Convert.ToDouble(row["elevation_2_se"]),
                Elevation1SW = Convert.ToDouble(row["elevation_1_sw"]),
                Elevation2SW = Convert.ToDouble(row["elevation_2_sw"]),
                WaterHeight = Convert.ToDouble(row["water_height"]),
                TerrainRaiseLimit = Convert.ToDouble(row["terrain_raise_limit"]),
                TerrainLowerLimit = Convert.ToDouble(row["terrain_lower_limit"]),
                UseEstateSun = Convert.ToBoolean(row["use_estate_sun"]),
                Sandbox = Convert.ToBoolean(row["sandbox"]),
                SunVector = new Vector3(
                                     Convert.ToSingle(row["sunvectorx"]),
                                     Convert.ToSingle(row["sunvectory"]),
                                     Convert.ToSingle(row["sunvectorz"])
                                     ),
                FixedSun = Convert.ToBoolean(row["fixed_sun"]),
                SunPosition = Convert.ToDouble(row["sun_position"]),
                Covenant = new UUID((string)row["covenant"]),
                CovenantChangedDateTime = Convert.ToInt32(row["covenant_datetime"]),
                TerrainImageID = new UUID((string)row["map_tile_ID"]),
                TelehubObject = new UUID((string)row["TelehubObject"]),
                ParcelImageID = new UUID((string)row["parcel_tile_ID"]),
                GodBlockSearch = Convert.ToBoolean(row["block_search"]),
                Casino = Convert.ToBoolean(row["casino"])
            };
            if (!(row["cacheID"] is System.DBNull))
                newSettings.CacheID = new UUID((string)row["cacheID"]);

            return newSettings;
        }

        /// <summary>
        /// Build a land access entry from the persisted data.
        /// </summary>
        /// <param name="row"></param>
        /// <returns></returns>
        private static LandAccessEntry buildLandAccessData(DataRow row)
        {
            LandAccessEntry entry = new LandAccessEntry
            {
                AgentID = new UUID((string)row["AccessUUID"]),
                Flags = (AccessList)row["Flags"],
                Expires = 0
            };
            return entry;
        }


        /// <summary>
        ///
        /// </summary>
        /// <param name="row"></param>
        /// <param name="prim"></param>
        /// <param name="sceneGroupID"></param>
        /// <param name="regionUUID"></param>
        private static void fillPrimRow(DataRow row, SceneObjectPart prim, UUID sceneGroupID, UUID regionUUID)
        {
            row["UUID"] = prim.UUID.ToString();
            row["RegionUUID"] = regionUUID.ToString();
            row["CreationDate"] = prim.CreationDate;
            row["Name"] = prim.Name;
            row["SceneGroupID"] = sceneGroupID.ToString();
            // the UUID of the root part for this SceneObjectGroup
            // various text fields
            row["Text"] = prim.Text;
            row["Description"] = prim.Description;
            row["SitName"] = prim.SitName;
            row["TouchName"] = prim.TouchName;
            // permissions
            row["ObjectFlags"] = (uint)prim.Flags;
            row["CreatorID"] = prim.CreatorIdentification.ToString();
            row["OwnerID"] = prim.OwnerID.ToString();
            row["GroupID"] = prim.GroupID.ToString();
            row["LastOwnerID"] = prim.LastOwnerID.ToString();
            row["RezzerID"] = prim.RezzerID.ToString();
            row["OwnerMask"] = prim.OwnerMask;
            row["NextOwnerMask"] = prim.NextOwnerMask;
            row["GroupMask"] = prim.GroupMask;
            row["EveryoneMask"] = prim.EveryoneMask;
            row["BaseMask"] = prim.BaseMask;
            // vectors
            row["PositionX"] = prim.OffsetPosition.X;
            row["PositionY"] = prim.OffsetPosition.Y;
            row["PositionZ"] = prim.OffsetPosition.Z;
            row["GroupPositionX"] = prim.GroupPosition.X;
            row["GroupPositionY"] = prim.GroupPosition.Y;
            row["GroupPositionZ"] = prim.GroupPosition.Z;
            row["VelocityX"] = prim.Velocity.X;
            row["VelocityY"] = prim.Velocity.Y;
            row["VelocityZ"] = prim.Velocity.Z;
            row["AngularVelocityX"] = prim.AngularVelocity.X;
            row["AngularVelocityY"] = prim.AngularVelocity.Y;
            row["AngularVelocityZ"] = prim.AngularVelocity.Z;
            row["AccelerationX"] = prim.Acceleration.X;
            row["AccelerationY"] = prim.Acceleration.Y;
            row["AccelerationZ"] = prim.Acceleration.Z;
            // quaternions
            row["RotationX"] = prim.RotationOffset.X;
            row["RotationY"] = prim.RotationOffset.Y;
            row["RotationZ"] = prim.RotationOffset.Z;
            row["RotationW"] = prim.RotationOffset.W;

            // Sit target
            Vector3 sitTargetPos = prim.SitTargetPositionLL;
            row["SitTargetOffsetX"] = sitTargetPos.X;
            row["SitTargetOffsetY"] = sitTargetPos.Y;
            row["SitTargetOffsetZ"] = sitTargetPos.Z;

            Quaternion sitTargetOrient = prim.SitTargetOrientationLL;
            row["SitTargetOrientW"] = sitTargetOrient.W;
            row["SitTargetOrientX"] = sitTargetOrient.X;
            row["SitTargetOrientY"] = sitTargetOrient.Y;
            row["SitTargetOrientZ"] = sitTargetOrient.Z;

            Vector3 standTarget = prim.StandOffset;
            row["standtargetx"] = standTarget.X;
            row["standtargety"] = standTarget.Y;
            row["standtargetz"] = standTarget.Z;

            row["sitactrange"] = prim.SitActiveRange;

            row["ColorR"] = Convert.ToInt32(prim.Color.R);
            row["ColorG"] = Convert.ToInt32(prim.Color.G);
            row["ColorB"] = Convert.ToInt32(prim.Color.B);
            row["ColorA"] = Convert.ToInt32(prim.Color.A);
            row["PayPrice"] = prim.PayPrice[0];
            row["PayButton1"] = prim.PayPrice[1];
            row["PayButton2"] = prim.PayPrice[2];
            row["PayButton3"] = prim.PayPrice[3];
            row["PayButton4"] = prim.PayPrice[4];

            row["TextureAnimation"] = Convert.ToBase64String(prim.TextureAnimation);
            row["ParticleSystem"] = Convert.ToBase64String(prim.ParticleSystem);

            row["CameraEyeOffsetX"] = prim.GetCameraEyeOffset().X;
            row["CameraEyeOffsetY"] = prim.GetCameraEyeOffset().Y;
            row["CameraEyeOffsetZ"] = prim.GetCameraEyeOffset().Z;

            row["CameraAtOffsetX"] = prim.GetCameraAtOffset().X;
            row["CameraAtOffsetY"] = prim.GetCameraAtOffset().Y;
            row["CameraAtOffsetZ"] = prim.GetCameraAtOffset().Z;

            if ((prim.SoundFlags & 1) != 0) // Looped
            {
                row["LoopedSound"] = prim.Sound.ToString();
                row["LoopedSoundGain"] = prim.SoundGain;
            }
            else
            {
                row["LoopedSound"] = UUID.Zero.ToString();
                row["LoopedSoundGain"] = 0.0f;
            }

            if (prim.GetForceMouselook())
                row["ForceMouselook"] = 1;
            else
                row["ForceMouselook"] = 0;

            row["ScriptAccessPin"] = prim.ScriptAccessPin;

            if (prim.AllowedDrop)
                row["AllowedDrop"] = 1;
            else
                row["AllowedDrop"] = 0;

            if (prim.DIE_AT_EDGE)
                row["DieAtEdge"] = 1;
            else
                row["DieAtEdge"] = 0;

            row["SalePrice"] = prim.SalePrice;
            row["SaleType"] = Convert.ToInt16(prim.ObjectSaleType);

            // click action
            row["ClickAction"] = prim.ClickAction;

            row["Material"] = prim.Material;

            row["CollisionSound"] = prim.CollisionSound.ToString();
            row["CollisionSoundVolume"] = prim.CollisionSoundVolume;
            if (prim.VolumeDetectActive)
                row["VolumeDetect"] = 1;
            else
                row["VolumeDetect"] = 0;

            row["MediaURL"] = prim.MediaUrl;

            row["AttachedPosX"] = prim.AttachedPos.X;
            row["AttachedPosY"] = prim.AttachedPos.Y;
            row["AttachedPosZ"] = prim.AttachedPos.Z;

            if (prim.DynAttrs!= null && prim.DynAttrs.CountNamespaces > 0)
                row["DynAttrs"] = prim.DynAttrs.ToXml();
            else
                row["DynAttrs"] = null;

            row["PhysicsShapeType"] = prim.PhysicsShapeType;
            row["Density"] = (double)prim.Density;
            row["GravityModifier"] = (double)prim.GravityModifier;
            row["Friction"] = (double)prim.Friction;
            row["Restitution"] = (double)prim.Restitution;

            if (prim.KeyframeMotion != null)
                row["KeyframeMotion"] = prim.KeyframeMotion.Serialize();
            else
                row["KeyframeMotion"] = new byte[0];

            row["PassTouches"] = prim.PassTouches;
            row["PassCollisions"] = prim.PassCollisions;
            row["RotationAxisLocks"] = prim.RotationAxisLocks;

            if (prim.VehicleParams != null)
                row["Vehicle"] = prim.VehicleParams.ToXml2();
            else
                row["Vehicle"] = string.Empty;

            if (prim.PhysicsInertia != null)
                row["PhysInertia"] = prim.PhysicsInertia.ToXml2();
            else
                row["PhysInertia"] = string.Empty;

            row["pseudocrc"] = prim.PseudoCRC;
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="row"></param>
        /// <param name="taskItem"></param>
        private static void fillItemRow(DataRow row, TaskInventoryItem taskItem)
        {
            row["itemID"] = taskItem.ItemID.ToString();
            row["primID"] = taskItem.ParentPartID.ToString();
            row["assetID"] = taskItem.AssetID.ToString();
            row["parentFolderID"] = taskItem.ParentID.ToString();

            row["invType"] = taskItem.InvType;
            row["assetType"] = taskItem.Type;

            row["name"] = taskItem.Name;
            row["description"] = taskItem.Description;
            row["creationDate"] = taskItem.CreationDate;
            row["creatorID"] = taskItem.CreatorIdentification.ToString();
            row["ownerID"] = taskItem.OwnerID.ToString();
            row["lastOwnerID"] = taskItem.LastOwnerID.ToString();
            row["groupID"] = taskItem.GroupID.ToString();
            row["nextPermissions"] = taskItem.NextPermissions;
            row["currentPermissions"] = taskItem.CurrentPermissions;
            row["basePermissions"] = taskItem.BasePermissions;
            row["everyonePermissions"] = taskItem.EveryonePermissions;
            row["groupPermissions"] = taskItem.GroupPermissions;
            row["flags"] = taskItem.Flags;
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="row"></param>
        /// <param name="land"></param>
        /// <param name="regionUUID"></param>
        private static void fillLandRow(DataRow row, LandData land, UUID regionUUID)
        {
            row["UUID"] = land.GlobalID.ToString();
            row["RegionUUID"] = regionUUID.ToString();
            row["LocalLandID"] = land.LocalID;

            // Bitmap is a byte[512]
            row["Bitmap"] = land.Bitmap;

            row["Name"] = land.Name;
            row["Desc"] = land.Description;
            row["OwnerUUID"] = land.OwnerID.ToString();
            row["IsGroupOwned"] = land.IsGroupOwned.ToString();
            row["Area"] = land.Area;
            row["AuctionID"] = land.AuctionID; //Unemplemented
            row["Category"] = land.Category; //Enum OpenMetaverse.Parcel.ParcelCategory
            row["ClaimDate"] = land.ClaimDate;
            row["ClaimPrice"] = land.ClaimPrice;
            row["GroupUUID"] = land.GroupID.ToString();
            row["SalePrice"] = land.SalePrice;
            row["LandStatus"] = land.Status; //Enum. OpenMetaverse.Parcel.ParcelStatus
            row["LandFlags"] = land.Flags;
            row["LandingType"] = land.LandingType;
            row["MediaAutoScale"] = land.MediaAutoScale;
            row["MediaTextureUUID"] = land.MediaID.ToString();
            row["MediaURL"] = land.MediaURL;
            row["MusicURL"] = land.MusicURL;
            row["PassHours"] = land.PassHours;
            row["PassPrice"] = land.PassPrice;
            row["SnapshotUUID"] = land.SnapshotID.ToString();
            row["UserLocationX"] = land.UserLocation.X;
            row["UserLocationY"] = land.UserLocation.Y;
            row["UserLocationZ"] = land.UserLocation.Z;
            row["UserLookAtX"] = land.UserLookAt.X;
            row["UserLookAtY"] = land.UserLookAt.Y;
            row["UserLookAtZ"] = land.UserLookAt.Z;
            row["AuthbuyerID"] = land.AuthBuyerID.ToString();
            row["OtherCleanTime"] = land.OtherCleanTime;
            row["Dwell"] = land.Dwell;
            row["MediaType"] = land.MediaType;
            row["MediaDescription"] = land.MediaDescription;
            row["MediaSize"] = string.Format("{0},{1}", land.MediaWidth, land.MediaHeight);
            row["MediaLoop"] = land.MediaLoop;
            row["ObscureMusic"] = land.ObscureMusic;
            row["ObscureMedia"] = land.ObscureMedia;
            row["SeeAVs"] = land.SeeAVs;
            row["AnyAVSounds"] = land.AnyAVSounds;
            row["GroupAVSounds"] = land.GroupAVSounds;

            if (land.Environment == null)
                row["environment"] = "";
            else
            {
                try
                {
                    row["environment"] = ViewerEnvironment.ToOSDString(land.Environment);
                }
                catch
                {
                    row["environment"] = "";
                }
            }

        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="row"></param>
        /// <param name="entry"></param>
        /// <param name="parcelID"></param>
        private static void fillLandAccessRow(DataRow row, LandAccessEntry entry, UUID parcelID)
        {
            row["LandUUID"] = parcelID.ToString();
            row["AccessUUID"] = entry.AgentID.ToString();
            row["Flags"] = entry.Flags;
        }

        private static void fillRegionSettingsRow(DataRow row, RegionSettings settings)
        {
            row["regionUUID"] = settings.RegionUUID.ToString();
            row["block_terraform"] = settings.BlockTerraform;
            row["block_fly"] = settings.BlockFly;
            row["allow_damage"] = settings.AllowDamage;
            row["restrict_pushing"] = settings.RestrictPushing;
            row["allow_land_resell"] = settings.AllowLandResell;
            row["allow_land_join_divide"] = settings.AllowLandJoinDivide;
            row["block_show_in_search"] = settings.BlockShowInSearch;
            row["agent_limit"] = settings.AgentLimit;
            row["object_bonus"] = settings.ObjectBonus;
            row["maturity"] = settings.Maturity;
            row["disable_scripts"] = settings.DisableScripts;
            row["disable_collisions"] = settings.DisableCollisions;
            row["disable_physics"] = settings.DisablePhysics;
            row["terrain_texture_1"] = settings.TerrainTexture1.ToString();
            row["terrain_texture_2"] = settings.TerrainTexture2.ToString();
            row["terrain_texture_3"] = settings.TerrainTexture3.ToString();
            row["terrain_texture_4"] = settings.TerrainTexture4.ToString();
            row["elevation_1_nw"] = settings.Elevation1NW;
            row["elevation_2_nw"] = settings.Elevation2NW;
            row["elevation_1_ne"] = settings.Elevation1NE;
            row["elevation_2_ne"] = settings.Elevation2NE;
            row["elevation_1_se"] = settings.Elevation1SE;
            row["elevation_2_se"] = settings.Elevation2SE;
            row["elevation_1_sw"] = settings.Elevation1SW;
            row["elevation_2_sw"] = settings.Elevation2SW;
            row["water_height"] = settings.WaterHeight;
            row["terrain_raise_limit"] = settings.TerrainRaiseLimit;
            row["terrain_lower_limit"] = settings.TerrainLowerLimit;
            row["use_estate_sun"] = settings.UseEstateSun;
            row["sandbox"] = settings.Sandbox; // unlike other database modules, sqlite uses a lower case s for sandbox!
            row["sunvectorx"] = settings.SunVector.X;
            row["sunvectory"] = settings.SunVector.Y;
            row["sunvectorz"] = settings.SunVector.Z;
            row["fixed_sun"] = settings.FixedSun;
            row["sun_position"] = settings.SunPosition;
            row["covenant"] = settings.Covenant.ToString();
            row["covenant_datetime"] = settings.CovenantChangedDateTime;
            row["map_tile_ID"] = settings.TerrainImageID.ToString();
            row["TelehubObject"] = settings.TelehubObject.ToString();
            row["parcel_tile_ID"] = settings.ParcelImageID.ToString();
            row["block_search"] = settings.GodBlockSearch;
            row["casino"] = settings.Casino;
            row["cacheID"] = settings.CacheID;
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="row"></param>
        /// <returns></returns>
        private PrimitiveBaseShape buildShape(DataRow row)
        {
            PrimitiveBaseShape s = new PrimitiveBaseShape
            {
                Scale = new Vector3(
                Convert.ToSingle(row["ScaleX"]),
                Convert.ToSingle(row["ScaleY"]),
                Convert.ToSingle(row["ScaleZ"])
                ),
                // paths
                PCode = Convert.ToByte(row["PCode"]),
                PathBegin = Convert.ToUInt16(row["PathBegin"]),
                PathEnd = Convert.ToUInt16(row["PathEnd"]),
                PathScaleX = Convert.ToByte(row["PathScaleX"]),
                PathScaleY = Convert.ToByte(row["PathScaleY"]),
                PathShearX = Convert.ToByte(row["PathShearX"]),
                PathShearY = Convert.ToByte(row["PathShearY"]),
                PathSkew = Convert.ToSByte(row["PathSkew"]),
                PathCurve = Convert.ToByte(row["PathCurve"]),
                PathRadiusOffset = Convert.ToSByte(row["PathRadiusOffset"]),
                PathRevolutions = Convert.ToByte(row["PathRevolutions"]),
                PathTaperX = Convert.ToSByte(row["PathTaperX"]),
                PathTaperY = Convert.ToSByte(row["PathTaperY"]),
                PathTwist = Convert.ToSByte(row["PathTwist"]),
                PathTwistBegin = Convert.ToSByte(row["PathTwistBegin"]),
                // profile
                ProfileBegin = Convert.ToUInt16(row["ProfileBegin"]),
                ProfileEnd = Convert.ToUInt16(row["ProfileEnd"]),
                ProfileCurve = Convert.ToByte(row["ProfileCurve"]),
                ProfileHollow = Convert.ToUInt16(row["ProfileHollow"]),
                State = Convert.ToByte(row["State"]),
                LastAttachPoint = Convert.ToByte(row["LastAttachPoint"])
            };

            byte[] textureEntry = (byte[])row["Texture"];
            s.TextureEntry = textureEntry;

            s.ExtraParams = (byte[])row["ExtraParams"];

            if (!(row["Media"] is System.DBNull))
                s.Media = PrimitiveBaseShape.MediaList.FromXml((string)row["Media"]);

            return s;
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="row"></param>
        /// <param name="prim"></param>
        private static void fillShapeRow(DataRow row, SceneObjectPart prim)
        {
            PrimitiveBaseShape s = prim.Shape;
            row["UUID"] = prim.UUID.ToString();
            // shape is an enum
            row["Shape"] = 0;
            // vectors
            row["ScaleX"] = s.Scale.X;
            row["ScaleY"] = s.Scale.Y;
            row["ScaleZ"] = s.Scale.Z;
            // paths
            row["PCode"] = s.PCode;
            row["PathBegin"] = s.PathBegin;
            row["PathEnd"] = s.PathEnd;
            row["PathScaleX"] = s.PathScaleX;
            row["PathScaleY"] = s.PathScaleY;
            row["PathShearX"] = s.PathShearX;
            row["PathShearY"] = s.PathShearY;
            row["PathSkew"] = s.PathSkew;
            row["PathCurve"] = s.PathCurve;
            row["PathRadiusOffset"] = s.PathRadiusOffset;
            row["PathRevolutions"] = s.PathRevolutions;
            row["PathTaperX"] = s.PathTaperX;
            row["PathTaperY"] = s.PathTaperY;
            row["PathTwist"] = s.PathTwist;
            row["PathTwistBegin"] = s.PathTwistBegin;
            // profile
            row["ProfileBegin"] = s.ProfileBegin;
            row["ProfileEnd"] = s.ProfileEnd;
            row["ProfileCurve"] = s.ProfileCurve;
            row["ProfileHollow"] = s.ProfileHollow;
            row["State"] = s.State;
            row["LastAttachPoint"] = s.LastAttachPoint;

            row["Texture"] = s.TextureEntry;
            row["ExtraParams"] = s.ExtraParams;

            if (s.Media != null)
                row["Media"] = s.Media.ToXml();
        }

        /// <summary>
        /// Persistently store a prim.
        /// </summary>
        /// <param name="prim"></param>
        /// <param name="sceneGroupID"></param>
        /// <param name="regionUUID"></param>
        private void addPrim(SceneObjectPart prim, UUID sceneGroupID, UUID regionUUID)
        {
            DataTable prims = ds.Tables["prims"];
            DataTable shapes = ds.Tables["primshapes"];

            DataRow primRow = prims.Rows.Find(prim.UUID.ToString());
            if (primRow == null)
            {
                primRow = prims.NewRow();
                fillPrimRow(primRow, prim, sceneGroupID, regionUUID);
                prims.Rows.Add(primRow);
            }
            else
            {
                fillPrimRow(primRow, prim, sceneGroupID, regionUUID);
            }

            DataRow shapeRow = shapes.Rows.Find(prim.UUID.ToString());
            if (shapeRow == null)
            {
                shapeRow = shapes.NewRow();
                fillShapeRow(shapeRow, prim);
                shapes.Rows.Add(shapeRow);
            }
            else
            {
                fillShapeRow(shapeRow, prim);
            }
        }

        /// <summary>
        /// </summary>
        /// <param name="primID"></param>
        /// <param name="items"></param>
        public void StorePrimInventory(UUID primID, ICollection<TaskInventoryItem> items)
        {
//            _log.DebugFormat("[SQLITE REGION DB]: Entered StorePrimInventory with prim ID {0}", primID);

            DataTable dbItems = ds.Tables["primitems"];

            // For now, we're just going to crudely remove all the previous inventory items
            // no matter whether they have changed or not, and replace them with the current set.
            lock (ds)
            {
                RemoveItems(primID);

                // repalce with current inventory details
                foreach (TaskInventoryItem newItem in items)
                {
                    //                    _log.InfoFormat(
                    //                        "[DATASTORE]: ",
                    //                        "Adding item {0}, {1} to prim ID {2}",
                    //                        newItem.Name, newItem.ItemID, newItem.ParentPartID);

                    DataRow newItemRow = dbItems.NewRow();
                    fillItemRow(newItemRow, newItem);
                    dbItems.Rows.Add(newItemRow);
                }
                Commit();
            }
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
        private static SqliteCommand createInsertCommand(string table, DataTable dt)
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
//            _log.DebugFormat("[SQLITE]: Created insert command {0}", sql);
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
        private static SqliteCommand createUpdateCommand(string table, string pk, DataTable dt)
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
        /// create an update command
        /// </summary>
        /// <param name="table">table name</param>
        /// <param name="pk"></param>
        /// <param name="dt"></param>
        /// <returns>the created command</returns>
        private static SqliteCommand createUpdateCommand(string table, string pk1, string pk2, DataTable dt)
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
            sql += " where " + pk1 + " and " + pk2;
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
        // private static string defineTable(DataTable dt)
        // {
        //     string sql = "create table " + dt.TableName + "(";
        //     string subsql = String.Empty;
        //     foreach (DataColumn col in dt.Columns)
        //     {
        //         if (subsql.Length > 0)
        //         {
        //             // a map function would rock so much here
        //             subsql += ",\n";
        //         }
        //         subsql += col.ColumnName + " " + sqliteType(col.DataType);
        //         if (dt.PrimaryKey.Length > 0 && col == dt.PrimaryKey[0])
        //         {
        //             subsql += " primary key";
        //         }
        //     }
        //     sql += subsql;
        //     sql += ")";
        //     return sql;
        // }

        /***********************************************************************
         *
         *  Database Binding functions
         *
         *  These will be db specific due to typing, and minor differences
         *  in databases.
         *
         **********************************************************************/

        ///<summary>
        /// This is a convenience function that collapses 5 repetitive
        /// lines for defining SqliteParameters to 2 parameters:
        /// column name and database type.
        ///
        /// It assumes certain conventions like :param as the param
        /// name to replace in parametrized queries, and that source
        /// version is always current version, both of which are fine
        /// for us.
        ///</summary>
        ///<returns>a built sqlite parameter</returns>
        private static SqliteParameter createSqliteParameter(string name, Type type)
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

        /// <summary>
        ///
        /// </summary>
        /// <param name="da"></param>
        /// <param name="conn"></param>
        private void setupPrimCommands(SqliteDataAdapter da, SqliteConnection conn)
        {
            da.InsertCommand = createInsertCommand("prims", ds.Tables["prims"]);
            da.InsertCommand.Connection = conn;

            da.UpdateCommand = createUpdateCommand("prims", "UUID=:UUID", ds.Tables["prims"]);
            da.UpdateCommand.Connection = conn;

            SqliteCommand delete = new SqliteCommand("delete from prims where UUID = :UUID");
            delete.Parameters.Add(createSqliteParameter("UUID", typeof(string)));
            delete.Connection = conn;
            da.DeleteCommand = delete;
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="da"></param>
        /// <param name="conn"></param>
        private void setupItemsCommands(SqliteDataAdapter da, SqliteConnection conn)
        {
            da.InsertCommand = createInsertCommand("primitems", ds.Tables["primitems"]);
            da.InsertCommand.Connection = conn;

            da.UpdateCommand = createUpdateCommand("primitems", "itemID = :itemID", ds.Tables["primitems"]);
            da.UpdateCommand.Connection = conn;

            SqliteCommand delete = new SqliteCommand("delete from primitems where itemID = :itemID");
            delete.Parameters.Add(createSqliteParameter("itemID", typeof(string)));
            delete.Connection = conn;
            da.DeleteCommand = delete;
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="da"></param>
        /// <param name="conn"></param>
        private void setupTerrainCommands(SqliteDataAdapter da, SqliteConnection conn)
        {
            da.InsertCommand = createInsertCommand("terrain", ds.Tables["terrain"]);
            da.InsertCommand.Connection = conn;
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="da"></param>
        /// <param name="conn"></param>
        private void setupLandCommands(SqliteDataAdapter da, SqliteConnection conn)
        {
            da.InsertCommand = createInsertCommand("land", ds.Tables["land"]);
            da.InsertCommand.Connection = conn;

            da.UpdateCommand = createUpdateCommand("land", "UUID=:UUID", ds.Tables["land"]);
            da.UpdateCommand.Connection = conn;

            SqliteCommand delete = new SqliteCommand("delete from land where UUID=:UUID");
            delete.Parameters.Add(createSqliteParameter("UUID", typeof(string)));
            da.DeleteCommand = delete;
            da.DeleteCommand.Connection = conn;
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="da"></param>
        /// <param name="conn"></param>
        private void setupLandAccessCommands(SqliteDataAdapter da, SqliteConnection conn)
        {
            da.InsertCommand = createInsertCommand("landaccesslist", ds.Tables["landaccesslist"]);
            da.InsertCommand.Connection = conn;

            da.UpdateCommand = createUpdateCommand("landaccesslist", "LandUUID=:landUUID", "AccessUUID=:AccessUUID", ds.Tables["landaccesslist"]);
            da.UpdateCommand.Connection = conn;

            SqliteCommand delete = new SqliteCommand("delete from landaccesslist where LandUUID= :LandUUID and AccessUUID= :AccessUUID");
            delete.Parameters.Add(createSqliteParameter("LandUUID", typeof(string)));
            delete.Parameters.Add(createSqliteParameter("AccessUUID", typeof(string)));
            da.DeleteCommand = delete;
            da.DeleteCommand.Connection = conn;
        }

        private void setupRegionSettingsCommands(SqliteDataAdapter da, SqliteConnection conn)
        {
            da.InsertCommand = createInsertCommand("regionsettings", ds.Tables["regionsettings"]);
            da.InsertCommand.Connection = conn;
            da.UpdateCommand = createUpdateCommand("regionsettings", "regionUUID=:regionUUID", ds.Tables["regionsettings"]);
            da.UpdateCommand.Connection = conn;
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="da"></param>
        /// <param name="conn"></param>
        private void setupRegionWindlightCommands(SqliteDataAdapter da, SqliteConnection conn)
        {
            da.InsertCommand = createInsertCommand("regionwindlight", ds.Tables["regionwindlight"]);
            da.InsertCommand.Connection = conn;
            da.UpdateCommand = createUpdateCommand("regionwindlight", "region_id=:region_id", ds.Tables["regionwindlight"]);
            da.UpdateCommand.Connection = conn;
        }

        private void setupRegionEnvironmentCommands(SqliteDataAdapter da, SqliteConnection conn)
        {
            da.InsertCommand = createInsertCommand("regionenvironment", ds.Tables["regionenvironment"]);
            da.InsertCommand.Connection = conn;
            da.UpdateCommand = createUpdateCommand("regionenvironment", "region_id=:region_id", ds.Tables["regionenvironment"]);
            da.UpdateCommand.Connection = conn;
        }

        private void setupRegionSpawnPointsCommands(SqliteDataAdapter da, SqliteConnection conn)
        {
            da.InsertCommand = createInsertCommand("spawn_points", ds.Tables["spawn_points"]);
            da.InsertCommand.Connection = conn;
            da.UpdateCommand = createUpdateCommand("spawn_points", "RegionID=:RegionID", ds.Tables["spawn_points"]);
            da.UpdateCommand.Connection = conn;
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="da"></param>
        /// <param name="conn"></param>
        private void setupShapeCommands(SqliteDataAdapter da, SqliteConnection conn)
        {
            da.InsertCommand = createInsertCommand("primshapes", ds.Tables["primshapes"]);
            da.InsertCommand.Connection = conn;

            da.UpdateCommand = createUpdateCommand("primshapes", "UUID=:UUID", ds.Tables["primshapes"]);
            da.UpdateCommand.Connection = conn;

            SqliteCommand delete = new SqliteCommand("delete from primshapes where UUID = :UUID");
            delete.Parameters.Add(createSqliteParameter("UUID", typeof(string)));
            delete.Connection = conn;
            da.DeleteCommand = delete;
        }

        /***********************************************************************
         *
         *  Type conversion functions
         *
         **********************************************************************/

        /// <summary>
        /// Type conversion function
        /// </summary>
        /// <param name="type"></param>
        /// <returns></returns>
        private static DbType dbtypeFromType(Type type)
        {
            if (type == typeof(string))
            {
                return DbType.String;
            }
            else if (type == typeof(int))
            {
                return DbType.Int32;
            }
            else if (type == typeof(double))
            {
                return DbType.Double;
            }
            else if (type == typeof(byte))
            {
                return DbType.Byte;
            }
            else if (type == typeof(double))
            {
                return DbType.Double;
            }
            else if (type == typeof(byte[]))
            {
                return DbType.Binary;
            }
            else if (type == typeof(bool))
            {
                return DbType.Boolean;
            }
            else
            {
                return DbType.String;
            }
        }

        static void PrintDataSet(DataSet ds)
        {
            // Print out any name and extended properties.
            Console.WriteLine("DataSet is named: {0}", ds.DataSetName);
            foreach (System.Collections.DictionaryEntry de in ds.ExtendedProperties)
            {
                Console.WriteLine("Key = {0}, Value = {1}", de.Key, de.Value);
            }
            Console.WriteLine();
            foreach (DataTable dt in ds.Tables)
            {
                Console.WriteLine("=> {0} Table:", dt.TableName);
                // Print out the column names.
                for (int curCol = 0; curCol < dt.Columns.Count; curCol++)
                {
                    Console.Write(dt.Columns[curCol].ColumnName + "\t");
                }
                Console.WriteLine("\n----------------------------------");
                // Print the DataTable.
                for (int curRow = 0; curRow < dt.Rows.Count; curRow++)
                {
                    for (int curCol = 0; curCol < dt.Columns.Count; curCol++)
                    {
                        Console.Write(dt.Rows[curRow][curCol].ToString() + "\t");
                    }
                    Console.WriteLine();
                }
            }
        }

        public UUID[] GetObjectIDs(UUID regionID)
        {
            return new UUID[0];
        }

        public void SaveExtra(UUID regionID, string name, string value)
        {
        }

        public void RemoveExtra(UUID regionID, string name)
        {
        }

        public Dictionary<string, string> GetExtra(UUID regionID)
        {
            return null;
        }
    }
}
