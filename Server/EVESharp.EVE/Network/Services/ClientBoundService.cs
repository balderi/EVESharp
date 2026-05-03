using System;
using EVESharp.Common.Configuration;
using EVESharp.EVE.Notifications.Network;
using EVESharp.EVE.Sessions;
using EVESharp.EVE.Types.Network;
using EVESharp.Types;
using EVESharp.Types.Collections;

namespace EVESharp.EVE.Network.Services;

public abstract class ClientBoundService : BoundService
{
    public Session Session { get; }

    protected ClientBoundService(IBoundServiceManager manager) 
        : base(manager) 
    { }

    protected ClientBoundService(IBoundServiceManager manager, Session session, int objectID)
        : base(manager, objectID)
    {
        Session = session;
    }

    // ------------------------------
    // MACHOBIND: MAIN LOGIC
    // ------------------------------
    protected override PyDataType MachoBindObject(ServiceCall call, ServiceBindParams bindParams, PyDataType callInfo)
    {
        BoundService instance = this.CreateBoundInstance(call, bindParams);

        BoundServiceInformation = new PyTuple(2)
        {
            [0] = instance.BoundString,
            [1] = Guid.NewGuid().ToString()
        };

        PyTuple result = new PyTuple(2)
        {
            [0] = new PySubStruct(new PySubStream(BoundServiceInformation)),
            [1] = null
        };

        // Ensure the session is registered
        BoundServiceManager.MachoNet.SessionManager.RegisterSession(call.Session);

        // -------------------------------------
        // CALL THE POST-BIND FUNCTION IF GIVEN
        // -------------------------------------
        if (callInfo is not null)
        {
            PyTuple      data           = callInfo as PyTuple;
            string       func           = data[0] as PyString;
            PyTuple      arguments      = data[1] as PyTuple;
            PyDictionary namedArguments = data[2] as PyDictionary;

            Console.WriteLine(
                $"[ClientBoundService.MachoBindObject] post-bind func='{func}', " +
                $"targetBound='{instance?.GetType().FullName}', boundID={instance?.BoundID}"
            );

            ServiceCall callInformation = new ServiceCall
            {
                MachoNet            = call.MachoNet,
                CallID              = call.CallID,
                Destination         = call.Destination,
                Source              = call.Source,
                Payload             = arguments,
                NamedPayload        = namedArguments,
                Session             = call.Session,
                BoundServiceManager = call.BoundServiceManager,
                ServiceManager      = call.ServiceManager
            };

            result[1] = BoundServiceManager.ServiceCall(instance.BoundID, func, callInformation);
        }

        call.ResultOutOfBounds["OID+"] = new PyList<PyTuple> { BoundServiceInformation };
        return result;
    }

    // ------------------------------
    // ABSTRACT: MUST BE IMPLEMENTED IN CHILD CLASSES
    // ------------------------------
    protected abstract BoundService CreateBoundInstance(ServiceCall call, ServiceBindParams bindParams);

    protected virtual void OnClientDisconnected() { }

    // ------------------------------
    // ACCESS CONTROL
    // ------------------------------
    public override bool IsClientAllowedToCall(Session session)
    {
        return Session.UserID == session.UserID;
    }

    public override void ClientHasReleasedThisObject(Session session)
    {
        if (IsClientAllowedToCall(session) == false)
            return;

        this.OnClientDisconnected();
        BoundServiceManager.UnbindService(this);
    }

    // ------------------------------
    // SESSION UPDATE HANDLING
    // ------------------------------
    public override void ApplySessionChange(int characterID, PyDictionary<PyString, PyTuple> changes)
    {
        if (Session.CharacterID != characterID)
            return;

        Session.ApplyDelta(changes);
    }

    // ------------------------------
    // DESTROY THE BOUND SERVICE AND NOTIFY CLIENT
    // ------------------------------
    public override void DestroyService()
    {
        PyTuple disconnectData = new OnMachoObjectDisconnect(
            this.BoundString,
            Session.UserID,
            BoundServiceInformation[1] as PyString
        );

        PyTuple dataContainer =
            new PyTuple(2)
            {
                [0] = 1,
                [1] = disconnectData
            };

        dataContainer =
            new PyTuple(2)
            {
                [0] = 0,
                [1] = dataContainer
            };

        dataContainer =
            new PyTuple(2)
            {
                [0] = 0,
                [1] = new PySubStream(dataContainer)
            };

        dataContainer =
            new PyTuple(2)
            {
                [0] = dataContainer,
                [1] = null
            };

        string idType = Session.USERID;
        PyList ids    = new PyList() { Session.UserID };

        PyPacket packet = new PyPacket(PyPacket.PacketType.NOTIFICATION)
        {
            Destination = new PyAddressBroadcast(ids, idType, "OnMachoObjectDisconnect"),
            Source      = new PyAddressNode(BoundServiceManager.MachoNet.NodeID),
            UserID      = -1,
            Payload     = dataContainer,
            OutOfBounds = new PyDictionary()
            {
                { "OID-", BoundServiceInformation[1] }
            }
        };

        BoundServiceManager.MachoNet.QueueOutputPacket(packet);
        BoundServiceManager.UnbindService(this);
    }
}