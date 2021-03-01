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

using OpenMetaverse.StructuredData;

namespace OpenSim.Framework.Monitoring
{
    // Create a time histogram of events. The histogram is built in a wrap-around
    //   array of equally distributed buckets.
    // For instance, a minute long histogram of second sized buckets would be:
    //          new EventHistogram(60, 1000)
    public class EventHistogram
{
    private int _timeBase;
    private readonly int _numBuckets;
    private readonly int _bucketMilliseconds;
    private int _lastBucket;
    private readonly int _totalHistogramMilliseconds;
    private readonly long[] _histogram;
    private readonly object histoLock = new object();

    public EventHistogram(int numberOfBuckets, int millisecondsPerBucket)
    {
        _numBuckets = numberOfBuckets;
        _bucketMilliseconds = millisecondsPerBucket;
        _totalHistogramMilliseconds = _numBuckets * _bucketMilliseconds;

        _histogram = new long[_numBuckets];
        Zero();
        _lastBucket = 0;
        _timeBase = Util.EnvironmentTickCount();
    }

    public void Event()
    {
        this.Event(1);
    }

    // Record an event at time 'now' in the histogram.
    public void Event(int cnt)
    {
        lock (histoLock)
        {
            // The time as displaced from the base of the histogram
            int bucketTime = Util.EnvironmentTickCountSubtract(_timeBase);

            // If more than the total time of the histogram, we just start over
            if (bucketTime > _totalHistogramMilliseconds)
            {
                Zero();
                _lastBucket = 0;
                _timeBase = Util.EnvironmentTickCount();
            }
            else
            {
                // To which bucket should we add this event?
                int bucket = bucketTime / _bucketMilliseconds;

                // Advance _lastBucket to the new bucket. Zero any buckets skipped over.
                while (bucket != _lastBucket)
                {
                    // Zero from just after the last bucket to the new bucket or the end
                    for (int jj = _lastBucket + 1; jj <= Math.Min(bucket, _numBuckets - 1); jj++)
                    {
                        _histogram[jj] = 0;
                    }
                    _lastBucket = bucket;
                    // If the new bucket is off the end, wrap around to the beginning
                    if (bucket > _numBuckets)
                    {
                        bucket -= _numBuckets;
                        _lastBucket = 0;
                        _histogram[_lastBucket] = 0;
                        _timeBase += _totalHistogramMilliseconds;
                    }
                }
            }
            _histogram[_lastBucket] += cnt;
        }
    }

    // Get a copy of the current histogram
    public long[] GetHistogram()
    {
        long[] ret = new long[_numBuckets];
        lock (histoLock)
        {
            int indx = _lastBucket + 1;
            for (int ii = 0; ii < _numBuckets; ii++, indx++)
            {
                if (indx >= _numBuckets)
                    indx = 0;
                ret[ii] = _histogram[indx];
            }
        }
        return ret;
    }

    public OSDMap GetHistogramAsOSDMap()
    {
        OSDMap ret = new OSDMap();

        ret.Add("Buckets", OSD.FromInteger(_numBuckets));
        ret.Add("BucketMilliseconds", OSD.FromInteger(_bucketMilliseconds));
        ret.Add("TotalMilliseconds", OSD.FromInteger(_totalHistogramMilliseconds));

        // Compute a number for the first bucket in the histogram.
        // This will allow readers to know how this histogram relates to any previously read histogram.
        int baseBucketNum = _timeBase / _bucketMilliseconds + _lastBucket + 1;
        ret.Add("BaseNumber", OSD.FromInteger(baseBucketNum));

        ret.Add("Values", GetHistogramAsOSDArray());

        return ret;
    }
    // Get a copy of the current histogram
    public OSDArray GetHistogramAsOSDArray()
    {
        OSDArray ret = new OSDArray(_numBuckets);
        lock (histoLock)
        {
            int indx = _lastBucket + 1;
            for (int ii = 0; ii < _numBuckets; ii++, indx++)
            {
                if (indx >= _numBuckets)
                    indx = 0;
                ret[ii] = OSD.FromLong(_histogram[indx]);
            }
        }
        return ret;
    }

    // Zero out the histogram
    public void Zero()
    {
        lock (histoLock)
        {
            for (int ii = 0; ii < _numBuckets; ii++)
                _histogram[ii] = 0;
        }
    }
}

}
