using System.Runtime.InteropServices;

namespace EVESharp.Destiny;

/// <summary>
/// Warp mode state (Apocrypha format).
///
/// Wire format (44 bytes total):
///   Location:    24 bytes (Vector3, warp destination)
///   EffectStamp: 4 bytes  (int)
///   FollowRange: 8 bytes  (double)
///   FollowId:    4 bytes  (int, not long!)
///   OwnerId:     4 bytes  (int, not long!)
/// </summary>
[StructLayout (LayoutKind.Sequential, Pack = 1)]
public struct WarpState
{
    public Vector3 Location;
    public int     EffectStamp;
    public double  FollowRange;
    public int     FollowId;
    public int     OwnerId;
}
