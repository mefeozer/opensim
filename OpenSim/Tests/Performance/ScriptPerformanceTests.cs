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
using System.Threading;
using Nini.Config;
using NUnit.Framework;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.CoreModules.Scripting.WorldComm;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.ScriptEngine.XEngine;
using OpenSim.Tests.Common;

namespace OpenSim.Tests.Performance
{
    /// <summary>
    /// Script performance tests
    /// </summary>
    /// <remarks>
    /// Don't rely on the numbers given by these tests - they will vary a lot depending on what is already cached,
    /// how much memory is free, etc.  In some cases, later larger tests will apparently take less time than smaller
    /// earlier tests.
    /// </remarks>
    [TestFixture]
    public class ScriptPerformanceTests : OpenSimTestCase
    {
        private TestScene _scene;
        private XEngine _xEngine;
        private readonly AutoResetEvent _chatEvent = new AutoResetEvent(false);

        private int _expectedChatMessages;
        private readonly List<OSChatMessage> _osChatMessagesReceived = new List<OSChatMessage>();

        [SetUp]
        public void Init()
        {
            //AppDomain.CurrentDomain.SetData("APPBASE", Environment.CurrentDirectory + "/bin");
//            Console.WriteLine(AppDomain.CurrentDomain.BaseDirectory);
            _xEngine = new XEngine();

            // Necessary to stop serialization complaining
            WorldCommModule wcModule = new WorldCommModule();

            IniConfigSource configSource = new IniConfigSource();

            IConfig startupConfig = configSource.AddConfig("Startup");
            startupConfig.Set("DefaultScriptEngine", "XEngine");

            IConfig xEngineConfig = configSource.AddConfig("XEngine");
            xEngineConfig.Set("Enabled", "true");

            // These tests will not run with AppDomainLoading = true, at least on mono.  For unknown reasons, the call
            // to AssemblyResolver.OnAssemblyResolve fails.
            xEngineConfig.Set("AppDomainLoading", "false");

            _scene = new SceneHelpers().SetupScene("My Test", UUID.Random(), 1000, 1000, configSource);
            SceneHelpers.SetupSceneModules(_scene, configSource, _xEngine, wcModule);

            _scene.EventManager.OnChatFromWorld += OnChatFromWorld;
            _scene.StartScripts();
        }

        [TearDown]
        public void TearDown()
        {
            _scene.Close();
            _scene = null;
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        [Test]
        public void TestCompileAndStart100Scripts()
        {
            TestHelpers.InMethod();
            log4net.Config.XmlConfigurator.Configure();

            TestCompileAndStartScripts(100);
        }

        private void TestCompileAndStartScripts(int scriptsToCreate)
        {
            UUID userId = TestHelpers.ParseTail(0x1);

            _expectedChatMessages = scriptsToCreate;
            int startingObjectIdTail = 0x100;

            GC.Collect();

            for (int idTail = startingObjectIdTail;idTail < startingObjectIdTail + scriptsToCreate; idTail++)
            {
                AddObjectAndScript(idTail, userId);
            }

            _chatEvent.WaitOne(40000 + scriptsToCreate * 1000);

            Assert.That(_osChatMessagesReceived.Count, Is.EqualTo(_expectedChatMessages));

            foreach (OSChatMessage msg in _osChatMessagesReceived)
                Assert.That(
                    msg.Message,
                    Is.EqualTo("Script running"),
                    string.Format(
                        "Message from {0} was {1} rather than {2}", msg.SenderUUID, msg.Message, "Script running"));
        }

        private void AddObjectAndScript(int objectIdTail, UUID userId)
        {
//            UUID itemId = TestHelpers.ParseTail(0x3);
            string itemName = string.Format("AddObjectAndScript() Item for object {0}", objectIdTail);

            SceneObjectGroup so = SceneHelpers.CreateSceneObject(1, userId, "AddObjectAndScriptPart_", objectIdTail);
            _scene.AddNewSceneObject(so, true);

            InventoryItemBase itemTemplate = new InventoryItemBase
            {
                //            itemTemplate.ID = itemId;
                Name = itemName,
                Folder = so.UUID,
                InvType = (int)InventoryType.LSL
            };

            _scene.RezNewScript(userId, itemTemplate);
        }

        private void OnChatFromWorld(object sender, OSChatMessage oscm)
        {
//            Console.WriteLine("Got chat [{0}]", oscm.Message);

            lock (_osChatMessagesReceived)
            {
                _osChatMessagesReceived.Add(oscm);

                if (_osChatMessagesReceived.Count == _expectedChatMessages)
                    _chatEvent.Set();
            }
        }
    }
}