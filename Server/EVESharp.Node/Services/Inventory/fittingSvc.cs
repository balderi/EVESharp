using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using EVESharp.EVE.Network.Services;
using EVESharp.EVE.Network.Services.Validators;
using EVESharp.Types;
using EVESharp.Types.Collections;
using Serilog;

namespace EVESharp.Node.Services.Inventory;

/// <summary>
/// In-memory fitting management service.
/// The client calls sm.RemoteSvc('fittingSvc') to save/load ship fittings.
/// Stores fittings in memory (no DB persistence for now).
/// </summary>
[MustBeCharacter]
[ConcreteService("fittingSvc")]
public class fittingSvc : Service
{
    public override AccessLevel AccessLevel => AccessLevel.None;

    private ILogger Log { get; }

    private int mNextFittingID = 1;

    // ownerID -> { fittingID -> fitting data }
    private readonly ConcurrentDictionary<int, ConcurrentDictionary<int, FittingEntry>> mFittings
        = new ConcurrentDictionary<int, ConcurrentDictionary<int, FittingEntry>>();

    private class FittingEntry
    {
        public int    FittingID;
        public int    OwnerID;
        public string Name;
        public int    ShipTypeID;
        public string Description;
        public PyList FitData; // list of (typeID, flag, qty)
    }

    public fittingSvc(ILogger logger)
    {
        Log = logger;
    }

    /// <summary>
    /// GetCharFittings - Returns all saved fittings for the calling character.
    /// </summary>
    public PyDataType GetFittings(ServiceCall call, PyInteger ownerID)
    {
        int charID = call.Session.CharacterID;
        int owner  = (int)(ownerID?.Value ?? charID);
        Log.Information("[fittingSvc] GetFittings called by char={CharID} for owner={OwnerID}", charID, owner);

        PyDictionary result = new PyDictionary();

        if (mFittings.TryGetValue(owner, out ConcurrentDictionary <int, FittingEntry> ownerFittings))
        {
            foreach (KeyValuePair <int, FittingEntry> kvp in ownerFittings)
            {
                FittingEntry entry = kvp.Value;
                PyDictionary fittingDict = new PyDictionary
                {
                    ["fittingID"]   = new PyInteger(entry.FittingID),
                    ["ownerID"]     = new PyInteger(entry.OwnerID),
                    ["name"]        = new PyString(entry.Name ?? ""),
                    ["shipTypeID"]  = new PyInteger(entry.ShipTypeID),
                    ["description"] = new PyString(entry.Description ?? ""),
                    ["fitData"]     = entry.FitData ?? new PyList()
                };
                result[new PyInteger(entry.FittingID)] = new PyObjectData("util.KeyVal", fittingDict);
            }
        }

        return result;
    }

    /// <summary>
    /// SaveFitting - Save a new fitting.
    /// </summary>
    public PyDataType SaveFitting(ServiceCall call, PyInteger ownerID, PyObjectData fitting)
    {
        int charID = call.Session.CharacterID;
        int owner  = (int)(ownerID?.Value ?? charID);
        Log.Information("[fittingSvc] SaveFitting called by char={CharID} for owner={OwnerID}", charID, owner);

        int fittingID = Interlocked.Increment(ref mNextFittingID);

        string name        = "";
        int    shipTypeID  = 0;
        string description = "";
        PyList fitData     = new PyList();

        if (fitting?.Arguments is PyDictionary dict)
        {
            if (dict.TryGetValue("name", out PyDataType nameVal) && nameVal is PyString nameStr)
                name = nameStr.Value;
            if (dict.TryGetValue("shipTypeID", out PyDataType shipVal) && shipVal is PyInteger shipInt)
                shipTypeID = (int)shipInt.Value;
            if (dict.TryGetValue("description", out PyDataType descVal) && descVal is PyString descStr)
                description = descStr.Value;
            if (dict.TryGetValue("fitData", out PyDataType fitVal) && fitVal is PyList fitList)
                fitData = fitList;
        }

        FittingEntry entry = new FittingEntry
        {
            FittingID   = fittingID,
            OwnerID     = owner,
            Name        = name,
            ShipTypeID  = shipTypeID,
            Description = description,
            FitData     = fitData
        };

        ConcurrentDictionary <int, FittingEntry> ownerFittings = mFittings.GetOrAdd(owner, _ => new ConcurrentDictionary<int, FittingEntry>());
        ownerFittings[fittingID] = entry;

        Log.Information("[fittingSvc] Saved fitting {FittingID} '{Name}' for owner {OwnerID}", fittingID, name, owner);
        return new PyInteger(fittingID);
    }

    /// <summary>
    /// DeleteFitting - Remove a fitting.
    /// </summary>
    public PyDataType DeleteFitting(ServiceCall call, PyInteger ownerID, PyInteger fittingID)
    {
        int owner = (int)(ownerID?.Value ?? call.Session.CharacterID);
        int fitID = (int)(fittingID?.Value ?? 0);
        Log.Information("[fittingSvc] DeleteFitting: owner={OwnerID}, fitting={FittingID}", owner, fitID);

        if (mFittings.TryGetValue(owner, out ConcurrentDictionary <int, FittingEntry> ownerFittings))
            ownerFittings.TryRemove(fitID, out _);

        return new PyNone();
    }

    /// <summary>
    /// UpdateNameAndDescription - Update a fitting's name and description.
    /// </summary>
    public PyDataType UpdateNameAndDescription(ServiceCall call, PyInteger fittingID, PyInteger ownerID,
                                               PyString    name, PyString  description)
    {
        int owner = (int)(ownerID?.Value ?? call.Session.CharacterID);
        int fitID = (int)(fittingID?.Value ?? 0);
        Log.Information("[fittingSvc] UpdateNameAndDescription: fitting={FittingID}, owner={OwnerID}", fitID, owner);

        if (mFittings.TryGetValue(owner, out ConcurrentDictionary <int, FittingEntry> ownerFittings)
            && ownerFittings.TryGetValue(fitID, out FittingEntry entry))
        {
            if (name != null) entry.Name               = name.Value;
            if (description != null) entry.Description = description.Value;
        }

        return new PyNone();
    }
}