using System.Collections.Generic;

namespace EVESharp.Destiny;

/// <summary>
/// A 500 km radius sphere containing entities. Entities within the same bubble
/// can see each other and receive each other's destiny updates.
/// </summary>
public class SystemBubble
{
    public const double BUBBLE_RADIUS = 500_000.0; // 500 km in meters

    public int     BubbleID { get; }
    public Vector3 Center   { get; }

    private readonly Dictionary<int, BubbleEntity> mEntities     = new Dictionary<int, BubbleEntity>();
    private readonly HashSet<int>                  mCharacterIDs = new HashSet<int>();

    public IReadOnlyDictionary<int, BubbleEntity> Entities     => mEntities;
    public IReadOnlyCollection<int>               CharacterIDs => mCharacterIDs;

    public SystemBubble(int bubbleID, Vector3 center)
    {
        BubbleID = bubbleID;
        Center   = center;
    }

    public bool ContainsPosition(Vector3 pos)
    {
        return Center.DistanceSquare(pos) <= BUBBLE_RADIUS * BUBBLE_RADIUS;
    }

    public void AddEntity(BubbleEntity entity)
    {
        mEntities[entity.ItemID] = entity;
        if (entity.IsPlayer)
            mCharacterIDs.Add(entity.CharacterID);
    }

    public void RemoveEntity(int itemID)
    {
        if (mEntities.TryGetValue(itemID, out BubbleEntity entity))
        {
            mEntities.Remove(itemID);
            if (entity.IsPlayer)
                mCharacterIDs.Remove(entity.CharacterID);
        }
    }

    public bool TryGetEntity(int itemID, out BubbleEntity entity)
    {
        return mEntities.TryGetValue(itemID, out entity);
    }

    public bool HasPlayers => mCharacterIDs.Count > 0;
    public bool IsEmpty    => mEntities.Count == 0;
}