using System;
using System.Collections.Generic;
using EVESharp.Database.Types;
using EVESharp.Destiny;
using EVESharp.EVE.Data.Inventory.Items;
using EVESharp.EVE.Data.Inventory;
using EVESharp.EVE.Network.Services;
using EVESharp.EVE.Sessions;
using EVESharp.Node.Services;
using EVESharp.Types;
using EVESharp.Types.Collections;
using EVESharp.Types.Serialization;

namespace EVESharp.Node.Services.Space;

// DISABLED: consolidated into beyonce.cs which is now the single ballpark service.
// Kept for reference only — not registered in DI container.
[ConcreteService("ballparkSvc_disabled")]
public class ballparkSvc : ClientBoundService
{
    private Ballpark mBallpark;
    private IItems   Items { get; }

    public override AccessLevel AccessLevel => AccessLevel.None;

    // --------------------------------------------------------------------
    // Global / Unbound Constructor
    // --------------------------------------------------------------------
    public ballparkSvc(IBoundServiceManager manager, IItems items)
        : base(manager)
    {
        Console.WriteLine("[ballparkSvc] Global service constructed");
        Items = items;
    }

    // --------------------------------------------------------------------
    // Bound Constructor
    // --------------------------------------------------------------------
    internal ballparkSvc(IBoundServiceManager manager, Session session, int objectID, IItems items)
        : base(manager, session, objectID)
    {
        int solarSystemID = session.SolarSystemID ?? 0;
        int ownerID       = session.CharacterID;

        Console.WriteLine(
            $"[ballparkSvc] ctor: solarSystemID={solarSystemID}, charID={ownerID}, shipID={session.ShipID}");

        Items = items;
        mBallpark  = new Ballpark(solarSystemID, ownerID);
        Console.WriteLine("[ballparkSvc] Ballpark created (no station autoload — stationSvc provides entities)");
    }

    // --------------------------------------------------------------------
    // Macho Resolve
    // --------------------------------------------------------------------
    protected override long MachoResolveObject(ServiceCall call, ServiceBindParams parameters)
    {
        // Ballparks are always on the local node
        return BoundServiceManager.MachoNet.NodeID;
    }

    // --------------------------------------------------------------------
    // Build Bound Instance
    // --------------------------------------------------------------------
    protected override BoundService CreateBoundInstance(ServiceCall call, ServiceBindParams bindParams)
    {
        Console.WriteLine(
            $"[ballparkSvc] CreateBoundInstance: objectID={bindParams.ObjectID}, charID={call.Session.CharacterID}, shipID={call.Session.ShipID}");

        // objectID here should be the solarSystemID
        return new ballparkSvc(BoundServiceManager, call.Session, bindParams.ObjectID, Items);
    }

    // --------------------------------------------------------------------
    // EnterBallpark (NOT actually used by the client in Apoc, but harmless)
    // --------------------------------------------------------------------
    public PyDataType EnterBallpark(ServiceCall call)
    {
        EnsureBallpark(call.Session);

        Console.WriteLine(
            $"[ballparkSvc] EnterBallpark(): charID={call.Session.CharacterID}, shipID={call.Session.ShipID}, system={mBallpark.SolarSystemID}");
        Console.WriteLine($"[ballparkSvc] EnterBallpark(): entities in ballpark before snapshot = {mBallpark.Entities.Count}");

        PyDataType snapshot = BuildSnapshot(
            call.Session.SolarSystemID ?? 0,
            call.Session.ShipID ?? 0,
            call.Session
        );

        Console.WriteLine("[ballparkSvc] EnterBallpark(): snapshot built, returning to client.");
        return snapshot;
    }

    // --------------------------------------------------------------------
    // GetInitialState (used by some client builds)
    // --------------------------------------------------------------------
    public PyDataType GetInitialState(ServiceCall call)
    {
        EnsureBallpark(call.Session);

        Console.WriteLine(
            $"[ballparkSvc] GetInitialState(): charID={call.Session.CharacterID}, shipID={call.Session.ShipID}, system={mBallpark.SolarSystemID}");
        Console.WriteLine($"[ballparkSvc] GetInitialState(): entities in ballpark before snapshot = {mBallpark.Entities.Count}");

        return BuildSnapshot(
            call.Session.SolarSystemID ?? 0,
            call.Session.ShipID ?? 0,
            call.Session
        );
    }

    // GetBallPark – client calls this first.
    // --------------------------------------------------------------------
    public PyDataType GetBallPark(ServiceCall call, PyInteger solarSystemID)
    {
        Console.WriteLine(
            $"[ballparkSvc] GetBallPark(): charID={call.Session.CharacterID}, solarsystemID={solarSystemID.Value}, shipID={call.Session.ShipID}");
        Console.WriteLine("[ballparkSvc] GetBallPark() was called by client");

        EnsureBallpark(call.Session);

        int objectID = (int)solarSystemID.Value;
        Console.WriteLine($"[ballparkSvc] GetBallPark(): returning objectID={objectID}");

        PyDictionary desc = new PyDictionary
        {
            ["nodeID"]   = new PyInteger(BoundServiceManager.MachoNet.NodeID),
            ["service"]  = new PyString("ballpark"),
            ["objectID"] = new PyInteger(objectID)
        };

        // util.KeyVal with nodeID/service/objectID – this is what the moniker code expects
        return new PyObjectData("util.KeyVal", desc);
    }

    // --------------------------------------------------------------------
    // UpdateStateRequest (this is what michelle / Park calls!)
    // --------------------------------------------------------------------
    public PyDataType UpdateStateRequest(ServiceCall call)
    {
        // Make sure the in-memory ballpark exists for this session
        EnsureBallpark(call.Session);

        Console.WriteLine(
            $"[ballparkSvc] UpdateStateRequest(): charID={call.Session.CharacterID}, shipID={call.Session.ShipID}, system={mBallpark.SolarSystemID}");
        Console.WriteLine($"[ballparkSvc] UpdateStateRequest(): entities in ballpark before snapshot = {mBallpark.Entities.Count}");

        PyDataType snapshot = BuildSnapshot(
            call.Session.SolarSystemID ?? 0,
            call.Session.ShipID ?? 0,
            call.Session
        );

        Console.WriteLine("[ballparkSvc] UpdateStateRequest(): snapshot built, returning to client.");
        return snapshot;
    }

    public PyDataType GetFormations(ServiceCall call)
    {
        Console.WriteLine("[beyonce] GetFormations() called");
        // Return empty formations list - format may need tuning
        return new PyList();
    }

    // --------------------------------------------------------------------
    // EnsureBallpark – just creates Ballpark for now
    // --------------------------------------------------------------------
    private void EnsureBallpark(Session session)
    {
        if (mBallpark != null)
        {
            Console.WriteLine($"[ballparkSvc] EnsureBallpark: existing ballpark reused");
            return;
        }

        int solarSystemID = session.SolarSystemID ?? 0;
        int ownerID       = session.CharacterID;
        int shipID        = session.ShipID ?? 0;
        int stationID     = session.StationID; // May be 0 if in space

        Console.WriteLine($"[ballparkSvc] EnsureBallpark: creating ballpark");

        mBallpark = new Ballpark(solarSystemID, ownerID);

        // Auto-load the player's ship
        if (shipID != 0 && Items.TryGetItem(shipID, out ItemEntity shipEntity))
        {
            Console.WriteLine($"[ballparkSvc] Auto-adding ship entity {shipID}");
            mBallpark.AddEntity(shipEntity);
        }

        // Auto-load the station if player just undocked
        if (stationID != 0 && Items.TryGetItem(stationID, out ItemEntity stationEntity))
        {
            Console.WriteLine($"[ballparkSvc] Auto-adding station entity {stationID}");
            mBallpark.AddEntity(stationEntity);
        }

        Console.WriteLine("[ballparkSvc] Ballpark created with entities");
    }

    /// <summary>
    /// Public method to build a snapshot for a specific session.
    /// Used by ship.Undock() to return ballpark data directly.
    /// </summary>
    public PyDataType BuildSnapshotForSession(int solarSystemID, int shipID, int characterID, int stationID)
    {
        Console.WriteLine($"[ballparkSvc] BuildSnapshotForSession: system={solarSystemID}, ship={shipID}, char={characterID}");
            
        // Create a minimal session for EnsureBallpark
        Session session = new Session
        {
            CharacterID = characterID
        };
            
        // Manually set the internal dictionary values to avoid property setter issues
        session[Session.SHIP_ID]         = new PyInteger(shipID);
        session[Session.SOLAR_SYSTEM_ID] = new PyInteger(solarSystemID);
        session[Session.STATION_ID]      = new PyInteger(stationID);
            
        // Ensure ballpark exists and entities are loaded
        EnsureBallpark(session);
            
        // Call BuildSnapshot with the int values directly
        return BuildSnapshot(solarSystemID, shipID, session);
    }

    // --------------------------------------------------------------------
    // Manual AddEntity (called from michelle.MachoBindObject)
    // --------------------------------------------------------------------
    public void AddEntity(ItemEntity entity)
    {
        EnsureBallpark(Session);

        Console.WriteLine(
            $"[ballparkSvc] AddEntity: entityID={entity.ID}, typeID={entity.Type?.ID}, pos=({entity.X},{entity.Y},{entity.Z})");

        mBallpark.AddEntity(entity);

        Console.WriteLine($"[ballparkSvc] AddEntity: now total entities={mBallpark.Entities.Count}");
    }

    // --------------------------------------------------------------------
    // Disconnect
    // --------------------------------------------------------------------
    protected override void OnClientDisconnected()
    {
        Console.WriteLine("[ballparkSvc] Client disconnected from ballpark.");
    }

    // ======================================================================
    // BuildDestinyStateForShip - Creates a proper destiny full-state binary
    // for the player's ship. This MUST be present inside the same partial
    // class as BuildSnapshot().
    // ======================================================================
    private byte[] BuildDestinyStateForShip(int shipID)
    {
        if (!mBallpark.TryGetEntity(shipID, out ItemEntity ent))
        {
            Console.WriteLine($"[ballparkSvc] BuildDestinyStateForShip: ship entity {shipID} not found");
            return Array.Empty<byte>();
        }

        // Position fallback
        double x = ent.X ?? 0.0;
        double y = ent.Y ?? 0.0;
        double z = ent.Z ?? 0.0;

        // ------------------------------------------------------------------
        // BALL HEADER (required by Destiny)
        // ------------------------------------------------------------------
        BallHeader header = new BallHeader
        {
            ItemId   = ent.ID,
            Mode     = BallMode.Stop,
            Radius   = 1000.0,
            Location = new Vector3 { X = x, Y = y, Z = z },
            Flags    = BallFlag.IsFree | BallFlag.IsMassive | BallFlag.IsInteractive
        };

        // ------------------------------------------------------------------
        // EXTRA HEADER
        // ------------------------------------------------------------------
        ExtraBallHeader extra = new ExtraBallHeader
        {
            Mass          = 1.0,
            CloakMode     = CloakMode.None,
            Harmonic      = 0xFFFFFFFFFFFFFFFF,
            CorporationId = 0,
            AllianceId    = 0
        };

        // ------------------------------------------------------------------
        // BALL DATA
        // ------------------------------------------------------------------
        BallData data = new BallData
        {
            MaxVelocity   = 200.0,
            Velocity      = new Vector3 { X = 0, Y = 0, Z = 0 },
            UnknownVec    = default (Vector3),
            Agility       = 1.0,
            SpeedFraction = 0.0
        };

        // ------------------------------------------------------------------
        // BUILD THE BALL
        // ------------------------------------------------------------------
        Ball ball = new Ball
        {
            Header      = header,
            ExtraHeader = extra,
            Data        = data,
            FormationId = 0
        };

        // DESTINY FULL STATE = list of balls
        List <Ball> balls = new List<Ball> { ball };

        // ------------------------------------------------------------------
        // ENCODE WITH DestinyBinaryEncoder
        // ------------------------------------------------------------------
        byte[] destinyBytes = DestinyBinaryEncoder.BuildFullState(balls, packetType: 0, stamp: 0);

        Console.WriteLine($"[ballparkSvc] BuildDestinyStateForShip: built destiny blob of {destinyBytes.Length} bytes");

        return destinyBytes;
    }

    // ======================================================================
    //  Apocrypha BallparkSnapshot Constructor
    //
    //  CRITICAL FIX: The solar system must be in solItem but NOT in slims.
    //  The client's SetState iterates slims and checks if each itemID is in self.balls.
    //  The native destiny parser does NOT add the solar system to self.balls,
    //  so including it in slims causes "BallNotInPark" error.
    // ======================================================================
    private PyDataType BuildSnapshot(int solarSystemID, int shipID, Session sess)
    {
        Console.WriteLine($"[ballpark] Building Apoc Snapshot system={solarSystemID}, ship={shipID}");

        //-----------------------------------------------------------------
        // Base bag (will be wrapped as util.KeyVal)
        //-----------------------------------------------------------------
        PyDictionary bagDict = new PyDictionary();

        //-----------------------------------------------------------------
        // aggressors  (empty)
        //-----------------------------------------------------------------
        bagDict["aggressors"] = new PyDictionary();

        //-----------------------------------------------------------------
        // droneState  (empty indexed rowset)
        //-----------------------------------------------------------------
        PyObjectData droneState = new PyObjectData(
            "util.Rowset",
            new PyDictionary
            {
                ["rowClass"] = new PyString("util.KeyVal"),
                ["header"]   = new PyList { new PyString("droneID") },
                ["lines"]    = new PyList()
            }
        );
        bagDict["droneState"] = droneState;

        //-----------------------------------------------------------------
        // solItem  (wrap as util.KeyVal) - solar system info goes HERE
        //-----------------------------------------------------------------
        PyDictionary solItemDict = new PyDictionary
        {
            ["itemID"]     = new PyInteger(solarSystemID),
            ["typeID"]     = new PyInteger(5),
            ["groupID"]    = new PyInteger(4),
            ["ownerID"]    = new PyInteger(1),
            ["locationID"] = new PyInteger(0),
            ["x"]          = new PyInteger(0),
            ["y"]          = new PyInteger(0),
            ["z"]          = new PyInteger(0),
            ["categoryID"] = new PyInteger(2),
            ["name"]       = new PyString("Solar System"),

            // extra fields so michelle.Park.SetState() can safely attribute-access
            ["corpID"]     = new PyInteger(0),
            ["allianceID"] = new PyInteger(0),
            ["charID"]     = new PyInteger(0)
        };
        bagDict["solItem"] = new PyObjectData("util.KeyVal", solItemDict);

        // -----------------------------------------------------------------
        // destiny state buffer (only ship, no solar system)
        // -----------------------------------------------------------------
        byte[] destinyData = BuildDestinyStateForShip(shipID);

        if (destinyData == null || destinyData.Length == 0)
        {
            Console.WriteLine($"[ballparkSvc] WARNING: Destiny state for ship {shipID} is empty; sending zero-length buffer");
            bagDict["state"] = new PyBuffer(new byte[0]);
        }
        else
        {
            Console.WriteLine($"[ballparkSvc] Destiny state buffer size: {destinyData.Length} bytes");
            bagDict["state"] = new PyBuffer(destinyData);
        }

        //-----------------------------------------------------------------
        // ego  (the player's ship ball ID)
        //-----------------------------------------------------------------
        bagDict["ego"] = new PyInteger(shipID);

        //-----------------------------------------------------------------
        // slims – ONLY actual space entities (ship, station, etc.)
        // CRITICAL: Do NOT include solar system here! It goes in solItem.
        //-----------------------------------------------------------------
        PyList slims = new PyList();

        // ship slim (NO solar system slim!)
        if (!mBallpark.TryGetEntity(shipID, out ItemEntity playerShipEntity))
        {
            Console.WriteLine("[ballparkSvc] ERROR: playerShipEntity missing for SLIM, cannot build ship slim");
        }
        else
        {
            string shipName = playerShipEntity.Name ?? playerShipEntity.Type?.Name ?? "Player Ship";

            PyDictionary slimShipDict = new PyDictionary
            {
                ["itemID"]     = new PyInteger(shipID),
                ["typeID"]     = new PyInteger(playerShipEntity.Type.ID),
                ["groupID"]    = new PyInteger(playerShipEntity.Type.Group.ID),
                ["ownerID"]    = new PyInteger(sess.CharacterID),
                ["locationID"] = new PyInteger(solarSystemID),
                ["categoryID"] = new PyInteger(playerShipEntity.Type.Group.Category.ID),
                ["name"]       = new PyString(shipName),

                ["corpID"]     = new PyInteger(sess.CorporationID),
                ["allianceID"] = new PyInteger(0),
                ["charID"]     = new PyInteger(sess.CharacterID)
            };

            slims.Add(new PyObjectData("util.KeyVal", slimShipDict));
            Console.WriteLine("[ballparkSvc] Added SHIP slim");
        }

        // station slim (if any)
        int stationID = sess.StationID; // StationID is plain int, no null-coalesce
        if (stationID != 0 && mBallpark.TryGetEntity(stationID, out ItemEntity stationEnt))
        {
            PyDictionary slimStationDict = new PyDictionary
            {
                ["itemID"]     = new PyInteger(stationEnt.ID),
                ["typeID"]     = new PyInteger(stationEnt.Type.ID),
                ["groupID"]    = new PyInteger(stationEnt.Type.Group.ID),
                ["ownerID"]    = new PyInteger(stationEnt.OwnerID),
                ["locationID"] = new PyInteger(solarSystemID),
                ["categoryID"] = new PyInteger(stationEnt.Type.Group.Category.ID),
                ["name"]       = new PyString(stationEnt.Type.Name),

                ["corpID"]     = new PyInteger(0),
                ["allianceID"] = new PyInteger(0),
                ["charID"]     = new PyInteger(0)
            };

            slims.Add(new PyObjectData("util.KeyVal", slimStationDict));
            Console.WriteLine("[ballparkSvc] Added STATION slim");
        }
        else
        {
            Console.WriteLine("[ballparkSvc] Station slim SKIPPED (no station entity in ballpark)");
        }

        bagDict["slims"] = slims;
        Console.WriteLine($"[ballparkSvc] Total slims count: {slims.Count} (no solar system - it's in solItem)");

        //-----------------------------------------------------------------
        // damageState, effectStates, allianceBridges
        //-----------------------------------------------------------------
        bagDict["damageState"]     = new PyDictionary();
        bagDict["effectStates"]    = new PyList();
        bagDict["allianceBridges"] = new PyList();

        Console.WriteLine("[ballpark] Apoc snapshot KeyVal bag constructed.");

        //-----------------------------------------------------------------
        // PACKAGE INTO destiny event list:
        //   [(timestamp, ('SetState', (bagKeyVal,)))]
        //-----------------------------------------------------------------
        PyObjectData bagKeyVal = new PyObjectData("util.KeyVal", bagDict);

        // ('SetState', (bagKeyVal,))
        PyTuple stateCallArgs = new PyTuple(1);
        stateCallArgs[0] = bagKeyVal;

        PyTuple innerCall = new PyTuple(2);
        innerCall[0] = new PyString("SetState");
        innerCall[1] = stateCallArgs;

        // (timestamp, innerCall)
        PyTuple eventTuple = new PyTuple(2);
        eventTuple[0] = new PyInteger(0); // timestamp placeholder
        eventTuple[1] = innerCall;

        PyList events = new PyList();
        events.Add(eventTuple);

        return events;
    }
}