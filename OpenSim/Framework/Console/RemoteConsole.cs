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
using System.Xml;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Timers;
using OpenMetaverse;
using Nini.Config;
using OpenSim.Framework.Servers.HttpServer;
using log4net;

namespace OpenSim.Framework.Console
{
    // A console that uses REST interfaces
    //
    public class RemoteConsole : CommandConsole
    {
        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        // Connection specific data, indexed by a session ID
        // we create when a client connects.
        protected class ConsoleConnection
        {
            // Last activity from the client
            public int last;

            // Last line of scrollback posted to this client
            public long lastLineSeen;

            // True if this is a new connection, e.g. has never
            // displayed a prompt to the user.
            public bool newConnection = true;
        }

        // A line in the scrollback buffer.
        protected class ScrollbackEntry
        {
            // The line number of this entry
            public long lineNumber;

            // The text to send to the client
            public string text;

            // The level this should be logged as. Omitted for
            // prompts and input echo.
            public string level;

            // True if the text above is a prompt, e.g. the
            // client should turn on the cursor / accept input
            public bool isPrompt;

            // True if the requested input is a command. A
            // client may offer help or validate input if
            // this is set. If false, input should be sent
            // as typed.
            public bool isCommand;

            // True if this text represents a line of text that
            // was input in response to a prompt. A client should
            // turn off the cursor and refrain from sending commands
            // until a new prompt is received.
            public bool isInput;
        }

        // Data that is relevant to all connections

        // The scrollback buffer
        protected List<ScrollbackEntry> _Scrollback = new List<ScrollbackEntry>();

        // Monotonously incrementing line number. This may eventually
        // wrap. No provision is made for that case because 64 bits
        // is a long, long time.
        protected long _lineNumber = 0;

        // These two variables allow us to send the correct
        // information about the prompt status to the client,
        // irrespective of what may have run off the top of the
        // scrollback buffer;
        protected bool _expectingInput = false;
        protected bool _expectingCommand = true;
        protected string _lastPromptUsed;

        // This is the list of things received from clients.
        // Note: Race conditions can happen. If a client sends
        // something while nothing is expected, it will be
        // intepreted as input to the next prompt. For
        // commands this is largely correct. For other prompts,
        // YMMV.
        // TODO: Find a better way to fix this
        protected List<string> _InputData = new List<string>();

        // Event to allow ReadLine to wait synchronously even though
        // everthing else is asynchronous here.
        protected ManualResetEvent _DataEvent = new ManualResetEvent(false);

        // The list of sessions we maintain. Unlike other console types,
        // multiple users on the same console are explicitly allowed.
        protected Dictionary<UUID, ConsoleConnection> _Connections =
                new Dictionary<UUID, ConsoleConnection>();

        // Timer to control expiration of sessions that have been
        // disconnected.
        protected System.Timers.Timer _expireTimer = new System.Timers.Timer(5000);

        // The less interesting stuff that makes the actual server
        // work.
        protected IHttpServer _Server = null;
        protected IConfigSource _Config = null;

        protected string _UserName = string.Empty;
        protected string _Password = string.Empty;
        protected string _AllowedOrigin = string.Empty;


        public RemoteConsole(string defaultPrompt) : base(defaultPrompt)
        {
            // There is something wrong with this architecture.
            // A prompt is sent on every single input, so why have this?
            // TODO: Investigate and fix.
            _lastPromptUsed = defaultPrompt;

            // Start expiration of sesssions.
            _expireTimer.Elapsed += DoExpire;
            _expireTimer.Start();
        }

        public override void ReadConfig(IConfigSource config)
        {
            _Config = config;

            // We're pulling this from the 'Network' section for legacy
            // compatibility. However, this is so essentially insecure
            // that TLS and client certs should be used instead of
            // a username / password.
            IConfig netConfig = _Config.Configs["Network"];

            if (netConfig == null)
                return;

            // Get the username and password.
            _UserName = netConfig.GetString("ConsoleUser", string.Empty);
            _Password = netConfig.GetString("ConsolePass", string.Empty);

            // Woefully underdocumented, this is what makes javascript
            // console clients work. Set to "*" for anywhere or (better)
            // to specific addresses.
            _AllowedOrigin = netConfig.GetString("ConsoleAllowedOrigin", string.Empty);
        }

        public void SetServer(IHttpServer server)
        {
            // This is called by the framework to give us the server
            // instance (means: port) to work with.
            _Server = server;

            // Add our handlers
            _Server.AddHTTPHandler("/StartSession/", HandleHttpStartSession);
            _Server.AddHTTPHandler("/CloseSession/", HandleHttpCloseSession);
            _Server.AddHTTPHandler("/SessionCommand/", HandleHttpSessionCommand);
        }

        public override void Output(string format)
        {
            Output(format, null);
        }

        public override void Output(string format, params object[] components)
        {
            string level = null;
            if (components != null && components.Length > 0)
            {
                if (components[0] == null || components[0] is ConsoleLevel)
                {
                    if (components[0] is ConsoleLevel)
                        level = ((ConsoleLevel)components[0]).ToString();

                    if (components.Length > 1)
                    {
                        object[] tmp = new object[components.Length - 1];
                        Array.Copy(components, 1, tmp, 0, components.Length - 1);
                        components = tmp;
                    }
                    else
                        components = null;
                }
            }

            string text;
            if (components == null || components.Length == 0)
                text = format;
            else
                text = string.Format(format, components);

            Output(text, level, false, false, false);
        }

        protected void Output(string text, string level, bool isPrompt, bool isCommand, bool isInput)
        {
            if (level == null)
                level = string.Empty;

            // Increment the line number. It was 0 and they start at 1
            // so we need to pre-increment.
            _lineNumber++;

            // Create and populate the new entry.
            ScrollbackEntry newEntry = new ScrollbackEntry
            {
                lineNumber = _lineNumber,
                text = text,
                level = level,
                isPrompt = isPrompt,
                isCommand = isCommand,
                isInput = isInput
            };

            // Add a line to the scrollback. In some cases, that may not
            // actually be a line of text.
            lock (_Scrollback)
            {
                // Prune the scrollback to the length se send as connect
                // burst to give the user some context.
                while (_Scrollback.Count >= 1000)
                    _Scrollback.RemoveAt(0);

                _Scrollback.Add(newEntry);
            }

            // Let the rest of the system know we have output something.
            FireOnOutput(text.Trim());

            // Also display it for debugging.
            System.Console.WriteLine(text.Trim());
        }

        public override string ReadLine(string p, bool isCommand, bool e)
        {
            // Output the prompt an prepare to wait. This
            // is called on a dedicated console thread and
            // needs to be synchronous. Old architecture but
            // not worth upgrading.
            if (isCommand)
            {
                _expectingInput = true;
                _expectingCommand = true;
                Output(p, string.Empty, true, true, false);
                _lastPromptUsed = p;
            }
            else
            {
                _expectingInput = true;
                Output(p, string.Empty, true, false, false);
            }


            // Here is where we wait for the user to input something.
            _DataEvent.WaitOne();

            string cmdinput;

            // Check for empty input. Read input if not empty.
            lock (_InputData)
            {
                if (_InputData.Count == 0)
                {
                    _DataEvent.Reset();
                    _expectingInput = false;
                    _expectingCommand = false;

                    return "";
                }

                cmdinput = _InputData[0];
                _InputData.RemoveAt(0);
                if (_InputData.Count == 0)
                    _DataEvent.Reset();

            }

            _expectingInput = false;
            _expectingCommand = false;

            // Echo to all the other users what we have done. This
            // will also go to ourselves.
            Output (cmdinput, string.Empty, false, false, true);

            // If this is a command, we need to resolve and execute it.
            if (isCommand)
            {
                // This call will actually execute the command and create
                // any output associated with it. The core just gets an
                // empty string so it will call again immediately.
                string[] cmd = Commands.Resolve(Parser.Parse(cmdinput));

                if (cmd.Length != 0)
                {
                    int i;

                    for (i=0 ; i < cmd.Length ; i++)
                    {
                        if (cmd[i].Contains(" "))
                            cmd[i] = "\"" + cmd[i] + "\"";
                    }
                    return string.Empty;
                }
            }

            // Return the raw input string if not a command.
            return cmdinput;
        }

        // Very simplistic static access control header.
        protected Hashtable CheckOrigin(Hashtable result)
        {
            if (!string.IsNullOrEmpty(_AllowedOrigin))
                result["access_control_allow_origin"] = _AllowedOrigin;

            return result;
        }

        /* TODO: Figure out how PollServiceHTTPHandler can access the request headers
         * in order to use _AllowedOrigin as a regular expression
        protected Hashtable CheckOrigin(Hashtable headers, Hashtable result)
        {
            if (!string.IsNullOrEmpty(_AllowedOrigin))
            {
                if (headers.ContainsKey("origin"))
                {
                    string origin = headers["origin"].ToString();
                    if (Regex.IsMatch(origin, _AllowedOrigin))
                        result["access_control_allow_origin"] = origin;
                }
            }
            return result;
        }
        */

        protected void DoExpire(object sender, ElapsedEventArgs e)
        {
            // Iterate the list of console connections and find those we
            // haven't heard from for longer then the longpoll interval.
            // Remove them.
            List<UUID> expired = new List<UUID>();

            lock (_Connections)
            {
                // Mark the expired ones
                foreach (KeyValuePair<UUID, ConsoleConnection> kvp in _Connections)
                {
                    if (System.Environment.TickCount - kvp.Value.last > 500000)
                        expired.Add(kvp.Key);
                }

                // Delete them
                foreach (UUID id in expired)
                {
                    _Connections.Remove(id);
                    CloseConnection(id);
                }
            }
        }

        // Start a new session.
        protected Hashtable HandleHttpStartSession(Hashtable request)
        {
            // The login is in the form of a http form post
            Hashtable post = DecodePostString(request["body"].ToString());
            Hashtable reply = new Hashtable();

            reply["str_response_string"] = "";
            reply["int_response_code"] = 401;
            reply["content_type"] = "text/plain";

            // Check user name and password
            if (string.IsNullOrEmpty(_UserName))
                return reply;

            if (post["USER"] == null || post["PASS"] == null)
                return reply;

            if (_UserName != post["USER"].ToString() ||
                _Password != post["PASS"].ToString())
            {
                return reply;
            }

            // Set up the new console connection record
            ConsoleConnection c = new ConsoleConnection
            {
                last = System.Environment.TickCount,
                lastLineSeen = 0
            };

            // Assign session ID
            UUID sessionID = UUID.Random();

            // Add connection to list.
            lock (_Connections)
            {
                _Connections[sessionID] = c;
            }

            // This call is a CAP. The URL is the authentication.
            string uri = "/ReadResponses/" + sessionID.ToString();

            _Server.AddPollServiceHTTPHandler(new PollServiceEventArgs(null, uri, HasEvents, GetEvents, NoEvents, null, sessionID,25000)); // 25 secs timeout

            // Our reply is an XML document.
            // TODO: Change this to Linq.Xml
            XmlDocument xmldoc = new XmlDocument();
            XmlNode xmlnode = xmldoc.CreateNode(XmlNodeType.XmlDeclaration,
                    "", "");

            xmldoc.AppendChild(xmlnode);
            XmlElement rootElement = xmldoc.CreateElement("", "ConsoleSession",
                    "");

            xmldoc.AppendChild(rootElement);

            XmlElement id = xmldoc.CreateElement("", "SessionID", "");
            id.AppendChild(xmldoc.CreateTextNode(sessionID.ToString()));

            rootElement.AppendChild(id);

            XmlElement prompt = xmldoc.CreateElement("", "Prompt", "");
            prompt.AppendChild(xmldoc.CreateTextNode(_lastPromptUsed));

            rootElement.AppendChild(prompt);

            rootElement.AppendChild(MainConsole.Instance.Commands.GetXml(xmldoc));

            // Set up the response and check origin
            reply["str_response_string"] = xmldoc.InnerXml;
            reply["int_response_code"] = 200;
            reply["content_type"] = "text/xml";
            reply = CheckOrigin(reply);

            return reply;
        }

        // Client closes session. Clean up.
        protected Hashtable HandleHttpCloseSession(Hashtable request)
        {
            Hashtable post = DecodePostString(request["body"].ToString());
            Hashtable reply = new Hashtable();

            reply["str_response_string"] = "";
            reply["int_response_code"] = 404;
            reply["content_type"] = "text/plain";

            if (post["ID"] == null)
                return reply;

            UUID id;
            if (!UUID.TryParse(post["ID"].ToString(), out id))
                return reply;

            lock (_Connections)
            {
                if (_Connections.ContainsKey(id))
                {
                    _Connections.Remove(id);
                    CloseConnection(id);
                }
            }

            XmlDocument xmldoc = new XmlDocument();
            XmlNode xmlnode = xmldoc.CreateNode(XmlNodeType.XmlDeclaration,
                    "", "");

            xmldoc.AppendChild(xmlnode);
            XmlElement rootElement = xmldoc.CreateElement("", "ConsoleSession",
                    "");

            xmldoc.AppendChild(rootElement);

            XmlElement res = xmldoc.CreateElement("", "Result", "");
            res.AppendChild(xmldoc.CreateTextNode("OK"));

            rootElement.AppendChild(res);

            reply["str_response_string"] = xmldoc.InnerXml;
            reply["int_response_code"] = 200;
            reply["content_type"] = "text/xml";
            reply = CheckOrigin(reply);

            return reply;
        }

        // Command received from the client.
        protected Hashtable HandleHttpSessionCommand(Hashtable request)
        {
            Hashtable post = DecodePostString(request["body"].ToString());
            Hashtable reply = new Hashtable();

            reply["str_response_string"] = "";
            reply["int_response_code"] = 404;
            reply["content_type"] = "text/plain";

            // Check the ID
            if (post["ID"] == null)
                return reply;

            UUID id;
            if (!UUID.TryParse(post["ID"].ToString(), out id))
                return reply;

            // Find the connection for that ID.
            lock (_Connections)
            {
                if (!_Connections.ContainsKey(id))
                    return reply;
            }

            // Empty post. Just error out.
            if (post["COMMAND"] == null)
                return reply;

            // Place the input data in the buffer.
            lock (_InputData)
            {
                _DataEvent.Set();
                _InputData.Add(post["COMMAND"].ToString());
            }

            // Create the XML reply document.
            XmlDocument xmldoc = new XmlDocument();
            XmlNode xmlnode = xmldoc.CreateNode(XmlNodeType.XmlDeclaration,
                    "", "");

            xmldoc.AppendChild(xmlnode);
            XmlElement rootElement = xmldoc.CreateElement("", "ConsoleSession",
                    "");

            xmldoc.AppendChild(rootElement);

            XmlElement res = xmldoc.CreateElement("", "Result", "");
            res.AppendChild(xmldoc.CreateTextNode("OK"));

            rootElement.AppendChild(res);

            reply["str_response_string"] = xmldoc.InnerXml;
            reply["int_response_code"] = 200;
            reply["content_type"] = "text/xml";
            reply = CheckOrigin(reply);

            return reply;
        }

        // Decode a HTTP form post to a Hashtable
        protected Hashtable DecodePostString(string data)
        {
            Hashtable result = new Hashtable();

            string[] terms = data.Split(new char[] {'&'});

            foreach (string term in terms)
            {
                string[] elems = term.Split(new char[] {'='});
                if (elems.Length == 0)
                    continue;

                string name = System.Web.HttpUtility.UrlDecode(elems[0]);
                string value = string.Empty;

                if (elems.Length > 1)
                    value = System.Web.HttpUtility.UrlDecode(elems[1]);

                result[name] = value;
            }

            return result;
        }

        // Close the CAP receiver for the responses for a given client.
        public void CloseConnection(UUID id)
        {
            try
            {
                string uri = "/ReadResponses/" + id.ToString() + "/";

                _Server.RemovePollServiceHTTPHandler("", uri);
            }
            catch (Exception)
            {
            }
        }

        // Check if there is anything to send. Return true if this client has
        // lines pending.
        protected bool HasEvents(UUID RequestID, UUID sessionID)
        {
            ConsoleConnection c = null;

            lock (_Connections)
            {
                if (!_Connections.ContainsKey(sessionID))
                    return false;
                c = _Connections[sessionID];
            }
            c.last = System.Environment.TickCount;
            if (c.lastLineSeen < _lineNumber)
                return true;
            return false;
        }

        // Send all pending output to the client.
        protected Hashtable GetEvents(UUID RequestID, UUID sessionID)
        {
            // Find the connection that goes with this client.
            ConsoleConnection c = null;

            lock (_Connections)
            {
                if (!_Connections.ContainsKey(sessionID))
                    return NoEvents(RequestID, UUID.Zero);
                c = _Connections[sessionID];
            }

            // If we have nothing to send, send the no events response.
            c.last = System.Environment.TickCount;
            if (c.lastLineSeen >= _lineNumber)
                return NoEvents(RequestID, UUID.Zero);

            Hashtable result = new Hashtable();

            // Create the response document.
            XmlDocument xmldoc = new XmlDocument();
            XmlNode xmlnode = xmldoc.CreateNode(XmlNodeType.XmlDeclaration,
                    "", "");

            xmldoc.AppendChild(xmlnode);
            XmlElement rootElement = xmldoc.CreateElement("", "ConsoleSession",
                    "");

            //if (c.newConnection)
            //{
            //    c.newConnection = false;
            //    Output("+++" + DefaultPrompt);
            //}

            lock (_Scrollback)
            {
                long startLine = _lineNumber - _Scrollback.Count;
                long sendStart = startLine;
                if (sendStart < c.lastLineSeen)
                    sendStart = c.lastLineSeen;

                for (long i = sendStart ; i < _lineNumber ; i++)
                {
                    ScrollbackEntry e = _Scrollback[(int)(i - startLine)];

                    XmlElement res = xmldoc.CreateElement("", "Line", "");
                    res.SetAttribute("Number", e.lineNumber.ToString());
                    res.SetAttribute("Level", e.level);
                    // Don't include these for the scrollback, we'll send the
                    // real state later.
                    if (!c.newConnection)
                    {
                        res.SetAttribute("Prompt", e.isPrompt ? "true" : "false");
                        res.SetAttribute("Command", e.isCommand ? "true" : "false");
                        res.SetAttribute("Input", e.isInput ? "true" : "false");
                    }
                    else if (i == _lineNumber - 1) // Last line for a new connection
                    {
                        res.SetAttribute("Prompt", _expectingInput ? "true" : "false");
                        res.SetAttribute("Command", _expectingCommand ? "true" : "false");
                        res.SetAttribute("Input", !_expectingInput ? "true" : "false");
                    }
                    else
                    {
                        res.SetAttribute("Input", e.isInput ? "true" : "false");
                    }

                    res.AppendChild(xmldoc.CreateTextNode(e.text));

                    rootElement.AppendChild(res);
                }
            }

            c.lastLineSeen = _lineNumber;
            c.newConnection = false;

            xmldoc.AppendChild(rootElement);

            result["str_response_string"] = xmldoc.InnerXml;
            result["int_response_code"] = 200;
            result["content_type"] = "application/xml";
            result["keepalive"] = false;
            result = CheckOrigin(result);

            return result;
        }

        // This is really just a no-op. It generates what is sent
        // to the client if the poll times out without any events.
        protected Hashtable NoEvents(UUID RequestID, UUID id)
        {
            Hashtable result = new Hashtable();

            XmlDocument xmldoc = new XmlDocument();
            XmlNode xmlnode = xmldoc.CreateNode(XmlNodeType.XmlDeclaration,
                    "", "");

            xmldoc.AppendChild(xmlnode);
            XmlElement rootElement = xmldoc.CreateElement("", "ConsoleSession",
                    "");

            xmldoc.AppendChild(rootElement);

            result["str_response_string"] = xmldoc.InnerXml;
            result["int_response_code"] = 200;
            result["content_type"] = "text/xml";
            result["keepalive"] = false;
            result = CheckOrigin(result);

            return result;
        }
    }
}
