using EVESharp.EVE.Sessions;
using EVESharp.Types;
using EVESharp.Types.Collections;

namespace EVESharp.Node.Services.Network;

/// <summary>
/// Placeholder for the Apoc "sessionchange" GPCS channel.
/// 
/// Right now all required sessionchange signalling is done via normal
/// macho packets in Node.SessionManager + MachoNet.QueueOutputPacket.
/// 
/// This class exists only so we have a clean place to hook extra GPCS
/// behaviour in the future without breaking the build.
/// </summary>
internal static class SessionChangeGPCS
{
    public static void Send(Session session, PyDictionary<PyString, PyTuple> changes)
    {
        // NO-OP for now.
        // The client already receives SESSIONCHANGENOTIFICATION via macho.
        // When/if we need real GPCS handling, we can implement it here.
    }
}