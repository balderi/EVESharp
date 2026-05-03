using System;

namespace EVESharp.Destiny;

/// <summary>
/// Runtime entity with mutable movement state, suitable for the physics tick loop.
/// Wraps data originally from an ItemEntity but adds velocity, mode, etc.
/// </summary>
public class BubbleEntity
{
    public int    ItemID        { get; set; }
    public int    TypeID        { get; set; }
    public int    GroupID       { get; set; }
    public int    CategoryID    { get; set; }
    public string Name          { get; set; }
    public int    OwnerID       { get; set; }
    public int    CorporationID { get; set; }
    public int    AllianceID    { get; set; }
    public int    CharacterID   { get; set; }

    // 3-D state (mutable)
    public Vector3 Position { get; set; }
    public Vector3 Velocity { get; set; }

    // Movement parameters (doubles to match Apocrypha binary format)
    public BallMode Mode          { get; set; } = BallMode.Stop;
    public BallFlag Flags         { get; set; }
    public double   Radius        { get; set; } = 50.0;
    public double   Mass          { get; set; } = 1000000.0;
    public double   MaxVelocity   { get; set; } = 200.0;
    public double   SpeedFraction { get; set; }
    public double   Agility       { get; set; } = 1.0;

    // Follow / Orbit targets
    public int    FollowTargetID { get; set; }
    public double FollowRange    { get; set; }

    // Goto target
    public Vector3  GotoTarget      { get; set; }

    // Warp target
    public Vector3 WarpTarget      { get; set; }
    public int     WarpEffectStamp { get; set; }

    public bool IsRigid  => Mode == BallMode.Rigid;
    public bool IsPlayer => CharacterID != 0;

    // =====================================================================
    //  NPC AI STATE
    //  These fields are only meaningful for NPC entities (IsNpc == true).
    // =====================================================================

    /// <summary>True if this entity is an NPC (Entity category, no character).</summary>
    public bool IsNpc => CategoryID == 11 && CharacterID == 0;

    /// <summary>The NPC's faction ID for standing lookups (e.g. 500010 = Serpentis).</summary>
    public int FactionID { get; set; }

    /// <summary>Current NPC AI activity state.</summary>
    public NpcAiState AiState { get; set; } = NpcAiState.Idle;

    /// <summary>ItemID of the NPC's current target (player ship).</summary>
    public int AiTargetID { get; set; }

    /// <summary>Range at which NPC detects and engages hostiles (from entityAttackRange).</summary>
    public double AttackRange { get; set; } = 50000.0;

    /// <summary>Preferred orbit range during combat (from entityFlyRange).</summary>
    public double OrbitRange { get; set; } = 8000.0;

    /// <summary>Maximum distance NPC will chase a target (from entityChaseMaxDistance).</summary>
    public double ChaseMaxDistance { get; set; } = 100000.0;

    /// <summary>Seconds elapsed since NPC started AI processing (tick counter).</summary>
    public double AiTimer { get; set; }

    /// <summary>Minimum delay between attacks in seconds (from entityAttackDelayMin).</summary>
    public double AttackDelayMin { get; set; } = 3.0;

    /// <summary>Maximum delay between attacks in seconds (from entityAttackDelayMax).</summary>
    public double AttackDelayMax { get; set; } = 6.0;

    /// <summary>Seconds remaining until next attack is allowed.</summary>
    public double AttackCooldown { get; set; }

    /// <summary>Position where the NPC was spawned. Used to compute chase distance.</summary>
    public Vector3 SpawnPosition { get; set; }

    /// <summary>Standing threshold below which a player is considered hostile. Default 0.</summary>
    public double AggroStandingThreshold { get; set; } = 0.0;

    // =====================================================================
    //  NPC DAMAGE ATTRIBUTES
    //  Base damage values loaded from dgmTypeAttributes for NPC entities.
    // =====================================================================

    public double NpcEmDamage        { get; set; }
    public double NpcExplosiveDamage { get; set; }
    public double NpcKineticDamage   { get; set; }
    public double NpcThermalDamage   { get; set; }

    // =====================================================================
    //  HP / RESISTANCE STATE
    //  Loaded from ship attributes for players, or from type attributes for NPCs.
    // =====================================================================

    // Shield
    public double ShieldCapacity { get; set; } = 100.0;
    public double ShieldCharge   { get; set; } = 100.0;

    // Armor
    public double ArmorHP     { get; set; } = 100.0;
    public double ArmorDamage { get; set; }  // 0 = full armor

    // Structure (hull) — attribute "hp" in EVE
    public double StructureHP     { get; set; } = 100.0;
    public double StructureDamage { get; set; }  // 0 = full hull

    // Shield resistances (resonances): 1.0 = no resist, 0.0 = immune
    public double ShieldEmResonance        { get; set; } = 1.0;
    public double ShieldExplosiveResonance { get; set; } = 1.0;
    public double ShieldKineticResonance   { get; set; } = 1.0;
    public double ShieldThermalResonance   { get; set; } = 1.0;

    // Armor resistances
    public double ArmorEmResonance        { get; set; } = 1.0;
    public double ArmorExplosiveResonance { get; set; } = 1.0;
    public double ArmorKineticResonance   { get; set; } = 1.0;
    public double ArmorThermalResonance   { get; set; } = 1.0;

    // Hull resistances
    public double HullEmResonance        { get; set; } = 1.0;
    public double HullExplosiveResonance { get; set; } = 1.0;
    public double HullKineticResonance   { get; set; } = 1.0;
    public double HullThermalResonance   { get; set; } = 1.0;

    public double SignatureRadius { get; set; } = 100.0;

    // Computed fractions (0.0 = dead, 1.0 = full)
    public double ShieldFraction => ShieldCapacity > 0 ? Math.Clamp(ShieldCharge / ShieldCapacity,    0, 1) : 1.0;
    public double ArmorFraction  => ArmorHP > 0 ? Math.Clamp(1.0 - ArmorDamage / ArmorHP,             0, 1) : 1.0;
    public double HullFraction   => StructureHP > 0 ? Math.Clamp(1.0 - StructureDamage / StructureHP, 0, 1) : 1.0;

    public bool IsDestroyed      => HullFraction <= 0;

    /// <summary>Flagged when hull reaches 0 — beyonce picks this up to handle player death.</summary>
    public bool PendingDestruction { get; set; }

    /// <summary>
    /// Build a Ball struct suitable for DestinyBinaryEncoder (Apocrypha format).
    /// </summary>
    public Ball ToBall()
    {
        Ball ball = new Ball
        {
            Header = new BallHeader
            {
                ItemId   = ItemID,
                Mode     = Mode,
                Radius   = Radius,
                Location = Position,
                Flags    = Flags
            },
            FormationId = 0xFF
        };

        if (Mode != BallMode.Rigid)
        {
            ball.ExtraHeader = new ExtraBallHeader
            {
                Mass          = Mass,
                CloakMode     = CloakMode.None,
                Harmonic      = 0xFFFFFFFFFFFFFFFF,
                CorporationId = CorporationID,
                AllianceId    = AllianceID
            };

            if (Flags.HasFlag(BallFlag.IsFree))
            {
                ball.Data = new BallData
                {
                    MaxVelocity   = MaxVelocity,
                    Velocity      = Velocity,
                    UnknownVec    = default (Vector3),
                    Agility       = Agility,
                    SpeedFraction = SpeedFraction
                };
            }
        }

        // Mode-specific state
        switch (Mode)
        {
            case BallMode.Follow:
            case BallMode.Orbit:
                ball.FollowState = new FollowState
                {
                    FollowId    = FollowTargetID,
                    FollowRange = FollowRange
                };
                break;
            case BallMode.Goto:
                ball.GotoState = new GotoState { Location = GotoTarget };
                break;
            case BallMode.Warp:
                ball.WarpState = new WarpState
                {
                    Location    = WarpTarget,
                    EffectStamp = WarpEffectStamp,
                    FollowRange = 0,
                    FollowId    = 0,
                    OwnerId     = OwnerID
                };
                break;
            case BallMode.Missile:
                ball.MissileState = new MissileState
                {
                    FollowId    = FollowTargetID,
                    FollowRange = 0,
                    OwnerId     = OwnerID
                };
                break;
        }

        return ball;
    }
}