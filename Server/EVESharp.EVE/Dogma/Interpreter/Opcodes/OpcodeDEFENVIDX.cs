using System;
using System.IO;
using EVESharp.EVE.Data.Inventory.Items;
using EVESharp.EVE.Dogma.Exception;
using EVESharp.EVE.Exceptions;

namespace EVESharp.EVE.Dogma.Interpreter.Opcodes;

public class OpcodeDEFENVIDX : Opcode
{
    public Database.Dogma.Environment Environment { get; private set; }

    public OpcodeDEFENVIDX (Interpreter interpreter) : base (interpreter) { }

    public override Opcode LoadOpcode (BinaryReader reader)
    {
        if (Enum.TryParse (reader.ReadString (), out Database.Dogma.Environment environment) == false)
            throw new DogmaMachineException ("Cannot determine environment id");

        this.Environment = environment;

        return this;
    }

    public ItemEntity GetItem ()
    {
        ItemEntity item = null;

        switch (this.Environment)
        {
            case Database.Dogma.Environment.Self:
                item = this.Interpreter.Environment.Self;
                break;

            case Database.Dogma.Environment.Char:
                item = this.Interpreter.Environment.Character;
                break;

            case Database.Dogma.Environment.Ship:
                item = this.Interpreter.Environment.Ship;
                break;

            case Database.Dogma.Environment.Target:
                item = this.Interpreter.Environment.Target;
                break;

            case Database.Dogma.Environment.Charge:
                item = this.Interpreter.Environment.Charge;
                break;

            case Database.Dogma.Environment.Area:
            case Database.Dogma.Environment.Other:
            default:
                throw new CustomError ("Unsupported target");
        }

        if (item is null)
            throw new CustomError ($"Environment item is null for {this.Environment}");

        return item;
    }
}