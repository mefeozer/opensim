/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyrightD
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

namespace OpenSim.Region.PhysicsModule.BulletS
{
    public sealed class BSTerrainHeightmap : BSTerrainPhys
{
    static readonly string LogHeader = "[BULLETSIM TERRAIN HEIGHTMAP]";

    BulletHMapInfo _mapInfo = null;

    // Constructor to build a default, flat heightmap terrain.
    public BSTerrainHeightmap(BSScene physicsScene, Vector3 regionBase, uint id, Vector3 regionSize)
        : base(physicsScene, regionBase, id)
    {
        Vector3 minTerrainCoords = new Vector3(0f, 0f, BSTerrainManager.HEIGHT_INITIALIZATION - BSTerrainManager.HEIGHT_EQUAL_FUDGE);
        Vector3 maxTerrainCoords = new Vector3(regionSize.X, regionSize.Y, BSTerrainManager.HEIGHT_INITIALIZATION);
        int totalHeights = (int)maxTerrainCoords.X * (int)maxTerrainCoords.Y;
        float[] initialMap = new float[totalHeights];
        for (int ii = 0; ii < totalHeights; ii++)
        {
            initialMap[ii] = BSTerrainManager.HEIGHT_INITIALIZATION;
        }
            _mapInfo = new BulletHMapInfo(id, initialMap, regionSize.X, regionSize.Y)
            {
                minCoords = minTerrainCoords,
                maxCoords = maxTerrainCoords,
                terrainRegionBase = TerrainBase
            };
            // Don't have to free any previous since we just got here.
            BuildHeightmapTerrain();
    }

    // This minCoords and maxCoords passed in give the size of the terrain (min and max Z
    //         are the high and low points of the heightmap).
    public BSTerrainHeightmap(BSScene physicsScene, Vector3 regionBase, uint id, float[] initialMap,
                                                    Vector3 minCoords, Vector3 maxCoords)
        : base(physicsScene, regionBase, id)
    {
            _mapInfo = new BulletHMapInfo(id, initialMap, maxCoords.X - minCoords.X, maxCoords.Y - minCoords.Y)
            {
                minCoords = minCoords,
                maxCoords = maxCoords,
                minZ = minCoords.Z,
                maxZ = maxCoords.Z,
                terrainRegionBase = TerrainBase
            };

            // Don't have to free any previous since we just got here.
            BuildHeightmapTerrain();
    }

    public override void Dispose()
    {
        ReleaseHeightMapTerrain();
    }

    // Using the information in _mapInfo, create the physical representation of the heightmap.
    private void BuildHeightmapTerrain()
    {
        // Create the terrain shape from the mapInfo
        _mapInfo.terrainShape = _physicsScene.PE.CreateTerrainShape( _mapInfo.ID,
                                new Vector3(_mapInfo.sizeX, _mapInfo.sizeY, 0), _mapInfo.minZ, _mapInfo.maxZ,
                                _mapInfo.heightMap, 1f, BSParam.TerrainCollisionMargin);


        // The terrain object initial position is at the center of the object
        Vector3 centerPos;
        centerPos.X = _mapInfo.minCoords.X + _mapInfo.sizeX / 2f;
        centerPos.Y = _mapInfo.minCoords.Y + _mapInfo.sizeY / 2f;
        centerPos.Z = _mapInfo.minZ + (_mapInfo.maxZ - _mapInfo.minZ) / 2f;

        _mapInfo.terrainBody = _physicsScene.PE.CreateBodyWithDefaultMotionState(_mapInfo.terrainShape,
                                _mapInfo.ID, centerPos, Quaternion.Identity);

        // Set current terrain attributes
        _physicsScene.PE.SetFriction(_mapInfo.terrainBody, BSParam.TerrainFriction);
        _physicsScene.PE.SetHitFraction(_mapInfo.terrainBody, BSParam.TerrainHitFraction);
        _physicsScene.PE.SetRestitution(_mapInfo.terrainBody, BSParam.TerrainRestitution);
        _physicsScene.PE.SetCollisionFlags(_mapInfo.terrainBody, CollisionFlags.CF_STATIC_OBJECT);

        _mapInfo.terrainBody.collisionType = CollisionType.Terrain;

        // Return the new terrain to the world of physical objects
        _physicsScene.PE.AddObjectToWorld(_physicsScene.World, _mapInfo.terrainBody);

        // redo its bounding box now that it is in the world
        _physicsScene.PE.UpdateSingleAabb(_physicsScene.World, _mapInfo.terrainBody);

        // Make it so the terrain will not move or be considered for movement.
        _physicsScene.PE.ForceActivationState(_mapInfo.terrainBody, ActivationState.DISABLE_SIMULATION);

        return;
    }

    // If there is information in _mapInfo pointing to physical structures, release same.
    private void ReleaseHeightMapTerrain()
    {
        if (_mapInfo != null)
        {
            if (_mapInfo.terrainBody.HasPhysicalBody)
            {
                _physicsScene.PE.RemoveObjectFromWorld(_physicsScene.World, _mapInfo.terrainBody);
                // Frees both the body and the shape.
                _physicsScene.PE.DestroyObject(_physicsScene.World, _mapInfo.terrainBody);
            }
            _mapInfo.Release();
        }
        _mapInfo = null;
    }

    // The passed position is relative to the base of the region.
    // There are many assumptions herein that the heightmap increment is 1.
    public override float GetTerrainHeightAtXYZ(Vector3 pos)
    {
        float ret = BSTerrainManager.HEIGHT_GETHEIGHT_RET;

        try {
            int baseX = (int)pos.X;
            int baseY = (int)pos.Y;
            int maxX = (int)_mapInfo.sizeX;
            int maxY = (int)_mapInfo.sizeY;
            float diffX = pos.X - baseX;
            float diffY = pos.Y - baseY;

            float mapHeight1 = _mapInfo.heightMap[baseY * maxY + baseX];
            float mapHeight2 = _mapInfo.heightMap[Math.Min(baseY + 1, maxY - 1) * maxY + baseX];
            float mapHeight3 = _mapInfo.heightMap[baseY * maxY + Math.Min(baseX + 1, maxX  - 1)];
            float mapHeight4 = _mapInfo.heightMap[Math.Min(baseY + 1, maxY - 1) * maxY +  Math.Min(baseX + 1, maxX  - 1)];

            float Xrise = (mapHeight4 - mapHeight3) * diffX;
            float Yrise = (mapHeight2 - mapHeight1) * diffY;

            ret = mapHeight1 + (Xrise + Yrise) / 2f;
            // _physicsScene.DetailLog("{0},BSTerrainHeightMap,GetTerrainHeightAtXYZ,pos={1},{2}/{3}/{4}/{5},ret={6}",
            //         BSScene.DetailLogZero, pos, mapHeight1, mapHeight2, mapHeight3, mapHeight4, ret);
        }
        catch
        {
            // Sometimes they give us wonky values of X and Y. Give a warning and return something.
            _physicsScene.Logger.WarnFormat("{0} Bad request for terrain height. terrainBase={1}, pos={2}",
                                LogHeader, _mapInfo.terrainRegionBase, pos);
            ret = BSTerrainManager.HEIGHT_GETHEIGHT_RET;
        }
        return ret;
    }

    // The passed position is relative to the base of the region.
    public override float GetWaterLevelAtXYZ(Vector3 pos)
    {
        return _physicsScene.SimpleWaterLevel;
    }
}
}
