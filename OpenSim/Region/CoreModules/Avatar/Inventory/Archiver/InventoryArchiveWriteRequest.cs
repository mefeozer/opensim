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
using System.IO;
using System.Reflection;
using System.Xml;
using log4net;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Serialization;
using OpenSim.Framework.Serialization.External;
using OpenSim.Region.CoreModules.World.Archiver;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;
using GZipStream = Ionic.Zlib.GZipStream;
using CompressionMode = Ionic.Zlib.CompressionMode;
using CompressionLevel = Ionic.Zlib.CompressionLevel;
using PermissionMask = OpenSim.Framework.PermissionMask;

namespace OpenSim.Region.CoreModules.Avatar.Inventory.Archiver
{
    public class InventoryArchiveWriteRequest
    {
        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// Determine whether this archive will save assets.  Default is true.
        /// </summary>
        public bool SaveAssets { get; set; }

        /// <summary>
        /// Determines which items will be included in the archive, according to their permissions.
        /// Default is null, meaning no permission checks.
        /// </summary>
        public string FilterContent { get; set; }

        /// <summary>
        /// Counter for inventory items saved to archive for passing to compltion event
        /// </summary>
        public int CountItems { get; set; }

        /// <summary>
        /// Counter for inventory items skipped due to permission filter option for passing to compltion event
        /// </summary>
        public int CountFiltered { get; set; }

        /// <value>
        /// Used to select all inventory nodes in a folder but not the folder itself
        /// </value>
        private const string STAR_WILDCARD = "*";

        private readonly InventoryArchiverModule _module;
        private readonly UserAccount _userInfo;
        private string _invPath;
        protected TarArchiveWriter _archiveWriter;
        protected UuidGatherer _assetGatherer;

        /// <value>
        /// We only use this to request modules
        /// </value>
        protected Scene _scene;

        /// <value>
        /// ID of this request
        /// </value>
        protected UUID _id;

        /// <value>
        /// Used to collect the uuids of the users that we need to save into the archive
        /// </value>
        protected Dictionary<UUID, int> _userUuids = new Dictionary<UUID, int>();

        /// <value>
        /// The stream to which the inventory archive will be saved.
        /// </value>
        private readonly Stream _saveStream;

        /// <summary>
        /// Constructor
        /// </summary>
        public InventoryArchiveWriteRequest(
            UUID id, InventoryArchiverModule module, Scene scene,
            UserAccount userInfo, string invPath, string savePath)
            : this(
                id,
                module,
                scene,
                userInfo,
                invPath,
                new GZipStream(new FileStream(savePath, FileMode.Create), CompressionMode.Compress, CompressionLevel.BestCompression))
        {
        }

        /// <summary>
        /// Constructor
        /// </summary>
        public InventoryArchiveWriteRequest(
            UUID id, InventoryArchiverModule module, Scene scene,
            UserAccount userInfo, string invPath, Stream saveStream)
        {
            _id = id;
            _module = module;
            _scene = scene;
            _userInfo = userInfo;
            _invPath = invPath;
            _saveStream = saveStream;
            _assetGatherer = new UuidGatherer(_scene.AssetService);

            SaveAssets = true;
            FilterContent = null;
        }

        protected void ReceivedAllAssets(ICollection<UUID> assetsFoundUuids, ICollection<UUID> assetsNotFoundUuids, bool timedOut)
        {
            Exception reportedException = null;
            bool succeeded = true;

            try
            {
                _archiveWriter.Close();
            }
            catch (Exception e)
            {
                reportedException = e;
                succeeded = false;
            }
            finally
            {
                _saveStream.Close();
            }

            if (timedOut)
            {
                succeeded = false;
                reportedException = new Exception("Loading assets timed out");
            }

            _module.TriggerInventoryArchiveSaved(
                _id, succeeded, _userInfo, _invPath, _saveStream, reportedException, CountItems, CountFiltered);
        }

        protected void SaveInvItem(InventoryItemBase inventoryItem, string path, Dictionary<string, object> options, IUserAccountService userAccountService)
        {
            if (options.ContainsKey("exclude"))
            {
                if (((List<string>)options["exclude"]).Contains(inventoryItem.Name) ||
                    ((List<string>)options["exclude"]).Contains(inventoryItem.ID.ToString()))
                {
                    if (options.ContainsKey("verbose"))
                    {
                        _log.InfoFormat(
                            "[INVENTORY ARCHIVER]: Skipping inventory item {0} {1} at {2}",
                            inventoryItem.Name, inventoryItem.ID, path);
                    }

                    CountFiltered++;

                    return;
                }
            }

            // Check For Permissions Filter Flags
            if (!CanUserArchiveObject(_userInfo.PrincipalID, inventoryItem))
            {
                _log.InfoFormat(
                            "[INVENTORY ARCHIVER]: Insufficient permissions, skipping inventory item {0} {1} at {2}",
                            inventoryItem.Name, inventoryItem.ID, path);

                // Count Items Excluded
                CountFiltered++;

                return;
            }

            if (options.ContainsKey("verbose"))
                _log.InfoFormat(
                    "[INVENTORY ARCHIVER]: Saving item {0} {1} (asset UUID {2})",
                    inventoryItem.ID, inventoryItem.Name, inventoryItem.AssetID);

            string filename = path + CreateArchiveItemName(inventoryItem);

            // Record the creator of this item for user record purposes (which might go away soon)
            _userUuids[inventoryItem.CreatorIdAsUuid] = 1;

            string serialization = UserInventoryItemSerializer.Serialize(inventoryItem, options, userAccountService);
            _archiveWriter.WriteFile(filename, serialization);

            AssetType itemAssetType = (AssetType)inventoryItem.AssetType;

            // Count inventory items (different to asset count)
            CountItems++;
            
            // Don't chase down link asset items as they actually point to their target item IDs rather than an asset
            if (SaveAssets && itemAssetType != AssetType.Link && itemAssetType != AssetType.LinkFolder)
            {
                int curErrorCntr = _assetGatherer.ErrorCount;
                int possible = _assetGatherer.possibleNotAssetCount;
                _assetGatherer.AddForInspection(inventoryItem.AssetID);
                _assetGatherer.GatherAll();
                curErrorCntr =  _assetGatherer.ErrorCount - curErrorCntr;
                possible = _assetGatherer.possibleNotAssetCount - possible;

                if(curErrorCntr > 0 || possible > 0)
                {
                    string spath;
                    int indx = path.IndexOf("__");
                    if(indx > 0)
                         spath = path.Substring(0,indx);
                    else
                        spath = path;

                    if(curErrorCntr > 0)
                    {
                        _log.ErrorFormat("[INVENTORY ARCHIVER Warning]: item {0} '{1}', type {2}, in '{3}', contains {4} references to  missing or damaged assets",
                            inventoryItem.ID, inventoryItem.Name, itemAssetType.ToString(), spath, curErrorCntr);
                        if(possible > 0)
                            _log.WarnFormat("[INVENTORY ARCHIVER Warning]: item also contains {0} references that may be to missing or damaged assets or not a problem", possible);
                    }
                    else if(possible > 0)
                    {
                        _log.WarnFormat("[INVENTORY ARCHIVER Warning]: item {0} '{1}', type {2}, in '{3}', contains {4} references that may be to missing or damaged assets or not a problem", inventoryItem.ID, inventoryItem.Name, itemAssetType.ToString(), spath, possible);
                    }
                }
            }
        }

        /// <summary>
        /// Save an inventory folder
        /// </summary>
        /// <param name="inventoryFolder">The inventory folder to save</param>
        /// <param name="path">The path to which the folder should be saved</param>
        /// <param name="saveThisFolderItself">If true, save this folder itself.  If false, only saves contents</param>
        /// <param name="options"></param>
        /// <param name="userAccountService"></param>
        protected void SaveInvFolder(
            InventoryFolderBase inventoryFolder, string path, bool saveThisFolderItself,
            Dictionary<string, object> options, IUserAccountService userAccountService)
        {
            if (options.ContainsKey("excludefolders"))
            {
                if (((List<string>)options["excludefolders"]).Contains(inventoryFolder.Name) ||
                    ((List<string>)options["excludefolders"]).Contains(inventoryFolder.ID.ToString()))
                {
                    if (options.ContainsKey("verbose"))
                    {
                        _log.InfoFormat(
                            "[INVENTORY ARCHIVER]: Skipping folder {0} at {1}",
                            inventoryFolder.Name, path);
                    }
                    return;
                }
            }

            if (options.ContainsKey("verbose"))
                _log.InfoFormat("[INVENTORY ARCHIVER]: Saving folder {0}", inventoryFolder.Name);

            if (saveThisFolderItself)
            {
                path += CreateArchiveFolderName(inventoryFolder);

                // We need to make sure that we record empty folders
                _archiveWriter.WriteDir(path);
            }

            InventoryCollection contents
                = _scene.InventoryService.GetFolderContent(inventoryFolder.Owner, inventoryFolder.ID);

            foreach (InventoryFolderBase childFolder in contents.Folders)
            {
                SaveInvFolder(childFolder, path, true, options, userAccountService);
            }

            foreach (InventoryItemBase item in contents.Items)
            {
                SaveInvItem(item, path, options, userAccountService);
            }
        }

        /// <summary>
        /// Checks whether the user has permission to export an inventory item to an IAR.
        /// </summary>
        /// <param name="UserID">The user</param>
        /// <param name="InvItem">The inventory item</param>
        /// <returns>Whether the user is allowed to export the object to an IAR</returns>
        private bool CanUserArchiveObject(UUID UserID, InventoryItemBase InvItem)
        {
            if (FilterContent == null)
                return true;// Default To Allow Export

            bool permitted = true;

            bool canCopy = (InvItem.CurrentPermissions & (uint)PermissionMask.Copy) != 0;
            bool canTransfer = (InvItem.CurrentPermissions & (uint)PermissionMask.Transfer) != 0;
            bool canMod = (InvItem.CurrentPermissions & (uint)PermissionMask.Modify) != 0;

            if (FilterContent.Contains("C") && !canCopy)
                permitted = false;

            if (FilterContent.Contains("T") && !canTransfer)
                permitted = false;

            if (FilterContent.Contains("M") && !canMod)
                permitted = false;

            return permitted;
        }

        /// <summary>
        /// Execute the inventory write request
        /// </summary>
        public void Execute(Dictionary<string, object> options, IUserAccountService userAccountService)
        {
            if (options.ContainsKey("noassets") && (bool)options["noassets"])
                SaveAssets = false;

            // Set Permission filter if flag is set
            if (options.ContainsKey("checkPermissions"))
            {
                object temp;
                if (options.TryGetValue("checkPermissions", out temp))
                    FilterContent = temp.ToString().ToUpper();
            }

            try
            {
                InventoryFolderBase inventoryFolder = null;
                InventoryItemBase inventoryItem = null;
                InventoryFolderBase rootFolder = _scene.InventoryService.GetRootFolder(_userInfo.PrincipalID);

                bool saveFolderContentsOnly = false;

                // Eliminate double slashes and any leading / on the path.
                string[] components
                    = _invPath.Split(
                        new string[] { InventoryFolderImpl.PATH_DELIMITER }, StringSplitOptions.RemoveEmptyEntries);

                int maxComponentIndex = components.Length - 1;

                // If the path terminates with a STAR then later on we want to archive all nodes in the folder but not the
                // folder itself.  This may get more sophisicated later on
                if (maxComponentIndex >= 0 && components[maxComponentIndex] == STAR_WILDCARD)
                {
                    saveFolderContentsOnly = true;
                    maxComponentIndex--;
                }
                else if (maxComponentIndex == -1)
                {
                    // If the user has just specified "/", then don't save the root "My Inventory" folder.  This is
                    // more intuitive then requiring the user to specify "/*" for this.
                    saveFolderContentsOnly = true;
                }

                _invPath = string.Empty;
                for (int i = 0; i <= maxComponentIndex; i++)
                {
                    _invPath += components[i] + InventoryFolderImpl.PATH_DELIMITER;
                }

                // Annoyingly Split actually returns the original string if the input string consists only of delimiters
                // Therefore if we still start with a / after the split, then we need the root folder
                if (_invPath.Length == 0)
                {
                    inventoryFolder = rootFolder;
                }
                else
                {
                    _invPath = _invPath.Remove(_invPath.LastIndexOf(InventoryFolderImpl.PATH_DELIMITER));
                    List<InventoryFolderBase> candidateFolders
                        = InventoryArchiveUtils.FindFoldersByPath(_scene.InventoryService, rootFolder, _invPath);
                    if (candidateFolders.Count > 0)
                        inventoryFolder = candidateFolders[0];
                }

                // The path may point to an item instead
                if (inventoryFolder == null)
                    inventoryItem = InventoryArchiveUtils.FindItemByPath(_scene.InventoryService, rootFolder, _invPath);

                if (null == inventoryFolder && null == inventoryItem)
                {
                    // We couldn't find the path indicated
                    string errorMessage = string.Format("Aborted save.  Could not find inventory path {0}", _invPath);
                    Exception e = new InventoryArchiverException(errorMessage);
                    _module.TriggerInventoryArchiveSaved(_id, false, _userInfo, _invPath, _saveStream, e, 0, 0);
                    if(_saveStream != null && _saveStream.CanWrite)
                       _saveStream.Close(); 
                    throw e;
                }

                _archiveWriter = new TarArchiveWriter(_saveStream);

                _log.InfoFormat("[INVENTORY ARCHIVER]: Adding control file to archive.");

                // Write out control file.  This has to be done first so that subsequent loaders will see this file first
                // XXX: I know this is a weak way of doing it since external non-OAR aware tar executables will not do this
                // not sure how to fix this though, short of going with a completely different file format.
                _archiveWriter.WriteFile(ArchiveConstants.CONTROL_FILE_PATH, CreateControlFile(options));

                if (inventoryFolder != null)
                {
                    _log.DebugFormat(
                        "[INVENTORY ARCHIVER]: Found folder {0} {1} at {2}",
                        inventoryFolder.Name,
                        inventoryFolder.ID,
                        string.IsNullOrEmpty(_invPath) ? InventoryFolderImpl.PATH_DELIMITER : _invPath);

                    //recurse through all dirs getting dirs and files
                    SaveInvFolder(inventoryFolder, ArchiveConstants.INVENTORY_PATH, !saveFolderContentsOnly, options, userAccountService);
                }
                else if (inventoryItem != null)
                {
                    _log.DebugFormat(
                        "[INVENTORY ARCHIVER]: Found item {0} {1} at {2}",
                        inventoryItem.Name, inventoryItem.ID, _invPath);

                    SaveInvItem(inventoryItem, ArchiveConstants.INVENTORY_PATH, options, userAccountService);
                }

                // Don't put all this profile information into the archive right now.
                //SaveUsers();

                if (SaveAssets)
                {
                    _assetGatherer.GatherAll();

                    int errors = _assetGatherer.FailedUUIDs.Count;

                    _log.DebugFormat(
                        "[INVENTORY ARCHIVER]: The items to save reference {0} possible assets", _assetGatherer.GatheredUuids.Count + errors);
                    if(errors > 0)
                        _log.DebugFormat("[INVENTORY ARCHIVER]: {0} of these have problems or are not assets and will be ignored", errors);

                    AssetsRequest ar = new AssetsRequest(
                            new AssetsArchiver(_archiveWriter),
                            _assetGatherer.GatheredUuids, _assetGatherer.FailedUUIDs.Count,
                            _scene.AssetService,
                            _scene.UserAccountService, _scene.RegionInfo.ScopeID,
                            options, ReceivedAllAssets);
                   ar.Execute();
                }
                else
                {
                    _log.DebugFormat("[INVENTORY ARCHIVER]: Not saving assets since --noassets was specified");

                    ReceivedAllAssets(new List<UUID>(), new List<UUID>(), false);
                }
            }
            catch (Exception)
            {
                _saveStream.Close();
                throw;
            }
        }

        /// <summary>
        /// Save information for the users that we've collected.
        /// </summary>
        protected void SaveUsers()
        {
            _log.InfoFormat("[INVENTORY ARCHIVER]: Saving user information for {0} users", _userUuids.Count);

            foreach (UUID creatorId in _userUuids.Keys)
            {
                // Record the creator of this item
                UserAccount creator = _scene.UserAccountService.GetUserAccount(_scene.RegionInfo.ScopeID, creatorId);

                if (creator != null)
                {
                    _archiveWriter.WriteFile(
                        ArchiveConstants.USERS_PATH + creator.FirstName + " " + creator.LastName + ".xml",
                        UserProfileSerializer.Serialize(creator.PrincipalID, creator.FirstName, creator.LastName));
                }
                else
                {
                    _log.WarnFormat("[INVENTORY ARCHIVER]: Failed to get creator profile for {0}", creatorId);
                }
            }
        }

        /// <summary>
        /// Create the archive name for a particular folder.
        /// </summary>
        ///
        /// These names are prepended with an inventory folder's UUID so that more than one folder can have the
        /// same name
        ///
        /// <param name="folder"></param>
        /// <returns></returns>
        public static string CreateArchiveFolderName(InventoryFolderBase folder)
        {
            return CreateArchiveFolderName(folder.Name, folder.ID);
        }

        /// <summary>
        /// Create the archive name for a particular item.
        /// </summary>
        ///
        /// These names are prepended with an inventory item's UUID so that more than one item can have the
        /// same name
        ///
        /// <param name="item"></param>
        /// <returns></returns>
        public static string CreateArchiveItemName(InventoryItemBase item)
        {
            return CreateArchiveItemName(item.Name, item.ID);
        }

        /// <summary>
        /// Create an archive folder name given its constituent components
        /// </summary>
        /// <param name="name"></param>
        /// <param name="id"></param>
        /// <returns></returns>
        public static string CreateArchiveFolderName(string name, UUID id)
        {
            return string.Format(
                "{0}{1}{2}/",
                InventoryArchiveUtils.EscapeArchivePath(name),
                ArchiveConstants.INVENTORY_NODE_NAME_COMPONENT_SEPARATOR,
                id);
        }

        /// <summary>
        /// Create an archive item name given its constituent components
        /// </summary>
        /// <param name="name"></param>
        /// <param name="id"></param>
        /// <returns></returns>
        public static string CreateArchiveItemName(string name, UUID id)
        {
            return string.Format(
                "{0}{1}{2}.xml",
                InventoryArchiveUtils.EscapeArchivePath(name),
                ArchiveConstants.INVENTORY_NODE_NAME_COMPONENT_SEPARATOR,
                id);
        }

        /// <summary>
        /// Create the control file for the archive
        /// </summary>
        /// <param name="options"></param>
        /// <returns></returns>
        public string CreateControlFile(Dictionary<string, object> options)
        {
            int majorVersion, minorVersion;

            if (options.ContainsKey("home"))
            {
                majorVersion = 1;
                minorVersion = 2;
            }
            else
            {
                majorVersion = 0;
                minorVersion = 3;
            }

            _log.InfoFormat("[INVENTORY ARCHIVER]: Creating version {0}.{1} IAR", majorVersion, minorVersion);

            StringWriter sw = new StringWriter();
            XmlTextWriter xtw = new XmlTextWriter(sw)
            {
                Formatting = Formatting.Indented
            };
            xtw.WriteStartDocument();
            xtw.WriteStartElement("archive");
            xtw.WriteAttributeString("major_version", majorVersion.ToString());
            xtw.WriteAttributeString("minor_version", minorVersion.ToString());

            xtw.WriteElementString("assets_included", SaveAssets.ToString());

            xtw.WriteEndElement();

            xtw.Flush();
            xtw.Close();

            string s = sw.ToString();
            sw.Close();

            return s;
        }
    }
}
