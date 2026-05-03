using System;
using System.Collections.Generic;
using System.Linq;
using EVESharp.Database;
using EVESharp.Database.Inventory;
using EVESharp.Database.Inventory.Categories;
using EVESharp.Database.Inventory.Groups;
using EVESharp.Database.Inventory.Types;
using EVESharp.Database.Old;
using EVESharp.Destiny;
using EVESharp.EVE.Data.Inventory;
using EVESharp.EVE.Data.Inventory.Items;
using EVESharp.EVE.Data.Inventory.Items.Types;
using EVESharp.EVE.Dogma;
using EVESharp.EVE.Notifications;
using EVESharp.EVE.Sessions;
using EVESharp.Node.Services.Space;
using EVESharp.Types;
using EVESharp.Types.Collections;
using Serilog;

namespace EVESharp.Node.Services.Combat;

/// <summary>
/// Singleton service that handles player ship destruction from combat.
/// Spawns a capsule at the death position (ship death) or respawns at clone station (pod kill).
/// </summary>
public class PlayerDeathHandler
{
    private readonly IItems              mItems;
    private readonly IDogmaItems         mDogmaItems;
    private readonly ISessionManager     mSessionManager;
    private readonly INotificationSender mNotifications;
    private readonly DestinyBroadcaster  mBroadcaster;
    private readonly IDatabase           mDatabase;
    private readonly ILogger             mLog;

    private ITypes Types => mItems.Types;

    public PlayerDeathHandler (IItems items, IDogmaItems dogmaItems, ISessionManager sessionManager,
                               INotificationSender notifications, DestinyBroadcaster broadcaster, IDatabase database, ILogger logger)
    {
        mItems          = items;
        mDogmaItems     = dogmaItems;
        mSessionManager = sessionManager;
        mNotifications  = notifications;
        mBroadcaster    = broadcaster;
        mDatabase       = database;
        mLog            = logger;
    }

    /// <summary>
    /// Handle a player ship destroyed by combat (NPC or weapon fire).
    /// Looks up the player's session, determines ship vs pod kill, and handles accordingly.
    /// </summary>
    public void HandlePlayerShipDestroyed (int solarSystemID, BubbleEntity shipBubble, DestinyManager destinyMgr)
    {
        try
        {
            int charID = shipBubble.CharacterID;
            if (charID == 0)
            {
                mLog.Warning ("[PlayerDeathHandler] Ship {ShipID} has no CharacterID, cannot handle death", shipBubble.ItemID);
                return;
            }

            Session session = mSessionManager.FindSession ("charid", charID).FirstOrDefault ();
            if (session == null)
            {
                mLog.Warning ("[PlayerDeathHandler] No session found for char {CharID}", charID);
                return;
            }

            int    shipID = shipBubble.ItemID;
            double posX   = shipBubble.Position.X;
            double posY   = shipBubble.Position.Y;
            double posZ   = shipBubble.Position.Z;

            // Load ship item to check if it's a capsule
            ItemEntity shipEntity = mItems.LoadItem (shipID);
            if (shipEntity == null)
            {
                mLog.Warning ("[PlayerDeathHandler] Ship {ShipID} not found in items", shipID);
                return;
            }

            bool isCapsule = shipEntity.Type.ID == (int) TypeID.Capsule;

            // Send TerminalExplosion so client shows explosion FX before the ball is removed
            mBroadcaster?.BroadcastTerminalExplosion (solarSystemID, shipID);

            // Unregister ship from DestinyManager and broadcast RemoveBalls
            destinyMgr.UnregisterEntity (shipID);

            PyList removeEvents = DestinyEventBuilder.BuildRemoveBalls (new[] { shipID });
            PyTuple removeNotif  = DestinyEventBuilder.WrapAsNotification (removeEvents);
            mNotifications.SendNotification ("DoDestinyUpdate", "solarsystemid", solarSystemID, removeNotif);

            // Destroy the ship item
            mDogmaItems.DestroyItem (shipEntity);

            mLog.Information ("[PlayerDeathHandler] Destroyed ship {ShipID} (isCapsule={IsCapsule}) for char {CharID}",
                              shipID, isCapsule, charID);

            if (isCapsule)
                HandlePodKill (charID, solarSystemID);
            else
                HandleShipDeath (charID, solarSystemID, session, posX, posY, posZ, destinyMgr);
        }
        catch (Exception ex)
        {
            mLog.Error (ex, "[PlayerDeathHandler] Error handling player death for ship {ShipID}: {Message}",
                        shipBubble?.ItemID, ex.Message);
        }
    }

    /// <summary>
    /// Ship destroyed in space -> spawn capsule at the same position.
    /// </summary>
    private void HandleShipDeath (int    charID, int    solarSystemID, Session session,
                                  double posX,   double posY,          double  posZ, DestinyManager destinyMgr)
    {
        mLog.Information ("[PlayerDeathHandler] HandleShipDeath: char={CharID}, spawning capsule at ({X:F0},{Y:F0},{Z:F0})",
                          charID, posX, posY, posZ);

        Character character = mItems.LoadItem<Character> (charID);
        if (character == null)
        {
            mLog.Error ("[PlayerDeathHandler] Character {CharID} not found", charID);
            return;
        }

        // Create capsule in the solar system
        ItemInventory capsule = mDogmaItems.CreateItem<ItemInventory> (
            character.Name + "'s Capsule", Types [TypeID.Capsule], charID, solarSystemID, Flags.None, 1, true
        );

        // Set capsule position to where the ship was destroyed
        capsule.X = posX;
        capsule.Y = posY;
        capsule.Z = posZ;
        capsule.Persist ();

        // Move character into the capsule
        mDogmaItems.MoveItem (character, capsule.ID, Flags.Pilot);

        // Update session to the new capsule ship
        mSessionManager.PerformSessionUpdate (Session.CHAR_ID, charID, new Session { ShipID = capsule.ID });

        // Register capsule in DestinyManager
        BubbleEntity capsuleBubble = new BubbleEntity
        {
            ItemID        = capsule.ID,
            TypeID        = (int) TypeID.Capsule,
            GroupID       = (int) GroupID.Capsule,
            CategoryID    = (int) CategoryID.Ship,
            Name          = capsule.Name ?? character.Name + "'s Capsule",
            OwnerID       = charID,
            CorporationID = session.CorporationID,
            AllianceID    = 0,
            CharacterID   = charID,
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

        destinyMgr.RegisterEntity (capsuleBubble);

        // Broadcast AddBalls for the capsule so other players see it
        int stamp         = DestinyEventBuilder.GetStamp ();
        PyList addBallEvents = DestinyEventBuilder.BuildAddBalls (new[] { capsuleBubble }, solarSystemID, stamp);
        PyTuple addBallNotif  = DestinyEventBuilder.WrapAsNotification (addBallEvents);

        mNotifications.SendNotification ("DoDestinyUpdate", "solarsystemid", solarSystemID, addBallNotif);

        mLog.Information ("[PlayerDeathHandler] HandleShipDeath: capsule {CapsuleID} spawned for char {CharID}",
                          capsule.ID, charID);
    }

    /// <summary>
    /// Pod killed -> respawn at clone station.
    /// </summary>
    private void HandlePodKill (int charID, int solarSystemID)
    {
        mLog.Information ("[PlayerDeathHandler] HandlePodKill: char={CharID}, respawning at clone station", charID);

        Character character = mItems.LoadItem<Character> (charID);
        if (character == null)
        {
            mLog.Error ("[PlayerDeathHandler] Character {CharID} not found", charID);
            return;
        }

        // Find clone station
        int cloneStationID = character.StationID;

        if (character.ActiveCloneID != null && character.ActiveCloneID.Value != 0)
        {
            ItemEntity cloneItem = mItems.LoadItem (character.ActiveCloneID.Value);
            if (cloneItem != null && cloneItem.LocationID != 0)
                cloneStationID = cloneItem.LocationID;
        }

        Station station = mItems.GetStaticStation (cloneStationID);
        if (station == null)
        {
            mLog.Error ("[PlayerDeathHandler] Clone station {StationID} not found, cannot respawn", cloneStationID);
            return;
        }

        // Create a new capsule at the clone station
        ItemInventory capsule = mDogmaItems.CreateItem<ItemInventory> (
            character.Name + "'s Capsule", Types [TypeID.Capsule], charID, cloneStationID, Flags.Hangar, 1, true
        );

        // Move character into the capsule
        mDogmaItems.MoveItem (character, capsule.ID, Flags.Pilot);

        // Update chrInformation to clone station
        mDatabase.Prepare (
            "UPDATE chrInformation " +
            "SET stationID = @stationID, solarSystemID = @solarSystemID, " +
            "    constellationID = @constellationID, regionID = @regionID " +
            "WHERE characterID = @characterID",
            new Dictionary<string, object>
            {
                { "@characterID", charID },
                { "@stationID", cloneStationID },
                { "@solarSystemID", station.SolarSystemID },
                { "@constellationID", station.ConstellationID },
                { "@regionID", station.RegionID }
            }
        );

        // Session change: dock at clone station with new capsule
        Session delta = new Session ();
        delta [Session.SHIP_ID]          = (PyInteger) capsule.ID;
        delta [Session.STATION_ID]       = (PyInteger) cloneStationID;
        delta [Session.LOCATION_ID]      = (PyInteger) cloneStationID;
        delta [Session.SOLAR_SYSTEM_ID]  = new PyNone ();
        delta [Session.SOLAR_SYSTEM_ID2] = (PyInteger) station.SolarSystemID;
        delta [Session.CONSTELLATION_ID] = (PyInteger) station.ConstellationID;
        delta [Session.REGION_ID]        = (PyInteger) station.RegionID;

        mSessionManager.PerformSessionUpdate (Session.CHAR_ID, charID, delta);

        mLog.Information ("[PlayerDeathHandler] HandlePodKill: char {CharID} respawned at station {StationID} in capsule {CapsuleID}",
                          charID, cloneStationID, capsule.ID);
    }
}