using EVESharp.Database.EVEMath;
using EVESharp.Database.Inventory.Attributes;
using Attribute = EVESharp.Database.Inventory.Attributes.Attribute;

namespace EVESharp.EVE.Data.Inventory.Items.Types;

public class Skill : ItemEntity
{
    private readonly double mSkillPointMultiplier;

    public long Level
    {
        get => Attributes [AttributeTypes.skillLevel].Integer;
        set
        {
            Attributes [AttributeTypes.skillLevel].Integer = value;
            Points                                         = this.GetSkillPointsForLevel (value);
        }
    }

    public double Points
    {
        get => Attributes [AttributeTypes.skillPoints].Float;
        set => Attributes [AttributeTypes.skillPoints].Float = value;
    }

    public Attribute TimeConstant => Attributes [AttributeTypes.skillTimeConstant];

    public Attribute PrimaryAttribute => Attributes [AttributeTypes.primaryAttribute];

    public Attribute SecondaryAttribute => Attributes [AttributeTypes.secondaryAttribute];

    public long ExpiryTime
    {
        get => Attributes [AttributeTypes.expiryTime].Integer;
        set => Attributes [AttributeTypes.expiryTime].Integer = value;
    }

    public Skill (Database.Inventory.Types.Information.Item info, double skillPointMultiplier) : base (info)
    {
        this.mSkillPointMultiplier = skillPointMultiplier;
    }

    public double GetSkillPointsForLevel (long level)
    {
        return Skills.GetSkillPointsForLevel (level, TimeConstant, this.mSkillPointMultiplier);
    }
}