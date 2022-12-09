﻿using LeagueToolkit.Helpers.Extensions;
using System.Collections.Generic;
using System.IO;
using System.Numerics;

namespace LeagueToolkit.Helpers.Structures
{
    /// <summary>
    /// Represents an Axis-Aligned Bounding Box
    /// </summary>
    public class R3DBox
    {
        public Vector3 Min { get; private set; }
        public Vector3 Max { get; private set; }

        /// <summary>
        /// Initializes a new <see cref="R3DBox"/> instance
        /// </summary>
        /// <param name="min"></param>
        /// <param name="max"></param>
        public R3DBox(Vector3 min, Vector3 max)
        {
            this.Min = min;
            this.Max = max;
        }

        /// <summary>
        /// Initializes a new <see cref="R3DBox"/> instance from a <see cref="BinaryReader"/>
        /// </summary>
        /// <param name="br">The <see cref="BinaryReader"/> to read from</param>
        public R3DBox(BinaryReader br)
        {
            this.Min = br.ReadVector3();
            this.Max = br.ReadVector3();
        }

        /// <summary>
        /// Creates a clone of a <see cref="R3DBox"/> object
        /// </summary>
        /// <param name="r3dBox">The <see cref="R3DBox"/> to clone</param>
        public R3DBox(R3DBox r3dBox)
        {
            this.Min = r3dBox.Min;
            this.Max = r3dBox.Max;
        }

        public static R3DBox FromVertices(IEnumerable<Vector3> vertices)
        {
            Vector3 min = new(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 max = new(float.MinValue, float.MinValue, float.MinValue);

            foreach (Vector3 vertex in vertices)
            {
                if (min.X > vertex.X)
                {
                    min.X = vertex.X;
                }
                if (min.Y > vertex.Y)
                {
                    min.Y = vertex.Y;
                }
                if (min.Z > vertex.Z)
                {
                    min.Z = vertex.Z;
                }
                if (max.X < vertex.X)
                {
                    max.X = vertex.X;
                }
                if (max.Y < vertex.Y)
                {
                    max.Y = vertex.Y;
                }
                if (max.Z < vertex.Z)
                {
                    max.Z = vertex.Z;
                }
            }

            return new(min, max);
        }

        /// <summary>
        /// Writes this <see cref="R3DBox"/> into a <see cref="BinaryWriter"/>
        /// </summary>
        /// <param name="bw"></param>
        public void Write(BinaryWriter bw)
        {
            bw.WriteVector3(this.Min);
            bw.WriteVector3(this.Max);
        }

        /// <summary>
        /// Calculates the proportions of this <see cref="R3DBox"/>
        /// </summary>
        public Vector3 GetProportions()
        {
            return this.Max - this.Min;
        }

        public Vector3 GetCentralPoint()
        {
            return new Vector3(
                0.5f * (this.Min.X + this.Max.X),
                0.5f * (this.Min.Y + this.Max.Y),
                0.5f * (this.Min.Z + this.Max.Z)
            );
        }

        public R3DSphere GetBoundingSphere()
        {
            Vector3 centralPoint = GetCentralPoint();

            return new R3DSphere(centralPoint, Vector3.Distance(centralPoint, this.Max));
        }

        /// <summary>
        /// Determines wheter this <see cref="R3DBox"/> contains the <see cref="Vector3"/> <paramref name="point"/>
        /// </summary>
        /// <param name="point">The containing point</param>
        /// <returns>Wheter this <see cref="R3DBox"/> contains the <see cref="Vector3"/> <paramref name="point"/></returns>
        public bool ContainsPoint(Vector3 point)
        {
            return (point.X >= this.Min.X)
                && (point.X <= this.Max.X)
                && (point.Y >= this.Min.Y)
                && (point.Y <= this.Max.Y)
                && (point.Z >= this.Min.Z)
                && (point.Z <= this.Max.Z);
        }
    }
}
