using System;
using EVESharp.Destiny;
using EVESharp.EVE.Notifications;
using EVESharp.Types;
using EVESharp.Types.Collections;
using Serilog;

namespace EVESharp.Node.Services.Space;

/// <summary>
/// Sends DoDestinyUpdate notifications to characters in a solar system.
/// Uses "solarsystemid2" broadcast type to match EVE client routing.
/// </summary>
public class DestinyBroadcaster
{
    private readonly INotificationSender mNotificationSender;
    private readonly ILogger             mLog;

    public DestinyBroadcaster(INotificationSender notificationSender, ILogger logger)
    {
        mNotificationSender = notificationSender;
        mLog                = logger;
    }

    /// <summary>
    /// Send destiny events to all characters in a solar system (bubble broadcast).
    /// </summary>
    public void BroadcastToSystem(int solarSystemID, PyList events)
    {
        if (events == null) return;

        PyTuple notification = DestinyEventBuilder.WrapAsNotification(events);
        SendToSystem(solarSystemID, notification);
    }

    /// <summary>
    /// Send destiny events to a specific character via solar system broadcast.
    /// </summary>
    public void SendToCharacterInSystem(int solarSystemID, PyList events)
    {
        if (events == null) return;

        PyTuple notification = DestinyEventBuilder.WrapAsNotification(events);
        SendToSystem(solarSystemID, notification);
    }

    /// <summary>
    /// Broadcast an OnTarget event to the entire solar system (used for NPC targeting indicators).
    /// </summary>
    public void BroadcastOnTarget(int solarSystemID, int attackerID, int targetID, string reason)
    {
        try
        {
            PyTuple eventEntry = new PyTuple(4)
            {
                [0] = new PyString("OnTarget"),
                [1] = new PyString(reason),
                [2] = new PyInteger(targetID),
                [3] = new PyInteger(attackerID)
            };

            SendScatterEvent(solarSystemID, eventEntry);
        }
        catch (Exception ex)
        {
            mLog.Error(ex, "[DestinyBroadcaster] Error sending OnTarget in system {SolarSystemID}: {Message}", solarSystemID, ex.Message);
        }
    }

    /// <summary>
    /// Send a targeted OnTarget notification to a specific character.
    /// Used for player-to-player targeting: "otheradd" when someone locks you,
    /// "otherlost" when someone unlocks you.
    /// The attacker handles their own lock from the AddTarget return value — no broadcast needed.
    /// </summary>
    public void SendOnTargetToCharacter(int charID, string what, int tid)
    {
        try
        {
            // OnTarget(what, tid) — two positional args for the client handler
            PyTuple data = new PyTuple(2)
            {
                [0] = new PyString(what),
                [1] = new PyInteger(tid)
            };

            mNotificationSender.SendNotification("OnTarget", "charid", charID, data);
            mLog.Information("[DestinyBroadcaster] OnTarget '{What}' sent to char {CharID}, tid={TID}", what, charID, tid);
        }
        catch (Exception ex)
        {
            mLog.Error(ex, "[DestinyBroadcaster] Error sending targeted OnTarget to char {CharID}: {Message}", charID, ex.Message);
        }
    }

    /// <summary>
    /// Broadcast an OnSpecialFX for NPC weapon fire so the client shows attack visuals.
    /// Sent as a destiny event in slot[0] of DoDestinyUpdate — michelle.py RealFlushState
    /// dispatches OnSpecialFX from the state list, NOT from dogmaMessages or ScatterEvent.
    /// </summary>
    public void BroadcastNpcAttackFX(int solarSystemID, int npcID, int npcTypeID, int targetID)
    {
        try
        {
            long startTime = DateTime.UtcNow.ToFileTimeUtc();

            PyList events = DestinyEventBuilder.BuildOnSpecialFX(
                shipID: npcID,
                moduleID: 0,
                moduleTypeID: npcTypeID,
                targetID: targetID,
                otherTypeID: new PyNone(),    // no charge type
                guid: "effects.Laser",
                isOffensive: true,
                start: true,
                active: true,
                durationMs: 3000,
                repeat: 1,
                startTime: startTime
            );

            BroadcastToSystem(solarSystemID, events);
        }
        catch (Exception ex)
        {
            mLog.Error(ex, "[DestinyBroadcaster] Error sending NPC attack FX in system {SolarSystemID}: {Message}", solarSystemID, ex.Message);
        }
    }

    /// <summary>
    /// Broadcast an OnSpecialFX for player weapon fire so the client renders turret beams / projectiles / missiles.
    /// Sent as a destiny event in slot[0] of DoDestinyUpdate — michelle.py RealFlushState
    /// dispatches OnSpecialFX from the state list, NOT from dogmaMessages or ScatterEvent.
    /// </summary>
    public void BroadcastPlayerAttackFX(int solarSystemID, int shipID, int moduleID,
                                        int moduleTypeID, int targetID, int chargeTypeID, string effectGuid, double durationMs)
    {
        try
        {
            long startTime = DateTime.UtcNow.ToFileTimeUtc();

            PyList events = DestinyEventBuilder.BuildOnSpecialFX(
                shipID: shipID,
                moduleID: moduleID,
                moduleTypeID: moduleTypeID,
                targetID: targetID,
                otherTypeID: new PyInteger(chargeTypeID),
                guid: effectGuid,
                isOffensive: true,
                start: true,
                active: true,
                durationMs: (long) durationMs,
                repeat: 1,
                startTime: startTime
            );

            BroadcastToSystem(solarSystemID, events);
        }
        catch (Exception ex)
        {
            mLog.Error(ex, "[DestinyBroadcaster] Error sending player attack FX in system {SolarSystemID}: {Message}", solarSystemID, ex.Message);
        }
    }

    /// <summary>
    /// Broadcast OnDamageStateChange so clients update HP bars for the targeted entity.
    /// Sent as a destiny event in slot[0] of DoDestinyUpdate — michelle.py RealFlushState
    /// dispatches OnDamageStateChange from the state list, NOT from dogmaMessages or ScatterEvent.
    /// </summary>
    public void BroadcastDamageStateChange(int    solarSystemID, int    itemID, double shieldFraction,
                                           double armorFraction, double hullFraction)
    {
        try
        {
            PyList events = DestinyEventBuilder.BuildOnDamageStateChange(
                shipID: itemID,
                shieldFrac: shieldFraction,
                armorFrac: armorFraction,
                hullFrac: hullFraction
            );

            BroadcastToSystem(solarSystemID, events);
        }
        catch (Exception ex)
        {
            mLog.Error(ex, "[DestinyBroadcaster] Error sending OnDamageStateChange in system {SolarSystemID}: {Message}", solarSystemID, ex.Message);
        }
    }

    /// <summary>
    /// Send a scatter event as a dogmaMessage inside a DoDestinyUpdate notification.
    /// The client's michelle.py FlushState processes DoDestinyUpdate as (events, waitForBubble, dogmaMessages).
    /// The dogmaMessages list contains scatter events that the client dispatches via sm.ScatterEvent.
    /// This is the correct channel for OnSpecialFX and similar space-scene VFX events.
    /// </summary>
    private void SendDogmaMessage(int solarSystemID, PyTuple eventEntry)
    {
        PyList dogmaMessages = new PyList();
        dogmaMessages.Add(eventEntry);

        PyTuple notification = new PyTuple(3)
        {
            [0] = new PyList(),           // no destiny ball events
            [1] = new PyBool(false),      // waitForBubble
            [2] = dogmaMessages           // scatter events for the space scene
        };

        try
        {
            mNotificationSender.SendNotification(
                "DoDestinyUpdate",
                "solarsystemid",
                solarSystemID,
                notification
            );
        }
        catch (Exception ex)
        {
            mLog.Error(ex, "[DestinyBroadcaster] Error sending dogma message to system {SolarSystemID}: {Message}", solarSystemID, ex.Message);
        }
    }

    /// <summary>
    /// Send a ScatterEvent-type notification by wrapping it inside OnMultiEvent.
    /// The client's OnMultiEvent handler unpacks the event list and dispatches each
    /// entry via sm.ScatterEvent(name, *args), which gives handlers individual positional args.
    /// </summary>
    private void SendScatterEvent(int solarSystemID, PyTuple eventEntry)
    {
        // Wrap: PyTuple(1){ PyList{ eventEntry } }
        PyList eventList = new PyList(1) { [0]  = eventEntry };
        PyTuple wrapped   = new PyTuple(1) { [0] = eventList };

        mNotificationSender.SendNotification(
            "OnMultiEvent",
            "solarsystemid",
            solarSystemID,
            wrapped
        );
    }

    /// <summary>
    /// Send OnDockingAccepted to a specific character so the client plays the docking fly-in animation.
    /// Must be sent BEFORE the session change (while the client still has a ballpark).
    /// </summary>
    public void BroadcastOnDockingAccepted(int    charID, double startX, double startY, double startZ,
                                           double endX,   double endY,   double endZ,   int    stationID)
    {
        try
        {
            PyTuple startPos = new PyTuple(3)
            {
                [0] = new PyDecimal(startX),
                [1] = new PyDecimal(startY),
                [2] = new PyDecimal(startZ)
            };

            PyTuple endPos = new PyTuple(3)
            {
                [0] = new PyDecimal(endX),
                [1] = new PyDecimal(endY),
                [2] = new PyDecimal(endZ)
            };

            PyTuple data = new PyTuple(3)
            {
                [0] = startPos,
                [1] = endPos,
                [2] = new PyInteger(stationID)
            };

            mNotificationSender.SendNotification("OnDockingAccepted", "charid", charID, data);
            mLog.Information("[DestinyBroadcaster] OnDockingAccepted sent to char {CharID} for station {StationID}", charID, stationID);
        }
        catch (Exception ex)
        {
            mLog.Error(ex, "[DestinyBroadcaster] Error sending OnDockingAccepted: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Broadcast OnSlimItemChange when an entity's properties change in space.
    /// Updates brackets, overview, and tactical overlay for all clients in the system.
    /// </summary>
    public void BroadcastOnSlimItemChange(int solarSystemID, int itemID, PyObjectData newSlim)
    {
        try
        {
            PyList events = DestinyEventBuilder.BuildOnSlimItemChange(itemID, newSlim);
            BroadcastToSystem(solarSystemID, events);
            mLog.Information("[DestinyBroadcaster] OnSlimItemChange sent for item {ItemID} in system {SolarSystemID}", itemID, solarSystemID);
        }
        catch (Exception ex)
        {
            mLog.Error(ex, "[DestinyBroadcaster] Error sending OnSlimItemChange: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Broadcast OnAggressionChange when aggression state changes (PvP timer, weapon timer).
    /// </summary>
    public void BroadcastOnAggressionChange(int solarSystemID, PyDictionary aggressors)
    {
        try
        {
            PyTuple eventEntry = new PyTuple(3)
            {
                [0] = new PyString("OnAggressionChange"),
                [1] = new PyInteger(solarSystemID),
                [2] = aggressors
            };

            SendScatterEvent(solarSystemID, eventEntry);
            mLog.Information("[DestinyBroadcaster] OnAggressionChange sent in system {SolarSystemID}", solarSystemID);
        }
        catch (Exception ex)
        {
            mLog.Error(ex, "[DestinyBroadcaster] Error sending OnAggressionChange: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Broadcast OnDroneStateChange when a drone's activity state changes.
    /// </summary>
    public void BroadcastOnDroneStateChange(int solarSystemID, int droneID, int ownerID,           int controllerID,
                                            int activityState, int typeID,  int controllerOwnerID, int targetID)
    {
        try
        {
            PyTuple eventEntry = new PyTuple(8)
            {
                [0] = new PyString("OnDroneStateChange"),
                [1] = new PyInteger(droneID),
                [2] = new PyInteger(ownerID),
                [3] = new PyInteger(controllerID),
                [4] = new PyInteger(activityState),
                [5] = new PyInteger(typeID),
                [6] = new PyInteger(controllerOwnerID),
                [7] = new PyInteger(targetID)
            };

            SendScatterEvent(solarSystemID, eventEntry);
        }
        catch (Exception ex)
        {
            mLog.Error(ex, "[DestinyBroadcaster] Error sending OnDroneStateChange: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Broadcast OnEwarStart when an electronic warfare effect begins on a target.
    /// </summary>
    public void BroadcastOnEwarStart(int solarSystemID, int sourceID, int moduleID, int targetID, string ewarType)
    {
        try
        {
            PyTuple eventEntry = new PyTuple(5)
            {
                [0] = new PyString("OnEwarStart"),
                [1] = new PyInteger(sourceID),
                [2] = new PyInteger(moduleID),
                [3] = new PyInteger(targetID),
                [4] = new PyString(ewarType)
            };

            SendScatterEvent(solarSystemID, eventEntry);
        }
        catch (Exception ex)
        {
            mLog.Error(ex, "[DestinyBroadcaster] Error sending OnEwarStart: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Broadcast OnEwarEnd when an electronic warfare effect ends on a target.
    /// </summary>
    public void BroadcastOnEwarEnd(int solarSystemID, int sourceID, int moduleID, int targetID, string ewarType)
    {
        try
        {
            PyTuple eventEntry = new PyTuple(5)
            {
                [0] = new PyString("OnEwarEnd"),
                [1] = new PyInteger(sourceID),
                [2] = new PyInteger(moduleID),
                [3] = new PyInteger(targetID),
                [4] = new PyString(ewarType)
            };

            SendScatterEvent(solarSystemID, eventEntry);
        }
        catch (Exception ex)
        {
            mLog.Error(ex, "[DestinyBroadcaster] Error sending OnEwarEnd: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Broadcast OnJamStart when a jamming effect (ECM, web, etc.) begins on a target.
    /// </summary>
    public void BroadcastOnJamStart(int    solarSystemID, int  sourceID,  int moduleID, int targetID,
                                    string jammingType,   long startTime, int duration)
    {
        try
        {
            PyTuple eventEntry = new PyTuple(7)
            {
                [0] = new PyString("OnJamStart"),
                [1] = new PyInteger(sourceID),
                [2] = new PyInteger(moduleID),
                [3] = new PyInteger(targetID),
                [4] = new PyString(jammingType),
                [5] = new PyInteger(startTime),
                [6] = new PyInteger(duration)
            };

            SendScatterEvent(solarSystemID, eventEntry);
        }
        catch (Exception ex)
        {
            mLog.Error(ex, "[DestinyBroadcaster] Error sending OnJamStart: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Broadcast OnJamEnd when a jamming effect ends on a target.
    /// </summary>
    public void BroadcastOnJamEnd(int solarSystemID, int sourceID, int moduleID, int targetID, string jammingType)
    {
        try
        {
            PyTuple eventEntry = new PyTuple(5)
            {
                [0] = new PyString("OnJamEnd"),
                [1] = new PyInteger(sourceID),
                [2] = new PyInteger(moduleID),
                [3] = new PyInteger(targetID),
                [4] = new PyString(jammingType)
            };

            SendScatterEvent(solarSystemID, eventEntry);
        }
        catch (Exception ex)
        {
            mLog.Error(ex, "[DestinyBroadcaster] Error sending OnJamEnd: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Broadcast TerminalExplosion before RemoveBalls so clients play explosion FX.
    /// </summary>
    public void BroadcastTerminalExplosion(int solarSystemID, int ballID)
    {
        try
        {
            PyList events = DestinyEventBuilder.BuildTerminalExplosion(ballID, 0);
            BroadcastToSystem(solarSystemID, events);
            mLog.Information("[DestinyBroadcaster] TerminalExplosion sent for ball {BallID} in system {SolarSystemID}", ballID, solarSystemID);
        }
        catch (Exception ex)
        {
            mLog.Error(ex, "[DestinyBroadcaster] Error sending TerminalExplosion: {Message}", ex.Message);
        }
    }

    private void SendToSystem(int solarSystemID, PyTuple notification)
    {
        try
        {
            // Use "solarsystemid" (not "solarsystemid2") so docked clients don't
            // receive destiny updates — they have no ballpark and would crash.
            // "solarsystemid" is null when docked, set when in space.
            mNotificationSender.SendNotification(
                "DoDestinyUpdate",
                "solarsystemid",
                solarSystemID,
                notification
            );
        }
        catch (Exception ex)
        {
            mLog.Error(ex, "[DestinyBroadcaster] Error sending to system {SolarSystemID}: {Message}", solarSystemID, ex.Message);
        }
    }
}