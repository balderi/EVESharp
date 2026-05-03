using System;
using EVESharp.EVE.Network;
using EVESharp.EVE.Network.Transports;
using EVESharp.EVE.Packets;
using EVESharp.EVE.Sessions;
using EVESharp.EVE.Types.Network;
using EVESharp.Types;
using EVESharp.Types.Collections;

namespace EVESharp.Node.Sessions;

public class SessionManager : EVE.Sessions.SessionManager
{
    private IMachoNet         MachoNet         { get; }
    private ITransportManager TransportManager { get; }

    public SessionManager(ITransportManager transportManager, IMachoNet machoNet)
    {
        TransportManager = transportManager;
        MachoNet         = machoNet;

        MachoNet.SessionManager = this;

        TransportManager.TransportRemoved += this.OnTransportClosed;
        TransportManager.ClientResolved   += this.OnClientResolved;
    }

    public override void InitializeSession(Session session)
    {
        this.RegisterSession(session);

        PyPacket packet = new PyPacket(PyPacket.PacketType.SESSIONINITIALSTATENOTIFICATION)
        {
            Source      = new PyAddressNode(MachoNet.NodeID),
            Destination = new PyAddressClient(session.UserID, 0),
            UserID      = session.UserID,
            Payload     = new SessionInitialStateNotification { Session = session },
            OutOfBounds = new PyDictionary { ["channel"]                = "sessionchange" }
        };

        MachoNet.QueueOutputPacket(packet);
    }

    // ------------------------------------------------------------------
    // REQUIRED OVERRIDE – dispatches to proxy/node handlers
    // ------------------------------------------------------------------
    public override void PerformSessionUpdate(string idType, int id, Session newValues)
    {
        Console.WriteLine(
            $"[SessionManager] PerformSessionUpdate: idType={idType}, id={id}, mode={MachoNet.Mode}"
        );

        switch (MachoNet.Mode)
        {
            case RunMode.Proxy:
            case RunMode.Single:
                this.PerformSessionUpdateForProxy(idType, id, newValues);
                break;

            case RunMode.Server:
                this.PerformSessionUpdateForNode(idType, id, newValues);
                break;
        }
    }

    // ------------------------------------------------------------------
    // PROXY / SINGLE MODE HANDLER
    // ------------------------------------------------------------------
    private void PerformSessionUpdateForProxy(string idType, int id, Session newValues)
    {
        foreach (Session session in this.FindSession(idType, id))
        {
            SessionChange delta = UpdateAttributes(session, newValues);

            Console.WriteLine(
                $"[SessionManager] PerformSessionUpdateForProxy idType={idType}, id={id}, deltaCount={delta.Count}"
            );

            foreach (PyDictionaryKeyValuePair <PyString, PyTuple> kvp in delta)
            {
                string  key  = kvp.Key;
                PyTuple diff = kvp.Value;
                Console.WriteLine(
                    $"[SessionManager]    delta[{key}] = (old={diff[0]}, new={diff[1]})"
                );
            }

            if (delta.Count == 0)
                continue;

            // ------------------------------------------------------
            // STEP 2 – ensure NodesOfInterest has node + system
            // ------------------------------------------------------
             

            // 1. Add local node (where ballpark service lives)
            PyInteger nodeIDpy = new PyInteger(MachoNet.NodeID);
            if (!session.NodesOfInterest.Contains(nodeIDpy))
            {
                Console.WriteLine(
                    $"[SessionManager] Adding NodeID {MachoNet.NodeID} to NodesOfInterest"
                );
                session.NodesOfInterest.Add(nodeIDpy);
            }

            // 2. Add solarSystemID (moniker objectID for ballpark – informational)
            if (session.SolarSystemID != null)
            {
                PyInteger solSys = new PyInteger(session.SolarSystemID.Value);
                if (!session.NodesOfInterest.Contains(solSys))
                {
                    Console.WriteLine(
                        $"[SessionManager] Adding solarSystemID {session.SolarSystemID.Value} to NodesOfInterest"
                    );
                    session.NodesOfInterest.Add(solSys);
                }
            }

            // Build SessionChangeNotification payload
            SessionChangeNotification scn = new SessionChangeNotification
            {
                Changes         = delta,
                NodesOfInterest = session.NodesOfInterest
            };

            scn.AddNodeOfInterest(session.NodeID);
            Console.WriteLine($"[SCN-PATCH] Injected nodeID {session.NodeID} into SCN.NodesOfInterest");

            // ------------------------------------------------------------------
            // Broadcast only to real nodeIDs (not the solarSystemID)
            // ------------------------------------------------------------------
            PyList broadcastNodes = new PyList();
            broadcastNodes.Add(nodeIDpy);

            // Node-level notification
            PyPacket nodePacket = new PyPacket(PyPacket.PacketType.SESSIONCHANGENOTIFICATION)
            {
                Source      = new PyAddressNode(MachoNet.NodeID),
                Destination = new PyAddressBroadcast(broadcastNodes, "nodeid"),
                Payload     = scn,
                UserID      = session.UserID,
                OutOfBounds = new PyDictionary
                {
                    ["channel"]     = "sessionchange",
                    ["characterID"] = session.CharacterID
                }
            };

            // Client notification
            PyPacket clientPacket = new PyPacket(PyPacket.PacketType.SESSIONCHANGENOTIFICATION)
            {
                Source      = new PyAddressNode(MachoNet.NodeID),
                Destination = new PyAddressClient(session.UserID),
                Payload     = scn,
                UserID      = session.UserID,
                OutOfBounds = new PyDictionary { ["channel"] = "sessionchange" }
            };

            MachoNet.QueueOutputPacket(nodePacket);
            MachoNet.QueueOutputPacket(clientPacket);
        }
    }

    // ------------------------------------------------------------------
    // SERVER MODE HANDLER
    // ------------------------------------------------------------------
    private void PerformSessionUpdateForNode(string idType, int id, Session newValues)
    {
        Console.WriteLine(
            $"[SessionManager] PerformSessionUpdateForNode idType={idType}, id={id}"
        );

        PyPacket packet = new PyPacket(PyPacket.PacketType.NOTIFICATION)
        {
            Source      = new PyAddressNode(MachoNet.NodeID),
            Destination = new PyAddressAny(0),
            Payload     = new PyTuple(2)
            {
                [0] = "UpdateSessionAttributes",
                [1] = new PyTuple(3)
                {
                    [0] = idType,
                    [1] = id,
                    [2] = newValues
                }
            }
        };

        foreach ((long _, IMachoTransport transport) in MachoNet.TransportManager.ProxyTransports)
            transport.Socket.Send(packet);
    }

    private void OnTransportClosed(IMachoTransport transport)
    {
        if (transport is MachoClientTransport)
            this.FreeSession(transport.Session);
    }

    private void OnClientResolved(MachoClientTransport transport)
    {
        this.InitializeSession(transport.Session);
    }
}