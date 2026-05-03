using System.Collections.Generic;
using EVESharp.EVE.Packets.Complex;
using EVESharp.Types;
using EVESharp.Types.Collections;

namespace EVESharp.EVE.Notifications.Network;

public class OnMachoObjectDisconnect : ClientNotification
{
    private const string NOTIFICATION_NAME = "OnMachoObjectDisconnect";

    public PyString  ObjectID    { get; }
    public PyInteger ClientID    { get; }
    public PyString  ReferenceID { get; }
    
    public OnMachoObjectDisconnect (PyString objectID, PyInteger clientID, PyString referenceID) : base (NOTIFICATION_NAME)
    {
        ObjectID    = objectID;
        ClientID    = clientID;
        ReferenceID = referenceID;
    }

    public override List <PyDataType> GetElements ()
    {
        return new List <PyDataType>
        {
            ObjectID,
            ClientID,
            ReferenceID
        };
    }

    public static implicit operator PyTuple(OnMachoObjectDisconnect notification)
    {
        List<PyDataType> data = notification.GetElements();
            
        PyTuple result = new PyTuple(data?.Count ?? 0);

        int i = 0;

        // add the rest of the data to the notification
        if (data is not null)
            foreach (PyDataType entry in data)
                result[i++] = entry;

        return result;
    }
}