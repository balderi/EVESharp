using System;
using EVESharp.EVE.Data.Inventory;
using EVESharp.EVE.Data.Inventory.Items;
using EVESharp.EVE.Data.Inventory.Items.Types;
using EVESharp.EVE.Network.Services;
using EVESharp.EVE.Sessions;
using EVESharp.Types;
using EVESharp.Types.Collections;

namespace EVESharp.Node.Services.Space;

// NOTE:
//  This service is now intentionally "disabled" by changing the service name
//  so the client will NOT resolve or bind it under the vanilla Apoc flow.
//
//  The original name was:
//      [ConcreteService("spaceReg")]
//
//  We rename it so that any legacy code that *might* reference "spaceReg"
//  simply won't find it, and the normal michelle → ballpark path is used.
[ConcreteService("spaceReg")]
public class SpaceServiceRegistrar : Service
{
    public override AccessLevel AccessLevel => AccessLevel.None;

    // Inventory access so we can resolve the player's ship (kept for future use)
    private readonly IItems _items;

    // DI constructor – IItems will be injected by the node just like in ship.cs
    public SpaceServiceRegistrar(IItems items)
    {
        _items = items ?? throw new ArgumentNullException(nameof(items));
        Console.WriteLine("[SpaceServiceRegistrar] Global service constructed (DISABLED: spaceRegDisabled)");
    }

    // The moniker resolver – the client calls this BEFORE binding
    // NOTE: With the renamed ConcreteService, the Apoc client will not call this
    public long MachoResolveObject(ServiceCall call, PyDataType arguments, PyInteger nodeID)
    {
        Console.WriteLine("[spaceReg] MachoResolveObject() called " +
                          $"charID={call.Session.CharacterID} " +
                          $"stationID={call.Session.StationID} " +
                          $"solarsystemid2={call.Session.SolarSystemID2}");

        // Always resolve to local node for now
        return call.MachoNet.NodeID;
    }

    // The binder – the client calls this AFTER MachoResolveObject
    // NOTE: With the renamed ConcreteService, this should no longer be in the normal path.
    public PyDataType MachoBindObject(ServiceCall call, PyDataType bindArgs, PyDataType sessionInfo)
    {
        Console.WriteLine("[SpaceServiceRegistrar] MachoBindObject() invoked (WARNING: service is disabled / spaceRegDisabled)");

        // Do NOT create or bind a ballpark here anymore.
        // We just no-op and return PyNone to avoid interfering with any flows
        // that might accidentally hit this under the new name.
        return new PyNone();
    }
}