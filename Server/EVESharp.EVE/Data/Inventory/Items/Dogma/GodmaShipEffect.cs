using System;
using System.Collections.Generic;
using EVESharp.Database.Dogma;
using EVESharp.EVE.Data.Inventory.Items.Types;
using EVESharp.Types;
using EVESharp.Types.Collections;

namespace EVESharp.EVE.Data.Inventory.Items.Dogma;

public class GodmaShipEffect
{
    public ShipModule AffectedItem { get; init; }
    public Effect     Effect       { get; init; }
    public bool       ShouldStart  { get; set; }
    public long       StartTime    { get; set; }
    public PyDataType Duration     { get; set; }
    public int        TargetID     { get; set; }

    public static implicit operator PyDataType (GodmaShipEffect effect)
    {
        return new PyList
        {
            effect.AffectedItem.ID,
            effect.Effect.EffectID,
            DateTime.UtcNow.ToFileTimeUtc (),
            effect.ShouldStart,
            effect.ShouldStart,
            new PyTuple (7)
            {
                [0] = effect.AffectedItem.ID,
                [1] = effect.AffectedItem.OwnerID,
                [2] = effect.AffectedItem.LocationID,
                [3] = effect.TargetID != 0 ? new PyInteger (effect.TargetID) : null,
                [4] = null,
                [5] = null,
                [6] = effect.Effect.EffectID
            },
            effect.StartTime,
            effect.Duration,
            effect.Effect.DisallowAutoRepeat ? 0 : 1,
            new PyTuple (3) { [0] = 1, [1] = 1, [2] = 1 },
            null
        };
    }

    public static implicit operator List <PyDataType> (GodmaShipEffect effect)
    {
        return new List <PyDataType>
        {
            effect.AffectedItem.ID,
            effect.Effect.EffectID,
            DateTime.UtcNow.ToFileTimeUtc (),
            effect.ShouldStart,
            effect.ShouldStart,
            new PyTuple (7)
            {
                [0] = effect.AffectedItem.ID,
                [1] = effect.AffectedItem.OwnerID,
                [2] = effect.AffectedItem.LocationID,
                [3] = effect.TargetID != 0 ? new PyInteger (effect.TargetID) : null,
                [4] = null,
                [5] = null,
                [6] = effect.Effect.EffectID
            },
            effect.StartTime,
            effect.Duration,
            effect.Effect.DisallowAutoRepeat ? 0 : 1,
            new PyTuple (3) { [0] = 1, [1] = 1, [2] = 1 },
            null
        };
    }
}