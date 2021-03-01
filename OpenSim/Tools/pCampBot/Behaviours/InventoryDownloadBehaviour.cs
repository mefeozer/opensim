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

using OpenMetaverse;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace pCampBot
{
    /// <summary>
    /// Do nothing
    /// </summary>
    public class InventoryDownloadBehaviour : AbstractBehaviour
    {
        private bool _initialized;
        private int _Requests = 2;
        private readonly Stopwatch _StopWatch = new Stopwatch();
        private readonly List<UUID> _processed = new List<UUID>();

        public InventoryDownloadBehaviour()
        {
            AbbreviatedName = "inv";
            Name = "Inventory";
        }

        public override void Action()
        {
            if (!_initialized)
            {
                _initialized = true;
                Bot.Client.Settings.HTTP_INVENTORY = true;
                Bot.Client.Settings.FETCH_MISSING_INVENTORY = true;
                Bot.Client.Inventory.FolderUpdated += Inventory_FolderUpdated;
                Console.WriteLine("Lib owner is " + Bot.Client.Inventory.Store.LibraryRootNode.Data.OwnerID);
                _StopWatch.Start();
                Bot.Client.Inventory.RequestFolderContents(Bot.Client.Inventory.Store.RootFolder.UUID, Bot.Client.Self.AgentID, true, true, InventorySortOrder.ByDate);
                Bot.Client.Inventory.RequestFolderContents(Bot.Client.Inventory.Store.LibraryRootNode.Data.UUID, Bot.Client.Inventory.Store.LibraryRootNode.Data.OwnerID, true, true, InventorySortOrder.ByDate);
            }

            Thread.Sleep(1000);
            Console.WriteLine("Total items: " + Bot.Client.Inventory.Store.Items.Count + "; Total requests: " + _Requests + "; Time: " + _StopWatch.Elapsed);

        }

        void Inventory_FolderUpdated(object sender, FolderUpdatedEventArgs e)
        {
            if (e.Success)
            {
                //Console.WriteLine("Folder " + e.FolderID + " updated");
                bool fetch = false;
                lock (_processed)
                {
                    if (!_processed.Contains(e.FolderID))
                    {
                        _processed.Add(e.FolderID);
                        fetch = true;
                    }
                }

                if (fetch)
                {
                    List<InventoryFolder> _foldersToFetch = new List<InventoryFolder>();
                    foreach (InventoryBase item in Bot.Client.Inventory.Store.GetContents(e.FolderID))
                    {
                        if (item is InventoryFolder)
                        {
                            InventoryFolder f = new InventoryFolder(item.UUID)
                            {
                                OwnerID = item.OwnerID
                            };
                            _foldersToFetch.Add(f);
                        }
                    }
                    if (_foldersToFetch.Count > 0)
                    {
                        _Requests += 1;
                        Bot.Client.Inventory.RequestFolderContentsCap(_foldersToFetch, Bot.Client.Network.CurrentSim.Caps.CapabilityURI("FetchInventoryDescendents2"), true, true, InventorySortOrder.ByDate);
                    }
                }

                if (Bot.Client.Inventory.Store.Items.Count >= 15739)
                {
                    _StopWatch.Stop();
                    Console.WriteLine("Stop! Total items: " + Bot.Client.Inventory.Store.Items.Count + "; Total requests: " + _Requests + "; Time: " + _StopWatch.Elapsed);
                }
            }

        }

        public override void Interrupt()
        {
            _interruptEvent.Set();
        }
    }
}