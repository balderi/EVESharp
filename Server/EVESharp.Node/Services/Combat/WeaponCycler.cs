using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using EVESharp.EVE.Data.Inventory.Items.Types;
using EVESharp.EVE.Sessions;

namespace EVESharp.Node.Services.Combat;

/// <summary>
/// Context for a weapon cycling timer.
/// </summary>
public class WeaponCycleContext
{
    public int        ModuleID      { get; set; }
    public int        ModuleTypeID  { get; set; }
    public int        ModuleGroupID { get; set; }
    public int        TargetID      { get; set; }
    public int        CharacterID   { get; set; }
    public int        ShipID        { get; set; }
    public int        SolarSystemID { get; set; }
    public double     DurationMs    { get; set; }
    public string     EffectName    { get; set; }
    public ShipModule Module        { get; set; }
    public Session    Session       { get; set; }
}

/// <summary>
/// Singleton service managing per-module auto-repeat timers for weapon cycling.
/// </summary>
public class WeaponCycler
{
    private readonly ConcurrentDictionary<int, (Timer Timer, WeaponCycleContext Context)> mTimers =
        new ConcurrentDictionary <int, (Timer Timer, WeaponCycleContext Context)> ();

    /// <summary>
    /// Callback invoked on each weapon cycle to fire the weapon again.
    /// Set by dogmaIM during initialization.
    /// </summary>
    public Action<WeaponCycleContext> OnCycleFire { get; set; }

    /// <summary>
    /// Callback to validate target is still locked and in range.
    /// Returns false to stop cycling.
    /// </summary>
    public Func<WeaponCycleContext, bool> OnCycleValidate { get; set; }

    /// <summary>
    /// Callback invoked when cycling stops (timer removed), so the caller
    /// can deactivate the module effect. NOT invoked when a timer is being
    /// replaced inside StartCycling (to avoid deactivating a just-applied effect).
    /// </summary>
    public Action<WeaponCycleContext> OnCycleStop { get; set; }

    public void StartCycling (WeaponCycleContext ctx)
    {
        StopCycling (ctx.ModuleID, false); // cancel any existing timer without deactivating effect

        int period = Math.Max ((int) ctx.DurationMs, 1000); // minimum 1 second cycle

        Timer timer = new Timer (CycleTick, ctx, period, period);
        mTimers[ctx.ModuleID] = (timer, ctx);

        Console.WriteLine ($"[WeaponCycler] Started cycling module {ctx.ModuleID} every {period}ms on target {ctx.TargetID}");
    }

    public void StopCycling (int moduleID, bool invokeStopCallback = true)
    {
        if (mTimers.TryRemove (moduleID, out (Timer Timer, WeaponCycleContext Context) entry))
        {
            entry.Timer.Dispose ();
            Console.WriteLine ($"[WeaponCycler] Stopped cycling module {moduleID}");

            if (invokeStopCallback)
            {
                try { OnCycleStop?.Invoke (entry.Context); }
                catch (Exception ex) { Console.WriteLine ($"[WeaponCycler] OnCycleStop error for module {moduleID}: {ex.Message}"); }
            }
        }
    }

    public void StopAll ()
    {
        foreach (KeyValuePair <int, (Timer Timer, WeaponCycleContext Context)> kvp in mTimers)
        {
            kvp.Value.Timer.Dispose ();
        }
        mTimers.Clear ();
    }

    /// <summary>
    /// Stop all weapon modules that are targeting a specific entity (e.g. when it dies).
    /// </summary>
    public void StopAllTargeting (int targetID)
    {
        foreach (KeyValuePair <int, (Timer Timer, WeaponCycleContext Context)> kvp in mTimers)
        {
            if (kvp.Value.Context.TargetID == targetID)
                StopCycling (kvp.Key);
        }
    }

    private void CycleTick (object state)
    {
        WeaponCycleContext ctx = (WeaponCycleContext) state;

        try
        {
            // Validate target is still valid
            if (OnCycleValidate != null && !OnCycleValidate (ctx))
            {
                StopCycling (ctx.ModuleID);
                Console.WriteLine ($"[WeaponCycler] Cycle stopped for module {ctx.ModuleID}: validation failed");
                return;
            }

            // Fire the weapon again
            OnCycleFire?.Invoke (ctx);
        }
        catch (Exception ex)
        {
            Console.WriteLine ($"[WeaponCycler] Error in cycle tick for module {ctx.ModuleID}: {ex.Message}");
            StopCycling (ctx.ModuleID);
        }
    }
}