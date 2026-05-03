using System;
using System.Collections.Generic;
using EVESharp.Database.Account;
using EVESharp.Database.Inventory;
using EVESharp.Database.Inventory.Attributes;
using EVESharp.Database.Inventory.Categories;
using EVESharp.Database.Inventory.Types;
using EVESharp.Destiny;
using EVESharp.EVE.Data.Inventory;
using EVESharp.EVE.Data.Inventory.Items;
using EVESharp.EVE.Dogma;
using EVESharp.EVE.Network.Services;
using EVESharp.EVE.Network.Services.Validators;
using EVESharp.EVE.Notifications;
using EVESharp.EVE.Sessions;
using EVESharp.Types;
using EVESharp.Types.Collections;
using Serilog;
using Type = EVESharp.Database.Inventory.Types.Type;

namespace EVESharp.Node.Services.Space;

[MustHaveRole(Roles.ROLE_DUNGEONMASTER)]
public class LevelEditor : ClientBoundService
{
    public override AccessLevel AccessLevel => AccessLevel.None;

    private DungeonData               DungeonData           { get; }
    private IItems                    Items                 { get; }
    private IDogmaItems               DogmaItems            { get; }
    private SolarSystemDestinyManager SolarSystemDestinyMgr { get; }
    private INotificationSender       NotificationSender    { get; }
    private ILogger                   Log                   { get; }
    private int                       CharacterID           { get; }
    private int                       CurrentDungeonID      { get; set; }

    // Unbound constructor for manual binding
    public LevelEditor(
        IBoundServiceManager      manager,
        Session                   session,
        int                       characterID,
        DungeonData               dungeonData,
        IItems                    items,
        IDogmaItems               dogmaItems,
        SolarSystemDestinyManager solarSystemDestinyMgr,
        INotificationSender       notificationSender,
        ILogger                   logger)
        : base(manager, session, characterID)
    {
        CharacterID           = characterID;
        DungeonData           = dungeonData;
        Items                 = items;
        DogmaItems            = dogmaItems;
        SolarSystemDestinyMgr = solarSystemDestinyMgr;
        NotificationSender    = notificationSender;
        Log                   = logger;
        Log.Information("[LevelEditor] Bound instance created for char={CharID}", characterID);
    }

    public void SetBoundServiceInfo(PyTuple info)
    {
        BoundServiceInformation = info;
    }

    // =====================================================================
    //  RPC METHODS
    // =====================================================================

    public PyDataType Bind(ServiceCall call)
    {
        Log.Information("[LevelEditor] Bind called for char={CharID}", CharacterID);
        return null;
    }

    public PyDataType PlayDungeon(ServiceCall call, PyInteger dungeonID, PyInteger roomID)
    {
        int dunID     = (int)dungeonID.Value;
        int roomIndex = roomID != null ? (int)roomID.Value : 0;
        Log.Information("[LevelEditor] PlayDungeon: dungeon={DungeonID}, roomIndex={RoomIndex}", dunID, roomIndex);

        if (!DungeonData.Dungeons.TryGetValue(dunID, out DungeonDefinition dung))
            return null;

        CurrentDungeonID = dunID;
        int actualRoomID = ResolveRoomID(dung, roomIndex);

        if (!DungeonData.Rooms.TryGetValue(actualRoomID, out RoomDefinition room))
            return null;

        Log.Information("[LevelEditor] PlayDungeon: resolved roomIndex={Index} -> roomID={RoomID} ({RoomName})",
                        roomIndex, actualRoomID, room.RoomName);
        SpawnRoomObjects(call.Session, room);
        return null;
    }

    public PyDataType EditDungeon(ServiceCall call, PyInteger dungeonID)
    {
        int dunID = (int)dungeonID.Value;
        Log.Information("[LevelEditor] EditDungeon: dungeon={DungeonID}", dunID);

        if (!DungeonData.Dungeons.TryGetValue(dunID, out DungeonDefinition dung))
            return null;

        CurrentDungeonID = dunID;
        int roomIndex = 0;

        // The client sends roomName=selectedRoom where selectedRoom is actually
        // a 1-based room index from the UI list, not a string name
        if (call.NamedPayload != null &&
            call.NamedPayload.TryGetValue("roomName", out PyDataType roomVal))
        {
            if (roomVal is PyInteger roomInt)
                roomIndex = (int)roomInt.Value;
            else if (roomVal is PyString roomStr && int.TryParse(roomStr.Value, out int parsed))
                roomIndex = parsed;
        }

        int actualRoomID = ResolveRoomID(dung, roomIndex);
        DungeonData.EditingRooms[CharacterID] = actualRoomID;

        if (DungeonData.Rooms.TryGetValue(actualRoomID, out RoomDefinition room))
        {
            Log.Information("[LevelEditor] EditDungeon: resolved roomIndex={Index} -> roomID={RoomID} ({RoomName})",
                            roomIndex, actualRoomID, room.RoomName);
            SpawnRoomObjects(call.Session, room);
        }

        return null;
    }

    public PyDataType GotoRoom(ServiceCall call, PyInteger roomID)
    {
        int roomIndex = (int)roomID.Value;
        Log.Information("[LevelEditor] GotoRoom: roomIndex={RoomIndex}, currentDungeon={DungeonID}", roomIndex, CurrentDungeonID);

        if (DungeonData.Dungeons.TryGetValue(CurrentDungeonID, out DungeonDefinition dung))
        {
            int actualRoomID = ResolveRoomID(dung, roomIndex);
            DungeonData.EditingRooms[CharacterID] = actualRoomID;
            Log.Information("[LevelEditor] GotoRoom: resolved -> roomID={RoomID}", actualRoomID);
        }

        return null;
    }

    /// <summary>
    /// The client sends a 1-based index into the dungeon's room list, not the actual roomID.
    /// This resolves the index to the real roomID. Falls back to first room if index is invalid.
    /// </summary>
    private int ResolveRoomID(DungeonDefinition dungeon, int roomIndex)
    {
        // If the index directly matches a room, use it (handles direct roomID lookups too)
        if (DungeonData.Rooms.ContainsKey(roomIndex))
            return roomIndex;

        // Treat as 1-based index into dungeon's room list
        if (roomIndex >= 1 && roomIndex <= dungeon.RoomIDs.Count)
            return dungeon.RoomIDs[roomIndex - 1];

        // Fall back to first room
        if (dungeon.RoomIDs.Count > 0)
            return dungeon.RoomIDs[0];

        return 0;
    }

    public PyDataType Reset(ServiceCall call)
    {
        Log.Information("[LevelEditor] Reset: char={CharID}", CharacterID);

        if (DungeonData.SpawnedEntities.TryGetValue(CharacterID, out List <int> spawnedList))
        {
            int?       solarSystemID = call.Session.SolarSystemID;
            List <int> removedIDs    = new List<int>();

            foreach (int itemID in spawnedList.ToArray())
            {
                if (solarSystemID != null &&
                    SolarSystemDestinyMgr.TryGet(solarSystemID.Value, out DestinyManager dm))
                    dm.UnregisterEntity(itemID);

                if (Items.TryGetItem(itemID, out ItemEntity item))
                    DogmaItems.DestroyItem(item);

                removedIDs.Add(itemID);
            }

            spawnedList.Clear();

            // Broadcast RemoveBalls so the client removes them from the scene
            if (removedIDs.Count > 0 && solarSystemID != null)
            {
                PyList removeEvents = DestinyEventBuilder.BuildRemoveBalls(removedIDs);
                PyTuple notification = DestinyEventBuilder.WrapAsNotification(removeEvents);

                NotificationSender.SendNotification(
                    "DoDestinyUpdate",
                    "solarsystemid",
                    solarSystemID.Value,
                    notification
                );

                Log.Information("[LevelEditor] Broadcast RemoveBalls for {Count} entities", removedIDs.Count);
            }
        }

        DungeonData.EditingRooms.TryRemove(CharacterID, out _);
        return null;
    }

    public PyDataType GetCurrentlyEditedRoomID(ServiceCall call)
    {
        if (DungeonData.EditingRooms.TryGetValue(CharacterID, out int roomID))
            return new PyInteger(roomID);
        return new PyNone();
    }

    // =====================================================================
    //  ENTITY SPAWNING (same pattern as slash.cs SpawnCmd)
    // =====================================================================

    private void SpawnRoomObjects(Session session, RoomDefinition room)
    {
        int solarSystemID = session.SolarSystemID ?? 0;
        int shipID        = session.ShipID ?? 0;

        if (solarSystemID == 0) return;

        // Get player position as base
        double baseX = 0, baseY = 0, baseZ = 0;
        if (shipID != 0 && SolarSystemDestinyMgr.TryGet(solarSystemID, out DestinyManager dm) &&
            dm.TryGetEntity(shipID, out BubbleEntity shipEnt))
        {
            baseX = shipEnt.Position.X;
            baseY = shipEnt.Position.Y;
            baseZ = shipEnt.Position.Z;
        }

        List <int>          spawnedList    = DungeonData.SpawnedEntities.GetOrAdd(CharacterID, _ => new List<int>());
        List <BubbleEntity> spawnedBubbles = new List<BubbleEntity>();

        foreach (int objID in room.ObjectIDs)
        {
            if (!DungeonData.Objects.TryGetValue(objID, out DungeonObject obj))
                continue;

            if (!Items.Types.ContainsKey(obj.TypeID))
            {
                Log.Warning("[LevelEditor] TypeID {TypeID} not found, skipping", obj.TypeID);
                continue;
            }

            Type type = Items.Types[obj.TypeID];

            double x = baseX + obj.X;
            double y = baseY + obj.Y;
            double z = baseZ + obj.Z;

            ItemEntity newItem = DogmaItems.CreateItem<ItemEntity>(
                type, session.CharacterID, solarSystemID, Flags.None, 1, true);

            newItem.X = x;
            newItem.Y = y;
            newItem.Z = z;
            newItem.Persist();

            if (SolarSystemDestinyMgr.TryGet(solarSystemID, out DestinyManager destinyMgr))
            {
                bool isShipCategory   = type.Group.Category.ID == (int)CategoryID.Ship;
                bool isEntityCategory = type.Group.Category.ID == (int)CategoryID.Entity;
                Vector3  spawnPos         = new Vector3 { X = x, Y = y, Z = z };

                BubbleEntity bubble = new BubbleEntity
                {
                    ItemID        = newItem.ID,
                    TypeID        = type.ID,
                    GroupID       = type.Group.ID,
                    CategoryID    = type.Group.Category.ID,
                    Name          = obj.ObjectName ?? type.Name,
                    OwnerID       = session.CharacterID,
                    CorporationID = session.CorporationID,
                    AllianceID    = 0,
                    CharacterID   = 0,
                    Position      = spawnPos,
                    Velocity      = default (Vector3),
                    Mode          = (isShipCategory || isEntityCategory) ? BallMode.Stop : BallMode.Rigid,
                    Flags = (isShipCategory || isEntityCategory)
                        ? BallFlag.IsFree | BallFlag.IsMassive | BallFlag.IsInteractive
                        : BallFlag.IsGlobal | BallFlag.IsMassive | BallFlag.IsInteractive,
                    Radius        = obj.Radius > 0 ? obj.Radius : type.Radius,
                    Mass          = 1000000.0,
                    MaxVelocity   = (isShipCategory || isEntityCategory) ? 200.0 : 0.0,
                    SpeedFraction = 0.0,
                    Agility       = 1.0,
                    SpawnPosition = spawnPos
                };

                // Set NPC AI properties for Entity-category items
                if (isEntityCategory)
                {
                    // Look up faction from dungeon definition
                    if (DungeonData.Rooms.TryGetValue(room.RoomID, out _) &&
                        DungeonData.Dungeons.TryGetValue(room.DungeonID, out DungeonDefinition dung) &&
                        dung.FactionID != 0)
                    {
                        bubble.FactionID = dung.FactionID;
                    }

                    PopulateNpcAiParams(bubble, newItem);
                }

                destinyMgr.RegisterEntity(bubble);
                spawnedBubbles.Add(bubble);
            }

            spawnedList.Add(newItem.ID);
            Log.Information("[LevelEditor] Spawned: typeID={TypeID} itemID={ItemID} at ({X:F0},{Y:F0},{Z:F0})",
                            obj.TypeID, newItem.ID, x, y, z);
        }

        // Broadcast AddBalls so the client actually renders the new entities
        if (spawnedBubbles.Count > 0)
        {
            int stamp         = DestinyEventBuilder.GetStamp();
            PyList addBallEvents = DestinyEventBuilder.BuildAddBalls(spawnedBubbles, solarSystemID, stamp);
            PyTuple notification  = DestinyEventBuilder.WrapAsNotification(addBallEvents);

            NotificationSender.SendNotification(
                "DoDestinyUpdate",
                "solarsystemid",
                solarSystemID,
                notification
            );

            Log.Information("[LevelEditor] Broadcast AddBalls for {Count} entities in system {SystemID}",
                            spawnedBubbles.Count, solarSystemID);
        }
    }

    /// <summary>
    /// Populate NPC AI parameters from dgmTypeAttributes.
    /// </summary>
    private void PopulateNpcAiParams(BubbleEntity bubble, ItemEntity item)
    {
        AttributeList attrs = item.Attributes;

        if (attrs.AttributeExists(AttributeTypes.entityAttackRange))
            bubble.AttackRange = (double) attrs[AttributeTypes.entityAttackRange];

        if (attrs.AttributeExists(AttributeTypes.entityFlyRange))
            bubble.OrbitRange = (double) attrs[AttributeTypes.entityFlyRange];

        if (attrs.AttributeExists(AttributeTypes.entityChaseMaxDistance))
            bubble.ChaseMaxDistance = (double) attrs[AttributeTypes.entityChaseMaxDistance];

        if (attrs.AttributeExists(AttributeTypes.entityAttackDelayMin))
            bubble.AttackDelayMin = (double) attrs[AttributeTypes.entityAttackDelayMin];

        if (attrs.AttributeExists(AttributeTypes.entityAttackDelayMax))
            bubble.AttackDelayMax = (double) attrs[AttributeTypes.entityAttackDelayMax];

        if (attrs.AttributeExists(AttributeTypes.maxVelocity))
            bubble.MaxVelocity = (double) attrs[AttributeTypes.maxVelocity];

        if (attrs.AttributeExists(AttributeTypes.entityCruiseSpeed))
            bubble.MaxVelocity = (double) attrs[AttributeTypes.entityCruiseSpeed];

        if (attrs.AttributeExists(AttributeTypes.agility))
            bubble.Agility = (double) attrs[AttributeTypes.agility];

        if (attrs.AttributeExists(AttributeTypes.mass))
            bubble.Mass = (double) attrs[AttributeTypes.mass];

        if (attrs.AttributeExists(AttributeTypes.radius))
            bubble.Radius = (double) attrs[AttributeTypes.radius];

        Log.Information("[NpcAI] LevelEditor params for {Name}: attackRange={AttackRange:F0}, orbitRange={OrbitRange:F0}, " +
                        "chaseMax={ChaseMax:F0}, maxVel={MaxVel:F0}, faction={FactionID}",
                        bubble.Name, bubble.AttackRange, bubble.OrbitRange,
                        bubble.ChaseMaxDistance, bubble.MaxVelocity, bubble.FactionID);
    }

    // =====================================================================
    //  ABSTRACT OVERRIDES (required by ClientBoundService)
    // =====================================================================

    protected override long MachoResolveObject(ServiceCall call, ServiceBindParams bindParams)
    {
        return BoundServiceManager.MachoNet.NodeID;
    }

    protected override BoundService CreateBoundInstance(ServiceCall call, ServiceBindParams bindParams)
    {
        // Not used - binding is done manually in keeper.GetLevelEditor
        return this;
    }

    public override bool IsClientAllowedToCall(Session session)
    {
        return true;
    }

    public override void ClientHasReleasedThisObject(Session session)
    {
        OnClientDisconnected();
        BoundServiceManager.UnbindService(this);
    }

    protected override void OnClientDisconnected()
    {
        Log.Information("[LevelEditor] Client disconnected, char={CharID}", CharacterID);
        DungeonData.EditingRooms.TryRemove(CharacterID, out _);
    }

    public override void ApplySessionChange(int characterID, PyDictionary<PyString, PyTuple> changes)
    {
        // no-op
    }

    public override void DestroyService()
    {
        // no-op
    }
}