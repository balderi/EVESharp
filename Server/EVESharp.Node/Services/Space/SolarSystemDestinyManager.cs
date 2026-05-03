using System;
using System.Collections.Concurrent;
using EVESharp.Database.Old;
using EVESharp.EVE.Data.Inventory;
using EVESharp.Node.Services.Combat;

namespace EVESharp.Node.Services.Space;

/// <summary>
/// Singleton registry of per-solar-system DestinyManagers.
/// Creates them on demand when a player enters a system.
/// </summary>
public class SolarSystemDestinyManager
{
    private readonly ConcurrentDictionary<int, DestinyManager> mManagers = new ConcurrentDictionary<int, DestinyManager>();
    private readonly ConcurrentDictionary<int, int> mUndockStations = new ConcurrentDictionary<int, int>();
    private readonly DestinyBroadcaster mBroadcaster;
    private readonly StandingDB         mStandingDB;
    private readonly CombatService      mCombat;
    private readonly MissileManager     mMissileManager;
    private readonly IItems             mItems;

    public SolarSystemDestinyManager(DestinyBroadcaster broadcaster, StandingDB standingDB, CombatService combatService, MissileManager missileManager, IItems items)
    {
        mBroadcaster    = broadcaster;
        mStandingDB     = standingDB;
        mCombat         = combatService;
        mMissileManager = missileManager;
        mItems          = items;
    }

    /// <summary>
    /// Get or create the DestinyManager for a solar system.
    /// </summary>
    public DestinyManager GetOrCreate(int solarSystemID)
    {
        return mManagers.GetOrAdd(solarSystemID, id =>
        {
            Console.WriteLine($"[SolarSystemDestinyManager] Creating DestinyManager for system {id}");
            return new DestinyManager(id, mBroadcaster, mStandingDB, mCombat, mMissileManager, mItems);
        });
    }

    /// <summary>
    /// Remove and dispose a DestinyManager when no players remain.
    /// </summary>
    public void Remove(int solarSystemID)
    {
        if (mManagers.TryRemove(solarSystemID, out DestinyManager manager))
        {
            manager.Dispose();
            Console.WriteLine($"[SolarSystemDestinyManager] Removed DestinyManager for system {solarSystemID}");
        }
    }

    /// <summary>
    /// Try to get an existing DestinyManager (does not create one).
    /// </summary>
    public bool TryGet(int solarSystemID, out DestinyManager manager)
    {
        return mManagers.TryGetValue(solarSystemID, out manager);
    }

    /// <summary>
    /// Store the station ID a character is undocking from.
    /// Called by ship.Undock() BEFORE the session change clears StationID.
    /// </summary>
    public void SetUndockStation(int charID, int stationID)
    {
        mUndockStations[charID] = stationID;
    }

    /// <summary>
    /// Retrieve and remove the undock station ID for a character.
    /// Called by beyonce's bound constructor to get the station that was cleared from session.
    /// </summary>
    public int TakeUndockStation(int charID)
    {
        mUndockStations.TryRemove(charID, out int stationID);
        return stationID;
    }
}