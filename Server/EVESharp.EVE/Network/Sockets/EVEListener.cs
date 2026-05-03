using System;
using System.Net;
using System.Net.Sockets;

namespace EVESharp.EVE.Network.Sockets;

public class EVEListener : IEVEListener
{
    public event Action <IEVESocket> ConnectionAccepted;
    public event Action <Exception>  Exception;
    private Socket                   Socket { get; }
    private int                      Port   { get; }

    public EVEListener (int port)
    {
        Port   = port;
        Socket = new Socket (AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
        // ensure support for both ipv4 and ipv4
        Socket.SetSocketOption (SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, false);
        // setup transfer buffers
        Socket.ReceiveBufferSize = 64 * 1024;
        Socket.SendBufferSize    = 64 * 1024;
    }

    public virtual void Listen ()
    {
        Socket.Bind (new IPEndPoint (IPAddress.IPv6Any, Port));
        Socket.Listen (20);
        
        // begin accepting connections too
        Socket.BeginAccept (this.AcceptCallback, this);
    }

    /// <summary>
    /// Fires the connection accepted event
    /// </summary>
    /// <param name="socket"></param>
    protected virtual void OnConnectionAccepted (IEVESocket socket)
    {
        this.ConnectionAccepted?.Invoke (socket);
    }

    private void AcceptCallback (IAsyncResult ar)
    {
        try
        {
            Socket    socket       = Socket.EndAccept (ar);
            EVESocket clientSocket = new EVESocket (socket);
            
            // begin accepting again
            Socket.BeginAccept (this.AcceptCallback, this);

            this.ConnectionAccepted?.Invoke (clientSocket);
        }
        catch (Exception e)
        {
            this.Exception?.Invoke (e);
        }
    }

    public void Close ()
    {
        try
        {
            Socket.Close ();
        }
        catch (SocketException)
        {
            // listener socket may not be connected, safe to ignore
        }
    }
    
    public void Dispose ()
    {
        Socket.Dispose ();
    }
}