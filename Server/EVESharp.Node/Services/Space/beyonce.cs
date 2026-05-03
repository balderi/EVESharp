using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.Common;
using EVESharp.Database;
using EVESharp.Database.Dogma;
using EVESharp.Database.Inventory;
using EVESharp.Database.Inventory.Attributes;
using EVESharp.Database.Inventory.Categories;
using EVESharp.Database.Inventory.Groups;
using EVESharp.Database.Inventory.Types;
using EVESharp.Database.Types;
using EVESharp.Destiny;
using EVESharp.EVE.Data.Inventory;
using EVESharp.EVE.Data.Inventory.Items;
using EVESharp.EVE.Data.Inventory.Items.Types;
using EVESharp.EVE.Dogma;
using EVESharp.EVE.Network.Services;
using EVESharp.EVE.Notifications;
using EVESharp.EVE.Sessions;
using EVESharp.Node.Services.Dogma;
using EVESharp.Types;
using EVESharp.Types.Collections;
using Serilog;
using Type = EVESharp.Database.Inventory.Stations.Type;

namespace EVESharp.Node.Services.Space;

/// <summary>
/// The 'beyonce' service handles all ballpark / Destiny state for space gameplay.
///
/// CLIENT FLOW (from decompiled michelle.py / eveMoniker.py):
///   1. ship.Undock() sends session change (stationid=None, solarsystemid=X)
///   2. Client gameui.OnSessionChanged → GoInflight → michelle.AddBallpark(ssid)
///   3. AddBallpark creates native destiny.Ballpark, then:
///      a. Park.__init__() calls moniker.GetBallPark(ssid) → Moniker('beyonce', ssid)
///      b. remoteBallpark.Bind() → MachoBindObject → SERVER creates BOUND beyonce instance
///         → bound constructor sends DoDestinyUpdate (targeted to this character only)
///      c. eve.RemoteSvc('beyonce').GetFormations() → calls GLOBAL service (just returns formations)
///      d. __bp.LoadFormations(formations)
///      e. __bp.Start() → starts tick loop, processes queued DoDestinyUpdate
///   4. Park.SetState(bag) → reads destiny binary → creates balls → DoBallsAdded → 3D render
///
/// IMPORTANT: The initial SetState must be sent via "charid" (character-targeted), NOT via
/// "solarsystemid2" (system broadcast). System broadcasts would replace every client's ballpark
/// in the solar system with this player's ego/state. Incremental updates (movement commands)
/// are broadcast system-wide via DestinyBroadcaster, which is correct.
/// </summary>
[ConcreteService("beyonce")]
public class beyonce : ClientBoundService
{
    public override AccessLevel AccessLevel => AccessLevel.None;

    private IItems                    Items                 { get; }
    private ITypes                    Types                 => Items.Types;
    private IDogmaItems               DogmaItems            { get; }
    private INotificationSender       NotificationSender    { get; }
    private SolarSystemDestinyManager SolarSystemDestinyMgr { get; }
    private ISessionManager           SessionManager        { get; }
    private IDatabase                 Database              { get; }
    private ILogger                   Log                   { get; }
    private TargetManager             TargetMgr             { get; }
    private DestinyBroadcaster        Broadcaster           { get; }

    private Ballpark                                               mBallpark;
    private DestinyManager                                         mDestinyManager;
    private int                                                    mSolarSystemID;
    private int                                                    mOwnerID;
    private Dictionary<int, List<(int GateID, int SolarSystemID)>> mStargateJumps =
        new Dictionary <int, List <(int GateID, int SolarSystemID)>> ();

    // =====================================================================
    //  GLOBAL / UNBOUND CONSTRUCTOR
    //  Called once at startup. No ballpark here.
    // =====================================================================

    public beyonce(IBoundServiceManager manager, IItems items, IDogmaItems dogmaItems, INotificationSender notificationSender,
                   SolarSystemDestinyManager solarSystemDestinyMgr, ISessionManager sessionManager, IDatabase database, ILogger logger,
                   TargetManager targetManager, DestinyBroadcaster broadcaster)
        : base(manager)
    {
        Items                 = items;
        DogmaItems            = dogmaItems;
        NotificationSender    = notificationSender;
        SolarSystemDestinyMgr = solarSystemDestinyMgr;
        SessionManager        = sessionManager;
        Database              = database;
        Log                   = logger;
        TargetMgr             = targetManager;
        Broadcaster           = broadcaster;
    }

    // =====================================================================
    //  BOUND CONSTRUCTOR
    //  Called per client during Moniker.Bind() - this is where we send state.
    // =====================================================================

    internal beyonce(
        IBoundServiceManager      manager,
        Session                   session,
        int                       objectID,
        IItems                    items,
        IDogmaItems               dogmaItems,
        INotificationSender       notificationSender,
        SolarSystemDestinyManager solarSystemDestinyMgr,
        ISessionManager           sessionManager,
        IDatabase                 database,
        ILogger                   logger,
        TargetManager             targetManager,
        DestinyBroadcaster        broadcaster)
        : base(manager, session, objectID)
    {
        Items                 = items;
        DogmaItems            = dogmaItems;
        NotificationSender    = notificationSender;
        SolarSystemDestinyMgr = solarSystemDestinyMgr;
        SessionManager        = sessionManager;
        Database              = database;
        Log                   = logger;
        TargetMgr             = targetManager;
        Broadcaster           = broadcaster;
        this.mSolarSystemID        = objectID;
        this.mOwnerID              = session.CharacterID;

        int shipID    = session.ShipID   ?? 0;
        int stationID = session.StationID;

        // Session StationID is cleared (=0) after undock session change.
        // Retrieve the station ID that ship.Undock() saved before clearing it.
        if (stationID == 0)
            stationID = SolarSystemDestinyMgr.TakeUndockStation(session.CharacterID);

        Log.Information("[beyonce] BIND: solarSystem={SolarSystemID}, char={OwnerID}, ship={ShipID}, station={StationID}", mSolarSystemID, mOwnerID, shipID, stationID);

        // ----------------------------------------------------------
        // Get or create the DestinyManager for this solar system
        // ----------------------------------------------------------
        mDestinyManager = SolarSystemDestinyMgr.GetOrCreate(mSolarSystemID);

        // ----------------------------------------------------------
        // Build the ballpark with all entities the player should see
        // ----------------------------------------------------------
        mBallpark = new Ballpark(mSolarSystemID, mOwnerID);

        // Use LoadItem (cache + DB fallback) to guarantee the ship is loaded even if
        // it was evicted from the cache (e.g. by dogmaIM.OnClientDisconnected or inventory unload).
        ItemEntity shipEntity = shipID != 0 ? Items.LoadItem(shipID) : null;
        if (shipEntity != null)
        {
            Log.Information("[beyonce] Added ship {ShipID} at ({X:F0},{Y:F0},{Z:F0})", shipID, shipEntity.X, shipEntity.Y, shipEntity.Z);
            mBallpark.AddEntity(shipEntity);

            // Register as BubbleEntity in the DestinyManager
            BubbleEntity shipBubble = CreateBubbleEntity(shipEntity, session, true);
            mDestinyManager.RegisterEntity(shipBubble);
        }

        if (stationID != 0 && Items.TryGetItem(stationID, out ItemEntity stationEntity))
        {
            Log.Information("[beyonce] Added station {StationID}", stationID);
            mBallpark.AddEntity(stationEntity);

            // Register station as rigid BubbleEntity (only if not already registered)
            if (!mDestinyManager.TryGetEntity(stationID, out _))
            {
                BubbleEntity stationBubble = CreateBubbleEntity(stationEntity, session, false);
                mDestinyManager.RegisterEntity(stationBubble);
            }

            // Set undock velocity on the ship so it launches out of the dock
            if (stationEntity is Station stationItem && mDestinyManager.TryGetEntity(shipID, out BubbleEntity shipBubble))
            {
                Type    stationType = stationItem.StationType;
                double undockSpeed = shipBubble.MaxVelocity;

                shipBubble.Velocity = new Vector3
                {
                    X = stationType.DockOrientationX * undockSpeed,
                    Y = stationType.DockOrientationY * undockSpeed,
                    Z = stationType.DockOrientationZ * undockSpeed
                };
                shipBubble.SpeedFraction = 1.0;

                Log.Information("[beyonce] Set undock velocity ({VelX:F0},{VelY:F0},{VelZ:F0}) speed={Speed:F0}",
                                shipBubble.Velocity.X, shipBubble.Velocity.Y, shipBubble.Velocity.Z, undockSpeed);
            }
        }

        // ----------------------------------------------------------
        // Load all celestials (planets, moons, stargates, etc.)
        // ----------------------------------------------------------
        LoadCelestials(mSolarSystemID);

        // ----------------------------------------------------------
        // Include all dynamic entities from DestinyManager
        // (other players' ships, spawned NPCs, etc.)
        // ----------------------------------------------------------
        foreach (BubbleEntity existingEntity in mDestinyManager.GetEntities())
        {
            if (existingEntity.ItemID == shipID)
                continue;
            if (mBallpark.Entities.ContainsKey(existingEntity.ItemID))
                continue;

            ItemEntity entity = Items.LoadItem(existingEntity.ItemID);
            if (entity != null)
            {
                mBallpark.AddEntity(entity);
                Log.Information("[beyonce] Added dynamic entity {EntityID} (type={TypeID}, player={IsPlayer}) to ballpark",
                                existingEntity.ItemID, existingEntity.TypeID, existingEntity.IsPlayer);
            }
        }

        // ----------------------------------------------------------
        // SEND DoDestinyUpdate IMMEDIATELY
        // The client's Park.__init__() queues events in self.history,
        // and DoPreTick (called each tick after Start()) processes them.
        // No artificial delay needed — the queuing mechanism handles timing.
        // ----------------------------------------------------------
        SendDoDestinyUpdate(session);

        // ----------------------------------------------------------
        // Broadcast AddBalls to existing players so they see us
        // ----------------------------------------------------------
        if (shipEntity != null && mDestinyManager.TryGetEntity(shipID, out BubbleEntity selfBubble))
        {
            // Use the same stamp computation as BuildSnapshot for consistency
            int addBallStamp = GetStamp();

            Log.Information("[beyonce] AddBalls: entity itemID={ItemID}, mode={Mode}, flags={Flags}, pos=({X:F0},{Y:F0},{Z:F0}), vel=({VX:F0},{VY:F0},{VZ:F0}), speed={Speed}",
                            selfBubble.ItemID, selfBubble.Mode, selfBubble.Flags,
                            selfBubble.Position.X, selfBubble.Position.Y, selfBubble.Position.Z,
                            selfBubble.Velocity.X, selfBubble.Velocity.Y, selfBubble.Velocity.Z,
                            selfBubble.SpeedFraction);

            PyList addBallEvents = DestinyEventBuilder.BuildAddBalls(
                new[] { selfBubble }, mSolarSystemID, addBallStamp);

            // Add modules to the slim for 3D hardpoint rendering
            AddModulesToAddBallSlims(addBallEvents, shipID);

            PyTuple notification = DestinyEventBuilder.WrapAsNotification(addBallEvents);

            NotificationSender.SendNotification(
                "DoDestinyUpdate",
                "solarsystemid",
                mSolarSystemID,
                notification
            );

            Log.Information("[beyonce] Broadcast AddBalls for ship {ShipID} to system {SystemID} stamp={Stamp}", shipID, mSolarSystemID, addBallStamp);
        }
    }

    // =====================================================================
    //  MACHO BINDING
    // =====================================================================

    protected override long MachoResolveObject(ServiceCall call, ServiceBindParams bindParams)
    {
        return BoundServiceManager.MachoNet.NodeID;
    }

    protected override BoundService CreateBoundInstance(ServiceCall call, ServiceBindParams bindParams)
    {
        Log.Information("[beyonce] CreateBoundInstance: objectID={ObjectID}, char={CharID}", bindParams.ObjectID, call.Session.CharacterID);
        return new beyonce(BoundServiceManager, call.Session, bindParams.ObjectID,
                           Items, DogmaItems, NotificationSender, SolarSystemDestinyMgr, SessionManager, Database, Log,
                           TargetMgr, Broadcaster);
    }

    // =====================================================================
    //  CLIENT API
    // =====================================================================

    /// <summary>
    /// GetJumpQueueStatus - Called by the client to check if there's a queue to enter the system.
    /// Returns None to indicate no queue.
    /// </summary>
    public PyDataType GetJumpQueueStatus(ServiceCall call)
    {
        return new PyNone();
    }

    /// <summary>
    /// GetFormations - Called by the client on the GLOBAL service via eve.RemoteSvc('beyonce').
    /// Returns ship formation data. In Apocrypha, formations are unused - return empty tuple.
    /// </summary>
    public PyDataType GetFormations(ServiceCall call)
    {
        Log.Information("[beyonce] GetFormations() called (global service, char={CharID})", call.Session.CharacterID);
        return new PyTuple(0);
    }

    /// <summary>
    /// UpdateStateRequest - Called by the BOUND moniker (remoteBallpark) during
    /// desync recovery (Park.RequestReset). Sends a fresh DoDestinyUpdate.
    /// </summary>
    public PyDataType UpdateStateRequest(ServiceCall call)
    {
        Log.Information("[beyonce] UpdateStateRequest() called, char={CharID}", call.Session.CharacterID);

        EnsureBallpark(call.Session);
        SendDoDestinyUpdate(call.Session);

        return new PyNone();
    }

    /// <summary>
    /// GetInitialState - Alternative method name some client builds use.
    /// </summary>
    public PyDataType GetInitialState(ServiceCall call)
    {
        Log.Information("[beyonce] GetInitialState() -> delegating to UpdateStateRequest");
        return UpdateStateRequest(call);
    }

    // =====================================================================
    //  MOVEMENT COMMANDS
    // =====================================================================

    public PyDataType Stop(ServiceCall call)
    {
        int shipID = call.Session.ShipID ?? 0;
        Log.Information("[beyonce] Stop: ship={ShipID}", shipID);
        mDestinyManager?.CmdStop(shipID);
        return new PyNone();
    }

    /// <summary>
    /// TeardownBallpark - Called when the ballpark is being torn down (docking, jumping, etc).
    /// </summary>
    public PyDataType TeardownBallpark(ServiceCall call)
    {
        int shipID = call.Session.ShipID ?? 0;
        Log.Information("[beyonce] TeardownBallpark: ship={ShipID}", shipID);
        mDestinyManager?.UnregisterEntity(shipID);
        BroadcastRemoveBalls(shipID);
        mBallpark = null;
        return new PyNone();
    }

    public PyDataType FollowBall(ServiceCall call, PyInteger ballID, PyInteger range)
    {
        int shipID = call.Session.ShipID ?? 0;
        Log.Information("[beyonce] FollowBall: ship={ShipID}, target={Target}, range={Range}", shipID, ballID?.Value, range?.Value);
        mDestinyManager?.CmdFollowBall(shipID, (int)(ballID?.Value ?? 0), (float)(range?.Value ?? 1000));
        return new PyNone();
    }

    public PyDataType Orbit(ServiceCall call, PyInteger entityID, PyInteger range)
    {
        int shipID = call.Session.ShipID ?? 0;
        Log.Information("[beyonce] Orbit: ship={ShipID}, target={Target}, range={Range}", shipID, entityID?.Value, range?.Value);
        mDestinyManager?.CmdOrbit(shipID, (int)(entityID?.Value ?? 0), (float)(range?.Value ?? 5000));
        return new PyNone();
    }

    public PyDataType AlignTo(ServiceCall call, PyInteger entityID)
    {
        int shipID = call.Session.ShipID ?? 0;
        Log.Information("[beyonce] AlignTo: ship={ShipID}, target={Target}", shipID, entityID?.Value);
        mDestinyManager?.CmdAlignTo(shipID, (int)(entityID?.Value ?? 0));
        return new PyNone();
    }

    public PyDataType GotoDirection(ServiceCall call, PyDecimal x, PyDecimal y, PyDecimal z)
    {
        int shipID = call.Session.ShipID ?? 0;
        Log.Information("[beyonce] GotoDirection: ship={ShipID}, dir=({X},{Y},{Z})", shipID, x?.Value, y?.Value, z?.Value);

        // GotoDirection sends a direction vector from the client.
        // We translate to a distant goto point in that direction.
        double dx = x?.Value ?? 0;
        double dy = y?.Value ?? 0;
        double dz = z?.Value ?? 0;

        if (mDestinyManager != null && mDestinyManager.TryGetEntity(shipID, out BubbleEntity ent))
        {
            Vector3    dir     = new Vector3 { X = dx, Y = dy, Z = dz }.Normalize();
            double farDist = 1e12; // 1 billion km - effectively infinite
            double gx      = ent.Position.X + dir.X * farDist;
            double gy      = ent.Position.Y + dir.Y * farDist;
            double gz      = ent.Position.Z + dir.Z * farDist;
            mDestinyManager.CmdGotoPoint(shipID, gx, gy, gz);
        }

        return new PyNone();
    }

    /// <summary>
    /// GetRelativity - Called periodically by the client to verify its position matches the server.
    /// Returns (diff, x, y, z, sTime, tTime, rTime) where diff = distance delta from predicted position.
    /// </summary>
    public PyDataType GetRelativity(ServiceCall call,     PyDecimal preX,     PyDecimal preY, PyDecimal preZ,
                                    PyDecimal   presTime, PyDecimal pretTime, PyDecimal prerTime)
    {
        int    shipID = call.Session.ShipID ?? 0;
        double x      = 0, y = 0, z = 0;

        if (mDestinyManager?.TryGetEntity(shipID, out BubbleEntity bubble) == true)
        {
            x = bubble.Position.X;
            y = bubble.Position.Y;
            z = bubble.Position.Z;
        }

        double dx   = x - (double)(preX?.Value ?? 0);
        double dy   = y - (double)(preY?.Value ?? 0);
        double dz   = z - (double)(preZ?.Value ?? 0);
        double diff = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        long   now  = DateTime.UtcNow.ToFileTimeUtc();

        return new PyTuple(7)
        {
            [0] = new PyDecimal(diff),
            [1] = new PyDecimal(x),
            [2] = new PyDecimal(y),
            [3] = new PyDecimal(z),
            [4] = new PyInteger(now),
            [5] = new PyInteger(now),
            [6] = new PyInteger(now)
        };
    }

    public PyDataType SetSpeedFraction(ServiceCall call, PyDecimal fraction)
    {
        int shipID = call.Session.ShipID ?? 0;
        Log.Information("[beyonce] SetSpeedFraction: ship={ShipID}, fraction={Fraction}", shipID, fraction?.Value);
        mDestinyManager?.CmdSetSpeedFraction(shipID, (float)(fraction?.Value ?? 0));
        return new PyNone();
    }

    public PyDataType WarpToStuff(ServiceCall call, PyString type, PyInteger itemID)
    {
        int shipID = call.Session.ShipID ?? 0;
        Log.Information("[beyonce] WarpToStuff: ship={ShipID}, type={WarpType}, item={ItemID}", shipID, type?.Value, itemID?.Value);

        int targetID = (int)(itemID?.Value ?? 0);
        if (targetID != 0 && Items.TryGetItem(targetID, out ItemEntity target))
        {
            double tx = target.X ?? 0;
            double ty = target.Y ?? 0;
            double tz = target.Z ?? 0;
            mDestinyManager?.CmdWarpTo(shipID, tx, ty, tz);
        }

        return new PyNone();
    }

    public PyDataType WarpToStuffAutopilot(ServiceCall call, PyInteger itemID)
    {
        int shipID = call.Session.ShipID ?? 0;
        Log.Information("[beyonce] WarpToStuffAutopilot: ship={ShipID}, item={ItemID}", shipID, itemID?.Value);

        int targetID = (int)(itemID?.Value ?? 0);
        if (targetID != 0 && Items.TryGetItem(targetID, out ItemEntity target))
        {
            double tx = target.X ?? 0;
            double ty = target.Y ?? 0;
            double tz = target.Z ?? 0;
            mDestinyManager?.CmdWarpTo(shipID, tx, ty, tz);
        }

        return new PyNone();
    }

    public PyDataType Dock(ServiceCall call, PyInteger stationID)
    {
        int charID        = call.Session.CharacterID;
        int shipID        = call.Session.ShipID ?? 0;
        int targetStation = (int)(stationID?.Value ?? 0);

        Log.Information("[beyonce] Dock: char={CharID}, ship={ShipID}, station={StationID}", charID, shipID, targetStation);

        // Unlock all targets when docking
        TargetMgr?.UnlockAll(charID);

        if (targetStation == 0)
            return new PyNone();

        Station station = Items.GetStaticStation(targetStation);
        if (station == null)
        {
            Log.Warning("[beyonce] Dock: station {StationID} not found", targetStation);
            return new PyNone();
        }

        // Unregister ship from DestinyManager and notify others
        mDestinyManager?.UnregisterEntity(shipID);
        BroadcastRemoveBalls(shipID);

        // Move ship to the new station in DB (same pattern as /move GM command)
        // Use LoadItem to guarantee the ship is loaded even if evicted from cache.
        ItemEntity shipEntity = shipID != 0 ? Items.LoadItem(shipID) : null;
        if (shipEntity != null)
        {
            shipEntity.LocationID = targetStation;
            shipEntity.Flag       = Flags.Hangar;
            shipEntity.Persist();
            Log.Information("[beyonce] Dock: Moved ship {ShipID} to station {StationID} in DB", shipID, targetStation);
        }

        // Update character location in chrInformation for login persistence
        Database.Prepare(
            "UPDATE chrInformation " +
            "SET stationID = @stationID, solarSystemID = @solarSystemID, " +
            "    constellationID = @constellationID, regionID = @regionID " +
            "WHERE characterID = @characterID",
            new Dictionary<string, object>
            {
                {"@characterID", charID},
                {"@stationID", targetStation},
                {"@solarSystemID", station.SolarSystemID},
                {"@constellationID", station.ConstellationID},
                {"@regionID", station.RegionID}
            }
        );
        Log.Information("[beyonce] Dock: Updated chrInformation for char {CharID}", charID);

        // Send OnDockingAccepted BEFORE session change so client still has a ballpark to animate in
        double shipX = 0, shipY = 0, shipZ = 0;
        if (mDestinyManager?.TryGetEntity(shipID, out BubbleEntity dockShipBubble) == true)
        {
            shipX = dockShipBubble.Position.X;
            shipY = dockShipBubble.Position.Y;
            shipZ = dockShipBubble.Position.Z;
        }
        Broadcaster?.BroadcastOnDockingAccepted(charID,
                                                shipX, shipY, shipZ,
                                                station.X ?? 0, station.Y ?? 0, station.Z ?? 0,
                                                targetStation);

        // Session change: enter station (reverse of ship.Undock)
        Session delta = new Session();
        delta[Session.STATION_ID]       = (PyInteger)targetStation;
        delta[Session.LOCATION_ID]      = (PyInteger)targetStation;
        delta[Session.SOLAR_SYSTEM_ID]  = new PyNone();
        delta[Session.SOLAR_SYSTEM_ID2] = (PyInteger)station.SolarSystemID;
        delta[Session.CONSTELLATION_ID] = (PyInteger)station.ConstellationID;
        delta[Session.REGION_ID]        = (PyInteger)station.RegionID;

        Log.Information("[beyonce] Dock: performing session update for char {CharID} -> station {StationID}", charID, targetStation);
        SessionManager.PerformSessionUpdate(Session.CHAR_ID, charID, delta);
        Log.Information("[beyonce] Dock: session update completed");

        return new PyNone();
    }

    public PyDataType StargateJump(ServiceCall call, PyInteger fromID, PyInteger toID)
    {
        int charID     = call.Session.CharacterID;
        int shipID     = call.Session.ShipID ?? 0;
        int destGateID = (int)(toID?.Value ?? 0);

        Log.Information("[beyonce] StargateJump: char={CharID}, ship={ShipID}, from={FromID}, to={ToID}", charID, shipID, fromID?.Value, destGateID);

        // Unlock all targets when jumping
        TargetMgr?.UnlockAll(charID);

        if (destGateID == 0)
            return new PyNone();

        // Look up destination gate's solar system, position, constellation, and region
        StargateDestination? destInfo = GetStargateDestinationInfo(destGateID);
        if (destInfo == null)
        {
            Log.Warning("[beyonce] StargateJump: could not find destination info for gate {GateID}", destGateID);
            return new PyNone();
        }

        Log.Information("[beyonce] StargateJump: destination system={SolarSystemID}, constellation={ConstellationID}, region={RegionID}, pos=({X:F0},{Y:F0},{Z:F0})",
                        destInfo.Value.SolarSystemID, destInfo.Value.ConstellationID, destInfo.Value.RegionID,
                        destInfo.Value.X, destInfo.Value.Y, destInfo.Value.Z);

        // Unregister ship from current DestinyManager and notify others
        mDestinyManager?.UnregisterEntity(shipID);
        BroadcastRemoveBalls(shipID);

        // Update ship position to near the destination gate and persist
        // Use LoadItem to guarantee the ship is loaded even if evicted from cache.
        ItemEntity shipEntity = shipID != 0 ? Items.LoadItem(shipID) : null;
        if (shipEntity != null)
        {
            // Place ship 15km from the gate (offset along X to avoid overlap)
            shipEntity.X          = destInfo.Value.X + 15000;
            shipEntity.Y          = destInfo.Value.Y;
            shipEntity.Z          = destInfo.Value.Z;
            shipEntity.LocationID = destInfo.Value.SolarSystemID;
            shipEntity.Persist();
            Log.Information("[beyonce] StargateJump: Ship {ShipID} persisted at ({X:F0},{Y:F0},{Z:F0}), locationID={LocationID}",
                            shipID, shipEntity.X, shipEntity.Y, shipEntity.Z, destInfo.Value.SolarSystemID);
        }

        // Update chrInformation for login persistence (in space, not docked)
        Database.Prepare(
            "UPDATE chrInformation " +
            "SET stationID = @stationID, solarSystemID = @solarSystemID, " +
            "    constellationID = @constellationID, regionID = @regionID " +
            "WHERE characterID = @characterID",
            new Dictionary<string, object>
            {
                {"@characterID", charID},
                {"@stationID", 0},
                {"@solarSystemID", destInfo.Value.SolarSystemID},
                {"@constellationID", destInfo.Value.ConstellationID},
                {"@regionID", destInfo.Value.RegionID}
            }
        );
        Log.Information("[beyonce] StargateJump: Updated chrInformation for char {CharID} -> system {SystemID}", charID, destInfo.Value.SolarSystemID);

        // Session change: transition to new solar system
        Session delta = new Session();
        delta[Session.STATION_ID]       = new PyNone();
        delta[Session.LOCATION_ID]      = (PyInteger)destInfo.Value.SolarSystemID;
        delta[Session.SOLAR_SYSTEM_ID]  = (PyInteger)destInfo.Value.SolarSystemID;
        delta[Session.SOLAR_SYSTEM_ID2] = (PyInteger)destInfo.Value.SolarSystemID;
        delta[Session.CONSTELLATION_ID] = (PyInteger)destInfo.Value.ConstellationID;
        delta[Session.REGION_ID]        = (PyInteger)destInfo.Value.RegionID;

        Log.Information("[beyonce] StargateJump: performing session update for char {CharID} -> system {SystemID}", charID, destInfo.Value.SolarSystemID);
        SessionManager.PerformSessionUpdate(Session.CHAR_ID, charID, delta);
        Log.Information("[beyonce] StargateJump: session update completed");

        return new PyNone();
    }

    private struct StargateDestination
    {
        public int    SolarSystemID;
        public double X, Y, Z;
        public int    ConstellationID;
        public int    RegionID;
    }

    private StargateDestination? GetStargateDestinationInfo(int gateID)
    {
        try
        {
            DbDataReader reader = Database.Select(
                "SELECT md.solarSystemID, md.x, md.y, md.z, ms.constellationID, ms.regionID " +
                "FROM mapDenormalize md " +
                "JOIN mapSolarSystems ms ON ms.solarSystemID = md.solarSystemID " +
                "WHERE md.itemID = @itemID",
                new Dictionary<string, object> { { "@itemID", gateID } }
            );

            using (reader)
            {
                if (reader.Read())
                {
                    return new StargateDestination
                    {
                        SolarSystemID   = reader.GetInt32(0),
                        X               = reader.GetDouble(1),
                        Y               = reader.GetDouble(2),
                        Z               = reader.GetDouble(3),
                        ConstellationID = reader.GetInt32(4),
                        RegionID        = reader.GetInt32(5)
                    };
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[beyonce] Failed to get destination info for gate {GateID}: {Message}", gateID, ex.Message);
        }

        return null;
    }

    // =====================================================================
    //  SHIP DESTRUCTION / CAPSULE / POD KILL
    // =====================================================================

    /// <summary>
    /// CmdSelfDestruct - Called by the client's right-click "Self Destruct" command.
    /// </summary>
    public PyDataType CmdSelfDestruct(ServiceCall call, PyInteger shipID)
    {
        int charID        = call.Session.CharacterID;
        int currentShipID = call.Session.ShipID ?? 0;

        Log.Information("[beyonce] CmdSelfDestruct: char={CharID}, shipID={ShipID}", charID, shipID?.Value);

        if (currentShipID == 0 || currentShipID != (int)(shipID?.Value ?? 0))
        {
            Log.Warning("[beyonce] CmdSelfDestruct: ship mismatch current={Current}, requested={Requested}", currentShipID, shipID?.Value);
            return new PyNone();
        }

        DestroyPlayerShip(call);
        return new PyNone();
    }

    /// <summary>
    /// Core ship destruction logic. Destroys the current ship, then either spawns
    /// a capsule at the same position (ship death) or respawns at clone station (pod kill).
    /// </summary>
    internal void DestroyPlayerShip(ServiceCall call)
    {
        int charID = call.Session.CharacterID;
        int shipID = call.Session.ShipID ?? 0;

        if (shipID == 0 || mDestinyManager == null)
            return;

        // Get ship position before destroying it
        double posX = 0, posY = 0, posZ = 0;
        if (mDestinyManager.TryGetEntity(shipID, out BubbleEntity shipBubble))
        {
            posX = shipBubble.Position.X;
            posY = shipBubble.Position.Y;
            posZ = shipBubble.Position.Z;
        }

        // Check if ship is a capsule (pod kill) before destroying
        ItemEntity shipEntity = Items.LoadItem(shipID);
        bool       isCapsule  = shipEntity != null && shipEntity.Type.ID == (int)TypeID.Capsule;

        // Send TerminalExplosion so client shows explosion FX before the ball is removed
        Broadcaster?.BroadcastTerminalExplosion(mSolarSystemID, shipID);

        // Unregister ship from DestinyManager
        mDestinyManager.UnregisterEntity(shipID);

        // Broadcast RemoveBalls to all players in system
        BroadcastRemoveBalls(shipID);

        // Remove from ballpark
        mBallpark?.RemoveEntity(shipID);

        // Destroy the ship item
        if (shipEntity != null)
            DogmaItems.DestroyItem(shipEntity);

        Log.Information("[beyonce] DestroyPlayerShip: destroyed ship {ShipID} (isCapsule={IsCapsule}) at ({X:F0},{Y:F0},{Z:F0})",
                        shipID, isCapsule, posX, posY, posZ);

        if (isCapsule)
            HandlePodKill(call, charID);
        else
            HandleShipDeath(call, charID, posX, posY, posZ);
    }

    /// <summary>
    /// Ship destroyed in space -> spawn capsule at the same position.
    /// </summary>
    private void HandleShipDeath(ServiceCall call, int charID, double posX, double posY, double posZ)
    {
        Log.Information("[beyonce] HandleShipDeath: char={CharID}, spawning capsule at ({X:F0},{Y:F0},{Z:F0})", charID, posX, posY, posZ);

        Character character = Items.LoadItem<Character>(charID);

        // Create capsule in the solar system
        ItemInventory capsule = DogmaItems.CreateItem<ItemInventory>(
            character.Name + "'s Capsule", Types[TypeID.Capsule], charID, mSolarSystemID, Flags.None, 1, true
        );

        // Set capsule position to where the ship was destroyed
        capsule.X = posX;
        capsule.Y = posY;
        capsule.Z = posZ;
        capsule.Persist();

        // Move character into the capsule
        DogmaItems.MoveItem(character, capsule.ID, Flags.Pilot);

        // Update session to the new capsule ship
        Session delta = new Session { ShipID = capsule.ID };
        SessionManager.PerformSessionUpdate(Session.CHAR_ID, charID, delta);

        // Register capsule in DestinyManager
        BubbleEntity capsuleBubble = new BubbleEntity
        {
            ItemID        = capsule.ID,
            TypeID        = (int)TypeID.Capsule,
            GroupID       = (int)GroupID.Capsule,
            CategoryID    = (int)CategoryID.Ship,
            Name          = capsule.Name ?? character.Name + "'s Capsule",
            OwnerID       = charID,
            CorporationID = call.Session.CorporationID,
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

        mDestinyManager.RegisterEntity(capsuleBubble);

        // Add capsule to ballpark
        mBallpark?.AddEntity(capsule);

        // Broadcast AddBalls for the capsule so other players see it
        int stamp         = GetStamp();
        PyList addBallEvents = DestinyEventBuilder.BuildAddBalls(new[] { capsuleBubble }, mSolarSystemID, stamp);
        PyTuple notification  = DestinyEventBuilder.WrapAsNotification(addBallEvents);

        NotificationSender.SendNotification(
            "DoDestinyUpdate",
            "solarsystemid",
            mSolarSystemID,
            notification
        );

        // Send fresh DoDestinyUpdate to the player with new capsule as ego
        // Build a fresh session with updated ShipID since the session change already happened
        Session freshSession = Session.FromPyDictionary(call.Session);
        freshSession.ShipID = capsule.ID;
        SendDoDestinyUpdate(freshSession);

        Log.Information("[beyonce] HandleShipDeath: capsule {CapsuleID} spawned for char {CharID}", capsule.ID, charID);
    }

    /// <summary>
    /// Pod killed -> respawn at clone station.
    /// </summary>
    private void HandlePodKill(ServiceCall call, int charID)
    {
        Log.Information("[beyonce] HandlePodKill: char={CharID}, respawning at clone station", charID);

        Character character = Items.LoadItem<Character>(charID);

        // Find clone station: look up active clone's location, fallback to character's home station
        int cloneStationID = character.StationID; // fallback

        if (character.ActiveCloneID != null && character.ActiveCloneID.Value != 0)
        {
            ItemEntity cloneItem = Items.LoadItem(character.ActiveCloneID.Value);
            if (cloneItem != null && cloneItem.LocationID != 0)
                cloneStationID = cloneItem.LocationID;
        }

        Log.Information("[beyonce] HandlePodKill: clone station={StationID}", cloneStationID);

        // Look up station details
        Station station = Items.GetStaticStation(cloneStationID);
        if (station == null)
        {
            Log.Error("[beyonce] HandlePodKill: station {StationID} not found, cannot respawn", cloneStationID);
            return;
        }

        // Create a new capsule at the clone station
        ItemInventory capsule = DogmaItems.CreateItem<ItemInventory>(
            character.Name + "'s Capsule", Types[TypeID.Capsule], charID, cloneStationID, Flags.Hangar, 1, true
        );

        // Move character into the capsule
        DogmaItems.MoveItem(character, capsule.ID, Flags.Pilot);

        // Update chrInformation to clone station
        Database.Prepare(
            "UPDATE chrInformation " +
            "SET stationID = @stationID, solarSystemID = @solarSystemID, " +
            "    constellationID = @constellationID, regionID = @regionID " +
            "WHERE characterID = @characterID",
            new Dictionary<string, object>
            {
                {"@characterID", charID},
                {"@stationID", cloneStationID},
                {"@solarSystemID", station.SolarSystemID},
                {"@constellationID", station.ConstellationID},
                {"@regionID", station.RegionID}
            }
        );

        // Session change: dock at clone station with new capsule
        Session delta = new Session();
        delta[Session.SHIP_ID]          = (PyInteger)capsule.ID;
        delta[Session.STATION_ID]       = (PyInteger)cloneStationID;
        delta[Session.LOCATION_ID]      = (PyInteger)cloneStationID;
        delta[Session.SOLAR_SYSTEM_ID]  = new PyNone();
        delta[Session.SOLAR_SYSTEM_ID2] = (PyInteger)station.SolarSystemID;
        delta[Session.CONSTELLATION_ID] = (PyInteger)station.ConstellationID;
        delta[Session.REGION_ID]        = (PyInteger)station.RegionID;

        SessionManager.PerformSessionUpdate(Session.CHAR_ID, charID, delta);

        // Clear local ballpark since player is no longer in space
        mBallpark = null;

        Log.Information("[beyonce] HandlePodKill: char {CharID} respawned at station {StationID} in capsule {CapsuleID}",
                        charID, cloneStationID, capsule.ID);
    }

    // =====================================================================
    //  DoDestinyUpdate NOTIFICATION
    // =====================================================================

    private void SendDoDestinyUpdate(Session session)
    {
        int charID = session.CharacterID;
        int shipID = session.ShipID ?? 0;

        Log.Information("[beyonce] SendDoDestinyUpdate: char={CharID}, ship={ShipID}, system={SolarSystemID}", charID, shipID, mSolarSystemID);

        if (mBallpark == null || mBallpark.Entities.Count == 0)
        {
            Log.Warning("[beyonce] No entities in ballpark, cannot send state");
            return;
        }

        try
        {
            // Build the state event list: [(timestamp, ('SetState', (bagKeyVal,)))]
            PyDataType stateEvents = BuildSnapshot(mSolarSystemID, shipID, session);

            // Wrap as DoDestinyUpdate args: (state_list, waitForBubble, dogmaMessages)
            PyTuple notificationData = new PyTuple(3)
            {
                [0] = stateEvents,
                [1] = new PyBool(false),  // waitForBubble
                [2] = new PyList()        // dogmaMessages
            };

            // Send initial SetState ONLY to the specific character, NOT the whole system.
            // Broadcasting SetState to solarsystemid2 would replace every client's ballpark
            // in the system with this player's ego/state, causing camera shifts and ship disappearance.
            // Incremental updates (FollowBall, Stop, etc.) from DestinyManager still broadcast
            // to the system correctly via DestinyBroadcaster.
            NotificationSender.SendNotification(
                "DoDestinyUpdate",
                "charid",
                charID,
                notificationData
            );
            Log.Information("[beyonce] DoDestinyUpdate sent via charid to character {CharID} in system {SystemID}", charID, mSolarSystemID);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[beyonce] ERROR sending DoDestinyUpdate: {Message}", ex.Message);
        }
    }

    // =====================================================================
    //  SNAPSHOT BUILDER
    // =====================================================================

    private PyDataType BuildSnapshot(int solarSystemID, int shipID, Session sess)
    {
        Log.Information("[beyonce] BuildSnapshot: system={SolarSystemID}, ship={ShipID}", solarSystemID, shipID);

        // Compute stamp once and share between destiny binary and event tuple.
        // CRITICAL: stamp must be > 0, otherwise the client's FlushState() silently
        // drops the SetState event (entryTime > newestOldStateTime fails when both are 0).
        long eveEpoch = new DateTime(2003, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;
        int  stamp    = (int)((DateTime.UtcNow.Ticks - eveEpoch) / 10000000 % int.MaxValue);

        byte[] destinyData = BuildDestinyBinary(solarSystemID, shipID, sess, stamp);
        Log.Information("[beyonce] Destiny binary: {ByteCount} bytes", destinyData.Length);

        PyDictionary bagDict = new PyDictionary();

        bagDict["aggressors"] = new PyDictionary();
        bagDict["droneState"] = BuildEmptyDroneState();
        bagDict["solItem"]    = BuildSolItem(solarSystemID);
        bagDict["state"]      = new PyBuffer(destinyData);
        bagDict["ego"]        = new PyInteger(shipID);

        PyList slims            = new PyList();
        PyList effectStatesList = new PyList();

        // Build slim items for all entities in the ballpark
        foreach (KeyValuePair <int, ItemEntity> kvpSlim in mBallpark.Entities)
        {
            ItemEntity  ent    = kvpSlim.Value;
            bool isShip = (ent.ID == shipID);

            // Look up BubbleEntity for ownership/state data
            BubbleEntity bubbleSlim = null;
            bool hasBubbleSlim = !isShip && mDestinyManager != null
                                         && mDestinyManager.TryGetEntity(ent.ID, out bubbleSlim);

            PyObjectData slim = BuildSlimItem(
                ent.ID,
                ent.Type.ID,
                ent.Type.Group.ID,
                ent.Type.Group.Category.ID,
                ent.Name ?? ent.Type?.Name ?? "Unknown",
                isShip ? sess.CharacterID : (hasBubbleSlim ? bubbleSlim.OwnerID : ent.OwnerID),
                solarSystemID,
                isShip ? sess.CorporationID : (hasBubbleSlim ? bubbleSlim.CorporationID : 0),
                hasBubbleSlim ? bubbleSlim.AllianceID : 0,
                isShip ? sess.CharacterID : (hasBubbleSlim ? bubbleSlim.CharacterID : 0)
            );

            bool isPlayerShip = isShip || (hasBubbleSlim && bubbleSlim.IsPlayer);
            Log.Information("[beyonce] BuildSnapshot slim: id={EntityID}, isShip={IsShip}, hasBubbleSlim={HasBubble}, bubbleIsPlayer={BubbleIsPlayer}, isPlayerShip={IsPlayerShip}",
                            ent.ID, isShip, hasBubbleSlim, hasBubbleSlim ? bubbleSlim.IsPlayer : false, isPlayerShip);

            // Player ships need a 'modules' field or the client crashes in FitHardpoints2
            if (isPlayerShip)
            {
                PyDictionary slimDict    = (PyDictionary)slim.Arguments;
                PyList modulesList = new PyList();

                // Load the ship to get its fitted modules for 3D hardpoint rendering
                // Only include actual ShipModule instances whose type is a real Module category.
                // Items with non-module typeIDs (e.g. ore typeID=31) crash FitHardpoints2
                // with KeyError on cfg.invtypes.Get() because the client cache only has published types.
                try
                {
                    Ship ship = Items.LoadItem<Ship>(ent.ID);
                    if (ship == null)
                    {
                        Log.Warning("[beyonce] BuildSnapshot: LoadItem<Ship>({ShipID}) returned NULL", ent.ID);
                    }
                    else if (ship.Items == null || ship.Items.Count == 0)
                    {
                        Log.Warning("[beyonce] BuildSnapshot: Ship {ShipID} has EMPTY Items collection (ContentsLoaded={ContentsLoaded})",
                                    ent.ID, ship.ContentsLoaded);
                    }
                    else
                    {
                        Log.Information("[beyonce] BuildSnapshot: Ship {ShipID} has {TotalItems} total items, scanning for modules...",
                                        ent.ID, ship.Items.Count);

                        foreach ((int _, ItemEntity module) in ship.Items)
                        {
                            bool inModuleSlot     = module.IsInModuleSlot();
                            bool inRigSlot        = module.IsInRigSlot();
                            bool isShipModule     = module is ShipModule;
                            bool isModuleCategory = module.Type?.Group?.Category?.ID == (int) CategoryID.Module;

                            if ((inModuleSlot || inRigSlot) && isShipModule && isModuleCategory)
                            {
                                // Client FitHardpoints2 unpacks as (flag, typeID) and calls
                                // cfg.invtypes.Get(typeID) on index [1]
                                PyTuple modEntry = new PyTuple(2)
                                {
                                    [0] = new PyInteger((int)module.Flag),
                                    [1] = new PyInteger(module.Type.ID)
                                };
                                modulesList.Add(modEntry);
                                Log.Information("[beyonce]   Module ADDED: typeID={TypeID}, flag={Flag}, name={Name}",
                                                module.Type.ID, module.Flag, module.Type?.Name ?? "?");

                                // NOTE: Passive/online effects are internal stat modifiers and do NOT
                                // produce visual effects. They must NOT be added to effectStates because
                                // the client dispatches each entry as OnSpecialFX arguments, which expects
                                // a completely different format (shipID, moduleID, moduleTypeID, targetID,
                                // otherTypeID, area, guid_string, ...). Hardpoint 3D rendering is handled
                                // by the modules list (typeID + flag tuples) above.
                            }
                            else if (inModuleSlot || inRigSlot)
                            {
                                Log.Information("[beyonce]   Module SKIPPED: typeID={TypeID}, flag={Flag}, isShipModule={IsShipModule}, isModuleCategory={IsModCat}, catID={CatID}",
                                                module.Type?.ID ?? 0, module.Flag, isShipModule, isModuleCategory, module.Type?.Group?.Category?.ID ?? -1);
                            }
                        }

                        Log.Information("[beyonce] Ship {ShipID} has {ModuleCount} fitted modules for rendering", ent.ID, modulesList.Count);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "[beyonce] Failed to load modules for ship {ShipID}: {Message}", ent.ID, ex.Message);
                }

                slimDict["modules"] = modulesList;
            }

            // Stargates need destination gate IDs in 'jumps' for the right-click menu
            int groupID = ent.Type?.Group?.ID ?? 0;
            if (groupID == (int)GroupID.Stargate && mStargateJumps.TryGetValue(ent.ID, out List <(int GateID, int SolarSystemID)> jumpDests))
            {
                PyDictionary slimDict  = (PyDictionary)slim.Arguments;
                PyList jumpsList = new PyList();
                foreach ((int GateID, int SolarSystemID) jump in jumpDests)
                    jumpsList.Add(new PyObjectData("util.KeyVal", new PyDictionary
                    {
                        ["locationID"]    = new PyInteger(jump.SolarSystemID),
                        ["toCelestialID"] = new PyInteger(jump.GateID)
                    }));
                slimDict["jumps"] = jumpsList;
            }

            slims.Add(slim);
            Log.Information("[beyonce] Added slim: id={EntityID}, name={EntityName}, type={TypeName}, isShip={IsShip}", ent.ID, ent.Name ?? "(null)", ent.Type?.Name, isShip);
        }

        bagDict["slims"] = slims;

        bagDict["damageState"]     = BuildDamageStateAll(shipID, stamp);
        bagDict["effectStates"]    = effectStatesList;
        bagDict["allianceBridges"] = new PyList();

        Log.Information("[beyonce] Snapshot: {SlimCount} slims, effectStates: {EffectCount} entries", slims.Count, effectStatesList.Count);

        PyObjectData bagKeyVal     = new PyObjectData("util.KeyVal", bagDict);
        PyTuple stateCallArgs = new PyTuple(1) { [0] = bagKeyVal };
        PyTuple innerCall     = new PyTuple(2) { [0] = new PyString("SetState"), [1] = stateCallArgs };
        PyTuple eventTuple    = new PyTuple(2) { [0] = new PyInteger(stamp), [1]     = innerCall };
        PyList events        = new PyList();
        events.Add(eventTuple);

        return events;
    }

    // =====================================================================
    //  DESTINY BINARY ENCODER
    // =====================================================================

    private byte[] BuildDestinyBinary(int solarSystemID, int egoShipID, Session sess, int stamp)
    {
        if (mBallpark == null || mBallpark.Entities.Count == 0)
            return Array.Empty<byte>();

        List <Ball> balls = new List<Ball>();

        foreach (KeyValuePair <int, ItemEntity> kvp in mBallpark.Entities)
        {
            ItemEntity ent   = kvp.Value;
            bool       isEgo = (ent.ID == egoShipID);

            // If we have a BubbleEntity with live position, use it
            double       x, y, z;
            BubbleEntity bubbleEnt = null;
            bool         hasBubble = mDestinyManager != null && mDestinyManager.TryGetEntity(ent.ID, out bubbleEnt);
            if (hasBubble)
            {
                x = bubbleEnt.Position.X;
                y = bubbleEnt.Position.Y;
                z = bubbleEnt.Position.Z;
            }
            else
            {
                x = ent.X ?? 0;
                y = ent.Y ?? 0;
                z = ent.Z ?? 0;
            }

            BallFlag flags;
            BallMode mode;

            if (isEgo)
            {
                flags = BallFlag.IsFree | BallFlag.IsMassive | BallFlag.IsInteractive;
                mode  = BallMode.Stop;
            }
            else if (hasBubble)
            {
                // Use the BubbleEntity's actual state (works for player ships, NPCs, etc.)
                flags = bubbleEnt.Flags;
                mode  = bubbleEnt.Mode;
            }
            else
            {
                flags = BallFlag.IsGlobal | BallFlag.IsMassive | BallFlag.IsInteractive;
                mode  = BallMode.Rigid;
            }

            BallHeader header = new BallHeader
            {
                ItemId   = ent.ID,
                Mode     = mode,
                Radius   = bubbleEnt?.Radius ?? ent.Type?.Radius ?? 5000.0,
                Location = new Vector3 { X = x, Y = y, Z = z },
                Flags    = flags
            };

            Ball ball = new Ball
            {
                Header      = header,
                FormationId = 0xFF
            };

            if (mode != BallMode.Rigid)
            {
                ball.ExtraHeader = new ExtraBallHeader
                {
                    Mass          = isEgo ? 1000000.0 : (hasBubble ? bubbleEnt.Mass : 1000000.0),
                    CloakMode     = CloakMode.None,
                    Harmonic      = 0xFFFFFFFFFFFFFFFF,
                    CorporationId = isEgo ? sess.CorporationID : (hasBubble ? bubbleEnt.CorporationID : 0),
                    AllianceId    = hasBubble ? bubbleEnt.AllianceID : 0
                };

                if (flags.HasFlag(BallFlag.IsFree))
                {
                    ball.Data = new BallData
                    {
                        MaxVelocity   = isEgo ? 200.0 : (hasBubble ? bubbleEnt.MaxVelocity : 200.0),
                        Velocity      = hasBubble ? bubbleEnt.Velocity : default (Vector3),
                        UnknownVec    = default (Vector3),
                        Agility       = hasBubble ? bubbleEnt.Agility : 1.0,
                        SpeedFraction = hasBubble ? bubbleEnt.SpeedFraction : 0.0
                    };
                }
            }

            balls.Add(ball);
            Log.Information("[beyonce] Ball: id={BallID}, ego={IsEgo}, hasBubble={HasBubble}, mode={Mode}, flags={Flags}, pos=({X:F0},{Y:F0},{Z:F0})",
                            ent.ID, isEgo, hasBubble, mode, flags, x, y, z);
        }

        byte[] result = DestinyBinaryEncoder.BuildFullState(balls, stamp, 0);
        Log.Information("[beyonce] Encoded {BallCount} balls -> {ByteCount} bytes", balls.Count, result.Length);

        return result;
    }

    // =====================================================================
    //  HELPERS
    // =====================================================================

    /// <summary>
    /// Create a BubbleEntity from an ItemEntity for the DestinyManager.
    /// </summary>
    private static BubbleEntity CreateBubbleEntity(ItemEntity entity, Session session, bool isPlayerShip)
    {
        double x = entity.X ?? 0;
        double y = entity.Y ?? 0;
        double z = entity.Z ?? 0;

        BallFlag flags;
        BallMode mode;

        if (isPlayerShip)
        {
            flags = BallFlag.IsFree | BallFlag.IsMassive | BallFlag.IsInteractive;
            mode  = BallMode.Stop;
        }
        else
        {
            flags = BallFlag.IsGlobal | BallFlag.IsMassive | BallFlag.IsInteractive;
            mode  = BallMode.Rigid;
        }

        BubbleEntity bubble = new BubbleEntity
        {
            ItemID        = entity.ID,
            TypeID        = entity.Type?.ID ?? 0,
            GroupID       = entity.Type?.Group?.ID ?? 0,
            CategoryID    = entity.Type?.Group?.Category?.ID ?? 0,
            Name          = entity.Name ?? entity.Type?.Name ?? "Unknown",
            OwnerID       = isPlayerShip ? session.CharacterID : entity.OwnerID,
            CorporationID = isPlayerShip ? session.CorporationID : 0,
            AllianceID    = 0,
            CharacterID   = isPlayerShip ? session.CharacterID : 0,
            Position      = new Vector3 { X = x, Y = y, Z = z },
            Velocity      = default (Vector3),
            Mode          = mode,
            Flags         = flags,
            Radius        = isPlayerShip ? 50.0 : (entity.Type?.Radius ?? 5000.0),
            Mass          = 1000000.0,
            MaxVelocity   = isPlayerShip ? 200.0 : 0.0,
            SpeedFraction = 0.0,
            Agility       = 1.0
        };

        // Load HP, resistances, and signature radius from item attributes
        if (isPlayerShip && entity.Attributes != null)
        {
            AttributeList attrs = entity.Attributes;

            // Shield
            if (attrs.AttributeExists(AttributeTypes.shieldCapacity))
            {
                bubble.ShieldCapacity = (double) attrs[AttributeTypes.shieldCapacity];
                // shieldCharge defaults to full if not set (dogmaIM.ShipGetInfo initializes it)
                bubble.ShieldCharge = attrs.AttributeExists(AttributeTypes.shieldCharge)
                    ? (double) attrs[AttributeTypes.shieldCharge]
                    : bubble.ShieldCapacity;
            }

            // Armor
            if (attrs.AttributeExists(AttributeTypes.armorHP))
                bubble.ArmorHP = (double) attrs[AttributeTypes.armorHP];
            if (attrs.AttributeExists(AttributeTypes.armorDamage))
                bubble.ArmorDamage = (double) attrs[AttributeTypes.armorDamage];

            // Structure (hull) — attribute "hp"
            if (attrs.AttributeExists(AttributeTypes.hp))
                bubble.StructureHP = (double) attrs[AttributeTypes.hp];

            // Shield resistances
            if (attrs.AttributeExists(AttributeTypes.shieldEmDamageResonance))
                bubble.ShieldEmResonance = (double) attrs[AttributeTypes.shieldEmDamageResonance];
            if (attrs.AttributeExists(AttributeTypes.shieldExplosiveDamageResonance))
                bubble.ShieldExplosiveResonance = (double) attrs[AttributeTypes.shieldExplosiveDamageResonance];
            if (attrs.AttributeExists(AttributeTypes.shieldKineticDamageResonance))
                bubble.ShieldKineticResonance = (double) attrs[AttributeTypes.shieldKineticDamageResonance];
            if (attrs.AttributeExists(AttributeTypes.shieldThermalDamageResonance))
                bubble.ShieldThermalResonance = (double) attrs[AttributeTypes.shieldThermalDamageResonance];

            // Armor resistances
            if (attrs.AttributeExists(AttributeTypes.armorEmDamageResonance))
                bubble.ArmorEmResonance = (double) attrs[AttributeTypes.armorEmDamageResonance];
            if (attrs.AttributeExists(AttributeTypes.armorExplosiveDamageResonance))
                bubble.ArmorExplosiveResonance = (double) attrs[AttributeTypes.armorExplosiveDamageResonance];
            if (attrs.AttributeExists(AttributeTypes.armorKineticDamageResonance))
                bubble.ArmorKineticResonance = (double) attrs[AttributeTypes.armorKineticDamageResonance];
            if (attrs.AttributeExists(AttributeTypes.armorThermalDamageResonance))
                bubble.ArmorThermalResonance = (double) attrs[AttributeTypes.armorThermalDamageResonance];

            // Hull resistances
            if (attrs.AttributeExists(AttributeTypes.hullEmDamageResonance))
                bubble.HullEmResonance = (double) attrs[AttributeTypes.hullEmDamageResonance];
            if (attrs.AttributeExists(AttributeTypes.hullExplosiveDamageResonance))
                bubble.HullExplosiveResonance = (double) attrs[AttributeTypes.hullExplosiveDamageResonance];
            if (attrs.AttributeExists(AttributeTypes.hullKineticDamageResonance))
                bubble.HullKineticResonance = (double) attrs[AttributeTypes.hullKineticDamageResonance];
            if (attrs.AttributeExists(AttributeTypes.hullThermalDamageResonance))
                bubble.HullThermalResonance = (double) attrs[AttributeTypes.hullThermalDamageResonance];

            // Signature radius
            if (attrs.AttributeExists(AttributeTypes.signatureRadius))
                bubble.SignatureRadius = (double) attrs[AttributeTypes.signatureRadius];

            // Movement attributes
            if (attrs.AttributeExists(AttributeTypes.maxVelocity))
                bubble.MaxVelocity = (double) attrs[AttributeTypes.maxVelocity];
            if (attrs.AttributeExists(AttributeTypes.mass))
                bubble.Mass = (double) attrs[AttributeTypes.mass];
            if (attrs.AttributeExists(AttributeTypes.agility))
                bubble.Agility = (double) attrs[AttributeTypes.agility];
            if (attrs.AttributeExists(AttributeTypes.radius))
                bubble.Radius = (double) attrs[AttributeTypes.radius];
        }

        return bubble;
    }

    private void BroadcastRemoveBalls(int shipID)
    {
        if (shipID == 0) return;

        PyList events       = DestinyEventBuilder.BuildRemoveBalls(new[] { shipID });
        PyTuple notification = DestinyEventBuilder.WrapAsNotification(events);

        NotificationSender.SendNotification(
            "DoDestinyUpdate",
            "solarsystemid",
            mSolarSystemID,
            notification
        );

        Log.Information("[beyonce] Broadcast RemoveBalls for ship {ShipID} in system {SystemID}", shipID, mSolarSystemID);
    }

    private static int GetStamp()
    {
        long eveEpoch = new DateTime(2003, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;
        return (int)((DateTime.UtcNow.Ticks - eveEpoch) / 10000000 % int.MaxValue);
    }

    /// <summary>
    /// Patches the slim entries inside an AddBalls event list to include 'modules'.
    /// The DestinyEventBuilder.BuildAddBalls doesn't know about modules, so we add them here.
    /// </summary>
    private void AddModulesToAddBallSlims(PyList events, int shipID)
    {
        try
        {
            Log.Information("[beyonce] AddModulesToAddBallSlims: looking for shipID={ShipID} in AddBalls slims", shipID);

            // events = [(stamp, ("AddBalls", (state, slims, damageDict)))]
            if (events.Count == 0) return;

            PyTuple eventTuple = events[0] as PyTuple;
            if (eventTuple == null || eventTuple.Count < 2) return;

            PyTuple innerCall = eventTuple[1] as PyTuple;
            if (innerCall == null || innerCall.Count < 2) return;

            PyTuple args = innerCall[1] as PyTuple;
            if (args == null || args.Count < 2) return;

            PyList slimsList = args[1] as PyList;
            if (slimsList == null) return;

            bool foundShip = false;
            foreach (PyObjectData slimObj in slimsList.GetEnumerable<PyObjectData>())
            {
                PyDictionary slimDict = slimObj.Arguments as PyDictionary;
                if (slimDict == null) continue;

                if (slimDict.TryGetValue("itemID", out PyDataType idVal) && idVal is PyInteger itemId && itemId.Value == shipID)
                {
                    foundShip = true;
                    PyList modulesList = new PyList();

                    Ship ship = Items.LoadItem<Ship>(shipID);
                    if (ship == null)
                    {
                        Log.Warning("[beyonce] AddModulesToAddBallSlims: LoadItem<Ship>({ShipID}) returned NULL", shipID);
                    }
                    else if (ship.Items == null || ship.Items.Count == 0)
                    {
                        Log.Warning("[beyonce] AddModulesToAddBallSlims: Ship {ShipID} has EMPTY Items (ContentsLoaded={ContentsLoaded})",
                                    shipID, ship.ContentsLoaded);
                    }
                    else
                    {
                        foreach ((int _, ItemEntity module) in ship.Items)
                        {
                            if ((module.IsInModuleSlot() || module.IsInRigSlot()) && module is ShipModule
                                && module.Type?.Group?.Category?.ID == (int) CategoryID.Module)
                            {
                                // Client FitHardpoints2 unpacks as (flag, typeID)
                                modulesList.Add(new PyTuple(2)
                                {
                                    [0] = new PyInteger((int)module.Flag),
                                    [1] = new PyInteger(module.Type.ID)
                                });
                            }
                        }
                    }

                    slimDict["modules"] = modulesList;
                    Log.Information("[beyonce] AddModulesToAddBallSlims: ship {ShipID} -> {ModuleCount} modules added to AddBalls slim", shipID, modulesList.Count);
                }
            }

            if (!foundShip)
                Log.Warning("[beyonce] AddModulesToAddBallSlims: ship {ShipID} NOT FOUND in slims list", shipID);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[beyonce] Failed to add modules to AddBalls slims: {Message}", ex.Message);
        }
    }

    // Groups that represent celestial objects visible in the overview
    private static readonly HashSet<int> CelestialGroups = new HashSet<int>
    {
        (int)GroupID.Sun,
        (int)GroupID.Planet,
        (int)GroupID.Moon,
        (int)GroupID.AsteroidBelt,
        (int)GroupID.Stargate,
        (int)GroupID.Station,
    };

    private void LoadCelestials(int solarSystemID)
    {
        try
        {
            SolarSystem                            solarSystem = Items.GetStaticSolarSystem(solarSystemID);
            ConcurrentDictionary <int, ItemEntity> allItems    = Items.LoadAllItemsLocatedAt(solarSystem);

            Log.Information("[beyonce] LoadCelestials: {TotalItems} total items found in solar system {SystemID}", allItems.Count, solarSystemID);

            // Load per-item radii from mapDenormalize (planets, moons, etc. each have unique radii)
            Dictionary <int, double> celestialRadii = new Dictionary<int, double>();
            DbDataReader radiusReader = Database.Select(
                "SELECT itemID, radius FROM mapDenormalize WHERE solarSystemID = @solarSystemID AND radius IS NOT NULL",
                new Dictionary<string, object> { { "@solarSystemID", solarSystemID } }
            );
            using (radiusReader)
            {
                while (radiusReader.Read())
                    celestialRadii[radiusReader.GetInt32(0)] = radiusReader.GetDouble(1);
            }

            int count = 0;
            foreach (KeyValuePair <int, ItemEntity> kvp in allItems)
            {
                ItemEntity ent     = kvp.Value;
                int        groupID = ent.Type?.Group?.ID ?? 0;

                if (!CelestialGroups.Contains(groupID))
                    continue;

                // Skip entities already in the ballpark (e.g. undock station)
                if (mBallpark.Entities.ContainsKey(ent.ID))
                    continue;

                double radius = celestialRadii.TryGetValue(ent.ID, out double mdRadius)
                    ? mdRadius
                    : (ent.Type?.Radius ?? 5000.0);
                int    typeID    = ent.Type?.ID ?? 0;
                string groupName = ent.Type?.Group?.Name ?? "???";

                Log.Information(
                    "[beyonce]   Celestial: itemID={ItemID} typeID={TypeID} group={GroupName}({GroupID}) " +
                    "name=\"{Name}\" radius={Radius:F0}m pos=({X:F0}, {Y:F0}, {Z:F0})",
                    ent.ID, typeID, groupName, groupID,
                    ent.Name ?? ent.Type?.Name ?? "Unknown",
                    radius, ent.X ?? 0, ent.Y ?? 0, ent.Z ?? 0);

                mBallpark.AddEntity(ent);

                if (!mDestinyManager.TryGetEntity(ent.ID, out _))
                {
                    BubbleEntity bubble = new BubbleEntity
                    {
                        ItemID        = ent.ID,
                        TypeID        = typeID,
                        GroupID       = groupID,
                        CategoryID    = ent.Type?.Group?.Category?.ID ?? 0,
                        Name          = ent.Name ?? ent.Type?.Name ?? "Unknown",
                        OwnerID       = ent.OwnerID,
                        CorporationID = 0,
                        AllianceID    = 0,
                        CharacterID   = 0,
                        Position      = new Vector3 { X = ent.X ?? 0, Y = ent.Y ?? 0, Z = ent.Z ?? 0 },
                        Velocity      = default (Vector3),
                        Mode          = BallMode.Rigid,
                        Flags         = BallFlag.IsGlobal | BallFlag.IsMassive | BallFlag.IsInteractive,
                        Radius        = radius,
                        Mass          = 1000000.0,
                        MaxVelocity   = 0.0,
                        SpeedFraction = 0.0,
                        Agility       = 1.0
                    };
                    mDestinyManager.RegisterEntity(bubble);
                }

                count++;
            }

            Log.Information("[beyonce] Loaded {Count} celestials for system {SystemID}", count, solarSystemID);

            // Load stargate jump destinations for this solar system
            LoadStargateJumps(solarSystemID);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[beyonce] Failed to load celestials for system {SystemID}: {Message}", solarSystemID, ex.Message);
        }
    }

    private void LoadStargateJumps(int solarSystemID)
    {
        try
        {
            mStargateJumps.Clear();

            DbDataReader reader = Database.Select(
                "SELECT mj.stargateID, mj.celestialID, md2.solarSystemID " +
                "FROM mapJumps mj " +
                "INNER JOIN mapDenormalize md ON md.itemID = mj.stargateID " +
                "INNER JOIN mapDenormalize md2 ON md2.itemID = mj.celestialID " +
                "WHERE md.solarSystemID = @solarSystemID AND md.groupID = 10",
                new Dictionary<string, object> { { "@solarSystemID", solarSystemID } }
            );

            using (reader)
            {
                while (reader.Read())
                {
                    int stargateID      = reader.GetInt32(0);
                    int destGateID      = reader.GetInt32(1);
                    int destSolarSystem = reader.GetInt32(2);

                    if (!mStargateJumps.TryGetValue(stargateID, out List <(int GateID, int SolarSystemID)> dests))
                    {
                        dests                      = new List<(int, int)>();
                        mStargateJumps[stargateID] = dests;
                    }
                    dests.Add((destGateID, destSolarSystem));
                }
            }

            Log.Information("[beyonce] Loaded stargate jumps: {Count} gates with destinations in system {SystemID}", mStargateJumps.Count, solarSystemID);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[beyonce] Failed to load stargate jumps for system {SystemID}: {Message}", solarSystemID, ex.Message);
        }
    }

    private void EnsureBallpark(Session session)
    {
        if (mBallpark != null)
            return;

        int solarSystemID = session.SolarSystemID ?? mSolarSystemID;
        int ownerID       = session.CharacterID;
        int shipID        = session.ShipID ?? 0;
        int stationID     = session.StationID;

        mBallpark       = new Ballpark(solarSystemID, ownerID);
        mDestinyManager = SolarSystemDestinyMgr.GetOrCreate(solarSystemID);

        // Use LoadItem to guarantee the ship is loaded even if evicted from cache.
        ItemEntity shipEntity = shipID != 0 ? Items.LoadItem(shipID) : null;
        if (shipEntity != null)
        {
            mBallpark.AddEntity(shipEntity);
            if (!mDestinyManager.TryGetEntity(shipID, out _))
            {
                BubbleEntity shipBubble = CreateBubbleEntity(shipEntity, session, true);
                mDestinyManager.RegisterEntity(shipBubble);
            }
        }

        if (stationID != 0 && Items.TryGetItem(stationID, out ItemEntity stationEntity))
        {
            mBallpark.AddEntity(stationEntity);
            if (!mDestinyManager.TryGetEntity(stationID, out _))
            {
                BubbleEntity stationBubble = CreateBubbleEntity(stationEntity, session, false);
                mDestinyManager.RegisterEntity(stationBubble);
            }
        }

        LoadCelestials(solarSystemID);
    }

    private static PyObjectData BuildSolItem(int solID)
    {
        PyDictionary d = new PyDictionary
        {
            ["itemID"]          = new PyInteger(solID),
            ["typeID"]          = new PyInteger(5),
            ["groupID"]         = new PyInteger(5),
            ["ownerID"]         = new PyInteger(1),
            ["locationID"]      = new PyInteger(0),
            ["x"]               = new PyInteger(0),
            ["y"]               = new PyInteger(0),
            ["z"]               = new PyInteger(0),
            ["categoryID"]      = new PyInteger(2),
            ["name"]            = new PyString("Solar System"),
            ["corpID"]          = new PyInteger(0),
            ["allianceID"]      = new PyInteger(0),
            ["charID"]          = new PyInteger(0),
            ["dunObjectID"]     = new PyNone(),
            ["jumps"]           = new PyList(),
            ["securityStatus"]  = new PyDecimal(0.0),
            ["orbitalVelocity"] = new PyDecimal(0.0),
            ["warFactionID"]    = new PyNone(),
            ["bounty"]          = new PyDecimal(0.0)
        };

        return new PyObjectData("util.KeyVal", d);
    }

    private static PyObjectData BuildSlimItem(
        int itemID,  int typeID,     int groupID, int categoryID, string name,
        int ownerID, int locationID, int corpID,  int allianceID, int    charID)
    {
        PyDictionary d = new PyDictionary
        {
            ["itemID"]          = new PyInteger(itemID),
            ["typeID"]          = new PyInteger(typeID),
            ["groupID"]         = new PyInteger(groupID),
            ["ownerID"]         = new PyInteger(ownerID),
            ["locationID"]      = new PyInteger(locationID),
            ["categoryID"]      = new PyInteger(categoryID),
            ["name"]            = new PyString(name),
            ["corpID"]          = new PyInteger(corpID),
            ["allianceID"]      = new PyInteger(allianceID),
            ["charID"]          = new PyInteger(charID),
            ["dunObjectID"]     = new PyNone(),
            ["jumps"]           = new PyList(),
            ["securityStatus"]  = new PyDecimal(0.0),
            ["orbitalVelocity"] = new PyDecimal(0.0),
            ["warFactionID"]    = new PyNone(),
            ["bounty"]          = new PyDecimal(0.0)
        };

        return new PyObjectData("util.KeyVal", d);
    }

    /// <summary>
    /// Build damage state for the ego ship plus all IsFree entities (player ships, NPCs, etc.).
    /// Without a damage state entry, the client crashes when inspecting the entity.
    /// </summary>
    private PyDictionary BuildDamageStateAll(int egoShipID, int stamp)
    {
        PyDictionary dict = new PyDictionary();

        // Add ego ship
        if (egoShipID != 0)
        {
            BubbleEntity egoBubble = null;
            mDestinyManager?.TryGetEntity(egoShipID, out egoBubble);
            dict[new PyInteger(egoShipID)] = MakeDamageEntry(stamp, egoBubble);
        }

        // Add all IsFree entities (player ships, spawned NPCs, etc.)
        if (mDestinyManager != null)
        {
            foreach (BubbleEntity ent in mDestinyManager.GetEntities())
            {
                if (ent.ItemID != egoShipID && ent.Flags.HasFlag(BallFlag.IsFree))
                    dict[new PyInteger(ent.ItemID)] = MakeDamageEntry(stamp, ent);
            }
        }

        return dict;
    }

    internal static PyTuple MakeDamageEntry(int stamp, BubbleEntity entity = null)
    {
        double shield = entity?.ShieldFraction ?? 1.0;
        double armor  = entity?.ArmorFraction  ?? 1.0;
        double hull   = entity?.HullFraction   ?? 1.0;

        // Client expects: ((shieldFrac, tau), armorFrac, hullFrac)
        // tau = shield recharge time constant; 1e20 = effectively no passive regen
        PyTuple shieldTuple = new PyTuple(2)
        {
            [0] = new PyDecimal(shield),
            [1] = new PyDecimal(1e20)
        };

        return new PyTuple(3)
        {
            [0] = shieldTuple,
            [1] = new PyDecimal(armor),
            [2] = new PyDecimal(hull)
        };
    }

    private static PyDictionary BuildDamageState(int shipID, int stamp)
    {
        if (shipID == 0)
            return new PyDictionary();

        // Client expects: ((shieldFrac, tau), armorFrac, hullFrac)
        PyTuple shieldTuple = new PyTuple(2)
        {
            [0] = new PyDecimal(1.0),
            [1] = new PyDecimal(1e20)
        };

        PyTuple entry = new PyTuple(3)
        {
            [0] = shieldTuple,
            [1] = new PyDecimal(1.0),  // armor
            [2] = new PyDecimal(1.0)   // hull
        };

        return new PyDictionary
        {
            [new PyInteger(shipID)] = entry
        };
    }

    private static PyObjectData BuildEmptyDroneState()
    {
        return new PyObjectData(
            "util.Rowset",
            new PyDictionary
            {
                ["header"]   = new PyList
                {
                    new PyString("droneID"),
                    new PyString("ownerID"),
                    new PyString("controllerID"),
                    new PyString("activityState"),
                    new PyString("typeID"),
                    new PyString("controllerOwnerID"),
                    new PyString("targetID")
                },
                ["RowClass"] = new PyString("util.Row"),
                ["lines"]    = new PyList()
            }
        );
    }

    protected override void OnClientDisconnected()
    {
        Log.Information("[beyonce] Client disconnected, char={CharID}", mOwnerID);

        // Unlock all targets on disconnect
        TargetMgr?.UnlockAll(mOwnerID);

        if (mBallpark == null || mDestinyManager == null)
            return;

        // Find the player's ship in the DestinyManager
        int shipID = 0;
        foreach (BubbleEntity ent in mDestinyManager.GetEntities())
        {
            if (ent.CharacterID == mOwnerID)
            {
                shipID = ent.ItemID;
                break;
            }
        }

        if (shipID == 0)
            return;

        // Persist ship position and chrInformation before cleanup
        try
        {
            if (mDestinyManager.TryGetEntity(shipID, out BubbleEntity shipBubble))
            {
                ItemEntity shipEntity = Items.LoadItem(shipID);
                if (shipEntity != null)
                {
                    shipEntity.X = shipBubble.Position.X;
                    shipEntity.Y = shipBubble.Position.Y;
                    shipEntity.Z = shipBubble.Position.Z;
                    shipEntity.Persist();
                    Log.Information("[beyonce] Disconnect: Persisted ship {ShipID} at ({X:F0},{Y:F0},{Z:F0})",
                                    shipID, shipEntity.X, shipEntity.Y, shipEntity.Z);
                }
            }

            SolarSystem solarSystem = Items.GetStaticSolarSystem(mSolarSystemID);
            Database.Prepare(
                "UPDATE chrInformation " +
                "SET stationID = @stationID, solarSystemID = @solarSystemID, " +
                "    constellationID = @constellationID, regionID = @regionID " +
                "WHERE characterID = @characterID",
                new Dictionary<string, object>
                {
                    {"@characterID", mOwnerID},
                    {"@stationID", 0},
                    {"@solarSystemID", mSolarSystemID},
                    {"@constellationID", solarSystem.ConstellationId},
                    {"@regionID", solarSystem.RegionId}
                }
            );
            Log.Information("[beyonce] Disconnect: Updated chrInformation for char {CharID} in system {SystemID}", mOwnerID, mSolarSystemID);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[beyonce] Disconnect: Failed to persist position for ship {ShipID}, char {CharID}", shipID, mOwnerID);
        }

        Log.Information("[beyonce] Unregistering ship {ShipID} and broadcasting RemoveBalls", shipID);

        // Unregister from DestinyManager
        mDestinyManager.UnregisterEntity(shipID);

        // Broadcast RemoveBalls to other players in the system
        PyList events       = DestinyEventBuilder.BuildRemoveBalls(new[] { shipID });
        PyTuple notification = DestinyEventBuilder.WrapAsNotification(events);

        NotificationSender.SendNotification(
            "DoDestinyUpdate",
            "solarsystemid",
            mSolarSystemID,
            notification
        );
    }
}