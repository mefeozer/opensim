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
using System.Reflection;
using OpenSim.Framework;

using log4net;

namespace OpenSim.Region.ClientStack.LindenUDP
{
    /// <summary>
    /// A hierarchical token bucket for bandwidth throttling. See
    /// http://en.wikipedia.org/wiki/Token_bucket for more information
    /// </summary>
    public class TokenBucket
    {
        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private static int _counter = 0;

         /// <summary>
        /// minimum recovery rate, ie bandwith
        /// </summary>
        protected const float MINDRIPRATE = 500;

        // maximim burst size, ie max number of bytes token can have
        protected const float MAXBURST = 7500;

        /// <summary>Time of the last drip</summary>
        protected double _lastDrip;

        /// <summary>
        /// The number of bytes that can be sent at this moment. This is the
        /// current number of tokens in the bucket
        /// </summary>
        protected float _tokenCount;

        /// <summary>
        /// Map of children buckets and their requested maximum burst rate
        /// </summary>

        protected Dictionary<TokenBucket, float> _children = new Dictionary<TokenBucket, float>();

#region Properties

        /// <summary>
        /// The parent bucket of this bucket, or null if this bucket has no
        /// parent. The parent bucket will limit the aggregate bandwidth of all
        /// of its children buckets
        /// </summary>
        protected TokenBucket _parent;
        public TokenBucket Parent
        {
            get => _parent;
            set => _parent = value;
        }
        /// <summary>
        /// This is the maximum number
        /// of tokens that can accumulate in the bucket at any one time. This
        /// also sets the total request for leaf nodes
        /// </summary>
        protected float _burst;

        protected float _maxDripRate = 0;
        public virtual float MaxDripRate
        {
            get => _maxDripRate;
            set => _maxDripRate = value;
        }

        public float RequestedBurst
        {
            get => _burst;
            set {
                float rate = value < 0 ? 0 : value;
                if (rate > MAXBURST)
                    rate = MAXBURST;

                _burst = rate;
                }
        }

        public float Burst => RequestedBurst * BurstModifier();

        /// <summary>
        /// The requested drip rate for this particular bucket.
        /// </summary>
        /// <remarks>
        /// 0 then TotalDripRequest is used instead.
        /// Can never be above MaxDripRate.
        /// Tokens are added to the bucket at any time
        /// <seealso cref="RemoveTokens"/> is called, at the granularity of
        /// the system tick interval (typically around 15-22ms)</remarks>
        protected float _dripRate;

        public float RequestedDripRate
        {
            get => _dripRate == 0 ? _totalDripRequest : _dripRate;
            set {
                _dripRate = value < 0 ? 0 : value;
                _totalDripRequest = _dripRate;

                if (_parent != null)
                    _parent.RegisterRequest(this,_dripRate);
            }
        }

       public float DripRate
        {
            get {
                float rate = Math.Min(RequestedDripRate,TotalDripRequest);
                if (_parent == null)
                    return rate;

                rate *= _parent.DripRateModifier();
                if (rate < MINDRIPRATE)
                    rate = MINDRIPRATE;

                return rate;
            }
        }

        /// <summary>
        /// The current total of the requested maximum burst rates of children buckets.
        /// </summary>
        protected float _totalDripRequest;
        public float TotalDripRequest
        {
            get => _totalDripRequest;
            set => _totalDripRequest = value;
        }

#endregion Properties

#region Constructor


        /// <summary>
        /// Default constructor
        /// </summary>
        /// <param name="identifier">Identifier for this token bucket</param>
        /// <param name="parent">Parent bucket if this is a child bucket, or
        /// null if this is a root bucket</param>
        /// <param name="maxBurst">Maximum size of the bucket in bytes, or
        /// zero if this bucket has no maximum capacity</param>
        /// <param name="dripRate">Rate that the bucket fills, in bytes per
        /// second. If zero, the bucket always remains full</param>
        public TokenBucket(TokenBucket parent, float dripRate, float MaxBurst)
        {
            _counter++;

            Parent = parent;
            RequestedDripRate = dripRate;
            RequestedBurst = MaxBurst;
            _lastDrip = Util.GetTimeStamp() + 1000; // skip first drip
        }

#endregion Constructor

        /// <summary>
        /// Compute a modifier for the MaxBurst rate. This is 1.0, meaning
        /// no modification if the requested bandwidth is less than the
        /// max burst bandwidth all the way to the root of the throttle
        /// hierarchy. However, if any of the parents is over-booked, then
        /// the modifier will be less than 1.
        /// </summary>
        protected float DripRateModifier()
        {
            float driprate = DripRate;
            return driprate >= TotalDripRequest ? 1.0f : driprate / TotalDripRequest;
        }

        /// <summary>
        /// </summary>
        protected float BurstModifier()
        {
            return DripRateModifier();
        }

        /// <summary>
        /// Register drip rate requested by a child of this throttle. Pass the
        /// changes up the hierarchy.
        /// </summary>
        public void RegisterRequest(TokenBucket child, float request)
        {
            lock (_children)
            {
                _children[child] = request;

                _totalDripRequest = 0;
                foreach (KeyValuePair<TokenBucket, float> cref in _children)
                    _totalDripRequest += cref.Value;
            }

            // Pass the new values up to the parent
            if (_parent != null)
                _parent.RegisterRequest(this, Math.Min(RequestedDripRate, TotalDripRequest));
        }

        /// <summary>
        /// Remove the rate requested by a child of this throttle. Pass the
        /// changes up the hierarchy.
        /// </summary>
        public void UnregisterRequest(TokenBucket child)
        {
            lock (_children)
            {
                _children.Remove(child);

                _totalDripRequest = 0;
                foreach (KeyValuePair<TokenBucket, float> cref in _children)
                    _totalDripRequest += cref.Value;
            }

            // Pass the new values up to the parent
            if (Parent != null)
                Parent.RegisterRequest(this,Math.Min(RequestedDripRate, TotalDripRequest));
        }

        /// <summary>
        /// Remove a given number of tokens from the bucket
        /// </summary>
        /// <param name="amount">Number of tokens to remove from the bucket</param>
        /// <returns>True if the requested number of tokens were removed from
        /// the bucket, otherwise false</returns>
        public bool RemoveTokens(int amount)
        {
            // Deposit tokens for this interval
            Drip();

            // If we have enough tokens then remove them and return
            if (_tokenCount > 0)
            {
                _tokenCount -= amount;
                return true;
            }

            return false;
        }

        public bool CheckTokens(int amount)
        {
            return  _tokenCount > 0;
        }

        public int GetCatBytesCanSend(int timeMS)
        {
            return (int)(timeMS * DripRate * 1e-3);
        }

        /// <summary>
        /// Add tokens to the bucket over time. The number of tokens added each
        /// call depends on the length of time that has passed since the last
        /// call to Drip
        /// </summary>
        /// <returns>True if tokens were added to the bucket, otherwise false</returns>
        protected void Drip()
        {
            // This should never happen... means we are a leaf node and were created
            // with no drip rate...
            if (DripRate == 0)
            {
                _log.WarnFormat("[TOKENBUCKET] something odd is happening and drip rate is 0 for {0}", _counter);
                return;
            }

            double now = Util.GetTimeStamp();
            double delta = now - _lastDrip;
            _lastDrip = now;

            if (delta <= 0)
                return;

            _tokenCount += (float)delta * DripRate;

            float burst = Burst;
            if (_tokenCount > burst)
                _tokenCount = burst;
        }
    }

    public class AdaptiveTokenBucket : TokenBucket
    {
        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public bool AdaptiveEnabled { get; set; }

        /// <summary>
        /// The minimum rate for flow control. Minimum drip rate is one
        /// packet per second.
        /// </summary>

        protected const float _minimumFlow = 50000;

        // <summary>
        // The maximum rate for flow control. Drip rate can never be
        // greater than this.
        // </summary>

        public override float MaxDripRate
        {
            get => _maxDripRate == 0 ? _totalDripRequest : _maxDripRate;
            set => _maxDripRate = value == 0 ? _totalDripRequest : Math.Max(value, _minimumFlow);
        }

        private readonly bool _enabled = false;

        // <summary>
        // Adjust drip rate in response to network conditions.
        // </summary>
        public float AdjustedDripRate
        {
            get => _dripRate;
            set
            {
                _dripRate = OpenSim.Framework.Util.Clamp<float>(value, _minimumFlow, MaxDripRate);

                if (_parent != null)
                    _parent.RegisterRequest(this, _dripRate);
            }
        }


        // <summary>
        //
        // </summary>
        public AdaptiveTokenBucket(TokenBucket parent, float maxDripRate, float maxBurst, bool enabled)
            : base(parent, maxDripRate, maxBurst)
        {
            _enabled = enabled;

            _maxDripRate = maxDripRate == 0 ? _totalDripRequest : Math.Max(maxDripRate, _minimumFlow);

            if (enabled)
                _dripRate = _maxDripRate * .5f;
            else
                _dripRate = _maxDripRate;
            if (_parent != null)
                _parent.RegisterRequest(this, _dripRate);
        }

        /// <summary>
        /// Reliable packets sent to the client for which we never received an ack adjust the drip rate down.
        /// <param name="packets">Number of packets that expired without successful delivery</param>
        /// </summary>
        public void ExpirePackets(int count)
        {
            // _log.WarnFormat("[ADAPTIVEBUCKET] drop {0} by {1} expired packets",AdjustedDripRate,count);
            if (_enabled)
                AdjustedDripRate = (long)(AdjustedDripRate / Math.Pow(2, count));
        }

        // <summary>
        //
        // </summary>
        public void AcknowledgePackets(int count)
        {
            if (_enabled)
                AdjustedDripRate = AdjustedDripRate + count;
        }
    }
}
