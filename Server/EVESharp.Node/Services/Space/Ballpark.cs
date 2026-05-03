using System;
using System.Collections.Generic;
using EVESharp.Destiny;
using EVESharp.EVE.Data.Inventory.Items;

namespace EVESharp.Node.Services.Space;

public class Ballpark
{
    public int SolarSystemID { get; }
    public int OwnerID       { get; }

    public BubbleManager BubbleManager { get; } = new BubbleManager();

    // Public readonly access for snapshot builder (backward-compatible)
    public IReadOnlyDictionary<int, ItemEntity> Entities => mEntities;

    private readonly Dictionary<int, ItemEntity> mEntities =
        new Dictionary<int, ItemEntity>();

    public Ballpark(int solarSystemID, int ownerID)
    {
        SolarSystemID = solarSystemID;
        OwnerID       = ownerID;
    }

    public void AddEntity(ItemEntity entity)
    {
        if (entity == null)
            throw new ArgumentNullException(nameof(entity));

        mEntities[entity.ID] = entity;
    }

    /// <summary>
    /// Add an entity and also register it in the bubble system.
    /// </summary>
    public BubbleEntity AddEntityWithBubble(ItemEntity entity, BubbleEntity bubbleEntity)
    {
        if (entity == null)
            throw new ArgumentNullException(nameof(entity));

        mEntities[entity.ID] = entity;
        BubbleManager.AddEntity(bubbleEntity);
        return bubbleEntity;
    }

    public bool TryGetEntity(int itemID, out ItemEntity ent)
    {
        return mEntities.TryGetValue(itemID, out ent);
    }

    public bool RemoveEntity(int itemID)
    {
        return mEntities.Remove(itemID);
    }
}