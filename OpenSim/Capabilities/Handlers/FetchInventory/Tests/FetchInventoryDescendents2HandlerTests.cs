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
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;

namespace OpenSim.Capabilities.Handlers.FetchInventory.Tests
{
    [TestFixture]
    public class FetchInventoryDescendents2HandlerTests : OpenSimTestCase
    {
        private UUID _userID = new UUID("00000000-0000-0000-0000-000000000001");
        private Scene _scene;
        private UUID _rootFolderID;
        private int _rootDescendents;
        private UUID _notecardsFolder;
        private UUID _objectsFolder;

        private void Init()
        {
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

            _scene = new SceneHelpers().SetupScene();

            _scene.InventoryService.CreateUserInventory(_userID);

            _rootFolderID = _scene.InventoryService.GetRootFolder(_userID).ID;

            InventoryFolderBase of = _scene.InventoryService.GetFolderForType(_userID, FolderType.Object);
            _objectsFolder = of.ID;

            // Add an object
            InventoryItemBase item = new InventoryItemBase(new UUID("b0000000-0000-0000-0000-00000000000b"), _userID)
            {
                AssetID = UUID.Random(),
                AssetType = (int)AssetType.Object,
                Folder = _objectsFolder,
                Name = "Some Object"
            };
            _scene.InventoryService.AddItem(item);

            InventoryFolderBase ncf = _scene.InventoryService.GetFolderForType(_userID, FolderType.Notecard);
            _notecardsFolder = ncf.ID;

            // Add a notecard
            item = new InventoryItemBase(new UUID("10000000-0000-0000-0000-000000000001"), _userID)
            {
                AssetID = UUID.Random(),
                AssetType = (int)AssetType.Notecard,
                Folder = _notecardsFolder,
                Name = "Test Notecard 1"
            };
            _scene.InventoryService.AddItem(item);
            // Add another notecard
            item.ID = new UUID("20000000-0000-0000-0000-000000000002");
            item.AssetID = new UUID("a0000000-0000-0000-0000-00000000000a");
            item.Name = "Test Notecard 2";
            _scene.InventoryService.AddItem(item);

            // Add a folder
            InventoryFolderBase folder = new InventoryFolderBase(new UUID("f0000000-0000-0000-0000-00000000000f"), "Test Folder", _userID, _rootFolderID)
            {
                Type = (short)FolderType.None
            };
            _scene.InventoryService.AddFolder(folder);

            // Add a link to notecard 2 in Test Folder
            item.AssetID = item.ID; // use item ID of notecard 2
            item.ID = new UUID("40000000-0000-0000-0000-000000000004");
            item.AssetType = (int)AssetType.Link;
            item.Folder = folder.ID;
            item.Name = "Link to notecard";
            _scene.InventoryService.AddItem(item);

            // Add a link to the Objects folder in Test Folder
            item.AssetID = _scene.InventoryService.GetFolderForType(_userID, FolderType.Object).ID; // use item ID of Objects folder
            item.ID = new UUID("50000000-0000-0000-0000-000000000005");
            item.AssetType = (int)AssetType.LinkFolder;
            item.Folder = folder.ID;
            item.Name = "Link to Objects folder";
            _scene.InventoryService.AddItem(item);

            InventoryCollection coll = _scene.InventoryService.GetFolderContent(_userID, _rootFolderID);
            _rootDescendents = coll.Items.Count + coll.Folders.Count;
            Console.WriteLine("Number of descendents: " + _rootDescendents);
        }

        private string dorequest(FetchInvDescHandler handler, string request)
        {
            TestOSHttpRequest req = new TestOSHttpRequest();
            TestOSHttpResponse resp = new TestOSHttpResponse();
            using(ExpiringKey<UUID> bad = new ExpiringKey<UUID>(5000)) // bad but this is test
            using (MemoryStream ms = new MemoryStream(Utils.StringToBytes(request), false))
            {
                req.InputStream = ms;
                handler.FetchInventoryDescendentsRequest(req, resp, bad);
            }
            return Util.UTF8.GetString(resp.RawBuffer);
        }

        [Test]
        public void Test_001_SimpleFolder()
        {
            TestHelpers.InMethod();

            Init();

            FetchInvDescHandler handler = new FetchInvDescHandler(_scene.InventoryService, null, _scene);

            string request = "<llsd><map><key>folders</key><array><map><key>fetch_folders</key><integer>1</integer><key>fetch_items</key><boolean>1</boolean><key>folder_id</key><uuid>";
            request += _rootFolderID;
            request += "</uuid><key>owner_id</key><uuid>";
            request += _userID.ToString();
            request += "</uuid><key>sort_order</key><integer>1</integer></map></array></map></llsd>";

            string llsdresponse = dorequest(handler, request);

            Assert.That(llsdresponse != null, Is.True, "Incorrect null response");
            Assert.That(!string.IsNullOrEmpty(llsdresponse), Is.True, "Incorrect empty response");
            Assert.That(llsdresponse.Contains(_userID.ToString()), Is.True, "Response should contain userID");

            string descendents = "descendents</key><integer>" + _rootDescendents + "</integer>";
            Assert.That(llsdresponse.Contains(descendents), Is.True, "Incorrect number of descendents");
            Console.WriteLine(llsdresponse);
        }

        [Test]
        public void Test_002_MultipleFolders()
        {
            TestHelpers.InMethod();

            FetchInvDescHandler handler = new FetchInvDescHandler(_scene.InventoryService, null, _scene);

            string request = "<llsd><map><key>folders</key><array>";
            request += "<map><key>fetch_folders</key><integer>1</integer><key>fetch_items</key><boolean>1</boolean><key>folder_id</key><uuid>";
            request += _rootFolderID;
            request += "</uuid><key>owner_id</key><uuid>00000000-0000-0000-0000-000000000001</uuid><key>sort_order</key><integer>1</integer></map>";
            request += "<map><key>fetch_folders</key><integer>1</integer><key>fetch_items</key><boolean>1</boolean><key>folder_id</key><uuid>";
            request += _notecardsFolder;
            request += "</uuid><key>owner_id</key><uuid>00000000-0000-0000-0000-000000000001</uuid><key>sort_order</key><integer>1</integer></map>";
            request += "</array></map></llsd>";

            string llsdresponse = dorequest(handler, request);
            Console.WriteLine(llsdresponse);

            string descendents = "descendents</key><integer>" + _rootDescendents + "</integer>";
            Assert.That(llsdresponse.Contains(descendents), Is.True, "Incorrect number of descendents for root folder");
            descendents = "descendents</key><integer>2</integer>";
            Assert.That(llsdresponse.Contains(descendents), Is.True, "Incorrect number of descendents for Notecard folder");

            Assert.That(llsdresponse.Contains("10000000-0000-0000-0000-000000000001"), Is.True, "Notecard 1 is missing from response");
            Assert.That(llsdresponse.Contains("20000000-0000-0000-0000-000000000002"), Is.True, "Notecard 2 is missing from response");
        }

        [Test]
        public void Test_003_Links()
        {
            TestHelpers.InMethod();

            FetchInvDescHandler handler = new FetchInvDescHandler(_scene.InventoryService, null, _scene);

            string request = "<llsd><map><key>folders</key><array><map><key>fetch_folders</key><integer>1</integer><key>fetch_items</key><boolean>1</boolean><key>folder_id</key><uuid>";
            request += "f0000000-0000-0000-0000-00000000000f";
            request += "</uuid><key>owner_id</key><uuid>00000000-0000-0000-0000-000000000001</uuid><key>sort_order</key><integer>1</integer></map></array></map></llsd>";

            string llsdresponse = dorequest(handler, request);
            Console.WriteLine(llsdresponse);

            string descendents = "descendents</key><integer>2</integer>";
            Assert.That(llsdresponse.Contains(descendents), Is.True, "Incorrect number of descendents for Test Folder");

            // Make sure that the note card link is included
            Assert.That(llsdresponse.Contains("Link to notecard"), Is.True, "Link to notecard is missing");

            //Make sure the notecard item itself is included
            Assert.That(llsdresponse.Contains("Test Notecard 2"), Is.True, "Notecard 2 item (the source) is missing");

            // Make sure that the source item is before the link item
            int pos1 = llsdresponse.IndexOf("Test Notecard 2");
            int pos2 = llsdresponse.IndexOf("Link to notecard");
            Assert.Less(pos1, pos2, "Source of link is after link");

            // Make sure the folder link is included
            Assert.That(llsdresponse.Contains("Link to Objects folder"), Is.True, "Link to Objects folder is missing");

/* contents of link folder are not supposed to be listed
            // Make sure the objects inside the Objects folder are included
            // Note: I'm not entirely sure this is needed, but that's what I found in the implementation
            Assert.That(llsdresponse.Contains("Some Object"), Is.True, "Some Object item (contents of the source) is missing");
*/
            // Make sure that the source item is before the link item
            pos1 = llsdresponse.IndexOf("Some Object");
            pos2 = llsdresponse.IndexOf("Link to Objects folder");
            Assert.Less(pos1, pos2, "Contents of source of folder link is after folder link");
        }

        [Test]
        public void Test_004_DuplicateFolders()
        {
            TestHelpers.InMethod();

            FetchInvDescHandler handler = new FetchInvDescHandler(_scene.InventoryService, null, _scene);

            string request = "<llsd><map><key>folders</key><array>";
            request += "<map><key>fetch_folders</key><integer>1</integer><key>fetch_items</key><boolean>1</boolean><key>folder_id</key><uuid>";
            request += _rootFolderID;
            request += "</uuid><key>owner_id</key><uuid>00000000-0000-0000-0000-000000000000</uuid><key>sort_order</key><integer>1</integer></map>";
            request += "<map><key>fetch_folders</key><integer>1</integer><key>fetch_items</key><boolean>1</boolean><key>folder_id</key><uuid>";
            request += _notecardsFolder;
            request += "</uuid><key>owner_id</key><uuid>00000000-0000-0000-0000-000000000000</uuid><key>sort_order</key><integer>1</integer></map>";
            request += "<map><key>fetch_folders</key><integer>1</integer><key>fetch_items</key><boolean>1</boolean><key>folder_id</key><uuid>";
            request += _rootFolderID;
            request += "</uuid><key>owner_id</key><uuid>00000000-0000-0000-0000-000000000000</uuid><key>sort_order</key><integer>1</integer></map>";
            request += "<map><key>fetch_folders</key><integer>1</integer><key>fetch_items</key><boolean>1</boolean><key>folder_id</key><uuid>";
            request += _notecardsFolder;
            request += "</uuid><key>owner_id</key><uuid>00000000-0000-0000-0000-000000000000</uuid><key>sort_order</key><integer>1</integer></map>";
            request += "</array></map></llsd>";

            string llsdresponse = dorequest(handler, request);
            Console.WriteLine(llsdresponse);

            string root_folder = "<key>folder_id</key><uuid>" + _rootFolderID + "</uuid>";
            string notecards_folder = "<key>folder_id</key><uuid>" + _notecardsFolder + "</uuid>";

            Assert.That(llsdresponse.Contains(root_folder), "Missing root folder");
            Assert.That(llsdresponse.Contains(notecards_folder), "Missing notecards folder");
            int count = Regex.Matches(llsdresponse, root_folder).Count;
            Assert.AreEqual(1, count, "More than 1 root folder in response");
            count = Regex.Matches(llsdresponse, notecards_folder).Count;
            Assert.AreEqual(2, count, "More than 1 notecards folder in response"); // Notecards will also be under root, so 2
        }

        [Test]
        public void Test_005_FolderZero()
        {

            TestHelpers.InMethod();

            Init();

            FetchInvDescHandler handler = new FetchInvDescHandler(_scene.InventoryService, null, _scene);

            string request = "<llsd><map><key>folders</key><array><map><key>fetch_folders</key><integer>1</integer><key>fetch_items</key><boolean>1</boolean><key>folder_id</key><uuid>";
            request += UUID.Zero;
            request += "</uuid><key>owner_id</key><uuid>00000000-0000-0000-0000-000000000000</uuid><key>sort_order</key><integer>1</integer></map></array></map></llsd>";

            string llsdresponse = dorequest(handler, request);

            Assert.That(llsdresponse != null, Is.True, "Incorrect null response");
            Assert.That(!string.IsNullOrEmpty(llsdresponse), Is.True, "Incorrect empty response");
            // we do return a answer now
            //Assert.That(llsdresponse.Contains("bad_folders</key><array><uuid>00000000-0000-0000-0000-000000000000"), Is.True, "Folder Zero should be a bad folder");

            Console.WriteLine(llsdresponse);
        }
    }

}