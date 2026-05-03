using System;

namespace EVESharp.EVE.Network.Services;

/// <summary>
/// Marks a method as an exposed service call (RPC) in EVESharp.
/// The service loader will detect and register it automatically.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class EVESharpCallAttribute : Attribute
{
    public string Name { get; }

    public EVESharpCallAttribute(string name)
    {
        Name = name;
    }
}