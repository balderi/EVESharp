using System;
using EVESharp.EVE.Data.Inventory;
using EVESharp.EVE.Data.Inventory.Items;
using EVESharp.EVE.Network.Caching;
using EVESharp.EVE.Network.Services;
using EVESharp.EVE.Network.Services.Validators;
using EVESharp.EVE.Packets.Complex;
using EVESharp.Node.Cache;
using EVESharp.Types;


namespace EVESharp.Node.Services.Stations;

public class stationSvc : Service

{
    public override AccessLevel   AccessLevel  => AccessLevel.Station;
    private         IItems        Items        { get; }
    private         ICacheStorage CacheStorage { get; }

    public stationSvc(IItems items, ICacheStorage cacheStorage)
           
    {
        Items        = items;
        CacheStorage = cacheStorage;
    }

    public PyDataType GetStation(ServiceCall call, PyInteger stationID)
    {
        if (CacheStorage.Exists("stationSvc", $"GetStation_{stationID}") == false)
            CacheStorage.StoreCall(
                "stationSvc", $"GetStation_{stationID}",
                Items.Stations[stationID].GetStationInfo(),
                DateTime.UtcNow.ToFileTimeUtc()
            );

        return CachedMethodCallResult.FromCacheHint(CacheStorage.GetHint("stationSvc", $"GetStation_{stationID}"));
    }

    public PyDataType GetSolarSystem(ServiceCall call, PyInteger solarSystemID)
    {
        return Items.SolarSystems[solarSystemID].GetSolarSystemInfo();
    }

        


}