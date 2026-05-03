using System.Runtime.InteropServices;

namespace EVESharp.Destiny;

/// <summary>
/// Missile mode state (Apocrypha format).
///
/// Wire format (44 bytes total):
///   FollowId:    4 bytes  (int, target entity)
///   FollowRange: 8 bytes  (double)
///   OwnerId:     4 bytes  (int, source/owner entity)
///   EffectStamp: 4 bytes  (int)
///   Location:    24 bytes (Vector3)
/// </summary>
[StructLayout (LayoutKind.Sequential, Pack = 1)]
public struct MissileState
{
    public int     FollowId;
    public double  FollowRange;
    public int     OwnerId;
    public int     EffectStamp;
    public Vector3 Location;
}
