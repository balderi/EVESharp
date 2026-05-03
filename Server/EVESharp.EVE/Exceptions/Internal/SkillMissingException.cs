using System;
using Type = EVESharp.Database.Inventory.Types.Type;

namespace EVESharp.EVE.Exceptions.Internal;

public class SkillMissingException : Exception
{
    public int SkillTypeID { get; }

    public SkillMissingException (Type skill)
    {
        SkillTypeID = skill.ID;
    }

    public SkillMissingException (int skillTypeID)
    {
        SkillTypeID = skillTypeID;
    }
}