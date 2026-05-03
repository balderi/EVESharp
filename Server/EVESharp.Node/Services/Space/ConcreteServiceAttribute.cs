using System;

namespace EVESharp.Node.Services;

[AttributeUsage(AttributeTargets.Class)]
public sealed class ConcreteServiceAttribute : Attribute
{
    public string ServiceName { get; }

    public ConcreteServiceAttribute(string serviceName)
    {
        ServiceName = serviceName;
    }
}