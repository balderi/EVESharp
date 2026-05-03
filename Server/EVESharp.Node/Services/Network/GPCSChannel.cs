using System;
using System.Collections.Generic;
using EVESharp.Types;
using EVESharp.EVE.Types.Network;
using EVESharp.EVE.Network;
using EVESharp.EVE.Network.Transports;

namespace EVESharp.Node.Services.Network;

public class GPCSChannel
{
    public  string    Name     { get; }
    private IMachoNet MachoNet { get; }

    private readonly HashSet<int> _listeners = new HashSet <int> ();

    public GPCSChannel(string name, IMachoNet machoNet)
    {
        Name     = name;
        MachoNet = machoNet;
    }

    // Client subscribes
    public void AddListener(int clientID)
    {
        _listeners.Add(clientID);
        Console.WriteLine($"[GPCS] Client {clientID} subscribed to {Name}");
    }

    // Client unsubscribes
    public void RemoveListener(int clientID)
    {
        if (_listeners.Remove(clientID))
            Console.WriteLine($"[GPCS] Client {clientID} unsubscribed from {Name}");
    }

    public void Send(PyPacket packet)
    {
        foreach (int clientID in _listeners)
        {
            if (MachoNet.TransportManager.ClientTransports.TryGetValue(clientID, out MachoClientTransport transport))
            {
                transport.Socket.Send(packet);
            }
        }
    }
}