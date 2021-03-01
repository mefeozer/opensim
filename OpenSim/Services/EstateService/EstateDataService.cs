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
using OpenMetaverse;
using Nini.Config;
using OpenSim.Services.Base;
using OpenSim.Services.Interfaces;
using OpenSim.Data;
using OpenSim.Framework;

namespace OpenSim.Services.EstateService
{
    public class EstateDataService : ServiceBase, IEstateDataService
    {
//        private static readonly ILog _log =
//                LogManager.GetLogger(
//                MethodBase.GetCurrentMethod().DeclaringType);

        protected IEstateDataStore _database;

        public EstateDataService(IConfigSource config)
            : base(config)
        {
            string dllName = string.Empty;
            string connString = string.Empty;

            // Try reading the [DatabaseService] section, if it exists
            IConfig dbConfig = config.Configs["DatabaseService"];
            if (dbConfig != null)
            {
                dllName = dbConfig.GetString("StorageProvider", string.Empty);
                connString = dbConfig.GetString("ConnectionString", string.Empty);
                connString = dbConfig.GetString("EstateConnectionString", connString);
            }

            // Try reading the [EstateDataStore] section, if it exists
            IConfig estConfig = config.Configs["EstateDataStore"];
            if (estConfig != null)
            {
                dllName = estConfig.GetString("StorageProvider", dllName);
                connString = estConfig.GetString("ConnectionString", connString);
            }

            // We tried, but this doesn't exist. We can't proceed
            if (string.IsNullOrEmpty(dllName))
                throw new Exception("No StorageProvider configured");

            _database = LoadPlugin<IEstateDataStore>(dllName, new object[] { connString });
            if (_database == null)
                throw new Exception("Could not find a storage interface in the given module");
        }

        public EstateSettings LoadEstateSettings(UUID regionID, bool create)
        {
            return _database.LoadEstateSettings(regionID, create);
        }

        public EstateSettings LoadEstateSettings(int estateID)
        {
            return _database.LoadEstateSettings(estateID);
        }

        public EstateSettings CreateNewEstate(int estateID = 0)
        {
            return _database.CreateNewEstate(estateID);
        }

        public List<EstateSettings> LoadEstateSettingsAll()
        {
            return _database.LoadEstateSettingsAll();
        }

        public void StoreEstateSettings(EstateSettings es)
        {
            _database.StoreEstateSettings(es);
        }

        public List<int> GetEstates(string search)
        {
            return _database.GetEstates(search);
        }

        public List<int> GetEstatesAll()
        {
            return _database.GetEstatesAll();
        }

        public List<int> GetEstatesByOwner(UUID ownerID)
        {
            return _database.GetEstatesByOwner(ownerID);
        }

        public bool LinkRegion(UUID regionID, int estateID)
        {
            return _database.LinkRegion(regionID, estateID);
        }

        public List<UUID> GetRegions(int estateID)
        {
            return _database.GetRegions(estateID);
        }

        public bool DeleteEstate(int estateID)
        {
            return _database.DeleteEstate(estateID);
        }
    }
}
