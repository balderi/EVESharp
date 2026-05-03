using System;
using System.Collections.Generic;
using System.Linq;
using EVESharp.Database.Account;
using EVESharp.Database.Characters;
using EVESharp.Database.Inventory;
using EVESharp.Database.Inventory.Attributes;
using EVESharp.Database.Inventory.Categories;
using EVESharp.Database.Inventory.Groups;
using Attribute = EVESharp.Database.Inventory.Attributes.Attribute;
using EVESharp.Database.Inventory.Types;
using EVESharp.Database.Market;
using EVESharp.Database.Old;
using EVESharp.Database.Standings;
using EVESharp.Destiny;
using EVESharp.EVE.Data.Inventory;
using EVESharp.EVE.Data.Inventory.Items;
using EVESharp.EVE.Data.Inventory.Items.Types;
using EVESharp.EVE.Dogma;
using EVESharp.EVE.Exceptions.slash;
using EVESharp.EVE.Market;
using EVESharp.EVE.Network.Services;
using EVESharp.EVE.Network.Services.Validators;
using EVESharp.EVE.Notifications;
using EVESharp.EVE.Notifications.Inventory;
using EVESharp.EVE.Notifications.Skills;
using EVESharp.EVE.Relationships;
using EVESharp.EVE.Sessions;
using EVESharp.Node.Services.Space;
using EVESharp.Types;
using EVESharp.Types.Collections;
using Serilog;
using Type = EVESharp.Database.Inventory.Types.Type;

namespace EVESharp.Node.Services.Network;

[MustBeCharacter]
[MustHaveRole (Roles.ROLE_ADMIN)]
public class slash : Service
{
    private readonly Dictionary <string, Action <string [], ServiceCall>> mCommands =
        new Dictionary <string, Action <string [], ServiceCall>> ();
    public override AccessLevel         AccessLevel           => AccessLevel.None;
    private         ITypes              Types                 => Items.Types;
    private         IItems              Items                 { get; }
    private         ILogger             Log                   { get; }
    private         OldCharacterDB      CharacterDB           { get; }
    private         SkillDB             SkillDB               { get; }
    private         INotificationSender Notifications         { get; }
    private         IWallets            Wallets               { get; }
    private         IDogmaNotifications DogmaNotifications    { get; }
    private         IDogmaItems         DogmaItems            { get; }
    private         ISessionManager     SessionManager        { get; }
    private         SolarSystemDestinyManager SolarSystemDestinyMgr { get; }
    private         IStandings          Standings             { get; }
    private         DungeonData         DungeonData           { get; }

public slash
(
    ILogger             logger,
    IItems              items,
    OldCharacterDB      characterDB,
    INotificationSender notificationSender,
    IWallets            wallets,
    IDogmaNotifications dogmaNotifications,
    IDogmaItems         dogmaItems,
    SkillDB             skillDB,
    ISessionManager     sessionManager,
    SolarSystemDestinyManager solarSystemDestinyMgr,
    IStandings          standings,
    DungeonData         dungeonData
)
{
    Log                          = logger;
    Items                   = items;
    CharacterDB                  = characterDB;
    Notifications                = notificationSender;
    Wallets                 = wallets;
    DogmaNotifications      = dogmaNotifications;
    DogmaItems              = dogmaItems;
    SkillDB                 = skillDB;
    SessionManager          = sessionManager;
    SolarSystemDestinyMgr   = solarSystemDestinyMgr;
    Standings               = standings;
    DungeonData             = dungeonData;

    // register commands
    this.mCommands["create"]        = this.CreateCmd;
    this.mCommands["createitem"]    = this.CreateCmd;
    this.mCommands["giveskills"]    = this.GiveSkillCmd;
    this.mCommands["giveskill"]     = this.GiveSkillCmd;
    this.mCommands["giveisk"]       = this.GiveIskCmd;
    this.mCommands["move"]          = this.MoveCmd;
    this.mCommands["heal"]          = this.HealCmd;
    this.mCommands["unload"]        = this.UnloadCmd;
    this.mCommands["spawn"]         = this.SpawnCmd;
    this.mCommands["fit"]           = this.FitCmd;
    this.mCommands["online"]        = this.OnlineCmd;
    this.mCommands["tr"]            = this.TrCmd;
    this.mCommands["removeskill"]   = this.RemoveSkillCmd;
    this.mCommands["removeskills"]  = this.RemoveSkillCmd;
    this.mCommands["spawnn"]        = this.SpawnNCmd;
    this.mCommands["entity"]        = this.EntityCmd;
    this.mCommands["bp"]            = this.BpCmd;
    this.mCommands["load"]          = this.LoadCmd;
    this.mCommands["repairmodules"] = this.RepairModulesCmd;
    this.mCommands["setstanding"]   = this.SetStandingCmd;
    this.mCommands["unspawn"]       = this.UnspawnCmd;
    this.mCommands["moveme"]        = this.MoveMeCmd;
    this.mCommands["kill"]          = this.KillCmd;
    this.mCommands["dungeon"]       = this.DungeonCmd;
}


    private string GetCommandListForClient ()
    {
        string result = "";

        foreach ((string name, _) in this.mCommands)
            result += $"'{name}',";

        return $"[{result}]";
    }

    public PyDataType SlashCmd (ServiceCall call, PyString line)
    {
        try
        {
            string [] parts = line.Value.Split (' ');

            // get the command name
            string command = parts [0].TrimStart ('/');

            // only a "/" means the client is requesting the list of commands available
            if (command.Length == 0 || this.mCommands.ContainsKey (command) == false)
                throw new SlashError ("Commands: " + this.GetCommandListForClient ());

            this.mCommands [command].Invoke (parts, call);
        }
        catch (SlashError)
        {
            throw;
        }
        catch (Exception e)
        {
            Log.Error (e.Message);
            Log.Error (e.StackTrace);

            throw new SlashError ($"Runtime error: {e.Message}");
        }

        return null;
    }

    // =====================================================================
    //  UTILITY METHODS
    // =====================================================================

    /// <summary>
    /// Resolves "me" to the caller's shipID, or parses argument as an integer itemID.
    /// </summary>
    private int ResolveItemTarget (string arg, ServiceCall call)
    {
        if (string.Equals (arg, "me", StringComparison.OrdinalIgnoreCase))
        {
            int? shipID = call.Session.ShipID;

            if (shipID == null || shipID.Value == 0)
                throw new SlashError ("You don't have an active ship");

            return shipID.Value;
        }

        if (int.TryParse (arg, out int itemID))
            return itemID;

        throw new SlashError ($"Invalid target: {arg}");
    }

    /// <summary>
    /// Resolves "me" to the caller's characterID, or parses argument as an integer characterID.
    /// </summary>
    private int ResolveCharacterTarget (string arg, ServiceCall call)
    {
        if (string.Equals (arg, "me", StringComparison.OrdinalIgnoreCase))
            return call.Session.CharacterID;

        if (int.TryParse (arg, out int charID))
            return charID;

        // try name lookup
        List <int> matches = CharacterDB.FindCharacters (arg);

        if (matches.Count == 0)
            throw new SlashError ($"Character not found: {arg}");
        if (matches.Count > 1)
            throw new SlashError ("Multiple characters match, please be more specific");

        return matches [0];
    }

    /// <summary>
    /// Moves a character to a station, updating DB and performing session change.
    /// Extracted from MoveCmd for reuse by /tr and /moveme.
    /// </summary>
    private void DoMoveToStation (int targetCharacterID, Session targetSession, Station target)
    {
        // Store location change in DB
        CharacterDB.UpdateStationAndLocation (
            targetCharacterID,
            target.ID,
            target.SolarSystemID,
            target.ConstellationID,
            target.RegionID
        );

        // Move active ship to new hangar
        int? shipID = targetSession.ShipID;

        if (shipID.HasValue && shipID.Value > 0)
        {
            ItemEntity ship = Items.GetItem <ItemEntity> (shipID.Value);

            if (ship != null)
            {
                ship.LocationID = target.ID;
                ship.Flag       = Flags.Hangar;
                ship.Persist ();
            }
        }

        // CREATE A NEW SESSION CLONE
        Session newSession = Session.FromPyDictionary (targetSession);

        // Update the clone's state — NOT the live session
        newSession.StationID       = target.ID;
        newSession.LocationID      = target.ID;
        newSession.SolarSystemID2  = target.SolarSystemID;
        newSession.ConstellationID = target.ConstellationID;
        newSession.RegionID        = target.RegionID;

        // Perform REAL session change (this triggers correct client notifications)
        SessionManager.PerformSessionUpdate (
            "charid",
            targetCharacterID,
            newSession
        );

        Log.Information ("Slash: moved character {CharacterID} to station {StationID}", targetCharacterID, target.ID);
    }

    // =====================================================================
    //  EXISTING COMMANDS
    // =====================================================================

    private void GiveIskCmd (string [] argv, ServiceCall call)
    {
        if (argv.Length < 3)
            throw new SlashError ("giveisk takes two arguments");

        string targetCharacter = argv [1];

        if (double.TryParse (argv [2], out double iskQuantity) == false)
            throw new SlashError ("giveisk second argument must be the ISK quantity to give");

        int targetCharacterID = 0;
        int originCharacterID = call.Session.CharacterID;

        if (string.Equals (targetCharacter, "me", StringComparison.OrdinalIgnoreCase))
        {
            targetCharacterID = originCharacterID;
        }
        else
        {
            List <int> matches = CharacterDB.FindCharacters (targetCharacter);

            if (matches.Count > 1)
                throw new SlashError ("There's more than one character that matches the search criteria, please narrow it down");

            targetCharacterID = matches [0];
        }

        using IWallet wallet = Wallets.AcquireWallet (targetCharacterID, WalletKeys.MAIN);

        {
            if (iskQuantity < 0)
            {
                wallet.EnsureEnoughBalance (iskQuantity);
                wallet.CreateJournalRecord (MarketReference.GMCashTransfer, Items.OwnerSCC.ID, null, -iskQuantity);
            }
            else
            {
                wallet.CreateJournalRecord (MarketReference.GMCashTransfer, Items.OwnerSCC.ID, targetCharacterID, null, iskQuantity);
            }
        }
    }

    private void MoveCmd (string [] argv, ServiceCall call)
    {
        int targetCharacterID = call.Session.CharacterID;
        int stationID;

        // Parse args: /move <stationID> or /move <charID> <stationID>
        if (argv.Length == 2)
        {
            int.TryParse (argv [1], out stationID);
        }
        else if (argv.Length == 3)
        {
            int.TryParse (argv [1], out targetCharacterID);
            int.TryParse (argv [2], out stationID);
        }
        else
            throw new SlashError ("Usage: /move <stationID> or /move <characterID> <stationID>");

        // Find target session (must be online)
        Session targetSession =
            (targetCharacterID == call.Session.CharacterID)
                ? call.Session
                : SessionManager.FindSession ("charid", targetCharacterID).FirstOrDefault ();

        if (targetSession == null)
            throw new SlashError ("Target character is not online.");

        targetSession.EnsureCharacterIsInStation ();

        // Lookup station
        Station target = Items.GetStaticStation (stationID);

        DoMoveToStation (targetCharacterID, targetSession, target);
    }


    private void CreateCmd (string [] argv, ServiceCall call)
    {
        if (argv.Length < 2)
            throw new SlashError ("create takes at least one argument");

        int typeID   = int.Parse (argv [1]);
        int quantity = 1;

        if (argv.Length > 2)
            quantity = int.Parse (argv [2]);

        call.Session.EnsureCharacterIsInStation ();

        // ensure the typeID exists
        if (Types.ContainsKey (typeID) == false)
            throw new SlashError ("The specified typeID doesn't exist");

        // create a new item with the correct locationID
        DogmaItems.CreateItem <ItemEntity> (Types [typeID], call.Session.CharacterID, call.Session.StationID, Flags.Hangar, quantity);
    }

    private static int ParseIntegerThatMightBeDecimal (string value)
    {
        int index = value.IndexOf ('.');

        if (index != -1)
            value = value.Substring (0, index);

        return int.Parse (value);
    }

    private void GiveSkillCmd (string [] argv, ServiceCall call)
    {
        // TODO: NOT NODE-SAFE, MUST REIMPLEMENT TAKING THAT INTO ACCOUNT!
        if (argv.Length != 4)
            throw new SlashError ("GiveSkill must have 4 arguments");

        int characterID = call.Session.CharacterID;

        string target    = argv [1].Trim ('"', ' ');
        string skillType = argv [2];
        int    level     = ParseIntegerThatMightBeDecimal (argv [3]);

        if (!string.Equals (target, "me", StringComparison.OrdinalIgnoreCase) && target != characterID.ToString ())
            throw new SlashError ("giveskill only supports me for now");

        Character character = Items.GetItem <Character> (characterID);

        if (skillType == "all")
        {
            // player wants all the skills!
            IEnumerable <KeyValuePair <int, Type>> skillTypes =
                Types.Where (x => x.Value.Group.Category.ID == (int) CategoryID.Skill && x.Value.Published);

            Dictionary <int, Skill> injectedSkills = character.InjectedSkillsByTypeID;

            foreach ((int typeID, Type type) in skillTypes)
                // skill already injected, train it to the desired level
                if (injectedSkills.ContainsKey (typeID))
                {
                    Skill skill = injectedSkills [typeID];

                    skill.Level = level;
                    skill.Persist ();
                    DogmaNotifications.QueueMultiEvent (character.ID, new OnSkillTrained (skill));
                }
                else
                {
                    Skill skill = DogmaItems.CreateItem <Skill> (type, character, character, Flags.Skill, 1, true);
                    skill.Level = level;
                    skill.Persist ();

                    DogmaNotifications.NotifyAttributeChange (character.ID, AttributeTypes.skillLevel, skill);
                    DogmaNotifications.QueueMultiEvent (character.ID, new OnSkillInjected ());

                    // add the skill history record too
                    SkillDB.CreateSkillHistoryRecord (
                        type, character, SkillHistoryReason.GMGiveSkill, skill.GetSkillPointsForLevel (level)
                    );
                }
        }
        else
        {
            Dictionary <int, Skill> injectedSkills = character.InjectedSkillsByTypeID;

            int skillTypeID = ParseIntegerThatMightBeDecimal (skillType);

            if (injectedSkills.ContainsKey (skillTypeID))
            {
                Skill skill = injectedSkills [skillTypeID];
                skill.Level = level;
                skill.Persist ();

                DogmaNotifications.QueueMultiEvent (character.ID, new OnSkillStartTraining (skill));
                DogmaNotifications.NotifyAttributeChange (character.ID, new [] {AttributeTypes.skillPoints, AttributeTypes.skillLevel}, skill);
                DogmaNotifications.QueueMultiEvent (character.ID, new OnSkillTrained (skill));
            }
            else
            {
                Skill skill = DogmaItems.CreateItem <Skill> (Types [skillTypeID], character, character, Flags.Skill, 1, true);
                skill.Level = level;
                skill.Persist ();

                DogmaNotifications.NotifyAttributeChange (character.ID, AttributeTypes.skillLevel, skill);
                DogmaNotifications.QueueMultiEvent (character.ID, new OnSkillInjected ());

                // add the skill history record too
                SkillDB.CreateSkillHistoryRecord (
                    Types [skillTypeID], character, SkillHistoryReason.GMGiveSkill, skill.GetSkillPointsForLevel (level)
                );

                DogmaNotifications.QueueMultiEvent (character.ID, new OnSkillInjected ());
            }
        }
    }

    // =====================================================================
    //  NEW COMMANDS
    // =====================================================================

    /// <summary>
    /// /heal target amount
    /// amount=0: destroy item
    /// amount>0: heal (restore HP)
    /// amount&lt;0: damage (reduce HP)
    /// </summary>
    private void HealCmd (string [] argv, ServiceCall call)
    {
        if (argv.Length < 3)
            throw new SlashError ("Usage: /heal <target|itemID> <amount>");

        int targetID = ResolveItemTarget (argv [1], call);

        if (double.TryParse (argv [2], out double amount) == false)
            throw new SlashError ("heal: amount must be a number");

        if (amount == 0)
        {
            // Destroy the item
            if (Items.TryGetItem (targetID, out ItemEntity item) == false)
                throw new SlashError ($"Item {targetID} not found");

            // Unregister from destiny if in space
            int? solarSystemID = call.Session.SolarSystemID;

            if (solarSystemID != null && SolarSystemDestinyMgr.TryGet (solarSystemID.Value, out DestinyManager dm))
                dm.UnregisterEntity (targetID);

            DogmaItems.DestroyItem (item);
            Log.Information ("Slash /heal: destroyed item {ItemID}", targetID);
        }
        else
        {
            // Heal or damage - modify shield/armor/hull
            if (Items.TryGetItem (targetID, out ItemEntity item) == false)
                throw new SlashError ($"Item {targetID} not found");

            if (amount > 0)
            {
                // Heal: restore shield charge, remove armor damage
                if (item.Attributes.AttributeExists (AttributeTypes.shieldCharge))
                {
                    double maxShield = item.Attributes [AttributeTypes.shieldCapacity];
                    double current   = item.Attributes [AttributeTypes.shieldCharge];
                    item.Attributes [AttributeTypes.shieldCharge] = new Attribute (AttributeTypes.shieldCharge, Math.Min (current + amount, maxShield));
                }

                if (item.Attributes.AttributeExists (AttributeTypes.armorDamage))
                {
                    double current = item.Attributes [AttributeTypes.armorDamage];
                    item.Attributes [AttributeTypes.armorDamage] = new Attribute (AttributeTypes.armorDamage, Math.Max (current - amount, 0));
                }

                if (item.Attributes.AttributeExists (AttributeTypes.damage))
                {
                    double current = item.Attributes [AttributeTypes.damage];
                    item.Attributes [AttributeTypes.damage] = new Attribute (AttributeTypes.damage, Math.Max (current - amount, 0));
                }

                Log.Information ("Slash /heal: healed item {ItemID} by {Amount}", targetID, amount);
            }
            else
            {
                // Damage: reduce shield charge, increase armor damage
                double dmgAmount = -amount;

                if (item.Attributes.AttributeExists (AttributeTypes.shieldCharge))
                {
                    double current = item.Attributes [AttributeTypes.shieldCharge];
                    item.Attributes [AttributeTypes.shieldCharge] = new Attribute (AttributeTypes.shieldCharge, Math.Max (current - dmgAmount, 0));
                }

                if (item.Attributes.AttributeExists (AttributeTypes.armorDamage))
                {
                    double current = item.Attributes [AttributeTypes.armorDamage];
                    double maxArmor = item.Attributes [AttributeTypes.armorHP];
                    item.Attributes [AttributeTypes.armorDamage] = new Attribute (AttributeTypes.armorDamage, Math.Min (current + dmgAmount, maxArmor));
                }

                if (item.Attributes.AttributeExists (AttributeTypes.damage))
                {
                    double current = item.Attributes [AttributeTypes.damage];
                    double maxHP   = item.Attributes [AttributeTypes.hp];
                    item.Attributes [AttributeTypes.damage] = new Attribute (AttributeTypes.damage, Math.Min (current + dmgAmount, maxHP));
                }

                Log.Information ("Slash /heal: damaged item {ItemID} by {Amount}", targetID, dmgAmount);
            }

            item.Persist ();
        }
    }

    /// <summary>
    /// /unload target typeID|all - Unload modules from a ship to cargo
    /// </summary>
    private void UnloadCmd (string [] argv, ServiceCall call)
    {
        if (argv.Length < 3)
            throw new SlashError ("Usage: /unload <target|me> <moduleTypeID|all>");

        int shipID = ResolveItemTarget (argv [1], call);
        string moduleArg = argv [2];

        Ship ship = Items.LoadItem <Ship> (shipID);

        if (ship == null)
            throw new SlashError ($"Ship {shipID} not found or is not a ship");

        int unloaded = 0;

        foreach (KeyValuePair <int, ItemEntity> kvp in ship.Items)
        {
            ItemEntity module = kvp.Value;

            if (!module.IsInModuleSlot () && !module.IsInRigSlot ())
                continue;

            if (moduleArg != "all")
            {
                if (int.TryParse (moduleArg, out int filterTypeID) == false)
                    throw new SlashError ("moduleTypeID must be a number or 'all'");

                if (module.Type.ID != filterTypeID)
                    continue;
            }

            DogmaItems.MoveItem (module, Flags.Cargo);
            unloaded++;
        }

        Log.Information ("Slash /unload: unloaded {Count} modules from ship {ShipID}", unloaded, shipID);
    }

    /// <summary>
    /// /spawn typeID [rest] - Spawn an entity in space at the caller's position
    /// </summary>
    private void SpawnCmd (string [] argv, ServiceCall call)
    {
        if (argv.Length < 2)
            throw new SlashError ("Usage: /spawn <typeID>");

        int typeID = int.Parse (argv [1]);

        if (Types.ContainsKey (typeID) == false)
            throw new SlashError ("The specified typeID doesn't exist");

        int solarSystemID = call.Session.EnsureCharacterIsInSpace ();
        int shipID        = call.Session.ShipID ?? 0;

        // Get the caller's position
        double x = 0, y = 0, z = 0;

        if (shipID != 0 && SolarSystemDestinyMgr.TryGet (solarSystemID, out DestinyManager dm) &&
            dm.TryGetEntity (shipID, out BubbleEntity shipEnt))
        {
            x = shipEnt.Position.X + 5000;
            y = shipEnt.Position.Y;
            z = shipEnt.Position.Z;
        }

        Type type = Types [typeID];

        // Create the item in the solar system
        ItemEntity newItem = DogmaItems.CreateItem <ItemEntity> (
            type, call.Session.CharacterID, solarSystemID, Flags.None, 1, true
        );

        // Set position
        newItem.X = x;
        newItem.Y = y;
        newItem.Z = z;
        newItem.Persist ();

        // Register in DestinyManager and broadcast AddBalls
        if (SolarSystemDestinyMgr.TryGet (solarSystemID, out DestinyManager destinyMgr))
        {
            bool isShipCategory = type.Group.Category.ID == (int) CategoryID.Ship;

            BubbleEntity bubble = new BubbleEntity
            {
                ItemID        = newItem.ID,
                TypeID        = type.ID,
                GroupID       = type.Group.ID,
                CategoryID    = type.Group.Category.ID,
                Name          = type.Name,
                OwnerID       = call.Session.CharacterID,
                CorporationID = call.Session.CorporationID,
                AllianceID    = 0,
                CharacterID   = 0,
                Position      = new Vector3 { X = x, Y = y, Z = z },
                Velocity      = default (Vector3),
                Mode          = isShipCategory ? BallMode.Stop : BallMode.Rigid,
                Flags         = isShipCategory
                    ? BallFlag.IsFree | BallFlag.IsMassive | BallFlag.IsInteractive
                    : BallFlag.IsGlobal | BallFlag.IsMassive | BallFlag.IsInteractive,
                Radius        = type.Radius,
                Mass          = 1000000.0,
                MaxVelocity   = isShipCategory ? 200.0 : 0.0,
                SpeedFraction = 0.0,
                Agility       = 1.0
            };

            destinyMgr.RegisterEntity (bubble);

            // Send AddBalls to the deploying player via "charid"
            int charID = call.Session.CharacterID;
            int stamp = DestinyEventBuilder.GetStamp ();
            PyList addBallEvents = DestinyEventBuilder.BuildAddBalls (new[] { bubble }, solarSystemID, stamp);
            PyTuple notification  = DestinyEventBuilder.WrapAsNotification (addBallEvents);

            Notifications.SendNotification ("DoDestinyUpdate", "charid", charID, notification);
        }

        Log.Information ("Slash /spawn: spawned typeID={TypeID} as itemID={ItemID} at ({X:F0},{Y:F0},{Z:F0})",
            typeID, newItem.ID, x, y, z);
    }

    /// <summary>
    /// /fit target typeID [qty] - Fit a module to a ship
    /// </summary>
    private void FitCmd (string [] argv, ServiceCall call)
    {
        if (argv.Length < 3)
            throw new SlashError ("Usage: /fit <target|me> <typeID> [qty]");

        int shipID = ResolveItemTarget (argv [1], call);
        int typeID = int.Parse (argv [2]);
        int qty    = argv.Length > 3 ? int.Parse (argv [3]) : 1;

        if (Types.ContainsKey (typeID) == false)
            throw new SlashError ("The specified typeID doesn't exist");

        Type type       = Types [typeID];
        int  categoryID = type.Group.Category.ID;

        // Determine target flag based on category/group
        Flags targetFlag;

        if (categoryID == (int) CategoryID.Charge)
            targetFlag = Flags.Cargo;
        else if (categoryID == (int) CategoryID.Drone)
            targetFlag = Flags.DroneBay;
        else
            targetFlag = Flags.Cargo;

        for (int i = 0; i < qty; i++)
        {
            ItemEntity module = DogmaItems.CreateItem <ItemEntity> (
                type, call.Session.CharacterID, shipID, targetFlag, 1, true
            );

            // If it's a module, try to fit it into an appropriate slot
            if (categoryID == (int) CategoryID.Module)
            {
                try
                {
                    DogmaItems.FitInto (module, shipID, FindFreeSlot (shipID, type), call.Session);
                }
                catch
                {
                    // If fitting fails, leave in cargo
                    Log.Warning ("Slash /fit: could not fit typeID={TypeID} into slot, left in cargo", typeID);
                }
            }
        }

        Log.Information ("Slash /fit: fitted {Qty}x typeID={TypeID} to ship {ShipID}", qty, typeID, shipID);
    }

    /// <summary>
    /// Find a free module slot on a ship for the given module type.
    /// </summary>
    private Flags FindFreeSlot (int shipID, Type moduleType)
    {
        Ship ship = Items.LoadItem <Ship> (shipID);
        int  effectCategory = GetModuleSlotCategory (moduleType);

        Flags startFlag, endFlag;

        switch (effectCategory)
        {
            case 1: // hi slot
                startFlag = Flags.HiSlot0;
                endFlag   = Flags.HiSlot7;
                break;
            case 2: // med slot
                startFlag = Flags.MedSlot0;
                endFlag   = Flags.MedSlot7;
                break;
            case 3: // lo slot
                startFlag = Flags.LoSlot0;
                endFlag   = Flags.LoSlot7;
                break;
            default:
                return Flags.Cargo;
        }

        HashSet <Flags> usedFlags = new HashSet <Flags> ();

        foreach (KeyValuePair <int, ItemEntity> kvp in ship.Items)
        {
            if (kvp.Value.IsInModuleSlot ())
                usedFlags.Add (kvp.Value.Flag);
        }

        for (Flags f = startFlag; f <= endFlag; f++)
        {
            if (!usedFlags.Contains (f))
                return f;
        }

        return Flags.Cargo;
    }

    /// <summary>
    /// Determine module slot category: 1=hi, 2=med, 3=lo.
    /// Uses effect list if available, otherwise defaults based on group.
    /// </summary>
    private static int GetModuleSlotCategory (Type moduleType)
    {
        // Default heuristic: weapons/launchers are high slot, shield/propulsion are med, armor/engineering are low
        // In a full implementation this would check the item's effects for hiPower/medPower/loPower
        int groupID = moduleType.Group.ID;

        // Common high slot groups: energy weapons, hybrid weapons, projectile weapons, missile launchers
        if (groupID >= 53 && groupID <= 56) return 1;  // weapon groups
        if (groupID == 72 || groupID == 73) return 1;   // missile launchers
        if (groupID == 507 || groupID == 508) return 1;  // turrets

        // Common med slot groups: shield, propulsion, ECM, sensor
        if (groupID == 38 || groupID == 39 || groupID == 40) return 2;  // shield related
        if (groupID == 46) return 2;  // propulsion

        // Default to low slot
        return 3;
    }

    /// <summary>
    /// /online target - Online all fitted modules on a ship
    /// </summary>
    private void OnlineCmd (string [] argv, ServiceCall call)
    {
        if (argv.Length < 2)
            throw new SlashError ("Usage: /online <target|me>");

        int shipID = ResolveItemTarget (argv [1], call);
        Ship ship  = Items.LoadItem <Ship> (shipID);

        if (ship == null)
            throw new SlashError ($"Ship {shipID} not found");

        int onlined = 0;

        foreach (KeyValuePair <int, ItemEntity> kvp in ship.Items)
        {
            ItemEntity module = kvp.Value;

            if (!module.IsInModuleSlot ())
                continue;

            try
            {
                if (module.Attributes.AttributeExists (AttributeTypes.isOnline) &&
                    module.Attributes [AttributeTypes.isOnline] == 0)
                {
                    module.Attributes [AttributeTypes.isOnline] = new Attribute (AttributeTypes.isOnline, 1);
                    module.Persist ();
                    onlined++;
                }
            }
            catch
            {
                // skip modules that fail to online
            }
        }

        Log.Information ("Slash /online: onlined {Count} modules on ship {ShipID}", onlined, shipID);
    }

    /// <summary>
    /// /tr target destination [offset=x,y,z]
    /// Teleport to a station (reuses DoMoveToStation) or to another character's position.
    /// </summary>
    private void TrCmd (string [] argv, ServiceCall call)
    {
        if (argv.Length < 3)
            throw new SlashError ("Usage: /tr <target|me> <stationID|charID>");

        string targetArg = argv [1];
        string destArg   = argv [2];

        int targetCharacterID = ResolveCharacterTarget (targetArg, call);

        Session targetSession =
            (targetCharacterID == call.Session.CharacterID)
                ? call.Session
                : SessionManager.FindSession ("charid", targetCharacterID).FirstOrDefault ();

        if (targetSession == null)
            throw new SlashError ("Target character is not online.");

        if (int.TryParse (destArg, out int destID) == false)
            throw new SlashError ("Destination must be a numeric ID (stationID or characterID)");

        // Try as station first
        try
        {
            Station station = Items.GetStaticStation (destID);

            // It's a valid station - move there
            DoMoveToStation (targetCharacterID, targetSession, station);
            return;
        }
        catch
        {
            // Not a station, try as character
        }

        // Try as another character - teleport to their position in space
        Session destSession = SessionManager.FindSession ("charid", destID).FirstOrDefault ();

        if (destSession != null)
        {
            int? destSystemID = destSession.SolarSystemID;

            if (destSystemID == null)
                throw new SlashError ("Destination character is not in space");

            int? destShipID = destSession.ShipID;

            if (destShipID != null && destShipID.Value != 0 &&
                SolarSystemDestinyMgr.TryGet (destSystemID.Value, out DestinyManager dm) &&
                dm.TryGetEntity (destShipID.Value, out BubbleEntity destEnt))
            {
                // Teleport target's ship to destination character's position
                int? myShipID   = targetSession.ShipID;
                int? mySystemID = targetSession.SolarSystemID;

                if (myShipID == null || myShipID.Value == 0)
                    throw new SlashError ("You don't have an active ship");

                if (mySystemID != null && SolarSystemDestinyMgr.TryGet (mySystemID.Value, out DestinyManager srcDm))
                    srcDm.UnregisterEntity (myShipID.Value);

                // Update ship position
                if (Items.TryGetItem (myShipID.Value, out ItemEntity shipEntity))
                {
                    shipEntity.X = destEnt.Position.X + 5000;
                    shipEntity.Y = destEnt.Position.Y;
                    shipEntity.Z = destEnt.Position.Z;
                    shipEntity.Persist ();
                }

                // Session change if different system
                if (mySystemID != destSystemID)
                {
                    Session newSession = Session.FromPyDictionary (targetSession);
                    newSession.SolarSystemID2 = destSystemID.Value;
                    newSession.LocationID     = destSystemID.Value;

                    SessionManager.PerformSessionUpdate ("charid", targetCharacterID, newSession);
                }

                Log.Information ("Slash /tr: teleported {CharID} to character {DestCharID}", targetCharacterID, destID);
                return;
            }
        }

        throw new SlashError ($"Could not resolve destination {destID} as station or online character");
    }

    /// <summary>
    /// /removeskill target typeID|all - Remove skills from a character
    /// </summary>
    private void RemoveSkillCmd (string [] argv, ServiceCall call)
    {
        if (argv.Length < 3)
            throw new SlashError ("Usage: /removeskill <target|me> <typeID|all>");

        int    characterID = ResolveCharacterTarget (argv [1], call);
        string skillArg    = argv [2];

        Character character = Items.GetItem <Character> (characterID);
        Dictionary <int, Skill> injectedSkills = character.InjectedSkillsByTypeID;

        if (skillArg == "all")
        {
            foreach (KeyValuePair <int, Skill> kvp in injectedSkills)
            {
                DogmaItems.DestroyItem (kvp.Value);
            }

            DogmaNotifications.QueueMultiEvent (character.ID, new OnSkillInjected ());
            Log.Information ("Slash /removeskill: removed all skills from character {CharID}", characterID);
        }
        else
        {
            int skillTypeID = ParseIntegerThatMightBeDecimal (skillArg);

            if (injectedSkills.TryGetValue (skillTypeID, out Skill skill) == false)
                throw new SlashError ($"Character does not have skill typeID {skillTypeID}");

            DogmaItems.DestroyItem (skill);
            DogmaNotifications.QueueMultiEvent (character.ID, new OnSkillInjected ());
            Log.Information ("Slash /removeskill: removed skill typeID={TypeID} from character {CharID}", skillTypeID, characterID);
        }
    }

    /// <summary>
    /// /spawnn qty deviation typeID - Spawn multiple entities scattered within a radius
    /// </summary>
    private void SpawnNCmd (string [] argv, ServiceCall call)
    {
        if (argv.Length < 4)
            throw new SlashError ("Usage: /spawnn <qty> <deviation> <typeID>");

        int    qty       = int.Parse (argv [1]);
        double deviation = double.Parse (argv [2]);
        int    typeID    = int.Parse (argv [3]);

        if (qty <= 0 || qty > 100)
            throw new SlashError ("qty must be between 1 and 100");

        if (Types.ContainsKey (typeID) == false)
            throw new SlashError ("The specified typeID doesn't exist");

        int solarSystemID = call.Session.EnsureCharacterIsInSpace ();
        int shipID        = call.Session.ShipID ?? 0;

        double baseX = 0, baseY = 0, baseZ = 0;

        if (shipID != 0 && SolarSystemDestinyMgr.TryGet (solarSystemID, out DestinyManager dm) &&
            dm.TryGetEntity (shipID, out BubbleEntity shipEnt))
        {
            baseX = shipEnt.Position.X;
            baseY = shipEnt.Position.Y;
            baseZ = shipEnt.Position.Z;
        }

        Type                type           = Types [typeID];
        Random              random         = new Random ();
        List <BubbleEntity> spawnedBubbles = new List<BubbleEntity> ();

        for (int i = 0; i < qty; i++)
        {
            double ox = (random.NextDouble () * 2 - 1) * deviation;
            double oy = (random.NextDouble () * 2 - 1) * deviation;
            double oz = (random.NextDouble () * 2 - 1) * deviation;
            double x  = baseX + ox;
            double y  = baseY + oy;
            double z  = baseZ + oz;

            ItemEntity newItem = DogmaItems.CreateItem <ItemEntity> (
                type, call.Session.CharacterID, solarSystemID, Flags.None, 1, true
            );

            newItem.X = x;
            newItem.Y = y;
            newItem.Z = z;
            newItem.Persist ();

            if (SolarSystemDestinyMgr.TryGet (solarSystemID, out DestinyManager destinyMgr))
            {
                BubbleEntity bubble = new BubbleEntity
                {
                    ItemID        = newItem.ID,
                    TypeID        = type.ID,
                    GroupID       = type.Group.ID,
                    CategoryID    = type.Group.Category.ID,
                    Name          = type.Name,
                    OwnerID       = call.Session.CharacterID,
                    CorporationID = call.Session.CorporationID,
                    AllianceID    = 0,
                    CharacterID   = 0,
                    Position      = new Vector3 { X = x, Y = y, Z = z },
                    Velocity      = default (Vector3),
                    Mode          = BallMode.Rigid,
                    Flags         = BallFlag.IsGlobal | BallFlag.IsMassive | BallFlag.IsInteractive,
                    Radius        = type.Radius,
                    Mass          = 1000000.0,
                    MaxVelocity   = 0.0,
                    SpeedFraction = 0.0,
                    Agility       = 1.0
                };

                destinyMgr.RegisterEntity (bubble);
                spawnedBubbles.Add (bubble);
            }
        }

        // Send AddBalls to the deploying player via "charid"
        if (spawnedBubbles.Count > 0)
        {
            int charID         = call.Session.CharacterID;
            int stamp          = DestinyEventBuilder.GetStamp ();
            PyList addBallEvents  = DestinyEventBuilder.BuildAddBalls (spawnedBubbles, solarSystemID, stamp);
            PyTuple notification   = DestinyEventBuilder.WrapAsNotification (addBallEvents);

            Notifications.SendNotification ("DoDestinyUpdate", "charid", charID, notification);
        }

        Log.Information ("Slash /spawnn: spawned {Qty}x typeID={TypeID} with deviation={Deviation}", qty, typeID, deviation);
    }

    /// <summary>
    /// /entity deploy qty typeID [factionID] - Spawn NPC entities with standings-aware AI.
    /// NPCs will check faction standings against nearby players and engage hostiles.
    /// Optional factionID sets which faction the NPC belongs to for standing checks.
    /// Example: /entity deploy 3 23707 500010  (3 Serpentis NPCs, faction=Serpentis)
    /// </summary>
    private void EntityCmd (string [] argv, ServiceCall call)
    {
        if (argv.Length < 4 || argv [1] != "deploy")
            throw new SlashError ("Usage: /entity deploy <qty> <typeID> [factionID]");

        int qty    = int.Parse (argv [2]);
        int typeID = int.Parse (argv [3]);
        int factionID = argv.Length > 4 ? int.Parse (argv [4]) : 0;

        if (qty <= 0 || qty > 100)
            throw new SlashError ("qty must be between 1 and 100");

        if (Types.ContainsKey (typeID) == false)
            throw new SlashError ("The specified typeID doesn't exist");

        Type type = Types [typeID];

        if (type.Group.Category.ID != (int) CategoryID.Entity)
            throw new SlashError ($"typeID {typeID} is not an Entity (category {type.Group.Category.ID})");

        int solarSystemID = call.Session.EnsureCharacterIsInSpace ();
        int shipID        = call.Session.ShipID ?? 0;

        double baseX = 0, baseY = 0, baseZ = 0;

        if (shipID != 0 && SolarSystemDestinyMgr.TryGet (solarSystemID, out DestinyManager dm) &&
            dm.TryGetEntity (shipID, out BubbleEntity shipEnt))
        {
            baseX = shipEnt.Position.X;
            baseY = shipEnt.Position.Y;
            baseZ = shipEnt.Position.Z;
        }

        Random              random         = new Random ();
        List <BubbleEntity> spawnedBubbles = new List<BubbleEntity> ();

        for (int i = 0; i < qty; i++)
        {
            double x = baseX + (random.NextDouble () * 2 - 1) * 10000;
            double y = baseY + (random.NextDouble () * 2 - 1) * 10000;
            double z = baseZ + (random.NextDouble () * 2 - 1) * 10000;

            // Use ownerID=1 (system) — the NPC's faction is set via FactionID on the BubbleEntity
            ItemEntity newItem = DogmaItems.CreateItem <ItemEntity> (
                type, 1, solarSystemID, Flags.None, 1, true
            );

            newItem.X = x;
            newItem.Y = y;
            newItem.Z = z;
            newItem.Persist ();

            if (SolarSystemDestinyMgr.TryGet (solarSystemID, out DestinyManager destinyMgr))
            {
                Vector3 spawnPos = new Vector3 { X = x, Y = y, Z = z };

                BubbleEntity bubble = new BubbleEntity
                {
                    ItemID        = newItem.ID,
                    TypeID        = type.ID,
                    GroupID       = type.Group.ID,
                    CategoryID    = type.Group.Category.ID,
                    Name          = type.Name,
                    OwnerID       = 1,
                    CorporationID = 0,
                    AllianceID    = 0,
                    CharacterID   = 0,
                    Position      = spawnPos,
                    Velocity      = default (Vector3),
                    Mode          = BallMode.Stop,
                    Flags         = BallFlag.IsFree | BallFlag.IsMassive | BallFlag.IsInteractive,
                    Radius        = type.Radius,
                    Mass          = 1000000.0,
                    MaxVelocity   = 200.0,
                    SpeedFraction = 0.0,
                    Agility       = 1.0,
                    // NPC AI properties
                    SpawnPosition = spawnPos
                };

                // Set faction if explicitly provided
                if (factionID != 0)
                    bubble.FactionID = factionID;

                // Populate NPC AI parameters from item attributes (dgmTypeAttributes)
                PopulateNpcAiParams (bubble, newItem);

                destinyMgr.RegisterEntity (bubble);
                spawnedBubbles.Add (bubble);
            }
        }

        // Send AddBalls to the deploying player via "charid" (proven path — same as
        // beyonce's SetState). The "solarsystemid" broadcast path is unreliable for the
        // player who issued the command; their client may silently discard it.
        if (spawnedBubbles.Count > 0)
        {
            int charID         = call.Session.CharacterID;
            int stamp          = DestinyEventBuilder.GetStamp ();
            PyList addBallEvents  = DestinyEventBuilder.BuildAddBalls (spawnedBubbles, solarSystemID, stamp);
            PyTuple notification   = DestinyEventBuilder.WrapAsNotification (addBallEvents);

            Notifications.SendNotification ("DoDestinyUpdate", "charid", charID, notification);
        }

        Log.Information ("Slash /entity: deployed {Qty}x typeID={TypeID} with standings-aware AI", qty, typeID);
    }

    /// <summary>
    /// Populate NPC AI parameters from the entity's dgmTypeAttributes.
    /// Reads entityAttackRange, entityFlyRange, entityChaseMaxDistance, etc.
    /// Also resolves the NPC's faction from its owner corporation.
    /// </summary>
    private void PopulateNpcAiParams (BubbleEntity bubble, ItemEntity item)
    {
        AttributeList attrs = item.Attributes;

        if (attrs.AttributeExists (AttributeTypes.entityAttackRange))
            bubble.AttackRange = (double) attrs [AttributeTypes.entityAttackRange];

        if (attrs.AttributeExists (AttributeTypes.entityFlyRange))
            bubble.OrbitRange = (double) attrs [AttributeTypes.entityFlyRange];

        if (attrs.AttributeExists (AttributeTypes.entityChaseMaxDistance))
            bubble.ChaseMaxDistance = (double) attrs [AttributeTypes.entityChaseMaxDistance];

        if (attrs.AttributeExists (AttributeTypes.entityAttackDelayMin))
            bubble.AttackDelayMin = (double) attrs [AttributeTypes.entityAttackDelayMin];

        if (attrs.AttributeExists (AttributeTypes.entityAttackDelayMax))
            bubble.AttackDelayMax = (double) attrs [AttributeTypes.entityAttackDelayMax];

        if (attrs.AttributeExists (AttributeTypes.maxVelocity))
            bubble.MaxVelocity = (double) attrs [AttributeTypes.maxVelocity];

        if (attrs.AttributeExists (AttributeTypes.entityCruiseSpeed))
            bubble.MaxVelocity = (double) attrs [AttributeTypes.entityCruiseSpeed];

        if (attrs.AttributeExists (AttributeTypes.agility))
            bubble.Agility = (double) attrs [AttributeTypes.agility];

        if (attrs.AttributeExists (AttributeTypes.mass))
            bubble.Mass = (double) attrs [AttributeTypes.mass];

        if (attrs.AttributeExists (AttributeTypes.radius))
            bubble.Radius = (double) attrs [AttributeTypes.radius];

        // NPC damage attributes
        if (attrs.AttributeExists (AttributeTypes.emDamage))
            bubble.NpcEmDamage = (double) attrs [AttributeTypes.emDamage];
        if (attrs.AttributeExists (AttributeTypes.explosiveDamage))
            bubble.NpcExplosiveDamage = (double) attrs [AttributeTypes.explosiveDamage];
        if (attrs.AttributeExists (AttributeTypes.kineticDamage))
            bubble.NpcKineticDamage = (double) attrs [AttributeTypes.kineticDamage];
        if (attrs.AttributeExists (AttributeTypes.thermalDamage))
            bubble.NpcThermalDamage = (double) attrs [AttributeTypes.thermalDamage];

        // NPC HP
        if (attrs.AttributeExists (AttributeTypes.shieldCapacity))
        {
            bubble.ShieldCapacity = (double) attrs [AttributeTypes.shieldCapacity];
            bubble.ShieldCharge   = bubble.ShieldCapacity;
        }
        if (attrs.AttributeExists (AttributeTypes.armorHP))
            bubble.ArmorHP = (double) attrs [AttributeTypes.armorHP];
        if (attrs.AttributeExists (AttributeTypes.hp))
            bubble.StructureHP = (double) attrs [AttributeTypes.hp];

        // NPC shield resistances
        if (attrs.AttributeExists (AttributeTypes.shieldEmDamageResonance))
            bubble.ShieldEmResonance = (double) attrs [AttributeTypes.shieldEmDamageResonance];
        if (attrs.AttributeExists (AttributeTypes.shieldExplosiveDamageResonance))
            bubble.ShieldExplosiveResonance = (double) attrs [AttributeTypes.shieldExplosiveDamageResonance];
        if (attrs.AttributeExists (AttributeTypes.shieldKineticDamageResonance))
            bubble.ShieldKineticResonance = (double) attrs [AttributeTypes.shieldKineticDamageResonance];
        if (attrs.AttributeExists (AttributeTypes.shieldThermalDamageResonance))
            bubble.ShieldThermalResonance = (double) attrs [AttributeTypes.shieldThermalDamageResonance];

        // NPC armor resistances
        if (attrs.AttributeExists (AttributeTypes.armorEmDamageResonance))
            bubble.ArmorEmResonance = (double) attrs [AttributeTypes.armorEmDamageResonance];
        if (attrs.AttributeExists (AttributeTypes.armorExplosiveDamageResonance))
            bubble.ArmorExplosiveResonance = (double) attrs [AttributeTypes.armorExplosiveDamageResonance];
        if (attrs.AttributeExists (AttributeTypes.armorKineticDamageResonance))
            bubble.ArmorKineticResonance = (double) attrs [AttributeTypes.armorKineticDamageResonance];
        if (attrs.AttributeExists (AttributeTypes.armorThermalDamageResonance))
            bubble.ArmorThermalResonance = (double) attrs [AttributeTypes.armorThermalDamageResonance];

        // NPC signature radius
        if (attrs.AttributeExists (AttributeTypes.signatureRadius))
            bubble.SignatureRadius = (double) attrs [AttributeTypes.signatureRadius];

        // Resolve faction from the NPC's owner corporation
        if (bubble.OwnerID > 0 && bubble.FactionID == 0)
        {
            // Faction can be set explicitly via /entity deploy or from dungeon data
        }

        Log.Information ("[NpcAI] Params for {Name}: attackRange={AttackRange:F0}, orbitRange={OrbitRange:F0}, " +
                         "chaseMax={ChaseMax:F0}, maxVel={MaxVel:F0}, faction={FactionID}, " +
                         "dmg(em={EmDmg:F0},exp={ExpDmg:F0},kin={KinDmg:F0},therm={ThermDmg:F0}), " +
                         "hp(shield={ShieldHP:F0},armor={ArmorHP:F0},hull={HullHP:F0})",
            bubble.Name, bubble.AttackRange, bubble.OrbitRange,
            bubble.ChaseMaxDistance, bubble.MaxVelocity, bubble.FactionID,
            bubble.NpcEmDamage, bubble.NpcExplosiveDamage, bubble.NpcKineticDamage, bubble.NpcThermalDamage,
            bubble.ShieldCapacity, bubble.ArmorHP, bubble.StructureHP);
    }

    /// <summary>
    /// /bp typeID [runs] [me] [pe] - Create a blueprint
    /// </summary>
    private void BpCmd (string [] argv, ServiceCall call)
    {
        if (argv.Length < 2)
            throw new SlashError ("Usage: /bp <typeID> [runs] [me] [pe]");

        int typeID = int.Parse (argv [1]);

        if (Types.ContainsKey (typeID) == false)
            throw new SlashError ("The specified typeID doesn't exist");

        call.Session.EnsureCharacterIsInStation ();

        ItemEntity bp = DogmaItems.CreateItem <ItemEntity> (
            Types [typeID], call.Session.CharacterID, call.Session.StationID, Flags.Hangar, 1, true
        );

        Log.Information ("Slash /bp: created blueprint typeID={TypeID} as itemID={ItemID}", typeID, bp.ID);
    }

    /// <summary>
    /// /load target typeID qty - Create items directly into a container's cargo
    /// </summary>
    private void LoadCmd (string [] argv, ServiceCall call)
    {
        if (argv.Length < 4)
            throw new SlashError ("Usage: /load <target|me> <typeID> <qty>");

        int containerID = ResolveItemTarget (argv [1], call);
        int typeID      = int.Parse (argv [2]);
        int qty         = int.Parse (argv [3]);

        if (Types.ContainsKey (typeID) == false)
            throw new SlashError ("The specified typeID doesn't exist");

        DogmaItems.CreateItem <ItemEntity> (
            Types [typeID], call.Session.CharacterID, containerID, Flags.Cargo, qty
        );

        Log.Information ("Slash /load: loaded {Qty}x typeID={TypeID} into container {ContainerID}", qty, typeID, containerID);
    }

    /// <summary>
    /// /repairmodules - Repair all modules on the caller's ship
    /// </summary>
    private void RepairModulesCmd (string [] argv, ServiceCall call)
    {
        int? shipID = call.Session.ShipID;

        if (shipID == null || shipID.Value == 0)
            throw new SlashError ("You don't have an active ship");

        Ship ship = Items.LoadItem <Ship> (shipID.Value);

        if (ship == null)
            throw new SlashError ("Could not load ship");

        int repaired = 0;

        foreach (KeyValuePair <int, ItemEntity> kvp in ship.Items)
        {
            ItemEntity module = kvp.Value;

            if (!module.IsInModuleSlot () && !module.IsInRigSlot ())
                continue;

            if (module.Attributes.AttributeExists (AttributeTypes.damage))
            {
                module.Attributes [AttributeTypes.damage] = new Attribute (AttributeTypes.damage, 0);
                module.Persist ();
                repaired++;
            }
        }

        Log.Information ("Slash /repairmodules: repaired {Count} modules on ship {ShipID}", repaired, shipID.Value);
    }

    /// <summary>
    /// /setstanding fromID toID value reason - Set NPC standings
    /// </summary>
    private void SetStandingCmd (string [] argv, ServiceCall call)
    {
        if (argv.Length < 5)
            throw new SlashError ("Usage: /setstanding <fromID> <toID> <value> <reason>");

        int    fromID = int.Parse (argv [1]);
        int    toID   = int.Parse (argv [2]);
        double value  = double.Parse (argv [3]);
        string reason = string.Join (" ", argv.Skip (4));

        Standings.SetStanding (EventType.StandingSlashSet, fromID, toID, value, reason);
        Log.Information ("Slash /setstanding: {FromID} -> {ToID} = {Value} ({Reason})", fromID, toID, value, reason);
    }

    /// <summary>
    /// /unspawn [range=N] - Destroy spawned entities within range
    /// Supports: /unspawn, /unspawn range=50000
    /// </summary>
    private void UnspawnCmd (string [] argv, ServiceCall call)
    {
        int solarSystemID = call.Session.EnsureCharacterIsInSpace ();
        int shipID        = call.Session.ShipID ?? 0;

        double range = 50000; // default 50km

        // Parse optional arguments
        for (int i = 1; i < argv.Length; i++)
        {
            if (argv [i].StartsWith ("range="))
            {
                double.TryParse (argv [i].Substring (6), out range);
            }
        }

        if (!SolarSystemDestinyMgr.TryGet (solarSystemID, out DestinyManager dm))
            throw new SlashError ("No destiny manager for this solar system");

        Vector3 myPos = default (Vector3);

        if (shipID != 0 && dm.TryGetEntity (shipID, out BubbleEntity myShip))
            myPos = myShip.Position;

        int        destroyed = 0;
        List <int> toRemove  = new List <int> ();

        foreach (BubbleEntity entity in dm.GetEntities ())
        {
            // Skip player ships and celestials (stations, planets, etc.)
            if (entity.IsPlayer)
                continue;
            if (entity.ItemID == shipID)
                continue;

            double dist = myPos.Distance (entity.Position);

            if (dist > range)
                continue;

            toRemove.Add (entity.ItemID);
        }

        foreach (int itemID in toRemove)
        {
            dm.UnregisterEntity (itemID);

            if (Items.TryGetItem (itemID, out ItemEntity item))
                DogmaItems.DestroyItem (item);

            destroyed++;
        }

        Log.Information ("Slash /unspawn: destroyed {Count} entities within {Range}m in system {SystemID}", destroyed, range, solarSystemID);
    }

    /// <summary>
    /// /moveme stationID - Simple alias for /move, moves the caller to a station
    /// </summary>
    private void MoveMeCmd (string [] argv, ServiceCall call)
    {
        if (argv.Length < 2)
            throw new SlashError ("Usage: /moveme <stationID>");

        if (int.TryParse (argv [1], out int stationID) == false)
            throw new SlashError ("stationID must be a number");

        Station target = Items.GetStaticStation (stationID);
        DoMoveToStation (call.Session.CharacterID, call.Session, target);
    }

    /// <summary>
    /// /kill target - Destroy the target's ship.
    /// If in a normal ship: ship is destroyed, player spawns in a capsule at the same position.
    /// If in a capsule: pod killed, player respawns docked at clone station in a new capsule.
    /// </summary>
    private void KillCmd (string [] argv, ServiceCall call)
    {
        if (argv.Length < 2)
            throw new SlashError ("Usage: /kill <target|me>");

        int targetCharacterID = ResolveCharacterTarget (argv [1], call);

        Session targetSession =
            (targetCharacterID == call.Session.CharacterID)
                ? call.Session
                : SessionManager.FindSession ("charid", targetCharacterID).FirstOrDefault ();

        if (targetSession == null)
            throw new SlashError ("Target character is not online.");

        int? solarSystemID = targetSession.SolarSystemID;

        if (solarSystemID == null)
            throw new SlashError ("Target character is not in space.");

        int shipID = targetSession.ShipID ?? 0;

        if (shipID == 0)
            throw new SlashError ("Target character has no active ship.");

        if (!SolarSystemDestinyMgr.TryGet (solarSystemID.Value, out DestinyManager dm))
            throw new SlashError ("No destiny manager for the target's solar system.");

        // Get ship position
        double posX = 0, posY = 0, posZ = 0;

        if (dm.TryGetEntity (shipID, out BubbleEntity shipBubble))
        {
            posX = shipBubble.Position.X;
            posY = shipBubble.Position.Y;
            posZ = shipBubble.Position.Z;
        }

        // Check if ship is a capsule
        ItemEntity shipEntity = Items.LoadItem (shipID);

        if (shipEntity == null)
            throw new SlashError ($"Ship {shipID} not found.");

        bool isCapsule = shipEntity.Type.ID == (int) TypeID.Capsule;

        // Unregister ship from DestinyManager and broadcast RemoveBalls
        dm.UnregisterEntity (shipID);

        PyList removeEvents  = DestinyEventBuilder.BuildRemoveBalls (new[] { shipID });
        PyTuple removeNotif   = DestinyEventBuilder.WrapAsNotification (removeEvents);

        Notifications.SendNotification ("DoDestinyUpdate", "solarsystemid", solarSystemID.Value, removeNotif);

        // Destroy the ship item
        DogmaItems.DestroyItem (shipEntity);

        Log.Information ("Slash /kill: destroyed ship {ShipID} (isCapsule={IsCapsule}) for char {CharID}",
            shipID, isCapsule, targetCharacterID);

        if (isCapsule)
        {
            // --- POD KILL: respawn at clone station ---
            Character character = Items.LoadItem<Character> (targetCharacterID);

            int cloneStationID = character.StationID;

            if (character.ActiveCloneID != null && character.ActiveCloneID.Value != 0)
            {
                ItemEntity cloneItem = Items.LoadItem (character.ActiveCloneID.Value);

                if (cloneItem != null && cloneItem.LocationID != 0)
                    cloneStationID = cloneItem.LocationID;
            }

            Station station = Items.GetStaticStation (cloneStationID);

            if (station == null)
                throw new SlashError ($"Clone station {cloneStationID} not found.");

            // Create capsule at clone station
            ItemInventory capsule = DogmaItems.CreateItem<ItemInventory> (
                character.Name + "'s Capsule", Types [TypeID.Capsule], targetCharacterID, cloneStationID, Flags.Hangar, 1, true
            );

            DogmaItems.MoveItem (character, capsule.ID, Flags.Pilot);

            // Update character location in DB
            CharacterDB.UpdateStationAndLocation (
                targetCharacterID,
                cloneStationID,
                station.SolarSystemID,
                station.ConstellationID,
                station.RegionID
            );

            // Session change: dock at clone station
            Session delta = new Session ();

            delta[Session.SHIP_ID]          = (PyInteger) capsule.ID;
            delta[Session.STATION_ID]       = (PyInteger) cloneStationID;
            delta[Session.LOCATION_ID]      = (PyInteger) cloneStationID;
            delta[Session.SOLAR_SYSTEM_ID]  = new PyNone ();
            delta[Session.SOLAR_SYSTEM_ID2] = (PyInteger) station.SolarSystemID;
            delta[Session.CONSTELLATION_ID] = (PyInteger) station.ConstellationID;
            delta[Session.REGION_ID]        = (PyInteger) station.RegionID;

            SessionManager.PerformSessionUpdate (Session.CHAR_ID, targetCharacterID, delta);

            Log.Information ("Slash /kill: pod kill -> char {CharID} respawned at station {StationID} in capsule {CapsuleID}",
                targetCharacterID, cloneStationID, capsule.ID);
        }
        else
        {
            // --- SHIP DEATH: spawn capsule at ship's position ---
            Character character = Items.LoadItem<Character> (targetCharacterID);

            ItemInventory capsule = DogmaItems.CreateItem<ItemInventory> (
                character.Name + "'s Capsule", Types [TypeID.Capsule], targetCharacterID, solarSystemID.Value, Flags.None, 1, true
            );

            capsule.X = posX;
            capsule.Y = posY;
            capsule.Z = posZ;
            capsule.Persist ();

            DogmaItems.MoveItem (character, capsule.ID, Flags.Pilot);

            // Update session with new capsule
            SessionManager.PerformSessionUpdate (Session.CHAR_ID, targetCharacterID, new Session { ShipID = capsule.ID });

            // Register capsule in DestinyManager
            BubbleEntity capsuleBubble = new BubbleEntity
            {
                ItemID        = capsule.ID,
                TypeID        = (int) TypeID.Capsule,
                GroupID       = (int) GroupID.Capsule,
                CategoryID    = (int) CategoryID.Ship,
                Name          = capsule.Name ?? character.Name + "'s Capsule",
                OwnerID       = targetCharacterID,
                CorporationID = targetSession.CorporationID,
                AllianceID    = 0,
                CharacterID   = targetCharacterID,
                Position      = new Vector3 { X = posX, Y = posY, Z = posZ },
                Velocity      = default (Vector3),
                Mode          = BallMode.Stop,
                Flags         = BallFlag.IsFree | BallFlag.IsMassive | BallFlag.IsInteractive,
                Radius        = 50.0,
                Mass          = 32000.0,
                MaxVelocity   = 150.0,
                SpeedFraction = 0.0,
                Agility       = 1.0
            };

            dm.RegisterEntity (capsuleBubble);

            // Broadcast AddBalls for the capsule
            int stamp          = DestinyEventBuilder.GetStamp ();
            PyList addBallEvents  = DestinyEventBuilder.BuildAddBalls (new[] { capsuleBubble }, solarSystemID.Value, stamp);
            PyTuple addBallNotif   = DestinyEventBuilder.WrapAsNotification (addBallEvents);

            Notifications.SendNotification ("DoDestinyUpdate", "solarsystemid", solarSystemID.Value, addBallNotif);

            Log.Information ("Slash /kill: ship death -> char {CharID} now in capsule {CapsuleID} at ({X:F0},{Y:F0},{Z:F0})",
                targetCharacterID, capsule.ID, posX, posY, posZ);
        }
    }

    /// <summary>
    /// /dungeon - Dungeon management commands
    /// </summary>
    private void DungeonCmd (string [] argv, ServiceCall call)
    {
        if (argv.Length < 2)
            throw new SlashError (
                "Usage:\n" +
                "  /dungeon list\n" +
                "  /dungeon create <name> [archetypeID] [factionID]\n" +
                "  /dungeon addroom <dungeonID> <roomName>\n" +
                "  /dungeon addobject <roomID> <typeID> [x y z]\n" +
                "  /dungeon play <dungeonID> [roomID]\n" +
                "  /dungeon reset");

        string sub = argv [1].ToLower ();

        switch (sub)
        {
            case "list":
            {
                string msg = "Dungeons:\n";
                foreach (KeyValuePair <int, DungeonDefinition> kvp in DungeonData.Dungeons)
                {
                    DungeonDefinition d = kvp.Value;
                    msg += $"  [{d.DungeonID}] {d.DungeonName} (archetype={d.ArchetypeID})\n";
                    foreach (int rid in d.RoomIDs)
                    {
                        if (DungeonData.Rooms.TryGetValue (rid, out RoomDefinition room))
                            msg += $"    Room [{room.RoomID}] {room.RoomName} ({room.ObjectIDs.Count} objects)\n";
                    }
                }
                throw new SlashError (msg);
            }

            case "create":
            {
                if (argv.Length < 3)
                    throw new SlashError ("Usage: /dungeon create <name> [archetypeID] [factionID]");

                string name = argv [2];
                int archetypeID = argv.Length > 3 ? int.Parse (argv [3]) : 1;
                int factionID = argv.Length > 4 ? int.Parse (argv [4]) : 0;

                int newID = DungeonData.NextDungeonID ();
                DungeonData.Dungeons [newID] = new DungeonDefinition
                {
                    DungeonID = newID,
                    DungeonName = name,
                    ArchetypeID = archetypeID,
                    FactionID = factionID
                };

                Log.Information ("Slash /dungeon create: created dungeon {ID} '{Name}'", newID, name);
                throw new SlashError ($"Created dungeon [{newID}] {name}");
            }

            case "addroom":
            {
                if (argv.Length < 4)
                    throw new SlashError ("Usage: /dungeon addroom <dungeonID> <roomName>");

                int dungeonID = int.Parse (argv [2]);
                string roomName = argv [3];

                if (!DungeonData.Dungeons.TryGetValue (dungeonID, out DungeonDefinition dung))
                    throw new SlashError ($"Dungeon {dungeonID} not found");

                int newRoomID = DungeonData.NextRoomID ();
                DungeonData.Rooms [newRoomID] = new RoomDefinition
                {
                    RoomID = newRoomID,
                    DungeonID = dungeonID,
                    RoomName = roomName,
                    ShortName = roomName
                };
                dung.RoomIDs.Add (newRoomID);

                Log.Information ("Slash /dungeon addroom: added room {RoomID} '{Name}' to dungeon {DungeonID}", newRoomID, roomName, dungeonID);
                throw new SlashError ($"Added room [{newRoomID}] {roomName} to dungeon [{dungeonID}] {dung.DungeonName}");
            }

            case "addobject":
            {
                if (argv.Length < 4)
                    throw new SlashError ("Usage: /dungeon addobject <roomID> <typeID> [x y z]");

                int roomID = int.Parse (argv [2]);
                int typeID = int.Parse (argv [3]);

                if (!DungeonData.Rooms.TryGetValue (roomID, out RoomDefinition room))
                    throw new SlashError ($"Room {roomID} not found");
                if (!Types.ContainsKey (typeID))
                    throw new SlashError ($"TypeID {typeID} not found");

                double ox = argv.Length > 4 ? double.Parse (argv [4]) : 0;
                double oy = argv.Length > 5 ? double.Parse (argv [5]) : 0;
                double oz = argv.Length > 6 ? double.Parse (argv [6]) : 0;

                int newObjID = DungeonData.NextObjectID ();
                DungeonData.Objects [newObjID] = new DungeonObject
                {
                    ObjectID = newObjID,
                    RoomID = roomID,
                    ObjectName = Types [typeID].Name,
                    TypeID = typeID,
                    X = ox, Y = oy, Z = oz,
                    Radius = Types [typeID].Radius
                };
                room.ObjectIDs.Add (newObjID);

                Log.Information ("Slash /dungeon addobject: added object {ObjID} type={TypeID} to room {RoomID}", newObjID, typeID, roomID);
                throw new SlashError ($"Added [{newObjID}] {Types [typeID].Name} to room [{roomID}] {room.RoomName} at ({ox},{oy},{oz})");
            }

            case "play":
            {
                if (argv.Length < 3)
                    throw new SlashError ("Usage: /dungeon play <dungeonID> [roomID]");

                int dungeonID = int.Parse (argv [2]);
                if (!DungeonData.Dungeons.TryGetValue (dungeonID, out DungeonDefinition dung))
                    throw new SlashError ($"Dungeon {dungeonID} not found");

                int roomID = 0;
                if (argv.Length > 3)
                    roomID = int.Parse (argv [3]);
                else if (dung.RoomIDs.Count > 0)
                    roomID = dung.RoomIDs [0];

                if (!DungeonData.Rooms.TryGetValue (roomID, out RoomDefinition room))
                    throw new SlashError ($"Room {roomID} not found. Use /dungeon list to see room IDs.");

                int solarSystemID = call.Session.EnsureCharacterIsInSpace ();
                int shipID = call.Session.ShipID ?? 0;
                double baseX = 0, baseY = 0, baseZ = 0;

                if (shipID != 0 && SolarSystemDestinyMgr.TryGet (solarSystemID, out DestinyManager dm) &&
                    dm.TryGetEntity (shipID, out BubbleEntity shipEnt))
                {
                    baseX = shipEnt.Position.X;
                    baseY = shipEnt.Position.Y;
                    baseZ = shipEnt.Position.Z;
                }

                List <int> spawnedList = DungeonData.SpawnedEntities.GetOrAdd (
                    call.Session.CharacterID, _ => new List<int> ());
                List <BubbleEntity> spawnedBubbles = new List<BubbleEntity> ();

                foreach (int objID in room.ObjectIDs)
                {
                    if (!DungeonData.Objects.TryGetValue (objID, out DungeonObject obj))
                        continue;
                    if (!Types.ContainsKey (obj.TypeID))
                        continue;

                    Type type = Types [obj.TypeID];
                    double x = baseX + obj.X;
                    double y = baseY + obj.Y;
                    double z = baseZ + obj.Z;

                    ItemEntity newItem = DogmaItems.CreateItem<ItemEntity> (
                        type, call.Session.CharacterID, solarSystemID, Flags.None, 1, true);
                    newItem.X = x;
                    newItem.Y = y;
                    newItem.Z = z;
                    newItem.Persist ();

                    if (SolarSystemDestinyMgr.TryGet (solarSystemID, out DestinyManager destinyMgr))
                    {
                        BubbleEntity bubble = new BubbleEntity
                        {
                            ItemID        = newItem.ID,
                            TypeID        = type.ID,
                            GroupID       = type.Group.ID,
                            CategoryID    = type.Group.Category.ID,
                            Name          = obj.ObjectName ?? type.Name,
                            OwnerID       = call.Session.CharacterID,
                            CorporationID = call.Session.CorporationID,
                            AllianceID    = 0,
                            CharacterID   = 0,
                            Position      = new Vector3 { X = x, Y = y, Z = z },
                            Velocity      = default (Vector3),
                            Mode          = BallMode.Rigid,
                            Flags         = BallFlag.IsGlobal | BallFlag.IsMassive | BallFlag.IsInteractive,
                            Radius        = obj.Radius > 0 ? obj.Radius : type.Radius,
                            Mass          = 1000000.0,
                            MaxVelocity   = 0.0,
                            SpeedFraction = 0.0,
                            Agility       = 1.0
                        };
                        destinyMgr.RegisterEntity (bubble);
                        spawnedBubbles.Add (bubble);
                    }

                    spawnedList.Add (newItem.ID);
                }

                // Send AddBalls to the deploying player via "charid"
                if (spawnedBubbles.Count > 0)
                {
                    int charID = call.Session.CharacterID;
                    int stamp = DestinyEventBuilder.GetStamp ();
                    PyList addBallEvents = DestinyEventBuilder.BuildAddBalls (spawnedBubbles, solarSystemID, stamp);
                    PyTuple notification  = DestinyEventBuilder.WrapAsNotification (addBallEvents);
                    Notifications.SendNotification ("DoDestinyUpdate", "charid", charID, notification);
                }

                Log.Information ("Slash /dungeon play: spawned {Count} objects from dungeon {DungeonID} room {RoomID}",
                    room.ObjectIDs.Count, dungeonID, roomID);
                break;
            }

            case "reset":
            {
                int charID = call.Session.CharacterID;
                if (!DungeonData.SpawnedEntities.TryGetValue (charID, out List <int> spawnedList))
                    throw new SlashError ("No spawned dungeon entities to reset");

                int        solarSystemID = call.Session.EnsureCharacterIsInSpace ();
                List <int> removedIDs    = new List<int> ();

                foreach (int itemID in spawnedList.ToArray ())
                {
                    if (SolarSystemDestinyMgr.TryGet (solarSystemID, out DestinyManager dm))
                        dm.UnregisterEntity (itemID);

                    if (Items.TryGetItem (itemID, out ItemEntity item))
                        DogmaItems.DestroyItem (item);

                    removedIDs.Add (itemID);
                }

                spawnedList.Clear ();

                // Broadcast RemoveBalls so clients remove them from the scene
                if (removedIDs.Count > 0)
                {
                    PyList removeEvents = DestinyEventBuilder.BuildRemoveBalls (removedIDs);
                    PyTuple notification = DestinyEventBuilder.WrapAsNotification (removeEvents);
                    Notifications.SendNotification ("DoDestinyUpdate", "solarsystemid", solarSystemID, notification);
                }

                Log.Information ("Slash /dungeon reset: destroyed {Count} entities for char {CharID}", removedIDs.Count, charID);
                break;
            }

            default:
                throw new SlashError (
                    "Usage:\n" +
                    "  /dungeon list\n" +
                    "  /dungeon create <name> [archetypeID] [factionID]\n" +
                    "  /dungeon addroom <dungeonID> <roomName>\n" +
                    "  /dungeon addobject <roomID> <typeID> [x y z]\n" +
                    "  /dungeon play <dungeonID> [roomID]\n" +
                    "  /dungeon reset");
        }
    }
}
