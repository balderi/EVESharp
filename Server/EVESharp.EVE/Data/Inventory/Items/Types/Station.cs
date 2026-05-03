using System;
using System.Collections.Generic;
using EVESharp.Database.Inventory.Stations;
using EVESharp.Database.Types;
using EVESharp.EVE.Types;
using EVESharp.Types;
using EVESharp.Types.Collections;
using Type = EVESharp.Database.Inventory.Stations.Type;

namespace EVESharp.EVE.Data.Inventory.Items.Types;

public class Station : ItemInventory
{
    public Database.Inventory.Types.Information.Station StationInformation { get; }

    public Operation                   Operations               => StationInformation.Operations;
    public Type                        StationType              => StationInformation.Type;
    public Dictionary <int, Character> Guests                   { get; } = new Dictionary <int, Character> ();
    public int                         Security                 => StationInformation.Security;
    public double                      DockingCostPerVolume     => StationInformation.DockingCostPerVolume;
    public double                      MaxShipVolumeDockable    => StationInformation.MaxShipVolumeDockable;
    public int                         OfficeRentalCost         => StationInformation.OfficeRentalCost;
    public int                         SolarSystemID            => LocationID;
    public int                         ConstellationID          => StationInformation.ConstellationID;
    public int                         RegionID                 => StationInformation.RegionID;
    public double                      ReprocessingEfficiency   => StationInformation.ReprocessingEfficiency;
    public double                      ReprocessingStationsTake => StationInformation.ReprocessingStationsTake;
    public int                         ReprocessingHangarFlag   => StationInformation.ReprocessingHangarFlag;

    public Station (Database.Inventory.Types.Information.Station info) : base (info.Information)
    {
        StationInformation = info;
    }

    public PyDataType GetStationInfo ()
    {
        PyDictionary data = new PyDictionary
        {
            ["stationID"]                = ID,
            ["security"]                 = Security,
            ["dockingCostPerVolume"]     = DockingCostPerVolume,
            ["maxShipVolumeDockable"]    = MaxShipVolumeDockable,
            ["officeRentalCost"]         = OfficeRentalCost,
            ["operationID"]              = Operations.OperationID,
            ["stationTypeID"]            = Type.ID,
            ["ownerID"]                  = OwnerID,
            ["solarSystemID"]            = SolarSystemID,
            ["constellationID"]          = ConstellationID,
            ["regionID"]                 = RegionID,
            ["stationName"]              = Name,
            ["x"]                        = X,
            ["y"]                        = Y,
            ["z"]                        = Z,
            ["reprocessingEfficiency"]   = ReprocessingEfficiency,
            ["reprocessingStationsTake"] = ReprocessingStationsTake,
            ["reprocessingHangarFlag"]   = ReprocessingHangarFlag,
            ["description"]              = Operations.Description,
            ["serviceMask"]              = Operations.ServiceMask
        };

        return KeyVal.FromDictionary (data);
    }

    public bool HasService (Service service)
    {
        return (Operations.ServiceMask & (int) service) == (int) service;
    }

    public override void Destroy ()
    {
        throw new NotImplementedException ("Stations cannot be destroyed as they're regarded as static data!");
    }
}