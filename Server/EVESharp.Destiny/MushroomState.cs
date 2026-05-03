using System.Runtime.InteropServices;

namespace EVESharp.Destiny;

/// <summary>
/// Mushroom (smartbomb/gravity well) mode state (Apocrypha format).
///
/// Wire format (24 bytes total):
///   FollowRange: 8 bytes (double)
///   Unknown:     8 bytes (double)
///   EffectStamp: 4 bytes (int)
///   OwnerId:     4 bytes (int)
/// </summary>
[StructLayout (LayoutKind.Sequential, Pack = 1)]
public struct MushroomState
{
    public double FollowRange;
    public double Unknown;
    public int    EffectStamp;
    public int    OwnerId;
}
