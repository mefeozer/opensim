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
using System.Xml.Serialization;
using OpenMetaverse;

namespace OpenSim.Framework
{
    [Flags]
    // this enum is stuck, can not be changed or will break compatibilty with any version older than that change
    public enum AssetFlags : int
    {
        Normal = 0,         // Immutable asset
        Maptile = 1,        // What it says
        Rewritable = 2,     // Content can be rewritten
        Collectable = 4,     // Can be GC'ed after some time
    }

    /// <summary>
    /// Asset class.   All Assets are reference by this class or a class derived from this class
    /// </summary>
    [Serializable]
    public class AssetBase
    {
        //private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public static readonly int MAX_ASSET_NAME = 64;
        public static readonly int MAX_ASSET_DESC = 64;

        /// <summary>
        /// Data of the Asset
        /// </summary>
        private byte[] _data;

        /// <summary>
        /// Meta Data of the Asset
        /// </summary>
        private AssetMetadata _metadata;

        private int _uploadAttempts;

        // This is needed for .NET serialization!!!
        // Do NOT "Optimize" away!
        public AssetBase()
        {
            _metadata = new AssetMetadata
            {
                FullID = UUID.Zero,
                ID = UUID.Zero.ToString(),
                Type = (sbyte)AssetType.Unknown,
                CreatorID = string.Empty
            };
        }

        public AssetBase(UUID assetID, string name, sbyte assetType, string creatorID)
        {
            /*
            if (assetType == (sbyte)AssetType.Unknown)
            {
                System.Diagnostics.StackTrace trace = new System.Diagnostics.StackTrace(true);
                _log.ErrorFormat("[ASSETBASE]: Creating asset '{0}' ({1}) with an unknown asset type\n{2}",
                    name, assetID, trace.ToString());
            }
            */

            _metadata = new AssetMetadata
            {
                FullID = assetID,
                Name = name,
                Type = assetType,
                CreatorID = creatorID
            };
        }

        public AssetBase(string assetID, string name, sbyte assetType, string creatorID)
        {
            /*
            if (assetType == (sbyte)AssetType.Unknown)
            {
                System.Diagnostics.StackTrace trace = new System.Diagnostics.StackTrace(true);
                _log.ErrorFormat("[ASSETBASE]: Creating asset '{0}' ({1}) with an unknown asset type\n{2}",
                    name, assetID, trace.ToString());
            }
            */

            _metadata = new AssetMetadata
            {
                ID = assetID,
                Name = name,
                Type = assetType,
                CreatorID = creatorID
            };
        }

        public bool ContainsReferences => IsTextualAsset && Type != (sbyte)AssetType.Notecard && Type != (sbyte)AssetType.CallingCard && Type != (sbyte)AssetType.LSLText && Type != (sbyte)AssetType.Landmark;

        public bool IsTextualAsset => !IsBinaryAsset;

        /// <summary>
        /// Checks if this asset is a binary or text asset
        /// </summary>
        public bool IsBinaryAsset =>
            Type == (sbyte)AssetType.Animation ||
            Type == (sbyte)AssetType.Gesture ||
            Type == (sbyte)AssetType.Simstate ||
            Type == (sbyte)AssetType.Unknown ||
            Type == (sbyte)AssetType.Object ||
            Type == (sbyte)AssetType.Sound ||
            Type == (sbyte)AssetType.SoundWAV ||
            Type == (sbyte)AssetType.Texture ||
            Type == (sbyte)AssetType.TextureTGA ||
            Type == (sbyte)AssetType.Folder ||
            Type == (sbyte)AssetType.ImageJPEG ||
            Type == (sbyte)AssetType.ImageTGA ||
            Type == (sbyte)AssetType.Mesh ||
            Type == (sbyte) AssetType.LSLBytecode;

        public byte[] Data
        {
            get => _data;
            set => _data = value;
        }

        /// <summary>
        /// Asset UUID
        /// </summary>
        public UUID FullID
        {
            get => _metadata.FullID;
            set => _metadata.FullID = value;
        }

        /// <summary>
        /// Asset MetaData ID (transferring from UUID to string ID)
        /// </summary>
        public string ID
        {
            get => _metadata.ID;
            set => _metadata.ID = value;
        }

        public string Name
        {
            get => _metadata.Name;
            set => _metadata.Name = value;
        }

        public string Description
        {
            get => _metadata.Description;
            set => _metadata.Description = value;
        }

        /// <summary>
        /// (sbyte) AssetType enum
        /// </summary>
        public sbyte Type
        {
            get => _metadata.Type;
            set => _metadata.Type = value;
        }

        public int UploadAttempts
        {
            get => _uploadAttempts;
            set => _uploadAttempts = value;
        }

        /// <summary>
        /// Is this a region only asset, or does this exist on the asset server also
        /// </summary>
        public bool Local
        {
            get => _metadata.Local;
            set => _metadata.Local = value;
        }

        /// <summary>
        /// Is this asset going to be saved to the asset database?
        /// </summary>
        public bool Temporary
        {
            get => _metadata.Temporary;
            set => _metadata.Temporary = value;
        }

        public string CreatorID
        {
            get => _metadata.CreatorID;
            set => _metadata.CreatorID = value;
        }

        public AssetFlags Flags
        {
            get => _metadata.Flags;
            set => _metadata.Flags = value;
        }

        [XmlIgnore]
        public AssetMetadata Metadata
        {
            get => _metadata;
            set => _metadata = value;
        }

        public override string ToString()
        {
            return FullID.ToString();
        }
    }

    [Serializable]
    public class AssetMetadata
    {
        private UUID _fullid;
        private string _id;
        private string _name = string.Empty;
        private string _description = string.Empty;
        private DateTime _creation_date;
        private sbyte _type = (sbyte)AssetType.Unknown;
        private string _content_type;
        private byte[] _sha1;
        private bool _local;
        private bool _temporary;
        private string _creatorid;
        private AssetFlags _flags;

        public UUID FullID
        {
            get => _fullid;
            set { _fullid = value; _id = _fullid.ToString(); }
        }

        public string ID
        {
            //get { return _fullid.ToString(); }
            //set { _fullid = new UUID(value); }
            get
            {
                if (string.IsNullOrEmpty(_id))
                    _id = _fullid.ToString();

                return _id;
            }

            set
            {
                if (UUID.TryParse(value, out UUID uuid))
                {
                    _fullid = uuid;
                    _id = uuid.ToString();
                }
                else
                    _id = value;
            }
        }

        public string Name
        {
            get => _name;
            set => _name = value;
        }

        public string Description
        {
            get => _description;
            set => _description = value;
        }

        public DateTime CreationDate
        {
            get => _creation_date;
            set => _creation_date = value;
        }

        public sbyte Type
        {
            get => _type;
            set => _type = value;
        }

        public string ContentType
        {
            get
            {
                if (!string.IsNullOrEmpty(_content_type))
                    return _content_type;
                else
                    return SLUtil.SLAssetTypeToContentType(_type);
            }
            set
            {
                _content_type = value;

                sbyte type = SLUtil.ContentTypeToSLAssetType(value);
                if (type != -1)
                    _type = type;
            }
        }

        public byte[] SHA1
        {
            get => _sha1;
            set => _sha1 = value;
        }

        public bool Local
        {
            get => _local;
            set => _local = value;
        }

        public bool Temporary
        {
            get => _temporary;
            set => _temporary = value;
        }

        public string CreatorID
        {
            get => _creatorid;
            set => _creatorid = value;
        }

        public AssetFlags Flags
        {
            get => _flags;
            set => _flags = value;
        }
    }
}
