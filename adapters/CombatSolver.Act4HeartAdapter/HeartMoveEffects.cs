using MegaCrit.Sts2.Core.Commands.Builders;
using MegaCrit.Sts2.Core.Entities.Ascension;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

// 「虚空」这张牌与 System.Void 同名，直接写 Void 会 CS0104。
using VoidCard = MegaCrit.Sts2.Core.Models.Cards.Void;

namespace CombatSolver.Act4HeartAdapter;

/// <summary>
/// Act4Heart 三个怪物的行动效果（非攻击部分）。
/// </summary>
/// <remarks>
/// 攻击部分由求解器的通用攻击循环按意图结算；这里只补行动里的减益、给牌、格挡、增益，
/// 以及每个行动对那个 <c>private byte state</c> 的推进——它就是
/// <c>POST_ATTACK_BRANCH</c> 的条件来源，见 <see cref="HeartBranchResolvers"/>。
/// 每条实现逐行对应 Act4Heart 源码里那个 <c>MoveState</c> 的回调，顺序也一致。
///
/// <para>
/// **只登记能完整建模的行动。** 三条 POST_ATTACK 分支之外的行动都能逐行复刻；其中唯一有保留的是
/// 盾兵 <c>BASH_MOVE</c> 的球位分支：源码在有球位时抽的是**怪物自己那条 RNG 流**
/// （<c>MonsterModel._rng</c>，按「run 种子 + 坐标 + CombatId」播种，与 <c>RunRng.MonsterAi</c> 无关），
/// 求解器不模拟这条流。适配层把实机实例上那条流的当前状态原样搬进分支状态（见
/// <see cref="ShieldOrbRngPredictionState"/>），在预测里**照着源码的抽样顺序复刻**，
/// 而不是绕过它——绕过去会让后面每一次抽样都错位。
/// </para>
/// </remarks>
internal static class HeartMoveEffects
{
    // === 行动 Id（与 Act4Heart 源码里的 MoveState 字符串逐字相同） ===

    internal const string DebilitateMove = "DEBILITATE_MOVE";
    internal const string BloodShotsMove = "BLOOD_SHOTS_MOVE";
    internal const string EchoMove = "ECHO_MOVE";
    internal const string BuffMove = "BUFF_MOVE";
    internal const string BashMove = "BASH_MOVE";
    internal const string FortifyMove = "FORTIFY_MOVE";
    internal const string SmashMove = "SMASH_MOVE";
    internal const string BurnStrikeMove = "BURN_STRIKE_MOVE";
    internal const string SkewerMove = "SKEWER_MOVE";
    internal const string PiercerMove = "PIERCER_MOVE";

    internal const string CorruptHeartType = "CorruptHeart";
    internal const string SpireShieldType = "SpireShield";
    internal const string SpireSpearType = "SpireSpear";

    // === 自检：与 Act4Heart 源码里的常量逐项核对 ===

    /// <summary>盾兵 FORTIFY 给全体队友的格挡（源码 <c>fortify_block</c>）。</summary>
    private static int _fortifyBlock = 30;

    /// <summary>矛兵 PIERCER 给全体队友的力量（源码 <c>piercer_amount</c>）。</summary>
    private static int _piercerAmount = 2;

    public static void Verify()
    {
        Type heart = A4hReflection.RequireMonsterType(CorruptHeartType);
        Type shield = A4hReflection.RequireMonsterType(SpireShieldType);
        Type spear = A4hReflection.RequireMonsterType(SpireSpearType);

        _fortifyBlock = A4hReflection.RequireStaticInt(shield, "fortify_block", 30);
        _piercerAmount = A4hReflection.RequireStaticInt(spear, "piercer_amount", 2);

        // 攻击行动的「已知稳定」声明就靠这些常量成立——它们会在意图构造时被烘进闭包，
        // 数值一变声明就不再成立（见 RegisterStableAttacks）。所以逐个钉死：对不上就整体拒绝登记。
        _ = A4hReflection.RequireStaticInt(heart, "blood_shots_damage", 2);
        _ = A4hReflection.RequireStaticInt(heart, "blood_shots_count", 15);
        _ = A4hReflection.RequireStaticInt(heart, "echo_damage", 45);
        _ = A4hReflection.RequireStaticInt(shield, "bash_damage", 14);
        _ = A4hReflection.RequireStaticInt(shield, "smash_damage", 38);
        _ = A4hReflection.RequireStaticInt(spear, "burn_strike_damage", 6);
        _ = A4hReflection.RequireStaticInt(spear, "burn_strike_count", 2);
        _ = A4hReflection.RequireStaticInt(spear, "skewer_damage", 10);
        _ = A4hReflection.RequireStaticInt(spear, "skewer_count", 4);

        // 效果里用到的第三方 Power 必须在场，否则 ApplyPower 会在预测中途才失败。
        _ = A4hReflection.RequirePowerType("BeatOfDeathPower");

        // 盾兵球位分支要照抄实机上那条私有 RNG 流。
        A4hReflection.RequireLiveMonsterRngField();
    }

    public static void RegisterAll()
    {
        // --- 腐化心脏：三轮循环 ---
        // debilite_move：Vulnerable 2 / Weak 2 / Frail 2，再把 5 张状态牌按随机位塞进抽牌堆
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect(CorruptHeartType, DebilitateMove, CorruptHeartDebilitate);
        // blod_shots_move：state |= 1，然后是 2×15 的攻击（攻击由通用循环结算）
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect(CorruptHeartType, BloodShotsMove, StateOrOnly);
        // echo_move：state |= 2，然后是 45 的单发攻击
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect(CorruptHeartType, EchoMove, SetEchoState);
        // buff_move：state = 0，力量清零/加 2，再按 buff_counter 发一份递增的增益
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect(CorruptHeartType, BuffMove, CorruptHeartBuff);

        // --- 盾兵 ---
        // bash_move：state |= 1，攻击后按球位/几率给 Strength -1 或 Focus -1
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect(SpireShieldType, BashMove, SpireShieldBash);
        // forify_move：state |= 2，给全体队友 30 格挡
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect(SpireShieldType, FortifyMove, SpireShieldFortify);
        // smash_move：state = 0，攻击后按难度给等量格挡（A9+ 固定 99）
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect(SpireShieldType, SmashMove, SpireShieldSmash);

        // --- 矛兵 ---
        // burn_strike_move：state |= 1，攻击后把 2 张灼伤放到抽牌堆顶部（A9 以下进弃牌堆）
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect(SpireSpearType, BurnStrikeMove, SpireSpearBurnStrike);
        // skewer_move：state = 0，然后是 4×10 的攻击
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect(SpireSpearType, SkewerMove, SetSkewerState);
        // piercer_move：state |= 2，给全体队友 +2 力量
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect(SpireSpearType, PiercerMove, SpireSpearPiercer);

        RegisterStableAttacks();
    }

    /// <summary>
    /// 声明三个怪物的攻击意图「数值已固定」，压掉意图预测器的「动态伤害」误报。
    /// </summary>
    /// <remarks>
    /// 反编译核对（Act4Heart 1.1.7）：腐化心脏 <c>BLOOD_SHOTS_MOVE</c> 是
    /// <c>MultiAttackIntent(blood_shots_damage, blood_shots_count)</c> 即 2×15，
    /// <c>ECHO_MOVE</c> 是 <c>SingleAttackIntent(echo_damage)</c> 即 45；盾兵 <c>BASH_MOVE</c> 14、
    /// <c>SMASH_MOVE</c> 38；矛兵 <c>BURN_STRIKE_MOVE</c> 6×2、<c>SKEWER_MOVE</c> 10×4。
    ///
    /// <para>
    /// 游戏本体的 <c>MultiAttackIntent(int, int)</c> 与 <c>SingleAttackIntent(int)</c> 都写作
    /// <c>DamageCalc = () =&gt; damage</c>，捕的是**构造实参**；段数是普通只读字段。
    /// 所以这些行动的伤害不随战斗状态变化，预测器按「<c>DamageCalc</c> 绑定了实例」判出来的「动态」
    /// 只是闭包显示类的假象。原版那批同类行动写死在 <c>IntentForecaster.IsKnownStableAttack</c> 里，
    /// 第三方只能在这里声明。
    /// </para>
    ///
    /// <para>
    /// 只声明**攻击部分**；这些行动的非攻击部分仍由上面的 <c>RegisterMonsterMoveEffect</c> 逐行复刻。
    /// </para>
    /// </remarks>
    private static void RegisterStableAttacks()
    {
        ThirdPartyAdapterRegistry.RegisterStableAttack(CorruptHeartType, BloodShotsMove);
        ThirdPartyAdapterRegistry.RegisterStableAttack(CorruptHeartType, EchoMove);
        ThirdPartyAdapterRegistry.RegisterStableAttack(SpireShieldType, BashMove);
        ThirdPartyAdapterRegistry.RegisterStableAttack(SpireShieldType, SmashMove);
        ThirdPartyAdapterRegistry.RegisterStableAttack(SpireSpearType, BurnStrikeMove);
        ThirdPartyAdapterRegistry.RegisterStableAttack(SpireSpearType, SkewerMove);
    }

    // === 行动实现（与 Act4Heart 源码逐行对应） ===

    private static bool CorruptHeartDebilitate(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        killedOwner = false;
        // 源码第一句就是 state = 0：三次削弱之后再回到「先打血弹」的那一支。
        combat.SetMonsterInt(move.Owner, HeartBranchResolvers.StateMember, 0);

        // PowerCmd.Apply<VulnerablePower/WeakPower/FrailPower>(ctx, targets, 2m, Creature, null, false)
        Debuff<VulnerablePower>(simulator, combat, player, 2, move.Owner);
        Debuff<WeakPower>(simulator, combat, player, 2, move.Owner);
        Debuff<FrailPower>(simulator, combat, player, 2, move.Owner);

        // 每个目标各拿 5 张：Dazed / Slimed / Wound / Burn / Void，进抽牌堆随机位置。
        // 源码写的是 (PileType)1 = Draw、(CardPilePosition)3 = Random。
        //
        // 这里的 Slimed 是**本体**语义（出牌抽 1 张）：往昔之章的「经典史莱姆」开关只作用于
        // 它自己的怪，心脏不是往昔之章的怪（往昔之章没做第四层），该功能不跨 Mod 生效。
        // 别把这张牌接到 ClassicSlimed.RecordGenerated 上。
        AddStatusCard<Dazed>(simulator, player);
        AddStatusCard<Slimed>(simulator, player);
        AddStatusCard<Wound>(simulator, player);
        AddStatusCard<Burn>(simulator, player);
        AddStatusCard<VoidCard>(simulator, player);
        return true;
    }

    /// <summary>血弹的 <c>state |= 1</c>，以及它之后 POST_ATTACK 的既有状态。</summary>
    private static bool StateOrOnly(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        killedOwner = false;
        SetStateBit(combat, move.Owner, 1);
        return true;
    }

    private static bool SetEchoState(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        killedOwner = false;
        SetStateBit(combat, move.Owner, 2);
        return true;
    }

    private static bool SetSkewerState(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        killedOwner = false;
        combat.SetMonsterInt(move.Owner, HeartBranchResolvers.StateMember, 0);
        return true;
    }

    private static bool CorruptHeartBuff(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        killedOwner = false;
        combat.SetMonsterInt(move.Owner, HeartBranchResolvers.StateMember, 0);

        // 源码：力量为负时先移除，再加 2。
        if (combat.GetAmount<StrengthPower>(move.Owner) < 0)
        {
            combat.SetAmount<StrengthPower>(move.Owner, 0);
        }
        combat.Apply<StrengthPower>(move.Owner, 2, move.Owner);

        // buff_counter++ 之后才 switch：第 1 次是神器，第 2 次才是死亡节拍，以此类推。
        int counter = combat.GetMonsterInt(move.Owner, HeartBranchResolvers.BuffCounterMember) + 1;
        combat.SetMonsterInt(move.Owner, HeartBranchResolvers.BuffCounterMember, counter);
        switch (counter)
        {
            case 1:
                combat.Apply<ArtifactPower>(move.Owner, 2, move.Owner);
                break;
            case 2:
                // 死亡节拍是第三方 Power，没有编译期类型可用，走按 Type 施加的重载。
                combat.ApplyPower(A4hReflection.RequirePowerType("BeatOfDeathPower"), move.Owner, 1, move.Owner);
                break;
            case 3:
                combat.Apply<PainfulStabsPower>(move.Owner, 1, move.Owner);
                break;
            case 4:
                combat.Apply<StrengthPower>(move.Owner, 10, move.Owner);
                break;
            default:
                combat.Apply<StrengthPower>(move.Owner, 50, move.Owner);
                break;
        }
        return true;
    }

    private static bool SpireShieldBash(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        killedOwner = false;
        SetStateBit(combat, move.Owner, 1);

        // 源码条件：!target.IsPlayer || capacity <= 0 || !(rng.NextFloat(1f) < odds)
        // → 单人战斗里 capacity <= 0 会短路，**一次 RNG 都不抽**；有球位才抽，且抽了就必须用掉。
        if (OrbCapacity(simulator, player) <= 0)
        {
            Debuff<StrengthPower>(simulator, combat, player, 1, move.Owner);
            return true;
        }

        float odds = A4hReflection.SpireShieldOrbsFocusDownOdds();
        ShieldOrbRngPredictionState state = ShieldOrbState(simulator, move.Owner);
        bool focus = state.RollNextFloat() < odds;
        // 已抽次数写进怪物标量状态，好让状态指纹看得见这条流走了多远：只在这条流上不同的两条分支
        // 否则会被当成同一个状态去重掉一条。
        combat.SetMonsterInt(move.Owner, HeartBranchResolvers.ShieldOrbRollsMember, state.Rolls);
        if (focus)
        {
            Debuff<FocusPower>(simulator, combat, player, 1, move.Owner);
        }
        else
        {
            Debuff<StrengthPower>(simulator, combat, player, 1, move.Owner);
        }
        return true;
    }

    private static bool SpireShieldFortify(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        killedOwner = false;
        SetStateBit(combat, move.Owner, 2);
        // 源码：CreatureCmd.GainBlock(a, fortify_block, (ValueProp)8 /*Move*/, null, false)
        // 对 GetTeammatesOf 的每一个（含自己）同时结算，彼此不互影响。GainBlock 自己会跳过已死的队友。
        foreach (Creature teammate in combat.GetTeammatesOf(move.Owner))
        {
            simulator.GainBlock(teammate, _fortifyBlock, ValueProp.Move);
        }
        return true;
    }

    private static bool SpireShieldSmash(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        killedOwner = false;
        combat.SetMonsterInt(move.Owner, HeartBranchResolvers.StateMember, 0);
        // 源码：A9（DeadlyEnemies）及以上固定 99 格挡；以下按这次砸击**实际造成的伤害**等量格挡。
        if (AscensionHelper.HasAscension(AscensionLevel.DeadlyEnemies))
        {
            simulator.GainBlock(move.Owner, 99m, ValueProp.Move);
            return true;
        }
        simulator.GainBlock(move.Owner, LastAttackTotalDamage(simulator, move.Owner), ValueProp.Unpowered);
        return true;
    }

    private static bool SpireSpearBurnStrike(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        killedOwner = false;
        SetStateBit(combat, move.Owner, 1);
        // 源码 burn_strike_pile：A9 及以上是 Draw，否则 Discard；位置固定 (CardPilePosition)2 = Top。
        PileType pile = AscensionHelper.HasAscension(AscensionLevel.DeadlyEnemies)
            ? PileType.Draw
            : PileType.Discard;
        simulator.AddToCombat<Burn>(player, pile, 2, null, CardPilePosition.Top);
        return true;
    }

    private static bool SpireSpearPiercer(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        killedOwner = false;
        SetStateBit(combat, move.Owner, 2);
        // 源码：PowerCmd.Apply<StrengthPower>(ctx, a, piercer_amount, Creature, null, false)，a 取全体队友。
        foreach (Creature teammate in combat.GetTeammatesOf(move.Owner))
        {
            combat.ApplyFromMonster<StrengthPower>(teammate, _piercerAmount, move.Owner);
        }
        return true;
    }

    // === 工具 ===

    /// <summary>源码里的 <c>state |= bit</c>。</summary>
    private static void SetStateBit(SimulatedCombatState combat, Creature owner, int bit)
        => combat.SetMonsterInt(
            owner,
            HeartBranchResolvers.StateMember,
            combat.GetMonsterInt(owner, HeartBranchResolvers.StateMember) | bit);

    /// <summary>
    /// 对玩家的减益：<c>PowerCmd.Apply&lt;T&gt;(ctx, targets, amount, Creature, null, false)</c>。
    /// 神器格挡与「目标已死」都在求解器的 Apply 里处理，这里不再自己过滤——源码也没有过滤。
    /// </summary>
    private static void Debuff<T>(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        Creature target,
        int amount,
        Creature applier) where T : PowerModel
        => combat.ApplyFromMonster<T>(target, amount, applier);

    /// <summary>
    /// <c>CombatState.CreateCard&lt;T&gt;(player)</c> 后 <c>AddGeneratedCardToCombat(..., Draw, null, Random)</c>。
    /// </summary>
    private static void AddStatusCard<TCard>(CombatPredictionSimulator simulator, Creature player)
        where TCard : CardModel
        => simulator.AddToCombat<TCard>(player, PileType.Draw, 1, null, CardPilePosition.Random);

    /// <summary>玩家当前球位数（<c>OrbQueue.Capacity</c>）；不是玩家时按 0 处理。</summary>
    private static int OrbCapacity(CombatPredictionSimulator simulator, Creature player)
        => player.Player is { } owner
            ? simulator.State.GetPlayerCombatState(owner).OrbQueue.Capacity
            : 0;

    /// <summary>
    /// 这次砸击实际打出的总伤害（源码 <c>val.Results</c> 展平后累加 <c>TotalDamage</c>）。
    /// </summary>
    /// <remarks>
    /// 行动效果是在求解器结算完这次攻击之后才调用的，而攻击结束时会往预测历史里写一条
    /// <c>CombatPredictionCreatureAttackedEntry</c>。从历史尾部往回找最近一条由这只怪物发起的攻击，
    /// 就是这次砸击——同一分支内中间不会再插进别的攻击条目。历史本身随分支 Fork，
    /// 所以这里读到的一定是当前分支的结果，不会串到别的分支。
    /// </remarks>
    private static decimal LastAttackTotalDamage(CombatPredictionSimulator simulator, Creature attacker)
    {
        IReadOnlyList<CombatPredictionHistoryEntry> entries = simulator.History.Entries;
        for (int index = entries.Count - 1; index >= 0; index--)
        {
            if (entries[index] is not CombatPredictionCreatureAttackedEntry entry
                || !ReferenceEquals(entry.Attacker, attacker))
            {
                continue;
            }
            decimal total = 0m;
            for (int hit = 0; hit < entry.HitResults.Count; hit++)
            {
                total += entry.HitResults[hit].TotalDamage;
            }
            return total;
        }
        throw new PredictionUnsupportedException(
            "盾兵砸击的暂存伤害读不到对应攻击记录；预测历史的形状已变动。");
    }

    /// <summary>
    /// 取盾兵那条私有 RNG 流的分支副本；还没有就地建一份，初值取自实机实例上的当前状态。
    /// </summary>
    private static ShieldOrbRngPredictionState ShieldOrbState(
        CombatPredictionSimulator simulator,
        Creature owner)
        => simulator.StateStore.Get(
            owner.Monster ?? throw new PredictionUnsupportedException("盾兵的球位分支缺少怪物模型。"),
            static monster => new ShieldOrbRngPredictionState(monster));

    /// <summary>
    /// 盾兵私有 RNG 流（<c>MonsterModel._rng</c>）在预测里的分支副本。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 这条流由 <c>CombatState</c> 在战斗开始时按「run 种子 + 地图坐标 + CombatId」播种，一局里只有
    /// Act4Heart 的盾兵会推动它（原版只拿它做外观），所以实机实例上的当前状态就是「本场战斗到目前为止
    /// 抽过几次」的准确记录，预测期间也不会被别人改。第一次取用时按实机状态整份拷一份，
    /// Fork 时再整份拷走，此后与实机彻底脱钩。
    /// </para>
    /// <para>
    /// <see cref="Rolls"/> 会被写进怪物标量状态，于是只差这条流进度的两条分支不会再被去重成一条。
    /// </para>
    /// </remarks>
    internal sealed class ShieldOrbRngPredictionState : IPredictionStateForkable
    {
        private readonly Rng _rng;

        public ShieldOrbRngPredictionState(MonsterModel monster)
        {
            _rng = A4hReflection.LiveMonsterRng(monster) is Rng live
                ? live.CaptureState().ToRng()
                : throw new PredictionUnsupportedException(
                    "盾兵的怪物实例上没有 RNG 流（MonsterModel._rng），无法复刻球位分支的抽样。");
        }

        private ShieldOrbRngPredictionState(Rng rng, int rolls)
        {
            _rng = rng;
            Rolls = rolls;
        }

        /// <summary>已经抽过几次——对应源码里那条流被推动的次数。</summary>
        public int Rolls { get; private set; }

        /// <summary>源码里的 <c>Rng.NextFloat(1f)</c>：先推进，再取值。</summary>
        public float RollNextFloat()
        {
            Rolls++;
            return _rng.NextFloat(1f);
        }

        public object Fork(PredictionForkContext context)
        {
            _ = context;
            return new ShieldOrbRngPredictionState(_rng.CaptureState().ToRng(), Rolls);
        }
    }
}
