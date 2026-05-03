using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace EVESharp.Node.Services.Space;

public class DungeonDefinition
{
    public int       DungeonID   { get; set; }
    public string    DungeonName { get; set; }
    public int       FactionID   { get; set; }
    public int       ArchetypeID { get; set; }
    public List<int> RoomIDs     { get; set; } = new List<int>();
}

public class RoomDefinition
{
    public int       RoomID    { get; set; }
    public int       DungeonID { get; set; }
    public string    RoomName  { get; set; }
    public string    ShortName { get; set; }
    public List<int> ObjectIDs { get; set; } = new List<int>();
}

public class DungeonObject
{
    public int    ObjectID   { get; set; }
    public int    RoomID     { get; set; }
    public string ObjectName { get; set; }
    public int    TypeID     { get; set; }
    public double X          { get; set; }
    public double Y          { get; set; }
    public double Z          { get; set; }
    public double Yaw        { get; set; }
    public double Pitch      { get; set; }
    public double Roll       { get; set; }
    public double Radius     { get; set; }
    public bool   IsLocked   { get; set; }
}

public class DungeonTemplate
{
    public int       TemplateID   { get; set; }
    public string    TemplateName { get; set; }
    public string    Description  { get; set; }
    public int       UserID       { get; set; }
    public string    UserName     { get; set; }
    public List<int> ObjectIDs    { get; set; } = new List<int>();
}

public class ArchetypeEntry
{
    public int    ArchetypeID   { get; set; }
    public string ArchetypeName { get; set; }
}

public class FactionEntry
{
    public int    FactionID   { get; set; }
    public string FactionName { get; set; }
}

public class DungeonData
{
    public ConcurrentDictionary<int, DungeonDefinition> Dungeons   { get; } =
        new ConcurrentDictionary <int, DungeonDefinition> ();
    public ConcurrentDictionary<int, RoomDefinition>  Rooms      { get; } =
        new ConcurrentDictionary <int, RoomDefinition> ();
    public ConcurrentDictionary<int, DungeonObject>   Objects    { get; } =
        new ConcurrentDictionary <int, DungeonObject> ();
    public ConcurrentDictionary<int, DungeonTemplate> Templates  { get; } =
        new ConcurrentDictionary <int, DungeonTemplate> ();
    public ConcurrentDictionary<int, ArchetypeEntry> Archetypes { get; } =
        new ConcurrentDictionary <int, ArchetypeEntry> ();
    public ConcurrentDictionary<int, FactionEntry>   Factions   { get; } =
        new ConcurrentDictionary <int, FactionEntry> ();

    // Per-character state
    public ConcurrentDictionary<int, int>       EditingRooms    { get; } = new ConcurrentDictionary <int, int> ();
    public ConcurrentDictionary<int, List<int>> SpawnedEntities { get; } = new ConcurrentDictionary <int, List <int>> ();

    private int _nextDungeonID  = 200;
    private int _nextRoomID     = 2000;
    private int _nextObjectID   = 20000;
    private int _nextTemplateID = 500;

    public int NextDungeonID()  => Interlocked.Increment(ref _nextDungeonID);
    public int NextRoomID()     => Interlocked.Increment(ref _nextRoomID);
    public int NextObjectID()   => Interlocked.Increment(ref _nextObjectID);
    public int NextTemplateID() => Interlocked.Increment(ref _nextTemplateID);

    public DungeonData()
    {
        PopulateSampleData();
    }

    private void PopulateSampleData()
    {
        // Archetypes
        Archetypes[1] = new ArchetypeEntry { ArchetypeID = 1, ArchetypeName = "Combat" };
        Archetypes[2] = new ArchetypeEntry { ArchetypeID = 2, ArchetypeName = "Mining" };
        Archetypes[3] = new ArchetypeEntry { ArchetypeID = 3, ArchetypeName = "Exploration" };
        Archetypes[4] = new ArchetypeEntry { ArchetypeID = 4, ArchetypeName = "Mission" };

        // Factions
        Factions[500001] = new FactionEntry { FactionID = 500001, FactionName = "Caldari State" };
        Factions[500002] = new FactionEntry { FactionID = 500002, FactionName = "Minmatar Republic" };
        Factions[500003] = new FactionEntry { FactionID = 500003, FactionName = "Amarr Empire" };
        Factions[500004] = new FactionEntry { FactionID = 500004, FactionName = "Gallente Federation" };
        Factions[500010] = new FactionEntry { FactionID = 500010, FactionName = "Serpentis" };
        Factions[500011] = new FactionEntry { FactionID = 500011, FactionName = "Angel Cartel" };
        Factions[500012] = new FactionEntry { FactionID = 500012, FactionName = "Blood Raiders" };
        Factions[500013] = new FactionEntry { FactionID = 500013, FactionName = "Guristas" };
        Factions[500014] = new FactionEntry { FactionID = 500014, FactionName = "Sansha's Nation" };

        // Sample dungeon 1: Asteroid Field Alpha
        DungeonObject obj1 = new DungeonObject { ObjectID = 20001, RoomID = 2001, ObjectName = "Veldspar Asteroid 1", TypeID = 1230, X = 5000, Y = 0, Z = 0, Radius = 500 };
        DungeonObject obj2 = new DungeonObject { ObjectID = 20002, RoomID = 2001, ObjectName = "Veldspar Asteroid 2", TypeID = 1230, X = -3000, Y = 1000, Z = 2000, Radius = 400 };
        DungeonObject obj3 = new DungeonObject { ObjectID = 20003, RoomID = 2001, ObjectName = "Scordite Asteroid", TypeID = 1232, X = 0, Y = -2000, Z = 4000, Radius = 600 };
        Objects[20001] = obj1;
        Objects[20002] = obj2;
        Objects[20003] = obj3;

        RoomDefinition room1 = new RoomDefinition
        {
            RoomID    = 2001, DungeonID = 101, RoomName = "Main Field", ShortName = "Main",
            ObjectIDs = new List<int> { 20001, 20002, 20003 }
        };
        Rooms[2001] = room1;

        Dungeons[101] = new DungeonDefinition
        {
            DungeonID = 101, DungeonName = "Asteroid Field Alpha",
            FactionID = 0, ArchetypeID   = 2,
            RoomIDs   = new List<int> { 2001 }
        };

        // Sample dungeon 2: Serpentis Hideout
        DungeonObject obj4 = new DungeonObject { ObjectID = 20004, RoomID = 2002, ObjectName = "Serpentis Sentry", TypeID = 23707, X = 10000, Y = 0, Z = 0, Radius = 100 };
        DungeonObject obj5 = new DungeonObject { ObjectID = 20005, RoomID = 2002, ObjectName = "Serpentis Bunker", TypeID = 12235, X = 0, Y = 0, Z = 5000, Radius = 2000 };
        DungeonObject obj6 = new DungeonObject { ObjectID = 20006, RoomID = 2003, ObjectName = "Serpentis Commander", TypeID = 23707, X = 0, Y = 5000, Z = 0, Radius = 150 };
        Objects[20004] = obj4;
        Objects[20005] = obj5;
        Objects[20006] = obj6;

        RoomDefinition room2 = new RoomDefinition
        {
            RoomID    = 2002, DungeonID = 102, RoomName = "Entrance", ShortName = "Entry",
            ObjectIDs = new List<int> { 20004, 20005 }
        };
        RoomDefinition room3 = new RoomDefinition
        {
            RoomID    = 2003, DungeonID = 102, RoomName = "Boss Chamber", ShortName = "Boss",
            ObjectIDs = new List<int> { 20006 }
        };
        Rooms[2002] = room2;
        Rooms[2003] = room3;

        Dungeons[102] = new DungeonDefinition
        {
            DungeonID = 102, DungeonName    = "Serpentis Hideout",
            FactionID = 500010, ArchetypeID = 1,
            RoomIDs   = new List<int> { 2002, 2003 }
        };

        // Sample template
        Templates[501] = new DungeonTemplate
        {
            TemplateID  = 501, TemplateName = "Basic Asteroid Cluster",
            Description = "A small cluster of 3 asteroids",
            UserID      = 0, UserName = "System",
            ObjectIDs   = new List<int> { 20001, 20002, 20003 }
        };
    }
}