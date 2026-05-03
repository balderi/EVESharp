using System.IO;

namespace EVESharp.EVE.Dogma.Interpreter.Opcodes;

/// <summary>
/// ATTACK opcode (13). Present in weapon effect pre-expressions to signal
/// that the effect performs an attack on a target. The actual damage
/// calculation is handled externally by CombatService/dogmaIM.FireWeapon,
/// so this opcode parses its operands but is a no-op at execution time.
///
/// In the expression DB, ATTACK has only FirstArgument (no SecondArgument),
/// so only one child is compiled into bytecode.
/// </summary>
public class OpcodeATTACK : OpcodeRunnable
{
    public Opcode Operand { get; private set; }

    public OpcodeATTACK (Interpreter interpreter) : base (interpreter) { }

    public override Opcode LoadOpcode (BinaryReader reader)
    {
        // ATTACK is a unary opcode — the expression tree only has FirstArgument.
        // Reading a second operand would read past the bytecode into padding zeros.
        this.Operand = this.Interpreter.Step (reader);

        return this;
    }

    public override void Execute ()
    {
        // No-op: weapon damage is applied by dogmaIM.FireWeapon / CombatService,
        // not by the Dogma expression interpreter.
    }
}
