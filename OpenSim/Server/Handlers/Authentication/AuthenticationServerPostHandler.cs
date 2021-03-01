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

using Nini.Config;
using log4net;
using System;
using System.Reflection;
using System.IO;
using System.Xml;
using System.Collections.Generic;
using OpenSim.Server.Base;
using OpenSim.Services.Interfaces;
using OpenSim.Framework;
using OpenSim.Framework.ServiceAuth;
using OpenSim.Framework.Servers.HttpServer;
using OpenMetaverse;

namespace OpenSim.Server.Handlers.Authentication
{
    public class AuthenticationServerPostHandler : BaseStreamHandler
    {
        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private readonly IAuthenticationService _AuthenticationService;

        private readonly bool _AllowGetAuthInfo = false;
        private readonly bool _AllowSetAuthInfo = false;
        private readonly bool _AllowSetPassword = false;

        public AuthenticationServerPostHandler(IAuthenticationService service) :
                this(service, null, null) {}

        public AuthenticationServerPostHandler(IAuthenticationService service, IConfig config, IServiceAuth auth) :
                base("POST", "/auth", auth)
        {
            _AuthenticationService = service;

            if (config != null)
            {
                _AllowGetAuthInfo = config.GetBoolean("AllowGetAuthInfo", _AllowGetAuthInfo);
                _AllowSetAuthInfo = config.GetBoolean("AllowSetAuthInfo", _AllowSetAuthInfo);
                _AllowSetPassword = config.GetBoolean("AllowSetPassword", _AllowSetPassword);
            }
        }

        protected override byte[] ProcessRequest(string path, Stream request,
                IOSHttpRequest httpRequest, IOSHttpResponse httpResponse)
        {
//            _log.Error("[XXX]: Authenticating...");
            string[] p = SplitParams(path);

            if (p.Length > 0)
            {
                switch (p[0])
                {
                case "plain":
                    string body;
                    using(StreamReader sr = new StreamReader(request))
                        body = sr.ReadToEnd();
                    return DoPlainMethods(body);

                case "crypt":
                    byte[] buffer = new byte[request.Length];
                    long length = request.Length;
                    if (length > 16384)
                        length = 16384;
                    request.Read(buffer, 0, (int)length);

                    return DoEncryptedMethods(buffer);
                }
            }
            return new byte[0];
        }

        private byte[] DoPlainMethods(string body)
        {
            Dictionary<string, object> request =
                    ServerUtils.ParseQueryString(body);

            int lifetime = 30;

            if (request.ContainsKey("LIFETIME"))
            {
                lifetime = Convert.ToInt32(request["LIFETIME"].ToString());
                if (lifetime > 30)
                    lifetime = 30;
            }

            if (!request.ContainsKey("METHOD"))
                return FailureResult();
            if (!request.ContainsKey("PRINCIPAL"))
                return FailureResult();

            string method = request["METHOD"].ToString();

            UUID principalID;
            string token;

            if (!UUID.TryParse(request["PRINCIPAL"].ToString(), out principalID))
                return FailureResult();

            switch (method)
            {
                case "authenticate":
                    if (!request.ContainsKey("PASSWORD"))
                        return FailureResult();

                    token = _AuthenticationService.Authenticate(principalID, request["PASSWORD"].ToString(), lifetime);

                    if (!string.IsNullOrEmpty(token))
                        return SuccessResult(token);
                    return FailureResult();

                case "setpassword":
                    if (!_AllowSetPassword)
                        return FailureResult();

                    if (!request.ContainsKey("PASSWORD"))
                        return FailureResult();

                    if (_AuthenticationService.SetPassword(principalID, request["PASSWORD"].ToString()))
                        return SuccessResult();
                    else
                        return FailureResult();

                case "verify":
                    if (!request.ContainsKey("TOKEN"))
                        return FailureResult();

                    if (_AuthenticationService.Verify(principalID, request["TOKEN"].ToString(), lifetime))
                        return SuccessResult();

                    return FailureResult();

                case "release":
                    if (!request.ContainsKey("TOKEN"))
                        return FailureResult();

                    if (_AuthenticationService.Release(principalID, request["TOKEN"].ToString()))
                        return SuccessResult();

                    return FailureResult();

                case "getauthinfo":
                    if (_AllowGetAuthInfo)
                        return GetAuthInfo(principalID);

                    break;

                case "setauthinfo":
                    if (_AllowSetAuthInfo)
                        return SetAuthInfo(principalID, request);

                    break;
            }

            return FailureResult();
        }

        private byte[] DoEncryptedMethods(byte[] ciphertext)
        {
            return new byte[0];
        }

        private byte[] SuccessResult()
        {
            XmlDocument doc = new XmlDocument();

            XmlNode xmlnode = doc.CreateNode(XmlNodeType.XmlDeclaration,
                    "", "");

            doc.AppendChild(xmlnode);

            XmlElement rootElement = doc.CreateElement("", "ServerResponse",
                    "");

            doc.AppendChild(rootElement);

            XmlElement result = doc.CreateElement("", "Result", "");
            result.AppendChild(doc.CreateTextNode("Success"));

            rootElement.AppendChild(result);

            return Util.DocToBytes(doc);
        }

        byte[] GetAuthInfo(UUID principalID)
        {
            AuthInfo info = _AuthenticationService.GetAuthInfo(principalID);

            if (info != null)
            {
                Dictionary<string, object> result = new Dictionary<string, object>();
                result["result"] = info.ToKeyValuePairs();

                return ResultToBytes(result);
            }
            else
            {
                return FailureResult();
            }
        }

        byte[] SetAuthInfo(UUID principalID, Dictionary<string, object> request)
        {
            AuthInfo existingInfo = _AuthenticationService.GetAuthInfo(principalID);

            if (existingInfo == null)
                return FailureResult();

            if (request.ContainsKey("AccountType"))
                existingInfo.AccountType = request["AccountType"].ToString();

            if (request.ContainsKey("PasswordHash"))
                existingInfo.PasswordHash = request["PasswordHash"].ToString();

            if (request.ContainsKey("PasswordSalt"))
                existingInfo.PasswordSalt = request["PasswordSalt"].ToString();

            if (request.ContainsKey("WebLoginKey"))
                existingInfo.WebLoginKey = request["WebLoginKey"].ToString();

            if (!_AuthenticationService.SetAuthInfo(existingInfo))
            {
                _log.ErrorFormat(
                    "[AUTHENTICATION SERVER POST HANDLER]: Authentication info store failed for account {0} {1} {2}",
                    existingInfo.PrincipalID);

                return FailureResult();
            }

            return SuccessResult();
        }

        private byte[] FailureResult()
        {
            XmlDocument doc = new XmlDocument();

            XmlNode xmlnode = doc.CreateNode(XmlNodeType.XmlDeclaration,
                    "", "");

            doc.AppendChild(xmlnode);

            XmlElement rootElement = doc.CreateElement("", "ServerResponse",
                    "");

            doc.AppendChild(rootElement);

            XmlElement result = doc.CreateElement("", "Result", "");
            result.AppendChild(doc.CreateTextNode("Failure"));

            rootElement.AppendChild(result);

            return Util.DocToBytes(doc);
        }

        private byte[] SuccessResult(string token)
        {
            XmlDocument doc = new XmlDocument();

            XmlNode xmlnode = doc.CreateNode(XmlNodeType.XmlDeclaration,
                    "", "");

            doc.AppendChild(xmlnode);

            XmlElement rootElement = doc.CreateElement("", "ServerResponse",
                    "");

            doc.AppendChild(rootElement);

            XmlElement result = doc.CreateElement("", "Result", "");
            result.AppendChild(doc.CreateTextNode("Success"));

            rootElement.AppendChild(result);

            XmlElement t = doc.CreateElement("", "Token", "");
            t.AppendChild(doc.CreateTextNode(token));

            rootElement.AppendChild(t);

            return Util.DocToBytes(doc);
        }

        private byte[] ResultToBytes(Dictionary<string, object> result)
        {
            string xmlString = ServerUtils.BuildXmlResponse(result);
            return Util.UTF8NoBomEncoding.GetBytes(xmlString);
        }
    }
}
