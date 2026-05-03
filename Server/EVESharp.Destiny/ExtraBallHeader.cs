using System.Runtime.InteropServices;

namespace EVESharp.Destiny;

/// <summary>
/// Extra ball header for non-Rigid mode balls (Apocrypha format).
/// Contains mass, cloak, and ownership data.
///
/// Wire format (25 bytes total):
///   Mass:          8 bytes (double - FIRST in Apocrypha, was last in Crucible)
///   CloakMode:     1 byte  (enum)
///   Harmonic:      8 bytes (ulong - "unknown52" in EVEmu, often 0xFFFFFFFFFFFFFFFF)
///   CorporationId: 4 bytes (int)
///   AllianceId:    4 bytes (int)
/// </summary>
[StructLayout (LayoutKind.Sequential, Pack = 1)]
public class ExtraBallHeader
{
    /// <summary>
    /// Mass from type information. FIRST field in Apocrypha format.
    /// </summary>
    public double Mass;

    public CloakMode CloakMode;

    /// <summary>
    /// Unknown field ("unknown52" in EVEmu). Often set to 0xFFFFFFFFFFFFFFFF.
    /// Was "Harmonic" (float) in Crucible format - completely different type in Apocrypha.
    /// </summary>
    public ulong Harmonic;

    public int CorporationId;

    public int AllianceId;
}
