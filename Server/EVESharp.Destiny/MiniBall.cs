using System.Runtime.InteropServices;

namespace EVESharp.Destiny;

/// <summary>
/// Mini ball sub-structure (Apocrypha format).
///
/// Wire format (32 bytes total):
///   Offset: 24 bytes (Vector3, relative to owner location)
///   Radius: 8 bytes  (double - Apocrypha uses double, NOT float!)
/// </summary>
[StructLayout (LayoutKind.Sequential, Pack = 1)]
public struct MiniBall
{
    /// <summary>
    /// relative to owner location
    /// </summary>
    public Vector3 Offset;

    public double Radius;
}
