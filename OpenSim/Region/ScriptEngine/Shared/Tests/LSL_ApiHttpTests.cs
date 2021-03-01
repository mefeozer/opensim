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
using System.IO;
using System.Net;
using Nini.Config;
using NUnit.Framework;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Servers;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.CoreModules.Scripting.LSLHttp;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.ScriptEngine.Shared.Api;
using OpenSim.Region.ScriptEngine.Shared.ScriptBase;
using OpenSim.Tests.Common;

namespace OpenSim.Region.ScriptEngine.Shared.Tests
{
    /// <summary>
    /// Tests for HTTP related functions in LSL
    /// </summary>
    [TestFixture]
    public class LSL_ApiHttpTests : OpenSimTestCase
    {
        private Scene _scene;
        private MockScriptEngine _engine;
        private UrlModule _urlModule;

        private TaskInventoryItem _scriptItem;
        private LSL_Api _lslApi;

        [TestFixtureSetUp]
        public void TestFixtureSetUp()
        {
            // Don't allow tests to be bamboozled by asynchronous events.  Execute everything on the same thread.
            Util.FireAndForgetMethod = FireAndForgetMethod.RegressionTest;
        }

        [TestFixtureTearDown]
        public void TestFixureTearDown()
        {
            // We must set this back afterwards, otherwise later tests will fail since they're expecting multiple
            // threads.  Possibly, later tests should be rewritten so none of them require async stuff (which regression
            // tests really shouldn't).
            Util.FireAndForgetMethod = Util.DefaultFireAndForgetMethod;
        }

        [SetUp]
        public override void SetUp()
        {
            base.SetUp();

            // This is an unfortunate bit of clean up we have to do because MainServer manages things through static
            // variables and the VM is not restarted between tests.
            uint port = 9999;
            MainServer.RemoveHttpServer(port);

            _engine = new MockScriptEngine();
            _urlModule = new UrlModule();

            IConfigSource config = new IniConfigSource();
            config.AddConfig("Network");
            config.Configs["Network"].Set("ExternalHostNameForLSL", "127.0.0.1");
            _scene = new SceneHelpers().SetupScene();

            BaseHttpServer server = new BaseHttpServer(port);
            MainServer.AddHttpServer(server);
            MainServer.Instance = server;

            server.Start();

            SceneHelpers.SetupSceneModules(_scene, config, _engine, _urlModule);

            SceneObjectGroup so = SceneHelpers.AddSceneObject(_scene);
            _scriptItem = TaskInventoryHelpers.AddScript(_scene.AssetService, so.RootPart);

            // This is disconnected from the actual script - the mock engine does not set up any LSL_Api atm.
            // Possibly this could be done and we could obtain it directly from the MockScriptEngine.
            _lslApi = new LSL_Api();
            _lslApi.Initialize(_engine, so.RootPart, _scriptItem);
        }

        [TearDown]
        public void TearDown()
        {
            MainServer.Instance.Stop();
        }

        [Test]
        public void TestLlReleaseUrl()
        {
            TestHelpers.InMethod();

            _lslApi.llRequestURL();
            string returnedUri = _engine.PostedEvents[_scriptItem.ItemID][0].Params[2].ToString();

            {
                // Check that the initial number of URLs is correct
                Assert.That(_lslApi.llGetFreeURLs().value, Is.EqualTo(_urlModule.TotalUrls - 1));
            }

            {
                // Check releasing a non-url
                _lslApi.llReleaseURL("GARBAGE");
                Assert.That(_lslApi.llGetFreeURLs().value, Is.EqualTo(_urlModule.TotalUrls - 1));
            }

            {
                // Check releasing a non-existing url
                _lslApi.llReleaseURL("http://example.com");
                Assert.That(_lslApi.llGetFreeURLs().value, Is.EqualTo(_urlModule.TotalUrls - 1));
            }

            {
                // Check URL release
                _lslApi.llReleaseURL(returnedUri);
                Assert.That(_lslApi.llGetFreeURLs().value, Is.EqualTo(_urlModule.TotalUrls));

                HttpWebRequest webRequest = (HttpWebRequest)WebRequest.Create(returnedUri);

                bool gotExpectedException = false;

                try
                {
                    using (HttpWebResponse webResponse = (HttpWebResponse)webRequest.GetResponse())
                    {}
                }
                catch (WebException)
                {
//                    using (HttpWebResponse response = (HttpWebResponse)e.Response)
//                        gotExpectedException = response.StatusCode == HttpStatusCode.NotFound;
                    gotExpectedException = true;
                }

                Assert.That(gotExpectedException, Is.True);
            }

            {
                // Check releasing the same URL again
                _lslApi.llReleaseURL(returnedUri);
                Assert.That(_lslApi.llGetFreeURLs().value, Is.EqualTo(_urlModule.TotalUrls));
            }
        }

        [Test]
        public void TestLlRequestUrl()
        {
            TestHelpers.InMethod();

            string requestId = _lslApi.llRequestURL();
            Assert.That(requestId, Is.Not.EqualTo(UUID.Zero.ToString()));
            string returnedUri;

            {
                // Check that URL is correctly set up
                Assert.That(_lslApi.llGetFreeURLs().value, Is.EqualTo(_urlModule.TotalUrls - 1));

                Assert.That(_engine.PostedEvents.ContainsKey(_scriptItem.ItemID));

                List<EventParams> events = _engine.PostedEvents[_scriptItem.ItemID];
                Assert.That(events.Count, Is.EqualTo(1));
                EventParams eventParams = events[0];
                Assert.That(eventParams.EventName, Is.EqualTo("http_request"));

                UUID returnKey;
                string rawReturnKey = eventParams.Params[0].ToString();
                string method = eventParams.Params[1].ToString();
                returnedUri = eventParams.Params[2].ToString();

                Assert.That(UUID.TryParse(rawReturnKey, out returnKey), Is.True);
                Assert.That(method, Is.EqualTo(ScriptBaseClass.URL_REQUEST_GRANTED));
                Assert.That(Uri.IsWellFormedUriString(returnedUri, UriKind.Absolute), Is.True);
            }

            {
                // Check that request to URL works.
                string testResponse = "Hello World";

                _engine.ClearPostedEvents();
                _engine.PostEventHook
                    += (itemId, evp) => _lslApi.llHTTPResponse(evp.Params[0].ToString(), 200, testResponse);

//                Console.WriteLine("Trying {0}", returnedUri);

                AssertHttpResponse(returnedUri, testResponse);

                Assert.That(_engine.PostedEvents.ContainsKey(_scriptItem.ItemID));

                List<EventParams> events = _engine.PostedEvents[_scriptItem.ItemID];
                Assert.That(events.Count, Is.EqualTo(1));
                EventParams eventParams = events[0];
                Assert.That(eventParams.EventName, Is.EqualTo("http_request"));

                UUID returnKey;
                string rawReturnKey = eventParams.Params[0].ToString();
                string method = eventParams.Params[1].ToString();
                string body = eventParams.Params[2].ToString();

                Assert.That(UUID.TryParse(rawReturnKey, out returnKey), Is.True);
                Assert.That(method, Is.EqualTo("GET"));
                Assert.That(body, Is.EqualTo(""));
            }
        }

        private void AssertHttpResponse(string uri, string expectedResponse)
        {
            HttpWebRequest webRequest = (HttpWebRequest)WebRequest.Create(uri);

            using (HttpWebResponse webResponse = (HttpWebResponse)webRequest.GetResponse())
            {
                using (Stream stream = webResponse.GetResponseStream())
                {
                    using (StreamReader reader = new StreamReader(stream))
                    {
                        Assert.That(reader.ReadToEnd(), Is.EqualTo(expectedResponse));
                    }
                }
            }
        }
    }
}
