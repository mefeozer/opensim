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
using OpenSim.Region.Framework.Interfaces;
using OpenMetaverse;

namespace OpenSim.Region.CoreModules.Framework.InterfaceCommander
{
    /// <summary>
    /// A single function call encapsulated in a class which enforces arguments when passing around as Object[]'s.
    /// Used for console commands and script API generation
    /// </summary>
    public class Command : ICommand
    {
        //private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private readonly List<CommandArgument> _args = new List<CommandArgument>();

        private readonly Action<object[]> _command;
        private readonly string _help;
        private readonly string _name;
        private readonly CommandIntentions _intentions; //A permission type system could implement this and know what a command intends on doing.

        public Command(string name, CommandIntentions intention, Action<object[]> command, string help)
        {
            _name = name;
            _command = command;
            _help = help;
            _intentions = intention;
        }

        #region ICommand Members

        public void AddArgument(string name, string helptext, string type)
        {
            _args.Add(new CommandArgument(name, helptext, type));
        }

        public string Name => _name;

        public CommandIntentions Intentions => _intentions;

        public string Help => _help;

        public Dictionary<string, string> Arguments
        {
            get
            {
                Dictionary<string, string> tmp = new Dictionary<string, string>();
                foreach (CommandArgument arg in _args)
                {
                    tmp.Add(arg.Name, arg.ArgumentType);
                }
                return tmp;
            }
        }

        public string ShortHelp()
        {
            string help = _name;

            foreach (CommandArgument arg in _args)
            {
                help += " <" + arg.Name + ">";
            }

            return help;
        }

        public void ShowConsoleHelp()
        {
            Console.WriteLine("== " + Name + " ==");
            Console.WriteLine(_help);
            Console.WriteLine("= Parameters =");
            foreach (CommandArgument arg in _args)
            {
                Console.WriteLine("* " + arg.Name + " (" + arg.ArgumentType + ")");
                Console.WriteLine("\t" + arg.HelpText);
            }
        }

        public void Run(object[] args)
        {
            object[] cleanArgs = new object[_args.Count];

            if (args.Length < cleanArgs.Length)
            {
                Console.WriteLine("ERROR: Missing " + (cleanArgs.Length - args.Length) + " argument(s)");
                ShowConsoleHelp();
                return;
            }
            if (args.Length > cleanArgs.Length)
            {
                Console.WriteLine("ERROR: Too many arguments for this command. Type '<module> <command> help' for help.");
                return;
            }

            int i = 0;
            foreach (object arg in args)
            {
                if (string.IsNullOrEmpty(arg.ToString()))
                {
                    Console.WriteLine("ERROR: Empty arguments are not allowed");
                    return;
                }
                try
                {
                    switch (_args[i].ArgumentType)
                    {
                        case "String":
                            _args[i].ArgumentValue = arg.ToString();
                            break;
                        case "Integer":
                            _args[i].ArgumentValue = int.Parse(arg.ToString());
                            break;
                        case "Float":
                            _args[i].ArgumentValue = float.Parse(arg.ToString(), OpenSim.Framework.Culture.NumberFormatInfo);
                            break;
                        case "Double":
                            _args[i].ArgumentValue = double.Parse(arg.ToString(), OpenSim.Framework.Culture.NumberFormatInfo);
                            break;
                        case "Boolean":
                            _args[i].ArgumentValue = bool.Parse(arg.ToString());
                            break;
                        case "UUID":
                            _args[i].ArgumentValue = UUID.Parse(arg.ToString());
                            break;
                        default:
                            Console.WriteLine("ERROR: Unknown desired type for argument " + _args[i].Name + " on command " + _name);
                            break;
                    }
                }
                catch (FormatException)
                {
                    Console.WriteLine("ERROR: Argument number " + (i + 1) +
                                " (" + _args[i].Name + ") must be a valid " +
                                _args[i].ArgumentType.ToLower() + ".");
                    return;
                }
                cleanArgs[i] = _args[i].ArgumentValue;

                i++;
            }

            _command.Invoke(cleanArgs);
        }

        #endregion
    }

    /// <summary>
    /// A single command argument, contains name, type and at runtime, value.
    /// </summary>
    public class CommandArgument
    {
        private readonly string _help;
        private readonly string _name;
        private readonly string _type;
        private object _val;

        public CommandArgument(string name, string help, string type)
        {
            _name = name;
            _help = help;
            _type = type;
        }

        public string Name => _name;

        public string HelpText => _help;

        public string ArgumentType => _type;

        public object ArgumentValue
        {
            get => _val;
            set => _val = value;
        }
    }
}
