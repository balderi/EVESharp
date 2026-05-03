using System;
using EVESharp.EVE.Network.Services;
using EVESharp.EVE.Sessions;
using EVESharp.Types;
using EVESharp.Types.Collections;
using Serilog;

namespace EVESharp.Node.Services.Space;

/// <summary>
/// Stub implementation of the scanMgr service.
/// The client binds this via util.Moniker('scanMgr', session.solarsystemid) for probe scanning.
/// Returns empty results to prevent client-side exceptions when opening the probe scanner.
/// </summary>
[ConcreteService("scanMgr")]
public class scanMgr : ClientBoundService
{
    public override AccessLevel AccessLevel => AccessLevel.None;

    private ILogger Log { get; }
    private int     mSolarSystemID;

    // Global / unbound constructor (DI)
    public scanMgr(IBoundServiceManager manager, ILogger logger)
        : base(manager)
    {
        Log = logger;
    }

    // Bound constructor (per-client instance)
    internal scanMgr(IBoundServiceManager manager, Session session, int objectID, ILogger logger)
        : base(manager, session, objectID)
    {
        Log            = logger;
        mSolarSystemID = objectID;
    }

    protected override long MachoResolveObject(ServiceCall call, ServiceBindParams bindParams)
    {
        return BoundServiceManager.MachoNet.NodeID;
    }

    protected override BoundService CreateBoundInstance(ServiceCall call, ServiceBindParams bindParams)
    {
        Log.Information("[scanMgr] CreateBoundInstance: objectID={ObjectID}, char={CharID}", bindParams.ObjectID, call.Session.CharacterID);
        return new scanMgr(BoundServiceManager, call.Session, bindParams.ObjectID, Log);
    }

    /// <summary>
    /// RequestScans - Submit a scan with probe positions. Returns empty results.
    /// </summary>
    public PyDataType RequestScans(ServiceCall call, PyDataType probes)
    {
        Log.Information("[scanMgr] RequestScans called by char={CharID} (stub)", call.Session.CharacterID);
        return new PyNone();
    }

    /// <summary>
    /// ConeScan - Directional scan. Returns empty list.
    /// </summary>
    public PyDataType ConeScan(ServiceCall call, PyDecimal scanAngle, PyDecimal scanRange,
                               PyDecimal   x,    PyDecimal y,         PyDecimal z)
    {
        Log.Information("[scanMgr] ConeScan called by char={CharID} angle={Angle} range={Range} (stub)",
                        call.Session.CharacterID, scanAngle?.Value, scanRange?.Value);
        return new PyList();
    }

    /// <summary>
    /// ReconnectToLostProbes - Reconnect after disconnect.
    /// </summary>
    public PyDataType ReconnectToLostProbes(ServiceCall call)
    {
        Log.Information("[scanMgr] ReconnectToLostProbes called (stub)");
        return new PyNone();
    }

    /// <summary>
    /// DestroyProbe - Destroy a probe.
    /// </summary>
    public PyDataType DestroyProbe(ServiceCall call, PyInteger probeID)
    {
        Log.Information("[scanMgr] DestroyProbe called for probe {ProbeID} (stub)", probeID?.Value);
        return new PyNone();
    }

    /// <summary>
    /// RecoverProbes - Recall probes.
    /// </summary>
    public PyDataType RecoverProbes(ServiceCall call, PyList probeIDs)
    {
        Log.Information("[scanMgr] RecoverProbes called (stub)");
        return new PyNone();
    }

    /// <summary>
    /// BookmarkResult - Bookmark a scan result.
    /// </summary>
    public PyDataType BookmarkResult(ServiceCall call, PyInteger targetID)
    {
        Log.Information("[scanMgr] BookmarkResult called for target {TargetID} (stub)", targetID?.Value);
        return new PyNone();
    }
}