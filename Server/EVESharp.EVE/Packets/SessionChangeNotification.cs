using System;
using System.IO;
using EVESharp.Types;
using EVESharp.Types.Collections;

namespace EVESharp.EVE.Packets;

public class SessionChangeNotification
{
    // This is the "clueless" int the client expects as the first element
    private int mClueless = 0;

    // { "charid": (old, new), "stationid": (old, new), ... }
    public PyDictionary<PyString, PyTuple> Changes { get; init; }

    /// <summary>
    /// List of nodes interested in the session change.
    /// MUST contain the current node during undock, or Apoc won't bind michelle.
    /// </summary>
    public PyList<PyInteger> NodesOfInterest { get; set; } =
        new PyList<PyInteger>();


    // Helper method to insert node IDs (used by SessionManager)
    public void AddNodeOfInterest(long nodeID)
    {
        NodesOfInterest.Add(new PyInteger((int)nodeID));
    }

    // ---- C# -> wire (to client) ----
    public static implicit operator PyTuple(SessionChangeNotification n)
    {
        // ---- DIAGNOSTIC LOGGING ----
        Console.WriteLine("========== SESSION CHANGE NOTIFICATION ==========");

        Console.WriteLine("SCN: NodesOfInterest:");
        foreach (PyInteger node in n.NodesOfInterest)
            Console.WriteLine($"  - Node: {node.Value}");

        Console.WriteLine("SCN: Changes:");
        foreach (PyDictionaryKeyValuePair <PyString, PyTuple> kvp in n.Changes)
        {
            string  key    = kvp.Key.ToString();
            PyTuple oldNew = kvp.Value;

            string oldVal = oldNew[0]?.ToString() ?? "None";
            string newVal = oldNew[1]?.ToString() ?? "None";

            Console.WriteLine($"  {key}: {oldVal} -> {newVal}");
        }

        Console.WriteLine("=================================================");

        // ---- ORIGINAL CODE ----
        return new PyTuple(2)
        {
            [0] = new PyTuple(2)
            {
                [0] = new PyInteger(n.mClueless),
                [1] = n.Changes
            },
            [1] = n.NodesOfInterest
        };
    }

    // ---- wire (from client / proxy) -> C# ----
    public static implicit operator SessionChangeNotification(PyTuple origin)
    {
        if (origin.Count != 2)
            throw new InvalidDataException(
                "SessionChangeNotification expects ((clueless, changesDict), nodesOfInterest)");

        if (origin[0] is not PyTuple sessionData)
            throw new InvalidDataException("First element must be a tuple (clueless, changesDict)");

        if (origin[1] is not PyList nodesList)
            throw new InvalidDataException("Second element must be a list of node IDs");

        if (sessionData.Count != 2)
            throw new InvalidDataException("Session data tuple must contain exactly two elements");

        if (sessionData[0] is not PyInteger clueless)
            throw new InvalidDataException("First element of session data must be PyInteger");

        if (sessionData[1] is not PyDictionary changesDict)
            throw new InvalidDataException("Second element of session data must be PyDictionary");

        return new SessionChangeNotification
        {
            mClueless       = (int)clueless.Value,
            Changes         = changesDict.GetEnumerable<PyString, PyTuple>(),
            NodesOfInterest = nodesList.GetEnumerable<PyInteger>()
        };
    }
}