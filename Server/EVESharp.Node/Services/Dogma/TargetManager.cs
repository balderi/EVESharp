using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace EVESharp.Node.Services.Dogma;

/// <summary>
/// Singleton service tracking target locks between entities.
/// Thread-safe: all state is in ConcurrentDictionaries.
/// </summary>
public class TargetManager
{
    // charID -> set of locked target itemIDs
    private readonly ConcurrentDictionary<int, HashSet<int>> mLockedTargets =
        new ConcurrentDictionary <int, HashSet <int>> ();

    // targetID -> set of charIDs that are locking this target
    private readonly ConcurrentDictionary<int, HashSet<int>> mTargeters =
        new ConcurrentDictionary <int, HashSet <int>> ();

    private readonly object mLock = new object ();

    public bool LockTarget(int charID, int targetID, int maxTargets)
    {
        lock (mLock)
        {
            HashSet <int> targets = mLockedTargets.GetOrAdd(charID, _ => new HashSet<int>());
            if (targets.Count >= maxTargets)
                return false;

            if (!targets.Add(targetID))
                return false; // already locked

            HashSet <int> targeters = mTargeters.GetOrAdd(targetID, _ => new HashSet<int>());
            targeters.Add(charID);
            return true;
        }
    }

    public void UnlockTarget(int charID, int targetID)
    {
        lock (mLock)
        {
            if (mLockedTargets.TryGetValue(charID, out HashSet <int> targets))
                targets.Remove(targetID);

            if (mTargeters.TryGetValue(targetID, out HashSet <int> targeters))
                targeters.Remove(charID);
        }
    }

    public void UnlockAll(int charID)
    {
        lock (mLock)
        {
            if (!mLockedTargets.TryRemove(charID, out HashSet <int> targets))
                return;

            foreach (int targetID in targets)
            {
                if (mTargeters.TryGetValue(targetID, out HashSet <int> targeters))
                    targeters.Remove(charID);
            }
        }
    }

    public List<int> GetTargets(int charID)
    {
        lock (mLock)
        {
            if (mLockedTargets.TryGetValue(charID, out HashSet <int> targets))
                return targets.ToList();
            return new List<int>();
        }
    }

    public List<int> GetTargeters(int targetID)
    {
        lock (mLock)
        {
            if (mTargeters.TryGetValue(targetID, out HashSet <int> targeters))
                return targeters.ToList();
            return new List<int>();
        }
    }

    /// <summary>
    /// Remove all locks on and from an entity (e.g. when it dies).
    /// Returns a list of (lockerID, targetID) pairs that were cleared, for broadcasting OnTarget "clear".
    /// </summary>
    public List<(int LockerID, int TargetID)> ClearEntity(int entityID)
    {
        List <(int, int)> cleared = new List<(int, int)>();

        lock (mLock)
        {
            // 1) Anyone locking this entity -> remove those locks
            if (mTargeters.TryRemove(entityID, out HashSet <int> lockers))
            {
                foreach (int lockerID in lockers)
                {
                    if (mLockedTargets.TryGetValue(lockerID, out HashSet <int> lockerTargets))
                        lockerTargets.Remove(entityID);
                    cleared.Add((lockerID, entityID));
                }
            }

            // 2) This entity's own locks -> remove from targeters
            if (mLockedTargets.TryRemove(entityID, out HashSet <int> ownTargets))
            {
                foreach (int targetID in ownTargets)
                {
                    if (mTargeters.TryGetValue(targetID, out HashSet <int> targeters))
                        targeters.Remove(entityID);
                    cleared.Add((entityID, targetID));
                }
            }
        }

        return cleared;
    }

    public bool IsLocked(int charID, int targetID)
    {
        lock (mLock)
        {
            return mLockedTargets.TryGetValue(charID, out HashSet <int> targets) && targets.Contains(targetID);
        }
    }
}