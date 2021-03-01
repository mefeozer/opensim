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
using System.Timers;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Reflection;
using System.Text.RegularExpressions;
using OpenMetaverse;
using log4net;
using OpenSim.Framework;
using OpenSim.Framework.Monitoring;

namespace OpenSim.Region.OptionalModules.Avatar.Chat
{
    public class IRCConnector
    {

        #region Global (static) state

        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        // Local constants

        // This computation is not the real region center if the region is larger than 256.
        //     This computation isn't fixed because there is not a handle back to the region.
        private static readonly Vector3 CenterOfRegion = new Vector3((int)Constants.RegionSize * 0.5f, (int)Constants.RegionSize * 0.5f, 20);
        private static readonly char[] CS_SPACE = { ' ' };

        private const int WD_INTERVAL = 1000;     // base watchdog interval
        private static int PING_PERIOD = 15;       // WD intervals per PING
        private static int ICCD_PERIOD = 10;       // WD intervals between Connects
        private static readonly int L_TIMEOUT = 25;       // Login time out interval

        private static int _idk_ = 0;        // core connector identifier
        private static int _pdk_ = 0;        // ping interval counter
        private static int _icc_ = ICCD_PERIOD; // IRC connect counter

        // List of configured connectors

        private static readonly List<IRCConnector> _connectors = new List<IRCConnector>();

        // Watchdog state

        private static readonly System.Timers.Timer _watchdog = null;

        // The watch-dog gets started as soon as the class is instantiated, and
        // ticks once every second (WD_INTERVAL)

        static IRCConnector()
        {
            _log.DebugFormat("[IRC-Connector]: Static initialization started");
            _watchdog = new System.Timers.Timer(WD_INTERVAL);
            _watchdog.Elapsed += new ElapsedEventHandler(WatchdogHandler);
            _watchdog.AutoReset = true;
            _watchdog.Start();
            _log.DebugFormat("[IRC-Connector]: Static initialization complete");
        }

        #endregion

        #region Instance state

        // Connector identity

        internal int idn = _idk_++;

        // How many regions depend upon this connection
        // This count is updated by the ChannelState object and reflects the sum
        // of the region clients associated with the set of associated channel
        // state instances. That's why it cannot be managed here.

        internal int depends = 0;

        // This variable counts the number of resets that have been performed
        // on the connector. When a listener thread terminates, it checks to
        // see of the reset count has changed before it schedules another
        // reset.

        internal int _resetk = 0;

        private readonly object msyncConnect = new object();

        internal bool _randomizeNick = true; // add random suffix
        internal string _baseNick = null;      // base name for randomizing
        internal string _nick = null;          // effective nickname

        public string Nick                        // Public property
        {
            get => _nick;
            set => _nick = value;
        }

        private bool _enabled = false;            // connector enablement
        public bool Enabled => _enabled;

        private bool _connected = false;        // connection status
        private bool _pending = false;        // login disposition
        private int _timeout = L_TIMEOUT;    // login timeout counter
        public bool Connected => _connected;

        private string _ircChannel;            // associated channel id
        public string IrcChannel
        {
            get => _ircChannel;
            set => _ircChannel = value;
        }

        private uint _port = 6667;                // session port
        public uint Port
        {
            get => _port;
            set => _port = value;
        }

        private string _server = null;            // IRC server name
        public string Server
        {
            get => _server;
            set => _server = value;
        }
        private string _password = null;
        public string Password
        {
            get => _password;
            set => _password = value;
        }

        private readonly string _user = "USER OpenSimBot 8 * :I'm an OpenSim to IRC bot";
        public string User => _user;

        // Network interface

        private TcpClient _tcp;
        private NetworkStream _stream = null;
        private StreamReader _reader;
        private StreamWriter _writer;

        // Channel characteristic info (if available)

        internal string usermod = string.Empty;
        internal string chanmod = string.Empty;
        internal string version = string.Empty;
        internal bool motd = false;

        #endregion

        #region connector instance management

        internal IRCConnector(ChannelState cs)
        {

            // Prepare network interface

            _tcp = null;
            _writer = null;
            _reader = null;

            // Setup IRC session parameters

            _server = cs.Server;
            _password = cs.Password;
            _baseNick = cs.BaseNickname;
            _randomizeNick = cs.RandomizeNickname;
            _ircChannel = cs.IrcChannel;
            _port = cs.Port;
            _user = cs.User;

            if (_watchdog == null)
            {
                // Non-differentiating

                ICCD_PERIOD = cs.ConnectDelay;
                PING_PERIOD = cs.PingDelay;

                // Smaller values are not reasonable

                if (ICCD_PERIOD < 5)
                    ICCD_PERIOD = 5;

                if (PING_PERIOD < 5)
                    PING_PERIOD = 5;

                _icc_ = ICCD_PERIOD;    // get started right away!

            }

            // The last line of defense

            if (_server == null || _baseNick == null || _ircChannel == null || _user == null)
                throw new Exception("Invalid connector configuration");

            // Generate an initial nickname

            if (_randomizeNick)
                _nick = _baseNick + Util.RandomClass.Next(1, 99);
            else
                _nick = _baseNick;

            _log.InfoFormat("[IRC-Connector-{0}]: Initialization complete", idn);

        }

        ~IRCConnector()
        {
            _watchdog.Stop();
            Close();
        }

        // Mark the connector as connectable. Harmless if already enabled.

        public void Open()
        {
            if (!_enabled)
            {

                if (!Connected)
                {
                    Connect();
                }

                lock (_connectors)
                    _connectors.Add(this);

                _enabled = true;

            }
        }

        // Only close the connector if the dependency count is zero.

        public void Close()
        {
            _log.InfoFormat("[IRC-Connector-{0}] Closing", idn);

            lock (msyncConnect)
            {

                if (depends == 0 && Enabled)
                {

                    _enabled = false;

                    if (Connected)
                    {
                        _log.DebugFormat("[IRC-Connector-{0}] Closing interface", idn);

                        // Cleanup the IRC session

                        try
                        {
                            _writer.WriteLine(string.Format("QUIT :{0} to {1} wormhole to {2} closing",
                                _nick, _ircChannel, _server));
                            _writer.Flush();
                        }
                        catch (Exception) { }

                        _connected = false;

                        try { _writer.Close(); }
                        catch (Exception) { }
                        try { _reader.Close(); }
                        catch (Exception) { }
                        try { _stream.Close(); }
                        catch (Exception) { }
                        try { _tcp.Close(); }
                        catch (Exception) { }

                    }
                    lock (_connectors)
                        _connectors.Remove(this);
                }
            }

            _log.InfoFormat("[IRC-Connector-{0}] Closed", idn);

        }

        #endregion

        #region session management

        // Connect to the IRC server. A connector should always be connected, once enabled

        public void Connect()
        {
            if (!_enabled)
                return;

            // Delay until next WD cycle if this is too close to the last start attempt
            if(_icc_ < ICCD_PERIOD)
                return;

            _log.DebugFormat("[IRC-Connector-{0}]: Connection request for {1} on {2}:{3}", idn, _nick, _server, _ircChannel);

            _icc_ = 0;

            lock (msyncConnect)
            {
                try
                {
                    if (_connected) return;

                    _connected = true;
                    _pending = true;
                    _timeout = L_TIMEOUT;

                    _tcp = new TcpClient(_server, (int)_port);
                    _stream = _tcp.GetStream();
                    _reader = new StreamReader(_stream);
                    _writer = new StreamWriter(_stream);

                    _log.InfoFormat("[IRC-Connector-{0}]: Connected to {1}:{2}", idn, _server, _port);

                    WorkManager.StartThread(ListenerRun, "IRCConnectionListenerThread", false, false);

                    // This is the message order recommended by RFC 2812
                    if (_password != null)
                        _writer.WriteLine(string.Format("PASS {0}", _password));
                    _writer.WriteLine(string.Format("NICK {0}", _nick));
                    _writer.Flush();
                    _writer.WriteLine(string.Format("USER {0} 0 * :OpenSim Relay",_user));
                    _writer.Flush();
                }
                catch (Exception e)
                {
                    _log.ErrorFormat("[IRC-Connector-{0}] cannot connect {1} to {2}:{3}: {4}",
                                      idn, _nick, _server, _port, e.Message);
                    // It might seem reasonable to reset connected and pending status here
                    // Seeing as we know that the login has failed, but if we do that, then
                    // connection will be retried each time the interconnection interval
                    // expires. By leaving them as they are, the connection will be retried
                    // when the login timeout expires. Which is preferred.
                }
            }

            return;
        }

        // Reconnect is used to force a re-cycle of the IRC connection. Should generally
        // be a transparent event

        public void Reconnect()
        {
            _log.DebugFormat("[IRC-Connector-{0}]: Reconnect request for {1} on {2}:{3}", idn, _nick, _server, _ircChannel);

            // Don't do this if a Connect is in progress...

            lock (msyncConnect)
            {

                if (_connected)
                {

                    _log.InfoFormat("[IRC-Connector-{0}] Resetting connector", idn);

                    // Mark as disconnected. This will allow the listener thread
                    // to exit if still in-flight.


                    // The listener thread is not aborted - it *might* actually be
                    // the thread that is running the Reconnect! Instead just close
                    // the socket and it will disappear of its own accord, once this
                    // processing is completed.

                    try { _writer.Close(); }
                    catch (Exception) { }
                    try { _reader.Close(); }
                    catch (Exception) { }
                    try { _tcp.Close(); }
                    catch (Exception) { }

                    _connected = false;
                    _pending = false;
                    _resetk++;

                }

            }

            Connect();

        }

        #endregion

        #region Outbound (to-IRC) message handlers

        public void PrivMsg(string pattern, string from, string region, string msg)
        {

            // _log.DebugFormat("[IRC-Connector-{0}] PrivMsg to IRC from {1}: <{2}>", idn, from,
            //     String.Format(pattern, _ircChannel, from, region, msg));

            // One message to the IRC server

            try
            {
                _writer.WriteLine(pattern, _ircChannel, from, region, msg);
                _writer.Flush();
                // _log.DebugFormat("[IRC-Connector-{0}]: PrivMsg from {1} in {2}: {3}", idn, from, region, msg);
            }
            catch (IOException)
            {
                _log.ErrorFormat("[IRC-Connector-{0}]: PrivMsg I/O Error: disconnected from IRC server", idn);
                Reconnect();
            }
            catch (Exception ex)
            {
                _log.ErrorFormat("[IRC-Connector-{0}]: PrivMsg exception : {1}", idn, ex.Message);
                _log.Debug(ex);
            }

        }

        public void Send(string msg)
        {

            // _log.DebugFormat("[IRC-Connector-{0}] Send to IRC : <{1}>", idn,  msg);

            try
            {
                _writer.WriteLine(msg);
                _writer.Flush();
                // _log.DebugFormat("[IRC-Connector-{0}] Sent command string: {1}", idn, msg);
            }
            catch (IOException)
            {
                _log.ErrorFormat("[IRC-Connector-{0}] Disconnected from IRC server.(Send)", idn);
                Reconnect();
            }
            catch (Exception ex)
            {
                _log.ErrorFormat("[IRC-Connector-{0}] Send exception trap: {0}", idn, ex.Message);
                _log.Debug(ex);
            }

        }

        #endregion

        public void ListenerRun()
        {

            string inputLine;
            int resetk = _resetk;

            try
            {
                while (_enabled && _connected)
                {
                    if ((inputLine = _reader.ReadLine()) == null)
                        throw new Exception("Listener input socket closed");

                    Watchdog.UpdateThread();

                    // _log.Info("[IRCConnector]: " + inputLine);

                    if (inputLine.Contains("PRIVMSG"))
                    {
                        Dictionary<string, string> data = ExtractMsg(inputLine);

                        // Any chat ???
                        if (data != null)
                        {
                            OSChatMessage c = new OSChatMessage
                            {
                                Message = data["msg"],
                                Type = ChatTypeEnum.Region,
                                Position = CenterOfRegion,
                                From = data["nick"] + "@IRC",
                                Sender = null,
                                SenderUUID = UUID.Zero
                            };

                            // Is message "\001ACTION foo bar\001"?
                            // Then change to: "/me foo bar"

                            if (1 == c.Message[0] && c.Message.Substring(1).StartsWith("ACTION"))
                                c.Message = string.Format("/me {0}", c.Message.Substring(8, c.Message.Length - 9));

                            ChannelState.OSChat(this, c, false);
                        }
                    }
                    else
                    {
                        ProcessIRCCommand(inputLine);
                    }
                }
            }
            catch (Exception /*e*/)
            {
                // _log.ErrorFormat("[IRC-Connector-{0}]: ListenerRun exception trap: {1}", idn, e.Message);
                // _log.Debug(e);
            }

            // This is potentially circular, but harmless if so.
            // The connection is marked as not connected the first time
            // through reconnect.

            if (_enabled && _resetk == resetk)
                Reconnect();

            Watchdog.RemoveThread();
        }

        private readonly Regex RE = new Regex(@":(?<nick>[\w-]*)!(?<user>\S*) PRIVMSG (?<channel>\S+) :(?<msg>.*)",
                                     RegexOptions.Multiline);

        private Dictionary<string, string> ExtractMsg(string input)
        {
            //examines IRC commands and extracts any private messages
            // which will then be reboadcast in the Sim

            // _log.InfoFormat("[IRC-Connector-{0}]: ExtractMsg: {1}", idn, input);

            Dictionary<string, string> result = null;
            MatchCollection matches = RE.Matches(input);

            // Get some direct matches $1 $4 is a
            if (matches.Count == 0 || matches.Count != 1 || matches[0].Groups.Count != 5)
            {
                // _log.Info("[IRCConnector]: Number of matches: " + matches.Count);
                // if (matches.Count > 0)
                // {
                //     _log.Info("[IRCConnector]: Number of groups: " + matches[0].Groups.Count);
                // }
                return null;
            }

            result = new Dictionary<string, string>();
            result.Add("nick", matches[0].Groups[1].Value);
            result.Add("user", matches[0].Groups[2].Value);
            result.Add("channel", matches[0].Groups[3].Value);
            result.Add("msg", matches[0].Groups[4].Value);

            return result;
        }

        public void BroadcastSim(string sender, string format, params string[] args)
        {
            try
            {
                OSChatMessage c = new OSChatMessage
                {
                    From = sender,
                    Message = string.Format(format, args),
                    Type = ChatTypeEnum.Region, // ChatTypeEnum.Say;
                    Position = CenterOfRegion,
                    Sender = null,
                    SenderUUID = UUID.Zero
                };

                ChannelState.OSChat(this, c, true);

            }
            catch (Exception ex) // IRC gate should not crash Sim
            {
                _log.ErrorFormat("[IRC-Connector-{0}]: BroadcastSim Exception Trap: {1}\n{2}", idn, ex.Message, ex.StackTrace);
            }
        }

        #region IRC Command Handlers

        public void ProcessIRCCommand(string command)
        {

            string[] commArgs;
            string c_server = _server;

            string pfx = string.Empty;
            string cmd = string.Empty;
            string parms = string.Empty;

            // ":" indicates that a prefix is present
            // There are NEVER more than 17 real
            // fields. A parameter that starts with
            // ":" indicates that the remainder of the
            // line is a single parameter value.

            commArgs = command.Split(CS_SPACE, 2);

            if (commArgs[0].StartsWith(":"))
            {
                pfx = commArgs[0].Substring(1);
                commArgs = commArgs[1].Split(CS_SPACE, 2);
            }

            cmd = commArgs[0];
            parms = commArgs[1];

            // _log.DebugFormat("[IRC-Connector-{0}] prefix = <{1}> cmd = <{2}>", idn, pfx, cmd);

            switch (cmd)
            {

                // Messages 001-004 are always sent
                // following signon.

                case "001": // Welcome ...
                case "002": // Server information
                case "003": // Welcome ...
                    break;
                case "004": // Server information

                    _log.DebugFormat("[IRC-Connector-{0}] [{1}] parms = <{2}>", idn, cmd, parms);
                    commArgs = parms.Split(CS_SPACE);
                    c_server = commArgs[1];
                    _server = c_server;
                    version = commArgs[2];
                    usermod = commArgs[3];
                    chanmod = commArgs[4];

                    _writer.WriteLine(string.Format("JOIN {0}", _ircChannel));
                    _writer.Flush();
                    _log.InfoFormat("[IRC-Connector-{0}]: sent request to join {1} ", idn, _ircChannel);

                    break;
                case "005": // Server information
                    break;
                case "042":
                case "250":
                case "251":
                case "252":
                case "254":
                case "255":
                case "265":
                case "266":
                case "332": // Subject
                case "333": // Subject owner (?)
                case "353": // Name list
                case "366": // End-of-Name list marker
                case "372": // MOTD body
                case "375": // MOTD start
                    // _log.InfoFormat("[IRC-Connector-{0}] [{1}] {2}", idn, cmd, parms.Split(CS_SPACE,2)[1]);
                    break;
                case "376": // MOTD end
                    // _log.InfoFormat("[IRC-Connector-{0}] [{1}] {2}", idn, cmd, parms.Split(CS_SPACE,2)[1]);
                    motd = true;
                    break;
                case "451": // Not registered
                    break;
                case "433": // Nickname in use
                    // Gen a new name
                    _nick = _baseNick + Util.RandomClass.Next(1, 99);
                    _log.ErrorFormat("[IRC-Connector-{0}]: [{1}] IRC SERVER reports NicknameInUse, trying {2}", idn, cmd, _nick);
                    // Retry
                    _writer.WriteLine(string.Format("NICK {0}", _nick));
                    _writer.Flush();
                    _writer.WriteLine(_user);
                    _writer.Flush();
                    _writer.WriteLine(string.Format("JOIN {0}", _ircChannel));
                    _writer.Flush();
                    break;
                case "479": // Bad channel name, etc. This will never work, so disable the connection
                    _log.ErrorFormat("[IRC-Connector-{0}] [{1}] {2}", idn, cmd, parms.Split(CS_SPACE, 2)[1]);
                    _log.ErrorFormat("[IRC-Connector-{0}] [{1}] Connector disabled", idn, cmd);
                    _enabled = false;
                    _connected = false;
                    _pending = false;
                    break;
                case "NOTICE":
                    // _log.WarnFormat("[IRC-Connector-{0}] [{1}] {2}", idn, cmd, parms.Split(CS_SPACE,2)[1]);
                    break;
                case "ERROR":
                    _log.ErrorFormat("[IRC-Connector-{0}] [{1}] {2}", idn, cmd, parms.Split(CS_SPACE, 2)[1]);
                    if (parms.Contains("reconnect too fast"))
                        ICCD_PERIOD++;
                    _pending = false;
                    Reconnect();
                    break;
                case "PING":
                    _log.DebugFormat("[IRC-Connector-{0}] [{1}] parms = <{2}>", idn, cmd, parms);
                    _writer.WriteLine(string.Format("PONG {0}", parms));
                    _writer.Flush();
                    break;
                case "PONG":
                    break;
                case "JOIN":

                    _log.DebugFormat("[IRC-Connector-{0}] [{1}] parms = <{2}>", idn, cmd, parms);
                    eventIrcJoin(pfx, cmd, parms);
                    break;
                case "PART":
                    _log.DebugFormat("[IRC-Connector-{0}] [{1}] parms = <{2}>", idn, cmd, parms);
                    eventIrcPart(pfx, cmd, parms);
                    break;
                case "MODE":
                    _log.DebugFormat("[IRC-Connector-{0}] [{1}] parms = <{2}>", idn, cmd, parms);
                    eventIrcMode(pfx, cmd, parms);
                    break;
                case "NICK":
                    _log.DebugFormat("[IRC-Connector-{0}] [{1}] parms = <{2}>", idn, cmd, parms);
                    eventIrcNickChange(pfx, cmd, parms);
                    break;
                case "KICK":
                    _log.DebugFormat("[IRC-Connector-{0}] [{1}] parms = <{2}>", idn, cmd, parms);
                    eventIrcKick(pfx, cmd, parms);
                    break;
                case "QUIT":
                    _log.DebugFormat("[IRC-Connector-{0}] [{1}] parms = <{2}>", idn, cmd, parms);
                    eventIrcQuit(pfx, cmd, parms);
                    break;
                default:
                    _log.DebugFormat("[IRC-Connector-{0}] Command '{1}' ignored, parms = {2}", idn, cmd, parms);
                    break;
            }

            // _log.DebugFormat("[IRC-Connector-{0}] prefix = <{1}> cmd = <{2}> complete", idn, pfx, cmd);

        }

        public void eventIrcJoin(string prefix, string command, string parms)
        {
            string[] args = parms.Split(CS_SPACE, 2);
            string IrcUser = prefix.Split('!')[0];
            string IrcChannel = args[0];

            if (IrcChannel.StartsWith(":"))
                IrcChannel = IrcChannel.Substring(1);

            if(IrcChannel == _ircChannel)
            {
                _log.InfoFormat("[IRC-Connector-{0}] Joined requested channel {1} at {2}", idn, IrcChannel,_server);
                _pending = false;
            }
            else
                _log.InfoFormat("[IRC-Connector-{0}] Joined unknown channel {1} at {2}", idn, IrcChannel,_server);
            BroadcastSim(IrcUser, "/me joins {0}", IrcChannel);
        }

        public void eventIrcPart(string prefix, string command, string parms)
        {
            string[] args = parms.Split(CS_SPACE, 2);
            string IrcUser = prefix.Split('!')[0];
            string IrcChannel = args[0];

            _log.DebugFormat("[IRC-Connector-{0}] Event: IRCPart {1}:{2}", idn, _server, _ircChannel);
            BroadcastSim(IrcUser, "/me parts {0}", IrcChannel);
        }

        public void eventIrcMode(string prefix, string command, string parms)
        {
            string[] args = parms.Split(CS_SPACE, 2);
            string UserMode = args[1];

            _log.DebugFormat("[IRC-Connector-{0}] Event: IRCMode {1}:{2}", idn, _server, _ircChannel);
            if (UserMode.Substring(0, 1) == ":")
            {
                UserMode = UserMode.Remove(0, 1);
            }
        }

        public void eventIrcNickChange(string prefix, string command, string parms)
        {
            string[] args = parms.Split(CS_SPACE, 2);
            string UserOldNick = prefix.Split('!')[0];
            string UserNewNick = args[0].Remove(0, 1);

            _log.DebugFormat("[IRC-Connector-{0}] Event: IRCNickChange {1}:{2}", idn, _server, _ircChannel);
            BroadcastSim(UserOldNick, "/me is now known as {0}", UserNewNick);
        }

        public void eventIrcKick(string prefix, string command, string parms)
        {
            string[] args = parms.Split(CS_SPACE, 3);
            string UserKicker = prefix.Split('!')[0];
            string IrcChannel = args[0];
            string UserKicked = args[1];
            string KickMessage = args[2];

            _log.DebugFormat("[IRC-Connector-{0}] Event: IRCKick {1}:{2}", idn, _server, _ircChannel);
            BroadcastSim(UserKicker, "/me kicks kicks {0} off {1} saying \"{2}\"", UserKicked, IrcChannel, KickMessage);

            if (UserKicked == _nick)
            {
                BroadcastSim(_nick, "Hey, that was me!!!");
            }

        }

        public void eventIrcQuit(string prefix, string command, string parms)
        {
            string IrcUser = prefix.Split('!')[0];
            string QuitMessage = parms;

            _log.DebugFormat("[IRC-Connector-{0}] Event: IRCQuit {1}:{2}", idn, _server, _ircChannel);
            BroadcastSim(IrcUser, "/me quits saying \"{0}\"", QuitMessage);
        }

        #endregion

        #region Connector Watch Dog

        // A single watch dog monitors extant connectors and makes sure that they
        // are re-connected as necessary. If a connector IS connected, then it is
        // pinged, but only if a PING period has elapsed.

        protected static void WatchdogHandler(object source, ElapsedEventArgs args)
        {

            // _log.InfoFormat("[IRC-Watchdog] Status scan, pdk = {0}, icc = {1}", _pdk_, _icc_);

            _pdk_ = (_pdk_ + 1) % PING_PERIOD;    // cycle the ping trigger
            _icc_++;    // increment the inter-consecutive-connect-delay counter

            lock (_connectors)
                foreach (IRCConnector connector in _connectors)
                {

                    // _log.InfoFormat("[IRC-Watchdog] Scanning {0}", connector);

                    if (connector.Enabled)
                    {
                        if (!connector.Connected)
                        {
                            try
                            {
                                // _log.DebugFormat("[IRC-Watchdog] Connecting {1}:{2}", connector.idn, connector._server, connector._ircChannel);
                                connector.Connect();
                            }
                            catch (Exception e)
                            {
                                _log.ErrorFormat("[IRC-Watchdog] Exception on connector {0}: {1} ", connector.idn, e.Message);
                            }
                        }
                        else
                        {

                            if (connector._pending)
                            {
                                if (connector._timeout == 0)
                                {
                                    _log.ErrorFormat("[IRC-Watchdog] Login timed-out for connector {0}, reconnecting", connector.idn);
                                    connector.Reconnect();
                                }
                                else
                                    connector._timeout--;
                            }

                            // Being marked connected is not enough to ping. Socket establishment can sometimes take a long
                            // time, in which case the watch dog might try to ping the server before the socket has been
                            // set up, with nasty side-effects.

                            else if (_pdk_ == 0)
                            {
                                try
                                {
                                    connector._writer.WriteLine(string.Format("PING :{0}", connector._server));
                                    connector._writer.Flush();
                                }
                                catch (Exception e)
                                {
                                    _log.ErrorFormat("[IRC-PingRun] Exception on connector {0}: {1} ", connector.idn, e.Message);
                                    _log.Debug(e);
                                    connector.Reconnect();
                                }
                            }

                        }
                    }
                }

            // _log.InfoFormat("[IRC-Watchdog] Status scan completed");

        }

        #endregion

    }
}
