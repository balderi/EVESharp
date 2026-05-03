using System.Runtime.InteropServices;

namespace EVESharp.Destiny;

/// <summary>
/// Follow/Orbit mode state (Apocrypha format).
///
/// Wire format (12 bytes total):
///   FollowId:    4 bytes (int, not long!)
///   FollowRange: 8 bytes (double, not float!)
/// </summary>
[StructLayout (LayoutKind.Sequential, Pack = 1)]
public struct FollowState
{
    public int    FollowId;
    public double FollowRange;
}
