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

namespace OpenSim.Region.PhysicsModule.Meshing
{
    public class Mesh : IMesh
    {
        private Dictionary<Vertex, int> _vertices;
        private List<Triangle> _triangles;
        GCHandle _pinnedVertexes;
        GCHandle _pinnedIndex;
        IntPtr _verticesPtr = IntPtr.Zero;
        int _vertexCount = 0;
        IntPtr _indicesPtr = IntPtr.Zero;
        int _indexCount = 0;
        public float[] _normals;
        Vector3 _centroid;
        int _centroidDiv;

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

        public Mesh()
        {
            vertexcomp vcomp = new vertexcomp();

            _vertices = new Dictionary<Vertex, int>(vcomp);
            _triangles = new List<Triangle>();
            _centroid = Vector3.Zero;
            _centroidDiv = 0;
        }

        public Mesh Clone()
        {
            Mesh result = new Mesh();

            foreach (Triangle t in _triangles)
            {
                result.Add(new Triangle(t.v1.Clone(), t.v2.Clone(), t.v3.Clone()));
            }
            result._centroid = _centroid;
            result._centroidDiv = _centroidDiv;
            return result;
        }

        public void Add(Triangle triangle)
        {
            if (_pinnedIndex.IsAllocated || _pinnedVertexes.IsAllocated || _indicesPtr != IntPtr.Zero || _verticesPtr != IntPtr.Zero)
                throw new NotSupportedException("Attempt to Add to a pinned Mesh");
            // If a vertex of the triangle is not yet in the vertices list,
            // add it and set its index to the current index count
            // vertex == seems broken
            // skip colapsed triangles
            if (triangle.v1.X == triangle.v2.X && triangle.v1.Y == triangle.v2.Y && triangle.v1.Z == triangle.v2.Z
                || triangle.v1.X == triangle.v3.X && triangle.v1.Y == triangle.v3.Y && triangle.v1.Z == triangle.v3.Z
                || triangle.v2.X == triangle.v3.X && triangle.v2.Y == triangle.v3.Y && triangle.v2.Z == triangle.v3.Z
                )
            {
                return;
            }

            if (_vertices.Count == 0)
            {
                _centroidDiv = 0;
                _centroid = Vector3.Zero;
            }

            if (!_vertices.ContainsKey(triangle.v1))
            {
                _vertices[triangle.v1] = _vertices.Count;
                _centroid.X += triangle.v1.X;
                _centroid.Y += triangle.v1.Y;
                _centroid.Z += triangle.v1.Z;
                _centroidDiv++;
            }
            if (!_vertices.ContainsKey(triangle.v2))
            {
                _vertices[triangle.v2] = _vertices.Count;
                _centroid.X += triangle.v2.X;
                _centroid.Y += triangle.v2.Y;
                _centroid.Z += triangle.v2.Z;
                _centroidDiv++;
            }
            if (!_vertices.ContainsKey(triangle.v3))
            {
                _vertices[triangle.v3] = _vertices.Count;
                _centroid.X += triangle.v3.X;
                _centroid.Y += triangle.v3.Y;
                _centroid.Z += triangle.v3.Z;
                _centroidDiv++;
            }
            _triangles.Add(triangle);
        }

        public Vector3 GetCentroid()
        {
            if (_centroidDiv > 0)
                return new Vector3(_centroid.X / _centroidDiv, _centroid.Y / _centroidDiv, _centroid.Z / _centroidDiv);
            else
                return Vector3.Zero;
        }

        // not functional
        public Vector3 GetOBB()
        {
            return new Vector3(0.5f, 0.5f, 0.5f);
        }

        public void CalcNormals()
        {
            int iTriangles = _triangles.Count;

            this._normals = new float[iTriangles * 3];

            int i = 0;
            foreach (Triangle t in _triangles)
            {
                float ux, uy, uz;
                float vx, vy, vz;
                float wx, wy, wz;

                ux = t.v1.X;
                uy = t.v1.Y;
                uz = t.v1.Z;

                vx = t.v2.X;
                vy = t.v2.Y;
                vz = t.v2.Z;

                wx = t.v3.X;
                wy = t.v3.Y;
                wz = t.v3.Z;


                // Vectors for edges
                float e1x, e1y, e1z;
                float e2x, e2y, e2z;

                e1x = ux - vx;
                e1y = uy - vy;
                e1z = uz - vz;

                e2x = ux - wx;
                e2y = uy - wy;
                e2z = uz - wz;


                // Cross product for normal
                float nx, ny, nz;
                nx = e1y * e2z - e1z * e2y;
                ny = e1z * e2x - e1x * e2z;
                nz = e1x * e2y - e1y * e2x;

                // Length
                float l = (float)Math.Sqrt(nx * nx + ny * ny + nz * nz);
                float lReciprocal = 1.0f / l;

                // Normalized "normal"
                //nx /= l;
                //ny /= l;
                //nz /= l;

                _normals[i] = nx * lReciprocal;
                _normals[i + 1] = ny * lReciprocal;
                _normals[i + 2] = nz * lReciprocal;

                i += 3;
            }
        }

        public List<Vector3> getVertexList()
        {
            List<Vector3> result = new List<Vector3>();
            foreach (Vertex v in _vertices.Keys)
            {
                result.Add(new Vector3(v.X, v.Y, v.Z));
            }
            return result;
        }

        public float[] getVertexListAsFloat()
        {
            if (_vertices == null)
                throw new NotSupportedException();
            float[] result = new float[_vertices.Count * 3];
            foreach (KeyValuePair<Vertex, int> kvp in _vertices)
            {
                Vertex v = kvp.Key;
                int i = kvp.Value;
                result[3 * i + 0] = v.X;
                result[3 * i + 1] = v.Y;
                result[3 * i + 2] = v.Z;
            }
            return result;
        }

        public float[] getVertexListAsFloatLocked()
        {
            if (_pinnedVertexes.IsAllocated)
                return (float[])_pinnedVertexes.Target;

            float[] result = getVertexListAsFloat();
            _pinnedVertexes = GCHandle.Alloc(result, GCHandleType.Pinned);
            // Inform the garbage collector of this unmanaged allocation so it can schedule
            // the next GC round more intelligently
            GC.AddMemoryPressure(Buffer.ByteLength(result));

            return result;
        }

        public void getVertexListAsPtrToFloatArray(out IntPtr vertices, out int vertexStride, out int vertexCount)
        {
            // A vertex is 3 floats

            vertexStride = 3 * sizeof(float);

            // If there isn't an unmanaged array allocated yet, do it now
            if (_verticesPtr == IntPtr.Zero)
            {
                float[] vertexList = getVertexListAsFloat();
                // Each vertex is 3 elements (floats)
                _vertexCount = vertexList.Length / 3;
                int byteCount = _vertexCount * vertexStride;
                _verticesPtr = System.Runtime.InteropServices.Marshal.AllocHGlobal(byteCount);
                System.Runtime.InteropServices.Marshal.Copy(vertexList, 0, _verticesPtr, _vertexCount * 3);
            }
            vertices = _verticesPtr;
            vertexCount = _vertexCount;
        }

        public int[] getIndexListAsInt()
        {
            if (_triangles == null)
                throw new NotSupportedException();
            int[] result = new int[_triangles.Count * 3];
            for (int i = 0; i < _triangles.Count; i++)
            {
                Triangle t = _triangles[i];
                result[3 * i + 0] = _vertices[t.v1];
                result[3 * i + 1] = _vertices[t.v2];
                result[3 * i + 2] = _vertices[t.v3];
            }
            return result;
        }

        /// <summary>
        /// creates a list of index values that defines triangle faces. THIS METHOD FREES ALL NON-PINNED MESH DATA
        /// </summary>
        /// <returns></returns>
        public int[] getIndexListAsIntLocked()
        {
            if (_pinnedIndex.IsAllocated)
                return (int[])_pinnedIndex.Target;

            int[] result = getIndexListAsInt();
            _pinnedIndex = GCHandle.Alloc(result, GCHandleType.Pinned);
            // Inform the garbage collector of this unmanaged allocation so it can schedule
            // the next GC round more intelligently
            GC.AddMemoryPressure(Buffer.ByteLength(result));

            return result;
        }

        public void getIndexListAsPtrToIntArray(out IntPtr indices, out int triStride, out int indexCount)
        {
            // If there isn't an unmanaged array allocated yet, do it now
            if (_indicesPtr == IntPtr.Zero)
            {
                int[] indexList = getIndexListAsInt();
                _indexCount = indexList.Length;
                int byteCount = _indexCount * sizeof(int);
                _indicesPtr = System.Runtime.InteropServices.Marshal.AllocHGlobal(byteCount);
                System.Runtime.InteropServices.Marshal.Copy(indexList, 0, _indicesPtr, _indexCount);
            }
            // A triangle is 3 ints (indices)
            triStride = 3 * sizeof(int);
            indices = _indicesPtr;
            indexCount = _indexCount;
        }

        public void releasePinned()
        {
            if (_pinnedVertexes.IsAllocated)
                _pinnedVertexes.Free();
            if (_pinnedIndex.IsAllocated)
                _pinnedIndex.Free();
            if (_verticesPtr != IntPtr.Zero)
            {
                System.Runtime.InteropServices.Marshal.FreeHGlobal(_verticesPtr);
                _verticesPtr = IntPtr.Zero;
            }
            if (_indicesPtr != IntPtr.Zero)
            {
                System.Runtime.InteropServices.Marshal.FreeHGlobal(_indicesPtr);
                _indicesPtr = IntPtr.Zero;
            }
        }

        /// <summary>
        /// frees up the source mesh data to minimize memory - call this method after calling get*Locked() functions
        /// </summary>
        public void releaseSourceMeshData()
        {
            _triangles = null;
            _vertices = null;
        }

        public void Append(IMesh newMesh)
        {
            if (_pinnedIndex.IsAllocated || _pinnedVertexes.IsAllocated || _indicesPtr != IntPtr.Zero || _verticesPtr != IntPtr.Zero)
                throw new NotSupportedException("Attempt to Append to a pinned Mesh");

            if (!(newMesh is Mesh))
                return;

            foreach (Triangle t in ((Mesh)newMesh)._triangles)
                Add(t);
        }

        // Do a linear transformation of  mesh.
        public void TransformLinear(float[,] matrix, float[] offset)
        {
            if (_pinnedIndex.IsAllocated || _pinnedVertexes.IsAllocated || _indicesPtr != IntPtr.Zero || _verticesPtr != IntPtr.Zero)
                throw new NotSupportedException("Attempt to TransformLinear a pinned Mesh");

            foreach (Vertex v in _vertices.Keys)
            {
                if (v == null)
                    continue;
                float x, y, z;
                x = v.X*matrix[0, 0] + v.Y*matrix[1, 0] + v.Z*matrix[2, 0];
                y = v.X*matrix[0, 1] + v.Y*matrix[1, 1] + v.Z*matrix[2, 1];
                z = v.X*matrix[0, 2] + v.Y*matrix[1, 2] + v.Z*matrix[2, 2];
                v.X = x + offset[0];
                v.Y = y + offset[1];
                v.Z = z + offset[2];
            }
        }

        public void DumpRaw(string path, string name, string title)
        {
            if (path == null)
                return;
            string fileName = name + "_" + title + ".raw";
            string completePath = System.IO.Path.Combine(path, fileName);
            StreamWriter sw = new StreamWriter(completePath);
            foreach (Triangle t in _triangles)
            {
                string s = t.ToStringRaw();
                sw.WriteLine(s);
            }
            sw.Close();
        }

        public void TrimExcess()
        {
            _triangles.TrimExcess();
        }
    }
}
