using System;
using System.Collections.Generic;
using EVESharp.Database;
using EVESharp.Database.Account;
using EVESharp.Database.Dogma;
using EVESharp.Database.Extensions;
using EVESharp.Database.Inventory;
using EVESharp.Database.Inventory.Attributes;
using EVESharp.Database.Inventory.Groups;
using EVESharp.Database.Types;
using EVESharp.Destiny;
using EVESharp.EVE.Data.Inventory;
using EVESharp.EVE.Data.Inventory.Items;
using EVESharp.EVE.Data.Inventory.Items.Dogma;
using EVESharp.EVE.Data.Inventory.Items.Types;
using EVESharp.EVE.Exceptions;
using EVESharp.EVE.Exceptions.inventory;
using EVESharp.EVE.Network.Services;
using EVESharp.EVE.Network.Services.Validators;
using EVESharp.EVE.Notifications;
using EVESharp.EVE.Notifications.Inventory;
using EVESharp.EVE.Notifications.Station;
using EVESharp.EVE.Packets.Complex;
using EVESharp.EVE.Sessions;
using EVESharp.EVE.Types;
using EVESharp.Node.Dogma;
using EVESharp.Node.Services.Combat;
using EVESharp.Node.Services.Space;
using EVESharp.Types;
using EVESharp.Types.Collections;
using System.Linq;
using EVESharp.EVE.Dogma;
using Serilog;

namespace EVESharp.Node.Services.Dogma;

[MustBeCharacter]
public class dogmaIM : ClientBoundService
{
    public override AccessLevel AccessLevel => AccessLevel.None;

    private IItems                     Items             { get; }
    private IAttributes                Attributes        => Items.Attributes;
    private IDefaultAttributes         DefaultAttributes => Items.DefaultAttributes;
    private ISolarSystems              SolarSystems      { get; }
    private INotificationSender        Notifications     { get; }
    private IDogmaNotifications        DogmaNotifications { get; }
    private EffectsManager             EffectsManager    { get; }
    private IDatabase                  Database          { get; }
    private TargetManager              TargetMgr         { get; }
    private SolarSystemDestinyManager  SolarSystemDestinyMgr { get; }
    private DestinyBroadcaster         Broadcaster       { get; }
    private CombatService              Combat            { get; }
    private WeaponCycler               Cycler            { get; }
    private ILogger                    Log               { get; }
    private bool                       mIsStationBound;

    public dogmaIM
    (
        EffectsManager effectsManager, IItems items, INotificationSender notificationSender, IDogmaNotifications dogmaNotifications,
        IBoundServiceManager manager, IDatabase database,
        ISolarSystems  solarSystems, TargetManager targetManager, SolarSystemDestinyManager solarSystemDestinyMgr,
        DestinyBroadcaster broadcaster, CombatService combatService, WeaponCycler weaponCycler, ILogger logger
    ) : base (manager)
    {
        EffectsManager        = effectsManager;
        Items                 = items;
        Notifications         = notificationSender;
        DogmaNotifications    = dogmaNotifications;
        Database              = database;
        SolarSystems          = solarSystems;
        TargetMgr             = targetManager;
        SolarSystemDestinyMgr = solarSystemDestinyMgr;
        Broadcaster           = broadcaster;
        Combat                = combatService;
        Cycler                = weaponCycler;
        Log                   = logger;

        // Wire up weapon cycler callbacks
        if (Cycler != null)
        {
            Cycler.OnCycleFire = ctx =>
            {
                try
                {
                    if (!FireWeapon (ctx.Session, ctx.Module, ctx.TargetID))
                        Cycler.StopCycling (ctx.ModuleID);
                }
                catch (Exception ex) { Log.Error (ex, "[WeaponCycler] Fire error: {Message}", ex.Message); }
            };
            Cycler.OnCycleValidate = ctx =>
            {
                // Validate target still locked and exists in destiny
                if (!TargetMgr.IsLocked (ctx.CharacterID, ctx.TargetID))
                    return false;
                if (ctx.SolarSystemID == 0 || !SolarSystemDestinyMgr.TryGet (ctx.SolarSystemID, out DestinyManager dm))
                    return false;
                if (!dm.TryGetEntity (ctx.TargetID, out BubbleEntity t) || t.IsDestroyed)
                    return false;

                // Validate ammo still loaded
                int shipID = ctx.Session.ShipID ?? 0;
                if (shipID != 0)
                {
                    Ship ship = Items.LoadItem <Ship> (shipID);
                    if (ship?.Items.Values.FirstOrDefault (i => i.Flag == ctx.Module.Flag && !(i is ShipModule)) == null)
                        return false;
                }

                return true;
            };
            Cycler.OnCycleStop = ctx =>
            {
                try
                {
                    EffectsManager.GetForItem (ctx.Module, ctx.Session)?.SafeDeactivateEffect (ctx.EffectName, ctx.Session);
                }
                catch (Exception ex)
                {
                    Log.Error (ex, "[dogmaIM] OnCycleStop deactivation error: {Message}", ex.Message);
                }
            };
        }
    }

    protected dogmaIM
    (
        int     locationID, EffectsManager effectsManager, IItems items, INotificationSender notificationSender, IDogmaNotifications dogmaNotifications,
        IBoundServiceManager manager,
        Session session,    ISolarSystems  solarSystems, TargetManager targetManager, SolarSystemDestinyManager solarSystemDestinyMgr,
        DestinyBroadcaster broadcaster, CombatService combatService, WeaponCycler weaponCycler, ILogger logger
    ) : base (manager, session, locationID)
    {
        EffectsManager        = effectsManager;
        Items                 = items;
        Notifications         = notificationSender;
        DogmaNotifications    = dogmaNotifications;
        SolarSystems          = solarSystems;
        TargetMgr             = targetManager;
        SolarSystemDestinyMgr = solarSystemDestinyMgr;
        Broadcaster           = broadcaster;
        Combat                = combatService;
        Cycler                = weaponCycler;
        Log                   = logger;
        mIsStationBound       = (session.StationID != 0 && session.StationID == locationID);
    }

    public PyDataType ShipGetInfo (ServiceCall call)
    {
        int  callerCharacterID = call.Session.CharacterID;
        int? shipID            = call.Session.ShipID;

        if (shipID is null)
            throw new CustomError ("The character is not aboard any ship");

        Ship ship = Items.LoadItem <Ship> ((int) shipID);

        if (ship is null)
            throw new CustomError ($"Cannot get information for ship {call.Session.ShipID}");

        ship.EnsureOwnership (callerCharacterID, call.Session.CorporationID, call.Session.CorporationRole, true);

        // Ensure shieldCharge is set to shieldCapacity if not already present.
        // Unlike armor (armorDamage defaults to 0 = full) and structure (damage defaults to 0 = full),
        // shields use shieldCharge which defaults to 0 = EMPTY. We must initialize it.
        if (!ship.Attributes.TryGetAttribute (AttributeTypes.shieldCharge, out _))
        {
            double shieldCap = ship.Attributes [AttributeTypes.shieldCapacity];
            ship.Attributes [AttributeTypes.shieldCharge] = new EVESharp.Database.Inventory.Attributes.Attribute (AttributeTypes.shieldCharge, shieldCap);
        }

        // Ensure warp attributes are set. Without baseWarpSpeed the client's godma proxy
        // calculates warp range as 0, causing "Out of warp range" for all targets.
        if (!ship.Attributes.TryGetAttribute (AttributeTypes.baseWarpSpeed, out _))
        {
            ship.Attributes [AttributeTypes.baseWarpSpeed] = new EVESharp.Database.Inventory.Attributes.Attribute (AttributeTypes.baseWarpSpeed, 1.0);
        }
        if (!ship.Attributes.TryGetAttribute (AttributeTypes.warpSpeedMultiplier, out _))
        {
            ship.Attributes [AttributeTypes.warpSpeedMultiplier] = new EVESharp.Database.Inventory.Attributes.Attribute (AttributeTypes.warpSpeedMultiplier, 1.0);
        }

        // pre-load the character's inventory (skills, implants) so that skill checks
        // during module effect initialization don't fail due to lazy-load timing
        Character character = Items.LoadItem <Character> (callerCharacterID);
        _ = character?.Items;

        ItemInfo itemInfo = new ItemInfo ();
        itemInfo.AddRow (ship.ID, ship.GetEntityRow (), ship.GetEffects (), ship.Attributes, DateTime.UtcNow.ToFileTime ());

        foreach ((int _, ItemEntity item) in ship.Items)
        {
            if (item.IsInModuleSlot () == false && item.IsInRigSlot () == false)
                continue;

            // ensure ItemEffects is initialized so effects list is populated
            // and the online state is restored from the persisted isOnline attribute
            if (item is ShipModule module)
            {
                try
                {
                    EffectsManager.GetForItem (module, call.Session);
                }
                catch (Exception ex)
                {
                    Log.Warning ("[dogmaIM] ShipGetInfo: effect init failed for module {ModuleID} (type={TypeID}): {Message}", module.ID, module.Type.ID, ex.Message);
                }
            }

            itemInfo.AddRow (
                item.ID,
                item.GetEntityRow (),
                item.GetEffects (),
                item.Attributes,
                DateTime.UtcNow.ToFileTime ()
            );
        }

        return itemInfo;
    }

    public PyDataType CharGetInfo (ServiceCall call)
    {
        int callerCharacterID = call.Session.CharacterID;
        Character character = Items.LoadItem <Character> (callerCharacterID);

        if (character is null)
            throw new CustomError ($"Cannot get information for character {callerCharacterID}");

        ItemInfo itemInfo = new ItemInfo ();
        itemInfo.AddRow (character.ID, character.GetEntityRow (), character.GetEffects (), character.Attributes, DateTime.UtcNow.ToFileTime ());

        foreach ((int _, ItemEntity item) in character.Items)
            switch (item.Flag)
            {
                case Flags.Booster:
                case Flags.Implant:
                case Flags.Skill:
                case Flags.SkillInTraining:
                    itemInfo.AddRow (
                        item.ID,
                        item.GetEntityRow (),
                        item.GetEffects (),
                        item.Attributes,
                        DateTime.UtcNow.ToFileTime ()
                    );
                    break;
            }

        return itemInfo;
    }

    public PyDataType ItemGetInfo (ServiceCall call, PyInteger itemID)
    {
        int callerCharacterID = call.Session.CharacterID;
        ItemEntity item = Items.LoadItem (itemID);

        if (item.ID != callerCharacterID && item.OwnerID != callerCharacterID && item.OwnerID != call.Session.CorporationID)
            throw new TheItemIsNotYoursToTake (itemID);

        return new Row (
            new PyList <PyString> (5)
            {
                [0] = "itemID",
                [1] = "invItem",
                [2] = "activeEffects",
                [3] = "attributes",
                [4] = "time"
            },
            new PyList (5)
            {
                [0] = item.ID,
                [1] = item.GetEntityRow (),
                [2] = item.GetEffects (),
                [3] = item.Attributes,
                [4] = DateTime.UtcNow.ToFileTimeUtc ()
            }
        );
    }

    public PyDataType GetWeaponBankInfoForShip (ServiceCall call)
    {
        return new PyDictionary ();
    }

    public PyDataType GetCharacterBaseAttributes (ServiceCall call)
    {
        int callerCharacterID = call.Session.CharacterID;
        Character character = Items.LoadItem <Character> (callerCharacterID);

        if (character is null)
            throw new CustomError ($"Cannot get information for character {callerCharacterID}");

        return new PyDictionary
        {
            [(int) AttributeTypes.willpower]    = character.Willpower,
            [(int) AttributeTypes.charisma]     = character.Charisma,
            [(int) AttributeTypes.intelligence] = character.Intelligence,
            [(int) AttributeTypes.perception]   = character.Perception,
            [(int) AttributeTypes.memory]       = character.Memory
        };
    }

    public PyDataType LogAttribute (ServiceCall call, PyInteger itemID, PyInteger attributeID)
    {
        return this.LogAttribute (call, itemID, attributeID, "");
    }

    public PyList <PyString> LogAttribute (ServiceCall call, PyInteger itemID, PyInteger attributeID, PyString reason)
    {
        ulong role     = call.Session.Role;
        ulong roleMask = (ulong) (Roles.ROLE_GDH | Roles.ROLE_QA | Roles.ROLE_PROGRAMMER | Roles.ROLE_GMH);

        if ((role & roleMask) == 0)
            throw new CustomError ("Not allowed!");

        ItemEntity item = Items.LoadItem (itemID);

        if (item.Attributes.AttributeExists (attributeID) == false)
            throw new CustomError ("The given attribute doesn't exists in the item");

        return new PyList <PyString> (5)
        {
            [0] = null,
            [1] = null,
            [2] = $"Server value: {item.Attributes [attributeID]}",
            [3] = $"Base value: {DefaultAttributes [item.Type.ID] [attributeID]}",
            [4] = $"Reason: {reason}"
        };
    }

    public PyDataType Activate (ServiceCall call, PyInteger itemID, PyString effectName, PyDataType target, PyDataType repeat)
    {
        ShipModule module = Items.LoadItem <ShipModule> (itemID);

        // Extract target ID if provided
        int targetID = 0;
        if (target is PyInteger targetInt)
            targetID = (int) targetInt.Value;

        // Extract repeat flag
        bool shouldRepeat = false;
        if (repeat is PyInteger repeatInt)
            shouldRepeat = repeatInt.Value != 0;
        else if (repeat is PyBool repeatBool)
            shouldRepeat = repeatBool.Value;

        Log.Information ("[dogmaIM] Activate: char={CharID}, item={ItemID}, effect={Effect}, target={TargetID}, repeat={Repeat}, groupID={GroupID}",
            call.Session.CharacterID, itemID?.Value, effectName?.Value, targetID, shouldRepeat, module.Type.Group.ID);

        bool isWeapon = IsWeaponGroup (module.Type.Group.ID);
        Log.Information ("[dogmaIM] Activate: IsWeaponGroup={IsWeapon}, targetID={TargetID}", isWeapon, targetID);

        // Pre-validate ammo before activating the effect — prevents module
        // appearing active on the client when there's nothing to fire
        if (isWeapon && targetID != 0)
        {
            int shipID = call.Session.ShipID ?? 0;
            if (shipID != 0)
            {
                Ship ship = Items.LoadItem <Ship> (shipID);
                ItemEntity charge = ship?.Items.Values
                    .FirstOrDefault (i => i.Flag == module.Flag && !(i is ShipModule));
                if (charge == null)
                    throw new CustomError ("No ammo loaded");
            }
        }

        // Set TargetID on the effect so the notification includes it
        ItemEffects effects = EffectsManager.GetForItem (module, call.Session);
        Effect effectDef = null;
        if (effects != null && module.Type.EffectsByName.TryGetValue (effectName, out effectDef))
        {
            if (module.Effects.TryGetEffect (effectDef.EffectID, out GodmaShipEffect godmaEffect))
                godmaEffect.TargetID = targetID;
        }

        // Pass target + charge to the effect system so the VM can resolve Environment.Target / Environment.Charge
        ItemEntity targetEntity = null;
        ItemEntity chargeEntity = null;
        if (isWeapon && targetID != 0)
        {
            try { targetEntity = Items.LoadItem (targetID); } catch { }

            int activateShipID = call.Session.ShipID ?? 0;
            if (activateShipID != 0)
            {
                Ship activateShip = Items.LoadItem <Ship> (activateShipID);
                chargeEntity = activateShip?.Items.Values
                    .FirstOrDefault (i => i.Flag == module.Flag && !(i is ShipModule));
            }
        }

        effects?.ApplyEffect (effectName, call.Session, targetEntity, chargeEntity);
        Log.Information ("[dogmaIM] ApplyEffect: effectName={EffectName}, ownerID={OwnerID}, TargetID={TargetID}",
            effectName?.Value, module.OwnerID, targetID);

        // If this is a weapon module with a valid target, fire it
        if (targetID != 0 && isWeapon)
        {
            // Validate target is locked
            int charID = call.Session.CharacterID;
            bool isLocked = TargetMgr.IsLocked (charID, targetID);
            Log.Information ("[dogmaIM] Activate: target lock check: IsLocked({CharID}, {TargetID})={IsLocked}", charID, targetID, isLocked);
            if (!isLocked)
            {
                effects?.SafeDeactivateEffect (effectName, call.Session);
                throw new CustomError ("Target is not locked");
            }

            bool fired = FireWeapon (call.Session, module, targetID);
            Log.Information ("[dogmaIM] Activate: FireWeapon={Fired}, starting cycler={StartCycler}", fired, fired && shouldRepeat && Cycler != null);

            if (!fired)
            {
                // Ammo disappeared between pre-check and fire — deactivate
                effects?.SafeDeactivateEffect (effectName, call.Session);
                return null;
            }

            // Start auto-repeat cycling if requested
            if (shouldRepeat && Cycler != null)
            {
                double durationMs = 5000; // default 5s cycle
                if (effectDef?.DurationAttributeID != null && module.Attributes.AttributeExists ((AttributeTypes) effectDef.DurationAttributeID.Value))
                    durationMs = (double) module.Attributes [(AttributeTypes) effectDef.DurationAttributeID.Value];

                WeaponCycleContext ctx = new WeaponCycleContext
                {
                    ModuleID        = module.ID,
                    ModuleTypeID    = module.Type.ID,
                    ModuleGroupID   = module.Type.Group.ID,
                    TargetID        = targetID,
                    CharacterID     = charID,
                    ShipID          = call.Session.ShipID ?? 0,
                    SolarSystemID   = call.Session.SolarSystemID ?? 0,
                    DurationMs      = durationMs,
                    EffectName      = effectName,
                    Module          = module,
                    Session         = call.Session
                };
                Cycler.StartCycling (ctx);
            }
        }

        return null;
    }

    public PyDataType Deactivate (ServiceCall call, PyInteger itemID, PyString effectName)
    {
        ShipModule module = Items.LoadItem <ShipModule> (itemID);
        EffectsManager.GetForItem (module, call.Session).SafeDeactivateEffect (effectName, call.Session);

        // Stop weapon cycling
        Cycler?.StopCycling (module.ID);

        return null;
    }

    // =====================================================================
    //  TARGET LOCK / UNLOCK
    // =====================================================================

    public PyDataType AddTarget (ServiceCall call, PyInteger targetID)
    {
        return LockTarget (call, targetID);
    }

    public PyDataType LockTarget (ServiceCall call, PyInteger targetID)
    {
        int charID  = call.Session.CharacterID;
        int tgtID   = (int) targetID.Value;
        int shipID  = call.Session.ShipID ?? 0;
        int ssID    = call.Session.SolarSystemID ?? 0;

        // Get max locked targets and targeting range from character's ship.
        // Use the implicit double operator (not .Float) because the attribute
        // may be stored as Integer — .Float only reads mFloat which stays 0
        // for integer-typed attributes, causing maxTargets=0 and all locks to fail.
        int maxTargets = 2; // fallback
        double scanRes = 100; // fallback scan resolution
        double maxTargetRange = 50000; // fallback 50km
        Ship ship = null;
        if (shipID != 0)
        {
            ship = Items.LoadItem <Ship> (shipID);
            if (ship?.Attributes != null)
            {
                if (ship.Attributes.AttributeExists (AttributeTypes.maxLockedTargets))
                    maxTargets = (int) (double) ship.Attributes [AttributeTypes.maxLockedTargets];
                if (ship.Attributes.AttributeExists (AttributeTypes.scanResolution))
                    scanRes = (double) ship.Attributes [AttributeTypes.scanResolution];
                if (ship.Attributes.AttributeExists (AttributeTypes.maxTargetRange))
                    maxTargetRange = (double) ship.Attributes [AttributeTypes.maxTargetRange];
            }
        }

        // Validate target exists in destiny and check targeting range
        if (SolarSystemDestinyMgr != null && SolarSystemDestinyMgr.TryGet (ssID, out DestinyManager destinyMgr))
        {
            if (!destinyMgr.TryGetEntity (tgtID, out BubbleEntity tgtEntity))
                throw new CustomError ("Target does not exist in space");

            if (destinyMgr.TryGetEntity (shipID, out BubbleEntity shipEntity))
            {
                double dist = (tgtEntity.Position - shipEntity.Position).Length;
                if (dist > maxTargetRange)
                    throw new CustomError ($"Target is out of range ({dist / 1000.0:F1} km / {maxTargetRange / 1000.0:F1} km max)");
            }
        }

        if (!TargetMgr.LockTarget (charID, tgtID, maxTargets))
            throw new CustomError ("Cannot lock target (max targets reached or already locked)");

        // Calculate lock time: 40000 / (scanRes * asinh(sigRadius)^2) seconds
        double sigRadius = 100; // fallback
        ItemEntity targetItem = null;
        try { targetItem = Items.LoadItem (tgtID); } catch { }
        if (targetItem?.Attributes != null && targetItem.Attributes.AttributeExists (AttributeTypes.signatureRadius))
            sigRadius = (double) targetItem.Attributes [AttributeTypes.signatureRadius];

        double asinh = Math.Log (sigRadius + Math.Sqrt (sigRadius * sigRadius + 1));
        double lockTimeSec = 40000.0 / (scanRes * asinh * asinh);
        if (lockTimeSec < 1.0) lockTimeSec = 1.0;
        if (lockTimeSec > 30.0) lockTimeSec = 30.0;
        long lockTimeMs = (long) (lockTimeSec * 1000);

        // Send targeted OnTarget "otheradd" to the target player (if it is a player)
        // The attacker handles their own lock via the AddTarget return value — no broadcast needed.
        if (Broadcaster != null && SolarSystemDestinyMgr != null && SolarSystemDestinyMgr.TryGet (ssID, out DestinyManager lockDestinyMgr))
        {
            if (lockDestinyMgr.TryGetEntity (tgtID, out BubbleEntity targetEntity) && targetEntity.CharacterID != 0)
                Broadcaster.SendOnTargetToCharacter (targetEntity.CharacterID, "otheradd", charID);
        }

        Log.Information ("[dogmaIM] LockTarget: char={CharID} locked target={TargetID} (max={MaxTargets}, lockTime={LockTimeMs}ms)", charID, tgtID, maxTargets, lockTimeMs);

        // Client unpacks: (flag, targetList) = self.GetDogmaLM().AddTarget(tid)
        // flag=0 means instant lock (client calls OnTargetAdded immediately)
        return new PyTuple (2)
        {
            [0] = new PyInteger (0),   // flag: 0 = instant lock
            [1] = new PyList ()        // targetList (unused by client after unpack)
        };
    }

    public PyDataType RemoveTarget (ServiceCall call, PyInteger targetID)
    {
        return UnlockTarget (call, targetID);
    }

    public PyDataType UnlockTarget (ServiceCall call, PyInteger targetID)
    {
        int charID = call.Session.CharacterID;
        int tgtID  = (int) targetID.Value;
        int ssID   = call.Session.SolarSystemID ?? 0;

        TargetMgr.UnlockTarget (charID, tgtID);

        // Stop any weapon cycles targeting this entity
        Cycler?.StopAllTargeting (tgtID);

        // Send targeted OnTarget "otherlost" to the target player (if it is a player)
        if (Broadcaster != null && SolarSystemDestinyMgr != null && SolarSystemDestinyMgr.TryGet (ssID, out DestinyManager unlockDestinyMgr))
        {
            if (unlockDestinyMgr.TryGetEntity (tgtID, out BubbleEntity targetEntity) && targetEntity.CharacterID != 0)
                Broadcaster.SendOnTargetToCharacter (targetEntity.CharacterID, "otherlost", charID);
        }

        Log.Information ("[dogmaIM] UnlockTarget: char={CharID} unlocked target={TargetID}", charID, tgtID);
        return null;
    }

    public PyDataType CheckSendLocationInfo(ServiceCall call)
    {
        return null;
    }

    public PyDataType GetTargets(ServiceCall call)
    {
        int        charID  = call.Session.CharacterID;
        List <int> targets = TargetMgr.GetTargets (charID);
        PyList        result  = new PyList ();
        foreach (int t in targets)
            result.Add (new PyInteger (t));
        return result;
    }

    // =====================================================================
    //  WEAPON FIRING
    // =====================================================================

    internal bool FireWeapon (Session session, ShipModule module, int targetID)
    {
        int charID  = session.CharacterID;
        int shipID  = session.ShipID ?? 0;
        int ssID    = session.SolarSystemID ?? 0;

        Log.Information ("[dogmaIM] FireWeapon: char={CharID}, ship={ShipID}, target={TargetID}, ssID={SsID}, moduleType={ModuleTypeID} ({ModuleName}), groupID={GroupID}",
            charID, shipID, targetID, ssID, module.Type.ID, module.Type.Name, module.Type.Group.ID);

        if (ssID == 0 || !SolarSystemDestinyMgr.TryGet (ssID, out DestinyManager destinyMgr))
        {
            Log.Warning ("[dogmaIM] FireWeapon: FAIL - SolarSystemDestinyMgr.TryGet failed for ssID={SsID}", ssID);
            return false;
        }

        if (!destinyMgr.TryGetEntity (shipID, out BubbleEntity attackerBubble))
        {
            Log.Warning ("[dogmaIM] FireWeapon: FAIL - TryGetEntity(shipID={ShipID}) not found in DestinyManager", shipID);
            return false;
        }
        if (!destinyMgr.TryGetEntity (targetID, out BubbleEntity targetBubble))
        {
            Log.Warning ("[dogmaIM] FireWeapon: FAIL - TryGetEntity(targetID={TargetID}) not found in DestinyManager", targetID);
            return false;
        }

        // Find the loaded charge in the same slot as the module
        Ship ship = Items.LoadItem <Ship> (shipID);
        ItemEntity charge = ship?.Items.Values
            .FirstOrDefault (i => i.Flag == module.Flag && !(i is ShipModule));

        if (charge == null)
        {
            Log.Warning ("[dogmaIM] FireWeapon: no ammo loaded in slot {Flag}, skipping shot", module.Flag);
            return false;
        }

        // Damage comes from the CHARGE, not the module
        AttributeList chargeAttrs = charge.Attributes;
        AttributeList moduleAttrs = module.Attributes;

        double emDmg    = chargeAttrs.AttributeExists (AttributeTypes.emDamage)        ? (double) chargeAttrs [AttributeTypes.emDamage]        : 0;
        double expDmg   = chargeAttrs.AttributeExists (AttributeTypes.explosiveDamage)  ? (double) chargeAttrs [AttributeTypes.explosiveDamage]  : 0;
        double kinDmg   = chargeAttrs.AttributeExists (AttributeTypes.kineticDamage)    ? (double) chargeAttrs [AttributeTypes.kineticDamage]    : 0;
        double thermDmg = chargeAttrs.AttributeExists (AttributeTypes.thermalDamage)    ? (double) chargeAttrs [AttributeTypes.thermalDamage]    : 0;

        // Damage multiplier stays on the MODULE
        double dmgMult  = moduleAttrs.AttributeExists (AttributeTypes.damageMultiplier) ? (double) moduleAttrs [AttributeTypes.damageMultiplier] : 1.0;

        int groupID = module.Type.Group.ID;
        int chargeTypeID = charge.Type.ID;

        // Compute real cycle time from the module's active effect duration
        double durationMs = 5000;
        if (module.Type.EffectsByName != null)
        {
            foreach (KeyValuePair <string, Effect> kvp in module.Type.EffectsByName)
            {
                if (kvp.Value.DurationAttributeID != null && moduleAttrs.AttributeExists ((AttributeTypes) kvp.Value.DurationAttributeID.Value))
                {
                    durationMs = (double) moduleAttrs [(AttributeTypes) kvp.Value.DurationAttributeID.Value];
                    break;
                }
            }
        }
        // fallback: standard rate-of-fire attribute (speed, id=21)
        if (durationMs <= 0 && moduleAttrs.AttributeExists (AttributeTypes.speed))
            durationMs = (double) moduleAttrs [AttributeTypes.speed];
        if (durationMs <= 0)
            durationMs = 5000;

        Log.Information ("[dogmaIM] FireWeapon: dmg(em={EmDmg:F1},exp={ExpDmg:F1},kin={KinDmg:F1},therm={ThermDmg:F1}) x{DmgMult:F2}, cycle={Duration:F0}ms, charge={ChargeName} (typeID={ChargeTypeID})",
            emDmg, expDmg, kinDmg, thermDmg, dmgMult, durationMs, charge.Type.Name, chargeTypeID);

        if (Combat == null)
            Log.Warning ("[dogmaIM] FireWeapon: CombatService is null, damage will NOT be applied");

        // Resolve VFX GUID once — used for both logging and the broadcast
        string fxGuid = GetDefaultEffectGuid (groupID);
        if (module.Type.EffectsByName != null)
        {
            string firstGuid = null;
            foreach (KeyValuePair <string, Effect> kvp in module.Type.EffectsByName)
            {
                if (string.IsNullOrEmpty (kvp.Value.GUID)) continue;
                firstGuid ??= kvp.Value.GUID;
                if (kvp.Value.EffectCategory == EffectCategory.Target || kvp.Value.EffectCategory == EffectCategory.Activation)
                { fxGuid = kvp.Value.GUID; break; }
            }
            if (fxGuid == GetDefaultEffectGuid (groupID) && firstGuid != null)
                fxGuid = firstGuid;
        }

        if (IsMissileLauncher (groupID))
        {
            // aoeCloudSize and aoeVelocity come from the CHARGE (missile type)
            double expRadius = chargeAttrs.AttributeExists (AttributeTypes.aoeCloudSize) ? (double) chargeAttrs [AttributeTypes.aoeCloudSize] : 100;
            double expVel    = chargeAttrs.AttributeExists (AttributeTypes.aoeVelocity)  ? (double) chargeAttrs [AttributeTypes.aoeVelocity]  : 100;

            Combat?.ApplyMissileDamage (ssID, attackerBubble, targetBubble, emDmg, expDmg, kinDmg, thermDmg, dmgMult, expRadius, expVel);
            Log.Information ("[dogmaIM] FireWeapon: missile fired, VFX guid={Guid}", fxGuid);
        }
        else
        {
            // Range/falloff/tracking stay on the MODULE
            double maxRange = moduleAttrs.AttributeExists (AttributeTypes.maxRange)      ? (double) moduleAttrs [AttributeTypes.maxRange]      : 10000;
            double falloff  = moduleAttrs.AttributeExists (AttributeTypes.falloff)       ? (double) moduleAttrs [AttributeTypes.falloff]       : 5000;
            double tracking = moduleAttrs.AttributeExists (AttributeTypes.trackingSpeed) ? (double) moduleAttrs [AttributeTypes.trackingSpeed] : 0.1;

            Log.Information ("[dogmaIM] FireWeapon: turret range={MaxRange:F0}, falloff={Falloff:F0}, tracking={Tracking:F4}", maxRange, falloff, tracking);
            Combat?.ApplyTurretDamage (ssID, attackerBubble, targetBubble,
                emDmg, expDmg, kinDmg, thermDmg, dmgMult, maxRange, falloff, tracking);
            Log.Information ("[dogmaIM] FireWeapon: turret fired, VFX guid={Guid}", fxGuid);
        }

        // Broadcast player weapon VFX so the client renders turret beams / projectiles / missiles
        Broadcaster?.BroadcastPlayerAttackFX (ssID, shipID, module.ID, module.Type.ID, targetID, chargeTypeID, fxGuid, durationMs);

        // Consume 1 unit of ammo
        ConsumeCharge (charge, charID, module.ID);

        // Check destruction
        if (targetBubble.IsDestroyed && !targetBubble.PendingDestruction)
        {
            targetBubble.PendingDestruction = true;
            Combat?.HandleEntityDestruction (ssID, targetBubble, destinyMgr);
        }

        return true;
    }

    private void ConsumeCharge (ItemEntity charge, int ownerID, int moduleID)
    {
        int oldQuantity = charge.Quantity;
        charge.Quantity -= 1;

        if (charge.Quantity <= 0)
        {
            // ammo depleted — notify qty→0, then destroy charge and stop cycling
            Log.Information ("[dogmaIM] Ammo depleted for module {ModuleID}, destroying charge {ChargeID}", moduleID, charge.ID);
            DogmaNotifications?.QueueMultiEvent (ownerID, OnItemChange.BuildQuantityChange (charge, oldQuantity));
            charge.Parent?.RemoveItem (charge);
            int oldLocationID = charge.LocationID;
            charge.LocationID = Items.LocationRecycler.ID;
            DogmaNotifications?.QueueMultiEvent (ownerID, OnItemChange.BuildLocationChange (charge, oldLocationID));
            charge.Destroy ();
            Cycler?.StopCycling (moduleID);
        }
        else
        {
            DogmaNotifications?.QueueMultiEvent (ownerID, OnItemChange.BuildQuantityChange (charge, oldQuantity));
            charge.Persist ();
        }
    }

    private static bool IsWeaponGroup (int groupID)
    {
        return groupID == 53    // Energy Weapon
            || groupID == 55    // Projectile Weapon
            || groupID == 74    // Hybrid Weapon
            || groupID == 56    // Missile Launcher
            || groupID == 506   // Missile Launcher Assault
            || groupID == 507   // Missile Launcher Defender
            || groupID == 508   // Missile Launcher Heavy
            || groupID == 509   // Missile Launcher Heavy Assault
            || groupID == 510   // Missile Launcher Cruise
            || groupID == 511   // Missile Launcher Siege
            || groupID == 771;  // Missile Launcher Rapid
    }

    private static bool IsMissileLauncher (int groupID)
    {
        return groupID == 56
            || groupID == 506
            || groupID == 507
            || groupID == 508
            || groupID == 509
            || groupID == 510
            || groupID == 511
            || groupID == 771;
    }

    private static string GetDefaultEffectGuid (int groupID)
    {
        if (IsMissileLauncher (groupID)) return "effects.MissileDeployment";
        return groupID switch
        {
            53 => "effects.Laser",             // Energy Weapon
            55 => "effects.ProjectileFired",   // Projectile Weapon
            74 => "effects.HybridFired",       // Hybrid Weapon
            _  => "effects.Laser"
        };
    }

    // === EXISTING OVERRIDES BELOW ===

    protected override long MachoResolveObject (ServiceCall call, ServiceBindParams parameters)
    {
        return parameters.ExtraValue switch
        {
            (int) GroupID.SolarSystem => Database.CluResolveAddress ("solarsystem", parameters.ObjectID),
            (int) GroupID.Station     => Database.CluResolveAddress ("station",     parameters.ObjectID),
            _                         => throw new CustomError ("Unknown item's groupID")
        };
    }

    protected override BoundService CreateBoundInstance (ServiceCall call, ServiceBindParams bindParams)
    {
        int characterID = call.Session.CharacterID;

        if (this.MachoResolveObject (call, bindParams) != BoundServiceManager.MachoNet.NodeID)
            throw new CustomError ("Trying to bind an object that does not belong to us!");

        Character character = Items.LoadItem <Character> (characterID);

        if (bindParams.ExtraValue == (int) GroupID.Station && call.Session.StationID == bindParams.ObjectID)
        {
            Items.GetStaticStation (bindParams.ObjectID).Guests [characterID] = character;
            Notifications.NotifyStation (bindParams.ObjectID, new OnCharNowInStation (call.Session));
        }

        return new dogmaIM (bindParams.ObjectID, EffectsManager, Items, Notifications, DogmaNotifications, BoundServiceManager, call.Session, SolarSystems,
            TargetMgr, SolarSystemDestinyMgr, Broadcaster, Combat, Cycler, Log);
    }

    protected override void OnClientDisconnected ()
    {
        int characterID = Session.CharacterID;

        // Only clear target locks from space-bound dogmaIM instances.
        // mIsStationBound is captured at construction time because Session.StationID
        // gets cleared by ApplySessionChange during undock BEFORE OnClientDisconnected runs,
        // which caused station-bound instances to incorrectly call UnlockAll.
        if (!mIsStationBound)
            TargetMgr?.UnlockAll (characterID);

        if (mIsStationBound)
        {
            Items.GetStaticStation (ObjectID).Guests.Remove (characterID);
            Notifications.NotifyStation (ObjectID, new OnCharNoLongerInStation (Session));
            Items.UnloadItem (characterID);
            if (Session.ShipID is not null)
                Items.UnloadItem ((int) Session.ShipID);
        }
    }

public PyDataType GetTargeters(ServiceCall call)
{
    int shipID = call.Session.ShipID ?? 0;
    if (shipID == 0) return new PyList ();

    List <int> targeters = TargetMgr.GetTargeters (shipID);
    PyList     result    = new PyList ();
    foreach (int t in targeters)
        result.Add (new PyInteger (t));
    return result;
}

}
