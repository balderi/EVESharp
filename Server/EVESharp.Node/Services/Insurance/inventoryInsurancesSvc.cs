using System;
using System.Collections.Generic;

using EVESharp.Database;
using EVESharp.Database.Extensions;
using EVESharp.Database.Market;
using EVESharp.Database.Old;

using EVESharp.EVE.Data.Inventory;
using EVESharp.EVE.Data.Inventory.Items.Types;
using EVESharp.EVE.Exceptions;
using EVESharp.EVE.Exceptions.insuranceSvc;
using EVESharp.EVE.Market;
using EVESharp.EVE.Network;
using EVESharp.EVE.Network.Services;
using EVESharp.EVE.Network.Services.Validators;
using EVESharp.EVE.Sessions;

using EVESharp.Types;
using EVESharp.Types.Collections;


namespace EVESharp.Node.Services.Insurance;

[MustBeCharacter]
public class inventoryInsurancesSvc : ClientBoundService
{
    // ❗ FORCE C# TO USE YOUR CUSTOM DB, NOT EVESharp.Database.OldInsuranceDB
    private readonly EVESharp.Node.Services.Insurance.OldInsuranceDB DB;

    private readonly IDatabase Database;

    public override AccessLevel AccessLevel => AccessLevel.None;

    // Unbound constructor
    public inventoryInsurancesSvc(
        IBoundServiceManager manager,
        IDatabase            database)
        : base(manager)
    {
        this.Database = database;

        // ❗ FULLY QUALIFIED NAME — GUARANTEED CORRECT CLASS
        this.DB = new EVESharp.Node.Services.Insurance.OldInsuranceDB(database);
    }

    // Bound constructor (station-bound instance)
    protected inventoryInsurancesSvc(
        IBoundServiceManager manager,
        Session              session,
        int                  stationID,
        IDatabase            database)
        : base(manager, session, stationID)
    {
        this.Database = database;

        // ❗ FULLY QUALIFIED NAME — GUARANTEED CORRECT CLASS
        this.DB = new EVESharp.Node.Services.Insurance.OldInsuranceDB(database);
    }

    // Called by client UI: insuranceSvc.GetContractForShip
    public PyPackedRow GetContractForShip(ServiceCall call, PyInteger shipID)
    {
        return DB.GetContractForShip(call.Session.CharacterID, shipID);
    }

    protected override long MachoResolveObject(ServiceCall call, ServiceBindParams parameters)
    {
        return Database.CluResolveAddress("station", parameters.ObjectID);
    }

    protected override BoundService CreateBoundInstance(ServiceCall call, ServiceBindParams bindParams)
    {
        return new inventoryInsurancesSvc(
            this.BoundServiceManager,
            call.Session,
            bindParams.ObjectID,
            this.Database
        );
    }
}