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

using System.Collections.Generic;
using NUnit.Framework;
using OpenMetaverse;
using OpenSim.Tests.Common;

namespace OpenSim.Region.Framework.Scenes.Tests
{
    /// <summary>
    /// Basic scene object status tests
    /// </summary>
    [TestFixture]
    public class SceneObjectStatusTests : OpenSimTestCase
    {
        private TestScene _scene;
        private readonly UUID _ownerId = TestHelpers.ParseTail(0x1);
        private SceneObjectGroup _so1;
        private SceneObjectGroup _so2;

        [SetUp]
        public void Init()
        {
            _scene = new SceneHelpers().SetupScene();
            _so1 = SceneHelpers.CreateSceneObject(1, _ownerId, "so1", 0x10);
            _so2 = SceneHelpers.CreateSceneObject(1, _ownerId, "so2", 0x20);
        }

        [Test]
        public void TestSetTemporary()
        {
            TestHelpers.InMethod();

            _scene.AddSceneObject(_so1);
            _so1.ScriptSetTemporaryStatus(true);

            // Is this really the correct flag?
            Assert.That(_so1.RootPart.Flags, Is.EqualTo(PrimFlags.TemporaryOnRez));
            Assert.That(_so1.Backup, Is.False);

            // Test setting back to non-temporary
            _so1.ScriptSetTemporaryStatus(false);

            Assert.That(_so1.RootPart.Flags, Is.EqualTo(PrimFlags.None));
            Assert.That(_so1.Backup, Is.True);
        }

        [Test]
        public void TestSetPhantomSinglePrim()
        {
            TestHelpers.InMethod();

            _scene.AddSceneObject(_so1);

            SceneObjectPart rootPart = _so1.RootPart;
            Assert.That(rootPart.Flags, Is.EqualTo(PrimFlags.None));

            _so1.ScriptSetPhantomStatus(true);

//            Console.WriteLine("so.RootPart.Flags [{0}]", so.RootPart.Flags);
            Assert.That(rootPart.Flags, Is.EqualTo(PrimFlags.Phantom));

            _so1.ScriptSetPhantomStatus(false);

            Assert.That(rootPart.Flags, Is.EqualTo(PrimFlags.None));
        }

        [Test]
        public void TestSetNonPhysicsVolumeDetectSinglePrim()
        {
            TestHelpers.InMethod();

            _scene.AddSceneObject(_so1);

            SceneObjectPart rootPart = _so1.RootPart;
            Assert.That(rootPart.Flags, Is.EqualTo(PrimFlags.None));

            _so1.ScriptSetVolumeDetect(true);

//            Console.WriteLine("so.RootPart.Flags [{0}]", so.RootPart.Flags);
            // PrimFlags.JointLP2P is incorrect it now means VolumeDetect (as defined by viewers)
            Assert.That(rootPart.Flags, Is.EqualTo(PrimFlags.Phantom | PrimFlags.JointLP2P));

            _so1.ScriptSetVolumeDetect(false);

            Assert.That(rootPart.Flags, Is.EqualTo(PrimFlags.None));
        }

        [Test]
        public void TestSetPhysicsSinglePrim()
        {
            TestHelpers.InMethod();

            _scene.AddSceneObject(_so1);

            SceneObjectPart rootPart = _so1.RootPart;
            Assert.That(rootPart.Flags, Is.EqualTo(PrimFlags.None));

            _so1.ScriptSetPhysicsStatus(true);

            Assert.That(rootPart.Flags, Is.EqualTo(PrimFlags.Physics));

            _so1.ScriptSetPhysicsStatus(false);

            Assert.That(rootPart.Flags, Is.EqualTo(PrimFlags.None));
        }

        [Test]
        public void TestSetPhysicsVolumeDetectSinglePrim()
        {
            TestHelpers.InMethod();

            _scene.AddSceneObject(_so1);

            SceneObjectPart rootPart = _so1.RootPart;
            Assert.That(rootPart.Flags, Is.EqualTo(PrimFlags.None));

            _so1.ScriptSetPhysicsStatus(true);
            _so1.ScriptSetVolumeDetect(true);

            // PrimFlags.JointLP2P is incorrect it now means VolumeDetect (as defined by viewers)
            Assert.That(rootPart.Flags, Is.EqualTo(PrimFlags.Phantom | PrimFlags.Physics | PrimFlags.JointLP2P));

            _so1.ScriptSetVolumeDetect(false);

            Assert.That(rootPart.Flags, Is.EqualTo(PrimFlags.Physics));
        }

        [Test]
        public void TestSetPhysicsLinkset()
        {
            TestHelpers.InMethod();

            _scene.AddSceneObject(_so1);
            _scene.AddSceneObject(_so2);

            _scene.LinkObjects(_ownerId, _so1.LocalId, new List<uint>() { _so2.LocalId });

            _so1.ScriptSetPhysicsStatus(true);

            Assert.That(_so1.RootPart.Flags, Is.EqualTo(PrimFlags.Physics));
            Assert.That(_so1.Parts[1].Flags, Is.EqualTo(PrimFlags.Physics));

            _so1.ScriptSetPhysicsStatus(false);

            Assert.That(_so1.RootPart.Flags, Is.EqualTo(PrimFlags.None));
            Assert.That(_so1.Parts[1].Flags, Is.EqualTo(PrimFlags.None));

            _so1.ScriptSetPhysicsStatus(true);

            Assert.That(_so1.RootPart.Flags, Is.EqualTo(PrimFlags.Physics));
            Assert.That(_so1.Parts[1].Flags, Is.EqualTo(PrimFlags.Physics));
        }

        /// <summary>
        /// Test that linking results in the correct physical status for all linkees.
        /// </summary>
        [Test]
        public void TestLinkPhysicsBothPhysical()
        {
            TestHelpers.InMethod();

            _scene.AddSceneObject(_so1);
            _scene.AddSceneObject(_so2);

            _so1.ScriptSetPhysicsStatus(true);
            _so2.ScriptSetPhysicsStatus(true);

            _scene.LinkObjects(_ownerId, _so1.LocalId, new List<uint>() { _so2.LocalId });

            Assert.That(_so1.RootPart.Flags, Is.EqualTo(PrimFlags.Physics));
            Assert.That(_so1.Parts[1].Flags, Is.EqualTo(PrimFlags.Physics));
        }

        /// <summary>
        /// Test that linking results in the correct physical status for all linkees.
        /// </summary>
        [Test]
        public void TestLinkPhysicsRootPhysicalOnly()
        {
            TestHelpers.InMethod();

            _scene.AddSceneObject(_so1);
            _scene.AddSceneObject(_so2);

            _so1.ScriptSetPhysicsStatus(true);

            _scene.LinkObjects(_ownerId, _so1.LocalId, new List<uint>() { _so2.LocalId });

            Assert.That(_so1.RootPart.Flags, Is.EqualTo(PrimFlags.Physics));
            Assert.That(_so1.Parts[1].Flags, Is.EqualTo(PrimFlags.Physics));
        }

        /// <summary>
        /// Test that linking results in the correct physical status for all linkees.
        /// </summary>
        [Test]
        public void TestLinkPhysicsChildPhysicalOnly()
        {
            TestHelpers.InMethod();

            _scene.AddSceneObject(_so1);
            _scene.AddSceneObject(_so2);

            _so2.ScriptSetPhysicsStatus(true);

            _scene.LinkObjects(_ownerId, _so1.LocalId, new List<uint>() { _so2.LocalId });

            Assert.That(_so1.RootPart.Flags, Is.EqualTo(PrimFlags.None));
            Assert.That(_so1.Parts[1].Flags, Is.EqualTo(PrimFlags.None));
        }
    }
}