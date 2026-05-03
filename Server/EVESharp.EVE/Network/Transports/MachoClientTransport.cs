using System;
using EVESharp.EVE.Network.Sockets;
using EVESharp.EVE.Sessions;
using EVESharp.EVE.Types.Network;
using EVESharp.Types;
using Serilog;

namespace EVESharp.EVE.Network.Transports;

public class MachoClientTransport : IMachoTransport
{
    public Session                        Session          { get; }
    public ILogger                        Log              { get; }
    public IEVESocket                     Socket           { get; }
    public IMachoNet                      MachoNet         { get; }
    public ITransportManager              TransportManager { get; }
    public event Action <IMachoTransport> Terminated;

    public MachoClientTransport (IMachoTransport source)
    {
        Socket           = source.Socket;
        Log              = source.Log;
        Session          = source.Session;
        MachoNet         = source.MachoNet;
        TransportManager = source.TransportManager;
        
        // finally assign the correct packet handler
        Socket.DataReceived   += this.ReceiveNormalPacket;
        Socket.Exception      += this.HandleException;
        Socket.ConnectionLost += this.HandleConnectionLost;
    }

    private void HandleConnectionLost ()
    {
        Log.Error ("Client {0} lost connection to the server", Session.UserID);

        // clean up ourselves
        this.Terminated?.Invoke (this);
    }

    private void HandleException (Exception ex)
    {
        Log.Error ("Exception detected: ");

        do
        {
            Log.Error ("{0}\n{1}", ex.Message, ex.StackTrace);
        }
        while ((ex = ex.InnerException) != null);
    }

    private void ReceiveNormalPacket (PyDataType packet)
    {
        if (packet is PyObject)
            throw new Exception ("Got exception from client");

        PyPacket pyPacket = packet;

        // replace the address if specific situations occur (why is CCP doing it like this?)
        if (pyPacket.Type == PyPacket.PacketType.NOTIFICATION && pyPacket.Source is PyAddressNode)
            pyPacket.Source = new PyAddressClient (Session.UserID);

        // ensure the source address is right as it cannot be trusted
        if (pyPacket.Source is not PyAddressClient source)
            throw new Exception ("Received a packet from client without a source client address");
        if (pyPacket.UserID != Session.UserID)
            throw new Exception ("Received a packet coming from a client trying to spoof it's userID");

        // ensure the clientId is set in the PyAddressClient
        source.ClientID = Session.UserID;

        // queue the input packet into machoNet so it handles it
        MachoNet.QueueInputPacket (this, pyPacket);
    }

    public void Close ()
    {
        this.Dispose ();
    }
    
    public void Dispose ()
    {
        // finally close the socket
        Socket.Close ();
        
        // cleanup callbacks
        Socket.DataReceived   -= this.ReceiveNormalPacket;
        Socket.Exception      -= this.HandleException;
        Socket.ConnectionLost -= this.HandleConnectionLost;
    }
}