using System;
using EVESharp.EVE.Network.Services;
using EVESharp.EVE.Sessions;
using EVESharp.Types;
using EVESharp.Types.Collections;
using EVESharp.Database.Types;

namespace EVESharp.Node.Services.Space;

/// <summary>
/// Server-side stub for the "michelle" service.
/// 
/// In the original EVE architecture, most of the heavy lifting for space
/// simulation is done client-side by the michelle.py service (Park / Ballpark).
/// On the server we only need a very thin service:
///
/// - Something that can be bound by MachoNet ("michelle" exists as a name)
/// - A hook (AddBallpark) that updates the session to point at a ballpark
/// - A way to hand the client a proper ballpark moniker (GetBallpark)
/// - Safe stubs for other calls the client might make
///
/// The real space state and Destiny snapshot are provided by beyonce
/// (the bound ballpark service) via DoDestinyUpdate notifications.
/// </summary>
[ConcreteService("michelle")]
public class michelle : ClientBoundService
{
    public override AccessLevel AccessLevel => AccessLevel.None;

    // =====================================================================
    //  CONSTRUCTORS
    // =====================================================================

    /// <summary>
    /// Global / unbound constructor.
    /// </summary>
    public michelle(IBoundServiceManager manager)
        : base(manager)
    {
        Console.WriteLine("[michelle] Global service constructed");
    }

    // =====================================================================
    //  MACHO BINDING
    // =====================================================================

    /// <summary>
    /// MachoResolveObject:
    /// The client is binding "michelle" for a specific context. We don't
    /// actually host per-solar-system michelle instances on different nodes,
    /// so we simply say "this object is on this node".
    /// </summary>
    protected override long MachoResolveObject(ServiceCall call, ServiceBindParams bindParams)
    {
        Console.WriteLine(
            $"[michelle] MachoResolveObject: objectID={bindParams.ObjectID}, charID={call.Session.CharacterID}");

        // For michelle we don't care about the objectID as a routing key:
        // just say "this lives on this node".
        long nodeId = BoundServiceManager.MachoNet.NodeID;
        Console.WriteLine($"[michelle] MachoResolveObject: returning nodeID={nodeId}");
        return nodeId;
    }

    /// <summary>
    /// CreateBoundInstance:
    /// Michelle is effectively stateless on the server; we can safely reuse
    /// the global instance instead of spinning up a per-session object.
    /// </summary>
    protected override ClientBoundService CreateBoundInstance(ServiceCall call, ServiceBindParams bindParams)
    {
        Console.WriteLine(
            $"[michelle] CreateBoundInstance: objectID={bindParams.ObjectID}, charID={call.Session.CharacterID}, shipID={call.Session.ShipID}");

        // No per-bound state; just reuse this instance.
        return this;
    }

    // =====================================================================
    //  LIFECYCLE
    // =====================================================================

    public override void DestroyService()
    {
        Console.WriteLine("[michelle] DestroyService()");
    }

    // =====================================================================
    //  API SURFACE (called by the client)
    // =====================================================================

    // ---------------------------------------------------------------------
    // GetInitialState
    //
    // Some EVE builds call michelle.GetInitialState() on the server side,
    // but in Apocrypha the real space snapshot comes via DoDestinyUpdate
    // notification, sent by the beyonce bound service during Moniker.Bind().
    //
    // Returning an empty dict here is safe: the client gets its state
    // from the DoDestinyUpdate notification, not from this method.
    // ---------------------------------------------------------------------
    public PyDataType GetInitialState(ServiceCall call)
    {
        Console.WriteLine("[michelle] GetInitialState() called");
        Console.WriteLine(
            "[michelle] GetInitialState: charID={0}, stationID={1}, system={2}",
            call.Session.CharacterID,
            call.Session.StationID,
            call.Session.SolarSystemID
        );

        // Empty dict – state comes from beyonce's DoDestinyUpdate notification.
        return new PyDictionary();
    }

    // ---------------------------------------------------------------------
    // AddBallpark
    //
    // This is the logical "enter space" hook on the server side. The client
    // michelle service (michelle.py) will:
    //   - create a local destiny.Ballpark instance,
    //   - call moniker.GetBallPark(solarsystemID),
    //   - bind the remote ballpark service,
    //   - then ask that remote ballpark for state.
    //
    // On the server, we only need to make sure the session is updated to
    // reflect the correct space environment, and that any server-side code
    // that looks at BallparkID/BallparkBroker can see "ballpark".
    // ---------------------------------------------------------------------
    public PyDataType AddBallpark(ServiceCall call, PyInteger solarSystemID)
    {
        int solID = (int)solarSystemID.Value;

        Console.WriteLine(
            $"[michelle] AddBallpark() called: charID={call.Session.CharacterID}, solarSystemID={solID}");
        Console.WriteLine(
            "[michelle] AddBallpark: session before: stationID={0}, solarsystemID={1}, solarsystemID2={2}, ballparkID={3}, ballparkBroker={4}",
            call.Session.StationID,
            call.Session.SolarSystemID,
            call.Session.SolarSystemID2,
            call.Session.BallparkID,
            call.Session.BallparkBroker
        );

        // Only touch the fields relevant for space / ballpark.
        call.Session.SolarSystemID  = solID;
        call.Session.SolarSystemID2 = solID;
        call.Session.BallparkID     = solID;
        call.Session.BallparkBroker = "beyonce";

        Console.WriteLine(
            "[michelle] AddBallpark: session after: stationID={0}, solarsystemID={1}, solarsystemID2={2}, ballparkID={3}, ballparkBroker={4}",
            call.Session.StationID,
            call.Session.SolarSystemID,
            call.Session.SolarSystemID2,
            call.Session.BallparkID,
            call.Session.BallparkBroker
        );

        // The original client michelle.AddBallpark() returns its local Park
        // object; on the server we don't have that, so an empty dict is fine.
        return new PyDictionary();
    }

    // ---------------------------------------------------------------------
    // GetBallpark
    //
    // Utility method that mirrors beyonce.GetBallPark, returning the
    // (nodeID, service, objectID) tuple the client moniker code expects.
    //
    // Some client codepaths might ask the "michelle" service for a ballpark
    // descriptor; this provides a stable answer that simply points back to
    // the real beyonce service.
    // ---------------------------------------------------------------------
    public PyDataType GetBallpark(ServiceCall call, PyInteger solarSystemID)
    {
        int solID = (int)solarSystemID.Value;

        Console.WriteLine(
            $"[michelle] GetBallpark() called: charID={call.Session.CharacterID}, solarSystemID={solID}");

        PyDictionary desc = new PyDictionary
        {
            ["nodeID"]   = new PyInteger(BoundServiceManager.MachoNet.NodeID),
            ["service"]  = new PyString("beyonce"),
            ["objectID"] = new PyInteger(solID)
        };

        Console.WriteLine(
            "[michelle] GetBallpark: returning KeyVal(nodeID={0}, service='beyonce', objectID={1})",
            BoundServiceManager.MachoNet.NodeID,
            solID
        );

        // util.KeyVal with nodeID/service/objectID – must match [ConcreteService("beyonce")].
        return new PyObjectData("util.KeyVal", desc);
    }

    // ---------------------------------------------------------------------
    // DoDestinyUpdate (RPC stub / debug hook)
    //
    // Normally DoDestinyUpdate reaches the client michelle via NOTIFICATION
    // (the ship service already sends that). However, if some client path
    // ever tries to call michelle.DoDestinyUpdate as an RPC, this stub
    // lets us see exactly what it passed without breaking anything.
    // ---------------------------------------------------------------------
    public PyDataType DoDestinyUpdate(ServiceCall call, PyDataType state, PyBool waitForBubble, PyDataType dogmaMessages)
    {
        Console.WriteLine(
            $"[michelle] DoDestinyUpdate() RPC called: charID={call.Session.CharacterID}, waitForBubble={(waitForBubble?.Value ?? false)}"
        );

        Console.WriteLine(
            $"[michelle]   state type = {state?.GetType().FullName ?? "<null>"}"
        );

        Console.WriteLine(
            $"[michelle]   dogmaMessages type = {dogmaMessages?.GetType().FullName ?? "<null>"}"
        );

        // We don't mutate anything here — the authoritative Destiny state
        // is handled by beyonce + DoDestinyUpdate notifications.
        return new PyNone();
    }


    // ---------------------------------------------------------------------
    // AddBalls / AddBalls2
    //
    // Client michelle may attempt to send batches of ball definitions back
    // to the server. EVESharp's current design runs the authoritative
    // simulation server-side in Ballpark; we don't need to process these
    // at all yet, but logging them is useful to confirm the path is hit.
    // ---------------------------------------------------------------------
    public PyDataType AddBalls(ServiceCall call, PyList balls)
    {
        Console.WriteLine(
            "[michelle] AddBalls() called: charID={0}, count={1}",
            call.Session.CharacterID,
            balls?.Count ?? 0
        );

        // No-op for now; simulation is driven by beyonce / Destiny snapshots.
        return new PyNone();
    }

    public PyDataType AddBalls2(ServiceCall call, PyList balls)
    {
        Console.WriteLine(
            "[michelle] AddBalls2() called: charID={0}, count={1}",
            call.Session.CharacterID,
            balls?.Count ?? 0
        );

        // Same as AddBalls – some client builds use the "2" variant.
        return new PyNone();
    }
}