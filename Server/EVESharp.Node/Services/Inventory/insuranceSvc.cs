using System;
using EVESharp.Database;
using EVESharp.Database.Market;
using EVESharp.Database.Old;
using EVESharp.EVE.Data.Inventory;
using EVESharp.EVE.Data.Inventory.Items.Types;
using EVESharp.EVE.Exceptions;
using EVESharp.EVE.Market;
using EVESharp.EVE.Network;
using EVESharp.EVE.Network.Services;
using EVESharp.EVE.Sessions;
using EVESharp.Types;
using EVESharp.Types.Collections;

namespace EVESharp.Node.Services.Inventory;

public class insuranceSvc : ClientBoundService
{
    private readonly int                  mStationID;
    private readonly IBoundServiceManager mManager;

    public override AccessLevel AccessLevel => AccessLevel.None;

    private readonly Node.Services.Insurance.OldInsuranceDB DB;

    private readonly IItems        Items;
    private readonly MarketDB      MarketDB;
    private readonly ISolarSystems SolarSystems;
    private readonly IWallets      Wallets;
    private readonly IDatabase     Database;

    // --------------------------------------------------------------------
    // Root (unbound) constructor - used by the service manager
    // --------------------------------------------------------------------
    public insuranceSvc
    (
        IClusterManager      clusterManager,
        IItems               items,
        InsuranceDB          deprecated,
        MarketDB             marketDB,
        IWallets             wallets,
        IBoundServiceManager manager,
        IDatabase            database,
        ISolarSystems        solarSystems
    ) : base(manager)
    {
        mStationID   = 0; // not bound to a specific station in the root instance
        mManager     = manager;
        Items        = items;
        MarketDB     = marketDB;
        Wallets      = wallets;
        Database     = database;
        SolarSystems = solarSystems;

        DB = new Node.Services.Insurance.OldInsuranceDB(Database);

        // Hook cluster event (if available)
        if (clusterManager != null)
            clusterManager.ClusterTimerTick += PerformTimedEvents;
    }

    // --------------------------------------------------------------------
    // Bound constructor - used for station-bound instances
    // --------------------------------------------------------------------
    protected insuranceSvc
    (
        IItems               items,
        InsuranceDB          deprecated,
        MarketDB             marketDB,
        IWallets             wallets,
        IBoundServiceManager manager,
        int                  stationID,
        Session              session,
        ISolarSystems        solarSystems,
        IDatabase            database
    ) : base(manager, session, stationID)
    {
        mStationID = stationID;
        mManager   = manager;

        Items        = items;
        MarketDB     = marketDB;
        Wallets      = wallets;
        Database     = database;
        SolarSystems = solarSystems;

        DB = new Node.Services.Insurance.OldInsuranceDB(Database);
    }

    // --------------------------------------------------------------------
    // MachoResolveObject - must return a long nodeID
    // --------------------------------------------------------------------
    protected override long MachoResolveObject(ServiceCall call, ServiceBindParams bindParams)
    {
        // Single-node setup: always resolve to this node
        return 1;
    }

    // --------------------------------------------------------------------
    // CreateBoundInstance - create the per-station bound instance
    // --------------------------------------------------------------------
    protected override BoundService CreateBoundInstance(ServiceCall call, ServiceBindParams bindParams)
    {
        // We ignore bindParams.* here and just bind to the caller's station
        int stationID = call.Session.StationID;

        return new insuranceSvc(
            Items,
            null,          // deprecated InsuranceDB not used
            MarketDB,
            Wallets,
            mManager,
            stationID,
            call.Session,
            SolarSystems,
            Database       // reuse the same IDatabase instance
        );
    }

    // --------------------------------------------------------------------
    // Cluster timer handler
    // --------------------------------------------------------------------
    private void PerformTimedEvents(object? sender, EventArgs e)
    {
        // Placeholder for insurance expiration / cleanup logic
        // (safe to leave empty for now)
    }

    // --------------------------------------------------------------------
    // GetContracts – return all insurance contracts for your ship/station
    // --------------------------------------------------------------------
    public PyList<PyPackedRow> GetContracts(ServiceCall call)
    {
        if (mStationID == 0)
        {
            int? shipID = call.Session.ShipID;

            if (shipID == null)
                throw new CustomError("Character is not onboard a ship.");

            return new PyList<PyPackedRow>(1)
            {
                [0] = DB.GetContractForShip(call.Session.CharacterID, shipID.Value)
            };
        }

        return DB.GetContractsForShipsOnStation(call.Session.CharacterID, mStationID);
    }

    // --------------------------------------------------------------------
    // GetContractForShip – single ship insurance query
    // --------------------------------------------------------------------
    public PyPackedRow GetContractForShip(ServiceCall call, PyInteger itemID)
    {
        return DB.GetContractForShip(call.Session.CharacterID, itemID);
    }
}