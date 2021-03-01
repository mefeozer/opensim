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
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Serialization;
using Nini.Config;

using OpenMetaverse;
using OpenSim.Data;
using OpenSim.Framework;
using OpenSim.Services.Interfaces;

namespace OpenSim.OfflineIM
{
    public class OfflineIMService : OfflineIMServiceBase, IOfflineIMService
    {
//        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private const int MAX_IM = 25;

        private readonly XmlSerializer _serializer;
        private static bool _Initialized = false;

        public OfflineIMService(IConfigSource config)
            : base(config)
        {
            _serializer = new XmlSerializer(typeof(GridInstantMessage));
            if (!_Initialized)
            {
                _Database.DeleteOld();
                _Initialized = true;
            }
        }

        public List<GridInstantMessage> GetMessages(UUID principalID)
        {
            List<GridInstantMessage> ims = new List<GridInstantMessage>();

            OfflineIMData[] messages = _Database.Get("PrincipalID", principalID.ToString());

            if (messages == null || messages != null && messages.Length == 0)
                return ims;

            foreach (OfflineIMData m in messages)
            {
                using (MemoryStream mstream = new MemoryStream(Encoding.UTF8.GetBytes(m.Data["Message"])))
                {
                    GridInstantMessage im = (GridInstantMessage)_serializer.Deserialize(mstream);
                    ims.Add(im);
                }
            }

            // Then, delete them
            _Database.Delete("PrincipalID", principalID.ToString());

            return ims;
        }

        public bool StoreMessage(GridInstantMessage im, out string reason)
        {
            reason = string.Empty;

            // Check limits
            UUID principalID = new UUID(im.toAgentID);
            long count = _Database.GetCount("PrincipalID", principalID.ToString());
            if (count >= MAX_IM)
            {
                reason = "Number of offline IMs has maxed out";
                return false;
            }

            string imXml;
            using (MemoryStream mstream = new MemoryStream())
            {
                XmlWriterSettings settings = new XmlWriterSettings
                {
                    Encoding = Util.UTF8NoBomEncoding
                };

                using (XmlWriter writer = XmlWriter.Create(mstream, settings))
                {
                    _serializer.Serialize(writer, im);
                    writer.Flush();
                    imXml = Util.UTF8NoBomEncoding.GetString(mstream.ToArray());
                }
            }

            OfflineIMData data = new OfflineIMData
            {
                PrincipalID = principalID,
                FromID = new UUID(im.fromAgentID),
                Data = new Dictionary<string, string>()
            };
            data.Data["Message"] = imXml;

            return _Database.Store(data);

        }

        public void DeleteMessages(UUID userID)
        {
            _Database.Delete("PrincipalID", userID.ToString());
            _Database.Delete("FromID", userID.ToString());
        }

    }
}
