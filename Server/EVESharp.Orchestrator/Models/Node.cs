namespace EVESharp.Orchestrator.Models;

public class Node
{
    public string Ip            { get; init; } = string.Empty;
    public string Address       { get; init; } = string.Empty;
    public int    Port          { get; init; }
    public int    NodeId        { get; init; }
    public string Role          { get; init; } = string.Empty;
    public long   LastHeartBeat { get; init; }
    public double Load          { get; init; } = 0.0f;
}