using System;
using EVESharp.EVE.Sessions;
using EVESharp.Types;

namespace EVESharp.EVE.Network.Services.Validators;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public class MustBeInSpace : CallValidator
{
    public override bool Validate(Session session)
    {
        return session.TryGetValue(Session.SOLAR_SYSTEM_ID, out PyDataType value) && value is not null;
    }
}