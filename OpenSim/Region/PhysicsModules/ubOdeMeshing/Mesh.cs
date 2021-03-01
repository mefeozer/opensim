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
using System.IO;
using System.Runtime.InteropServices;
using OpenSim.Region.PhysicsModules.SharedBase;
using OpenMetaverse;

namespace OpenSim.Region.PhysicsModule.ubODEMeshing
{
    public class MeshBuildingData
    {
        private class vertexcomp : IEqualityComparer<Vertex>
        {
            public bool Equals(Vertex v1, Vertex v2)
            {
                if (v1.X == v2.X && v1.Y == v2.Y && v1.Z == v2.Z)
                    return true;
                else
                    return false;
            }
            public int GetHashCode(Vertex v)
            {
                int a = v.X.GetHashCode();
                int b = v.Y.GetHashCode();
                int c = v.Z.GetHashCode();
                return (a << 16) ^ (b << 8) ^ c;
            }
        }

        public Dictionary<Vertex, int> _vertices;
        public List<Triangle> _triangles;
        public float _obbXmin;
        public float _obbXmax;
        public float _obbYmin;
        public float _obbYmax;
        public float _obbZmin;
        public float _obbZmax;
        public Vector3 _centroid;
        public int _centroidDiv;

        public  MeshBuildingData()
        {
            vertexcomp vcomp = new vertexcomp();
            _vertices = new Dictionary<Vertex, int>(vcomp);
            _triangles = new List<Triangle>();
            _centroid = Vector3.Zero;
            _centroidDiv = 0;
            _obbXmin = float.MaxValue;
            _obbXmax = float.MinValue;
            _obbYmin = float.MaxValue;
            _obbYmax = float.MinValue;
            _obbZmin = float.MaxValue;
            _obbZmax = float.MinValue;
        }
    }

    [Serializable()]
    public class Mesh : IMesh
    {
        float[] vertices;
        int[] indexes;
        Vector3 _obb;
        Vector3 _obboffset;
        [NonSerialized()]
        MeshBuildingData _bdata;
        [NonSerialized()]
        GCHandle vhandler;
        [NonSerialized()]
        GCHandle ihandler;
        [NonSerialized()]
        IntPtr _verticesPtr = IntPtr.Zero;
        [NonSerialized()]
        IntPtr _indicesPtr = IntPtr.Zero;
        [NonSerialized()]
        int _vertexCount = 0;
        [NonSerialized()]
        int _indexCount = 0;

        public int RefCount { get; set; }
        public AMeshKey Key { get; set; }

        public Mesh(bool forbuild)
        {
            if(forbuild)
                _bdata = new MeshBuildingData();
            _obb = new Vector3(0.5f, 0.5f, 0.5f);
            _obboffset = Vector3.Zero;
        }

        public Mesh Scale(Vector3 scale)
        {
            if (_verticesPtr == null || _indicesPtr == null)
                return null;

            Mesh result = new Mesh(false);

            float x = scale.X;
            float y = scale.Y;
            float z = scale.Z;

            float tmp;
            tmp = _obb.X * x;
            if(tmp < 0.0005f)
                tmp = 0.0005f;
            result._obb.X = tmp;

            tmp =  _obb.Y * y;
            if(tmp < 0.0005f)
                tmp = 0.0005f;
            result._obb.Y = tmp;

            tmp =  _obb.Z * z;
            if(tmp < 0.0005f)
                tmp = 0.0005f;
            result._obb.Z = tmp;

            result._obboffset.X = _obboffset.X * x;
            result._obboffset.Y = _obboffset.Y * y;
            result._obboffset.Z = _obboffset.Z * z;

            result.vertices = new float[vertices.Length];
            int j = 0;
            for (int i = 0; i < _vertexCount; i++)
            {
                result.vertices[j] = vertices[j] * x;
                j++;
                result.vertices[j] = vertices[j] * y;
                j++;
                result.vertices[j] = vertices[j] * z;
                j++;
            }

            result.indexes = new int[indexes.Length];
            indexes.CopyTo(result.indexes,0);

            result.pinMemory();

            return result;
        }

        public Mesh Clone()
        {
            Mesh result = new Mesh(false);

            if (_bdata != null)
            {
                result._bdata = new MeshBuildingData();
                foreach (Triangle t in _bdata._triangles)
                {
                    result.Add(new Triangle(t.v1.Clone(), t.v2.Clone(), t.v3.Clone()));
                }
                result._bdata._centroid = _bdata._centroid;
                result._bdata._centroidDiv = _bdata._centroidDiv;
                result._bdata._obbXmin = _bdata._obbXmin;
                result._bdata._obbXmax = _bdata._obbXmax;
                result._bdata._obbYmin = _bdata._obbYmin;
                result._bdata._obbYmax = _bdata._obbYmax;
                result._bdata._obbZmin = _bdata._obbZmin;
                result._bdata._obbZmax = _bdata._obbZmax;
            }
            result._obb = _obb;
            result._obboffset = _obboffset;
            return result;
        }

        public void addVertexLStats(Vertex v)
        {
            float x = v.X;
            float y = v.Y;
            float z = v.Z;

            _bdata._centroid.X += x;
            _bdata._centroid.Y += y;
            _bdata._centroid.Z += z;
            _bdata._centroidDiv++;

            if (x > _bdata._obbXmax)
                _bdata._obbXmax = x;
            if (x < _bdata._obbXmin)
                _bdata._obbXmin = x;

            if (y > _bdata._obbYmax)
                _bdata._obbYmax = y;
            if (y < _bdata._obbYmin)
                _bdata._obbYmin = y;

            if (z > _bdata._obbZmax)
                _bdata._obbZmax = z;
            if (z < _bdata._obbZmin)
                _bdata._obbZmin = z;
        }

        public void Add(Triangle triangle)
        {
            if (_indicesPtr != IntPtr.Zero || _verticesPtr != IntPtr.Zero)
                throw new NotSupportedException("Attempt to Add to a pinned Mesh");


            triangle.v1.X = (float)Math.Round(triangle.v1.X, 6);
            triangle.v1.Y = (float)Math.Round(triangle.v1.Y, 6);
            triangle.v1.Z = (float)Math.Round(triangle.v1.Z, 6);
            triangle.v2.X = (float)Math.Round(triangle.v2.X, 6);
            triangle.v2.Y = (float)Math.Round(triangle.v2.Y, 6);
            triangle.v2.Z = (float)Math.Round(triangle.v2.Z, 6);
            triangle.v3.X = (float)Math.Round(triangle.v3.X, 6);
            triangle.v3.Y = (float)Math.Round(triangle.v3.Y, 6);
            triangle.v3.Z = (float)Math.Round(triangle.v3.Z, 6);

            if (triangle.v1.X == triangle.v2.X && triangle.v1.Y == triangle.v2.Y && triangle.v1.Z ==
                triangle.v2.Z
                || triangle.v1.X == triangle.v3.X && triangle.v1.Y == triangle.v3.Y && triangle.v1.Z ==
                triangle.v3.Z
                || triangle.v2.X == triangle.v3.X && triangle.v2.Y == triangle.v3.Y && triangle.v2.Z ==
                triangle.v3.Z
                )
            {
                return;
            }

            if (_bdata._vertices.Count == 0)
            {
                _bdata._centroidDiv = 0;
                _bdata._centroid = Vector3.Zero;
            }

            if (!_bdata._vertices.ContainsKey(triangle.v1))
            {
                _bdata._vertices[triangle.v1] = _bdata._vertices.Count;
                addVertexLStats(triangle.v1);
            }
            if (!_bdata._vertices.ContainsKey(triangle.v2))
            {
                _bdata._vertices[triangle.v2] = _bdata._vertices.Count;
                addVertexLStats(triangle.v2);
            }
            if (!_bdata._vertices.ContainsKey(triangle.v3))
            {
                _bdata._vertices[triangle.v3] = _bdata._vertices.Count;
                addVertexLStats(triangle.v3);
            }
            _bdata._triangles.Add(triangle);
        }

        public Vector3 GetCentroid()
        {
            return _obboffset;

        }

        public Vector3 GetOBB()
        {
            return _obb;
/*
            float x, y, z;
            if (_bdata._centroidDiv > 0)
            {
                x = (_bdata._obbXmax - _bdata._obbXmin) * 0.5f;
                y = (_bdata._obbYmax - _bdata._obbYmin) * 0.5f;
                z = (_bdata._obbZmax - _bdata._obbZmin) * 0.5f;
            }
            else // ??
            {
                x = 0.5f;
                y = 0.5f;
                z = 0.5f;
            }
            return new Vector3(x, y, z);
*/
        }

        public int numberVertices()
        {
            return _bdata._vertices.Count;
        }

        public int numberTriangles()
        {
            return _bdata._triangles.Count;
        }

        public List<Vector3> getVertexList()
        {
            List<Vector3> result = new List<Vector3>();
            foreach (Vertex v in _bdata._vertices.Keys)
            {
                result.Add(new Vector3(v.X, v.Y, v.Z));
            }
            return result;
        }

        public float[] getVertexListAsFloat()
        {
            if (_bdata._vertices == null)
                throw new NotSupportedException();
            float[] result = new float[_bdata._vertices.Count * 3];
            int k = 0;
            foreach (KeyValuePair<Vertex, int> kvp in _bdata._vertices)
            {
                Vertex v = kvp.Key;
                int i = kvp.Value;
                k = 3 * i;
                result[k] = v.X;
                result[k + 1] = v.Y;
                result[k + 2] = v.Z;
            }
            return result;
        }

        public float[] getVertexListAsFloatLocked()
        {
            return null;
        }

        public void getVertexListAsPtrToFloatArray(out IntPtr _vertices, out int vertexStride, out int vertexCount)
        {
            // A vertex is 3 floats
            vertexStride = 3 * sizeof(float);

            // If there isn't an unmanaged array allocated yet, do it now
            if (_verticesPtr == IntPtr.Zero && _bdata != null)
            {
                vertices = getVertexListAsFloat();
                // Each vertex is 3 elements (floats)
                _vertexCount = vertices.Length / 3;
                vhandler = GCHandle.Alloc(vertices, GCHandleType.Pinned);
                _verticesPtr = vhandler.AddrOfPinnedObject();
                GC.AddMemoryPressure(Buffer.ByteLength(vertices));
            }
            _vertices = _verticesPtr;
            vertexCount = _vertexCount;
        }

        public int[] getIndexListAsInt()
        {
            if (_bdata._triangles == null)
                throw new NotSupportedException();
            int[] result = new int[_bdata._triangles.Count * 3];
            int k;
            for (int i = 0; i < _bdata._triangles.Count; i++)
            {
                k= 3 * i;
                Triangle t = _bdata._triangles[i];
                result[k] = _bdata._vertices[t.v1];
                result[k + 1] = _bdata._vertices[t.v2];
                result[k + 2] = _bdata._vertices[t.v3];
            }
            return result;
        }

        /// <summary>
        /// creates a list of index values that defines triangle faces. THIS METHOD FREES ALL NON-PINNED MESH DATA
        /// </summary>
        /// <returns></returns>
        public int[] getIndexListAsIntLocked()
        {
            return null;
        }

        public void getIndexListAsPtrToIntArray(out IntPtr indices, out int triStride, out int indexCount)
        {
            // If there isn't an unmanaged array allocated yet, do it now
            if (_indicesPtr == IntPtr.Zero && _bdata != null)
            {
                indexes = getIndexListAsInt();
                _indexCount = indexes.Length;
                ihandler = GCHandle.Alloc(indexes, GCHandleType.Pinned);
                _indicesPtr = ihandler.AddrOfPinnedObject();
                GC.AddMemoryPressure(Buffer.ByteLength(indexes));
            }
            // A triangle is 3 ints (indices)
            triStride = 3 * sizeof(int);
            indices = _indicesPtr;
            indexCount = _indexCount;
        }

        public void releasePinned()
        {
            if (_verticesPtr != IntPtr.Zero)
            {
                vhandler.Free();
                GC.RemoveMemoryPressure(Buffer.ByteLength(vertices));
                vertices = null;
                _verticesPtr = IntPtr.Zero;
            }
            if (_indicesPtr != IntPtr.Zero)
            {
                ihandler.Free();
                GC.RemoveMemoryPressure(Buffer.ByteLength(indexes));
                indexes = null;
                _indicesPtr = IntPtr.Zero;
            }
        }

        /// <summary>
        /// frees up the source mesh data to minimize memory - call this method after calling get*Locked() functions
        /// </summary>
        public void releaseSourceMeshData()
        {
            if (_bdata != null)
            {
                _bdata._triangles = null;
                _bdata._vertices = null;
            }
        }

        public void releaseBuildingMeshData()
        {
            if (_bdata != null)
            {
                _bdata._triangles = null;
                _bdata._vertices = null;
                _bdata = null;
            }
        }

        public void Append(IMesh newMesh)
        {
            if (_indicesPtr != IntPtr.Zero || _verticesPtr != IntPtr.Zero)
                throw new NotSupportedException("Attempt to Append to a pinned Mesh");

            if (!(newMesh is Mesh))
                return;

            foreach (Triangle t in ((Mesh)newMesh)._bdata._triangles)
                Add(t);
        }

        // Do a linear transformation of  mesh.
        public void TransformLinear(float[,] matrix, float[] offset)
        {
            if (_indicesPtr != IntPtr.Zero || _verticesPtr != IntPtr.Zero)
                throw new NotSupportedException("Attempt to TransformLinear a pinned Mesh");

            foreach (Vertex v in _bdata._vertices.Keys)
            {
                if (v == null)
                    continue;
                float x, y, z;
                x = v.X * matrix[0, 0] + v.Y * matrix[1, 0] + v.Z * matrix[2, 0];
                y = v.X * matrix[0, 1] + v.Y * matrix[1, 1] + v.Z * matrix[2, 1];
                z = v.X * matrix[0, 2] + v.Y * matrix[1, 2] + v.Z * matrix[2, 2];
                v.X = x + offset[0];
                v.Y = y + offset[1];
                v.Z = z + offset[2];
            }
        }

        public void DumpRaw(string path, string name, string title)
        {
            if (path == null)
                return;
            if (_bdata == null)
                return;
            string fileName = name + "_" + title + ".raw";
            string completePath = System.IO.Path.Combine(path, fileName);
            StreamWriter sw = new StreamWriter(completePath);
            foreach (Triangle t in _bdata._triangles)
            {
                string s = t.ToStringRaw();
                sw.WriteLine(s);
            }
            sw.Close();
        }

        public void TrimExcess()
        {
            _bdata._triangles.TrimExcess();
        }

        public void pinMemory()
        {
            _vertexCount = vertices.Length / 3;
            vhandler = GCHandle.Alloc(vertices, GCHandleType.Pinned);
            _verticesPtr = vhandler.AddrOfPinnedObject();
            GC.AddMemoryPressure(Buffer.ByteLength(vertices));

            _indexCount = indexes.Length;
            ihandler = GCHandle.Alloc(indexes, GCHandleType.Pinned);
            _indicesPtr = ihandler.AddrOfPinnedObject();
            GC.AddMemoryPressure(Buffer.ByteLength(indexes));
        }

        public void PrepForOde()
        {
            // If there isn't an unmanaged array allocated yet, do it now
            if (_verticesPtr == IntPtr.Zero)
                vertices = getVertexListAsFloat();

            // If there isn't an unmanaged array allocated yet, do it now
            if (_indicesPtr == IntPtr.Zero)
                indexes = getIndexListAsInt();

            float x, y, z;

            if (_bdata._centroidDiv > 0)
            {
                _obboffset = new Vector3(_bdata._centroid.X / _bdata._centroidDiv, _bdata._centroid.Y / _bdata._centroidDiv, _bdata._centroid.Z / _bdata._centroidDiv);
                x = (_bdata._obbXmax - _bdata._obbXmin) * 0.5f;
                if(x < 0.0005f)
                    x = 0.0005f;
                y = (_bdata._obbYmax - _bdata._obbYmin) * 0.5f;
                if(y < 0.0005f)
                    y = 0.0005f;
                z = (_bdata._obbZmax - _bdata._obbZmin) * 0.5f;
                if(z < 0.0005f)
                    z = 0.0005f;
            }

            else
            {
                _obboffset = Vector3.Zero;
                x = 0.5f;
                y = 0.5f;
                z = 0.5f;
            }

            _obb = new Vector3(x, y, z);

            releaseBuildingMeshData();
            pinMemory();
        }

        public bool ToStream(Stream st)
        {
            if (_indicesPtr == IntPtr.Zero || _verticesPtr == IntPtr.Zero)
                return false;

            bool ok = true;

            try
            {
                using(BinaryWriter bw = new BinaryWriter(st))
                {
                    bw.Write(_vertexCount);
                    bw.Write(_indexCount);

                    for (int i = 0; i < 3 * _vertexCount; i++)
                        bw.Write(vertices[i]);
                    for (int i = 0; i < _indexCount; i++)
                        bw.Write(indexes[i]);
                    bw.Write(_obb.X);
                    bw.Write(_obb.Y);
                    bw.Write(_obb.Z);
                    bw.Write(_obboffset.X);
                    bw.Write(_obboffset.Y);
                    bw.Write(_obboffset.Z);
                    bw.Flush();
                    bw.Close();
               }
            }
            catch
            {
                ok = false;
            }

            return ok;
        }

        public static Mesh FromStream(Stream st, AMeshKey key)
        {
            Mesh mesh = new Mesh(false);

            bool ok = true;
            try
            {
                using(BinaryReader br = new BinaryReader(st))
                {
                    mesh._vertexCount = br.ReadInt32();
                    mesh._indexCount = br.ReadInt32();

                    int n = 3 * mesh._vertexCount;
                    mesh.vertices = new float[n];
                    for (int i = 0; i < n; i++)
                        mesh.vertices[i] = br.ReadSingle();

                    mesh.indexes = new int[mesh._indexCount];
                    for (int i = 0; i < mesh._indexCount; i++)
                        mesh.indexes[i] = br.ReadInt32();

                    mesh._obb.X = br.ReadSingle();
                    mesh._obb.Y = br.ReadSingle();
                    mesh._obb.Z = br.ReadSingle();

                    mesh._obboffset.X = br.ReadSingle();
                    mesh._obboffset.Y = br.ReadSingle();
                    mesh._obboffset.Z = br.ReadSingle();
                }
            }
            catch
            {
                ok = false;
            }

            if (ok)
            {
                mesh.pinMemory();

                mesh.Key = key;
                mesh.RefCount = 1;

                return mesh;
            }

            mesh.vertices = null;
            mesh.indexes = null;
            return null;
        }
    }
}
