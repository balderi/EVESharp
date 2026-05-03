using System;
using System.Collections.Generic;
using EVESharp.Destiny;
using EVESharp.EVE.Notifications;
using EVESharp.Node.Services.Dogma;
using EVESharp.Node.Services.Space;
using EVESharp.Types;
using EVESharp.Types.Collections;

namespace EVESharp.Node.Services.Combat;

/// <summary>
/// Singleton service for damage calculation and application.
/// Handles turret, missile, and NPC damage with EVE's HP layer system.
/// </summary>
public class CombatService
{
    private readonly INotificationSender mNotifications;
    private readonly DestinyBroadcaster  mBroadcaster;
    private readonly TargetManager       mTargetManager;
    private readonly WeaponCycler        mWeaponCycler;
    private readonly PlayerDeathHandler  mPlayerDeath;
    private readonly Random              mRng = new Random ();

    public CombatService (INotificationSender notifications, DestinyBroadcaster broadcaster,
                          TargetManager targetManager, WeaponCycler weaponCycler, PlayerDeathHandler playerDeathHandler)
    {
        mNotifications = notifications;
        mBroadcaster   = broadcaster;
        mTargetManager = targetManager;
        mWeaponCycler  = weaponCycler;
        mPlayerDeath   = playerDeathHandler;
    }

    // =====================================================================
    //  TURRET DAMAGE
    // =====================================================================

    /// <summary>
    /// Apply turret damage using EVE's hit chance formula.
    /// </summary>
    public void ApplyTurretDamage (int    solarSystemID, BubbleEntity attacker, BubbleEntity target,
                                   double emDmg,         double expDmg, double kinDmg, double thermDmg, double dmgMult,
                                   double maxRange,      double falloff, double tracking)
    {
        double dist = (target.Position - attacker.Position).Length;

        Console.WriteLine ($"[CombatService] ApplyTurretDamage: attacker={attacker.Name}({attacker.ItemID}) -> target={target.Name}({target.ItemID}), dist={dist:F0}m, range={maxRange:F0}, falloff={falloff:F0}, tracking={tracking:F4}");

        // EVE hit chance formula
        double hitChance = CalculateHitChance (dist,              maxRange,        falloff, tracking,
                                               attacker.Velocity, target.Velocity, target.SignatureRadius);

        double roll = mRng.NextDouble ();
        Console.WriteLine ($"[CombatService] Turret hitChance={hitChance:P1}, roll={roll:F3}");

        if (roll > hitChance)
        {
            Console.WriteLine ($"[CombatService] Turret MISS: {attacker.Name} -> {target.Name}");
            return; // Miss
        }

        // Damage quality (wrecking hit, etc.) — simplified: scale between 0.5 and 3.0
        double quality = CalculateDamageQuality (hitChance, roll);
        Console.WriteLine ($"[CombatService] Turret HIT: quality={quality:F2}x, dmgMult={dmgMult:F2}");

        ApplyDamage (solarSystemID, attacker, target,
                     emDmg * dmgMult * quality,
                     expDmg * dmgMult * quality,
                     kinDmg * dmgMult * quality,
                     thermDmg * dmgMult * quality);
    }

    // =====================================================================
    //  MISSILE DAMAGE
    // =====================================================================

    /// <summary>
    /// Apply missile damage using EVE's explosion radius/velocity formula.
    /// </summary>
    public void ApplyMissileDamage (int    solarSystemID, BubbleEntity attacker, BubbleEntity target,
                                    double emDmg, double expDmg, double kinDmg, double thermDmg, double dmgMult,
                                    double explosionRadius, double explosionVelocity)
    {
        // Missile damage reduction formula:
        // reduction = MIN(1, sigRadius/expRadius, (sigRadius/expRadius * expVelocity/velocity)^(ln(drf)/ln(5.5)))
        // Simplified: just use sig radius ratio
        double sigRatio = target.SignatureRadius / Math.Max (explosionRadius, 1.0);
        double velRatio = target.Velocity.Length > 0
            ? explosionVelocity / target.Velocity.Length
            : 1.0;

        double reduction = Math.Min (1.0, Math.Min (sigRatio, sigRatio * velRatio));
        reduction = Math.Max (reduction, 0.01); // minimum 1% damage

        Console.WriteLine ($"[CombatService] ApplyMissileDamage: attacker={attacker.Name}({attacker.ItemID}) -> target={target.Name}({target.ItemID}), sigRatio={sigRatio:F2}, velRatio={velRatio:F2}, reduction={reduction:F2}");

        ApplyDamage (solarSystemID, attacker, target,
                     emDmg * dmgMult * reduction,
                     expDmg * dmgMult * reduction,
                     kinDmg * dmgMult * reduction,
                     thermDmg * dmgMult * reduction);
    }

    // =====================================================================
    //  NPC DAMAGE (flat, no miss chance)
    // =====================================================================

    public void ApplyNpcDamage (int    solarSystemID, BubbleEntity attacker, BubbleEntity target,
                                double emDmg,         double       expDmg,   double       kinDmg, double thermDmg)
    {
        Console.WriteLine ($"[CombatService] ApplyNpcDamage: attacker={attacker.Name}({attacker.ItemID}) -> target={target.Name}({target.ItemID})");
        ApplyDamage (solarSystemID, attacker, target, emDmg, expDmg, kinDmg, thermDmg);
    }

    // =====================================================================
    //  CORE DAMAGE APPLICATION
    //  Shield -> Armor -> Hull, each with per-type resonances
    // =====================================================================

    private void ApplyDamage (int    solarSystemID, BubbleEntity attacker, BubbleEntity target,
                              double emDmg,         double       expDmg,   double       kinDmg, double thermDmg)
    {
        // Stations, celestials, stargates are invulnerable (they have IsGlobal, not IsFree)
        if (!target.Flags.HasFlag (BallFlag.IsFree))
        {
            Console.WriteLine ($"[CombatService] ApplyDamage: target {target.Name}({target.ItemID}) is NOT IsFree (flags={target.Flags}), invulnerable - skipping");
            return;
        }

        double totalRawDmg = emDmg + expDmg + kinDmg + thermDmg;
        if (totalRawDmg <= 0) return;

        Console.WriteLine ($"[CombatService] ApplyDamage: {attacker.Name}({attacker.ItemID}) -> {target.Name}({target.ItemID}), raw total={totalRawDmg:F1} (em={emDmg:F1},exp={expDmg:F1},kin={kinDmg:F1},therm={thermDmg:F1})");

        // ---- Shield layer ----
        double shieldDmg = emDmg   * target.ShieldEmResonance
                           + expDmg  * target.ShieldExplosiveResonance
                           + kinDmg  * target.ShieldKineticResonance
                           + thermDmg * target.ShieldThermalResonance;

        double shieldRemaining = target.ShieldCharge;
        if (shieldDmg <= shieldRemaining)
        {
            target.ShieldCharge -= shieldDmg;
            BroadcastDamageStateChange (solarSystemID, target);
            SendCombatLogMessage (solarSystemID, attacker, target, shieldDmg, "shield");
            Console.WriteLine ($"[CombatService] Hit {target.Name} shield for {shieldDmg:F1} (shield={target.ShieldFraction:P0})");
            return;
        }

        // Shield depleted, overflow to armor
        double shieldOverflowFraction = shieldRemaining > 0 ? 1.0 - (shieldRemaining / shieldDmg) : 1.0;
        target.ShieldCharge = 0;

        // ---- Armor layer ----
        double armorDmg = (emDmg   * target.ArmorEmResonance
                           + expDmg  * target.ArmorExplosiveResonance
                           + kinDmg  * target.ArmorKineticResonance
                           + thermDmg * target.ArmorThermalResonance) * shieldOverflowFraction;

        double armorRemaining = target.ArmorHP - target.ArmorDamage;
        if (armorDmg <= armorRemaining)
        {
            target.ArmorDamage += armorDmg;
            BroadcastDamageStateChange (solarSystemID, target);
            double totalApplied = shieldRemaining + armorDmg;
            SendCombatLogMessage (solarSystemID, attacker, target, totalApplied, "armor");
            Console.WriteLine ($"[CombatService] Hit {target.Name} armor for {armorDmg:F1} (armor={target.ArmorFraction:P0})");
            return;
        }

        // Armor depleted, overflow to hull
        double armorOverflowFraction = armorRemaining > 0 ? 1.0 - (armorRemaining / armorDmg) : 1.0;
        target.ArmorDamage = target.ArmorHP;

        // ---- Hull layer ----
        double hullDmg = (emDmg   * target.HullEmResonance
                          + expDmg  * target.HullExplosiveResonance
                          + kinDmg  * target.HullKineticResonance
                          + thermDmg * target.HullThermalResonance) * shieldOverflowFraction * armorOverflowFraction;

        target.StructureDamage += hullDmg;
        if (target.StructureDamage > target.StructureHP)
            target.StructureDamage = target.StructureHP;

        BroadcastDamageStateChange (solarSystemID, target);
        double totalHullApplied = shieldRemaining + armorRemaining + hullDmg;
        SendCombatLogMessage (solarSystemID, attacker, target, totalHullApplied, "hull");
        Console.WriteLine ($"[CombatService] Hit {target.Name} hull for {hullDmg:F1} (hull={target.HullFraction:P0})");
    }

    // =====================================================================
    //  DAMAGE STATE BROADCAST
    // =====================================================================

    private void BroadcastDamageStateChange (int solarSystemID, BubbleEntity target)
    {
        Console.WriteLine ($"[CombatService] OnDamageStateChange: item={target.ItemID} shield={target.ShieldFraction:F3} armor={target.ArmorFraction:F3} hull={target.HullFraction:F3}");

        mBroadcaster?.BroadcastDamageStateChange (solarSystemID, target.ItemID,
                                                  target.ShieldFraction, target.ArmorFraction, target.HullFraction);
    }

    // =====================================================================
    //  COMBAT LOG (OnDamageMessage)
    // =====================================================================

    /// <summary>
    /// Send OnDamageMessage to the attacker and target characters so they see
    /// combat text in the client's combat log window.
    ///
    /// The EVE Apocrypha client expects:
    ///   OnDamageMessage(self, msgKey, args)
    /// where msgKey is a string like "AttackHit1" (you hit) or "AttackHit2" (you were hit),
    /// and args is a PyDictionary with keys: "damage", "source", "target", "owner".
    /// The client resolves display names from item IDs via the bracket service.
    /// </summary>
    private void SendCombatLogMessage (int    solarSystemID, BubbleEntity attacker, BubbleEntity target,
                                       double totalDamage,   string       layerHit)
    {
        try
        {
            // Send "AttackHit1" to the attacker (you dealt damage)
            if (attacker.CharacterID != 0)
            {
                PyDictionary argsDict = new PyDictionary ();
                argsDict [new PyString ("damage")] = new PyDecimal (totalDamage);
                argsDict [new PyString ("source")] = new PyInteger (attacker.ItemID);
                argsDict [new PyString ("target")] = new PyInteger (target.ItemID);
                argsDict [new PyString ("owner")]  = new PyInteger (attacker.CharacterID);

                PyTuple data = new PyTuple (2)
                {
                    [0] = new PyString ("AttackHit1"),
                    [1] = argsDict
                };

                mNotifications.SendNotification ("OnDamageMessage", "charid", attacker.CharacterID, data);
                Console.WriteLine ($"[CombatService] OnDamageMessage AttackHit1 sent to attacker char={attacker.CharacterID}: {attacker.Name} hit {target.Name} for {totalDamage:F1} ({layerHit})");
            }

            // Send "AttackHit2" to the target (you were hit)
            if (target.CharacterID != 0 && target.CharacterID != attacker.CharacterID)
            {
                PyDictionary argsDict = new PyDictionary ();
                argsDict [new PyString ("damage")] = new PyDecimal (totalDamage);
                argsDict [new PyString ("source")] = new PyInteger (attacker.ItemID);
                argsDict [new PyString ("target")] = new PyInteger (target.ItemID);
                argsDict [new PyString ("owner")]  = new PyInteger (attacker.CharacterID);

                PyTuple data = new PyTuple (2)
                {
                    [0] = new PyString ("AttackHit2"),
                    [1] = argsDict
                };

                mNotifications.SendNotification ("OnDamageMessage", "charid", target.CharacterID, data);
                Console.WriteLine ($"[CombatService] OnDamageMessage AttackHit2 sent to target char={target.CharacterID}: {attacker.Name} hit {target.Name} for {totalDamage:F1} ({layerHit})");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine ($"[CombatService] SendCombatLogMessage error: {ex.Message}");
        }
    }

    // =====================================================================
    //  DESTRUCTION HANDLING
    // =====================================================================

    public void HandleEntityDestruction (int solarSystemID, BubbleEntity target, DestinyManager destinyMgr)
    {
        Console.WriteLine ($"[CombatService] Entity destroyed: {target.Name} ({target.ItemID})");

        // --- Common cleanup for ALL entities ---

        // 1) Clear all target locks on/from this entity and broadcast OnTarget "clear"
        if (mTargetManager != null)
        {
            List <(int LockerID, int TargetID)> cleared = mTargetManager.ClearEntity (target.ItemID);
            foreach ((int lockerID, int targetID) in cleared)
                mBroadcaster?.SendOnTargetToCharacter (lockerID, "clear", targetID);
        }

        // 2) Stop all weapons targeting this entity
        mWeaponCycler?.StopAllTargeting (target.ItemID);

        // --- Entity-type-specific handling ---

        if (target.IsNpc)
        {
            // Send TerminalExplosion so client shows explosion FX before the ball is removed
            mBroadcaster?.BroadcastTerminalExplosion (solarSystemID, target.ItemID);

            // NPC: remove from destiny and broadcast RemoveBalls
            destinyMgr.UnregisterEntity (target.ItemID);

            PyList events       = DestinyEventBuilder.BuildRemoveBalls (new[] { target.ItemID });
            PyTuple notification = DestinyEventBuilder.WrapAsNotification (events);
            mNotifications.SendNotification ("DoDestinyUpdate", "solarsystemid", solarSystemID, notification);
        }
        else if (target.IsPlayer)
        {
            // Player ship: delegate to PlayerDeathHandler (handles capsule spawn / pod kill)
            mPlayerDeath?.HandlePlayerShipDestroyed (solarSystemID, target, destinyMgr);
        }
    }

    public bool CheckDestruction (BubbleEntity target)
    {
        return target.IsDestroyed;
    }

    // =====================================================================
    //  HIT CHANCE FORMULA
    // =====================================================================

    /// <summary>
    /// Simplified EVE turret hit chance:
    /// chance = 0.5 ^ ((transversal / (tracking * sigRadius))^2 + (max(0, dist - optRange) / falloff)^2)
    /// </summary>
    private static double CalculateHitChance (double distance, double optimalRange, double falloff,
                                              double tracking, Vector3 attackerVel, Vector3 targetVel, double signatureRadius)
    {
        // Transversal velocity (simplified - use velocity difference magnitude)
        Vector3 relVel      = targetVel - attackerVel;
        double  transversal = relVel.Length;

        double trackingTerm = signatureRadius > 0 && tracking > 0
            ? transversal / (tracking * signatureRadius)
            : 0;

        double rangeTerm = falloff > 0
            ? Math.Max (0, distance - optimalRange) / falloff
            : 0;

        double exponent = trackingTerm * trackingTerm + rangeTerm * rangeTerm;
        return Math.Pow (0.5, exponent);
    }

    /// <summary>
    /// Damage quality roll. Simplified version:
    /// Low roll = wrecking hit (3x), average = normal (1x), high roll = graze (0.5x).
    /// </summary>
    private double CalculateDamageQuality (double hitChance, double roll)
    {
        // ratio of roll to hitChance
        double ratio = hitChance > 0 ? roll / hitChance : 1.0;
        if (ratio < 0.01)
            return 3.0; // Wrecking hit
        if (ratio < 0.5)
            return 1.0 + (0.5 - ratio) * 2.0; // Good hit
        return Math.Max (0.5, 1.0 - (ratio - 0.5)); // Glancing
    }
}