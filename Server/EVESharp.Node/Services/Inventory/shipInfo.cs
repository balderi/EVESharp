using System;
using EVESharp.EVE.Network.Services;
using EVESharp.EVE.Sessions;
using EVESharp.Types;
using EVESharp.Types.Collections;
using EVESharp.EVE.Data.Inventory;
using EVESharp.EVE.Data.Inventory.Items;
using EVESharp.EVE.Network.Services.Validators;


namespace EVESharp.Node.Services.Inventory;

[ConcreteService("shipSvc")]
public class shipSvc : ClientBoundService
{
    public override AccessLevel AccessLevel => AccessLevel.None;

    private IItems Items { get; }

    // Global constructor
    public shipSvc(IItems items, IBoundServiceManager manager)
        : base(manager)
    {
        Items = items;
        Console.WriteLine("[shipSvc] Global service constructed");
    }

    // Bound constructor
    public shipSvc(IItems items, IBoundServiceManager manager, Session session, int shipID)
        : base(manager, session, shipID)
    {
        Items = items;
        Console.WriteLine(
            $"[shipSvc] Bound instance created for char={session.CharacterID}, shipID={shipID}");
    }

    // Tell client this node owns all ships
    protected override long MachoResolveObject(ServiceCall call, ServiceBindParams parameters)
    {
        Console.WriteLine("[shipSvc] MachoResolveObject invoked");
        return BoundServiceManager.MachoNet.NodeID;
    }

    // Create a bound ship instance
    protected override BoundService CreateBoundInstance(ServiceCall call, ServiceBindParams bindParams)
    {
        Console.WriteLine($"[shipSvc] CreateBoundInstance for shipID={bindParams.ObjectID}");

        shipSvc instance = new shipSvc(Items, BoundServiceManager, call.Session, bindParams.ObjectID);
        return instance;
    }

    // -------------------------
    // REQUIRED BY CLIENT
    // Called during undock flow
    // -------------------------
    public PyDataType GetStateForShip(ServiceCall call)
    {
        Console.WriteLine("[shipSvc] GetStateForShip() called");

        int shipID  = call.Session.ShipID ?? 0;
        int ownerID = call.Session.CharacterID;

        // Minimal slimItem
        PyDictionary slimItem = new PyDictionary
        {
            ["itemID"]   = new PyInteger(shipID),
            ["typeID"]   = new PyInteger(GetShipTypeID(call)),
            ["ownerID"]  = new PyInteger(ownerID),
            ["flag"]     = new PyInteger(0),
            ["quantity"] = new PyInteger(1)
        };

        // The client expects a tuple: (slimItem, damageState)
        PyTuple stateTuple = new PyTuple(2)
        {
            [0] = slimItem,
            [1] = new PyNone() // no damage state yet
        };

        return stateTuple;
    }

    // Apoc client often calls this too
    public PyDataType GetDamageState(ServiceCall call)
    {
        Console.WriteLine("[shipSvc] GetDamageState() called");
        return new PyNone();
    }

    // Safety fallback
    public PyDataType GetModules(ServiceCall call)
    {
        Console.WriteLine("[shipSvc] GetModules() called");
        return new PyList(); 
    }

    public PyDataType GetMultiLevels(ServiceCall call)
    {
        Console.WriteLine("[shipSvc] GetMultiLevels() called");
        return new PyList();
    }

    // Let ballpark take over universe entry
    public PyDataType EnterSpace(ServiceCall call)
    {
        Console.WriteLine("[shipSvc] EnterSpace() called - passing through");

        // Client expects a PyNone
        return new PyNone();
    }

    protected override void OnClientDisconnected()
    {
        Console.WriteLine("[shipSvc] Client disconnected from ship bound object.");
    }
    private int GetShipTypeID(ServiceCall call)
    {
        // Try the session variable first
        int? typeID = call.Session.ShipTypeID;
        if (typeID != null && typeID.Value != 0)
            return typeID.Value;

        // Fallback: load the ship item directly
        int shipID = call.Session.ShipID ?? 0;
        if (shipID != 0)
        {
            try
            {
                ItemEntity shipItem = Items.GetItem(shipID);
                if (shipItem?.Type != null)
                    return shipItem.Type.ID;
            }
            catch
            {
                // item not loadable
            }
        }

        return 0;
    }

}