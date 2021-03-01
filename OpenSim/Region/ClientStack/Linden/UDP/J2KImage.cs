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
using OpenMetaverse.Imaging;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Services.Interfaces;
using log4net;
using System.Reflection;

namespace OpenSim.Region.ClientStack.LindenUDP
{
    /// <summary>
    /// Stores information about a current texture download and a reference to the texture asset
    /// </summary>
    public class J2KImage
    {
        private const int IMAGE_PACKET_SIZE = 1000;
        private const int FIRST_PACKET_SIZE = 600;

        /// <summary>
        /// If we've requested an asset but not received it in this ticks timeframe, then allow a duplicate
        /// request from the client to trigger a fresh asset request.
        /// </summary>
        /// <remarks>
        /// There are 10,000 ticks in a millisecond
        /// </remarks>
        private const int ASSET_REQUEST_TIMEOUT = 100000000;

        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public uint LastSequence;
        public float Priority;
        public uint StartPacket;
        public sbyte DiscardLevel;
        public UUID TextureID;
        public IJ2KDecoder J2KDecoder;
        public IAssetService AssetService;
        public UUID AgentID;
        public IInventoryAccessModule InventoryAccessModule;
        private OpenJPEG.J2KLayerInfo[] _layers;

        /// <summary>
        /// Has this request decoded the asset data?
        /// </summary>
        public bool IsDecoded { get; private set; }

        /// <summary>
        /// Has this request received the required asset data?
        /// </summary>
        public bool HasAsset { get; private set; }

        /// <summary>
        /// Time in milliseconds at which the asset was requested.
        /// </summary>
        public long AssetRequestTime { get; private set; }

        public C5.IPriorityQueueHandle<J2KImage> PriorityQueueHandle;

        private uint _currentPacket;
        private bool _decodeRequested;
        private bool _assetRequested;
        private bool _sentInfo;
        private uint _stopPacket;
        private byte[] _asset;
        private readonly LLImageManager _imageManager;

        public J2KImage(LLImageManager imageManager)
        {
            _imageManager = imageManager;
        }

        /// <summary>
        /// Sends packets for this texture to a client until packetsToSend is
        /// hit or the transfer completes
        /// </summary>
        /// <param name="client">Reference to the client that the packets are destined for</param>
        /// <param name="packetsToSend">Maximum number of packets to send during this call</param>
        /// <param name="packetsSent">Number of packets sent during this call</param>
        /// <returns>True if the transfer completes at the current discard level, otherwise false</returns>
        public bool SendPackets(IClientAPI client, int packetsToSend, out int packetsSent)
        {
            packetsSent = 0;

            if (_currentPacket <= _stopPacket)
            {
                bool sendMore = true;

                if (!_sentInfo || _currentPacket == 0)
                {
                    sendMore = !SendFirstPacket(client);

                    _sentInfo = true;
                    ++_currentPacket;
                    ++packetsSent;
                }
                if (_currentPacket < 2)
                {
                    _currentPacket = 2;
                }

                while (sendMore && packetsSent < packetsToSend && _currentPacket <= _stopPacket)
                {
                    sendMore = SendPacket(client);
                    ++_currentPacket;
                    ++packetsSent;
                }
            }

            return _currentPacket > _stopPacket;
        }

        /// <summary>
        /// This is where we decide what we need to update
        /// and assign the real discardLevel and packetNumber
        /// assuming of course that the connected client might be bonkers
        /// </summary>
        public void RunUpdate()
        {
            if (!HasAsset)
            {
                if (!_assetRequested || DateTime.UtcNow.Ticks > AssetRequestTime + ASSET_REQUEST_TIMEOUT)
                {
//                    _log.DebugFormat(
//                        "[J2KIMAGE]: Requesting asset {0} from request in packet {1}, already requested? {2}, due to timeout? {3}",
//                        TextureID, LastSequence, _assetRequested, DateTime.UtcNow.Ticks > AssetRequestTime + ASSET_REQUEST_TIMEOUT);

                    _assetRequested = true;
                    AssetRequestTime = DateTime.UtcNow.Ticks;

                    AssetService.Get(TextureID.ToString(), this, AssetReceived);
                }
            }
            else
            {
                if (!IsDecoded)
                {
                    //We need to decode the requested image first
                    if (!_decodeRequested)
                    {
                        //Request decode
                        _decodeRequested = true;

//                        _log.DebugFormat("[J2KIMAGE]: Requesting decode of asset {0}", TextureID);

                        // Do we have a jpeg decoder?
                        if (J2KDecoder != null)
                        {
                            if (_asset == null)
                            {
                                J2KDecodedCallback(TextureID, new OpenJPEG.J2KLayerInfo[0]);
                            }
                            else
                            {
                                // Send it off to the jpeg decoder
                                J2KDecoder.BeginDecode(TextureID, _asset, J2KDecodedCallback);
                            }
                        }
                        else
                        {
                            J2KDecodedCallback(TextureID, new OpenJPEG.J2KLayerInfo[0]);
                        }
                    }
                }
                else
                {
                    // Check for missing image asset data
                    if (_asset == null)
                    {
                        //_log.Warn("[J2KIMAGE]: RunUpdate() called with missing asset data (no missing image texture?). Canceling texture transfer");
                        _currentPacket = _stopPacket;
                        return;
                    }

                    if (DiscardLevel >= 0 || _stopPacket == 0)
                    {
                        // This shouldn't happen, but if it does, we really can't proceed
                        if (_layers == null)
                        {
                            _log.Warn("[J2KIMAGE]: RunUpdate() called with missing Layers. Canceling texture transfer");
                            _currentPacket = _stopPacket;
                            return;
                        }

                        int maxDiscardLevel = Math.Max(0, _layers.Length - 1);

                        // Treat initial texture downloads with a DiscardLevel of -1 a request for the highest DiscardLevel
                        if (DiscardLevel < 0 && _stopPacket == 0)
                            DiscardLevel = (sbyte)maxDiscardLevel;

                        // Clamp at the highest discard level
                        DiscardLevel = (sbyte)Math.Min(DiscardLevel, maxDiscardLevel);

                        //Calculate the _stopPacket
                        if (_layers.Length > 0)
                        {
                            _stopPacket = (uint)GetPacketForBytePosition(_layers[_layers.Length - 1 - DiscardLevel].End);
                            //I don't know why, but the viewer seems to expect the final packet if the file
                            //is just one packet bigger.
                            if (TexturePacketCount() == _stopPacket + 1)
                            {
                                _stopPacket = TexturePacketCount();
                            }
                        }
                        else
                        {
                            _stopPacket = TexturePacketCount();
                        }

                        //Give them at least two packets, to play nice with some broken viewers (SL also behaves this way)
                        if (_stopPacket == 1 && _layers[0].End > FIRST_PACKET_SIZE) _stopPacket++;

                        _currentPacket = StartPacket;
                    }
                }
            }
        }

        private bool SendFirstPacket(IClientAPI client)
        {
            if (client == null)
                return false;

            if (_asset == null)
            {
                _log.Warn("[J2KIMAGE]: Sending ImageNotInDatabase for texture " + TextureID);
                client.SendImageNotFound(TextureID);
                return true;
            }
            else if (_asset.Length <= FIRST_PACKET_SIZE)
            {
                // We have less then one packet's worth of data
                client.SendImageFirstPart(1, TextureID, (uint)_asset.Length, _asset, 2);
                _stopPacket = 0;
                return true;
            }
            else
            {
                // This is going to be a multi-packet texture download
                byte[] firstImageData = new byte[FIRST_PACKET_SIZE];

                try { Buffer.BlockCopy(_asset, 0, firstImageData, 0, FIRST_PACKET_SIZE); }
                catch (Exception)
                {
                    _log.ErrorFormat("[J2KIMAGE]: Texture block copy for the first packet failed. textureid={0}, assetlength={1}", TextureID, _asset.Length);
                    return true;
                }

                client.SendImageFirstPart(TexturePacketCount(), TextureID, (uint)_asset.Length, firstImageData, (byte)ImageCodec.J2C);
            }
            return false;
        }

        private bool SendPacket(IClientAPI client)
        {
            if (client == null)
                return false;

            bool complete = false;
            int imagePacketSize = (int)_currentPacket == TexturePacketCount() ? LastPacketSize() : IMAGE_PACKET_SIZE;

            try
            {
                if (CurrentBytePosition() + IMAGE_PACKET_SIZE > _asset.Length)
                {
                    imagePacketSize = LastPacketSize();
                    complete = true;
                    if (CurrentBytePosition() + imagePacketSize > _asset.Length)
                    {
                        imagePacketSize = _asset.Length - CurrentBytePosition();
                        complete = true;
                    }
                }

                // It's concievable that the client might request packet one
                // from a one packet image, which is really packet 0,
                // which would leave us with a negative imagePacketSize..
                if (imagePacketSize > 0)
                {
                    byte[] imageData = new byte[imagePacketSize];
                    int currentPosition = CurrentBytePosition();

                    try { Buffer.BlockCopy(_asset, currentPosition, imageData, 0, imagePacketSize); }
                    catch (Exception e)
                    {
                        _log.ErrorFormat("[J2KIMAGE]: Texture block copy for the first packet failed. textureid={0}, assetlength={1}, currentposition={2}, imagepacketsize={3}, exception={4}",
                            TextureID, _asset.Length, currentPosition, imagePacketSize, e.Message);
                        return false;
                    }

                    //Send the packet
                    client.SendImageNextPart((ushort)(_currentPacket - 1), TextureID, imageData);
                }

                return !complete;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private ushort TexturePacketCount()
        {
            if (!IsDecoded)
                return 0;

            if (_asset == null)
                return 0;

            if (_asset.Length <= FIRST_PACKET_SIZE)
                return 1;

            return (ushort)((_asset.Length - FIRST_PACKET_SIZE + IMAGE_PACKET_SIZE - 1) / IMAGE_PACKET_SIZE + 1);
        }

        private int GetPacketForBytePosition(int bytePosition)
        {
            return (bytePosition - FIRST_PACKET_SIZE + IMAGE_PACKET_SIZE - 1) / IMAGE_PACKET_SIZE + 1;
        }

        private int LastPacketSize()
        {
            if (_currentPacket == 1)
                return _asset.Length;
            int lastsize = (_asset.Length - FIRST_PACKET_SIZE) % IMAGE_PACKET_SIZE;
            //If the last packet size is zero, it's really cImagePacketSize, it sits on the boundary
            if (lastsize == 0)
            {
                lastsize = IMAGE_PACKET_SIZE;
            }
            return lastsize;
        }

        private int CurrentBytePosition()
        {
            if (_currentPacket == 0)
                return 0;

            if (_currentPacket == 1)
                return FIRST_PACKET_SIZE;

            int result = FIRST_PACKET_SIZE + ((int)_currentPacket - 2) * IMAGE_PACKET_SIZE;

            if (result < 0)
                result = FIRST_PACKET_SIZE;

            return result;
        }

        private void J2KDecodedCallback(UUID AssetId, OpenJPEG.J2KLayerInfo[] layers)
        {
            _layers = layers;
            IsDecoded = true;
            RunUpdate();
        }

        private void AssetDataCallback(UUID AssetID, AssetBase asset)
        {
            HasAsset = true;

            if (asset == null || asset.Data == null)
            {
                if (_imageManager.MissingImage != null)
                {
                    _asset = _imageManager.MissingImage.Data;
                }
                else
                {
                    _asset = null;
                    IsDecoded = true;
                }
            }
            else
            {
                _asset = asset.Data;
            }

            RunUpdate();
        }

        private void AssetReceived(string id, object sender, AssetBase asset)
        {
//            _log.DebugFormat(
//                "[J2KIMAGE]: Received asset {0} ({1} bytes)", id, asset != null ? asset.Data.Length.ToString() : "n/a");

            UUID assetID = UUID.Zero;
            if (asset != null)
            {
                assetID = asset.FullID;
                if(asset.Type != (byte)AssetType.Texture)
                    asset = null;
            }
            else if (InventoryAccessModule != null && sender != InventoryAccessModule)
            {
                // Unfortunately we need this here, there's no other way.
                // This is due to the fact that textures opened directly from the agent's inventory
                // don't have any distinguishing feature. As such, in order to serve those when the
                // foreign user is visiting, we need to try again after the first fail to the local
                // asset service.
                string assetServerURL = string.Empty;
                if (InventoryAccessModule.IsForeignUser(AgentID, out assetServerURL) && !string.IsNullOrEmpty(assetServerURL))
                {
                    if (!assetServerURL.EndsWith("/") && !assetServerURL.EndsWith("="))
                        assetServerURL = assetServerURL + "/";

//                    _log.DebugFormat("[J2KIMAGE]: texture {0} not found in local asset storage. Trying user's storage.", assetServerURL + id);
                    AssetService.Get(assetServerURL + id, InventoryAccessModule, AssetReceived);
                    return;
                }
            }

            AssetDataCallback(assetID, asset);
        }
    }
}
