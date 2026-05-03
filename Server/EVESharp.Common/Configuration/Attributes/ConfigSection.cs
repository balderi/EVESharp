using System;

namespace EVESharp.Common.Configuration.Attributes;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface)]
public class ConfigSection : Attribute
{
    public string Section  { get; }
    public bool   Optional { get; }
    
    public ConfigSection (string sectionName, bool optional = false)
    {
        Section  = sectionName;
        Optional = optional;
    }
}