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

namespace OpenSim.Framework
{
    public struct SpawnPoint
    {
        public float Yaw;
        public float Pitch;
        public float Distance;

        public void SetLocation(Vector3 pos, Quaternion rot, Vector3 point)
        {
            // The point is an absolute position, so we need the relative
            // location to the spawn point
            Vector3 offset = point - pos;
            Distance = Vector3.Mag(offset);

            // Next we need to rotate this vector into the spawn point's
            // coordinate system
            rot.W = -rot.W;
            offset = offset * rot;

            Vector3 dir = Vector3.Normalize(offset);

            // Get the bearing (yaw)
            Yaw = (float)Math.Atan2(dir.Y, dir.X);

            // Get the elevation (pitch)
            Pitch = (float)-Math.Atan2(dir.Z, Math.Sqrt(dir.X * dir.X + dir.Y * dir.Y));
        }

        public Vector3 GetLocation(Vector3 pos, Quaternion rot)
        {
            Quaternion y = Quaternion.CreateFromEulers(0, 0, Yaw);
            Quaternion p = Quaternion.CreateFromEulers(0, Pitch, 0);

            Vector3 dir = new Vector3(1, 0, 0) * p * y;
            Vector3 offset = dir * (float)Distance;

            offset *= rot;

            return pos + offset;
        }

        /// <summary>
        /// Returns a string representation of this SpawnPoint.
        /// </summary>
        /// <returns></returns>
        public override string ToString()
        {
            return string.Format("{0},{1},{2}", Yaw, Pitch, Distance);
        }

        /// <summary>
        /// Generate a SpawnPoint from a string
        /// </summary>
        /// <param name="str"></param>
        public static SpawnPoint Parse(string str)
        {
            string[] parts = str.Split(',');
            if (parts.Length != 3)
                throw new ArgumentException("Invalid string: " + str);

            SpawnPoint sp = new SpawnPoint
            {
                Yaw = float.Parse(parts[0]),
                Pitch = float.Parse(parts[1]),
                Distance = float.Parse(parts[2])
            };
            return sp;
        }
    }

    public class RegionSettings
    {
        public delegate void SaveDelegate(RegionSettings rs);

        public event SaveDelegate OnSave;

        /// <value>
        /// These appear to be terrain textures that are shipped with the client.
        /// </value>
        public static readonly UUID DEFAULT_TERRAIN_TEXTURE_1 = new UUID("b8d3965a-ad78-bf43-699b-bff8eca6c975");
        public static readonly UUID DEFAULT_TERRAIN_TEXTURE_2 = new UUID("abb783e6-3e93-26c0-248a-247666855da3");
        public static readonly UUID DEFAULT_TERRAIN_TEXTURE_3 = new UUID("179cdabd-398a-9b6b-1391-4dc333ba321f");
        public static readonly UUID DEFAULT_TERRAIN_TEXTURE_4 = new UUID("beb169c7-11ea-fff2-efe5-0f24dc881df2");

        public void Save()
        {
            if (OnSave != null)
                OnSave(this);
        }

        private UUID _RegionUUID = UUID.Zero;
        public UUID RegionUUID
        {
            get => _RegionUUID;
            set => _RegionUUID = value;
        }

        public UUID CacheID { get; set; } = UUID.Random();

        private bool _BlockTerraform = false;
        public bool BlockTerraform
        {
            get => _BlockTerraform;
            set => _BlockTerraform = value;
        }

        private bool _BlockFly = false;
        public bool BlockFly
        {
            get => _BlockFly;
            set => _BlockFly = value;
        }

        private bool _AllowDamage = false;
        public bool AllowDamage
        {
            get => _AllowDamage;
            set => _AllowDamage = value;
        }

        private bool _RestrictPushing = false;
        public bool RestrictPushing
        {
            get => _RestrictPushing;
            set => _RestrictPushing = value;
        }

        private bool _AllowLandResell = true;
        public bool AllowLandResell
        {
            get => _AllowLandResell;
            set => _AllowLandResell = value;
        }

        private bool _AllowLandJoinDivide = true;
        public bool AllowLandJoinDivide
        {
            get => _AllowLandJoinDivide;
            set => _AllowLandJoinDivide = value;
        }

        private bool _BlockShowInSearch = false;
        public bool BlockShowInSearch
        {
            get => _BlockShowInSearch;
            set => _BlockShowInSearch = value;
        }

        private int _AgentLimit = 40;
        public int AgentLimit
        {
            get => _AgentLimit;
            set => _AgentLimit = value;
        }

        private double _ObjectBonus = 1.0;
        public double ObjectBonus
        {
            get => _ObjectBonus;
            set => _ObjectBonus = value;
        }

        private int _Maturity = 0;
        public int Maturity
        {
            get => _Maturity;
            set => _Maturity = value;
        }

        private bool _DisableScripts = false;
        public bool DisableScripts
        {
            get => _DisableScripts;
            set => _DisableScripts = value;
        }

        private bool _DisableCollisions = false;
        public bool DisableCollisions
        {
            get => _DisableCollisions;
            set => _DisableCollisions = value;
        }

        private bool _DisablePhysics = false;
        public bool DisablePhysics
        {
            get => _DisablePhysics;
            set => _DisablePhysics = value;
        }

        private UUID _TerrainTexture1 = UUID.Zero;

        public UUID TerrainTexture1
        {
            get => _TerrainTexture1;
            set
            {
                if (value == UUID.Zero)
                    _TerrainTexture1 = DEFAULT_TERRAIN_TEXTURE_1;
                else
                    _TerrainTexture1 = value;
            }
        }

        private UUID _TerrainTexture2 = UUID.Zero;

        public UUID TerrainTexture2
        {
            get => _TerrainTexture2;
            set
            {
                if (value == UUID.Zero)
                    _TerrainTexture2 = DEFAULT_TERRAIN_TEXTURE_2;
                else
                    _TerrainTexture2 = value;
            }
        }

        private UUID _TerrainTexture3 = UUID.Zero;

        public UUID TerrainTexture3
        {
            get => _TerrainTexture3;
            set
            {
                if (value == UUID.Zero)
                    _TerrainTexture3 = DEFAULT_TERRAIN_TEXTURE_3;
                else
                    _TerrainTexture3 = value;
            }
        }

        private UUID _TerrainTexture4 = UUID.Zero;

        public UUID TerrainTexture4
        {
            get => _TerrainTexture4;
            set
            {
                if (value == UUID.Zero)
                    _TerrainTexture4 = DEFAULT_TERRAIN_TEXTURE_4;
                else
                    _TerrainTexture4 = value;
            }
        }

        private double _Elevation1NW = 10;
        public double Elevation1NW
        {
            get => _Elevation1NW;
            set => _Elevation1NW = value;
        }

        private double _Elevation2NW = 60;
        public double Elevation2NW
        {
            get => _Elevation2NW;
            set => _Elevation2NW = value;
        }

        private double _Elevation1NE = 10;
        public double Elevation1NE
        {
            get => _Elevation1NE;
            set => _Elevation1NE = value;
        }

        private double _Elevation2NE = 60;
        public double Elevation2NE
        {
            get => _Elevation2NE;
            set => _Elevation2NE = value;
        }

        private double _Elevation1SE = 10;
        public double Elevation1SE
        {
            get => _Elevation1SE;
            set => _Elevation1SE = value;
        }

        private double _Elevation2SE = 60;
        public double Elevation2SE
        {
            get => _Elevation2SE;
            set => _Elevation2SE = value;
        }

        private double _Elevation1SW = 10;
        public double Elevation1SW
        {
            get => _Elevation1SW;
            set => _Elevation1SW = value;
        }

        private double _Elevation2SW = 60;
        public double Elevation2SW
        {
            get => _Elevation2SW;
            set => _Elevation2SW = value;
        }

        private double _WaterHeight = 20;
        public double WaterHeight
        {
            get => _WaterHeight;
            set => _WaterHeight = value;
        }

        private double _TerrainRaiseLimit = 100;
        public double TerrainRaiseLimit
        {
            get => _TerrainRaiseLimit;
            set => _TerrainRaiseLimit = value;
        }

        private double _TerrainLowerLimit = -100;
        public double TerrainLowerLimit
        {
            get => _TerrainLowerLimit;
            set => _TerrainLowerLimit = value;
        }

        private bool _UseEstateSun = true;
        public bool UseEstateSun
        {
            get => _UseEstateSun;
            set => _UseEstateSun = value;
        }

        private bool _Sandbox = false;
        public bool Sandbox
        {
            get => _Sandbox;
            set => _Sandbox = value;
        }

        public Vector3 SunVector
        {
            get => Vector3.Zero;
            set { }
        }

        private UUID _ParcelImageID;
        public UUID ParcelImageID
        {
            get => _ParcelImageID;
            set => _ParcelImageID = value;
        }

        private UUID _TerrainImageID;
        public UUID TerrainImageID
        {
            get => _TerrainImageID;
            set => _TerrainImageID = value;
        }

        public bool FixedSun
        {
            get => false;
            set { }
        }

        public double SunPosition
        {
            get => 0;
            set { }
        }

        private UUID _Covenant = UUID.Zero;

        public UUID Covenant
        {
            get => _Covenant;
            set => _Covenant = value;
        }

        private int _CovenantChanged = 0;

        public int CovenantChangedDateTime
        {
            get => _CovenantChanged;
            set => _CovenantChanged = value;
        }

        private int _LoadedCreationDateTime;
        public int LoadedCreationDateTime
        {
            get => _LoadedCreationDateTime;
            set => _LoadedCreationDateTime = value;
        }

        public string LoadedCreationDate
        {
            get
            {
                TimeSpan ts = new TimeSpan(0, 0, LoadedCreationDateTime);
                DateTime stamp = new DateTime(1970, 1, 1) + ts;
                return stamp.ToLongDateString();
            }
        }

        public string LoadedCreationTime
        {
            get
            {
                TimeSpan ts = new TimeSpan(0, 0, LoadedCreationDateTime);
                DateTime stamp = new DateTime(1970, 1, 1) + ts;
                return stamp.ToLongTimeString();
            }
        }

        private string _LoadedCreationID;
        public string LoadedCreationID
        {
            get => _LoadedCreationID;
            set => _LoadedCreationID = value;
        }

        private bool _GodBlockSearch = false;
        public bool GodBlockSearch
        {
            get => _GodBlockSearch;
            set => _GodBlockSearch = value;
        }

        private bool _Casino = false;
        public bool Casino
        {
            get => _Casino;
            set => _Casino = value;
        }

        // Telehub support
        private bool _TelehubEnabled = false;
        public bool HasTelehub
        {
            get => _TelehubEnabled;
            set => _TelehubEnabled = value;
        }

        /// <summary>
        /// Connected Telehub object
        /// </summary>
        public UUID TelehubObject { get; set; }

        /// <summary>
        /// Our connected Telehub's SpawnPoints
        /// </summary>
        public List<SpawnPoint> l_SpawnPoints = new List<SpawnPoint>();

        // Add a SpawnPoint
        // ** These are not region coordinates **
        // They are relative to the Telehub coordinates
        //
        public void AddSpawnPoint(SpawnPoint point)
        {
            l_SpawnPoints.Add(point);
        }

        // Remove a SpawnPoint
        public void RemoveSpawnPoint(int point_index)
        {
            l_SpawnPoints.RemoveAt(point_index);
        }

        // Return the List of SpawnPoints
        public List<SpawnPoint> SpawnPoints()
        {
            return l_SpawnPoints;

        }

        // Clear the SpawnPoints List of all entries
        public void ClearSpawnPoints()
        {
            l_SpawnPoints.Clear();
        }
    }
}
