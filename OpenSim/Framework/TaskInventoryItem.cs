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
using OpenMetaverse;

namespace OpenSim.Framework
{
    /// <summary>
    /// Represents an item in a task inventory
    /// </summary>
    public class TaskInventoryItem : ICloneable
    {
//        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// XXX This should really be factored out into some constants class.
        /// </summary>
        private const uint FULL_MASK_PERMISSIONS_GENERAL = 2147483647;

        private UUID _assetID = UUID.Zero;

        private uint _baseMask = FULL_MASK_PERMISSIONS_GENERAL;
        private uint _creationDate = 0;
        private UUID _creatorID = UUID.Zero;
        private string _creatorData = string.Empty;
        private string _description = string.Empty;
        private uint _everyoneMask = FULL_MASK_PERMISSIONS_GENERAL;
        private uint _flags = 0;
        private UUID _groupID = UUID.Zero;
        private uint _groupMask = FULL_MASK_PERMISSIONS_GENERAL;

        private int _invType = 0;
        private UUID _itemID = UUID.Zero;
        private UUID _lastOwnerID = UUID.Zero;
        private UUID _rezzerID = UUID.Zero;
        private string _name = string.Empty;
        private uint _nextOwnerMask = FULL_MASK_PERMISSIONS_GENERAL;
        private UUID _ownerID = UUID.Zero;
        private uint _ownerMask = FULL_MASK_PERMISSIONS_GENERAL;
        private UUID _parentID = UUID.Zero; //parent folder id
        private UUID _parentPartID = UUID.Zero; // SceneObjectPart this is inside
        private UUID _permsGranter;
        private int _permsMask;
        private int _type = 0;
        private UUID _oldID;
        private UUID _loadedID = UUID.Zero;

        private bool _ownerChanged = false;

        public UUID AssetID {
            get => _assetID;
            set => _assetID = value;
        }

        public uint BasePermissions {
            get => _baseMask;
            set => _baseMask = value;
        }

        public uint CreationDate {
            get => _creationDate;
            set => _creationDate = value;
        }

        public UUID CreatorID {
            get => _creatorID;
            set => _creatorID = value;
        }

        public string CreatorData // = <profile url>;<name>
        {
            get => _creatorData;
            set => _creatorData = value;
        }

        /// <summary>
        /// Used by the DB layer to retrieve / store the entire user identification.
        /// The identification can either be a simple UUID or a string of the form
        /// uuid[;profile_url[;name]]
        /// </summary>
        public string CreatorIdentification
        {
            get
            {
                if (!string.IsNullOrEmpty(_creatorData))
                    return _creatorID.ToString() + ';' + _creatorData;
                else
                    return _creatorID.ToString();
            }
            set
            {
                if (value == null || value != null && string.IsNullOrEmpty(value))
                {
                    _creatorData = string.Empty;
                    return;
                }

                if (!value.Contains(";")) // plain UUID
                {
                    UUID uuid = UUID.Zero;
                    UUID.TryParse(value, out uuid);
                    _creatorID = uuid;
                }
                else // <uuid>[;<endpoint>[;name]]
                {
                    string name = "Unknown User";
                    string[] parts = value.Split(';');
                    if (parts.Length >= 1)
                    {
                        UUID uuid = UUID.Zero;
                        UUID.TryParse(parts[0], out uuid);
                        _creatorID = uuid;
                    }
                    if (parts.Length >= 2)
                        _creatorData = parts[1];
                    if (parts.Length >= 3)
                        name = parts[2];

                    _creatorData += ';' + name;

                }
            }
        }

        public string Description {
            get => _description;
            set => _description = value;
        }

        public uint EveryonePermissions {
            get => _everyoneMask;
            set => _everyoneMask = value;
        }

        public uint Flags {
            get => _flags;
            set => _flags = value;
        }

        public UUID GroupID {
            get => _groupID;
            set => _groupID = value;
        }

        public uint GroupPermissions {
            get => _groupMask;
            set => _groupMask = value;
        }

        public int InvType {
            get => _invType;
            set => _invType = value;
        }

        public UUID ItemID {
            get => _itemID;
            set => _itemID = value;
        }

        public UUID OldItemID {
            get => _oldID;
            set => _oldID = value;
        }

        public UUID LoadedItemID {
            get => _loadedID;
            set => _loadedID = value;
        }

        public UUID LastOwnerID {
            get => _lastOwnerID;
            set => _lastOwnerID = value;
        }

        public UUID RezzerID
        {
            get => _rezzerID;
            set => _rezzerID = value;
        }

        public string Name {
            get => _name;
            set => _name = value;
        }

        public uint NextPermissions {
            get => _nextOwnerMask;
            set => _nextOwnerMask = value;
        }

        public UUID OwnerID {
            get => _ownerID;
            set => _ownerID = value;
        }

        public uint CurrentPermissions {
            get => _ownerMask;
            set => _ownerMask = value;
        }

        public UUID ParentID {
            get => _parentID;
            set => _parentID = value;
        }

        public UUID ParentPartID {
            get => _parentPartID;
            set => _parentPartID = value;
        }

        public UUID PermsGranter {
            get => _permsGranter;
            set => _permsGranter = value;
        }

        public int PermsMask {
            get => _permsMask;
            set => _permsMask = value;
        }

        public int Type {
            get => _type;
            set => _type = value;
        }

        public bool OwnerChanged
        {
            get => _ownerChanged;
            set => _ownerChanged = value;
            //                _log.DebugFormat(
            //                    "[TASK INVENTORY ITEM]: Owner changed set {0} for {1} {2} owned by {3}",
            //                    _ownerChanged, Name, ItemID, OwnerID);
        }

        /// <summary>
        /// This used ONLY during copy. It can't be relied on at other times!
        /// </summary>
        /// <remarks>
        /// For true script running status, use IEntityInventory.TryGetScriptInstanceRunning() for now.
        /// </remarks>
        public bool ScriptRunning { get; set; }

        // See ICloneable

        #region ICloneable Members

        public object Clone()
        {
            return MemberwiseClone();
        }

        #endregion

        /// <summary>
        /// Reset the UUIDs for this item.
        /// </summary>
        /// <param name="partID">The new part ID to which this item belongs</param>
        public void ResetIDs(UUID partID)
        {
            LoadedItemID = OldItemID;
            OldItemID = ItemID;
            ItemID = UUID.Random();
            ParentPartID = partID;
            ParentID = partID;
        }

        public TaskInventoryItem()
        {
            ScriptRunning = true;
            CreationDate = (uint)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;
        }
    }
}
