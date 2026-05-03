using System;
using System.Collections.Generic;

namespace EVESharp.Destiny;

/// <summary>
/// Per-solar-system bubble registry. Manages spatial partitioning of entities
/// into 500 km radius bubbles for visibility and update broadcasting.
/// </summary>
public class BubbleManager
{
    private readonly Dictionary<int, SystemBubble> mBubbles        = new Dictionary<int, SystemBubble>();
    private readonly Dictionary<int, int>          mEntityToBubble = new Dictionary<int, int>(); // itemID -> bubbleID
    private          int                           mNextBubbleID   = 1;

    public IReadOnlyDictionary<int, SystemBubble> Bubbles => mBubbles;

    /// <summary>
    /// Place an entity into the appropriate bubble (creating one if needed).
    /// </summary>
    public SystemBubble AddEntity(BubbleEntity entity)
    {
        // Find an existing bubble that contains this position
        foreach (KeyValuePair <int, SystemBubble> kvp in mBubbles)
        {
            if (kvp.Value.ContainsPosition(entity.Position))
            {
                kvp.Value.AddEntity(entity);
                mEntityToBubble[entity.ItemID] = kvp.Key;
                Console.WriteLine($"[BubbleManager] Entity {entity.ItemID} placed in existing bubble {kvp.Key}");
                return kvp.Value;
            }
        }

        // No existing bubble contains this position - create a new one centered on the entity
        int id     = mNextBubbleID++;
        SystemBubble bubble = new SystemBubble(id, entity.Position);
        mBubbles[id] = bubble;
        bubble.AddEntity(entity);
        mEntityToBubble[entity.ItemID] = id;
        Console.WriteLine($"[BubbleManager] Entity {entity.ItemID} placed in new bubble {id} at {entity.Position}");
        return bubble;
    }

    /// <summary>
    /// Remove an entity from its bubble.
    /// </summary>
    public void RemoveEntity(int itemID)
    {
        if (mEntityToBubble.TryGetValue(itemID, out int bubbleID))
        {
            if (mBubbles.TryGetValue(bubbleID, out SystemBubble bubble))
            {
                bubble.RemoveEntity(itemID);
                // Clean up empty bubbles with no players
                if (bubble.IsEmpty)
                {
                    mBubbles.Remove(bubbleID);
                    Console.WriteLine($"[BubbleManager] Removed empty bubble {bubbleID}");
                }
            }
            mEntityToBubble.Remove(itemID);
        }
    }

    /// <summary>
    /// Check if an entity has drifted out of its bubble and move it if needed.
    /// Returns the new bubble if a transfer occurred, null otherwise.
    /// </summary>
    public SystemBubble UpdateEntityBubble(BubbleEntity entity)
    {
        if (!mEntityToBubble.TryGetValue(entity.ItemID, out int currentBubbleID))
            return AddEntity(entity);

        if (mBubbles.TryGetValue(currentBubbleID, out SystemBubble currentBubble))
        {
            if (currentBubble.ContainsPosition(entity.Position))
                return null; // Still in the same bubble
        }

        // Entity has moved out of its bubble - remove and re-add
        RemoveEntity(entity.ItemID);
        return AddEntity(entity);
    }

    /// <summary>
    /// Get the bubble that contains a given entity.
    /// </summary>
    public SystemBubble GetBubbleForEntity(int itemID)
    {
        if (mEntityToBubble.TryGetValue(itemID, out int bubbleID))
            if (mBubbles.TryGetValue(bubbleID, out SystemBubble bubble))
                return bubble;
        return null;
    }

    /// <summary>
    /// Try to find an entity across all bubbles.
    /// </summary>
    public bool TryGetEntity(int itemID, out BubbleEntity entity)
    {
        if (mEntityToBubble.TryGetValue(itemID, out int bubbleID))
            if (mBubbles.TryGetValue(bubbleID, out SystemBubble bubble))
                return bubble.TryGetEntity(itemID, out entity);

        entity = null;
        return false;
    }
}