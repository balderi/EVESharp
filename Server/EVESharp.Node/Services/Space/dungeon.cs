using System;
using System.Collections.Generic;
using System.Linq;
using EVESharp.Database.Account;
using EVESharp.Database.Inventory.Types;
using EVESharp.EVE.Data.Inventory;
using EVESharp.EVE.Network.Services;
using EVESharp.EVE.Network.Services.Validators;
using EVESharp.Types;
using EVESharp.Types.Collections;
using Serilog;
using Type = EVESharp.Database.Inventory.Types.Type;

namespace EVESharp.Node.Services.Space;

[MustBeCharacter]
[MustHaveRole(Roles.ROLE_DUNGEONMASTER)]
public class dungeon : Service
{
    public override AccessLevel AccessLevel => AccessLevel.None;

    private DungeonData DungeonData { get; }
    private IItems      Items       { get; }
    private ILogger     Log         { get; }

    public dungeon(DungeonData dungeonData, IItems items, ILogger logger)
    {
        DungeonData = dungeonData;
        Items       = items;
        Log         = logger;
    }

    // =====================================================================
    //  KWARGS METHODS (zero-arg C# signature, read from NamedPayload)
    // =====================================================================

    public PyDataType DEGetDungeons(ServiceCall call)
    {
        Log.Information("[dungeon] DEGetDungeons called");

        int? archetypeID = null;
        int? factionID   = null;
        int? dungeonID   = null;

        if (call.NamedPayload != null)
        {
            if (call.NamedPayload.TryGetValue("archetypeID", out PyDataType archVal) && archVal is PyInteger archInt)
                archetypeID = (int)archInt.Value;
            if (call.NamedPayload.TryGetValue("factionID", out PyDataType facVal) && facVal is PyInteger facInt)
                factionID = (int)facInt.Value;
            if (call.NamedPayload.TryGetValue("dungeonID", out PyDataType dunVal) && dunVal is PyInteger dunInt)
                dungeonID = (int)dunInt.Value;
        }

        PyList result = new PyList();
        foreach (KeyValuePair <int, DungeonDefinition> kvp in DungeonData.Dungeons)
        {
            DungeonDefinition d = kvp.Value;
            if (archetypeID.HasValue && archetypeID.Value != 0 && d.ArchetypeID != archetypeID.Value)
                continue;
            if (factionID.HasValue && factionID.Value != 0 && d.FactionID != factionID.Value)
                continue;
            if (dungeonID.HasValue && dungeonID.Value != 0 && d.DungeonID != dungeonID.Value)
                continue;

            PyObjectData entry = new PyObjectData("util.KeyVal", new PyDictionary
            {
                ["dungeonID"]   = new PyInteger(d.DungeonID),
                ["dungeonName"] = new PyString(d.DungeonName),
                ["factionID"]   = d.FactionID != 0 ? new PyInteger(d.FactionID) : (PyDataType)new PyNone(),
                ["archetypeID"] = new PyInteger(d.ArchetypeID),
                ["roomCount"]   = new PyInteger(d.RoomIDs.Count),
                ["status"]      = new PyInteger(1)
            });
            result.Add(entry);
        }

        return result;
    }

    public PyDataType GetArchetypes(ServiceCall call)
    {
        Log.Information("[dungeon] GetArchetypes called");
        PyList result = new PyList();
        foreach (KeyValuePair <int, ArchetypeEntry> kvp in DungeonData.Archetypes)
        {
            ArchetypeEntry a = kvp.Value;
            result.Add(new PyObjectData("util.KeyVal", new PyDictionary
            {
                ["archetypeID"]   = new PyInteger(a.ArchetypeID),
                ["archetypeName"] = new PyString(a.ArchetypeName)
            }));
        }
        return result;
    }

    public PyDataType DEGetFactions(ServiceCall call)
    {
        Log.Information("[dungeon] DEGetFactions called");
        PyList result = new PyList();
        foreach (KeyValuePair <int, FactionEntry> kvp in DungeonData.Factions)
        {
            FactionEntry f = kvp.Value;
            result.Add(new PyObjectData("util.KeyVal", new PyDictionary
            {
                ["factionID"]   = new PyInteger(f.FactionID),
                ["factionName"] = new PyString(f.FactionName)
            }));
        }
        return result;
    }

    public PyDataType DEGetRooms(ServiceCall call)
    {
        Log.Information("[dungeon] DEGetRooms called");

        int? dungeonID = null;
        if (call.NamedPayload != null &&
            call.NamedPayload.TryGetValue("dungeonID", out PyDataType dunVal) && dunVal is PyInteger dunInt)
            dungeonID = (int)dunInt.Value;

        PyList result = new PyList();
        foreach (KeyValuePair <int, RoomDefinition> kvp in DungeonData.Rooms)
        {
            RoomDefinition r = kvp.Value;
            if (dungeonID.HasValue && r.DungeonID != dungeonID.Value)
                continue;

            result.Add(new PyObjectData("util.KeyVal", new PyDictionary
            {
                ["roomID"]      = new PyInteger(r.RoomID),
                ["dungeonID"]   = new PyInteger(r.DungeonID),
                ["roomName"]    = new PyString(r.RoomName),
                ["shortName"]   = new PyString(r.ShortName ?? r.RoomName),
                ["objectCount"] = new PyInteger(r.ObjectIDs.Count)
            }));
        }
        return result;
    }

    public PyDataType DEGetTemplates(ServiceCall call)
    {
        Log.Information("[dungeon] DEGetTemplates called");

        PyList header = new PyList
        {
            new PyString("templateID"),
            new PyString("templateName"),
            new PyString("description"),
            new PyString("userID"),
            new PyString("userName")
        };
        PyList lines = new PyList();

        foreach (KeyValuePair <int, DungeonTemplate> kvp in DungeonData.Templates)
        {
            DungeonTemplate t = kvp.Value;
            PyList row = new PyList
            {
                new PyInteger(t.TemplateID),
                new PyString(t.TemplateName),
                new PyString(t.Description ?? ""),
                new PyInteger(t.UserID),
                new PyString(t.UserName ?? "Unknown")
            };
            lines.Add(row);
        }

        return new PyObjectData("util.Rowset", new PyDictionary
        {
            ["header"]   = header,
            ["RowClass"] = new PyToken("util.Row"),
            ["lines"]    = lines
        });
    }

    public PyDataType DEGetRoomObjectPaletteData(ServiceCall call)
    {
        Log.Information("[dungeon] DEGetRoomObjectPaletteData called");

        PyDictionary result = new PyDictionary();

        // Curated group IDs for dungeon palette
        int[] paletteGroupIDs = {
            12,   // Cargo Container
            226,  // Beacon
            227,  // Large Collidable Object
            319,  // Billboard
            397,  // Assembly Array
            448,  // Mining Laser
            450,  // Cloud
            711,  // Jump Portal Array
            784,  // Asteroid (Veldspar)
            790,  // Asteroid (Scordite)
        };

        Dictionary <int, List <(int typeID, string typeName, string groupName)>> groupedTypes = new Dictionary<int, List<(int typeID, string typeName, string groupName)>>();

        foreach (KeyValuePair <int, Type> kvp in Items.Types)
        {
            Type type = kvp.Value;
            if (type.Group == null) continue;
            int gid = type.Group.ID;
            if (!((IList<int>)paletteGroupIDs).Contains(gid)) continue;
            if (!type.Published) continue;

            if (!groupedTypes.TryGetValue(gid, out List <(int typeID, string typeName, string groupName)> list))
            {
                list              = new List<(int, string, string)>();
                groupedTypes[gid] = list;
            }
            list.Add((type.ID, type.Name, type.Group.Name));
        }

        foreach (KeyValuePair <int, List <(int typeID, string typeName, string groupName)>> kvp in groupedTypes)
        {
            int                                                    gid  = kvp.Key;
            List <(int typeID, string typeName, string groupName)> list = kvp.Value;
            if (list.Count == 0) continue;

            string groupName = list[0].groupName;
            PyTuple    key       = new PyTuple(2) { [0] = new PyInteger(gid), [1] = new PyString(groupName) };
            PyList    typeList  = new PyList();

            foreach ((int typeID, string typeName, string _) in list.OrderBy(x => x.typeName))
            {
                typeList.Add(new PyTuple(2)
                {
                    [0] = new PyInteger(typeID),
                    [1] = new PyString(typeName)
                });
            }

            result[key] = typeList;
        }

        return result;
    }

    // =====================================================================
    //  KWARGS EDIT METHODS (zero-arg signature)
    // =====================================================================

    public PyDataType EditObjectXYZ(ServiceCall call)
    {
        Log.Information("[dungeon] EditObjectXYZ called");
        if (call.NamedPayload == null) return null;

        if (call.NamedPayload.TryGetValue("objectID", out PyDataType objVal) && objVal is PyInteger objInt)
        {
            int objectID = (int)objInt.Value;
            if (DungeonData.Objects.TryGetValue(objectID, out DungeonObject obj))
            {
                if (call.NamedPayload.TryGetValue("x", out PyDataType xVal) && xVal is PyDecimal xDec)
                    obj.X = xDec.Value;
                if (call.NamedPayload.TryGetValue("y", out PyDataType yVal) && yVal is PyDecimal yDec)
                    obj.Y = yDec.Value;
                if (call.NamedPayload.TryGetValue("z", out PyDataType zVal) && zVal is PyDecimal zDec)
                    obj.Z = zDec.Value;
            }
        }
        return null;
    }

    public PyDataType EditObjectYawPitchRoll(ServiceCall call)
    {
        Log.Information("[dungeon] EditObjectYawPitchRoll called");
        if (call.NamedPayload == null) return null;

        if (call.NamedPayload.TryGetValue("objectID", out PyDataType objVal) && objVal is PyInteger objInt)
        {
            int objectID = (int)objInt.Value;
            if (DungeonData.Objects.TryGetValue(objectID, out DungeonObject obj))
            {
                if (call.NamedPayload.TryGetValue("yaw", out PyDataType yawVal) && yawVal is PyDecimal yawDec)
                    obj.Yaw = yawDec.Value;
                if (call.NamedPayload.TryGetValue("pitch", out PyDataType pitchVal) && pitchVal is PyDecimal pitchDec)
                    obj.Pitch = pitchDec.Value;
                if (call.NamedPayload.TryGetValue("roll", out PyDataType rollVal) && rollVal is PyDecimal rollDec)
                    obj.Roll = rollDec.Value;
            }
        }
        return null;
    }

    public PyDataType EditObjectRadius(ServiceCall call)
    {
        Log.Information("[dungeon] EditObjectRadius called");
        if (call.NamedPayload == null) return null;

        if (call.NamedPayload.TryGetValue("objectID", out PyDataType objVal) && objVal is PyInteger objInt)
        {
            int objectID = (int)objInt.Value;
            if (DungeonData.Objects.TryGetValue(objectID, out DungeonObject obj))
            {
                if (call.NamedPayload.TryGetValue("radius", out PyDataType radVal) && radVal is PyDecimal radDec)
                    obj.Radius = radDec.Value;
            }
        }
        return null;
    }

    // =====================================================================
    //  POSITIONAL METHODS
    // =====================================================================

    public PyDataType IsObjectLocked(ServiceCall call, PyInteger objectID)
    {
        int objID = (int)objectID.Value;
        Log.Information("[dungeon] IsObjectLocked: objectID={ObjectID}", objID);

        bool locked   = false;
        PyList  lockedBy = new PyList();

        if (DungeonData.Objects.TryGetValue(objID, out DungeonObject obj))
            locked = obj.IsLocked;

        return new PyTuple(2) { [0] = new PyBool(locked), [1] = lockedBy };
    }

    public PyDataType CopyObject(ServiceCall call, PyInteger objectID, PyInteger roomID,
                                 PyDecimal   x,    PyDecimal y,        PyDecimal z)
    {
        int srcID        = (int)objectID.Value;
        int targetRoomID = (int)roomID.Value;
        Log.Information("[dungeon] CopyObject: src={SrcID}, room={RoomID}", srcID, targetRoomID);

        if (!DungeonData.Objects.TryGetValue(srcID, out DungeonObject srcObj))
            return new PyInteger(0);

        int newID = DungeonData.NextObjectID();
        DungeonObject newObj = new DungeonObject
        {
            ObjectID   = newID,
            RoomID     = targetRoomID,
            ObjectName = srcObj.ObjectName + " (copy)",
            TypeID     = srcObj.TypeID,
            X          = x?.Value ?? srcObj.X,
            Y          = y?.Value ?? srcObj.Y,
            Z          = z?.Value ?? srcObj.Z,
            Yaw        = srcObj.Yaw,
            Pitch      = srcObj.Pitch,
            Roll       = srcObj.Roll,
            Radius     = srcObj.Radius
        };
        DungeonData.Objects[newID] = newObj;

        if (DungeonData.Rooms.TryGetValue(targetRoomID, out RoomDefinition room))
            room.ObjectIDs.Add(newID);

        return new PyInteger(newID);
    }

    public PyDataType AddObject(ServiceCall call,   PyInteger roomID, PyString  objectName,
                                PyInteger   typeID, PyDecimal x,      PyDecimal y,    PyDecimal z,
                                PyDecimal   yaw,    PyDecimal pitch,  PyDecimal roll, PyDecimal radius)
    {
        int rID = (int)roomID.Value;
        int tID = (int)typeID.Value;
        Log.Information("[dungeon] AddObject: room={RoomID}, type={TypeID}, name={Name}", rID, tID, objectName?.Value);

        int newID = DungeonData.NextObjectID();
        DungeonObject obj = new DungeonObject
        {
            ObjectID   = newID,
            RoomID     = rID,
            ObjectName = objectName?.Value ?? "Object",
            TypeID     = tID,
            X          = x?.Value ?? 0,
            Y          = y?.Value ?? 0,
            Z          = z?.Value ?? 0,
            Yaw        = yaw?.Value ?? 0,
            Pitch      = pitch?.Value ?? 0,
            Roll       = roll?.Value ?? 0,
            Radius     = radius?.Value ?? 100
        };
        DungeonData.Objects[newID] = obj;

        if (DungeonData.Rooms.TryGetValue(rID, out RoomDefinition room))
            room.ObjectIDs.Add(newID);

        return new PyInteger(newID);
    }

    public PyDataType RemoveObject(ServiceCall call, PyInteger objectID)
    {
        int objID = (int)objectID.Value;
        Log.Information("[dungeon] RemoveObject: objectID={ObjectID}", objID);

        if (DungeonData.Objects.TryRemove(objID, out DungeonObject removed))
        {
            if (DungeonData.Rooms.TryGetValue(removed.RoomID, out RoomDefinition room))
                room.ObjectIDs.Remove(objID);
        }
        return null;
    }

    public PyDataType TemplateAdd(ServiceCall call, PyString templateName, PyString description)
    {
        Log.Information("[dungeon] TemplateAdd: name={Name}", templateName?.Value);
        int newID = DungeonData.NextTemplateID();
        DungeonTemplate template = new DungeonTemplate
        {
            TemplateID   = newID,
            TemplateName = templateName?.Value ?? "Template",
            Description  = description?.Value ?? "",
            UserID       = call.Session.CharacterID,
            UserName     = "GM"
        };
        DungeonData.Templates[newID] = template;
        return new PyInteger(newID);
    }

    public PyDataType TemplateEdit(ServiceCall call, PyInteger templateID, PyString templateName, PyString description)
    {
        int tID = (int)templateID.Value;
        Log.Information("[dungeon] TemplateEdit: templateID={TemplateID}", tID);

        if (DungeonData.Templates.TryGetValue(tID, out DungeonTemplate t))
        {
            if (templateName != null) t.TemplateName = templateName.Value;
            if (description != null) t.Description   = description.Value;
        }
        return null;
    }

    public PyDataType TemplateRemove(ServiceCall call, PyInteger templateID)
    {
        int tID = (int)templateID.Value;
        Log.Information("[dungeon] TemplateRemove: templateID={TemplateID}", tID);
        DungeonData.Templates.TryRemove(tID, out _);
        return null;
    }

    public PyDataType TemplateObjectAddDungeonList(ServiceCall call, PyInteger templateID, PyList objectIDs)
    {
        int tID = (int)templateID.Value;
        Log.Information("[dungeon] TemplateObjectAddDungeonList: templateID={TemplateID}", tID);

        if (DungeonData.Templates.TryGetValue(tID, out DungeonTemplate t) && objectIDs != null)
        {
            foreach (PyDataType item in objectIDs)
            {
                if (item is PyInteger objInt)
                    t.ObjectIDs.Add((int)objInt.Value);
            }
        }
        return null;
    }

    public PyDataType AddTemplateObjects(ServiceCall call, PyInteger templateID, PyInteger roomID, PyTuple position)
    {
        int tID = (int)templateID.Value;
        int rID = (int)roomID.Value;
        Log.Information("[dungeon] AddTemplateObjects: templateID={TemplateID}, roomID={RoomID}", tID, rID);

        double baseX = 0, baseY = 0, baseZ = 0;
        if (position != null && position.Count >= 3)
        {
            if (position[0] is PyDecimal px) baseX = px.Value;
            if (position[1] is PyDecimal py) baseY = py.Value;
            if (position[2] is PyDecimal pz) baseZ = pz.Value;
        }

        PyList newObjectIDs = new PyList();

        if (DungeonData.Templates.TryGetValue(tID, out DungeonTemplate template))
        {
            foreach (int srcObjID in template.ObjectIDs)
            {
                if (!DungeonData.Objects.TryGetValue(srcObjID, out DungeonObject srcObj))
                    continue;

                int newID = DungeonData.NextObjectID();
                DungeonObject newObj = new DungeonObject
                {
                    ObjectID   = newID,
                    RoomID     = rID,
                    ObjectName = srcObj.ObjectName,
                    TypeID     = srcObj.TypeID,
                    X          = baseX + srcObj.X,
                    Y          = baseY + srcObj.Y,
                    Z          = baseZ + srcObj.Z,
                    Yaw        = srcObj.Yaw,
                    Pitch      = srcObj.Pitch,
                    Roll       = srcObj.Roll,
                    Radius     = srcObj.Radius
                };
                DungeonData.Objects[newID] = newObj;

                if (DungeonData.Rooms.TryGetValue(rID, out RoomDefinition room))
                    room.ObjectIDs.Add(newID);

                newObjectIDs.Add(new PyInteger(newID));
            }
        }

        return newObjectIDs;
    }
}