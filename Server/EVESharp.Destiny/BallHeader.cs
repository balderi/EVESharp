using System.Runtime.InteropServices;

namespace EVESharp.Destiny;

/// <summary>
/// Ball header structure for Destiny binary protocol (Apocrypha format).
///
/// Wire format (38 bytes total):
///   ItemId:   4 bytes (int, entity/ball identifier)
///   Mode:     1 byte  (BallMode enum)
///   Radius:   8 bytes (double - Apocrypha uses double, NOT float!)
///   Location: 24 bytes (Vector3: 3 doubles)
///   Flags:    1 byte  (BallFlag enum, called "sub_type" in EVEmu)
/// </summary>
[StructLayout (LayoutKind.Sequential, Pack = 1)]
public class BallHeader
{
    /// <summary>
    /// Ball/Item ID. For Apocrypha client this is 32-bit.
    /// </summary>
    public int      ItemId;

    public BallMode Mode;

    /// <summary>
    /// Radius in meters. Apocrypha uses double (8 bytes), not float.
    /// </summary>
    public double   Radius;

    public Vector3  Location;

    /// <summary>
    /// Ball flags (sub_type in EVEmu). Written LAST in Apocrypha format.
    /// </summary>
    public BallFlag Flags;
}
