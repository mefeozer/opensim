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
using OpenSim.Framework;

namespace OpenSim.Region.CoreModules.World.Estate
{

    public class EstateTerrainXferHandler
    {
        //private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private readonly AssetBase _asset;

        public delegate void TerrainUploadComplete(string name, byte[] filedata, IClientAPI remoteClient);
        public event TerrainUploadComplete TerrainUploadDone;

        //private string _description = String.Empty;
        //private string _name = String.Empty;
        //private UUID TransactionID = UUID.Zero;
        private readonly sbyte type = 0;

        public ulong mXferID;

        public EstateTerrainXferHandler(IClientAPI pRemoteClient, string pClientFilename)
        {
            _asset = new AssetBase(UUID.Zero, pClientFilename, type, pRemoteClient.AgentId.ToString())
            {
                Data = new byte[0],
                Description = "empty",
                Local = true,
                Temporary = true
            };
        }

        public ulong XferID => mXferID;

        public void RequestStartXfer(IClientAPI pRemoteClient)
        {
            mXferID = Util.GetNextXferID();
            pRemoteClient.SendXferRequest(mXferID, _asset.Type, _asset.FullID, 0, Utils.StringToBytes(_asset.Name));
        }

        /// <summary>
        /// Process transfer data received from the client.
        /// </summary>
        /// <param name="xferID"></param>
        /// <param name="packetID"></param>
        /// <param name="data"></param>
        public void XferReceive(IClientAPI remoteClient, ulong xferID, uint packetID, byte[] data)
        {
            if (mXferID != xferID)
                return;

            lock (this)
            {
                if (_asset.Data.Length > 1)
                {
                    byte[] destinationArray = new byte[_asset.Data.Length + data.Length];
                    Array.Copy(_asset.Data, 0, destinationArray, 0, _asset.Data.Length);
                    Array.Copy(data, 0, destinationArray, _asset.Data.Length, data.Length);
                    _asset.Data = destinationArray;
                }
                else
                {
                    byte[] buffer2 = new byte[data.Length - 4];
                    Array.Copy(data, 4, buffer2, 0, data.Length - 4);
                    _asset.Data = buffer2;
                }

                remoteClient.SendConfirmXfer(xferID, packetID);

                if ((packetID & 0x80000000) != 0)
                {
                    SendCompleteMessage(remoteClient);
                }
            }
        }

        public void SendCompleteMessage(IClientAPI remoteClient)
        {
            TerrainUploadDone?.Invoke(_asset.Name, _asset.Data, remoteClient);
        }
    }
}
