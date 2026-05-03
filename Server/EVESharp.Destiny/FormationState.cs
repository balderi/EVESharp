using System.Runtime.InteropServices;

namespace EVESharp.Destiny;

/// <summary>
/// Formation mode state (Apocrypha format).
///
/// Wire format (16 bytes total):
///   FollowId:    4 bytes (int, not long!)
///   FollowRange: 8 bytes (double, not float!)
///   EffectStamp: 4 bytes (int, not float!)
/// </summary>
[StructLayout (LayoutKind.Sequential, Pack = 1)]
public struct FormationState
{
    public int    FollowId;
    public double FollowRange;
    public int    EffectStamp;
}
