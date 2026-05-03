namespace EVESharp.Destiny;

/// <summary>
/// NPC entity activity states, matching the EVE client's entity* constants.
/// Used by the server-side NPC AI to track behavior phases.
/// </summary>
public enum NpcAiState
{
    Idle        = 0,
    Combat      = 1,
    Approaching = 3,
    Departing   = 4,
    Departing2  = 5,
    Pursuit     = 6,
    Fleeing     = 7
}