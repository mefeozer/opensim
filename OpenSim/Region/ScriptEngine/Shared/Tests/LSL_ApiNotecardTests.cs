using System.Collections.Generic;
using Nini.Config;
using NUnit.Framework;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.ScriptEngine.Shared.Api;
using OpenSim.Region.ScriptEngine.Shared.ScriptBase;
using OpenSim.Tests.Common;

namespace OpenSim.Region.ScriptEngine.Shared.Tests
{
    /// <summary>
    /// Tests for notecard related functions in LSL
    /// </summary>
    [TestFixture]
    public class LSL_ApiNotecardTests : OpenSimTestCase
    {
        private Scene _scene;
        private MockScriptEngine _engine;

        private SceneObjectGroup _so;
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

            _engine = new MockScriptEngine();

            _scene = new SceneHelpers().SetupScene();
            SceneHelpers.SetupSceneModules(_scene, new IniConfigSource(), _engine);

            _so = SceneHelpers.AddSceneObject(_scene);
            _scriptItem = TaskInventoryHelpers.AddScript(_scene.AssetService, _so.RootPart);

            // This is disconnected from the actual script - the mock engine does not set up any LSL_Api atm.
            // Possibly this could be done and we could obtain it directly from the MockScriptEngine.
            _lslApi = new LSL_Api();
            _lslApi.Initialize(_engine, _so.RootPart, _scriptItem);
        }

        [Test]
        public void TestLlGetNotecardLine()
        {
            TestHelpers.InMethod();

            string[] ncLines = { "One", "Twoè", "Three" };

            TaskInventoryItem ncItem
                = TaskInventoryHelpers.AddNotecard(_scene.AssetService, _so.RootPart, "nc", "1", "10", string.Join("\n", ncLines));

            AssertValidNotecardLine(ncItem.Name, 0, ncLines[0]);
            AssertValidNotecardLine(ncItem.Name, 2, ncLines[2]);
            AssertValidNotecardLine(ncItem.Name, 3, ScriptBaseClass.EOF);
            AssertValidNotecardLine(ncItem.Name, 4, ScriptBaseClass.EOF);

            // XXX: Is this correct or do we really expect no dataserver event to fire at all?
            AssertValidNotecardLine(ncItem.Name, -1, "");
            AssertValidNotecardLine(ncItem.Name, -2, "");
        }

        [Test]
        public void TestLlGetNotecardLine_NoNotecard()
        {
            TestHelpers.InMethod();

            AssertInValidNotecardLine("nc", 0);
        }

        [Test]
        public void TestLlGetNotecardLine_NotANotecard()
        {
            TestHelpers.InMethod();

            TaskInventoryItem ncItem = TaskInventoryHelpers.AddScript(_scene.AssetService, _so.RootPart, "nc1", "Not important");

            AssertInValidNotecardLine(ncItem.Name, 0);
        }

        private void AssertValidNotecardLine(string ncName, int lineNumber, string assertLine)
        {
            string key = _lslApi.llGetNotecardLine(ncName, lineNumber);
            Assert.That(key, Is.Not.EqualTo(UUID.Zero.ToString()));

            Assert.That(_engine.PostedEvents.Count, Is.EqualTo(1));
            Assert.That(_engine.PostedEvents.ContainsKey(_scriptItem.ItemID));

            List<EventParams> events = _engine.PostedEvents[_scriptItem.ItemID];
            Assert.That(events.Count, Is.EqualTo(1));
            EventParams eventParams = events[0];

            Assert.That(eventParams.EventName, Is.EqualTo("dataserver"));
            Assert.That(eventParams.Params[0].ToString(), Is.EqualTo(key));
            Assert.That(eventParams.Params[1].ToString(), Is.EqualTo(assertLine));

            _engine.ClearPostedEvents();
        }

        private void AssertInValidNotecardLine(string ncName, int lineNumber)
        {
            string key = _lslApi.llGetNotecardLine(ncName, lineNumber);
            Assert.That(key, Is.EqualTo(UUID.Zero.ToString()));

            Assert.That(_engine.PostedEvents.Count, Is.EqualTo(0));
        }

//        [Test]
//        public void TestLlReleaseUrl()
//        {
//            TestHelpers.InMethod();
//
//            _lslApi.llRequestURL();
//            string returnedUri = _engine.PostedEvents[_scriptItem.ItemID][0].Params[2].ToString();
//
//            {
//                // Check that the initial number of URLs is correct
//                Assert.That(_lslApi.llGetFreeURLs().value, Is.EqualTo(_urlModule.TotalUrls - 1));
//            }
//
//            {
//                // Check releasing a non-url
//                _lslApi.llReleaseURL("GARBAGE");
//                Assert.That(_lslApi.llGetFreeURLs().value, Is.EqualTo(_urlModule.TotalUrls - 1));
//            }
//
//            {
//                // Check releasing a non-existing url
//                _lslApi.llReleaseURL("http://example.com");
//                Assert.That(_lslApi.llGetFreeURLs().value, Is.EqualTo(_urlModule.TotalUrls - 1));
//            }
//
//            {
//                // Check URL release
//                _lslApi.llReleaseURL(returnedUri);
//                Assert.That(_lslApi.llGetFreeURLs().value, Is.EqualTo(_urlModule.TotalUrls));
//
//                HttpWebRequest webRequest = (HttpWebRequest)WebRequest.Create(returnedUri);
//
//                bool gotExpectedException = false;
//
//                try
//                {
//                    using (HttpWebResponse webResponse = (HttpWebResponse)webRequest.GetResponse())
//                    {}
//                }
//                catch (WebException e)
//                {
//                    using (HttpWebResponse response = (HttpWebResponse)e.Response)
//                        gotExpectedException = response.StatusCode == HttpStatusCode.NotFound;
//                }
//
//                Assert.That(gotExpectedException, Is.True);
//            }
//
//            {
//                // Check releasing the same URL again
//                _lslApi.llReleaseURL(returnedUri);
//                Assert.That(_lslApi.llGetFreeURLs().value, Is.EqualTo(_urlModule.TotalUrls));
//            }
//        }
//
//        [Test]
//        public void TestLlRequestUrl()
//        {
//            TestHelpers.InMethod();
//
//            string requestId = _lslApi.llRequestURL();
//            Assert.That(requestId, Is.Not.EqualTo(UUID.Zero.ToString()));
//            string returnedUri;
//
//            {
//                // Check that URL is correctly set up
//                Assert.That(_lslApi.llGetFreeURLs().value, Is.EqualTo(_urlModule.TotalUrls - 1));
//
//                Assert.That(_engine.PostedEvents.ContainsKey(_scriptItem.ItemID));
//
//                List<EventParams> events = _engine.PostedEvents[_scriptItem.ItemID];
//                Assert.That(events.Count, Is.EqualTo(1));
//                EventParams eventParams = events[0];
//                Assert.That(eventParams.EventName, Is.EqualTo("http_request"));
//
//                UUID returnKey;
//                string rawReturnKey = eventParams.Params[0].ToString();
//                string method = eventParams.Params[1].ToString();
//                returnedUri = eventParams.Params[2].ToString();
//
//                Assert.That(UUID.TryParse(rawReturnKey, out returnKey), Is.True);
//                Assert.That(method, Is.EqualTo(ScriptBaseClass.URL_REQUEST_GRANTED));
//                Assert.That(Uri.IsWellFormedUriString(returnedUri, UriKind.Absolute), Is.True);
//            }
//
//            {
//                // Check that request to URL works.
//                string testResponse = "Hello World";
//
//                _engine.ClearPostedEvents();
//                _engine.PostEventHook
//                    += (itemId, evp) => _lslApi.llHTTPResponse(evp.Params[0].ToString(), 200, testResponse);
//
////                Console.WriteLine("Trying {0}", returnedUri);
//                HttpWebRequest webRequest = (HttpWebRequest)WebRequest.Create(returnedUri);
//
//                AssertHttpResponse(returnedUri, testResponse);
//
//                Assert.That(_engine.PostedEvents.ContainsKey(_scriptItem.ItemID));
//
//                List<EventParams> events = _engine.PostedEvents[_scriptItem.ItemID];
//                Assert.That(events.Count, Is.EqualTo(1));
//                EventParams eventParams = events[0];
//                Assert.That(eventParams.EventName, Is.EqualTo("http_request"));
//
//                UUID returnKey;
//                string rawReturnKey = eventParams.Params[0].ToString();
//                string method = eventParams.Params[1].ToString();
//                string body = eventParams.Params[2].ToString();
//
//                Assert.That(UUID.TryParse(rawReturnKey, out returnKey), Is.True);
//                Assert.That(method, Is.EqualTo("GET"));
//                Assert.That(body, Is.EqualTo(""));
//            }
//        }
//
//        private void AssertHttpResponse(string uri, string expectedResponse)
//        {
//            HttpWebRequest webRequest = (HttpWebRequest)WebRequest.Create(uri);
//
//            using (HttpWebResponse webResponse = (HttpWebResponse)webRequest.GetResponse())
//            {
//                using (Stream stream = webResponse.GetResponseStream())
//                {
//                    using (StreamReader reader = new StreamReader(stream))
//                    {
//                        Assert.That(reader.ReadToEnd(), Is.EqualTo(expectedResponse));
//                    }
//                }
//            }
//        }
    }
}
