using System;
using EVESharp.Database.Account;
using EVESharp.EVE.Data.Inventory;
using EVESharp.EVE.Dogma;
using EVESharp.EVE.Network.Services;
using EVESharp.EVE.Network.Services.Validators;
using EVESharp.EVE.Notifications;
using EVESharp.EVE.Sessions;
using EVESharp.Types;
using EVESharp.Types.Collections;
using Serilog;

namespace EVESharp.Node.Services.Space;

[MustBeCharacter]
[MustHaveRole(Roles.ROLE_DUNGEONMASTER)]
public class keeper : ClientBoundService
{
    public override AccessLevel AccessLevel => AccessLevel.None;

    private DungeonData               DungeonData           { get; }
    private IItems                    Items                 { get; }
    private IDogmaItems               DogmaItems            { get; }
    private SolarSystemDestinyManager SolarSystemDestinyMgr { get; }
    private INotificationSender       NotificationSender    { get; }
    private ILogger                   Log                   { get; }

    // Global / unbound constructor
    public keeper(
        IBoundServiceManager      manager,
        DungeonData               dungeonData,
        IItems                    items,
        IDogmaItems               dogmaItems,
        SolarSystemDestinyManager solarSystemDestinyMgr,
        INotificationSender       notificationSender,
        ILogger                   logger)
        : base(manager)
    {
        DungeonData           = dungeonData;
        Items                 = items;
        DogmaItems            = dogmaItems;
        SolarSystemDestinyMgr = solarSystemDestinyMgr;
        NotificationSender    = notificationSender;
        Log                   = logger;
        Log.Information("[keeper] Global service constructed");
    }

    // =====================================================================
    //  RPC METHODS
    // =====================================================================

    public PyDataType GetLevelEditor(ServiceCall call)
    {
        int charID = call.Session.CharacterID;
        Log.Information("[keeper] GetLevelEditor called by char={CharID}", charID);

        // Create a LevelEditor bound instance manually
        LevelEditor editor = new LevelEditor(
            BoundServiceManager,
            call.Session,
            charID,
            DungeonData,
            Items,
            DogmaItems,
            SolarSystemDestinyMgr,
            NotificationSender,
            Log);

        // BoundServiceInformation is already set by the base(manager, session, objectID) constructor
        // which calls manager.BindService() and sets BoundID + BoundString

        string guid = Guid.NewGuid().ToString();
        PyTuple boundInfo = new PyTuple(2)
        {
            [0] = editor.BoundString,
            [1] = new PyString(guid)
        };
        editor.SetBoundServiceInfo(boundInfo);

        call.ResultOutOfBounds["OID+"] = new PyList<PyTuple> { boundInfo };

        return new PySubStruct(new PySubStream(boundInfo));
    }

    // =====================================================================
    //  ABSTRACT OVERRIDES (required by ClientBoundService)
    // =====================================================================

    protected override long MachoResolveObject(ServiceCall call, ServiceBindParams bindParams)
    {
        return BoundServiceManager.MachoNet.NodeID;
    }

    protected override BoundService CreateBoundInstance(ServiceCall call, ServiceBindParams bindParams)
    {
        // Not used for keeper - it's a global service
        return this;
    }

    public override bool IsClientAllowedToCall(Session session)
    {
        return true;
    }

    public override void ClientHasReleasedThisObject(Session session)
    {
        // no-op for global service
    }

    public override void ApplySessionChange(int characterID, PyDictionary<PyString, PyTuple> changes)
    {
        // no-op
    }

    public override void DestroyService()
    {
        // no-op
    }
}