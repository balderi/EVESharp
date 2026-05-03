using System;
using System.Runtime.InteropServices;

namespace EVESharp.Destiny;

[StructLayout (LayoutKind.Sequential, Pack = 1)]
public struct Vector3
{
    public double X;
    public double Y;
    public double Z;

    public double DistanceSquare (Vector3 b)
    {
        return Math.Pow (b.X - this.X, 2) + Math.Pow (b.Y - this.Y, 2) + Math.Pow (b.Z - this.Z, 2);
    }

    public double Distance (Vector3 b)
    {
        return Math.Sqrt (this.DistanceSquare (b));
    }

    public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);

    public Vector3 Normalize()
    {
        double len = Length;
        if (len < 1e-12) return default (Vector3);
        return new Vector3 { X = X / len, Y = Y / len, Z = Z / len };
    }

    public static Vector3 operator +(Vector3 a, Vector3 b)
        => new Vector3 { X = a.X + b.X, Y = a.Y + b.Y, Z = a.Z + b.Z };

    public static Vector3 operator -(Vector3 a, Vector3 b)
        => new Vector3 { X = a.X - b.X, Y = a.Y - b.Y, Z = a.Z - b.Z };

    public static Vector3 operator *(Vector3 v, double s)
        => new Vector3 { X = v.X * s, Y = v.Y * s, Z = v.Z * s };

    public static Vector3 operator *(double s, Vector3 v) => v * s;

    public override string ToString ()
    {
        return "(" + Math.Round (this.X) + ", " + Math.Round (this.Y) + ", " + Math.Round (this.Z) + ")";
    }
}