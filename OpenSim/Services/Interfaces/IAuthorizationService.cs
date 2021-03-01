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

namespace OpenSim.Services.Interfaces
{
    // Generic Authorization service used for authorizing principals in a particular region

    public interface IAuthorizationService
    {
        /// <summary>
        /// Check whether the user should be given access to the region.
        /// </summary>
        /// <remarks>
        /// We also supply user first name and last name for situations where the user does not have an account
        /// on the region (e.g. they're a visitor via Hypergrid).
        /// </remarks>
        /// <param name="userID"></param>
        /// <param name="firstName">/param>
        /// <param name="lastName"></param>
        /// <param name="regionID"></param>
        /// <param name="message"></param>
        /// <returns></returns>
        bool IsAuthorizedForRegion(
            string userID, string firstName, string lastName, string regionID, out string message);
    }

    public class AuthorizationRequest
    {
        private string _userID;
        private string _firstname;
        private string _surname;
        private string _email;
        private string _regionName;
        private string _regionID;

        public AuthorizationRequest()
        {
        }

        public AuthorizationRequest(string ID, string RegionID)
        {
            _userID = ID;
            _regionID = RegionID;
        }

        public AuthorizationRequest(
            string ID, string FirstName, string SurName, string Email, string RegionName, string RegionID)
        {
            _userID = ID;
            _firstname = FirstName;
            _surname = SurName;
            _email = Email;
            _regionName = RegionName;
            _regionID = RegionID;
        }

        public string ID
        {
            get => _userID;
            set => _userID = value;
        }

        public string FirstName
        {
            get => _firstname;
            set => _firstname = value;
        }

        public string SurName
        {
            get => _surname;
            set => _surname = value;
        }

        public string Email
        {
            get => _email;
            set => _email = value;
        }

        public string RegionName
        {
            get => _regionName;
            set => _regionName = value;
        }

        public string RegionID
        {
            get => _regionID;
            set => _regionID = value;
        }
    }

    public class AuthorizationResponse
    {
        private bool _isAuthorized;
        private string _message;

        public AuthorizationResponse()
        {
        }

        public AuthorizationResponse(bool isAuthorized, string message)
        {
            _isAuthorized = isAuthorized;
            _message = message;
        }

        public bool IsAuthorized
        {
            get => _isAuthorized;
            set => _isAuthorized = value;
        }

        public string Message
        {
            get => _message;
            set => _message = value;
        }
    }
}