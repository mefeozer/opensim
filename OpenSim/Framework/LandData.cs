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
using System.Xml.Serialization;

using OpenMetaverse;

namespace OpenSim.Framework
{
    public class LandAccessEntry
    {
        public UUID AgentID;
        public int Expires;
        public AccessList Flags;
    }

    /// <summary>
    /// Details of a Parcel of land
    /// </summary>
    public class LandData
    {
        private Vector3 _AABBMax = new Vector3();
        private Vector3 _AABBMin = new Vector3();
        private int _area = 0;
        private uint _auctionID = 0; //Unemplemented. If set to 0, not being auctioned
        private UUID _authBuyerID = UUID.Zero; //Unemplemented. Authorized Buyer's UUID
        private ParcelCategory _category = ParcelCategory.None; //Unemplemented. Parcel's chosen category
        private int _claimDate = 0;
        private int _claimPrice = 0; //Unemplemented
        private UUID _globalID = UUID.Zero;
        private UUID _groupID = UUID.Zero;
        private bool _isGroupOwned = false;
        private byte[] _bitmap = new byte[512];
        private string _description = string.Empty;

        private uint _flags = (uint)ParcelFlags.AllowFly | (uint)ParcelFlags.AllowLandmark |
                                (uint)ParcelFlags.AllowAPrimitiveEntry |
                                (uint)ParcelFlags.AllowDeedToGroup |
                                (uint)ParcelFlags.CreateObjects | (uint)ParcelFlags.AllowOtherScripts |
                                (uint)ParcelFlags.AllowVoiceChat;

        private byte _landingType = (byte)OpenMetaverse.LandingType.Direct;
        private string _name = "Your Parcel";
        private ParcelStatus _status = ParcelStatus.Leased;
        private int _localID = 0;
        private byte _mediaAutoScale = 0;
        private UUID _mediaID = UUID.Zero;
        private string _mediaURL = string.Empty;
        private string _musicURL = string.Empty;
        private UUID _ownerID = UUID.Zero;
        private List<LandAccessEntry> _parcelAccessList = new List<LandAccessEntry>();
        private float _passHours = 0;
        private int _passPrice = 0;
        private int _salePrice = 0; //Unemeplemented. Parcels price.
        private int _simwideArea = 0;
        private int _simwidePrims = 0;
        private UUID _snapshotID = UUID.Zero;
        private Vector3 _userLocation = new Vector3();
        private Vector3 _userLookAt = new Vector3();
        private int _otherCleanTime = 0;
        private string _mediaType = "none/none";
        private string _mediaDescription = "";
        private int _mediaHeight = 0;
        private int _mediaWidth = 0;
        private bool _mediaLoop = false;
        private bool _obscureMusic = false;
        private bool _obscureMedia = false;

        private float _dwell = 0;
        public double LastDwellTimeMS;

        public bool SeeAVs { get; set; }
        public bool AnyAVSounds { get; set; }
        public bool GroupAVSounds { get; set; }

        private UUID _fakeID = UUID.Zero;
        public UUID FakeID
        {
            get => _fakeID;
            set => _fakeID = value;
        }

        /// <summary>
        /// Traffic count of parcel
        /// </summary>
        [XmlIgnore]
        public float Dwell
        {
            get => _dwell;
            set
            {
                _dwell = value;
                LastDwellTimeMS = Util.GetTimeStampMS();
            }
        }

        /// <summary>
        /// Whether to obscure parcel media URL
        /// </summary>
        [XmlIgnore]
        public bool ObscureMedia
        {
            get => _obscureMedia;
            set => _obscureMedia = value;
        }

        /// <summary>
        /// Whether to obscure parcel music URL
        /// </summary>
        [XmlIgnore]
        public bool ObscureMusic
        {
            get => _obscureMusic;
            set => _obscureMusic = value;
        }

        /// <summary>
        /// Whether to loop parcel media
        /// </summary>
        [XmlIgnore]
        public bool MediaLoop
        {
            get => _mediaLoop;
            set => _mediaLoop = value;
        }

        /// <summary>
        /// Height of parcel media render
        /// </summary>
        [XmlIgnore]
        public int MediaHeight
        {
            get => _mediaHeight;
            set => _mediaHeight = value;
        }

        /// <summary>
        /// Width of parcel media render
        /// </summary>
        [XmlIgnore]
        public int MediaWidth
        {
            get => _mediaWidth;
            set => _mediaWidth = value;
        }

        /// <summary>
        /// Upper corner of the AABB for the parcel
        /// </summary>
        [XmlIgnore]
        public Vector3 AABBMax
        {
            get => _AABBMax;
            set => _AABBMax = value;
        }
        /// <summary>
        /// Lower corner of the AABB for the parcel
        /// </summary>
        [XmlIgnore]
        public Vector3 AABBMin
        {
            get => _AABBMin;
            set => _AABBMin = value;
        }

        /// <summary>
        /// Area in meters^2 the parcel contains
        /// </summary>
        public int Area
        {
            get => _area;
            set => _area = value;
        }

        /// <summary>
        /// ID of auction (3rd Party Integration) when parcel is being auctioned
        /// </summary>
        public uint AuctionID
        {
            get => _auctionID;
            set => _auctionID = value;
        }

        /// <summary>
        /// UUID of authorized buyer of parcel.  This is UUID.Zero if anyone can buy it.
        /// </summary>
        public UUID AuthBuyerID
        {
            get => _authBuyerID;
            set => _authBuyerID = value;
        }

        /// <summary>
        /// Category of parcel.  Used for classifying the parcel in classified listings
        /// </summary>
        public ParcelCategory Category
        {
            get => _category;
            set => _category = value;
        }

        /// <summary>
        /// Date that the current owner purchased or claimed the parcel
        /// </summary>
        public int ClaimDate
        {
            get => _claimDate;
            set => _claimDate = value;
        }

        /// <summary>
        /// The last price that the parcel was sold at
        /// </summary>
        public int ClaimPrice
        {
            get => _claimPrice;
            set => _claimPrice = value;
        }

        /// <summary>
        /// Global ID for the parcel.  (3rd Party Integration)
        /// </summary>
        public UUID GlobalID
        {
            get => _globalID;
            set => _globalID = value;
        }

        /// <summary>
        /// Unique ID of the Group that owns
        /// </summary>
        public UUID GroupID
        {
            get => _groupID;
            set => _groupID = value;
        }

        /// <summary>
        /// Returns true if the Land Parcel is owned by a group
        /// </summary>
        public bool IsGroupOwned
        {
            get => _isGroupOwned;
            set => _isGroupOwned = value;
        }

        /// <summary>
        /// parcel shape in bits per ocupied location
        /// </summary>
        public byte[] Bitmap
        {
            get => _bitmap;
            set => _bitmap = value;
        }

        /// <summary>
        /// Parcel Description
        /// </summary>
        public string Description
        {
            get => _description;
            set => _description = value;
        }

        /// <summary>
        /// Parcel settings.  Access flags, Fly, NoPush, Voice, Scripts allowed, etc.  ParcelFlags
        /// </summary>
        public uint Flags
        {
            get => _flags;
            set => _flags = value;
        }

        /// <summary>
        /// Determines if people are able to teleport where they please on the parcel or if they
        /// get constrainted to a specific point on teleport within the parcel
        /// </summary>
        public byte LandingType
        {
            get => _landingType;
            set => _landingType = value;
        }

        /// <summary>
        /// Parcel Name
        /// </summary>
        public string Name
        {
            get => _name;
            set => _name = value;
        }

        /// <summary>
        /// Status of Parcel, Leased, Abandoned, For Sale
        /// </summary>
        public ParcelStatus Status
        {
            get => _status;
            set => _status = value;
        }

        /// <summary>
        /// Internal ID of the parcel.  Sometimes the client will try to use this value
        /// </summary>
        public int LocalID
        {
            get => _localID;
            set => _localID = value;
        }

        /// <summary>
        /// Determines if we scale the media based on the surface it's on
        /// </summary>
        public byte MediaAutoScale
        {
            get => _mediaAutoScale;
            set => _mediaAutoScale = value;
        }

        /// <summary>
        /// Texture Guid to replace with the output of the media stream
        /// </summary>
        public UUID MediaID
        {
            get => _mediaID;
            set => _mediaID = value;
        }

        /// <summary>
        /// URL to the media file to display
        /// </summary>
        public string MediaURL
        {
            get => _mediaURL;
            set => _mediaURL = value;
        }

        public string MediaType
        {
            get => _mediaType;
            set => _mediaType = value;
        }

        /// <summary>
        /// URL to the shoutcast music stream to play on the parcel
        /// </summary>
        public string MusicURL
        {
            get => _musicURL;
            set => _musicURL = value;
        }

        /// <summary>
        /// Owner Avatar or Group of the parcel.  Naturally, all land masses must be
        /// owned by someone
        /// </summary>
        public UUID OwnerID
        {
            get => _ownerID;
            set => _ownerID = value;
        }

        /// <summary>
        /// List of access data for the parcel.  User data, some bitflags, and a time
        /// </summary>
        public List<LandAccessEntry> ParcelAccessList
        {
            get => _parcelAccessList;
            set => _parcelAccessList = value;
        }

        /// <summary>
        /// How long in hours a Pass to the parcel is given
        /// </summary>
        public float PassHours
        {
            get => _passHours;
            set => _passHours = value;
        }

        /// <summary>
        /// Price to purchase a Pass to a restricted parcel
        /// </summary>
        public int PassPrice
        {
            get => _passPrice;
            set => _passPrice = value;
        }

        /// <summary>
        /// When the parcel is being sold, this is the price to purchase the parcel
        /// </summary>
        public int SalePrice
        {
            get => _salePrice;
            set => _salePrice = value;
        }

        /// <summary>
        /// Number of meters^2 that the land owner has in the Simulator
        /// </summary>
        [XmlIgnore]
        public int SimwideArea
        {
            get => _simwideArea;
            set => _simwideArea = value;
        }

        /// <summary>
        /// Number of SceneObjectPart in the Simulator
        /// </summary>
        [XmlIgnore]
        public int SimwidePrims
        {
            get => _simwidePrims;
            set => _simwidePrims = value;
        }

        /// <summary>
        /// ID of the snapshot used in the client parcel dialog of the parcel
        /// </summary>
        public UUID SnapshotID
        {
            get => _snapshotID;
            set => _snapshotID = value;
        }

        /// <summary>
        /// When teleporting is restricted to a certain point, this is the location
        /// that the user will be redirected to
        /// </summary>
        public Vector3 UserLocation
        {
            get => _userLocation;
            set => _userLocation = value;
        }

        /// <summary>
        /// When teleporting is restricted to a certain point, this is the rotation
        /// that the user will be positioned
        /// </summary>
        public Vector3 UserLookAt
        {
            get => _userLookAt;
            set => _userLookAt = value;
        }

        /// <summary>
        /// Autoreturn number of minutes to return SceneObjectGroup that are owned by someone who doesn't own
        /// the parcel and isn't set to the same 'group' as the parcel.
        /// </summary>
        public int OtherCleanTime
        {
            get => _otherCleanTime;
            set => _otherCleanTime = value;
        }

        /// <summary>
        /// parcel media description
        /// </summary>
        public string MediaDescription
        {
            get => _mediaDescription;
            set => _mediaDescription = value;
        }

        public int EnvironmentVersion = -1;

        [XmlIgnore] //this needs to be added by hand
        public ViewerEnvironment Environment { get; set;}

        public LandData()
        {
            _globalID = UUID.Random();
            SeeAVs = true;
            AnyAVSounds = true;
            GroupAVSounds = true;
            LastDwellTimeMS = Util.GetTimeStampMS();
            EnvironmentVersion = -1;
            Environment = null;
        }

        /// <summary>
        /// Make a new copy of the land data
        /// </summary>
        /// <returns></returns>
        public LandData Copy()
        {
            LandData landData = new LandData
            {
                _AABBMax = _AABBMax,
                _AABBMin = _AABBMin,
                _area = _area,
                _auctionID = _auctionID,
                _authBuyerID = _authBuyerID,
                _category = _category,
                _claimDate = _claimDate,
                _claimPrice = _claimPrice,
                _globalID = _globalID,
                _fakeID = _fakeID,
                _groupID = _groupID,
                _isGroupOwned = _isGroupOwned,
                _localID = _localID,
                _landingType = _landingType,
                _mediaAutoScale = _mediaAutoScale,
                _mediaID = _mediaID,
                _mediaURL = _mediaURL,
                _musicURL = _musicURL,
                _ownerID = _ownerID,
                _bitmap = (byte[])_bitmap.Clone(),
                _description = _description,
                _flags = _flags,
                _name = _name,
                _status = _status,
                _passHours = _passHours,
                _passPrice = _passPrice,
                _salePrice = _salePrice,
                _snapshotID = _snapshotID,
                _userLocation = _userLocation,
                _userLookAt = _userLookAt,
                _otherCleanTime = _otherCleanTime,
                _mediaType = _mediaType,
                _mediaDescription = _mediaDescription,
                _mediaWidth = _mediaWidth,
                _mediaHeight = _mediaHeight,
                _mediaLoop = _mediaLoop,
                _obscureMusic = _obscureMusic,
                _obscureMedia = _obscureMedia,
                _simwideArea = _simwideArea,
                _simwidePrims = _simwidePrims,
                _dwell = _dwell,
                SeeAVs = SeeAVs,
                AnyAVSounds = AnyAVSounds,
                GroupAVSounds = GroupAVSounds
            };

            landData._parcelAccessList.Clear();
            foreach (LandAccessEntry entry in _parcelAccessList)
            {
                LandAccessEntry newEntry = new LandAccessEntry
                {
                    AgentID = entry.AgentID,
                    Flags = entry.Flags,
                    Expires = entry.Expires
                };

                landData._parcelAccessList.Add(newEntry);
            }

            if (Environment == null)
            {
                landData.Environment = null;
                landData.EnvironmentVersion = -1;
            }
            else
            {
                landData.Environment = Environment.Clone();
                landData.EnvironmentVersion = EnvironmentVersion;
            }

            return landData;
        }
    }
}
