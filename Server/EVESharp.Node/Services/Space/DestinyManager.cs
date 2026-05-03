using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using EVESharp.Database.Old;
using EVESharp.Destiny;
using EVESharp.EVE.Data.Inventory;
using EVESharp.EVE.Data.Inventory.Items;
using EVESharp.Node.Services.Combat;
using EVESharp.Types.Collections;

namespace EVESharp.Node.Services.Space;

/// <summary>
/// Per-solar-system physics tick loop. Processes movement commands,
/// runs NPC AI (standings-aware aggression), and broadcasts incremental
/// destiny updates to bubble occupants.
/// </summary>
public class DestinyManager : IDisposable
{
    // EVE physics constants
    private const double TIC_DURATION    = 1.0;   // seconds per tick
    private const double SPACE_FRICTION  = 1.0e6;
    private const double AU_IN_METERS    = 1.496e11;
    private const double WARP_SPEED_AUS  = 3.0;                        // AU/s - sent to client
    private const double WARP_SPEED      = WARP_SPEED_AUS * AU_IN_METERS; // m/s - server physics
    private const double ARRIVE_DIST     = 15000.0; // stop distance for goto/follow
    private const double ORBIT_TOLERANCE = 500.0;

    // NPC AI constants
    private const double NPC_SCAN_INTERVAL = 3.0;  // seconds between target scans when idle
    private const double NPC_LEASH_RETURN  = 5000.0; // distance threshold to return to spawn

    public int           SolarSystemID { get; }
    public BubbleManager BubbleManager { get; }

    private readonly DestinyBroadcaster                      mBroadcaster;
    private readonly StandingDB                              mStandingDB;
    private readonly CombatService                           mCombat;
    private readonly MissileManager                          mMissileManager;
    private readonly IItems                                  mItems;
    private readonly ConcurrentDictionary<int, BubbleEntity> mEntities = new ConcurrentDictionary<int, BubbleEntity>();
    private readonly ConcurrentQueue<Action>                 mPendingCommands = new ConcurrentQueue<Action>();
    private readonly Timer                                   mTickTimer;
    private          bool                                    mDisposed;
    private readonly Random                                  mRng = new Random();

    public DestinyManager(int solarSystemID, DestinyBroadcaster broadcaster, StandingDB standingDB, CombatService combatService, MissileManager missileManager, IItems items)
    {
        SolarSystemID   = solarSystemID;
        BubbleManager   = new BubbleManager();
        mBroadcaster    = broadcaster;
        mStandingDB     = standingDB;
        mCombat         = combatService;
        mMissileManager = missileManager;
        mItems          = items;

        // Start tick loop at 1 second intervals
        mTickTimer = new Timer(Tick, null, 1000, 1000);
        Console.WriteLine($"[DestinyManager] Started for system {solarSystemID}");
    }

    // =====================================================================
    //  ENTITY MANAGEMENT
    // =====================================================================

    public void RegisterEntity(BubbleEntity entity)
    {
        mEntities[entity.ItemID] = entity;
        BubbleManager.AddEntity(entity);
        Console.WriteLine($"[DestinyManager] Registered entity {entity.ItemID} ({entity.Name}) in system {SolarSystemID}");
    }

    public void UnregisterEntity(int itemID)
    {
        mEntities.TryRemove(itemID, out _);
        BubbleManager.RemoveEntity(itemID);
    }

    public bool TryGetEntity(int itemID, out BubbleEntity entity)
    {
        return mEntities.TryGetValue(itemID, out entity);
    }

    /// <summary>
    /// Returns all registered entities. Used by GM commands like /unspawn.
    /// </summary>
    public IEnumerable<BubbleEntity> GetEntities()
    {
        return mEntities.Values;
    }

    // =====================================================================
    //  COMMAND METHODS (called from beyonce service thread)
    //  These enqueue commands to be processed on the next tick.
    // =====================================================================

    public void CmdStop(int shipID)
    {
        mPendingCommands.Enqueue(() =>
        {
            if (!mEntities.TryGetValue(shipID, out BubbleEntity ent)) return;

            ent.Mode          = BallMode.Stop;
            ent.Velocity      = default (Vector3);
            ent.SpeedFraction = 0;

            SystemBubble bubble = BubbleManager.GetBubbleForEntity(shipID);
            if (bubble != null)
            {
                PyList events = DestinyEventBuilder.BuildSetSpeedFraction(shipID, 0);
                mBroadcaster.BroadcastToSystem(SolarSystemID, events);
            }

            Console.WriteLine($"[DestinyManager] Stop: entity {shipID}");
        });
    }

    public void CmdGotoPoint(int shipID, double x, double y, double z)
    {
        mPendingCommands.Enqueue(() =>
        {
            if (!mEntities.TryGetValue(shipID, out BubbleEntity ent)) return;

            ent.Mode       = BallMode.Goto;
            ent.GotoTarget = new Vector3 { X = x, Y = y, Z = z };
            if (ent.SpeedFraction <= 0) ent.SpeedFraction = 1.0;

            SystemBubble bubble = BubbleManager.GetBubbleForEntity(shipID);
            if (bubble != null)
            {
                PyList events = DestinyEventBuilder.BuildGotoPoint(shipID, x, y, z);
                mBroadcaster.BroadcastToSystem(SolarSystemID, events);
            }

            Console.WriteLine($"[DestinyManager] GotoPoint: entity {shipID} → ({x:F0},{y:F0},{z:F0})");
        });
    }

    public void CmdFollowBall(int shipID, int targetID, float range)
    {
        mPendingCommands.Enqueue(() =>
        {
            if (!mEntities.TryGetValue(shipID, out BubbleEntity ent)) return;

            ent.Mode           = BallMode.Follow;
            ent.FollowTargetID = targetID;
            ent.FollowRange    = range;
            if (ent.SpeedFraction <= 0) ent.SpeedFraction = 1.0;

            SystemBubble bubble = BubbleManager.GetBubbleForEntity(shipID);
            if (bubble != null)
            {
                PyList events = DestinyEventBuilder.BuildFollowBall(shipID, targetID, range);
                mBroadcaster.BroadcastToSystem(SolarSystemID, events);
            }

            Console.WriteLine($"[DestinyManager] FollowBall: entity {shipID} → target {targetID}, range {range}");
        });
    }

    public void CmdOrbit(int shipID, int targetID, float range)
    {
        mPendingCommands.Enqueue(() =>
        {
            if (!mEntities.TryGetValue(shipID, out BubbleEntity ent)) return;

            ent.Mode           = BallMode.Orbit;
            ent.FollowTargetID = targetID;
            ent.FollowRange    = range;
            if (ent.SpeedFraction <= 0) ent.SpeedFraction = 1.0;

            SystemBubble bubble = BubbleManager.GetBubbleForEntity(shipID);
            if (bubble != null)
            {
                PyList events = DestinyEventBuilder.BuildOrbit(shipID, targetID, range);
                mBroadcaster.BroadcastToSystem(SolarSystemID, events);
            }

            Console.WriteLine($"[DestinyManager] Orbit: entity {shipID} → target {targetID}, range {range}");
        });
    }

    public void CmdSetSpeedFraction(int shipID, float fraction)
    {
        mPendingCommands.Enqueue(() =>
        {
            if (!mEntities.TryGetValue(shipID, out BubbleEntity ent)) return;

            ent.SpeedFraction = Math.Clamp(fraction, 0.0, 1.0);

            SystemBubble bubble = BubbleManager.GetBubbleForEntity(shipID);
            if (bubble != null)
            {
                PyList events = DestinyEventBuilder.BuildSetSpeedFraction(shipID, ent.SpeedFraction);
                mBroadcaster.BroadcastToSystem(SolarSystemID, events);
            }

            Console.WriteLine($"[DestinyManager] SetSpeedFraction: entity {shipID} = {ent.SpeedFraction}");
        });
    }

    public void CmdAlignTo(int shipID, int targetID)
    {
        // AlignTo is Follow with 0 range (approach target direction)
        CmdFollowBall(shipID, targetID, 0);
    }

    public void CmdWarpTo(int shipID, double x, double y, double z)
    {
        mPendingCommands.Enqueue(() =>
        {
            if (!mEntities.TryGetValue(shipID, out BubbleEntity ent)) return;

            long eveEpoch = new DateTime(2003, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;
            int  stamp    = (int)((DateTime.UtcNow.Ticks - eveEpoch) / 10000000 % int.MaxValue);

            ent.Mode            = BallMode.Warp;
            ent.WarpTarget      = new Vector3 { X = x, Y = y, Z = z };
            ent.WarpEffectStamp = stamp;
            ent.SpeedFraction   = 1.0;

            SystemBubble bubble = BubbleManager.GetBubbleForEntity(shipID);
            if (bubble != null)
            {
                // Send GotoPoint first (starts alignment + acceleration toward destination),
                // then WarpTo (engages warp once the ball reaches 75% max velocity while aligned).
                // The client's native destiny code requires the ball to be in motion for warp to commit.
                PyList events     = DestinyEventBuilder.BuildGotoPoint(shipID, x, y, z);
                PyList warpEvents = DestinyEventBuilder.BuildWarpTo(shipID, x, y, z, WARP_SPEED_AUS, stamp);
                for (int i = 0; i < warpEvents.Count; i++)
                    events.Add(warpEvents[i]);

                mBroadcaster.BroadcastToSystem(SolarSystemID, events);
            }

            Console.WriteLine($"[DestinyManager] WarpTo: entity {shipID} → ({x:F0},{y:F0},{z:F0})");
        });
    }

    // =====================================================================
    //  TICK LOOP
    // =====================================================================

    private void Tick(object state)
    {
        if (mDisposed) return;

        try
        {
            // 1) Drain command queue
            while (mPendingCommands.TryDequeue(out Action cmd))
                cmd();

            // 2) Process movement for all entities
            foreach (KeyValuePair <int, BubbleEntity> kvp in mEntities)
            {
                BubbleEntity ent = kvp.Value;
                if (ent.IsRigid || ent.SpeedFraction <= 0) continue;

                ProcessMovement(ent);
            }

            // 3) Process NPC AI (standings-aware target selection and engagement)
            ProcessNpcAi();

            // 4) Process in-flight missiles
            mMissileManager?.ProcessMissiles(TIC_DURATION, this);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DestinyManager] Tick error in system {SolarSystemID}: {ex.Message}");
        }
    }

    private void ProcessMovement(BubbleEntity ent)
    {
        switch (ent.Mode)
        {
            case BallMode.Goto:
                ProcessGoto(ent);
                break;

            case BallMode.Follow:
                ProcessFollow(ent);
                break;

            case BallMode.Orbit:
                ProcessOrbit(ent);
                break;

            case BallMode.Warp:
                ProcessWarp(ent);
                break;

            case BallMode.Stop:
                // Decelerate to zero
                if (ent.Velocity.Length > 1.0)
                {
                    ent.Velocity = ent.Velocity * 0.5;
                    ent.Position = ent.Position + ent.Velocity * TIC_DURATION;
                }
                else
                {
                    ent.Velocity      = default (Vector3);
                    ent.SpeedFraction = 0;
                }
                break;
        }

        // Check bubble transitions
        BubbleManager.UpdateEntityBubble(ent);
    }

    private void ProcessGoto(BubbleEntity ent)
    {
        Vector3 toTarget = ent.GotoTarget - ent.Position;
        double  dist     = toTarget.Length;

        if (dist < ARRIVE_DIST)
        {
            ent.Mode          = BallMode.Stop;
            ent.Velocity      = default (Vector3);
            ent.SpeedFraction = 0;
            return;
        }

        Vector3 dir   = toTarget.Normalize();
        double  speed = ent.MaxVelocity * ent.SpeedFraction;
        ent.Velocity = dir * speed;
        ent.Position = ent.Position + ent.Velocity * TIC_DURATION;
    }

    private void ProcessFollow(BubbleEntity ent)
    {
        if (!mEntities.TryGetValue(ent.FollowTargetID, out BubbleEntity target))
        {
            ent.Mode = BallMode.Stop;
            return;
        }

        Vector3 toTarget = target.Position - ent.Position;
        double  dist     = toTarget.Length;

        if (dist <= ent.FollowRange + ARRIVE_DIST)
        {
            // Within range, slow down
            ent.Velocity = ent.Velocity * 0.5;
            ent.Position = ent.Position + ent.Velocity * TIC_DURATION;
            return;
        }

        Vector3 dir   = toTarget.Normalize();
        double  speed = ent.MaxVelocity * ent.SpeedFraction;
        ent.Velocity = dir * speed;
        ent.Position = ent.Position + ent.Velocity * TIC_DURATION;
    }

    private void ProcessOrbit(BubbleEntity ent)
    {
        if (!mEntities.TryGetValue(ent.FollowTargetID, out BubbleEntity target))
        {
            ent.Mode = BallMode.Stop;
            return;
        }

        Vector3 toTarget     = target.Position - ent.Position;
        double  dist         = toTarget.Length;
        double  desiredRange = ent.FollowRange > 0 ? ent.FollowRange : 5000;

        if (Math.Abs(dist - desiredRange) > ORBIT_TOLERANCE)
        {
            // Approach/retreat to orbit range
            Vector3 dir   = toTarget.Normalize();
            double  speed = ent.MaxVelocity * ent.SpeedFraction;
            double  sign  = dist > desiredRange ? 1.0 : -1.0;
            ent.Velocity = dir * (speed * sign);
        }
        else
        {
            // At orbit range - rotate around target
            Vector3 dir = toTarget.Normalize();
            // Perpendicular vector (rotate 90 degrees in XZ plane)
            Vector3 perp  = new Vector3 { X = -dir.Z, Y = 0, Z = dir.X };
            double  speed = ent.MaxVelocity * ent.SpeedFraction;
            ent.Velocity = perp * speed;
        }

        ent.Position = ent.Position + ent.Velocity * TIC_DURATION;
    }

    private void ProcessWarp(BubbleEntity ent)
    {
        Vector3 toTarget = ent.WarpTarget - ent.Position;
        double  dist     = toTarget.Length;

        // Arrive at warp destination
        if (dist < ARRIVE_DIST)
        {
            ent.Position      = ent.WarpTarget;
            ent.Mode          = BallMode.Stop;
            ent.Velocity      = default (Vector3);
            ent.SpeedFraction = 0;

            Console.WriteLine($"[DestinyManager] Warp complete: entity {ent.ItemID} arrived at {ent.Position}");

            // Persist ship position for player ships (crash recovery)
            if (ent.IsPlayer)
            {
                try
                {
                    ItemEntity shipEntity = mItems.LoadItem(ent.ItemID);
                    if (shipEntity != null)
                    {
                        shipEntity.X = ent.Position.X;
                        shipEntity.Y = ent.Position.Y;
                        shipEntity.Z = ent.Position.Z;
                        shipEntity.Persist();
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[DestinyManager] Failed to persist warp position for {ent.ItemID}: {ex.Message}");
                }
            }

            // Broadcast stop at destination
            SystemBubble bubble = BubbleManager.GetBubbleForEntity(ent.ItemID);
            if (bubble != null)
            {
                PyList events = DestinyEventBuilder.BuildSetSpeedFraction(ent.ItemID, 0);
                mBroadcaster.BroadcastToSystem(SolarSystemID, events);
            }
            return;
        }

        // Move at warp speed toward target
        Vector3 dir          = toTarget.Normalize();
        double  moveDistance = Math.Min(WARP_SPEED * TIC_DURATION, dist);
        ent.Position = ent.Position + dir * moveDistance;
        ent.Velocity = dir * WARP_SPEED;
    }

    // =====================================================================
    //  NPC AI — STANDINGS-AWARE AGGRESSION
    // =====================================================================

    /// <summary>
    /// Process AI for all NPC entities in this solar system.
    /// NPCs scan for nearby players, check faction standings, and engage
    /// hostiles (standing below threshold). Friendly/neutral players are ignored.
    /// </summary>
    private void ProcessNpcAi()
    {
        foreach (KeyValuePair <int, BubbleEntity> kvp in mEntities)
        {
            BubbleEntity npc = kvp.Value;
            if (!npc.IsNpc) continue;

            npc.AiTimer += TIC_DURATION;
            if (npc.AttackCooldown > 0)
                npc.AttackCooldown -= TIC_DURATION;

            switch (npc.AiState)
            {
                case NpcAiState.Idle:
                    NpcAi_Idle(npc);
                    break;

                case NpcAiState.Combat:
                    NpcAi_Combat(npc);
                    break;

                case NpcAiState.Pursuit:
                    NpcAi_Pursuit(npc);
                    break;

                case NpcAiState.Departing:
                    NpcAi_Departing(npc);
                    break;
            }
        }
    }

    /// <summary>
    /// IDLE: Periodically scan for hostile players within attack range.
    /// A player is hostile if the NPC's faction standing toward them is below the threshold.
    /// </summary>
    private void NpcAi_Idle(BubbleEntity npc)
    {
        // Only scan every NPC_SCAN_INTERVAL seconds to avoid DB spam
        if (npc.AiTimer < NPC_SCAN_INTERVAL)
            return;

        npc.AiTimer = 0;

        BubbleEntity closestHostile = null;
        double       closestDist    = double.MaxValue;

        foreach (KeyValuePair <int, BubbleEntity> kvp in mEntities)
        {
            BubbleEntity target = kvp.Value;
            if (!target.IsPlayer) continue;

            double dist = (target.Position - npc.Position).Length;
            if (dist > npc.AttackRange) continue;
            if (dist >= closestDist) continue;

            // Check faction standing toward this player
            double standing = GetNpcStandingToPlayer(npc, target.CharacterID);
            if (standing >= npc.AggroStandingThreshold)
                continue; // Friendly or neutral — ignore

            closestHostile = target;
            closestDist    = dist;
        }

        if (closestHostile == null)
            return;

        // Engage! Transition to combat
        npc.AiState        = NpcAiState.Combat;
        npc.AiTargetID     = closestHostile.ItemID;
        npc.AttackCooldown = npc.AttackDelayMin + mRng.NextDouble() * (npc.AttackDelayMax - npc.AttackDelayMin);

        // Command: orbit target at fly range
        NpcOrbitTarget(npc, closestHostile.ItemID, npc.OrbitRange);

        // Notify the targeted player that an NPC locked them (camera stays on player's ship)
        mBroadcaster.SendOnTargetToCharacter(closestHostile.CharacterID, "otheradd", npc.ItemID);

        Console.WriteLine($"[NpcAI] {npc.Name} ({npc.ItemID}) engaging {closestHostile.Name} ({closestHostile.ItemID}) " +
                          $"standing={GetNpcStandingToPlayer(npc, closestHostile.CharacterID):F1}, dist={closestDist:F0}m");
    }

    /// <summary>
    /// COMBAT: Orbit target, attack when cooldown expires.
    /// If target leaves the system or gets too far, switch to pursuit/disengage.
    /// </summary>
    private void NpcAi_Combat(BubbleEntity npc)
    {
        // Validate target still exists
        if (!mEntities.TryGetValue(npc.AiTargetID, out BubbleEntity target) || !target.IsPlayer)
        {
            NpcDisengage(npc, "target lost");
            return;
        }

        double dist      = (target.Position - npc.Position).Length;
        double spawnDist = (npc.Position - npc.SpawnPosition).Length;

        // Chase distance exceeded — disengage and return home
        if (spawnDist > npc.ChaseMaxDistance)
        {
            NpcDisengage(npc, "chase distance exceeded");
            return;
        }

        // Target moved out of attack range — switch to pursuit
        if (dist > npc.AttackRange * 1.5)
        {
            npc.AiState = NpcAiState.Pursuit;
            NpcFollowTarget(npc, target.ItemID, npc.OrbitRange);
            Console.WriteLine($"[NpcAI] {npc.Name} ({npc.ItemID}) pursuing {target.Name} ({target.ItemID}), dist={dist:F0}m");
            return;
        }

        // Re-check standings periodically — if player became friendly, disengage
        if (npc.AiTimer > NPC_SCAN_INTERVAL)
        {
            npc.AiTimer = 0;
            double standing = GetNpcStandingToPlayer(npc, target.CharacterID);
            if (standing >= npc.AggroStandingThreshold)
            {
                NpcDisengage(npc, $"standing improved to {standing:F1}");
                return;
            }
        }

        // Attack: apply NPC damage when cooldown expires
        if (npc.AttackCooldown <= 0)
        {
            npc.AttackCooldown = npc.AttackDelayMin + mRng.NextDouble() * (npc.AttackDelayMax - npc.AttackDelayMin);

            if (mCombat != null)
            {
                mCombat.ApplyNpcDamage(SolarSystemID, npc, target,
                                       npc.NpcEmDamage, npc.NpcExplosiveDamage,
                                       npc.NpcKineticDamage, npc.NpcThermalDamage);

                Console.WriteLine($"[NpcAI] {npc.Name} ({npc.ItemID}) attacks {target.Name} ({target.ItemID}), " +
                                  $"dist={dist:F0}m, dmg(em={npc.NpcEmDamage:F0},exp={npc.NpcExplosiveDamage:F0}," +
                                  $"kin={npc.NpcKineticDamage:F0},therm={npc.NpcThermalDamage:F0})");

                // Broadcast attack visual effect so the client shows weapon fire
                mBroadcaster.BroadcastNpcAttackFX(SolarSystemID, npc.ItemID, npc.TypeID, target.ItemID);

                // Check if target was destroyed
                if (target.IsDestroyed && !target.PendingDestruction)
                {
                    target.PendingDestruction = true;
                    mCombat.HandleEntityDestruction(SolarSystemID, target, this);
                    NpcDisengage(npc, "target destroyed");
                }
            }
            else
            {
                Console.WriteLine($"[NpcAI] {npc.Name} ({npc.ItemID}) attacks {target.Name} ({target.ItemID}), dist={dist:F0}m (no CombatService)");
            }
        }
    }

    /// <summary>
    /// PURSUIT: Chase a target that moved out of range.
    /// If target returns to range, switch back to combat.
    /// If chase distance is exceeded, disengage.
    /// </summary>
    private void NpcAi_Pursuit(BubbleEntity npc)
    {
        if (!mEntities.TryGetValue(npc.AiTargetID, out BubbleEntity target) || !target.IsPlayer)
        {
            NpcDisengage(npc, "target lost during pursuit");
            return;
        }

        double dist      = (target.Position - npc.Position).Length;
        double spawnDist = (npc.Position - npc.SpawnPosition).Length;

        // Chase distance exceeded — give up
        if (spawnDist > npc.ChaseMaxDistance)
        {
            NpcDisengage(npc, "max chase distance");
            return;
        }

        // Back in range — resume combat
        if (dist <= npc.AttackRange)
        {
            npc.AiState = NpcAiState.Combat;
            NpcOrbitTarget(npc, target.ItemID, npc.OrbitRange);
            Console.WriteLine($"[NpcAI] {npc.Name} ({npc.ItemID}) re-engaging {target.Name}, dist={dist:F0}m");
        }
    }

    /// <summary>
    /// DEPARTING: Return to spawn position after disengaging.
    /// Once close to spawn, transition to idle.
    /// </summary>
    private void NpcAi_Departing(BubbleEntity npc)
    {
        double dist = (npc.SpawnPosition - npc.Position).Length;

        if (dist < NPC_LEASH_RETURN)
        {
            // Arrived home — stop and go idle
            npc.Mode          = BallMode.Stop;
            npc.Velocity      = default (Vector3);
            npc.SpeedFraction = 0;
            npc.AiState       = NpcAiState.Idle;
            npc.AiTimer       = 0;

            PyList events = DestinyEventBuilder.BuildSetSpeedFraction(npc.ItemID, 0);
            mBroadcaster.BroadcastToSystem(SolarSystemID, events);

            Console.WriteLine($"[NpcAI] {npc.Name} ({npc.ItemID}) returned to spawn, now idle");
        }
    }

    /// <summary>
    /// Disengage the NPC from its target and return to spawn position.
    /// </summary>
    private void NpcDisengage(BubbleEntity npc, string reason)
    {
        Console.WriteLine($"[NpcAI] {npc.Name} ({npc.ItemID}) disengaging: {reason}");

        // Notify the targeted player that the NPC stopped locking them
        if (npc.AiTargetID != 0 && mEntities.TryGetValue(npc.AiTargetID, out BubbleEntity disengageTarget) && disengageTarget.CharacterID != 0)
            mBroadcaster.SendOnTargetToCharacter(disengageTarget.CharacterID, "otherlost", npc.ItemID);

        npc.AiState    = NpcAiState.Departing;
        npc.AiTargetID = 0;

        // Command: go back to spawn position
        npc.Mode       = BallMode.Goto;
        npc.GotoTarget = npc.SpawnPosition;
        if (npc.SpeedFraction <= 0) npc.SpeedFraction = 1.0;

        PyList events = DestinyEventBuilder.BuildGotoPoint(npc.ItemID,
                                                           npc.SpawnPosition.X, npc.SpawnPosition.Y, npc.SpawnPosition.Z);
        mBroadcaster.BroadcastToSystem(SolarSystemID, events);
    }

    /// <summary>
    /// Command an NPC to orbit a target — sets movement state and broadcasts.
    /// </summary>
    private void NpcOrbitTarget(BubbleEntity npc, int targetID, double range)
    {
        npc.Mode           = BallMode.Orbit;
        npc.FollowTargetID = targetID;
        npc.FollowRange    = range;
        if (npc.SpeedFraction <= 0) npc.SpeedFraction = 1.0;

        PyList events = DestinyEventBuilder.BuildOrbit(npc.ItemID, targetID, (float)range);
        mBroadcaster.BroadcastToSystem(SolarSystemID, events);
    }

    /// <summary>
    /// Command an NPC to follow a target — sets movement state and broadcasts.
    /// </summary>
    private void NpcFollowTarget(BubbleEntity npc, int targetID, double range)
    {
        npc.Mode           = BallMode.Follow;
        npc.FollowTargetID = targetID;
        npc.FollowRange    = range;
        if (npc.SpeedFraction <= 0) npc.SpeedFraction = 1.0;

        PyList events = DestinyEventBuilder.BuildFollowBall(npc.ItemID, targetID, (float)range);
        mBroadcaster.BroadcastToSystem(SolarSystemID, events);
    }

    /// <summary>
    /// Get the NPC's faction standing toward a player character.
    /// Uses the NPC's FactionID (or falls back to OwnerID) to query chrNPCStandings.
    /// Returns 0.0 (neutral) if no data is available.
    /// </summary>
    private double GetNpcStandingToPlayer(BubbleEntity npc, int characterID)
    {
        if (mStandingDB == null)
            return 0.0;

        int factionID = npc.FactionID;
        if (factionID == 0)
            factionID = npc.OwnerID; // Fallback: use ownerID as faction

        if (factionID == 0)
            return 0.0;

        try
        {
            return mStandingDB.GetNpcStandingToCharacter(factionID, characterID);
        }
        catch
        {
            return 0.0; // DB error — treat as neutral
        }
    }

    // =====================================================================
    //  CLEANUP
    // =====================================================================

    public bool HasPlayers
    {
        get
        {
            foreach (KeyValuePair <int, BubbleEntity> kvp in mEntities)
                if (kvp.Value.IsPlayer) return true;
            return false;
        }
    }

    public void Dispose()
    {
        if (mDisposed) return;
        mDisposed = true;
        mTickTimer?.Dispose();
        Console.WriteLine($"[DestinyManager] Disposed for system {SolarSystemID}");
    }
}