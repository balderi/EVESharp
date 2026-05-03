using EVESharp.Destiny;
using EVESharp.EVE.Data.Inventory.Items;
using System;

namespace EVESharp.Node.Services.Space;

/// <summary>
/// Builds Destiny Ball structures from ItemEntity objects.
/// Used by beyonce/ballparkSvc to create balls for the destiny binary snapshot.
/// </summary>
public static class DestinyBallBuilder
{
    /// <summary>
    /// Create a Destiny Ball from an ItemEntity.
    /// </summary>
    /// <param name="ent">The item entity (ship, station, etc.)</param>
    /// <param name="isEgo">True if this is the player's own ship</param>
    /// <returns>A fully constructed Ball ready for binary encoding</returns>
    public static Ball FromEntity(ItemEntity ent, bool isEgo)
    {
        // Use double precision for coordinates - EVE uses very large numbers
        double x = ent.X ?? 0.0;
        double y = ent.Y ?? 0.0;
        double z = ent.Z ?? 0.0;

        Console.WriteLine($"[DestinyBallBuilder] Creating ball for entity {ent.ID}, isEgo={isEgo}, pos=({x:F0},{y:F0},{z:F0})");

        // -------------------------------------------------------
        // Ball Header
        // -------------------------------------------------------
        // All balls should have at least IsMassive flag
        // Ego (player ship) also needs IsFree and IsInteractive
        BallFlag flags = BallFlag.IsMassive | BallFlag.IsInteractive;
        if (isEgo)
        {
            flags |= BallFlag.IsFree;
        }

        BallHeader header = new BallHeader
        {
            ItemId   = ent.ID,
            Mode     = BallMode.Stop,           // Not moving initially
            Radius   = isEgo ? 50.0 : 500.0,   // Ship radius vs station/other
            Location = new Vector3 { X = x, Y = y, Z = z },
            Flags    = flags
        };

        // -------------------------------------------------------
        // Extra Header (required when Mode != Rigid)
        // Mode.Stop requires ExtraHeader
        // -------------------------------------------------------
        ExtraBallHeader extra = new ExtraBallHeader
        {
            Mass          = 1.0,
            CloakMode     = CloakMode.None,
            Harmonic      = 0xFFFFFFFFFFFFFFFF,
            CorporationId = 0,
            AllianceId    = 0
        };

        // -------------------------------------------------------
        // Ball Data (required when IsFree flag is set)
        // -------------------------------------------------------
        BallData data = default (BallData);

        if (header.Flags.HasFlag(BallFlag.IsFree))
        {
            data = new BallData
            {
                MaxVelocity   = 200.0,   // Will be overridden by ship type
                Velocity      = new Vector3 { X = 0, Y = 0, Z = 0 },
                UnknownVec    = default (Vector3),
                Agility       = 1.0,
                SpeedFraction = 0.0      // Not moving
            };
        }

        // -------------------------------------------------------
        // Construct full Destiny Ball
        // -------------------------------------------------------
        Ball ball = new Ball
        {
            Header      = header,
            ExtraHeader = extra,
            Data        = header.Flags.HasFlag(BallFlag.IsFree) ? data : default (BallData),
            FormationId = 0xFF,

            // Mode-specific states - not needed for Stop mode
            FollowState    = default (FollowState),
            FormationState = default (FormationState),
            MissileState   = default (MissileState),
            GotoState      = default (GotoState),
            WarpState      = default (WarpState),
            TrollState     = default (TrollState),
            MushroomState  = default (MushroomState),

            MiniBalls = null
        };

        Console.WriteLine($"[DestinyBallBuilder] Ball created: flags={flags}, mode={header.Mode}");

        return ball;
    }

    /// <summary>
    /// Create a station ball with appropriate settings.
    /// Stations are massive, non-moving, global objects.
    /// </summary>
    public static Ball FromStation(ItemEntity stationEnt, int solarSystemID)
    {
        double x = stationEnt.X ?? 0.0;
        double y = stationEnt.Y ?? 0.0;
        double z = stationEnt.Z ?? 0.0;

        Console.WriteLine($"[DestinyBallBuilder] Creating STATION ball {stationEnt.ID} at ({x:F0},{y:F0},{z:F0})");

        BallHeader header = new BallHeader
        {
            ItemId   = stationEnt.ID,
            Mode     = BallMode.Rigid,  // Stations are rigid - no movement
            Radius   = 5000.0,          // Station radius
            Location = new Vector3 { X = x, Y = y, Z = z },
            Flags    = BallFlag.IsGlobal | BallFlag.IsMassive | BallFlag.IsInteractive
        };

        // Rigid balls don't need ExtraHeader or BallData
        Ball ball = new Ball
        {
            Header      = header,
            ExtraHeader = null,  // Not needed for Rigid
            Data        = default (BallData),
            FormationId = 0xFF,

            FollowState    = default (FollowState),
            FormationState = default (FormationState),
            MissileState   = default (MissileState),
            GotoState      = default (GotoState),
            WarpState      = default (WarpState),
            TrollState     = default (TrollState),
            MushroomState  = default (MushroomState),

            MiniBalls = null
        };

        return ball;
    }
}