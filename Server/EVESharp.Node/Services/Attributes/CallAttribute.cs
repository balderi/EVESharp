using System;

namespace EVESharp.EVE.Network.Services;

/// <summary>
/// Used to mark methods as exposed RPC calls in EVESharp.
/// Matches the naming used by the service dispatcher.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class CallAttribute : Attribute
{
    public string Name { get; }

    public CallAttribute(string name)
    {
        Name = name;
    }
}