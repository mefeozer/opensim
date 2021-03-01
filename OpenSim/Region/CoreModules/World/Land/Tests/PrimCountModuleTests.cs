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
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;

namespace OpenSim.Region.CoreModules.World.Land.Tests
{
    [TestFixture]
    public class PrimCountModuleTests : OpenSimTestCase
    {
        protected UUID _userId = new UUID("00000000-0000-0000-0000-100000000000");
        protected UUID _groupId = new UUID("00000000-0000-0000-8888-000000000000");
        protected UUID _otherUserId = new UUID("99999999-9999-9999-9999-999999999999");
        protected TestScene _scene;
        protected PrimCountModule _pcm;

        /// <summary>
        /// A parcel that covers the entire sim except for a 1 unit wide strip on the eastern side.
        /// </summary>
        protected ILandObject _lo;

        /// <summary>
        /// A parcel that covers just the eastern strip of the sim.
        /// </summary>
        protected ILandObject _lo2;

        [SetUp]
        public override void SetUp()
        {
            base.SetUp();

            _pcm = new PrimCountModule();
            LandManagementModule lmm = new LandManagementModule();
            _scene = new SceneHelpers().SetupScene();
            SceneHelpers.SetupSceneModules(_scene, lmm, _pcm);

            int xParcelDivider = (int)Constants.RegionSize - 1;

            ILandObject lo = new LandObject(_userId, false, _scene);
            lo.LandData.Name = "_lo";
            lo.SetLandBitmap(
                lo.GetSquareLandBitmap(0, 0, xParcelDivider, (int)Constants.RegionSize));
            _lo = lmm.AddLandObject(lo);

            ILandObject lo2 = new LandObject(_userId, false, _scene);
            lo2.SetLandBitmap(
                lo2.GetSquareLandBitmap(xParcelDivider, 0, (int)Constants.RegionSize, (int)Constants.RegionSize));
            lo2.LandData.Name = "_lo2";
            _lo2 = lmm.AddLandObject(lo2);
        }

        /// <summary>
        /// Test that counts before we do anything are correct.
        /// </summary>
        [Test]
        public void TestInitialCounts()
        {
            IPrimCounts pc = _lo.PrimCounts;

            Assert.That(pc.Owner, Is.EqualTo(0));
            Assert.That(pc.Group, Is.EqualTo(0));
            Assert.That(pc.Others, Is.EqualTo(0));
            Assert.That(pc.Total, Is.EqualTo(0));
            Assert.That(pc.Selected, Is.EqualTo(0));
            Assert.That(pc.Users[_userId], Is.EqualTo(0));
            Assert.That(pc.Users[_otherUserId], Is.EqualTo(0));
            Assert.That(pc.Simulator, Is.EqualTo(0));
        }

        /// <summary>
        /// Test count after a parcel owner owned object is added.
        /// </summary>
        [Test]
        public void TestAddOwnerObject()
        {
            TestHelpers.InMethod();
//            log4net.Config.XmlConfigurator.Configure();

            IPrimCounts pc = _lo.PrimCounts;

            SceneObjectGroup sog = SceneHelpers.CreateSceneObject(3, _userId, "a", 0x01);
            _scene.AddNewSceneObject(sog, false);

            Assert.That(pc.Owner, Is.EqualTo(3));
            Assert.That(pc.Group, Is.EqualTo(0));
            Assert.That(pc.Others, Is.EqualTo(0));
            Assert.That(pc.Total, Is.EqualTo(3));
            Assert.That(pc.Selected, Is.EqualTo(0));
            Assert.That(pc.Users[_userId], Is.EqualTo(3));
            Assert.That(pc.Users[_otherUserId], Is.EqualTo(0));
            Assert.That(pc.Simulator, Is.EqualTo(3));

            // Add a second object and retest
            SceneObjectGroup sog2 = SceneHelpers.CreateSceneObject(2, _userId, "b", 0x10);
            _scene.AddNewSceneObject(sog2, false);

            Assert.That(pc.Owner, Is.EqualTo(5));
            Assert.That(pc.Group, Is.EqualTo(0));
            Assert.That(pc.Others, Is.EqualTo(0));
            Assert.That(pc.Total, Is.EqualTo(5));
            Assert.That(pc.Selected, Is.EqualTo(0));
            Assert.That(pc.Users[_userId], Is.EqualTo(5));
            Assert.That(pc.Users[_otherUserId], Is.EqualTo(0));
            Assert.That(pc.Simulator, Is.EqualTo(5));
        }

        /// <summary>
        /// Test count after a parcel owner owned copied object is added.
        /// </summary>
        [Test]
        public void TestCopyOwnerObject()
        {
            TestHelpers.InMethod();
//            log4net.Config.XmlConfigurator.Configure();

            IPrimCounts pc = _lo.PrimCounts;

            SceneObjectGroup sog = SceneHelpers.CreateSceneObject(3, _userId, "a", 0x01);
            _scene.AddNewSceneObject(sog, false);
            _scene.SceneGraph.DuplicateObject(sog.LocalId, Vector3.Zero, _userId, UUID.Zero, Quaternion.Identity, false);

            Assert.That(pc.Owner, Is.EqualTo(6));
            Assert.That(pc.Group, Is.EqualTo(0));
            Assert.That(pc.Others, Is.EqualTo(0));
            Assert.That(pc.Total, Is.EqualTo(6));
            Assert.That(pc.Selected, Is.EqualTo(0));
            Assert.That(pc.Users[_userId], Is.EqualTo(6));
            Assert.That(pc.Users[_otherUserId], Is.EqualTo(0));
            Assert.That(pc.Simulator, Is.EqualTo(6));
        }

        /// <summary>
        /// Test that parcel counts update correctly when an object is moved between parcels, where that movement
        /// is not done directly by the user/
        /// </summary>
        [Test]
        public void TestMoveOwnerObject()
        {
            TestHelpers.InMethod();
//            log4net.Config.XmlConfigurator.Configure();

            SceneObjectGroup sog = SceneHelpers.CreateSceneObject(3, _userId, "a", 0x01);
            _scene.AddNewSceneObject(sog, false);
            SceneObjectGroup sog2 = SceneHelpers.CreateSceneObject(2, _userId, "b", 0x10);
            _scene.AddNewSceneObject(sog2, false);

            // Move the first scene object to the eastern strip parcel
            sog.AbsolutePosition = new Vector3(254, 2, 2);

            IPrimCounts pclo1 = _lo.PrimCounts;

            Assert.That(pclo1.Owner, Is.EqualTo(2));
            Assert.That(pclo1.Group, Is.EqualTo(0));
            Assert.That(pclo1.Others, Is.EqualTo(0));
            Assert.That(pclo1.Total, Is.EqualTo(2));
            Assert.That(pclo1.Selected, Is.EqualTo(0));
            Assert.That(pclo1.Users[_userId], Is.EqualTo(2));
            Assert.That(pclo1.Users[_otherUserId], Is.EqualTo(0));
            Assert.That(pclo1.Simulator, Is.EqualTo(5));

            IPrimCounts pclo2 = _lo2.PrimCounts;

            Assert.That(pclo2.Owner, Is.EqualTo(3));
            Assert.That(pclo2.Group, Is.EqualTo(0));
            Assert.That(pclo2.Others, Is.EqualTo(0));
            Assert.That(pclo2.Total, Is.EqualTo(3));
            Assert.That(pclo2.Selected, Is.EqualTo(0));
            Assert.That(pclo2.Users[_userId], Is.EqualTo(3));
            Assert.That(pclo2.Users[_otherUserId], Is.EqualTo(0));
            Assert.That(pclo2.Simulator, Is.EqualTo(5));

            // Now move it back again
            sog.AbsolutePosition = new Vector3(2, 2, 2);

            Assert.That(pclo1.Owner, Is.EqualTo(5));
            Assert.That(pclo1.Group, Is.EqualTo(0));
            Assert.That(pclo1.Others, Is.EqualTo(0));
            Assert.That(pclo1.Total, Is.EqualTo(5));
            Assert.That(pclo1.Selected, Is.EqualTo(0));
            Assert.That(pclo1.Users[_userId], Is.EqualTo(5));
            Assert.That(pclo1.Users[_otherUserId], Is.EqualTo(0));
            Assert.That(pclo1.Simulator, Is.EqualTo(5));

            Assert.That(pclo2.Owner, Is.EqualTo(0));
            Assert.That(pclo2.Group, Is.EqualTo(0));
            Assert.That(pclo2.Others, Is.EqualTo(0));
            Assert.That(pclo2.Total, Is.EqualTo(0));
            Assert.That(pclo2.Selected, Is.EqualTo(0));
            Assert.That(pclo2.Users[_userId], Is.EqualTo(0));
            Assert.That(pclo2.Users[_otherUserId], Is.EqualTo(0));
            Assert.That(pclo2.Simulator, Is.EqualTo(5));
        }

        /// <summary>
        /// Test count after a parcel owner owned object is removed.
        /// </summary>
        [Test]
        public void TestRemoveOwnerObject()
        {
            TestHelpers.InMethod();
//            log4net.Config.XmlConfigurator.Configure();

            IPrimCounts pc = _lo.PrimCounts;

            _scene.AddNewSceneObject(SceneHelpers.CreateSceneObject(1, _userId, "a", 0x1), false);
            SceneObjectGroup sogToDelete = SceneHelpers.CreateSceneObject(3, _userId, "b", 0x10);
            _scene.AddNewSceneObject(sogToDelete, false);
            _scene.DeleteSceneObject(sogToDelete, false);

            Assert.That(pc.Owner, Is.EqualTo(1));
            Assert.That(pc.Group, Is.EqualTo(0));
            Assert.That(pc.Others, Is.EqualTo(0));
            Assert.That(pc.Total, Is.EqualTo(1));
            Assert.That(pc.Selected, Is.EqualTo(0));
            Assert.That(pc.Users[_userId], Is.EqualTo(1));
            Assert.That(pc.Users[_otherUserId], Is.EqualTo(0));
            Assert.That(pc.Simulator, Is.EqualTo(1));
        }

        [Test]
        public void TestAddGroupObject()
        {
            TestHelpers.InMethod();
//            log4net.Config.XmlConfigurator.Configure();

            _lo.DeedToGroup(_groupId);

            IPrimCounts pc = _lo.PrimCounts;

            SceneObjectGroup sog = SceneHelpers.CreateSceneObject(3, _otherUserId, "a", 0x01);
            sog.GroupID = _groupId;
            _scene.AddNewSceneObject(sog, false);

            Assert.That(pc.Owner, Is.EqualTo(0));
            Assert.That(pc.Group, Is.EqualTo(3));
            Assert.That(pc.Others, Is.EqualTo(0));
            Assert.That(pc.Total, Is.EqualTo(3));
            Assert.That(pc.Selected, Is.EqualTo(0));

            // Is this desired behaviour?  Not totally sure.
            Assert.That(pc.Users[_userId], Is.EqualTo(0));
            Assert.That(pc.Users[_groupId], Is.EqualTo(0));
            Assert.That(pc.Users[_otherUserId], Is.EqualTo(3));

            Assert.That(pc.Simulator, Is.EqualTo(3));
        }

        /// <summary>
        /// Test count after a parcel owner owned object is removed.
        /// </summary>
        [Test]
        public void TestRemoveGroupObject()
        {
            TestHelpers.InMethod();
//            log4net.Config.XmlConfigurator.Configure();

            _lo.DeedToGroup(_groupId);

            IPrimCounts pc = _lo.PrimCounts;

            SceneObjectGroup sogToKeep = SceneHelpers.CreateSceneObject(1, _userId, "a", 0x1);
            sogToKeep.GroupID = _groupId;
            _scene.AddNewSceneObject(sogToKeep, false);

            SceneObjectGroup sogToDelete = SceneHelpers.CreateSceneObject(3, _userId, "b", 0x10);
            _scene.AddNewSceneObject(sogToDelete, false);
            _scene.DeleteSceneObject(sogToDelete, false);

            Assert.That(pc.Owner, Is.EqualTo(0));
            Assert.That(pc.Group, Is.EqualTo(1));
            Assert.That(pc.Others, Is.EqualTo(0));
            Assert.That(pc.Total, Is.EqualTo(1));
            Assert.That(pc.Selected, Is.EqualTo(0));
            Assert.That(pc.Users[_userId], Is.EqualTo(1));
            Assert.That(pc.Users[_groupId], Is.EqualTo(0));
            Assert.That(pc.Users[_otherUserId], Is.EqualTo(0));
            Assert.That(pc.Simulator, Is.EqualTo(1));
        }

        [Test]
        public void TestAddOthersObject()
        {
            TestHelpers.InMethod();
//            log4net.Config.XmlConfigurator.Configure();

            IPrimCounts pc = _lo.PrimCounts;

            SceneObjectGroup sog = SceneHelpers.CreateSceneObject(3, _otherUserId, "a", 0x01);
            _scene.AddNewSceneObject(sog, false);

            Assert.That(pc.Owner, Is.EqualTo(0));
            Assert.That(pc.Group, Is.EqualTo(0));
            Assert.That(pc.Others, Is.EqualTo(3));
            Assert.That(pc.Total, Is.EqualTo(3));
            Assert.That(pc.Selected, Is.EqualTo(0));
            Assert.That(pc.Users[_userId], Is.EqualTo(0));
            Assert.That(pc.Users[_otherUserId], Is.EqualTo(3));
            Assert.That(pc.Simulator, Is.EqualTo(3));
        }

        [Test]
        public void TestRemoveOthersObject()
        {
            TestHelpers.InMethod();
//            log4net.Config.XmlConfigurator.Configure();

            IPrimCounts pc = _lo.PrimCounts;

            _scene.AddNewSceneObject(SceneHelpers.CreateSceneObject(1, _otherUserId, "a", 0x1), false);
            SceneObjectGroup sogToDelete = SceneHelpers.CreateSceneObject(3, _otherUserId, "b", 0x10);
            _scene.AddNewSceneObject(sogToDelete, false);
            _scene.DeleteSceneObject(sogToDelete, false);

            Assert.That(pc.Owner, Is.EqualTo(0));
            Assert.That(pc.Group, Is.EqualTo(0));
            Assert.That(pc.Others, Is.EqualTo(1));
            Assert.That(pc.Total, Is.EqualTo(1));
            Assert.That(pc.Selected, Is.EqualTo(0));
            Assert.That(pc.Users[_userId], Is.EqualTo(0));
            Assert.That(pc.Users[_otherUserId], Is.EqualTo(1));
            Assert.That(pc.Simulator, Is.EqualTo(1));
        }

        /// <summary>
        /// Test the count is correct after is has been tainted.
        /// </summary>
        [Test]
        public void TestTaint()
        {
            TestHelpers.InMethod();
            IPrimCounts pc = _lo.PrimCounts;

            SceneObjectGroup sog = SceneHelpers.CreateSceneObject(3, _userId, "a", 0x01);
            _scene.AddNewSceneObject(sog, false);

            _pcm.TaintPrimCount();

            Assert.That(pc.Owner, Is.EqualTo(3));
            Assert.That(pc.Group, Is.EqualTo(0));
            Assert.That(pc.Others, Is.EqualTo(0));
            Assert.That(pc.Total, Is.EqualTo(3));
            Assert.That(pc.Selected, Is.EqualTo(0));
            Assert.That(pc.Users[_userId], Is.EqualTo(3));
            Assert.That(pc.Users[_otherUserId], Is.EqualTo(0));
            Assert.That(pc.Simulator, Is.EqualTo(3));
        }
    }
}