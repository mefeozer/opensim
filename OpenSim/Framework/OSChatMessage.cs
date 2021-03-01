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
using OpenMetaverse;

namespace OpenSim.Framework
{
    public interface IEventArgs
    {
        IScene Scene { get; set; }
        IClientAPI Sender { get; set; }
    }

    /// <summary>
    /// ChatFromViewer Arguments
    /// </summary>
    public class OSChatMessage : EventArgs, IEventArgs
    {
        protected int _channel;
        protected string _from;
        protected string _message;
        protected Vector3 _position;

        protected IScene _scene;
        protected IClientAPI _sender;
        protected object _senderObject;
        protected ChatTypeEnum _type;
        protected UUID _fromID;
        protected UUID _destination = UUID.Zero;

        public OSChatMessage()
        {
            _position = new Vector3();
        }

        /// <summary>
        /// The message sent by the user
        /// </summary>
        public string Message
        {
            get => _message;
            set => _message = value;
        }

        /// <summary>
        /// The type of message, eg say, shout, broadcast.
        /// </summary>
        public ChatTypeEnum Type
        {
            get => _type;
            set => _type = value;
        }

        /// <summary>
        /// Which channel was this message sent on? Different channels may have different listeners. Public chat is on channel zero.
        /// </summary>
        public int Channel
        {
            get => _channel;
            set => _channel = value;
        }

        /// <summary>
        /// The position of the sender at the time of the message broadcast.
        /// </summary>
        public Vector3 Position
        {
            get => _position;
            set => _position = value;
        }

        /// <summary>
        /// The name of the sender (needed for scripts)
        /// </summary>
        public string From
        {
            get => _from;
            set => _from = value;
        }

        #region IEventArgs Members

        /// TODO: Sender and SenderObject should just be Sender and of
        /// type IChatSender

        /// <summary>
        /// The client responsible for sending the message, or null.
        /// </summary>
        public IClientAPI Sender
        {
            get => _sender;
            set => _sender = value;
        }

        /// <summary>
        /// The object responsible for sending the message, or null.
        /// </summary>
        public object SenderObject
        {
            get => _senderObject;
            set => _senderObject = value;
        }

        public UUID SenderUUID
        {
            get => _fromID;
            set => _fromID = value;
        }

        public UUID Destination
        {
            get => _destination;
            set => _destination = value;
        }

        /// <summary>
        ///
        /// </summary>
        public IScene Scene
        {
            get => _scene;
            set => _scene = value;
        }

        public override string ToString()
        {
            return _message;
        }

        #endregion
    }
}
