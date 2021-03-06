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
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.SharedBase;
using System.Text;
using System.IO;
using System.Xml;

namespace OpenSim.Region.Framework.Scenes
{
    public class SOPVehicle
    {
        public VehicleData vd;

        public Vehicle Type => vd._type;

        public SOPVehicle()
        {
            vd = new VehicleData();
            ProcessTypeChange(Vehicle.TYPE_NONE); // is needed?
        }

        public void ProcessFloatVehicleParam(Vehicle pParam, float pValue)
        {
            float len;
            float timestep = 0.01f;
            switch (pParam)
            {
                case Vehicle.ANGULAR_DEFLECTION_EFFICIENCY:
                    if (pValue < 0f) pValue = 0f;
                    if (pValue > 1f) pValue = 1f;
                    vd._angularDeflectionEfficiency = pValue;
                    break;
                case Vehicle.ANGULAR_DEFLECTION_TIMESCALE:
                    if (pValue < timestep) pValue = timestep;
                    vd._angularDeflectionTimescale = pValue;
                    break;
                case Vehicle.ANGULAR_MOTOR_DECAY_TIMESCALE:
                    if (pValue < timestep) pValue = timestep;
                    else if (pValue > 120) pValue = 120;
                    vd._angularMotorDecayTimescale = pValue;
                    break;
                case Vehicle.ANGULAR_MOTOR_TIMESCALE:
                    if (pValue < timestep) pValue = timestep;
                    vd._angularMotorTimescale = pValue;
                    break;
                case Vehicle.BANKING_EFFICIENCY:
                    if (pValue < -1f) pValue = -1f;
                    if (pValue > 1f) pValue = 1f;
                    vd._bankingEfficiency = pValue;
                    break;
                case Vehicle.BANKING_MIX:
                    if (pValue < 0f) pValue = 0f;
                    if (pValue > 1f) pValue = 1f;
                    vd._bankingMix = pValue;
                    break;
                case Vehicle.BANKING_TIMESCALE:
                    if (pValue < timestep) pValue = timestep;
                    vd._bankingTimescale = pValue;
                    break;
                case Vehicle.BUOYANCY:
                    if (pValue < -1f) pValue = -1f;
                    if (pValue > 1f) pValue = 1f;
                    vd._VehicleBuoyancy = pValue;
                    break;
                case Vehicle.HOVER_EFFICIENCY:
                    if (pValue < 0f) pValue = 0f;
                    if (pValue > 1f) pValue = 1f;
                    vd._VhoverEfficiency = pValue;
                    break;
                case Vehicle.HOVER_HEIGHT:
                    vd._VhoverHeight = pValue;
                    break;
                case Vehicle.HOVER_TIMESCALE:
                    if (pValue < timestep) pValue = timestep;
                    vd._VhoverTimescale = pValue;
                    break;
                case Vehicle.LINEAR_DEFLECTION_EFFICIENCY:
                    if (pValue < 0f) pValue = 0f;
                    if (pValue > 1f) pValue = 1f;
                    vd._linearDeflectionEfficiency = pValue;
                    break;
                case Vehicle.LINEAR_DEFLECTION_TIMESCALE:
                    if (pValue < timestep) pValue = timestep;
                    vd._linearDeflectionTimescale = pValue;
                    break;
                case Vehicle.LINEAR_MOTOR_DECAY_TIMESCALE:
                    if (pValue < timestep) pValue = timestep;
                    else if (pValue > 120) pValue = 120;
                    vd._linearMotorDecayTimescale = pValue;
                    break;
                case Vehicle.LINEAR_MOTOR_TIMESCALE:
                    if (pValue < timestep) pValue = timestep;
                    vd._linearMotorTimescale = pValue;
                    break;
                case Vehicle.VERTICAL_ATTRACTION_EFFICIENCY:
                    if (pValue < 0f) pValue = 0f;
                    if (pValue > 1f) pValue = 1f;
                    vd._verticalAttractionEfficiency = pValue;
                    break;
                case Vehicle.VERTICAL_ATTRACTION_TIMESCALE:
                    if (pValue < timestep) pValue = timestep;
                    vd._verticalAttractionTimescale = pValue;
                    break;

                // These are vector properties but the engine lets you use a single float value to
                // set all of the components to the same value
                case Vehicle.ANGULAR_FRICTION_TIMESCALE:
                    if (pValue < timestep) pValue = timestep;
                    vd._angularFrictionTimescale = new Vector3(pValue, pValue, pValue);
                    break;
                case Vehicle.ANGULAR_MOTOR_DIRECTION:
                    vd._angularMotorDirection = new Vector3(pValue, pValue, pValue);
                    len = vd._angularMotorDirection.Length();
                    if (len > 12.566f)
                        vd._angularMotorDirection *= 12.566f / len;
                    break;
                case Vehicle.LINEAR_FRICTION_TIMESCALE:
                    if (pValue < timestep) pValue = timestep;
                    vd._linearFrictionTimescale = new Vector3(pValue, pValue, pValue);
                    break;
                case Vehicle.LINEAR_MOTOR_DIRECTION:
                    vd._linearMotorDirection = new Vector3(pValue, pValue, pValue);
                    len = vd._linearMotorDirection.Length();
                    if (len > 30.0f)
                        vd._linearMotorDirection *= 30.0f / len;
                    break;
                case Vehicle.LINEAR_MOTOR_OFFSET:
                    vd._linearMotorOffset = new Vector3(pValue, pValue, pValue);
                    len = vd._linearMotorOffset.Length();
                    if (len > 100.0f)
                        vd._linearMotorOffset *= 100.0f / len;
                    break;
            }
        }//end ProcessFloatVehicleParam

        public void ProcessVectorVehicleParam(Vehicle pParam, Vector3 pValue)
        {
            float len;
            float timestep = 0.01f;
            switch (pParam)
            {
                case Vehicle.ANGULAR_FRICTION_TIMESCALE:
                    if (pValue.X < timestep) pValue.X = timestep;
                    if (pValue.Y < timestep) pValue.Y = timestep;
                    if (pValue.Z < timestep) pValue.Z = timestep;

                    vd._angularFrictionTimescale = new Vector3(pValue.X, pValue.Y, pValue.Z);
                    break;
                case Vehicle.ANGULAR_MOTOR_DIRECTION:
                    vd._angularMotorDirection = new Vector3(pValue.X, pValue.Y, pValue.Z);
                    // Limit requested angular speed to 2 rps= 4 pi rads/sec
                    len = vd._angularMotorDirection.Length();
                    if (len > 12.566f)
                        vd._angularMotorDirection *= 12.566f / len;
                    break;
                case Vehicle.LINEAR_FRICTION_TIMESCALE:
                    if (pValue.X < timestep) pValue.X = timestep;
                    if (pValue.Y < timestep) pValue.Y = timestep;
                    if (pValue.Z < timestep) pValue.Z = timestep;
                    vd._linearFrictionTimescale = new Vector3(pValue.X, pValue.Y, pValue.Z);
                    break;
                case Vehicle.LINEAR_MOTOR_DIRECTION:
                    vd._linearMotorDirection = new Vector3(pValue.X, pValue.Y, pValue.Z);
                    len = vd._linearMotorDirection.Length();
                    if (len > 30.0f)
                        vd._linearMotorDirection *= 30.0f / len;
                    break;
                case Vehicle.LINEAR_MOTOR_OFFSET:
                    vd._linearMotorOffset = new Vector3(pValue.X, pValue.Y, pValue.Z);
                    len = vd._linearMotorOffset.Length();
                    if (len > 100.0f)
                        vd._linearMotorOffset *= 100.0f / len;
                    break;
            }
        }//end ProcessVectorVehicleParam

        public void ProcessRotationVehicleParam(Vehicle pParam, Quaternion pValue)
        {
            switch (pParam)
            {
                case Vehicle.REFERENCE_FRAME:
                    vd._referenceFrame = pValue;
                    break;
            }
        }//end ProcessRotationVehicleParam

        public void ProcessVehicleFlags(int pParam, bool remove)
        {
            if (remove)
            {
                vd._flags &= ~(VehicleFlag)pParam;
            }
            else
            {
                vd._flags |= (VehicleFlag)pParam;
            }
        }//end ProcessVehicleFlags

        public void ProcessTypeChange(Vehicle pType)
        {
            vd._linearMotorDirection = Vector3.Zero;
            vd._angularMotorDirection = Vector3.Zero;
            vd._linearMotorOffset = Vector3.Zero;
            vd._referenceFrame = Quaternion.Identity;

            // Set Defaults For Type
            vd._type = pType;
            switch (pType)
            {
                case Vehicle.TYPE_NONE:
                    vd._linearFrictionTimescale = new Vector3(1000, 1000, 1000);
                    vd._angularFrictionTimescale = new Vector3(1000, 1000, 1000);
                    vd._linearMotorTimescale = 1000;
                    vd._linearMotorDecayTimescale = 120;
                    vd._angularMotorTimescale = 1000;
                    vd._angularMotorDecayTimescale = 1000;
                    vd._VhoverHeight = 0;
                    vd._VhoverEfficiency = 1;
                    vd._VhoverTimescale = 1000;
                    vd._VehicleBuoyancy = 0;
                    vd._linearDeflectionEfficiency = 0;
                    vd._linearDeflectionTimescale = 1000;
                    vd._angularDeflectionEfficiency = 0;
                    vd._angularDeflectionTimescale = 1000;
                    vd._bankingEfficiency = 0;
                    vd._bankingMix = 1;
                    vd._bankingTimescale = 1000;
                    vd._verticalAttractionEfficiency = 0;
                    vd._verticalAttractionTimescale = 1000;

                    vd._flags = 0;
                    break;

                case Vehicle.TYPE_SLED:
                    vd._linearFrictionTimescale = new Vector3(30, 1, 1000);
                    vd._angularFrictionTimescale = new Vector3(1000, 1000, 1000);
                    vd._linearMotorTimescale = 1000;
                    vd._linearMotorDecayTimescale = 120;
                    vd._angularMotorTimescale = 1000;
                    vd._angularMotorDecayTimescale = 120;
                    vd._VhoverHeight = 0;
                    vd._VhoverEfficiency = 1;
                    vd._VhoverTimescale = 10;
                    vd._VehicleBuoyancy = 0;
                    vd._linearDeflectionEfficiency = 1;
                    vd._linearDeflectionTimescale = 1;
                    vd._angularDeflectionEfficiency = 0;
                    vd._angularDeflectionTimescale = 1000;
                    vd._bankingEfficiency = 0;
                    vd._bankingMix = 1;
                    vd._bankingTimescale = 10;
                    vd._flags &=
                         ~(VehicleFlag.HOVER_WATER_ONLY | VehicleFlag.HOVER_TERRAIN_ONLY |
                           VehicleFlag.HOVER_GLOBAL_HEIGHT | VehicleFlag.HOVER_UP_ONLY);
                    vd._flags |= VehicleFlag.NO_DEFLECTION_UP | VehicleFlag.LIMIT_ROLL_ONLY | VehicleFlag.LIMIT_MOTOR_UP;
                    break;
                case Vehicle.TYPE_CAR:
                    vd._linearFrictionTimescale = new Vector3(100, 2, 1000);
                    vd._angularFrictionTimescale = new Vector3(1000, 1000, 1000);
                    vd._linearMotorTimescale = 1;
                    vd._linearMotorDecayTimescale = 60;
                    vd._angularMotorTimescale = 1;
                    vd._angularMotorDecayTimescale = 0.8f;
                    vd._VhoverHeight = 0;
                    vd._VhoverEfficiency = 0;
                    vd._VhoverTimescale = 1000;
                    vd._VehicleBuoyancy = 0;
                    vd._linearDeflectionEfficiency = 1;
                    vd._linearDeflectionTimescale = 2;
                    vd._angularDeflectionEfficiency = 0;
                    vd._angularDeflectionTimescale = 10;
                    vd._verticalAttractionEfficiency = 1f;
                    vd._verticalAttractionTimescale = 10f;
                    vd._bankingEfficiency = -0.2f;
                    vd._bankingMix = 1;
                    vd._bankingTimescale = 1;
                    vd._flags &= ~(VehicleFlag.HOVER_WATER_ONLY | VehicleFlag.HOVER_TERRAIN_ONLY | VehicleFlag.HOVER_GLOBAL_HEIGHT);
                    vd._flags |= VehicleFlag.NO_DEFLECTION_UP | VehicleFlag.LIMIT_ROLL_ONLY |
                                  VehicleFlag.LIMIT_MOTOR_UP | VehicleFlag.HOVER_UP_ONLY;
                    break;
                case Vehicle.TYPE_BOAT:
                    vd._linearFrictionTimescale = new Vector3(10, 3, 2);
                    vd._angularFrictionTimescale = new Vector3(10, 10, 10);
                    vd._linearMotorTimescale = 5;
                    vd._linearMotorDecayTimescale = 60;
                    vd._angularMotorTimescale = 4;
                    vd._angularMotorDecayTimescale = 4;
                    vd._VhoverHeight = 0;
                    vd._VhoverEfficiency = 0.5f;
                    vd._VhoverTimescale = 2;
                    vd._VehicleBuoyancy = 1;
                    vd._linearDeflectionEfficiency = 0.5f;
                    vd._linearDeflectionTimescale = 3;
                    vd._angularDeflectionEfficiency = 0.5f;
                    vd._angularDeflectionTimescale = 5;
                    vd._verticalAttractionEfficiency = 0.5f;
                    vd._verticalAttractionTimescale = 5f;
                    vd._bankingEfficiency = -0.3f;
                    vd._bankingMix = 0.8f;
                    vd._bankingTimescale = 1;
                    vd._flags &= ~(VehicleFlag.HOVER_TERRAIN_ONLY |
                            VehicleFlag.HOVER_GLOBAL_HEIGHT |
                            VehicleFlag.HOVER_UP_ONLY |
                            VehicleFlag.LIMIT_ROLL_ONLY);
                    vd._flags |= VehicleFlag.NO_DEFLECTION_UP |
                                  VehicleFlag.LIMIT_MOTOR_UP |
                                  VehicleFlag.HOVER_WATER_ONLY;
                    break;
                case Vehicle.TYPE_AIRPLANE:
                    vd._linearFrictionTimescale = new Vector3(200, 10, 5);
                    vd._angularFrictionTimescale = new Vector3(20, 20, 20);
                    vd._linearMotorTimescale = 2;
                    vd._linearMotorDecayTimescale = 60;
                    vd._angularMotorTimescale = 4;
                    vd._angularMotorDecayTimescale = 8;
                    vd._VhoverHeight = 0;
                    vd._VhoverEfficiency = 0.5f;
                    vd._VhoverTimescale = 1000;
                    vd._VehicleBuoyancy = 0;
                    vd._linearDeflectionEfficiency = 0.5f;
                    vd._linearDeflectionTimescale = 0.5f;
                    vd._angularDeflectionEfficiency = 1;
                    vd._angularDeflectionTimescale = 2;
                    vd._verticalAttractionEfficiency = 0.9f;
                    vd._verticalAttractionTimescale = 2f;
                    vd._bankingEfficiency = 1;
                    vd._bankingMix = 0.7f;
                    vd._bankingTimescale = 2;
                    vd._flags &= ~(VehicleFlag.HOVER_WATER_ONLY |
                        VehicleFlag.HOVER_TERRAIN_ONLY |
                        VehicleFlag.HOVER_GLOBAL_HEIGHT |
                        VehicleFlag.HOVER_UP_ONLY |
                        VehicleFlag.NO_DEFLECTION_UP |
                        VehicleFlag.LIMIT_MOTOR_UP);
                    vd._flags |= VehicleFlag.LIMIT_ROLL_ONLY;
                    break;
                case Vehicle.TYPE_BALLOON:
                    vd._linearFrictionTimescale = new Vector3(5, 5, 5);
                    vd._angularFrictionTimescale = new Vector3(10, 10, 10);
                    vd._linearMotorTimescale = 5;
                    vd._linearMotorDecayTimescale = 60;
                    vd._angularMotorTimescale = 6;
                    vd._angularMotorDecayTimescale = 10;
                    vd._VhoverHeight = 5;
                    vd._VhoverEfficiency = 0.8f;
                    vd._VhoverTimescale = 10;
                    vd._VehicleBuoyancy = 1;
                    vd._linearDeflectionEfficiency = 0;
                    vd._linearDeflectionTimescale = 5;
                    vd._angularDeflectionEfficiency = 0;
                    vd._angularDeflectionTimescale = 5;
                    vd._verticalAttractionEfficiency = 0f;
                    vd._verticalAttractionTimescale = 1000f;
                    vd._bankingEfficiency = 0;
                    vd._bankingMix = 0.7f;
                    vd._bankingTimescale = 5;
                    vd._flags &= ~(VehicleFlag.HOVER_WATER_ONLY |
                        VehicleFlag.HOVER_TERRAIN_ONLY |
                        VehicleFlag.HOVER_UP_ONLY |
                        VehicleFlag.NO_DEFLECTION_UP |
                        VehicleFlag.LIMIT_MOTOR_UP);
                    vd._flags |= VehicleFlag.LIMIT_ROLL_ONLY |
                                  VehicleFlag.HOVER_GLOBAL_HEIGHT;
                    break;
            }
        }
        public void SetVehicle(PhysicsActor ph)
        {
            if (ph == null)
                return;
            ph.SetVehicle(vd);
        }

        public bool CameraDecoupled
        {
            get
            {
                if((vd._flags & VehicleFlag.CAMERA_DECOUPLED) != 0)
                    return true;
                return false;
            }
        }

        private XmlTextWriter writer;

        private void XWint(string name, int i)
        {
            writer.WriteElementString(name, i.ToString());
        }

        private void XWfloat(string name, float f)
        {
            writer.WriteElementString(name, f.ToString(Culture.FormatProvider));
        }

        private void XWVector(string name, Vector3 vec)
        {
            writer.WriteStartElement(name);
            writer.WriteElementString("X", vec.X.ToString(Culture.FormatProvider));
            writer.WriteElementString("Y", vec.Y.ToString(Culture.FormatProvider));
            writer.WriteElementString("Z", vec.Z.ToString(Culture.FormatProvider));
            writer.WriteEndElement();
        }

        private void XWQuat(string name, Quaternion quat)
        {
            writer.WriteStartElement(name);
            writer.WriteElementString("X", quat.X.ToString(Culture.FormatProvider));
            writer.WriteElementString("Y", quat.Y.ToString(Culture.FormatProvider));
            writer.WriteElementString("Z", quat.Z.ToString(Culture.FormatProvider));
            writer.WriteElementString("W", quat.W.ToString(Culture.FormatProvider));
            writer.WriteEndElement();
        }

        public void ToXml2(XmlTextWriter twriter)
        {
            writer = twriter;
            writer.WriteStartElement("Vehicle");

            XWint("TYPE", (int)vd._type);
            XWint("FLAGS", (int)vd._flags);

            // Linear properties
            XWVector("LMDIR", vd._linearMotorDirection);
            XWVector("LMFTIME", vd._linearFrictionTimescale);
            XWfloat("LMDTIME", vd._linearMotorDecayTimescale);
            XWfloat("LMTIME", vd._linearMotorTimescale);
            XWVector("LMOFF", vd._linearMotorOffset);

            //Angular properties
            XWVector("AMDIR", vd._angularMotorDirection);
            XWfloat("AMTIME", vd._angularMotorTimescale);
            XWfloat("AMDTIME", vd._angularMotorDecayTimescale);
            XWVector("AMFTIME", vd._angularFrictionTimescale);

            //Deflection properties
            XWfloat("ADEFF", vd._angularDeflectionEfficiency);
            XWfloat("ADTIME", vd._angularDeflectionTimescale);
            XWfloat("LDEFF", vd._linearDeflectionEfficiency);
            XWfloat("LDTIME", vd._linearDeflectionTimescale);

            //Banking properties
            XWfloat("BEFF", vd._bankingEfficiency);
            XWfloat("BMIX", vd._bankingMix);
            XWfloat("BTIME", vd._bankingTimescale);

            //Hover and Buoyancy properties
            XWfloat("HHEI", vd._VhoverHeight);
            XWfloat("HEFF", vd._VhoverEfficiency);
            XWfloat("HTIME", vd._VhoverTimescale);
            XWfloat("VBUO", vd._VehicleBuoyancy);

            //Attractor properties
            XWfloat("VAEFF", vd._verticalAttractionEfficiency);
            XWfloat("VATIME", vd._verticalAttractionTimescale);

            XWQuat("REF_FRAME", vd._referenceFrame);

            writer.WriteEndElement();
            writer = null;
        }



        XmlReader reader;

        private int XRint()
        {
            return reader.ReadElementContentAsInt();
        }

        private float XRfloat()
        {
            return reader.ReadElementContentAsFloat();
        }

        public Vector3 XRvector()
        {
            Vector3 vec;
            reader.ReadStartElement();
            vec.X = reader.ReadElementContentAsFloat();
            vec.Y = reader.ReadElementContentAsFloat();
            vec.Z = reader.ReadElementContentAsFloat();
            reader.ReadEndElement();
            return vec;
        }

        public Quaternion XRquat()
        {
            Quaternion q;
            reader.ReadStartElement();
            q.X = reader.ReadElementContentAsFloat();
            q.Y = reader.ReadElementContentAsFloat();
            q.Z = reader.ReadElementContentAsFloat();
            q.W = reader.ReadElementContentAsFloat();
            reader.ReadEndElement();
            return q;
        }

        public static bool EReadProcessors(
            Dictionary<string, Action> processors,
            XmlReader xtr)
        {
            bool errors = false;

            string nodeName = string.Empty;
            while (xtr.NodeType != XmlNodeType.EndElement)
            {
                nodeName = xtr.Name;

                // _log.DebugFormat("[ExternalRepresentationUtils]: Processing: {0}", nodeName);

                Action p = null;
                if (processors.TryGetValue(xtr.Name, out p))
                {
                    // _log.DebugFormat("[ExternalRepresentationUtils]: Found {0} processor, nodeName);

                    try
                    {
                        p();
                    }
                    catch
                    {
                        errors = true;
                        if (xtr.NodeType == XmlNodeType.EndElement)
                            xtr.Read();
                    }
                }
                else
                {
                    // _log.DebugFormat("[LandDataSerializer]: caught unknown element {0}", nodeName);
                    xtr.ReadOuterXml(); // ignore
                }
            }

            return errors;
        }


        public string ToXml2()
        {
            using (StringWriter sw = new StringWriter())
            {
                using (XmlTextWriter xwriter = new XmlTextWriter(sw))
                {
                    ToXml2(xwriter);
                }

                return sw.ToString();
            }
        }

        public static SOPVehicle FromXml2(string text)
        {
            if (string.IsNullOrEmpty(text))
                return null;

            UTF8Encoding enc = new UTF8Encoding();
            MemoryStream ms = new MemoryStream(enc.GetBytes(text));
            XmlReader xreader = new XmlReader(ms);

            SOPVehicle v = new SOPVehicle();
            bool error;

            v.FromXml2(xreader, out error);

            xreader.Close();

            if (error)
            {
                v = null;
                return null;
            }
            return v;
        }

        public static SOPVehicle FromXml2(XmlReader reader)
        {
            SOPVehicle vehicle = new SOPVehicle();

            bool errors = false;

            vehicle.FromXml2(reader, out errors);
            if (errors)
                return null;

            return vehicle;
        }

        private void FromXml2(XmlReader _reader, out bool errors)
        {
            errors = false;
            reader = _reader;

            Dictionary<string, Action> _VehicleXmlProcessors
            = new Dictionary<string, Action>();

            _VehicleXmlProcessors.Add("TYPE", ProcessXR_type);
            _VehicleXmlProcessors.Add("FLAGS", ProcessXR_flags);

            // Linear properties
            _VehicleXmlProcessors.Add("LMDIR", ProcessXR_linearMotorDirection);
            _VehicleXmlProcessors.Add("LMFTIME", ProcessXR_linearFrictionTimescale);
            _VehicleXmlProcessors.Add("LMDTIME", ProcessXR_linearMotorDecayTimescale);
            _VehicleXmlProcessors.Add("LMTIME", ProcessXR_linearMotorTimescale);
            _VehicleXmlProcessors.Add("LMOFF", ProcessXR_linearMotorOffset);

            //Angular properties
            _VehicleXmlProcessors.Add("AMDIR", ProcessXR_angularMotorDirection);
            _VehicleXmlProcessors.Add("AMTIME", ProcessXR_angularMotorTimescale);
            _VehicleXmlProcessors.Add("AMDTIME", ProcessXR_angularMotorDecayTimescale);
            _VehicleXmlProcessors.Add("AMFTIME", ProcessXR_angularFrictionTimescale);

            //Deflection properties
            _VehicleXmlProcessors.Add("ADEFF", ProcessXR_angularDeflectionEfficiency);
            _VehicleXmlProcessors.Add("ADTIME", ProcessXR_angularDeflectionTimescale);
            _VehicleXmlProcessors.Add("LDEFF", ProcessXR_linearDeflectionEfficiency);
            _VehicleXmlProcessors.Add("LDTIME", ProcessXR_linearDeflectionTimescale);

            //Banking properties
            _VehicleXmlProcessors.Add("BEFF", ProcessXR_bankingEfficiency);
            _VehicleXmlProcessors.Add("BMIX", ProcessXR_bankingMix);
            _VehicleXmlProcessors.Add("BTIME", ProcessXR_bankingTimescale);

            //Hover and Buoyancy properties
            _VehicleXmlProcessors.Add("HHEI", ProcessXR_VhoverHeight);
            _VehicleXmlProcessors.Add("HEFF", ProcessXR_VhoverEfficiency);
            _VehicleXmlProcessors.Add("HTIME", ProcessXR_VhoverTimescale);

            _VehicleXmlProcessors.Add("VBUO", ProcessXR_VehicleBuoyancy);

            //Attractor properties
            _VehicleXmlProcessors.Add("VAEFF", ProcessXR_verticalAttractionEfficiency);
            _VehicleXmlProcessors.Add("VATIME", ProcessXR_verticalAttractionTimescale);

            _VehicleXmlProcessors.Add("REF_FRAME", ProcessXR_referenceFrame);

            vd = new VehicleData();

            reader.ReadStartElement("Vehicle", string.Empty);

            errors = EReadProcessors(
                _VehicleXmlProcessors,
                reader);

            reader.ReadEndElement();
            reader = null;
        }

        private void ProcessXR_type()
        {
            vd._type = (Vehicle)XRint();
        }
        private void ProcessXR_flags()
        {
            vd._flags = (VehicleFlag)XRint();
        }
        // Linear properties
        private void ProcessXR_linearMotorDirection()
        {
            vd._linearMotorDirection = XRvector();
        }

        private void ProcessXR_linearFrictionTimescale()
        {
            vd._linearFrictionTimescale = XRvector();
        }

        private void ProcessXR_linearMotorDecayTimescale()
        {
            vd._linearMotorDecayTimescale = XRfloat();
        }
        private void ProcessXR_linearMotorTimescale()
        {
            vd._linearMotorTimescale = XRfloat();
        }
        private void ProcessXR_linearMotorOffset()
        {
            vd._linearMotorOffset = XRvector();
        }


        //Angular properties
        private void ProcessXR_angularMotorDirection()
        {
            vd._angularMotorDirection = XRvector();
        }
        private void ProcessXR_angularMotorTimescale()
        {
            vd._angularMotorTimescale = XRfloat();
        }
        private void ProcessXR_angularMotorDecayTimescale()
        {
            vd._angularMotorDecayTimescale = XRfloat();
        }
        private void ProcessXR_angularFrictionTimescale()
        {
            vd._angularFrictionTimescale = XRvector();
        }

        //Deflection properties
        private void ProcessXR_angularDeflectionEfficiency()
        {
            vd._angularDeflectionEfficiency = XRfloat();
        }
        private void ProcessXR_angularDeflectionTimescale()
        {
            vd._angularDeflectionTimescale = XRfloat();
        }
        private void ProcessXR_linearDeflectionEfficiency()
        {
            vd._linearDeflectionEfficiency = XRfloat();
        }
        private void ProcessXR_linearDeflectionTimescale()
        {
            vd._linearDeflectionTimescale = XRfloat();
        }

        //Banking properties
        private void ProcessXR_bankingEfficiency()
        {
            vd._bankingEfficiency = XRfloat();
        }
        private void ProcessXR_bankingMix()
        {
            vd._bankingMix = XRfloat();
        }
        private void ProcessXR_bankingTimescale()
        {
            vd._bankingTimescale = XRfloat();
        }

        //Hover and Buoyancy properties
        private void ProcessXR_VhoverHeight()
        {
            vd._VhoverHeight = XRfloat();
        }
        private void ProcessXR_VhoverEfficiency()
        {
            vd._VhoverEfficiency = XRfloat();
        }
        private void ProcessXR_VhoverTimescale()
        {
            vd._VhoverTimescale = XRfloat();
        }

        private void ProcessXR_VehicleBuoyancy()
        {
            vd._VehicleBuoyancy = XRfloat();
        }

        //Attractor properties
        private void ProcessXR_verticalAttractionEfficiency()
        {
            vd._verticalAttractionEfficiency = XRfloat();
        }
        private void ProcessXR_verticalAttractionTimescale()
        {
            vd._verticalAttractionTimescale = XRfloat();
        }

        private void ProcessXR_referenceFrame()
        {
            vd._referenceFrame = XRquat();
        }
    }
}
