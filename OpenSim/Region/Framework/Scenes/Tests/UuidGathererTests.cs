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

using NUnit.Framework;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Services.Interfaces;
using OpenSim.Tests.Common;

namespace OpenSim.Region.Framework.Scenes.Tests
{
    [TestFixture]
    public class UuidGathererTests : OpenSimTestCase
    {
        protected IAssetService _assetService;
        protected UuidGatherer _uuidGatherer;

        protected static string noteBase = @"Linden text version 2\n{\nLLEmbeddedItems version 1\n
{\ncount 0\n}\nText length xxx\n"; // len does not matter on this test
        [SetUp]
        public void Init()
        {
            // FIXME: We don't need a full scene here - it would be enough to set up the asset service.
            Scene scene = new SceneHelpers().SetupScene();
            _assetService = scene.AssetService;
            _uuidGatherer = new UuidGatherer(_assetService);
        }

        [Test]
        public void TestCorruptAsset()
        {
            TestHelpers.InMethod();

            UUID corruptAssetUuid = UUID.Parse("00000000-0000-0000-0000-000000000666");
            AssetBase corruptAsset
                = AssetHelpers.CreateAsset(corruptAssetUuid, AssetType.Notecard, noteBase + "CORRUPT ASSET", UUID.Zero);
            _assetService.Store(corruptAsset);

            _uuidGatherer.AddForInspection(corruptAssetUuid);
            _uuidGatherer.GatherAll();

            // We count the uuid as gathered even if the asset itself is corrupt.
            Assert.That(_uuidGatherer.GatheredUuids.Count, Is.EqualTo(1));
        }

        /// <summary>
        /// Test requests made for non-existent assets while we're gathering
        /// </summary>
        [Test]
        public void TestMissingAsset()
        {
            TestHelpers.InMethod();

            UUID missingAssetUuid = UUID.Parse("00000000-0000-0000-0000-000000000666");

            _uuidGatherer.AddForInspection(missingAssetUuid);
            _uuidGatherer.GatherAll();

            Assert.That(_uuidGatherer.GatheredUuids.Count, Is.EqualTo(0));
        }

        [Test]
        public void TestNotecardAsset()
        {
            TestHelpers.InMethod();
            // TestHelpers.EnableLogging();

            UUID ownerId = TestHelpers.ParseTail(0x10);
            UUID embeddedId = TestHelpers.ParseTail(0x20);
            UUID secondLevelEmbeddedId = TestHelpers.ParseTail(0x21);
            UUID missingEmbeddedId = TestHelpers.ParseTail(0x22);
            UUID ncAssetId = TestHelpers.ParseTail(0x30);

            AssetBase ncAsset
                = AssetHelpers.CreateNotecardAsset(
                    ncAssetId, string.Format("{0}Hello{1}World{2}", noteBase, embeddedId, missingEmbeddedId));
            _assetService.Store(ncAsset);

            AssetBase embeddedAsset
                = AssetHelpers.CreateNotecardAsset(embeddedId, string.Format("{0}{1} We'll meet again.", noteBase, secondLevelEmbeddedId));
            _assetService.Store(embeddedAsset);

            AssetBase secondLevelEmbeddedAsset
                = AssetHelpers.CreateNotecardAsset(secondLevelEmbeddedId, noteBase + "Don't know where, don't know when.");
            _assetService.Store(secondLevelEmbeddedAsset);

            _uuidGatherer.AddForInspection(ncAssetId);
            _uuidGatherer.GatherAll();

            // foreach (UUID key in _uuidGatherer.GatheredUuids.Keys)
            // System.Console.WriteLine("key : {0}", key);

            Assert.That(_uuidGatherer.GatheredUuids.Count, Is.EqualTo(3));
            Assert.That(_uuidGatherer.GatheredUuids.ContainsKey(ncAssetId));
            Assert.That(_uuidGatherer.GatheredUuids.ContainsKey(embeddedId));
            Assert.That(_uuidGatherer.GatheredUuids.ContainsKey(secondLevelEmbeddedId));
        }

        [Test]
        public void TestTaskItems()
        {
            TestHelpers.InMethod();
            // TestHelpers.EnableLogging();

            UUID ownerId = TestHelpers.ParseTail(0x10);

            SceneObjectGroup soL0 = SceneHelpers.CreateSceneObject(1, ownerId, "l0", 0x20);
            SceneObjectGroup soL1 = SceneHelpers.CreateSceneObject(1, ownerId, "l1", 0x21);
            SceneObjectGroup soL2 = SceneHelpers.CreateSceneObject(1, ownerId, "l2", 0x22);

            TaskInventoryHelpers.AddScript(
                _assetService, soL2.RootPart, TestHelpers.ParseTail(0x33), TestHelpers.ParseTail(0x43), "l3-script", "gibberish");

            TaskInventoryHelpers.AddSceneObject(
                _assetService, soL1.RootPart, "l2-item", TestHelpers.ParseTail(0x32), soL2, TestHelpers.ParseTail(0x42));
            TaskInventoryHelpers.AddSceneObject(
                _assetService, soL0.RootPart, "l1-item", TestHelpers.ParseTail(0x31), soL1, TestHelpers.ParseTail(0x41));

            _uuidGatherer.AddForInspection(soL0);
            _uuidGatherer.GatherAll();

//                        foreach (UUID key in _uuidGatherer.GatheredUuids.Keys)
//                            System.Console.WriteLine("key : {0}", key);

            // We expect to see the default prim texture and the assets of the contained task items
            Assert.That(_uuidGatherer.GatheredUuids.Count, Is.EqualTo(4));
            Assert.That(_uuidGatherer.GatheredUuids.ContainsKey(new UUID(Constants.DefaultTexture)));
            Assert.That(_uuidGatherer.GatheredUuids.ContainsKey(TestHelpers.ParseTail(0x41)));
            Assert.That(_uuidGatherer.GatheredUuids.ContainsKey(TestHelpers.ParseTail(0x42)));
            Assert.That(_uuidGatherer.GatheredUuids.ContainsKey(TestHelpers.ParseTail(0x43)));
        }
    }
}