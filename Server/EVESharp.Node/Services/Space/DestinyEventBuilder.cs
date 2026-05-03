using System;
using System.Collections.Generic;
using EVESharp.Destiny;
using EVESharp.Types;
using EVESharp.Types.Collections;

namespace EVESharp.Node.Services.Space;

/// <summary>
/// Static builder for DoDestiny_* event tuples.
/// Each method returns a PyList of (stamp, (methodName, (args...))) tuples
/// that can be wrapped and sent as DoDestinyUpdate notifications.
/// </summary>
public static class DestinyEventBuilder
{
    public static int GetStamp()
    {
        long eveEpoch = new DateTime(2003, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;
        return (int)((DateTime.UtcNow.Ticks - eveEpoch) / 10000000 % int.MaxValue);
    }

    public static PyList BuildStop(int ballID)
    {
        return BuildEvent("SetBallMass", new PyTuple(2)
        {
            [0] = new PyInteger(ballID),
            [1] = new PyInteger(0) // placeholder - Stop is handled via binary state
        });
    }

    public static PyList BuildGotoPoint(int ballID, double x, double y, double z)
    {
        return BuildEvent("GotoPoint", new PyTuple(4)
        {
            [0] = new PyInteger(ballID),
            [1] = new PyDecimal(x),
            [2] = new PyDecimal(y),
            [3] = new PyDecimal(z)
        });
    }

    public static PyList BuildFollowBall(int ballID, int targetBallID, float range)
    {
        return BuildEvent("FollowBall", new PyTuple(3)
        {
            [0] = new PyInteger(ballID),
            [1] = new PyInteger(targetBallID),
            [2] = new PyDecimal(range)
        });
    }

    public static PyList BuildOrbit(int ballID, int targetBallID, float range)
    {
        return BuildEvent("Orbit", new PyTuple(3)
        {
            [0] = new PyInteger(ballID),
            [1] = new PyInteger(targetBallID),
            [2] = new PyDecimal(range)
        });
    }

    public static PyList BuildWarpTo(int ballID, double x, double y, double z, double speed, int effectStamp)
    {
        return BuildEvent("WarpTo", new PyTuple(6)
        {
            [0] = new PyInteger(ballID),
            [1] = new PyDecimal(x),
            [2] = new PyDecimal(y),
            [3] = new PyDecimal(z),
            [4] = new PyDecimal(speed),
            [5] = new PyInteger(effectStamp)
        });
    }

    public static PyList BuildSetSpeedFraction(int ballID, double fraction)
    {
        return BuildEvent("SetSpeedFraction", new PyTuple(2)
        {
            [0] = new PyInteger(ballID),
            [1] = new PyDecimal(fraction)
        });
    }

    public static PyList BuildSetBallVelocity(int ballID, double vx, double vy, double vz)
    {
        return BuildEvent("SetBallVelocity", new PyTuple(4)
        {
            [0] = new PyInteger(ballID),
            [1] = new PyDecimal(vx),
            [2] = new PyDecimal(vy),
            [3] = new PyDecimal(vz)
        });
    }

    /// <summary>
    /// Build an AddBalls event containing destiny binary + slims for new arrivals.
    /// </summary>
    public static PyList BuildAddBalls(IEnumerable<BubbleEntity> entities, int solarSystemID, int stamp)
    {
        List <Ball> balls      = new List<Ball>();
        PyList     slims      = new PyList();
        PyDictionary     damageDict = new PyDictionary();

        foreach (BubbleEntity ent in entities)
        {
            balls.Add(ent.ToBall());
            slims.Add(BuildSlimFromEntity(ent, solarSystemID));

            // Add damage state: ((shieldFrac, tau), armorFrac, hullFrac)
            // Must match beyonce.MakeDamageEntry and OnDamageStateChange format
            PyTuple shieldTuple = new PyTuple(2)
            {
                [0] = new PyDecimal(ent.ShieldFraction),
                [1] = new PyDecimal(1e20)
            };
            damageDict[new PyInteger(ent.ItemID)] = new PyTuple(3)
            {
                [0] = shieldTuple,
                [1] = new PyDecimal(ent.ArmorFraction),
                [2] = new PyDecimal(ent.HullFraction)
            };
        }

        // packetType=0: The binary is a full-state snapshot of these new balls.
        // The "incremental add" semantic comes from the event name "AddBalls",
        // NOT from the binary packet type. Using type=1 causes some clients
        // to skip the ball data.
        byte[] destinyBinary = DestinyBinaryEncoder.BuildFullState(balls, stamp, 0);

        PyTuple args = new PyTuple(3)
        {
            [0] = new PyBuffer(destinyBinary),
            [1] = slims,
            [2] = damageDict
        };

        // CRITICAL: pass the same stamp used for the destiny binary.
        // The client may validate that the event stamp matches the binary stamp.
        return BuildEvent("AddBalls", args, stamp);
    }

    /// <summary>
    /// Build a RemoveBalls event for entities leaving the bubble.
    /// </summary>
    public static PyList BuildRemoveBalls(IEnumerable<int> ballIDs)
    {
        PyList idList = new PyList();
        foreach (int id in ballIDs)
            idList.Add(new PyInteger(id));

        return BuildEvent("RemoveBalls", new PyTuple(1) { [0] = idList });
    }

    /// <summary>
    /// Wrap a list of events into the full DoDestinyUpdate notification format:
    /// PyTuple(3) { events, waitForBubble, dogmaMessages }
    /// </summary>
    public static PyTuple WrapAsNotification(PyList events)
    {
        return new PyTuple(3)
        {
            [0] = events,
            [1] = new PyBool(false),
            [2] = new PyList()
        };
    }

    // =====================================================================
    //  BALL PROPERTY EVENTS
    //  michelle.py RealFlushState() scatterEvents dict expects these
    // =====================================================================

    public static PyList BuildSetBallRadius(int ballID, double radius)
    {
        return BuildEvent("SetBallRadius", new PyTuple(2)
        {
            [0] = new PyInteger(ballID),
            [1] = new PyDecimal(radius)
        });
    }

    public static PyList BuildSetBallInteractive(int ballID, bool interactive)
    {
        return BuildEvent("SetBallInteractive", new PyTuple(2)
        {
            [0] = new PyInteger(ballID),
            [1] = new PyInteger(interactive ? 1 : 0)
        });
    }

    public static PyList BuildSetBallFree(int ballID, bool isFree)
    {
        return BuildEvent("SetBallFree", new PyTuple(2)
        {
            [0] = new PyInteger(ballID),
            [1] = new PyInteger(isFree ? 1 : 0)
        });
    }

    public static PyList BuildSetBallHarmonic(int ballID, double harmonic)
    {
        return BuildEvent("SetBallHarmonic", new PyTuple(2)
        {
            [0] = new PyInteger(ballID),
            [1] = new PyDecimal(harmonic)
        });
    }

    public static PyList BuildTerminalExplosion(int ballID, int bubbleID)
    {
        return BuildEvent("TerminalExplosion", new PyTuple(2)
        {
            [0] = new PyInteger(ballID),
            [1] = new PyInteger(bubbleID)
        });
    }

    public static PyList BuildGotoDirection(int ballID, double x, double y, double z)
    {
        return BuildEvent("GotoDirection", new PyTuple(4)
        {
            [0] = new PyInteger(ballID),
            [1] = new PyDecimal(x),
            [2] = new PyDecimal(y),
            [3] = new PyDecimal(z)
        });
    }

    public static PyList BuildOnSlimItemChange(int itemID, PyObjectData newSlim)
    {
        return BuildEvent("OnSlimItemChange", new PyTuple(2)
        {
            [0] = new PyInteger(itemID),
            [1] = newSlim
        });
    }

    // =====================================================================
    //  COMBAT / VFX EVENTS
    //  michelle.py RealFlushState() dispatches OnSpecialFX and
    //  OnDamageStateChange from destiny events in slot[0] (state list).
    // =====================================================================

    public static PyList BuildOnSpecialFX(int  shipID,   int        moduleID, int moduleTypeID,
                                          int  targetID, PyDataType otherTypeID, string guid, bool isOffensive,
                                          bool start,    bool       active, long durationMs, int repeat, long startTime)
    {
        return BuildEvent("OnSpecialFX", new PyTuple(13)
        {
            [0]  = new PyInteger(shipID),
            [1]  = new PyInteger(moduleID),
            [2]  = new PyInteger(moduleTypeID),
            [3]  = new PyInteger(targetID),
            [4]  = otherTypeID,              // charge typeID or PyNone
            [5]  = new PyList(),             // area
            [6]  = new PyString(guid),
            [7]  = new PyBool(isOffensive),
            [8]  = new PyBool(start),
            [9]  = new PyBool(active),
            [10] = new PyInteger(durationMs),
            [11] = new PyInteger(repeat),
            [12] = new PyInteger(startTime)
        });
    }

    public static PyList BuildOnDamageStateChange(int    shipID,
                                                  double shieldFrac, double armorFrac, double hullFrac)
    {
        PyTuple shieldTuple = new PyTuple(2)
        {
            [0] = new PyDecimal(shieldFrac),
            [1] = new PyDecimal(1e20)         // tau (no passive regen)
        };
        PyTuple damageState = new PyTuple(3)
        {
            [0] = shieldTuple,
            [1] = new PyDecimal(armorFrac),
            [2] = new PyDecimal(hullFrac)
        };
        return BuildEvent("OnDamageStateChange", new PyTuple(2)
        {
            [0] = new PyInteger(shipID),
            [1] = damageState
        });
    }

    // =====================================================================
    //  HELPERS
    // =====================================================================

    private static PyList BuildEvent(string methodName, PyTuple args, int? overrideStamp = null)
    {
        int stamp = overrideStamp ?? GetStamp();

        PyTuple innerCall  = new PyTuple(2) { [0] = new PyString(methodName), [1] = args };
        PyTuple eventTuple = new PyTuple(2) { [0] = new PyInteger(stamp),     [1] = innerCall };

        PyList events = new PyList();
        events.Add(eventTuple);
        return events;
    }

    private static PyObjectData BuildSlimFromEntity(BubbleEntity ent, int solarSystemID)
    {
        PyDictionary d = new PyDictionary
        {
            ["itemID"]          = new PyInteger(ent.ItemID),
            ["typeID"]          = new PyInteger(ent.TypeID),
            ["groupID"]         = new PyInteger(ent.GroupID),
            ["ownerID"]         = new PyInteger(ent.OwnerID),
            ["locationID"]      = new PyInteger(solarSystemID),
            ["categoryID"]      = new PyInteger(ent.CategoryID),
            ["name"]            = new PyString(ent.Name ?? "Unknown"),
            ["corpID"]          = new PyInteger(ent.CorporationID),
            ["allianceID"]      = new PyInteger(ent.AllianceID),
            ["charID"]          = new PyInteger(ent.CharacterID),
            ["dunObjectID"]     = new PyNone(),
            ["jumps"]           = new PyList(),
            ["securityStatus"]  = new PyDecimal(0.0),
            ["orbitalVelocity"] = new PyDecimal(0.0),
            ["warFactionID"]    = new PyNone(),
            ["bounty"]          = new PyDecimal(0.0)
        };

        return new PyObjectData("util.KeyVal", d);
    }
}