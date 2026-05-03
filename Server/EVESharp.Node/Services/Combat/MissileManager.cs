using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using EVESharp.Destiny;
using EVESharp.EVE.Notifications;
using EVESharp.Node.Services.Space;
using EVESharp.Types;
using EVESharp.Types.Collections;

namespace EVESharp.Node.Services.Combat;

/// <summary>
/// Tracks a missile in flight.
/// </summary>
public class MissileFlightInfo
{
    public int          MissileID           { get; set; }
    public int          SolarSystemID       { get; set; }
    public BubbleEntity MissileBubble       { get; set; }
    public BubbleEntity Attacker            { get; set; }
    public BubbleEntity Target              { get; set; }
    public double       EmDamage            { get; set; }
    public double       ExplosiveDamage     { get; set; }
    public double       KineticDamage       { get; set; }
    public double       ThermalDamage       { get; set; }
    public double       DamageMultiplier    { get; set; }
    public double       ExplosionRadius     { get; set; }
    public double       ExplosionVelocity   { get; set; }
    public double       FlightTimeRemaining { get; set; }
    public double       Velocity            { get; set; }
}

/// <summary>
/// Singleton service that spawns missile balls in space and processes their flight.
/// Missiles appear as real balls that fly toward targets, then explode on arrival.
/// </summary>
public class MissileManager
{
    private static int sNextMissileID = -100000; // negative IDs to avoid DB conflict

    private readonly INotificationSender                          mNotifications;
    private readonly CombatService                                mCombat;
    private readonly ConcurrentDictionary<int, MissileFlightInfo> mMissiles =
        new ConcurrentDictionary <int, MissileFlightInfo> ();

    public MissileManager (INotificationSender notifications, CombatService combat)
    {
        mNotifications = notifications;
        mCombat        = combat;
    }

    /// <summary>
    /// Spawn a missile ball in space flying toward a target.
    /// </summary>
    public void SpawnMissile (int solarSystemID, BubbleEntity attacker, BubbleEntity target,
                              int launcherTypeID, double emDmg, double expDmg, double kinDmg, double thermDmg,
                              double dmgMult, double expRadius, double expVelocity, double missileVelocity,
                              DestinyManager destinyMgr)
    {
        int missileID = System.Threading.Interlocked.Decrement (ref sNextMissileID);

        BubbleEntity missileBubble = new BubbleEntity
        {
            ItemID         = missileID,
            TypeID         = launcherTypeID,
            GroupID        = 0,
            CategoryID     = 0,
            Name           = "Missile",
            OwnerID        = attacker.OwnerID,
            CorporationID  = attacker.CorporationID,
            AllianceID     = attacker.AllianceID,
            CharacterID    = 0,
            Position       = attacker.Position,
            Velocity       = default (Vector3),
            Mode           = BallMode.Missile,
            Flags          = BallFlag.IsFree | BallFlag.IsMassive,
            Radius         = 10.0,
            Mass           = 1.0,
            MaxVelocity    = missileVelocity > 0 ? missileVelocity : 3000,
            SpeedFraction  = 1.0,
            Agility        = 0.01,
            FollowTargetID = target.ItemID,
        };

        destinyMgr.RegisterEntity (missileBubble);

        // Broadcast AddBalls for the missile
        int stamp         = DestinyEventBuilder.GetStamp ();
        PyList addBallEvents = DestinyEventBuilder.BuildAddBalls (new[] { missileBubble }, solarSystemID, stamp);
        PyTuple notification  = DestinyEventBuilder.WrapAsNotification (addBallEvents);
        mNotifications.SendNotification ("DoDestinyUpdate", "solarsystemid", solarSystemID, notification);

        double flightTime = 20.0; // max 20 seconds in flight
        double dist       = (target.Position - attacker.Position).Length;
        if (missileBubble.MaxVelocity > 0)
            flightTime = Math.Min (flightTime, (dist / missileBubble.MaxVelocity) * 2);

        MissileFlightInfo flight = new MissileFlightInfo
        {
            MissileID           = missileID,
            SolarSystemID       = solarSystemID,
            MissileBubble       = missileBubble,
            Attacker            = attacker,
            Target              = target,
            EmDamage            = emDmg,
            ExplosiveDamage     = expDmg,
            KineticDamage       = kinDmg,
            ThermalDamage       = thermDmg,
            DamageMultiplier    = dmgMult,
            ExplosionRadius     = expRadius,
            ExplosionVelocity   = expVelocity,
            FlightTimeRemaining = flightTime,
            Velocity            = missileBubble.MaxVelocity
        };

        mMissiles[missileID] = flight;

        Console.WriteLine ($"[MissileManager] Spawned missile {missileID} from {attacker.Name} toward {target.Name}, speed={missileBubble.MaxVelocity:F0}");
    }

    /// <summary>
    /// Called from DestinyManager tick loop to update missile positions and check arrivals.
    /// </summary>
    public void ProcessMissiles (double dt, DestinyManager destinyMgr)
    {
        List <int> toRemove = new List<int> ();

        foreach (KeyValuePair <int, MissileFlightInfo> kvp in mMissiles)
        {
            MissileFlightInfo flight = kvp.Value;
            flight.FlightTimeRemaining -= dt;

            // Update missile position: fly toward target
            BubbleEntity    missile  = flight.MissileBubble;
            Vector3    toTarget = flight.Target.Position - missile.Position;
            double dist     = toTarget.Length;

            double arrivalDist = flight.Target.Radius + 50.0;

            if (dist <= arrivalDist || flight.FlightTimeRemaining <= 0)
            {
                if (dist <= arrivalDist)
                {
                    // Hit! Apply damage
                    mCombat?.ApplyMissileDamage (flight.SolarSystemID, flight.Attacker, flight.Target,
                                                 flight.EmDamage, flight.ExplosiveDamage,
                                                 flight.KineticDamage, flight.ThermalDamage,
                                                 flight.DamageMultiplier, flight.ExplosionRadius, flight.ExplosionVelocity);

                    Console.WriteLine ($"[MissileManager] Missile {flight.MissileID} hit {flight.Target.Name}");

                    // Check destruction
                    if (flight.Target.IsDestroyed && !flight.Target.PendingDestruction)
                    {
                        flight.Target.PendingDestruction = true;
                        mCombat?.HandleEntityDestruction (flight.SolarSystemID, flight.Target, destinyMgr);
                    }
                }
                else
                {
                    Console.WriteLine ($"[MissileManager] Missile {flight.MissileID} expired");
                }

                // Remove missile ball
                destinyMgr.UnregisterEntity (flight.MissileID);

                PyList events       = DestinyEventBuilder.BuildRemoveBalls (new[] { flight.MissileID });
                PyTuple notification = DestinyEventBuilder.WrapAsNotification (events);
                mNotifications.SendNotification ("DoDestinyUpdate", "solarsystemid", flight.SolarSystemID, notification);

                toRemove.Add (kvp.Key);
            }
            else
            {
                // Move toward target
                Vector3    dir       = toTarget.Normalize ();
                double moveSpeed = flight.Velocity * dt;
                missile.Position = missile.Position + dir * moveSpeed;
                missile.Velocity = dir * flight.Velocity;
            }
        }

        foreach (int id in toRemove)
            mMissiles.TryRemove (id, out _);
    }
}