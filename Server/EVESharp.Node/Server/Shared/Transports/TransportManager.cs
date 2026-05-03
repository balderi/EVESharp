using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using EVESharp.Common.Configuration;
using EVESharp.Common.Logging;
using EVESharp.EVE.Network;
using EVESharp.EVE.Network.Sockets;
using EVESharp.EVE.Network.Transports;
using EVESharp.Node.Configuration;
using Serilog;

namespace EVESharp.Node.Server.Shared.Transports;

public class TransportManager : ITransportManager
{
    private readonly object _lock = new object ();
    private readonly List <IMachoTransport> _unauthenticatedTransports = new List <IMachoTransport> ();
    private readonly List <IMachoTransport> _transportList = new List <IMachoTransport> ();

    /// <summary>
    /// The current server transport in use
    /// </summary>
    public MachoServerTransport ServerTransport { get; protected set; }
    /// <summary>
    /// The unvalidated transports (returns a snapshot)
    /// </summary>
    public IReadOnlyList <IMachoTransport> UnauthenticatedTransports
    {
        get { lock (_lock) { return _unauthenticatedTransports.ToList (); } }
    }
    /// <summary>
    /// The registered and validated client transports
    /// </summary>
    public ConcurrentDictionary <int, MachoClientTransport> ClientTransports { get; } = new ConcurrentDictionary <int, MachoClientTransport> ();
    /// <summary>
    /// The registered and validated node transports
    /// </summary>
    public ConcurrentDictionary <long, MachoNodeTransport> NodeTransports { get; } = new ConcurrentDictionary <long, MachoNodeTransport> ();
    /// <summary>
    /// The registered and validated proxy transports
    /// </summary>
    public ConcurrentDictionary <long, MachoProxyTransport> ProxyTransports { get; } = new ConcurrentDictionary <long, MachoProxyTransport> ();
    /// <summary>
    /// Full list of active transports for this node (returns a snapshot)
    /// </summary>
    public IReadOnlyList <IMachoTransport> TransportList
    {
        get { lock (_lock) { return _transportList.ToList (); } }
    }
    protected ILogger    Log        { get; }
    protected HttpClient HttpClient { get; }

    /// <summary>
    /// Event fired when a transport is removed
    /// </summary>
    public event Action <IMachoTransport> TransportRemoved;
    public event Action <MachoClientTransport> ClientResolved;
    public event Action <MachoNodeTransport>   NodeResolved;
    public event Action <MachoProxyTransport>  ProxyResolved;

    public TransportManager (HttpClient httpClient, ILogger logger)
    {
        Log        = logger;
        HttpClient = httpClient;
    }

    public virtual MachoServerTransport OpenServerTransport (IMachoNet machoNet, MachoNet configuration)
    {
        return this.ServerTransport = new MachoServerTransport (configuration.Port, machoNet, Log.ForContext <MachoServerTransport> ());
    }

    public virtual IMachoTransport NewTransport (IMachoNet machoNet, IEVESocket socket)
    {
        MachoUnauthenticatedTransport transport = new MachoUnauthenticatedTransport (
            machoNet, this.HttpClient, socket, Log.ForContext <MachoUnauthenticatedTransport> (socket.RemoteAddress)
        );

        this.PrepareTransport (transport);

        lock (_lock)
        {
            _unauthenticatedTransports.Add (transport);
        }

        return transport;
    }

    /// <summary>
    /// Opens a new transport to the given IP and port
    /// </summary>
    /// <param name="machoNet"></param>
    /// <param name="ip"></param>
    /// <param name="port"></param>
    /// <returns></returns>
    public virtual IMachoTransport OpenNewTransport (IMachoNet machoNet, string ip, ushort port)
    {
        EVESocket socket = new EVESocket ();

        socket.Connect (ip, port);

        return this.NewTransport (machoNet, socket);
    }

    private void PrepareTransport (IMachoTransport transport)
    {
        // set some events on the transport
        transport.Terminated += this.OnTransportTerminated;
    }

    /// <summary>
    /// Registers the given transport as a client's transport
    /// </summary>
    /// <param name="transport"></param>
    public void ResolveClientTransport (MachoUnauthenticatedTransport transport)
    {
        MachoClientTransport original = null;
        MachoClientTransport newTransport;

        lock (_lock)
        {
            _unauthenticatedTransports.Remove (transport);
        }

        // clear transport's callbacks
        transport.Dispose ();

        // create the new client transport and store it somewhere
        newTransport = new MachoClientTransport (transport);

        if (ClientTransports.TryRemove (newTransport.Session.UserID, out original))
        {
            lock (_lock)
            {
                _transportList.Remove (original);
            }
        }

        this.PrepareTransport (newTransport);

        ClientTransports [newTransport.Session.UserID] = newTransport;

        lock (_lock)
        {
            _transportList.Add (newTransport);
        }

        // close old transport outside lock to avoid holding lock during I/O
        original?.Close ();

        this.ClientResolved?.Invoke (newTransport);
    }

    public void ResolveNodeTransport (MachoUnauthenticatedTransport transport)
    {
        Log.Information ($"Connection from server with nodeID {transport.Session.NodeID}");

        MachoNodeTransport original = null;
        MachoNodeTransport newTransport;

        lock (_lock)
        {
            _unauthenticatedTransports.Remove (transport);
        }

        // clear transport's callbacks
        transport.Dispose ();

        // create the new client transport and store it somewhere
        newTransport = new MachoNodeTransport (transport);

        if (NodeTransports.TryRemove (newTransport.Session.NodeID, out original))
        {
            lock (_lock)
            {
                _transportList.Remove (original);
            }
        }

        this.PrepareTransport (newTransport);

        NodeTransports [newTransport.Session.NodeID] = newTransport;

        lock (_lock)
        {
            _transportList.Add (newTransport);
        }

        original?.Close ();

        this.NodeResolved?.Invoke (newTransport);
    }

    public void ResolveProxyTransport (MachoUnauthenticatedTransport transport)
    {
        Log.Information ($"Connection from proxy with nodeID {transport.Session.NodeID}");

        MachoProxyTransport original = null;
        MachoProxyTransport newTransport;

        lock (_lock)
        {
            _unauthenticatedTransports.Remove (transport);
        }

        // clear transport's callbacks
        transport.Dispose ();

        // create the new client transport and store it somewhere
        newTransport = new MachoProxyTransport (transport);

        if (ProxyTransports.TryRemove (newTransport.Session.NodeID, out original))
        {
            lock (_lock)
            {
                _transportList.Remove (original);
            }
        }

        this.PrepareTransport (newTransport);

        ProxyTransports [newTransport.Session.NodeID] = newTransport;

        lock (_lock)
        {
            _transportList.Add (newTransport);
        }

        original?.Close ();

        this.ProxyResolved?.Invoke (newTransport);
    }

    private void OnTransportTerminated (IMachoTransport transport)
    {
        lock (_lock)
        {
            if (transport is not MachoUnauthenticatedTransport)
                _transportList.Remove (transport);

            switch (transport)
            {
                case MachoUnauthenticatedTransport:
                    _unauthenticatedTransports.Remove (transport);
                    break;

                case MachoClientTransport:
                    ClientTransports.TryRemove (transport.Session.UserID, out _);
                    break;

                case MachoNodeTransport:
                    NodeTransports.TryRemove (transport.Session.NodeID, out _);
                    break;

                case MachoProxyTransport:
                    ProxyTransports.TryRemove (transport.Session.NodeID, out _);
                    break;
            }
        }

        // close the transport and free any resources left
        transport.Close ();

        this.TransportRemoved?.Invoke (transport);
    }
}
