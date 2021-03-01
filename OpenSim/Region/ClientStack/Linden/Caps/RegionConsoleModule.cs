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
using System.Collections.Concurrent;
using System.Net;
using Nini.Config;
using Mono.Addins;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Framework.Console;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using Caps = OpenSim.Framework.Capabilities.Caps;


namespace OpenSim.Region.ClientStack.Linden
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "RegionConsoleModule")]
    public class RegionConsoleModule : INonSharedRegionModule, IRegionConsole
    {
//        private static readonly ILog _log =
//            LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private Scene _scene;
        private IEventQueue _eventQueue;
        private readonly Commands _commands = new Commands();
        public ICommands Commands => _commands;

        readonly ConcurrentDictionary<UUID, OnOutputDelegate> currentConsoles = new ConcurrentDictionary<UUID, OnOutputDelegate>();

        public event ConsoleMessage OnConsoleMessage;

        public void Initialise(IConfigSource source)
        {
            _commands.AddCommand( "Help", false, "help", "help [<item>]", "Display help on a particular command or on a list of commands in a category", Help);
        }

        public void AddRegion(Scene s)
        {
            _scene = s;
            _scene.RegisterModuleInterface<IRegionConsole>(this);
        }

        public void RemoveRegion(Scene s)
        {
            _scene.EventManager.OnRegisterCaps -= RegisterCaps;
            _scene = null;
        }

        public void RegionLoaded(Scene s)
        {
            _scene.EventManager.OnRegisterCaps += RegisterCaps;
            _eventQueue = _scene.RequestModuleInterface<IEventQueue>();
        }

        public void PostInitialise()
        {
        }

        public void Close() { }

        public string Name => "RegionConsoleModule";

        public Type ReplaceableInterface => null;

        public void RegisterCaps(UUID agentID, Caps caps)
        {
            //if (!_scene.RegionInfo.EstateSettings.IsEstateManagerOrOwner(agentID) && !_scene.Permissions.IsGod(agentID))
            //    return;
            caps.RegisterSimpleHandler("SimConsoleAsync",
                    new ConsoleHandler("/" + UUID.Random(), "SimConsoleAsync", agentID, this, _scene));
        }

        public void AddConsole(UUID agentID, OnOutputDelegate console)
        {
            if(currentConsoles.TryAdd(agentID, console))
                MainConsole.Instance.OnOutput += console;
        }

        public void RemoveConsole(UUID agentID)
        {
            if(currentConsoles.TryRemove(agentID, out OnOutputDelegate console))
                MainConsole.Instance.OnOutput -= console;
        }

        public void SendConsoleOutput(UUID agentID, string message)
        {
            if (!_scene.TryGetScenePresence(agentID, out ScenePresence sp) || sp.IsChildAgent || sp.IsDeleted)
            {
                RemoveConsole(agentID);
                return;
            }

            _eventQueue.Enqueue(_eventQueue.BuildEvent("SimConsoleResponse", OSD.FromString(message)), agentID);
            OnConsoleMessage?.Invoke( agentID, message);
        }

        public bool RunCommand(string command, UUID invokerID)
        {
            string[] parts = Parser.Parse(command);
            Array.Resize(ref parts, parts.Length + 1);
            parts[parts.Length - 1] = invokerID.ToString();

            if (_commands.Resolve(parts).Length == 0)
                return false;

            return true;
        }

        private void Help(string module, string[] cmd)
        {
            UUID agentID = new UUID(cmd[cmd.Length - 1]);
            Array.Resize(ref cmd, cmd.Length - 1);

            List<string> help = Commands.GetHelp(cmd);

            string reply = string.Empty;

            foreach (string s in help)
            {
                reply += s + "\n";
            }

            SendConsoleOutput(agentID, reply);
        }

        public void AddCommand(string module, bool shared, string command, string help, string longhelp, CommandDelegate fn)
        {
            _commands.AddCommand(module, shared, command, help, longhelp, fn);
        }
    }

    public class ConsoleHandler : SimpleStreamHandler
    {
//        private static readonly ILog _log =
//            LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private readonly RegionConsoleModule _consoleModule;
        private readonly UUID _agentID;
        private readonly bool _isGod;
        private readonly Scene _scene;
        private bool _consoleIsOn = false;

        public ConsoleHandler(string path, string name, UUID agentID, RegionConsoleModule module, Scene scene)
                :base(path)
        {
            _agentID = agentID;
            _consoleModule = module;
            _scene = scene;
            _isGod = _scene.Permissions.IsGod(agentID);
        }

        protected override void ProcessRequest(IOSHttpRequest httpRequest, IOSHttpResponse httpResponse)
        {
            httpResponse.KeepAlive = false;

            if(httpRequest.HttpMethod != "POST")
            {
                httpResponse.StatusCode = (int)HttpStatusCode.NotFound;
                return;
            }

            httpResponse.StatusCode = (int)HttpStatusCode.OK;

            if (!_scene.TryGetScenePresence(_agentID, out ScenePresence sp) || sp.IsChildAgent || sp.IsDeleted)
            {
                return;
            }

            if (!_scene.RegionInfo.EstateSettings.IsEstateManagerOrOwner(_agentID) && !_isGod)
            {
                _consoleModule.SendConsoleOutput(_agentID, "No access");
                return;
            }

            OSD osd;
            try
            {
                osd = OSDParser.DeserializeLLSDXml(httpRequest.InputStream);
            }
            catch
            {
                httpResponse.StatusCode = (int)HttpStatusCode.BadRequest;
                return;
            }

            string cmd = osd.AsString();
            if (cmd == "set console on")
            {
                if (_isGod)
                {
                    _consoleModule.AddConsole(_agentID, ConsoleSender);
                    _consoleIsOn = true;
                    _consoleModule.SendConsoleOutput(_agentID, "Console is now on");
                }
                return;
            }
            else if (cmd == "set console off")
            {
                _consoleModule.RemoveConsole(_agentID);
                _consoleIsOn = false;
                _consoleModule.SendConsoleOutput(_agentID, "Console is now off");
                return;
            }

            if (_consoleIsOn == false && _consoleModule.RunCommand(osd.AsString().Trim(), _agentID))
                return;

            if (_isGod && _consoleIsOn)
            {
                MainConsole.Instance.RunCommand(osd.AsString().Trim());
            }
            else
            {
                _consoleModule.SendConsoleOutput(_agentID, "Unknown command");
            }
        }

        private void ConsoleSender(string text)
        {
            _consoleModule.SendConsoleOutput(_agentID, text);
        }

 
    }
}
