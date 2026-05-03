using System.Runtime.InteropServices;

namespace EVESharp.Destiny;

/// <summary>
/// Troll mode state (Apocrypha format).
///
/// Wire format (4 bytes total):
///   EffectStamp: 4 bytes (int, not float!)
/// </summary>
[StructLayout (LayoutKind.Sequential, Pack = 1)]
public struct TrollState
{
    public int EffectStamp;
}
