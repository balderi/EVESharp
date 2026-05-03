using EVESharp.EVE.Network.Services;
using EVESharp.Types;
using EVESharp.Types.Collections;

namespace EVESharp.Node.Services.Navigation;

public class dungeonExplorationMgr : Service
{
    public override AccessLevel AccessLevel => AccessLevel.None;

    public PyDataType GetMyEscalatingPathDetails (ServiceCall call)
    {
        // TODO: IMPLEMENT THIS - should return expedition/escalation data for the journal
        return new PyList (0);
    }
}
