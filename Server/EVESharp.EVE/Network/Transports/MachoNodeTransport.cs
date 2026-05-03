using System;
using EVESharp.EVE.Network.Sockets;
using EVESharp.EVE.Sessions;
using EVESharp.Types;
using Serilog;

namespace EVESharp.EVE.Network.Transports;

public class MachoNodeTransport : IMachoTransport
{
    public Session                        Session          { get; }
    public ILogger                        Log              { get; }
    public IEVESocket                     Socket           { get; }
    public IMachoNet                      MachoNet         { get; }
    public ITransportManager              TransportManager { get; }
    public event Action <IMachoTransport> Terminated;
    
    public MachoNodeTransport (IMachoTransport source)
    {
        Socket           = source.Socket;
        Log              = source.Log;
        Session          = source.Session;
        MachoNet         = source.MachoNet;
        TransportManager = source.TransportManager;
        // add load status to the session
        Session.LoadMetric      =  0;
        Socket.DataReceived   += this.HandlePacket;
        Socket.Exception      += this.HandleException;
        Socket.ConnectionLost += this.HandleConnectionLost;
    }

    private void HandlePacket (PyDataType data)
    {
        MachoNet.QueueInputPacket (this, data);
    }

    private void HandleConnectionLost ()
    {
        Log.Fatal ("Lost connection to node {0}, is it down?", Session.NodeID);

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

    public void Close ()
    {
        this.Dispose ();
    }
    
    public void Dispose ()
    {
        Socket.Close ();
        Socket.DataReceived   -= this.HandlePacket;
        Socket.Exception      -= this.HandleException;
        Socket.ConnectionLost -= this.HandleConnectionLost;
    }
}