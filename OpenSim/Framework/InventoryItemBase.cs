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
using OpenMetaverse.StructuredData;

namespace OpenSim.Framework
{
    /// <summary>
    /// Inventory Item - contains all the properties associated with an individual inventory piece.
    /// </summary>
    public class InventoryItemBase : InventoryNodeBase, ICloneable
    {
        /// <value>
        /// The inventory type of the item.  This is slightly different from the asset type in some situations.
        /// </value>
        public int InvType
        {
            get => _invType;

            set => _invType = value;
        }
        protected int _invType;

        /// <value>
        /// The folder this item is contained in
        /// </value>
        public UUID Folder
        {
            get => _folder;

            set => _folder = value;
        }
        protected UUID _folder;

        /// <value>
        /// The creator of this item
        /// </value>
        public string CreatorId
        {
            get => _creatorId;

            set
            {
                _creatorId = value;

                if (_creatorId == null || !UUID.TryParse(_creatorId, out _creatorIdAsUuid))
                    _creatorIdAsUuid = UUID.Zero;
            }
        }
        protected string _creatorId;

        /// <value>
        /// The CreatorId expressed as a UUID.
        /// </value>
        public UUID CreatorIdAsUuid
        {
            get
            {
                if (UUID.Zero == _creatorIdAsUuid)
                {
                    UUID.TryParse(CreatorId, out _creatorIdAsUuid);
                }

                return _creatorIdAsUuid;
            }
        }
        protected UUID _creatorIdAsUuid = UUID.Zero;

        /// <summary>
        /// Extended creator information of the form <profile url>;<name>
        /// </summary>
        public string CreatorData // = <profile url>;<name>
        {
            get => _creatorData;
            set => _creatorData = value;
        }
        protected string _creatorData = string.Empty;

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
                    return _creatorId + ';' + _creatorData;
                else
                    return _creatorId;
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
                    _creatorId = value;
                }
                else // <uuid>[;<endpoint>[;name]]
                {
                    string name = "Unknown User";
                    string[] parts = value.Split(';');
                    if (parts.Length >= 1)
                        _creatorId = parts[0];
                    if (parts.Length >= 2)
                        _creatorData = parts[1];
                    if (parts.Length >= 3)
                        name = parts[2];

                    _creatorData += ';' + name;
                }
            }
        }

        /// <value>
        /// The description of the inventory item (must be less than 64 characters)
        /// </value>
        
        public osUTF8 UTF8Description;
        public string Description
        {
            get => UTF8Description == null ? string.Empty : UTF8Description.ToString();
            set => UTF8Description = string.IsNullOrWhiteSpace(value) ? null : new osUTF8(value);
        }

        /// <value>
        ///
        /// </value>
        public uint NextPermissions
        {
            get => _nextPermissions;

            set => _nextPermissions = value;
        }
        protected uint _nextPermissions;

        /// <value>
        /// A mask containing permissions for the current owner (cannot be enforced)
        /// </value>
        public uint CurrentPermissions
        {
            get => _currentPermissions;

            set => _currentPermissions = value;
        }
        protected uint _currentPermissions;

        /// <value>
        ///
        /// </value>
        public uint BasePermissions
        {
            get => _basePermissions;

            set => _basePermissions = value;
        }
        protected uint _basePermissions;

        /// <value>
        ///
        /// </value>
        public uint EveryOnePermissions
        {
            get => _everyonePermissions;

            set => _everyonePermissions = value;
        }
        protected uint _everyonePermissions;

        /// <value>
        ///
        /// </value>
        public uint GroupPermissions
        {
            get => _groupPermissions;

            set => _groupPermissions = value;
        }
        protected uint _groupPermissions;

        /// <value>
        /// This is an enumerated value determining the type of asset (eg Notecard, Sound, Object, etc)
        /// </value>
        public int AssetType
        {
            get => _assetType;

            set => _assetType = value;
        }
        protected int _assetType;

        /// <value>
        /// The UUID of the associated asset on the asset server
        /// </value>
        public UUID AssetID
        {
            get => _assetID;

            set => _assetID = value;
        }
        protected UUID _assetID;

        /// <value>
        ///
        /// </value>
        public UUID GroupID
        {
            get => _groupID;

            set => _groupID = value;
        }
        protected UUID _groupID;

        /// <value>
        ///
        /// </value>
        public bool GroupOwned
        {
            get => _groupOwned;

            set => _groupOwned = value;
        }
        protected bool _groupOwned;

        /// <value>
        ///
        /// </value>
        public int SalePrice
        {
            get => _salePrice;

            set => _salePrice = value;
        }
        protected int _salePrice;

        /// <value>
        ///
        /// </value>
        public byte SaleType
        {
            get => _saleType;

            set => _saleType = value;
        }
        protected byte _saleType;

        /// <value>
        ///
        /// </value>
        public uint Flags
        {
            get => _flags;

            set => _flags = value;
        }
        protected uint _flags;

        /// <value>
        ///
        /// </value>
        public int CreationDate
        {
            get => _creationDate;

            set => _creationDate = value;
        }
        protected int _creationDate = (int)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;

        public InventoryItemBase()
        {
        }

        public InventoryItemBase(UUID id)
        {
            ID = id;
        }

        public InventoryItemBase(UUID id, UUID owner)
        {
            ID = id;
            Owner = owner;
        }

        public object Clone()
        {
            return MemberwiseClone();
        }

        public void ToLLSDxml(osUTF8 lsl, uint flagsMask = 0xffffffff)
        {
            LLSDxmlEncode2.AddMap(lsl);
                LLSDxmlEncode2.AddEle_parent_id(Folder, lsl);
                LLSDxmlEncode2.AddElem("asset_id", AssetID, lsl);
                LLSDxmlEncode2.AddElem("ite_id", ID, lsl);

                LLSDxmlEncode2.AddMap("permissions",lsl);
                    LLSDxmlEncode2.AddElem("creator_id", CreatorIdAsUuid, lsl);
                    LLSDxmlEncode2.AddEle_owner_id( Owner, lsl);
                    LLSDxmlEncode2.AddElem("group_id", GroupID, lsl);
                    LLSDxmlEncode2.AddElem("base_mask", (int)CurrentPermissions, lsl);
                    LLSDxmlEncode2.AddElem("owner_mask", (int)CurrentPermissions, lsl);
                    LLSDxmlEncode2.AddElem("group_mask", (int)GroupPermissions, lsl);
                    LLSDxmlEncode2.AddElem("everyone_mask", (int)EveryOnePermissions, lsl);
                    LLSDxmlEncode2.AddElem("next_owner_mask", (int)NextPermissions, lsl);
                    LLSDxmlEncode2.AddElem("is_owner_group", GroupOwned, lsl);
                LLSDxmlEncode2.AddEndMap(lsl);

                LLSDxmlEncode2.AddElem("type", AssetType, lsl);
                LLSDxmlEncode2.AddElem("inv_type", InvType, lsl);
                LLSDxmlEncode2.AddElem("flags", (int)(Flags & flagsMask), lsl);

                LLSDxmlEncode2.AddMap("sale_info",lsl);
                    LLSDxmlEncode2.AddElem("sale_price", SalePrice, lsl);
                    LLSDxmlEncode2.AddElem("sale_type", SaleType, lsl);
                LLSDxmlEncode2.AddEndMap(lsl);

                LLSDxmlEncode2.AddEle_name(Name, lsl);
                LLSDxmlEncode2.AddElem("desc", Description, lsl);
                LLSDxmlEncode2.AddElem("created_at", CreationDate, lsl);

            LLSDxmlEncode2.AddEndMap(lsl);
        }
    }
}
