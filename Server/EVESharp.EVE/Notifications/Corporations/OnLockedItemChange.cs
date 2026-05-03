using System.Collections.Generic;
using EVESharp.EVE.Packets.Complex;
using EVESharp.Types;
using EVESharp.Types.Collections;

namespace EVESharp.EVE.Notifications.Corporations;

public class OnLockedItemChange : ClientNotification
{
    private const string NOTIFICATION_NAME = "OnLockedItemChange";

    public int          ItemID     { get; }
    public int          LocationID { get; }
    public int          OwnerID    { get; }
    public PyDictionary Changes    { get; }

    public OnLockedItemChange (int itemID, int ownerID, int locationID) : base (NOTIFICATION_NAME)
    {
        ItemID     = itemID;
        OwnerID    = ownerID;
        LocationID = locationID;
        Changes    = new PyDictionary ();
    }

    public OnLockedItemChange AddChange (string changeName, PyDataType oldValue, PyDataType newValue)
    {
        Changes [changeName] = new PyTuple (2)
        {
            [0] = oldValue,
            [1] = newValue
        };

        return this;
    }

    public override List <PyDataType> GetElements ()
    {
        return new List <PyDataType>
        {
            ItemID,
            OwnerID,
            LocationID,
            Changes
        };
    }
}