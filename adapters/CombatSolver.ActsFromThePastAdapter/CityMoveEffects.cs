using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver.ActsFromThePastAdapter;

/// <summary>
/// 往昔之章第二幕（The City）怪物的行动效果（非攻击部分）。
/// </summary>
/// <remarks>
/// 与第一幕（<see cref="ExordiumMoveEffects"/>）同一条纪律：逐行对照 AFTP 源码里那个 <c>MoveState</c>
/// 的回调，顺序也一致；攻击部分仍由求解器的通用攻击循环按意图结算。
/// </remarks>
internal static class CityMoveEffects
{
    /// <summary>
    /// 适配层自己往怪物标量状态里塞的一项：百夫长那条私有 RNG 流已经抽过几次。
    /// </summary>
    /// <remarks>
    /// 这个名字**不在** <see cref="ThirdPartyAdapterRegistry.RegisterMonsterStateMembers"/> 的名单里——
    /// 实机怪物身上并没有这个成员，登记进名单会让根捕获去读一个不存在的字段。它只在预测过程中由
    /// 适配层写入，一样会进状态指纹（指纹遍历的是整张标量状态表）。
    /// </remarks>
    internal const string CenturionRngDrawsMember = "adapter_centurion_rng_draws";

    private static readonly string[] MonsterTypes =
    [
        "Centurion",
        "Mystic",
        "Bear",
        "Pointy",
        "Taskmaster",
        "Mugger",
        "Romeo",
        "SphericGuardian",
        "Snecko",
        "Chosen",
        "Champ",
        "BookOfStabbing",
    ];

    /// <summary>Bear 的冲刺格挡（AFTP <c>LungeBlock</c>）。</summary>
    private static int _bearLungeBlock;

    /// <summary>Romeo 的虚弱层数（AFTP <c>WeakAmount</c>）。</summary>
    private static int _romeoWeakAmount;

    /// <summary>球状守卫的硬化格挡与破甲层数（AFTP <c>HardenBlock</c> / <c>FrailAmount</c>）。</summary>
    private static int _sphericHardenBlock;
    private static int _sphericFrailAmount;

    /// <summary>灾祸（Chosen）与冠军（Champ）钉死的常量。</summary>
    private static int _chosenDebilitateVuln;
    private static int _chosenDrainStrength;
    private static int _chosenDrainWeak;
    private static int _chosenHexAmount;
    private static int _champDebuffAmount;

    public static void Verify()
    {
        foreach (string typeName in MonsterTypes)
            AfpReflection.RequireMonsterType(typeName);
        // 百夫长的 Protect 要用它自己那条私有 RNG 流抽目标，核对该访问点还在。
        MonsterRngSupport.VerifyShape();
        _bearLungeBlock = AfpReflection.RequireConst("Bear", "LungeBlock", 9);
        _romeoWeakAmount = AfpReflection.RequireConst("Romeo", "WeakAmount", 3);
        _sphericHardenBlock = AfpReflection.RequireConst("SphericGuardian", "HardenBlock", 15);
        _sphericFrailAmount = AfpReflection.RequireConst("SphericGuardian", "FrailAmount", 5);
        _chosenDebilitateVuln = AfpReflection.RequireConst("Chosen", "DebilitateVuln", 2);
        _chosenDrainStrength = AfpReflection.RequireConst("Chosen", "DrainStrength", 3);
        _chosenDrainWeak = AfpReflection.RequireConst("Chosen", "DrainWeak", 3);
        _chosenHexAmount = AfpReflection.RequireConst("Chosen", "HexAmount", 1);
        _champDebuffAmount = AfpReflection.RequireConst("Champ", "DebuffAmount", 2);
        CityBranchResolvers.champForgeThreshold = AfpReflection.RequireConst("Champ", "ForgeThreshold", 2);
        // 蛇怪的尾鞭按 A9 分支，判据是游戏内部的 AscensionHelper.HasAscension（反射调用，先核对形状）。
        AfpReflection.VerifyAscensionHelper();
    }

    public static void RegisterAll()
    {
        // 都是 AscensionHelper 形式的实例属性（A8+ 20／否则 15；治疗量、治疗阈值、力量见下），
        // 根捕获时读一次，之后整场不变。
        ThirdPartyAdapterRegistry.RegisterStaticIntMembers("Centurion", "ProtectBlock");
        ThirdPartyAdapterRegistry.RegisterStaticIntMembers(
            "Mystic",
            "HealAmount",
            "HealThreshold",
            "StrengthAmount");
        ThirdPartyAdapterRegistry.RegisterStaticIntMembers("Bear", "DexReduction");
        ThirdPartyAdapterRegistry.RegisterStaticIntMembers("Taskmaster", "WoundCount");
        ThirdPartyAdapterRegistry.RegisterStaticIntMembers("Mugger", "EscapeBlock");

        // Centurion.Protect：给一只随机的存活队友（一只都没有就给自己）ProtectBlock 点格挡
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("Centurion", "PROTECT", CenturionProtect);
        // Mystic.Attack：攻击 + 全体目标 Frail 2
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("Mystic", "ATTACK", MysticAttack);
        // Mystic.Heal：给所有存活队友（含自己）回复 HealAmount
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("Mystic", "HEAL", MysticHeal);
        // Mystic.Buff：给所有存活队友（含自己）StrengthAmount 点力量
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("Mystic", "BUFF", MysticBuff);

        // --- 熊与尖刺（同一场遭遇「熊与尖刺」） ---
        // Bear.BearHug：给每个活着的目标 -DexReduction 点敏捷（负数施加，与原版同型）
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("Bear", "BEAR_HUG", BearHug);
        // Bear.Lunge：攻击 + 给自己 LungeBlock 点格挡
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("Bear", "LUNGE", BearLunge);
        // Bear.Maul 是纯攻击（意图由通用攻击循环结算），不需要行动效果登记。
        // Pointy 整只怪只有一个纯攻击行动 STAB，同样不需要行动效果；它的 AfterAddedToRoom
        // 只是给熊的死亡事件挂了一句台词。
        ThirdPartyAdapterRegistry.RegisterMonsterStateMembers("Mugger", "_mugCount");

        // Taskmaster.ScouringWhip：攻击 + WoundCount 张 Wound 进弃牌堆（A9+ 再给自己 1 点力量）
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect(
            "Taskmaster",
            "SCOURING_WHIP",
            TaskmasterScouringWhip);

        // --- 强盗（Mugger，与第一幕的 Looter 同型） ---
        // Mugger.Mug / BigSwipe：偷金币 + _mugCount++
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("Mugger", "MUG", MuggerMug);
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("Mugger", "BIG_SWIPE", MuggerBigSwipe);
        // Mugger.SmokeBomb：给自己 EscapeBlock 点格挡（ValueProp.Move）
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("Mugger", "SMOKE_BOMB", MuggerSmokeBomb);
        // Mugger.Escape：施法者自己离场，两侧都要声明
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("Mugger", "ESCAPE", MuggerEscape);
        ThirdPartyAdapterRegistry.RegisterOwnerRemovingMove("Mugger", "ESCAPE");

        // --- Romeo（与熊／尖刺同场）与球状守卫、蛇怪 ---
        ThirdPartyAdapterRegistry.RegisterStaticIntMembers("SphericGuardian", "ActivateBlock");
        // Romeo.Mock 只有台词，登记成空操作免得 UnknownIntent 被记成「未支持意图」；
        // AgonizingSlash：攻击 + WeakAmount 层虚弱（攻击前没有别的部分）。
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("Romeo", "MOCK", NoEffect);
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("Romeo", "AGONIZING_SLASH", RomeoAgonizingSlash);
        // SphericGuardian：Activate 格挡、FrailAttack 上破甲；Harden **先加格挡再打**（攻击前钩子）；
        // Slam 是纯攻击。Barricade／Artifact 与开场 40 格挡都发生在 AfterAddedToRoom，已在根里。
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect(
            "SphericGuardian",
            "ACTIVATE",
            SphericGuardianActivate);
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect(
            "SphericGuardian",
            "FRAIL_ATTACK",
            SphericGuardianFrailAttack);
        ThirdPartyAdapterRegistry.RegisterMonsterMoveBeforeAttack(
            "SphericGuardian",
            "HARDEN",
            SphericGuardianHardenBeforeAttack);
        // Snecko：Glare 上困惑、TailWhip 攻击后上易伤（A9 及以上再加虚弱）；Bite 是纯攻击。
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("Snecko", "GLARE", SneckoGlare);
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("Snecko", "TAIL_WHIP", SneckoTailWhip);

        // --- 灾祸（Chosen）：开场必 HEX，之后减益／攻击轮换 ---
        ThirdPartyAdapterRegistry.RegisterMonsterStateMembers("Chosen", "_usedHex");
        // HexMove：给活着的目标各 1 层灾祸（能力镜像见 CityHooks）
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("Chosen", "HEX", ChosenHex);
        // Debilitate：攻击 + DebilitateVuln 层易伤；Drain：DrainWeak 层虚弱 + 自己 DrainStrength 点力量
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("Chosen", "DEBILITATE", ChosenDebilitate);
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("Chosen", "DRAIN", ChosenDrain);

        // --- 冠军（Champ，第二幕首领） ---
        ThirdPartyAdapterRegistry.RegisterStaticIntMembers(
            "Champ",
            "StrengthAmount",
            "ForgeAmount",
            "BlockAmount");
        ThirdPartyAdapterRegistry.RegisterMonsterStateMembers(
            "Champ",
            "_numTurns",
            "_forgeTimes",
            "_thresholdReached");
        // DefensiveStance：BlockAmount 点格挡 + ForgeAmount 层金属化（回合末镜像见 CityHooks）
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect(
            "Champ",
            "DEFENSIVE_STANCE",
            ChampDefensiveStance);
        // FaceSlap：攻击 + Frail 2 + Vulnerable 2；Taunt：Weak 2 + Vulnerable 2；Gloat：StrengthAmount 力量
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("Champ", "FACE_SLAP", ChampFaceSlap);
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("Champ", "TAUNT", ChampTaunt);
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("Champ", "GLOAT", ChampGloat);
        // Anger：先清掉自己身上所有减益，再加 StrengthAmount × 3 点力量
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("Champ", "ANGER", ChampAnger);

        // --- 往昔之书（BookOfStabbing，第二幕精英） ---
        // STAB 的意图是 DynamicMultiAttackIntent(() => StabDamage, () => StabCount)：伤害按 A9
        // 冻结（StabDamage 属性），段数是**每次出手现算**的 _stabCount，所以只有它必须走动态登记；
        // BIG_STAB 是 SingleAttackIntent(BigStabDamage)，已在 LaterActsStableAttacks 里按冻结值登记。
        ThirdPartyAdapterRegistry.RegisterStaticIntMembers("BookOfStabbing", "StabDamage");
        ThirdPartyAdapterRegistry.RegisterMonsterStateMembers("BookOfStabbing", "_stabCount");
        ThirdPartyAdapterRegistry.RegisterMonsterAttackValues(
            "BookOfStabbing",
            "STAB",
            BookOfStabbingStab);
        // 开场那条 PainfulStabsPower（AfterAddedToRoom 施加）是**原版** Power，攻击后镜像
        // （AfterAttackMirrors.HandlePainfulStabsPower，按未格挡命中数往弃牌堆塞 Wound）已在核心里，
        // 根捕获时这只怪身上就带着它，不需要适配层再登记。
    }

    /// <summary>
    /// BookOfStabbing.STAB 的出手数值：伤害 = <c>StabDamage</c>（A9+ 7，否则 6，根捕获时冻结），
    /// 段数 = 当前分支的 <c>_stabCount</c>（分支解析器每次转移 +1）。
    /// </summary>
    private static BranchMonsterAttack BookOfStabbingStab(
        SimulatedCombatState combat,
        MonsterModel monster)
        => new(
            combat.GetMonsterStaticInt(monster.Creature, "StabDamage"),
            combat.GetMonsterInt(monster.Creature, "_stabCount"));

    /// <summary>Chosen.HexMove：给每个活着的目标 <c>HexAmount</c> 层灾祸。</summary>
    private static bool ChosenHex(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        killedOwner = false;
        if (simulator.State.GetCreature(player).IsAlive)
            combat.ApplyPower(CityHooks.HexType, player, _chosenHexAmount, move.Owner);
        return true;
    }

    /// <summary>Chosen.Debilitate：给每个活着的目标 <c>DebilitateVuln</c> 层易伤。</summary>
    private static bool ChosenDebilitate(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        killedOwner = false;
        if (simulator.State.GetCreature(player).IsAlive)
            combat.ApplyFromMonster<VulnerablePower>(player, _chosenDebilitateVuln, move.Owner);
        return true;
    }

    /// <summary>Chosen.Drain：给玩家 <c>DrainWeak</c> 层虚弱，再给自己 <c>DrainStrength</c> 点力量。</summary>
    private static bool ChosenDrain(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        killedOwner = false;
        if (simulator.State.GetCreature(player).IsAlive)
            combat.ApplyFromMonster<WeakPower>(player, _chosenDrainWeak, move.Owner);
        combat.Apply<StrengthPower>(move.Owner, _chosenDrainStrength, move.Owner);
        return true;
    }

    /// <summary>Champ.DefensiveStance：给自己 <c>BlockAmount</c> 点格挡与 <c>ForgeAmount</c> 层金属化。</summary>
    private static bool ChampDefensiveStance(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        killedOwner = false;
        simulator.GainBlock(
            move.Owner,
            combat.GetMonsterStaticInt(move.Owner, "BlockAmount"),
            ValueProp.Move);
        combat.ApplyPower(
            CityHooks.MetallicizeType,
            move.Owner,
            combat.GetMonsterStaticInt(move.Owner, "ForgeAmount"),
            move.Owner);
        return true;
    }

    /// <summary>Champ.FaceSlap：给每个活着的目标 <c>DebuffAmount</c> 层破甲与易伤。</summary>
    private static bool ChampFaceSlap(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        killedOwner = false;
        if (!simulator.State.GetCreature(player).IsAlive)
            return true;
        combat.ApplyFromMonster<FrailPower>(player, _champDebuffAmount, move.Owner);
        combat.ApplyFromMonster<VulnerablePower>(player, _champDebuffAmount, move.Owner);
        return true;
    }

    /// <summary>Champ.Taunt：给每个活着的目标 <c>DebuffAmount</c> 层虚弱与易伤。</summary>
    private static bool ChampTaunt(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        killedOwner = false;
        if (!simulator.State.GetCreature(player).IsAlive)
            return true;
        combat.ApplyFromMonster<WeakPower>(player, _champDebuffAmount, move.Owner);
        combat.ApplyFromMonster<VulnerablePower>(player, _champDebuffAmount, move.Owner);
        return true;
    }

    /// <summary>Champ.Gloat：给自己 <c>StrengthAmount</c> 点力量。</summary>
    private static bool ChampGloat(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        killedOwner = false;
        combat.Apply<StrengthPower>(
            move.Owner,
            combat.GetMonsterStaticInt(move.Owner, "StrengthAmount"),
            move.Owner);
        return true;
    }

    /// <summary>
    /// Champ.Anger：先把自己身上**所有减益**（<c>PowerType.Debuff</c>）移除，再加
    /// <c>StrengthAmount × 3</c> 点力量。
    /// </summary>
    private static bool ChampAnger(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        killedOwner = false;
        foreach (PowerModel power in combat.EffectivePowers()
                     .Where(power => power.Owner == move.Owner && power.Type == PowerType.Debuff)
                     .ToArray())
        {
            combat.SetPowerAmount(power, 0);
        }
        combat.Apply<StrengthPower>(
            move.Owner,
            combat.GetMonsterStaticInt(move.Owner, "StrengthAmount") * 3,
            move.Owner);
        return true;
    }

    /// <summary>往昔之章里「什么都不做」的行动（UnknownIntent 的纯表演招），登记成空操作。</summary>
    private static bool NoEffect(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        killedOwner = false;
        return true;
    }

    /// <summary>Romeo.AgonizingSlash：给每个活着的目标 <c>WeakAmount</c> 层虚弱。</summary>
    private static bool RomeoAgonizingSlash(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        killedOwner = false;
        if (simulator.State.GetCreature(player).IsAlive)
            combat.ApplyFromMonster<WeakPower>(player, _romeoWeakAmount, move.Owner);
        return true;
    }

    /// <summary>SphericGuardian.Activate：给自己 <c>ActivateBlock</c>（A8+ 35／否则 25）点格挡。</summary>
    private static bool SphericGuardianActivate(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        killedOwner = false;
        simulator.GainBlock(
            move.Owner,
            combat.GetMonsterStaticInt(move.Owner, "ActivateBlock"),
            ValueProp.Move);
        return true;
    }

    /// <summary>SphericGuardian.FrailAttack：给每个活着的目标 <c>FrailAmount</c> 层破甲。</summary>
    private static bool SphericGuardianFrailAttack(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        killedOwner = false;
        if (simulator.State.GetCreature(player).IsAlive)
            combat.ApplyFromMonster<FrailPower>(player, _sphericFrailAmount, move.Owner);
        return true;
    }

    /// <summary>
    /// SphericGuardian.Harden：源码是**先** <c>GainBlock(HardenBlock)</c>、**再**打出这次攻击，
    /// 所以走攻击前钩子而不是普通的行动效果（后者跑在攻击之后，格挡会晚一拍）。
    /// </summary>
    private static void SphericGuardianHardenBeforeAttack(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player)
        => simulator.GainBlock(move.Owner, _sphericHardenBlock, ValueProp.Move);

    /// <summary>Snecko.Glare：给每个活着的目标 1 层困惑。</summary>
    private static bool SneckoGlare(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        killedOwner = false;
        if (simulator.State.GetCreature(player).IsAlive)
            combat.ApplyFromMonster<ConfusedPower>(player, 1, move.Owner);
        return true;
    }

    /// <summary>
    /// Snecko.TailWhip：给每个活着的目标 2 层易伤，**A9 及以上**再加 2 层虚弱。
    /// 源码那一支写的是内联的 <c>AscensionHelper.HasAscension(A9)</c>（整局不变），这里照同一判据。
    /// </summary>
    private static bool SneckoTailWhip(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        killedOwner = false;
        if (!simulator.State.GetCreature(player).IsAlive)
            return true;
        if (AfpReflection.HasAscension(9))
            combat.ApplyFromMonster<WeakPower>(player, 2, move.Owner);
        combat.ApplyFromMonster<VulnerablePower>(player, 2, move.Owner);
        return true;
    }

    /// <summary>Bear.BearHug：给每个活着的目标 <c>-DexReduction</c> 点敏捷。</summary>
    private static bool BearHug(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        killedOwner = false;
        if (!simulator.State.GetCreature(player).IsAlive)
            return true;
        combat.Apply<DexterityPower>(
            player,
            -combat.GetMonsterStaticInt(move.Owner, "DexReduction"),
            move.Owner);
        return true;
    }

    /// <summary>Bear.Lunge：给自己 <c>LungeBlock</c> 点格挡（攻击部分由通用攻击循环结算）。</summary>
    private static bool BearLunge(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        killedOwner = false;
        simulator.GainBlock(move.Owner, _bearLungeBlock, ValueProp.Move);
        return true;
    }

    /// <summary>
    /// Taskmaster.ScouringWhip：把 <c>WoundCount</c> 张 <c>Wound</c> 塞进目标的弃牌堆；
    /// A9 及以上再给自己 1 点力量（源码用 <c>GainsStrength</c> = <c>HasAscension(A9)</c> 判断，
    /// 那是整场不变的运行期属性，与 <c>GremlinFat.AppliesFrail</c> 同一读法）。
    /// </summary>
    private static bool TaskmasterScouringWhip(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        killedOwner = false;
        simulator.AddToCombat<Wound>(
            player,
            PileType.Discard,
            combat.GetMonsterStaticInt(move.Owner, "WoundCount"),
            null);
        if (MonsterValueReader.ReadBool(
                move.Owner.Monster ?? throw new PredictionUnsupportedException("监工缺少怪物模型。"),
                "GainsStrength"))
        {
            combat.Apply<StrengthPower>(move.Owner, 1, move.Owner);
        }
        return true;
    }

    /// <summary>Mugger.Mug：偷完金币记一次 <c>_mugCount++</c>（与第一幕 Looter 同型）。</summary>
    private static bool MuggerMug(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        killedOwner = false;
        StealGoldAndCountMug(simulator, combat, move);
        return true;
    }

    /// <summary>Mugger.BigSwipe：与 Mug 同一套「偷金币 + 记数」。</summary>
    private static bool MuggerBigSwipe(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        killedOwner = false;
        StealGoldAndCountMug(simulator, combat, move);
        return true;
    }

    /// <summary>Mugger.SmokeBomb：给自己 <c>EscapeBlock</c>（A8+ 17／否则 11）点格挡。</summary>
    private static bool MuggerSmokeBomb(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        killedOwner = false;
        simulator.GainBlock(
            move.Owner,
            combat.GetMonsterStaticInt(move.Owner, "EscapeBlock"),
            ValueProp.Move);
        return true;
    }

    /// <summary>Mugger.Escape：施法者自己离开战斗，已经偷到手的金币不会再回来。</summary>
    private static bool MuggerEscape(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        killedOwner = false;
        combat.CreatureEscaped(move.Owner);
        return true;
    }

    /// <summary>复刻 <c>Mugger.StealGold</c> + <c>_mugCount++</c>，与第一幕 Looter 同一口径。</summary>
    private static void StealGoldAndCountMug(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move)
    {
        combat.RecordThievery(simulator, move.Owner);
        combat.SetMonsterInt(
            move.Owner,
            "_mugCount",
            combat.GetMonsterInt(move.Owner, "_mugCount") + 1);
    }

    /// <summary>
    /// Centurion.Protect：与小鬼盾兵的 <c>Protect</c> 同型——在所有存活队友（不含自己）里用这只怪物
    /// 自己那条 RNG 流抽一只，给它 <c>ProtectBlock</c> 点格挡；一只存活队友都没有时格挡落在自己身上，
    /// 且**一次都不抽**（源码先 <c>teammates.Any()</c> 再进 <c>NextItem</c>）。
    /// </summary>
    private static bool CenturionProtect(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        killedOwner = false;
        List<Creature> teammates = [];
        foreach (Creature candidate in combat.GetTeammatesOf(move.Owner))
        {
            if (candidate != move.Owner && simulator.State.GetCreature(candidate).IsAlive)
                teammates.Add(candidate);
        }
        Creature target = move.Owner;
        if (teammates.Count > 0)
        {
            MonsterRngPredictionState rng = MonsterRngSupport.State(
                simulator,
                move.Owner.Monster ?? throw new PredictionUnsupportedException("百夫长缺少怪物模型。"));
            target = rng.NextItem(teammates)!;
            combat.SetMonsterInt(move.Owner, CenturionRngDrawsMember, rng.Draws);
        }
        simulator.GainBlock(
            target,
            combat.GetMonsterStaticInt(move.Owner, "ProtectBlock"),
            ValueProp.Move);
        return true;
    }

    /// <summary>Mystic.Attack：攻击之后给目标挂 2 层 Frail（源码对 <c>targets</c> 全体施加）。</summary>
    private static bool MysticAttack(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        killedOwner = false;
        if (simulator.State.GetCreature(player).IsAlive)
            combat.ApplyFromMonster<FrailPower>(player, 2, move.Owner);
        return true;
    }

    /// <summary>Mystic.Heal：给所有存活队友（含自己）各回复 <c>HealAmount</c>。</summary>
    private static bool MysticHeal(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        killedOwner = false;
        int amount = combat.GetMonsterStaticInt(move.Owner, "HealAmount");
        foreach (Creature teammate in combat.GetTeammatesOf(move.Owner))
        {
            if (simulator.State.GetCreature(teammate).IsAlive)
                simulator.Heal(teammate, amount);
        }
        return true;
    }

    /// <summary>Mystic.Buff：给所有存活队友（含自己）各 <c>StrengthAmount</c> 点力量。</summary>
    private static bool MysticBuff(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        killedOwner = false;
        int amount = combat.GetMonsterStaticInt(move.Owner, "StrengthAmount");
        foreach (Creature teammate in combat.GetTeammatesOf(move.Owner))
        {
            if (simulator.State.GetCreature(teammate).IsAlive)
                combat.Apply<StrengthPower>(teammate, amount, move.Owner);
        }
        return true;
    }
}
