using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace EVESharp.Destiny;

/// <summary>
/// Builds Destiny binary packets in Apocrypha format.
/// Field order and types match EVEmu Apocrypha DestinyStructs.h.
/// </summary>
public static class DestinyBinaryEncoder
{
    /// <summary>
    /// Build a full-state Destiny packet containing all given balls.
    /// PacketType:
    ///   0 = full state snapshot
    ///   1 = incremental update (same wire format, different semantics)
    /// </summary>
    public static byte[] BuildFullState(IEnumerable<Ball> balls, int stamp, byte packetType = 0)
    {
        if (balls == null)
            throw new ArgumentNullException(nameof(balls));

        using MemoryStream ms     = new MemoryStream();
        using BinaryWriter writer = new BinaryWriter(ms);

        // -----------------------------------------------------------------
        // 1) Destiny header (matches Header struct)
        // -----------------------------------------------------------------
        writer.Write(packetType);    // byte PacketType
        writer.Write(stamp);         // int Stamp

        Console.WriteLine($"[DestinyEncoder] Header: packetType={packetType}, stamp={stamp}");

        // -----------------------------------------------------------------
        // 2) All balls
        // -----------------------------------------------------------------
        int ballCount = 0;
        foreach (Ball ball in balls)
        {
            if (ball == null)
                continue;

            WriteBallExplicit(writer, ball);
            ballCount++;
        }

        Console.WriteLine($"[DestinyEncoder] Wrote {ballCount} balls, total size = {ms.Position} bytes");

        writer.Flush();
        return ms.ToArray();
    }

    /// <summary>
    /// Convenience helper for single ball encoding
    /// </summary>
    public static byte[] BuildSingleBall(Ball ball, int stamp, byte packetType = 0)
    {
        if (ball == null)
            throw new ArgumentNullException(nameof(ball));

        return BuildFullState(new[] { ball }, stamp, packetType);
    }

    // =====================================================================
    // Explicit field writing - Apocrypha format
    // =====================================================================

    private static void WriteBallExplicit(BinaryWriter writer, Ball ball)
    {
        if (ball.Header == null)
            throw new InvalidOperationException("Ball.Header must be non-null before encoding.");

        BallHeader h = ball.Header;

        // -------------------------
        // BallHeader (Apocrypha order: entityID, mode, radius, xyz, sub_type)
        // 38 bytes total
        // -------------------------
        writer.Write(h.ItemId);                // int (4 bytes)
        writer.Write((byte)h.Mode);            // BallMode : byte
        writer.Write(h.Radius);                // double (8 bytes - Apocrypha!)
        WriteVector3(writer, h.Location);       // 24 bytes (3 doubles)
        writer.Write((byte)h.Flags);           // BallFlag : byte (sub_type)

        Console.WriteLine($"[DestinyEncoder]   Ball {h.ItemId}: Mode={h.Mode}, Flags={h.Flags}, Pos=({h.Location.X:F0},{h.Location.Y:F0},{h.Location.Z:F0}), R={h.Radius}");

        // -------------------------
        // ExtraBallHeader / MassSector (if mode != Rigid)
        // Apocrypha order: mass, cloak, unknown52, corpID, allianceID
        // 25 bytes total
        // -------------------------
        if (h.Mode != BallMode.Rigid)
        {
            ExtraBallHeader extra = ball.ExtraHeader ?? new ExtraBallHeader();
            writer.Write(extra.Mass);              // double (8 bytes) - FIRST in Apocrypha
            writer.Write((byte)extra.CloakMode);   // CloakMode : byte
            writer.Write(extra.Harmonic);          // ulong (8 bytes) - "unknown52"
            writer.Write(extra.CorporationId);     // int (4 bytes)
            writer.Write(extra.AllianceId);        // int (4 bytes)

            Console.WriteLine($"[DestinyEncoder]     ExtraHeader: mass={extra.Mass}, cloak={extra.CloakMode}, corp={extra.CorporationId}, alliance={extra.AllianceId}");
        }

        // -------------------------
        // BallData / ShipSector (if IsFree flag is set)
        // Apocrypha order: max_speed, vel xyz, unk xyz, agility, speed_fraction
        // 72 bytes total
        // -------------------------
        if (h.Flags.HasFlag(BallFlag.IsFree))
        {
            BallData data = ball.Data ?? new BallData();
            writer.Write(data.MaxVelocity);        // double (8 bytes)
            WriteVector3(writer, data.Velocity);    // 24 bytes (3 doubles)
            WriteVector3(writer, data.UnknownVec);  // 24 bytes (3 doubles) - NEW
            writer.Write(data.Agility);            // double (8 bytes) - NEW
            writer.Write(data.SpeedFraction);      // double (8 bytes)

            Console.WriteLine($"[DestinyEncoder]     BallData: maxVel={data.MaxVelocity}, speedFrac={data.SpeedFraction}, agility={data.Agility}");
        }

        // -------------------------
        // FormationId (always a single byte)
        // -------------------------
        writer.Write(ball.FormationId);

        // -------------------------
        // Mode-specific state
        // -------------------------
        switch (h.Mode)
        {
            case BallMode.Follow:
            case BallMode.Orbit:
                WriteFollowState(writer, ball.FollowState);
                break;

            case BallMode.Formation:
                WriteFormationState(writer, ball.FormationState);
                break;

            case BallMode.Troll:
                WriteTrollState(writer, ball.TrollState);
                break;

            case BallMode.Missile:
                WriteMissileState(writer, ball.MissileState);
                break;

            case BallMode.Goto:
                WriteGotoState(writer, ball.GotoState);
                break;

            case BallMode.Warp:
                WriteWarpState(writer, ball.WarpState);
                break;

            case BallMode.Mushroom:
                WriteMushroomState(writer, ball.MushroomState);
                break;

            case BallMode.Stop:
            case BallMode.Field:
            case BallMode.Rigid:
                // no extra state for these
                break;
        }

        // -------------------------
        // MiniBalls
        // -------------------------
        if (h.Flags.HasFlag(BallFlag.HasMiniBalls))
        {
            MiniBall[] minis = ball.MiniBalls ?? Array.Empty<MiniBall>();
            writer.Write((short)minis.Length);

            for (int i = 0; i < minis.Length; i++)
                WriteMiniBall(writer, minis[i]);
        }

        // Name field (Apocrypha format): byte nameWords, then nameWords*2 bytes of Unicode.
        // Writing 0 = "no name" (just the single count byte).
        writer.Write((byte)0);
    }

    // =====================================================================
    // Vector3 helper
    // =====================================================================
    private static void WriteVector3(BinaryWriter writer, Vector3 v)
    {
        writer.Write(v.X);
        writer.Write(v.Y);
        writer.Write(v.Z);
    }

    // =====================================================================
    // State struct writers (Apocrypha format)
    // =====================================================================
    private static void WriteFollowState(BinaryWriter writer, FollowState state)
    {
        // FollowState: FollowId (int), FollowRange (double)
        writer.Write(state.FollowId);          // int (4 bytes)
        writer.Write(state.FollowRange);        // double (8 bytes)
    }

    private static void WriteFormationState(BinaryWriter writer, FormationState state)
    {
        // FormationState: FollowId (int), FollowRange (double), EffectStamp (int)
        writer.Write(state.FollowId);          // int (4 bytes)
        writer.Write(state.FollowRange);        // double (8 bytes)
        writer.Write(state.EffectStamp);        // int (4 bytes)
    }

    private static void WriteTrollState(BinaryWriter writer, TrollState state)
    {
        // TrollState: EffectStamp (int)
        writer.Write(state.EffectStamp);        // int (4 bytes)
    }

    private static void WriteMissileState(BinaryWriter writer, MissileState state)
    {
        // MissileState: FollowId (int), FollowRange (double), OwnerId (int),
        //               EffectStamp (int), Location (Vector3)
        writer.Write(state.FollowId);          // int (4 bytes)
        writer.Write(state.FollowRange);        // double (8 bytes)
        writer.Write(state.OwnerId);           // int (4 bytes)
        writer.Write(state.EffectStamp);        // int (4 bytes)
        WriteVector3(writer, state.Location);   // 24 bytes
    }

    private static void WriteGotoState(BinaryWriter writer, GotoState state)
    {
        // GotoState: Location (Vector3) - 24 bytes (unchanged)
        WriteVector3(writer, state.Location);
        Console.WriteLine($"[DestinyEncoder]     GotoState: target=({state.Location.X:F0},{state.Location.Y:F0},{state.Location.Z:F0})");
    }

    private static void WriteWarpState(BinaryWriter writer, WarpState state)
    {
        // WarpState: Location (Vector3), EffectStamp (int),
        //            FollowRange (double), FollowId (int), OwnerId (int)
        WriteVector3(writer, state.Location);   // 24 bytes
        writer.Write(state.EffectStamp);        // int (4 bytes)
        writer.Write(state.FollowRange);        // double (8 bytes)
        writer.Write(state.FollowId);           // int (4 bytes)
        writer.Write(state.OwnerId);            // int (4 bytes)
    }

    private static void WriteMushroomState(BinaryWriter writer, MushroomState state)
    {
        // MushroomState: FollowRange (double), Unknown (double),
        //                EffectStamp (int), OwnerId (int)
        writer.Write(state.FollowRange);        // double (8 bytes)
        writer.Write(state.Unknown);            // double (8 bytes)
        writer.Write(state.EffectStamp);        // int (4 bytes)
        writer.Write(state.OwnerId);            // int (4 bytes)
    }

    private static void WriteMiniBall(BinaryWriter writer, MiniBall mini)
    {
        // MiniBall: Offset (Vector3), Radius (double)
        WriteVector3(writer, mini.Offset);
        writer.Write(mini.Radius);              // double (8 bytes - Apocrypha!)
    }

    // =====================================================================
    // Legacy method using Marshal (kept for reference/comparison)
    // =====================================================================
    private static void WriteStruct<T>(Stream stream, T value)
    {
        object boxed = value;

        if (boxed == null)
            boxed = Activator.CreateInstance(typeof(T))!;

        Type type = boxed.GetType();
        int  size = Marshal.SizeOf(type);

        byte[] buffer = new byte[size];
        IntPtr ptr    = IntPtr.Zero;

        try
        {
            ptr = Marshal.AllocHGlobal(size);
            if (ptr == IntPtr.Zero)
                throw new Exception("Failed to allocate unmanaged memory for destiny struct write");

            Marshal.StructureToPtr(boxed, ptr, false);
            Marshal.Copy(ptr, buffer, 0, size);

            stream.Write(buffer, 0, size);
        }
        finally
        {
            if (ptr != IntPtr.Zero)
                Marshal.FreeHGlobal(ptr);
        }
    }
}