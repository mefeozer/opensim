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
using NUnit.Framework;

using OpenSim.Framework;
using OpenSim.Services.Connectors;

using OpenSim.Tests.Common;

namespace Robust.Tests
{
    [TestFixture]
    public class InventoryClient
    {
//        private static readonly ILog _log =
//                LogManager.GetLogger(
//                MethodBase.GetCurrentMethod().DeclaringType);

        private readonly UUID _userID = new UUID("00000000-0000-0000-0000-333333333333");
        private UUID _rootFolderID;
        private UUID _notecardsFolder;
        private UUID _objectsFolder;

        [Test]
        public void Inventory_001_CreateInventory()
        {
            TestHelpers.InMethod();
            XInventoryServicesConnector _Connector = new XInventoryServicesConnector(DemonServer.Address);

            // Create an inventory that looks like this:
            //
            // /My Inventory
            //   <other system folders>
            //   /Objects
            //      Some Object
            //   /Notecards
            //      Notecard 1
            //      Notecard 2
            //   /Test Folder
            //      Link to notecard  -> /Notecards/Notecard 2
            //      Link to Objects folder -> /Objects

            bool success = _Connector.CreateUserInventory(_userID);
            Assert.IsTrue(success, "Failed to create user inventory");

            _rootFolderID = _Connector.GetRootFolder(_userID).ID;
            Assert.AreNotEqual(_rootFolderID, UUID.Zero, "Root folder ID must not be UUID.Zero");

            InventoryFolderBase of = _Connector.GetFolderForType(_userID, FolderType.Object);
            Assert.IsNotNull(of, "Failed to retrieve Objects folder");
            _objectsFolder = of.ID;
            Assert.AreNotEqual(_objectsFolder, UUID.Zero, "Objects folder ID must not be UUID.Zero");

            // Add an object
            InventoryItemBase item = new InventoryItemBase(new UUID("b0000000-0000-0000-0000-00000000000b"), _userID)
            {
                AssetID = UUID.Random(),
                AssetType = (int)AssetType.Object,
                Folder = _objectsFolder,
                Name = "Some Object",
                Description = string.Empty
            };
            success = _Connector.AddItem(item);
            Assert.IsTrue(success, "Failed to add object to inventory");

            InventoryFolderBase ncf = _Connector.GetFolderForType(_userID, FolderType.Notecard);
            Assert.IsNotNull(of, "Failed to retrieve Notecards folder");
            _notecardsFolder = ncf.ID;
            Assert.AreNotEqual(_notecardsFolder, UUID.Zero, "Notecards folder ID must not be UUID.Zero");
            _notecardsFolder = ncf.ID;

            // Add a notecard
            item = new InventoryItemBase(new UUID("10000000-0000-0000-0000-000000000001"), _userID)
            {
                AssetID = UUID.Random(),
                AssetType = (int)AssetType.Notecard,
                Folder = _notecardsFolder,
                Name = "Test Notecard 1",
                Description = string.Empty
            };
            success = _Connector.AddItem(item);
            Assert.IsTrue(success, "Failed to add Notecard 1 to inventory");
            // Add another notecard
            item.ID = new UUID("20000000-0000-0000-0000-000000000002");
            item.AssetID = new UUID("a0000000-0000-0000-0000-00000000000a");
            item.Name = "Test Notecard 2";
            item.Description = string.Empty;
            success = _Connector.AddItem(item);
            Assert.IsTrue(success, "Failed to add Notecard 2 to inventory");

            // Add a folder
            InventoryFolderBase folder = new InventoryFolderBase(new UUID("f0000000-0000-0000-0000-00000000000f"), "Test Folder", _userID, _rootFolderID)
            {
                Type = (int)FolderType.None
            };
            success = _Connector.AddFolder(folder);
            Assert.IsTrue(success, "Failed to add Test Folder to inventory");

            // Add a link to notecard 2 in Test Folder
            item.AssetID = item.ID; // use item ID of notecard 2
            item.ID = new UUID("40000000-0000-0000-0000-000000000004");
            item.AssetType = (int)AssetType.Link;
            item.Folder = folder.ID;
            item.Name = "Link to notecard";
            item.Description = string.Empty;
            success = _Connector.AddItem(item);
            Assert.IsTrue(success, "Failed to add link to notecard to inventory");

            // Add a link to the Objects folder in Test Folder
            item.AssetID = _Connector.GetFolderForType(_userID, FolderType.Object).ID; // use item ID of Objects folder
            item.ID = new UUID("50000000-0000-0000-0000-000000000005");
            item.AssetType = (int)AssetType.LinkFolder;
            item.Folder = folder.ID;
            item.Name = "Link to Objects folder";
            item.Description = string.Empty;
            success = _Connector.AddItem(item);
            Assert.IsTrue(success, "Failed to add link to objects folder to inventory");

            InventoryCollection coll = _Connector.GetFolderContent(_userID, _rootFolderID);
            Assert.IsNotNull(coll, "Failed to retrieve contents of root folder");
            Assert.Greater(coll.Folders.Count, 0, "Root folder does not have any subfolders");

            coll = _Connector.GetFolderContent(_userID, folder.ID);
            Assert.IsNotNull(coll, "Failed to retrieve contents of Test Folder");
            Assert.AreEqual(coll.Items.Count + coll.Folders.Count, 2, "Test Folder is expected to have exactly 2 things inside");

        }

        [Test]
        public void Inventory_002_MultipleItemsRequest()
        {
            TestHelpers.InMethod();
            XInventoryServicesConnector _Connector = new XInventoryServicesConnector(DemonServer.Address);

            // Prefetch Notecard 1, will be cached from here on
            InventoryItemBase item = _Connector.GetItem(_userID, new UUID("10000000-0000-0000-0000-000000000001"));
            Assert.NotNull(item, "Failed to get Notecard 1");
            Assert.AreEqual("Test Notecard 1", item.Name, "Wrong name for Notecard 1");

            UUID[] uuids = new UUID[2];
            uuids[0] = item.ID;
            uuids[1] = new UUID("20000000-0000-0000-0000-000000000002");

            InventoryItemBase[] items = _Connector.GetMultipleItems(_userID, uuids);
            Assert.NotNull(items, "Failed to get multiple items");
            Assert.IsTrue(items.Length == 2, "Requested 2 items, but didn't receive 2 items");

            // Now they should both be cached
            items = _Connector.GetMultipleItems(_userID, uuids);
            Assert.NotNull(items, "(Repeat) Failed to get multiple items");
            Assert.IsTrue(items.Length == 2, "(Repeat) Requested 2 items, but didn't receive 2 items");

            // This item doesn't exist, but [0] does, and it's cached.
            uuids[1] = new UUID("bb000000-0000-0000-0000-0000000000bb");
            // Fetching should return 2 items, but [1] should be null
            items = _Connector.GetMultipleItems(_userID, uuids);
            Assert.NotNull(items, "(Three times) Failed to get multiple items");
            Assert.IsTrue(items.Length == 2, "(Three times) Requested 2 items, but didn't receive 2 items");
            Assert.AreEqual("Test Notecard 1", items[0].Name, "(Three times) Wrong name for Notecard 1");
            Assert.IsNull(items[1], "(Three times) Expecting 2nd item to be null");

            // Now both don't exist
            uuids[0] = new UUID("aa000000-0000-0000-0000-0000000000aa");
            items = _Connector.GetMultipleItems(_userID, uuids);
            Assert.Null(items[0], "Request to multiple non-existent items is supposed to return null [0]");
            Assert.Null(items[1], "Request to multiple non-existent items is supposed to return null [1]");

            // This item exists, and it's not cached
            uuids[1] = new UUID("b0000000-0000-0000-0000-00000000000b");
            // Fetching should return 2 items, but [0] should be null
            items = _Connector.GetMultipleItems(_userID, uuids);
            Assert.NotNull(items, "(Four times) Failed to get multiple items");
            Assert.IsTrue(items.Length == 2, "(Four times) Requested 2 items, but didn't receive 2 items");
            Assert.AreEqual("Some Object", items[1].Name, "(Four times) Wrong name for Some Object");
            Assert.IsNull(items[0], "(Four times) Expecting 1st item to be null");

        }
    }
}
