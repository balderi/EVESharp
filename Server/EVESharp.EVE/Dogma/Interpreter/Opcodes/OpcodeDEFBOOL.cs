using System.IO;

namespace EVESharp.EVE.Dogma.Interpreter.Opcodes;

public class OpcodeDEFBOOL : OpcodeWithBooleanOutput
{
    public bool Value { get; private set; }

    public OpcodeDEFBOOL (Interpreter interpreter) : base (interpreter) { }

    public override Opcode LoadOpcode (BinaryReader reader)
    {
        this.Value = reader.ReadString () == "1";

        return this;
    }

    public override bool Execute ()
    {
        return this.Value;
    }
}
