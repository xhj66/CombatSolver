using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;

namespace CombatSolver.ActsFromThePastAdapter;

/// <summary>
/// 往昔之章第一幕怪物的行动效果（非攻击部分）。
/// </summary>
/// <remarks>
/// 攻击部分由求解器的通用攻击循环按意图结算；这里只补行动里的增益、减益、给牌与格挡。
/// 每条实现逐行对应 AFTP 源码里那个 <c>MoveState</c> 的回调，顺序也一致。
///
/// **只登记能完整建模的行动。** 行动里含未镜像的第三方 Power、或会让怪物离场的语义
/// （例如 Looter 的 ESCAPE、SlaverRed 的 Entangle）时不登记：求解器会给这个意图打红字
/// 说「这里没镜像」，而不是静默当成空操作。
/// </remarks>
internal static class ExordiumMoveEffects
{
    // 适配层钉死的常量。Verify() 会与 AFTP 源码里的 private const 逐项核对，
    // 不一致就抛异常、一个都不登记。
    private static int _acidSlimeMediumSlimedCount;
    private static int _acidSlimeMediumWeakTurns;
    private static int _acidSlimeSmallWeakTurns;
    private static int _spikeSlimeMediumSlimedCount;
    private static int _spikeSlimeMediumFrailTurns;
    private static int _louseGreenWeakAmount;
    private static int _gremlinFatWeakAmount;
    private static int _gremlinFatFrailAmount;
    private static int _looterEscapeBlock;

    /// <summary>GremlinWizard 的充能上限（AFTP <c>ChargeLimit</c>）。</summary>
    internal static int GremlinWizardChargeLimit { get; private set; } = 3;

    private static readonly string[] MonsterTypes =
    [
        "AcidSlimeMedium",
        "AcidSlimeSmall",
        "SpikeSlimeMedium",
        "SpikeSlimeSmall",
        "FungiBeast",
        "JawWorm",
        "GremlinNob",
        "GremlinWizard",
        "SlaverBlue",
        "Cultist",
        "Sentry",
        "LouseGreen",
        "LouseRed",
        "GremlinFat",
        "Looter",
    ];

    public static void Verify()
    {
        foreach (string typeName in MonsterTypes)
            AfpReflection.RequireMonsterType(typeName);

        _acidSlimeMediumSlimedCount = AfpReflection.RequireConst("AcidSlimeMedium", "SlimedCount", 1);
        _acidSlimeMediumWeakTurns = AfpReflection.RequireConst("AcidSlimeMedium", "WeakTurns", 1);
        _acidSlimeSmallWeakTurns = AfpReflection.RequireConst("AcidSlimeSmall", "WeakTurns", 1);
        _spikeSlimeMediumSlimedCount = AfpReflection.RequireConst("SpikeSlimeMedium", "SlimedCount", 1);
        _spikeSlimeMediumFrailTurns = AfpReflection.RequireConst("SpikeSlimeMedium", "FrailTurns", 1);
        _louseGreenWeakAmount = AfpReflection.RequireConst("LouseGreen", "WeakAmount", 2);
        _gremlinFatWeakAmount = AfpReflection.RequireConst("GremlinFat", "WeakAmount", 1);
        _gremlinFatFrailAmount = AfpReflection.RequireConst("GremlinFat", "FrailAmount", 1);
        _looterEscapeBlock = AfpReflection.RequireConst("Looter", "EscapeBlock", 6);
        GremlinWizardChargeLimit = AfpReflection.RequireConst("GremlinWizard", "ChargeLimit", 3);
    }

    public static void RegisterAll()
    {
        RegisterFrozenValues();
        RegisterMonsterState();

        // --- 史莱姆 ---
        // AcidSlimeMedium.CorrosiveSpit：攻击 + CardPileCmd.AddToCombatAndPreview<Slimed>(targets, Discard, SlimedCount, null)
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("AcidSlimeMedium", "CORROSIVE_SPIT", AcidSlimeCorrosiveSpit);
        // AcidSlimeMedium.Lick：PowerCmd.Apply<WeakPower>(target, WeakTurns, Creature, null)
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("AcidSlimeMedium", "LICK", AcidSlimeLick);
        // AcidSlimeSmall.Lick：同上（WeakTurns = 1）
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("AcidSlimeSmall", "LICK", AcidSlimeSmallLick);
        // SpikeSlimeMedium.FlameTackle：攻击 + AddToCombatAndPreview<Slimed>(... SlimedCount ...)
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("SpikeSlimeMedium", "FLAME_TACKLE", SpikeSlimeFlameTackle);
        // SpikeSlimeMedium.Lick：PowerCmd.Apply<FrailPower>(target, FrailTurns, Creature, null)
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("SpikeSlimeMedium", "LICK", SpikeSlimeLick);

        // --- 蘑菇 / 颚虫 ---
        // FungiBeast.Grow：PowerCmd.Apply<StrengthPower>(Creature, StrengthAmount, Creature, null)
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("FungiBeast", "GROW", FungiBeastGrow);
        // JawWorm.Bellow：Apply<StrengthPower>(Creature, BellowStrength, ...) + GainBlock(Creature, BellowBlock, Move, null)
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("JawWorm", "BELLOW", JawWormBellow);
        // JawWorm.Thrash：攻击 + GainBlock(Creature, ThrashBlock, Move, null)
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("JawWorm", "THRASH", JawWormThrash);

        // --- 小鬼 ---
        // GremlinNob.Bellow：Apply<EnragePower>(Creature, EnrageAmount, Creature, null)
        // AFTP 在多人数 > 2 时把层数压成 1；求解器只跑单人，取 EnrageAmount。
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("GremlinNob", "BELLOW", GremlinNobBellow);
        // GremlinWizard.Charging：CurrentCharge++（到上限时只有台词变化）
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("GremlinWizard", "CHARGING", GremlinWizardCharging);
        // GremlinWizard.UltimateBlast：CurrentCharge = 0，然后才是攻击
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("GremlinWizard", "ULTIMATE_BLAST", GremlinWizardUltimateBlast);
        // GremlinFat.Smash：攻击 + Weak；致命敌人词缀下额外 Frail
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("GremlinFat", "SMASH", GremlinFatSmash);

        // --- 强盗 / 邪教徒 / 哨卫 ---
        // SlaverBlue.Rake：攻击 + PowerCmd.Apply<WeakPower>(target, WeakAmount, Creature, null)
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("SlaverBlue", "RAKE", SlaverBlueRake);
        // Cultist.Incantation：PowerCmd.Apply<RitualPower>(Creature, RitualAmount, Creature, null)
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("Cultist", "INCANTATION", CultistIncantation);
        // Sentry.Bolt：CardPileCmd.AddToCombatAndPreview<Dazed>(targets, Discard, DazedAmount, null)
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("Sentry", "BOLT", SentryBolt);

        // --- 虱子 ---
        // LouseGreen.SpitWeb：PowerCmd.Apply<WeakPower>(target, WeakAmount, Creature, null)（WeakAmount 是常量 2）
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("LouseGreen", "SPIT_WEB", LouseGreenSpitWeb);
        // LouseRed.Grow：PowerCmd.Apply<StrengthPower>(Creature, StrengthAmount, Creature, null)
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("LouseRed", "GROW", LouseRedGrow);

        // --- 强盗（偷金币） ---
        // Looter.Mug：攻击 + StealGold（ThieveryPower 的 Amount 与玩家金币取小）+ _mugCount++
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("Looter", "MUG", LooterMug);
        // Looter.Lunge：同上
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("Looter", "LUNGE", LooterLunge);
        // Looter.SmokeBomb：CreatureCmd.GainBlock(Creature, EscapeBlock, ValueProp.Move, null, false)
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("Looter", "SMOKE_BOMB", LooterSmokeBomb);
        // Looter.Escape：CreatureCmd.Escape(Creature, true)——施法者自己离场，两侧都要声明
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("Looter", "ESCAPE", LooterEscape);
        ThirdPartyAdapterRegistry.RegisterOwnerRemovingMove("Looter", "ESCAPE");
    }

    /// <summary>
    /// 声明需要冻结的数值成员。这些是 <c>private int X =&gt; AscensionHelper.GetValueIfAscension(...)</c>
    /// 形式的实例属性，求解器在根捕获时读一次，之后整场不变——和原版怪物同一口径。
    /// </summary>
    private static void RegisterFrozenValues()
    {
        ThirdPartyAdapterRegistry.RegisterStaticIntMembers("FungiBeast", "StrengthAmount");
        ThirdPartyAdapterRegistry.RegisterStaticIntMembers("JawWorm", "BellowStrength", "BellowBlock", "ThrashBlock");
        ThirdPartyAdapterRegistry.RegisterStaticIntMembers("GremlinNob", "EnrageAmount");
        ThirdPartyAdapterRegistry.RegisterStaticIntMembers("SlaverBlue", "WeakAmount");
        ThirdPartyAdapterRegistry.RegisterStaticIntMembers("LouseRed", "StrengthAmount");
        ThirdPartyAdapterRegistry.RegisterStaticIntMembers("Sentry", "DazedAmount");
        ThirdPartyAdapterRegistry.RegisterStaticIntMembers("Cultist", "RitualAmount");
    }

    /// <summary>
    /// 声明会随战斗推进变化、且分支／效果依赖的标量成员。
    /// 名单里的成员在根捕获时播种进模拟状态，并进入状态指纹与续用核对文本。
    /// </summary>
    private static void RegisterMonsterState()
    {
        ThirdPartyAdapterRegistry.RegisterMonsterStateMembers("GremlinWizard", "_currentCharge");
        // Looter 的分支 MUG_BRANCH 只看 _mugCount（Mug／Lunge 各 +1），必须随分支 Fork、
        // 进状态指纹，否则同一场里「已经偷过两次」的个体和没偷过的会被当成同一个状态。
        ThirdPartyAdapterRegistry.RegisterMonsterStateMembers("Looter", "_mugCount");
    }

    // === 行动实现（与 AFTP 源码逐行对应） ===

    private static bool AcidSlimeCorrosiveSpit(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        killedOwner = false;
        AddSlimedToCombat(simulator, player, _acidSlimeMediumSlimedCount);
        return true;
    }

    private static bool AcidSlimeLick(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        killedOwner = false;
        Debuff<WeakPower>(simulator, combat, player, _acidSlimeMediumWeakTurns, move);
        return true;
    }

    private static bool AcidSlimeSmallLick(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        killedOwner = false;
        Debuff<WeakPower>(simulator, combat, player, _acidSlimeSmallWeakTurns, move);
        return true;
    }

    private static bool SpikeSlimeFlameTackle(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        killedOwner = false;
        AddSlimedToCombat(simulator, player, _spikeSlimeMediumSlimedCount);
        return true;
    }

    private static bool SpikeSlimeLick(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        killedOwner = false;
        Debuff<FrailPower>(simulator, combat, player, _spikeSlimeMediumFrailTurns, move);
        return true;
    }

    private static bool FungiBeastGrow(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        killedOwner = false;
        Buff<StrengthPower>(
            combat,
            move.Owner,
            combat.GetMonsterStaticInt(move.Owner, "StrengthAmount"));
        return true;
    }

    private static bool JawWormBellow(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        killedOwner = false;
        // 源码顺序：先力量，再格挡。
        Buff<StrengthPower>(combat, move.Owner, combat.GetMonsterStaticInt(move.Owner, "BellowStrength"));
        simulator.GainBlock(move.Owner, combat.GetMonsterStaticInt(move.Owner, "BellowBlock"), ValueProp.Move);
        return true;
    }

    private static bool JawWormThrash(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        killedOwner = false;
        simulator.GainBlock(move.Owner, combat.GetMonsterStaticInt(move.Owner, "ThrashBlock"), ValueProp.Move);
        return true;
    }

    private static bool GremlinNobBellow(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        killedOwner = false;
        Buff<EnragePower>(combat, move.Owner, combat.GetMonsterStaticInt(move.Owner, "EnrageAmount"));
        return true;
    }

    private static bool GremlinWizardCharging(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        killedOwner = false;
        combat.SetMonsterInt(
            move.Owner,
            "_currentCharge",
            combat.GetMonsterInt(move.Owner, "_currentCharge") + 1);
        return true;
    }

    private static bool GremlinWizardUltimateBlast(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        killedOwner = false;
        combat.SetMonsterInt(move.Owner, "_currentCharge", 0);
        return true;
    }

    private static bool GremlinFatSmash(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        killedOwner = false;
        // 源码：foreach (target in targets.Where(IsAlive)) { Apply<WeakPower>(WeakAmount); if (AppliesFrail) Apply<FrailPower>(FrailAmount); }
        Debuff<WeakPower>(simulator, combat, player, _gremlinFatWeakAmount, move);
        // AppliesFrail => AscensionHelper.HasAscension(DeadlyEnemies)：整轮不变的运行期属性，
        // 直接读同一个实例属性，与原实现同源。
        if (MonsterValueReader.ReadBool(move.Owner.Monster!, "AppliesFrail"))
            Debuff<FrailPower>(simulator, combat, player, _gremlinFatFrailAmount, move);
        return true;
    }

    private static bool SlaverBlueRake(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        killedOwner = false;
        Debuff<WeakPower>(simulator, combat, player, combat.GetMonsterStaticInt(move.Owner, "WeakAmount"), move);
        return true;
    }

    private static bool CultistIncantation(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        killedOwner = false;
        Buff<RitualPower>(combat, move.Owner, combat.GetMonsterStaticInt(move.Owner, "RitualAmount"));
        return true;
    }

    private static bool SentryBolt(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        killedOwner = false;
        simulator.AddToCombat<Dazed>(
            player,
            PileType.Discard,
            combat.GetMonsterStaticInt(move.Owner, "DazedAmount"),
            null);
        return true;
    }

    private static bool LouseGreenSpitWeb(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        killedOwner = false;
        Debuff<WeakPower>(simulator, combat, player, _louseGreenWeakAmount, move);
        return true;
    }

    private static bool LouseRedGrow(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        killedOwner = false;
        Buff<StrengthPower>(combat, move.Owner, combat.GetMonsterStaticInt(move.Owner, "StrengthAmount"));
        return true;
    }

    /// <summary>Looter.Mug：偷完金币记一次 <c>_mugCount++</c>（攻击部分由通用攻击循环结算）。</summary>
    private static bool LooterMug(
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

    /// <summary>Looter.Lunge：与 Mug 同一套「偷金币 + 记数」。</summary>
    private static bool LooterLunge(
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

    /// <summary>Looter.SmokeBomb：给自己 <c>EscapeBlock</c> 格挡（<c>ValueProp.Move</c>）。</summary>
    private static bool LooterSmokeBomb(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        killedOwner = false;
        simulator.GainBlock(move.Owner, _looterEscapeBlock, ValueProp.Move);
        return true;
    }

    /// <summary>
    /// Looter.Escape：<c>CreatureCmd.Escape(Creature, true)</c>——施法者自己离开战斗，
    /// 已经偷到手的金币不会再回来（那是 <c>OnDeath</c> 的奖励补偿，只走击杀那条路）。
    /// </summary>
    private static bool LooterEscape(
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

    /// <summary>
    /// 复刻 <c>Looter.StealGold</c>：对每一个 <c>ThieveryPower</c> 实例，目标活着且还有金币时
    /// 偷 <c>min(Amount, 玩家金币)</c>。求解器本体已有这条口径（原版 <c>GremlinMerc</c> 同款），
    /// 直接复用 <c>RecordThievery</c>，不自己算金币。
    /// </summary>
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

    // === 工具 ===

    /// <summary>
    /// 复刻 <c>CardPileCmd.AddToCombatAndPreview&lt;Slimed&gt;</c>：把史莱姆塞进玩家的弃牌堆。
    /// </summary>
    /// <remarks>
    /// 走的是求解器 <c>AddToCombat</c> 同一条路径、同一组守卫（目标玩家缺失或已死就什么都不做，
    /// 入堆位置一样是 <c>CardPilePosition.Bottom</c>，AFTP 源码里写的是 <c>(CardPilePosition)1</c>），
    /// 差别只在拿到生成出来的卡牌：往昔之章在生成期间依配置给它们打上「经典」标记，适配层必须在
    /// 预测里做同一件事，否则这些牌出牌时会按本体语义多抽一张（见 <see cref="ClassicSlimed"/>）。
    /// </remarks>
    private static void AddSlimedToCombat(
        CombatPredictionSimulator simulator,
        Creature player,
        int count)
    {
        Player? owner = player.Player ?? player.PetOwner;
        if (owner is null || simulator.State.GetCreature(owner.Creature).IsDead)
            return;
        foreach (SimCardPileAddResult added in simulator.CreateAndAddGeneratedCardsToCombat<Slimed>(
                     owner,
                     PileType.Discard,
                     count,
                     null))
        {
            ClassicSlimed.RecordGenerated(added.CardAdded);
        }
    }

    /// <summary>对怪物自己的增益：<c>PowerCmd.Apply&lt;T&gt;(ctx, Creature, amount, Creature, null)</c>。</summary>
    private static void Buff<T>(SimulatedCombatState combat, Creature owner, int amount) where T : PowerModel
        => combat.Apply<T>(owner, amount, owner);

    /// <summary>
    /// 对玩家的减益：<c>PowerCmd.Apply&lt;T&gt;(ctx, target, amount, Creature, null)</c>。
    /// 目标已死则跳过——AFTP 那一侧是 <c>targets.Where(t =&gt; t.IsAlive)</c>。
    /// </summary>
    private static void Debuff<T>(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        Creature target,
        int amount,
        ForecastMove move) where T : PowerModel
    {
        if (!simulator.State.GetCreature(target).IsAlive)
            return;
        combat.ApplyFromMonster<T>(target, amount, move.Owner);
    }
}
