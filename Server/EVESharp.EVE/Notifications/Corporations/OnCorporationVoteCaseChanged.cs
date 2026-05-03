using System.Collections.Generic;
using EVESharp.EVE.Packets.Complex;
using EVESharp.Types;
using EVESharp.Types.Collections;

namespace EVESharp.EVE.Notifications.Corporations;

public class OnCorporationVoteCaseChanged : ClientNotification
{
    private const string NOTIFICATION_NAME = "OnCorporationVoteCaseChanged";

    public  int                              CorporationID { get; init; }
    public  int                              VoteCaseID    { get; init; }
    private PyDictionary <PyString, PyTuple> Changes       { get; }

    public OnCorporationVoteCaseChanged (int corporationID, int voteCaseID) : base (NOTIFICATION_NAME)
    {
        CorporationID = corporationID;
        VoteCaseID    = voteCaseID;
        Changes       = new PyDictionary <PyString, PyTuple> ();
    }

    public OnCorporationVoteCaseChanged AddValue (string columnName, PyDataType oldValue, PyDataType newValue)
    {
        Changes [columnName] = new PyTuple (2)
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
            CorporationID,
            VoteCaseID,
            Changes
        };
    }
}