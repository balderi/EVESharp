using System;
using EVESharp.Database.Types;
using EVESharp.EVE.Types;
using EVESharp.Types;
using EVESharp.Types.Collections;

namespace EVESharp.EVE.Data.Inventory.Items.Types;

public class Faction : ItemEntity
{
    public Database.Inventory.Types.Information.Faction FactionInformation { get; init; }

    public string Description          => FactionInformation.Description;
    public int    RaceIDs              => FactionInformation.RaceIDs;
    public int    SolarSystemId        => FactionInformation.SolarSystemID;
    public int    CorporationId        => FactionInformation.CorporationID;
    public double SizeFactor           => FactionInformation.SizeFactor;
    public int    StationCount         => FactionInformation.StationCount;
    public int    StationSystemCount   => FactionInformation.StationSystemCount;
    public int    MilitiaCorporationId => FactionInformation.MilitiaCorporationID;

    public Faction (Database.Inventory.Types.Information.Faction info) : base (info.Information)
    {
        FactionInformation = info;
    }

    public override void Destroy ()
    {
        throw new NotImplementedException ("Factions cannot be destroyed as they're regarded as static data!");
    }

    public PyDataType GetKeyVal ()
    {
        return KeyVal.FromDictionary (
            new PyDictionary
            {
                ["factionID"]     = ID,
                ["factionName"]   = Name,
                ["description"]   = Description,
                ["solarSystemID"] = SolarSystemId,
                ["corporationID"] = CorporationId,
                ["militiaID"]     = MilitiaCorporationId
            }
        );
    }
}