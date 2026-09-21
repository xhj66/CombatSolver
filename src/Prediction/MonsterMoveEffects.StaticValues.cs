using MegaCrit.Sts2.Core.Models;

namespace CombatSolver;

internal static partial class MonsterMoveEffects
{
    private static readonly IReadOnlyDictionary<string, string[]> StaticIntMembers =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["LivingFog"] = ["BloatAmount"],
            ["ToughEgg"] = ["HatchlingMinHp", "HatchlingMaxHp"],
            ["TestSubject"] = ["BurningGrowlBurnCount", "BurningGrowlStrengthGain"],
            ["BowlbugEgg"] = ["ProtectBlock"],
            ["InfestedPrism"] = ["RadiateBlock", "PulsateBlock", "VitalSparkAmount"],
            ["KinFollower"] = ["DanceStrength"],
            ["KinPriest"] = ["RitualStrength"],
            ["KnowledgeDemon"] = ["PonderStrength"],
            ["LagavulinMatriarch"] = ["Slash2Block"],
            ["Myte"] = ["SuckStrength"],
            ["Nibbit"] = ["SliceBlock", "HissStrengthGain"],
            ["MagiKnight"] = ["PowerShieldBlock"],
            ["Seapunk"] = ["BubbleBlock", "BubbleStr"],
            ["BowlbugNectar"] = ["BuffStrengthGain"],
            ["CorpseSlug"] = ["GoopFrailAmt"],
            ["SoulFysh"] = ["ScreamMoveAmount", "GazeMoveAmount"],
            ["TheLost"] = ["DebilitatingSmogStrengthStealAmount"],
            ["CalcifiedCultist"] = ["IncantationAmount"],
            ["DampCultist"] = ["IncantationAmount"],
            ["CeremonialBeast"] = ["PlowStrength", "CrushStrength", "PlowAmount"],
            ["Ovicopter"] = ["NutritionalPasteStrengthAmount"],
            ["SkulkingColony"] = ["InertiaStrengthGain"],
            ["TheInsatiable"] = ["SalivateStrength"],
            ["TheObscura"] = ["HardeningStrikeBlock"],
            ["TheForgotten"] = ["DebilitatingSmogDexStealAmount"],
            ["LouseProgenitor"] = ["CurlBlock", "GrowStrength"],
            ["Tunneler"] = ["BlockGain"],
            ["Crusher"] = ["AdaptStrengthGain"],
            ["Rocket"] = ["ChargeUpStrengthGain"],
            ["PhantasmalGardener"] = ["EnlargeStr"],
            ["Axebot"] = ["BootUpBlock", "BootUpStrGain", "RespawnCount"],
            ["Aeonglass"] = ["EbbBlock", "WitherAmount", "IncreasingIntensityBaseStrength"],
            ["AxeRubyRaider"] = ["SwingBlock"],
            ["DevotedSculptor"] = ["_ritualGain"],
            ["WaterfallGiant"] = ["PressurizeAmount", "SiphonHeal", "PressureGunIncrease"],
        };

    internal static IReadOnlyDictionary<string, int> CaptureStaticIntValues(MonsterModel monster)
    {
        string typeName = monster.GetType().Name;
        if (!StaticIntMembers.TryGetValue(typeName, out string[]? members))
        {
            // 第三方适配 Mod 自行声明它需要冻结的静态数值成员。
            if (!ThirdPartyAdapterRegistry.TryGetStaticIntMembers(typeName, out string[] foreignMembers))
                return new Dictionary<string, int>(0, StringComparer.Ordinal);
            members = foreignMembers;
        }
        Dictionary<string, int> values = new(members.Length, StringComparer.Ordinal);
        foreach (string member in members)
            values.Add(member, MonsterValueReader.ReadInt(monster, member));
        return values;
    }
}
