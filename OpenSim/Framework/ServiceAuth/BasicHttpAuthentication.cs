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
using System.Collections.Specialized;
using System.Net;

using Nini.Config;

namespace OpenSim.Framework.ServiceAuth
{
    public class BasicHttpAuthentication : IServiceAuth
    {
//        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public string Name => "BasicHttp";

        private readonly string _Username;
        private readonly string _Password;
        private readonly string _CredentialsB64;

//        private string remove_me;

        public string Credentials => _CredentialsB64;

        public BasicHttpAuthentication(IConfigSource config, string section)
        {
//            remove_me = section;
            _Username = Util.GetConfigVarFromSections<string>(config, "HttpAuthUsername", new string[] { "Network", section }, string.Empty);
            _Password = Util.GetConfigVarFromSections<string>(config, "HttpAuthPassword", new string[] { "Network", section }, string.Empty);
            string str = _Username + ":" + _Password;
            byte[] encData_byte = Util.UTF8.GetBytes(str);

            _CredentialsB64 = Convert.ToBase64String(encData_byte);
//            _log.DebugFormat("[HTTP BASIC AUTH]: {0} {1} [{2}]", _Username, _Password, section);
        }

        public void AddAuthorization(NameValueCollection headers)
        {
            //_log.DebugFormat("[HTTP BASIC AUTH]: Adding authorization for {0}", remove_me);
            headers["Authorization"] = "Basic " + _CredentialsB64;
        }

        public bool Authenticate(string data)
        {
            string recovered = Util.Base64ToString(data);
            if (!string.IsNullOrEmpty(recovered))
            {
                string[] parts = recovered.Split(new char[] { ':' });
                if (parts.Length >= 2)
                {
                    return _Username.Equals(parts[0]) && _Password.Equals(parts[1]);
                }
            }

            return false;
        }

        public bool Authenticate(NameValueCollection requestHeaders, AddHeaderDelegate d, out HttpStatusCode statusCode)
        {
//            _log.DebugFormat("[HTTP BASIC AUTH]: Authenticate in {0}", "BasicHttpAuthentication");

            string value = requestHeaders.Get("Authorization");
            if (value != null)
            {
                value = value.Trim();
                if (value.StartsWith("Basic "))
                {
                    value = value.Replace("Basic ", string.Empty);
                    if (Authenticate(value))
                    {
                        statusCode = HttpStatusCode.OK;
                        return true;
                    }
                }
            }

            d("WWW-Authenticate", "Basic realm = \"Asset Server\"");

            statusCode = HttpStatusCode.Unauthorized;
            return false;
        }
    }
}
