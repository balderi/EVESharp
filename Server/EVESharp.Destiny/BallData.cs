using System.Runtime.InteropServices;

namespace EVESharp.Destiny;

/// <summary>
/// Ball movement data for IsFree balls (Apocrypha format).
/// All velocity/speed fields are doubles, not floats.
///
/// Wire format (72 bytes total):
///   MaxVelocity:   8 bytes (double)
///   Velocity:      24 bytes (Vector3: 3 doubles)
///   UnknownVec:    24 bytes (Vector3: 3 doubles - acceleration or similar)
///   Agility:       8 bytes (double - ship agility modifier)
///   SpeedFraction: 8 bytes (double - 0.0 to 1.0 throttle)
/// </summary>
[StructLayout (LayoutKind.Sequential, Pack = 1)]
public class BallData
{
    public double  MaxVelocity;
    public Vector3 Velocity;
    public Vector3 UnknownVec;
    public double  Agility;
    public double  SpeedFraction;
}
