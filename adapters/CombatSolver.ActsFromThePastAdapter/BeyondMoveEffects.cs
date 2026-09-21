using System.Reflection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Attack;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Damage;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Death;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.TurnEnd;
using CombatSolver.Engine.InCombat.Simulation;
namespace CombatSolver.ActsFromThePastAdapter;

/// <summary>
/// 往昔之章第三幕（The Beyond）怪物的行动效果（非攻击部分）。
/// </summary>
/// <remarks>
/// 与第一、二幕同一条纪律：逐行对照 AFTP 源码里那个 <c>MoveState</c> 的回调，顺序也一致；
/// 攻击部分仍由求解器的通用攻击循环按意图结算。
/// </remarks>
internal static class BeyondMoveEffects
{
    private static readonly string[] MonsterTypes =
    [
        "Repulsor",
        "SnakeDagger",
        "Spiker",
        "OrbWalker",
        "SpireGrowth",
        "Maw",
        "GiantHead",
        "Reptomancer",
        "Exploder",
        "Donu",
        "Deca",
        "SnakePlant",
    ];

    /// <summary>蛇草 SPORES 给的虚弱／破甲层数（AFTP <c>DebuffAmount</c>）。</summary>
    private static int _snakePlantDebuffAmount;

    /// <summary>AFTP 自己的 <c>PlatedArmorPower</c>（与原版 <c>PlatingPower</c> 是两个类型）。</summary>
    private static Type _platedArmorType = null!;

    /// <summary>AFTP 自己的 <c>MalleablePower</c>（蛇草挂的那一个）。</summary>
    private static Type _malleableType = null!;

    /// <summary>它的私有 <c>_pendingBlock</c>：每次挨打累加，下一次攻击或回合末清空。</summary>
    private static FieldInfo _malleablePendingBlock = null!;

    /// <summary>读实机／影子实例上的 <c>_pendingBlock</c>（越界或字段缺失都显式失败）。</summary>
    internal static int ReadMalleablePendingBlock(PowerModel power)
        => _malleablePendingBlock.GetValue(power) is decimal value
            ? (int)value
            : throw new InvalidOperationException("MalleablePower._pendingBlock 不是 decimal。");

    /// <summary>Deca 的「保护之方」给每个存活队友的格挡与镀甲层数。</summary>
    private static int _decaProtectBlock;
    private static int _decaProtectPlatedArmor;

    /// <summary>Donu 的「保护之环」给每个存活队友的力量（AFTP <c>CircleStrengthAmount</c>）。</summary>
    private static int _donuCircleStrengthAmount;

    /// <summary>Exploder 自爆前的回合数（AFTP <c>ExplosiveCountdown</c>）；分支解析器也用它。</summary>
    internal static int ExploderCountdown = 3;

    /// <summary>AFTP 自己的 <c>ConstrictedPower</c>（与原版 <c>ConstrictPower</c> 是两个类型）。</summary>
    private static Type _constrictedPowerType = null!;

    /// <summary>SpireGrowth 每次缠绕的层数（AFTP <c>ConstrictAmount</c>，A9+ 12／否则 10）。</summary>
    private static int _spireGrowthConstrictAmount;

    /// <summary>GiantHead 每次 COUNT 递减后给 IT_IS_TIME 加的伤害（AFTP <c>IncrementDmg</c>）。</summary>
    private static int _giantHeadIncrementDmg;

    /// <summary>GiantHead 的 GLARE 给的虚弱层数（AFTP <c>GlareDuration</c>）。</summary>
    private static int _giantHeadGlareDuration;

    /// <summary>Repulsor 的 Daze 张数（AFTP <c>DazeAmount</c>）。</summary>
    private static int _repulsorDazeAmount;

    /// <summary>Spiker 每次加荆棘的层数（AFTP <c>BuffAmount</c>）。</summary>
    private static int _spikerBuffAmount;

    public static void Verify()
    {
        foreach (string typeName in MonsterTypes)
            AfpReflection.RequireMonsterType(typeName);
        _repulsorDazeAmount = AfpReflection.RequireConst("Repulsor", "DazeAmount", 2);
        _spikerBuffAmount = AfpReflection.RequireConst("Spiker", "BuffAmount", 2);
        // ConstrictAmount 是 A9 分支的运行期属性（没有可钉的常量），所以它走静态数值成员：
        // 适配层的自检只核对类型与两个钩子的签名，数值由根捕获读一次。
        _constrictedPowerType = AfpReflection.RequireType("ActsFromThePast.ConstrictedPower");
        _ = AfpReflection.RequireOverride("ConstrictedPower", "AfterSideTurnEnd", 3);
        _ = AfpReflection.RequireOverride("ConstrictedPower", "AfterDeath", 4);
        _giantHeadIncrementDmg = AfpReflection.RequireConst("GiantHead", "IncrementDmg", 5);
        _giantHeadGlareDuration = AfpReflection.RequireConst("GiantHead", "GlareDuration", 1);
        _snakeDaggerType = AfpReflection.RequireType("ActsFromThePast.SnakeDagger");
        ExploderCountdown = AfpReflection.RequireConst("Exploder", "ExplosiveCountdown", 3);
        _donuCircleStrengthAmount = AfpReflection.RequireConst("Donu", "CircleStrengthAmount", 3);
        _platedArmorType = AfpReflection.RequireType("ActsFromThePast.PlatedArmorPower");
        _ = AfpReflection.RequireOverride("PlatedArmorPower", "BeforeSideTurnStart", 4);
        _ = AfpReflection.RequireOverride("PlatedArmorPower", "BeforeSideTurnEndEarly", 3);
        _ = AfpReflection.RequireOverride("PlatedArmorPower", "AfterDamageReceived", 6);
        _decaProtectBlock = AfpReflection.RequireConst("Deca", "ProtectBlock", 16);
        _decaProtectPlatedArmor = AfpReflection.RequireConst("Deca", "ProtectPlatedArmorAmount", 3);
        _malleableType = AfpReflection.RequireType("ActsFromThePast.MalleablePower");
        _ = AfpReflection.RequireOverride("MalleablePower", "AfterDamageReceived", 6);
        _ = AfpReflection.RequireOverride("MalleablePower", "AfterAttack", 2);
        _ = AfpReflection.RequireOverride("MalleablePower", "AfterSideTurnEnd", 3);
        _malleablePendingBlock = _malleableType.GetField(
                "_pendingBlock",
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "ActsFromThePast.MalleablePower._pendingBlock 不存在，往昔之章版本可能已变动。");
        _snakePlantDebuffAmount = AfpReflection.RequireConst("SnakePlant", "DebuffAmount", 2);
    }

    public static void RegisterAll()
    {
        // Repulsor.Daze：往**抽牌堆的随机位置**塞 DazeAmount 张 Dazed（意图是 StatusIntent，攻击部分没有）。
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("Repulsor", "DAZE", RepulsorDaze);

        // Spiker：开场的 StartingThorns 层荆棘发生在 AfterAddedToRoom（已在根里），这里只补 BuffThorns
        // 那一步（自己记一次数 + 再挂 BuffAmount 层荆棘）；分支要读这个计数，所以它进状态名单。
        ThirdPartyAdapterRegistry.RegisterMonsterStateMembers("Spiker", "_thornsCount");
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("Spiker", "BUFF_THORNS", SpikerBuffThorns);

        // SnakeDagger（ Reptomancer 召唤的蛇匕首）：
        // WoundStab 那次 9 点攻击已由通用攻击循环按意图结算，这里只补 1 张 Wound 进弃牌堆；
        // Explode 的 25 点攻击同理（DeathBlowIntent 就是 SingleAttackIntent 的派生），这里只补自杀。
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("SnakeDagger", "WOUND_STAB", SnakeDaggerWoundStab);
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("SnakeDagger", "EXPLODE", SnakeDaggerExplode);
        ThirdPartyAdapterRegistry.RegisterOwnerRemovingMove("SnakeDagger", "EXPLODE");

        // OrbWalker：LASER 的单次攻击由通用攻击循环结算，这里补它塞的两张 Burn——**一张进弃牌堆底部、
        // 一张进抽牌堆随机位置**（源码就是两次不同的 AddGeneratedCardToCombat，不是同一种入堆）。
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("OrbWalker", "LASER", OrbWalkerLaser);

        // OrbWalker 开场挂的那个 AFTP StrengthUpPower 重写的是**常规（非 Late）AfterSideTurnEnd**：
        // 自己那一方回合末按层数给自己加力量。开场的施加发生在 AfterAddedToRoom（已在根里），
        // 这里只登记它在回合末的行为——这也是本轮新入口的第一家用户。
        ThirdPartyAdapterRegistry.RegisterSideTurnEndPower("StrengthUpPower", StrengthUpPowerTurnEnd);

        // --- 塔蔓（SpireGrowth） ---
        // CONSTRICT：给每个活着的玩家挂 ConstrictAmount 层 AFTP 自己的 ConstrictedPower；
        // 那个 Power 的两个钩子分别登记成「自己那一方回合末按层数吃 Unpowered 伤害」与
        // 「施加者死亡时移除自己」——后者是名字带 Death 的重写，不登记会让整场给不出战损。
        ThirdPartyAdapterRegistry.RegisterStaticIntMembers("SpireGrowth", "ConstrictAmount");
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("SpireGrowth", "CONSTRICT", SpireGrowthConstrict);
        ThirdPartyAdapterRegistry.RegisterSideTurnEndPower("ConstrictedPower", ConstrictedPowerTurnEnd);
        AfterDeathMirrors.Register(_constrictedPowerType, ConstrictedPowerAfterDeath);

        // --- 大嘴（Maw） ---
        // 开场 _turnCount = 1、_roared = false 是字段初值（根捕获时读实机实例）；分支每回合 +1 并读这两个值。
        ThirdPartyAdapterRegistry.RegisterMonsterStateMembers("Maw", "_turnCount", "_roared");
        ThirdPartyAdapterRegistry.RegisterStaticIntMembers("Maw", "TerrifyDuration", "StrUp");
        // ROAR：给每个活着的目标 TerrifyDuration 层虚弱与破甲，然后把自己标记成「已咆哮」。
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("Maw", "ROAR", MawRoar);
        // DROOL：给自己 StrUp 点力量。
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("Maw", "DROOL", MawDrool);
        // NOMNOMNOM_MULTI 的段数是 TurnCount / 2 现算的（NOMNOMNOM_SINGLE 是常量 5，已在常量表里）。
        ThirdPartyAdapterRegistry.RegisterMonsterAttackValues("Maw", "NOMNOMNOM_MULTI", MawNomNomNom);
        // BeforeDeath 只有一句死亡音效，登记为忽略（名字带 Death，不登记会让整场给不出战损）。
        BeforeDeathMirrors.RegisterIgnored(AfpReflection.RequireType("ActsFromThePast.Maw"));

        // --- 巨头（GiantHead） ---
        // 开场 _count = 4（A8+）／5 与 1 层 SlowPower 都发生在 AfterAddedToRoom（已在根里）；
        // 分支每回合减一次计数，所以它进状态名单。
        ThirdPartyAdapterRegistry.RegisterMonsterStateMembers("GiantHead", "_count");
        ThirdPartyAdapterRegistry.RegisterStaticIntMembers("GiantHead", "StartingDeathDmg");
        // GLARE：给每个活着的目标 GlareDuration 层虚弱。
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("GiantHead", "GLARE", GiantHeadGlare);
        // COUNT：意图表里挂了一个 DebuffIntent，但源码回调只打 13 点（没有任何减益实现），
        // 所以登记成空操作——它表达的是「这条行动的非攻击部分就是没有」，免得界面报一条假的未支持意图。
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("GiantHead", "COUNT", NoNonAttackEffect);
        // IT_IS_TIME 的伤害是现算的：StartingDeathDmg - Count * IncrementDmg（Count 为负时继续变大）。
        ThirdPartyAdapterRegistry.RegisterMonsterAttackValues("GiantHead", "IT_IS_TIME", GiantHeadItIsTime);
        // BeforeDeath 只有一句死亡音效，登记为忽略（名字带 Death，不登记会让整场给不出战损）。
        BeforeDeathMirrors.RegisterIgnored(AfpReflection.RequireType("ActsFromThePast.GiantHead"));

        // --- 蛇怪术士（Reptomancer） ---
        // 开场的 SPAWN_DAGGER 是初始行动（MoveState，不是分支）；它带来的匕首在 AfterAddedToRoom 里被
        // 挂上 MinionPower（已在根里）。这里要补的是战斗中召唤的那一步与 SNAKE_STRIKE 的虚弱。
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("Reptomancer", "SPAWN_DAGGER", ReptomancerSpawnDagger);
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("Reptomancer", "SNAKE_STRIKE", ReptomancerSnakeStrike);

        // --- 自爆虫（Exploder） ---
        // 开场 _turnCount = 0（AfterAddedToRoom，已在根里）；分支每回合 +1，所以它进状态名单。
        ThirdPartyAdapterRegistry.RegisterMonsterStateMembers("Exploder", "_turnCount");
        // EXPLODE：DeathBlowIntent 就是攻击意图（SingleAttackIntent 的派生），30 点伤害由通用攻击循环
        // 按意图结算，这里只补源码最后那句 CreatureCmd.Kill(自己, false)。
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("Exploder", "EXPLODE", ExploderExplode);
        ThirdPartyAdapterRegistry.RegisterOwnerRemovingMove("Exploder", "EXPLODE");

        // --- 多努（Donu，与 Deca 同场） ---
        // 初始行动是 CIRCLE_OF_PROTECTION、之后与 BEAM 交替（没有分支）；开场的 Artifact 发生在
        // AfterAddedToRoom（已在根里）。要补的只有「保护之环」那一下给全体存活队友的力量。
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect(
            "Donu",
            "CIRCLE_OF_PROTECTION",
            DonuCircleOfProtection);

        // --- 戴卡（Deca，与 Donu 同场） ---
        // 行动在 BEAM 与 SQUARE_OF_PROTECTION 之间交替（没有分支）；开场的 Artifact 在 AfterAddedToRoom。
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("Deca", "BEAM", DecaBeam);
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect(
            "Deca",
            "SQUARE_OF_PROTECTION",
            DecaSquareOfProtection);
        // AFTP 自己的 PlatedArmorPower（Deca 的「保护之方」挂的那一个）：三个钩子逐一登记。
        ThirdPartyAdapterRegistry.RegisterSideTurnStartPower("PlatedArmorPower", PlatedArmorStart);
        BeforeSideTurnEndMirrors.RegisterEarly(_platedArmorType, PlatedArmorTurnEnd);
        AfterDamageReceivedMirrors.Register(_platedArmorType, PlatedArmorDamageReceived);

        // --- AFTP MalleablePower（蛇草的「可塑」；蛇草本体下一批接） ---
        // 它的私有 _pendingBlock 会在克隆时丢掉，所以按心脏无敌那套：根捕获把实机值搬进预测状态，
        // 并登记一个指纹槽（只在累计值上不同的两条分支否则会被去重掉一条）。
        AfterDamageReceivedMirrors.Register(_malleableType, MalleableDamageReceived);
        AfterAttackMirrors.Register(_malleableType, MalleableAfterAttack);
        ThirdPartyAdapterRegistry.RegisterSideTurnEndPower("MalleablePower", MalleableSideTurnEnd);
        PowerHiddenStateMirrors.RegisterRootCapture(
            _malleableType,
            (simulator, clone, original) => _ = simulator.StateStore.GetReadOnly(
                clone,
                () => new MalleablePendingBlockState(original)));
        PowerHiddenStateMirrors.Register(
            _malleableType,
            "pendingBlock",
            static (simulator, power) => simulator.StateStore
                .Peek(power, () => new MalleablePendingBlockState(power))
                .PendingBlock);

        // --- 蛇草（SnakePlant） ---
        // 开场的 3 层 MalleablePower 发生在 AfterAddedToRoom（已在根里，镜像见上）；
        // CHOMP 是常量构造的三段攻击；这里只补 SPORES。
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("SnakePlant", "SPORES", SnakePlantSpores);
    }

    /// <summary>SnakePlant.Spores：给每个活着的目标 <c>DebuffAmount</c> 层破甲与虚弱（施加者是蛇草）。</summary>
    private static bool SnakePlantSpores(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        _ = plannedChoices;
        killedOwner = false;
        if (!simulator.State.GetCreature(player).IsAlive)
            return true;
        combat.Apply<FrailPower>(player, _snakePlantDebuffAmount, move.Owner);
        combat.Apply<WeakPower>(player, _snakePlantDebuffAmount, move.Owner);
        return true;
    }

    /// <summary>
    /// AFTP <c>MalleablePower.AfterDamageReceived</c>：持有者吃到未被格挡的 <c>Move</c> 伤害（非
    /// <c>Unpowered</c>）且还活着时，把**当前层数**累加进 <c>_pendingBlock</c>，然后自己的层数 +1。
    /// </summary>
    private static void MalleableDamageReceived(AbstractModel model, AfterDamageReceivedMirrorContext context)
    {
        PowerModel power = (PowerModel)model;
        if (context.Target != power.Owner
            || context.Result.UnblockedDamage <= 0
            || !context.Props.HasFlag(ValueProp.Move)
            || context.Props.HasFlag(ValueProp.Unpowered)
            || context.Simulator.State.GetCreature(power.Owner).CurrentHp <= 0)
        {
            return;
        }
        ICombatPredictionEffectSink effects = context.CombatState as ICombatPredictionEffectSink
            ?? throw new PredictionUnsupportedException("可塑缺少可写的预测状态。");
        MalleablePendingBlockState state = context.Simulator.StateStore
            .Get(power, () => new MalleablePendingBlockState(power));
        state.PendingBlock += power.Amount;
        effects.SetPowerAmount(power, power.Amount + 1);
    }

    /// <summary>
    /// AFTP <c>MalleablePower.AfterAttack</c>：只要还有累计值就把它们换成 <c>Unpowered</c> 格挡并清零。
    /// </summary>
    /// <remarks>源码对「是谁打的」不加任何条件——任何一次攻击命令都会把它兑现，这里照抄。</remarks>
    private static void MalleableAfterAttack(AbstractModel model, AfterAttackMirrorContext context)
    {
        PowerModel power = (PowerModel)model;
        MalleablePendingBlockState state = context.Simulator.StateStore
            .Get(power, () => new MalleablePendingBlockState(power));
        if (state.PendingBlock <= 0)
            return;
        context.Simulator.GainBlock(power.Owner, state.PendingBlock, ValueProp.Unpowered);
        state.PendingBlock = 0;
    }

    /// <summary>
    /// AFTP <c>MalleablePower.AfterSideTurnEnd</c>（常规、非 Late）：自己那一方回合末先兑现剩余累计值，
    /// 再把层数回滚到施加时的 <c>BaseAmount</c>。
    /// </summary>
    private static void MalleableSideTurnEnd(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        PowerModel power,
        CombatSide side,
        IReadOnlyCollection<Creature> participants)
    {
        _ = participants;
        if (side != power.Owner.Side)
            return;
        MalleablePendingBlockState state = simulator.StateStore
            .Get(power, () => new MalleablePendingBlockState(power));
        if (state.PendingBlock > 0)
        {
            simulator.GainBlock(power.Owner, state.PendingBlock, ValueProp.Unpowered);
            state.PendingBlock = 0;
        }
        int baseAmount = power.DynamicVars["BaseAmount"].IntValue;
        if (power.Amount == baseAmount)
            return;
        ICombatPredictionEffectSink effects = combat as ICombatPredictionEffectSink
            ?? throw new PredictionUnsupportedException("可塑缺少可写的预测状态。");
        effects.SetPowerAmount(power, baseAmount);
    }

    /// <summary>
    /// AFTP <c>MalleablePower</c> 的私有 <c>_pendingBlock</c> 在预测里的分支副本。
    /// </summary>
    internal sealed class MalleablePendingBlockState : IPredictionStateForkable
    {
        public MalleablePendingBlockState(PowerModel power)
            => PendingBlock = ReadMalleablePendingBlock(power);

        public int PendingBlock { get; set; }

        public object Fork(PredictionForkContext context) => MemberwiseClone();
    }

    /// <summary>Deca.Beam：两段攻击由通用攻击循环结算，这里补攻击后塞进弃牌堆底部的 2 张 Dazed。</summary>
    private static bool DecaBeam(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        _ = combat;
        _ = move;
        _ = plannedChoices;
        killedOwner = false;
        simulator.AddToCombat<Dazed>(player, PileType.Discard, 2, null, CardPilePosition.Bottom);
        return true;
    }

    /// <summary>
    /// Deca.SquareOfProtection：给每个存活队友（含自己）<c>ProtectBlock</c> 点格挡（<c>Move</c>）与
    /// <c>ProtectPlatedArmorAmount</c> 层 AFTP <c>PlatedArmorPower</c>。
    /// </summary>
    private static bool DecaSquareOfProtection(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        _ = player;
        _ = plannedChoices;
        killedOwner = false;
        foreach (Creature teammate in combat.GetTeammatesOf(move.Owner))
        {
            if (!simulator.State.GetCreature(teammate).IsAlive)
                continue;
            simulator.GainBlock(teammate, _decaProtectBlock, ValueProp.Move);
            combat.ApplyPower(_platedArmorType, teammate, _decaProtectPlatedArmor, move.Owner);
        }
        return true;
    }

    /// <summary>
    /// AFTP <c>PlatedArmorPower.BeforeSideTurnStart</c>：只在**第 1 回合、玩家侧开始时**，
    /// 敌人身上的镀甲按层数补一次 <c>Unpowered</c> 格挡（判据与源码逐字一致：
    /// <c>Owner.Side == Enemy &amp;&amp; side == Player &amp;&amp; RoundNumber == 1</c>）。
    /// </summary>
    private static void PlatedArmorStart(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        PowerModel power)
    {
        if (combat.CurrentSide != CombatSide.Player || combat.RoundNumber != 1 || !power.Owner.IsEnemy)
            return;
        simulator.GainBlock(power.Owner, power.Amount, ValueProp.Unpowered);
    }

    /// <summary>AFTP <c>PlatedArmorPower.BeforeSideTurnEndEarly</c>：自己那一方回合末按层数获得格挡。</summary>
    private static void PlatedArmorTurnEnd(AbstractModel model, BeforeSideTurnEndMirrorContext context)
    {
        PowerModel power = (PowerModel)model;
        if (context.Side != power.Owner.Side)
            return;
        context.Simulator.GainBlock(power.Owner, power.Amount, ValueProp.Unpowered);
    }

    /// <summary>
    /// AFTP <c>PlatedArmorPower.AfterDamageReceived</c>：持有者吃到**未被格挡的 <c>Move</c> 伤害**（不是
    /// <c>Unpowered</c>）时减 1 层。
    /// </summary>
    /// <remarks>
    /// 源码在层数归零且持有者是**甲壳寄生虫**时还会调 <c>OnArmorBreak()</c>；那只怪还没适配，
    /// 所以这里**显式失败**而不是静默跳过——装一半比不装更糟。
    /// </remarks>
    private static void PlatedArmorDamageReceived(
        AbstractModel model,
        AfterDamageReceivedMirrorContext context)
    {
        PowerModel power = (PowerModel)model;
        if (context.Target != power.Owner
            || context.Result.UnblockedDamage <= 0
            || !context.Props.HasFlag(ValueProp.Move)
            || context.Props.HasFlag(ValueProp.Unpowered))
        {
            return;
        }
        ICombatPredictionEffectSink effects = context.CombatState as ICombatPredictionEffectSink
            ?? throw new PredictionUnsupportedException("镀甲缺少可写的预测状态。");
        effects.ApplyPower(_platedArmorType, power.Owner, -1, power.Applier);
        if (power.Amount - 1 <= 0
            && string.Equals(
                power.Owner.Monster?.GetType().Name,
                "ShelledParasite",
                StringComparison.Ordinal))
        {
            throw new PredictionUnsupportedException(
                "镀甲层数归零时的甲壳寄生虫破甲（OnArmorBreak）还没有适配。");
        }
    }

    /// <summary>
    /// Donu.CircleOfProtection：给每个存活队友（含自己）挂 <c>CircleStrengthAmount</c> 点力量；
    /// <c>Beam</c> 的两段攻击由通用攻击循环按意图结算，没有非攻击部分。
    /// </summary>
    private static bool DonuCircleOfProtection(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        _ = player;
        _ = plannedChoices;
        killedOwner = false;
        foreach (Creature teammate in combat.GetTeammatesOf(move.Owner))
        {
            if (simulator.State.GetCreature(teammate).IsAlive)
                combat.Apply<StrengthPower>(teammate, _donuCircleStrengthAmount, move.Owner);
        }
        return true;
    }

    /// <summary>
    /// Exploder.Explode：伤害由通用攻击循环按 <c>DeathBlowIntent(30)</c> 结算，效果侧只补自杀
    /// （与 <c>SnakeDagger.EXPLODE</c> 同型）。
    /// </summary>
    /// <remarks>
    /// 源码这一下用的是 <c>CreatureCmd.Damage</c>（直伤）而不是 <c>DamageCmd.Attack</c>；求解器按意图把它
    /// 当攻击命中结算。两条路在这个核心里走的是同一套伤害管线（同样的 <c>ValueProp.Move</c>、同样的
    /// <c>BeforeDamageReceived</c> 反伤），差别只落在「会不会派发 <c>AfterAttack</c>」——而核心登记的那些
    /// <c>AfterAttack</c> 镜像要么要求攻击者自己持有 Power、要么只对卡牌来源生效，所以对自爆虫这场战斗
    /// 的结果没有可观察差别。已在 TEST_MATRIX 记为已知差异。
    /// </remarks>
    private static bool ExploderExplode(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        _ = combat;
        _ = player;
        _ = plannedChoices;
        killedOwner = false;
        if (simulator.State.GetCreature(move.Owner).IsDead)
            return true;
        simulator.Kill(move.Owner);
        killedOwner = true;
        return true;
    }

    /// <summary>AFTP 的蛇匕首类型（Reptomancer 召唤用）。</summary>
    private static Type _snakeDaggerType = null!;

    /// <summary>
    /// Reptomancer.SpawnDagger：按遭遇布点表里**除 `reptomancer` 之外**的空槽依次召唤，最多 2 只；
    /// 每只都挂 1 层原版 <c>MinionPower</c>（与源码一致，也由 <c>minion: true</c> 这条路做掉）。
    /// </summary>
    private static bool ReptomancerSpawnDagger(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        _ = player;
        _ = plannedChoices;
        killedOwner = false;
        HashSet<string> occupied = [];
        foreach (Creature teammate in combat.GetTeammatesOf(move.Owner))
        {
            if (simulator.State.GetCreature(teammate).IsAlive && teammate.SlotName is { } occupiedSlot)
                occupied.Add(occupiedSlot);
        }
        int spawned = 0;
        foreach (string slot in combat.EncounterSlots)
        {
            if (spawned >= 2)
                break;
            if (string.Equals(slot, "reptomancer", StringComparison.Ordinal) || occupied.Contains(slot))
                continue;
            MonsterSpawnSupport.SpawnByType(
                simulator,
                combat,
                move.Owner,
                _snakeDaggerType,
                slot,
                maxHpOverride: null,
                minion: true);
            occupied.Add(slot);
            spawned++;
        }
        return true;
    }

    /// <summary>Reptomancer.SnakeStrike：两段攻击由通用攻击循环结算，这里补攻击后给每个活着的目标 1 层虚弱。</summary>
    private static bool ReptomancerSnakeStrike(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        _ = plannedChoices;
        killedOwner = false;
        if (simulator.State.GetCreature(player).IsAlive)
            combat.Apply<WeakPower>(player, 1, move.Owner);
        return true;
    }

    /// <summary>
    /// GiantHead.ItIsTime：单段伤害 = <c>StartingDeathDmg - Count * IncrementDmg</c>
    /// （<c>StartingDeathDmg</c> 按 A9 冻结，<c>IncrementDmg</c> 自检里钉死）。
    /// </summary>
    private static BranchMonsterAttack GiantHeadItIsTime(
        SimulatedCombatState combat,
        MonsterModel monster)
        => new(
            combat.GetMonsterStaticInt(monster.Creature, "StartingDeathDmg")
                - combat.GetMonsterInt(monster.Creature, "_count") * _giantHeadIncrementDmg,
            1);

    /// <summary>GiantHead.Glare：给每个活着的目标 <c>GlareDuration</c> 层虚弱。</summary>
    private static bool GiantHeadGlare(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        _ = plannedChoices;
        killedOwner = false;
        if (simulator.State.GetCreature(player).IsAlive)
            combat.Apply<WeakPower>(player, _giantHeadGlareDuration, move.Owner);
        return true;
    }

    /// <summary>
    /// 「这条行动没有非攻击部分」：源码回调里除了攻击什么都不做时用它登记，
    /// 压掉预测器里由意图表带出来的假「未支持意图」。
    /// </summary>
    private static bool NoNonAttackEffect(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        _ = simulator;
        _ = combat;
        _ = move;
        _ = player;
        _ = plannedChoices;
        killedOwner = false;
        return true;
    }

    /// <summary>
    /// Maw.NomNomNom 的多段版本：伤害固定 5，段数 = <c>TurnCount / 2</c>（向下取整，源码 <c>NomHitCount</c>）。
    /// </summary>
    private static BranchMonsterAttack MawNomNomNom(
        SimulatedCombatState combat,
        MonsterModel monster)
        => new(
            5,
            combat.GetMonsterInt(monster.Creature, "_turnCount") / 2);

    /// <summary>
    /// Maw.Roar：给每个活着的目标挂 <c>TerrifyDuration</c> 层虚弱与破甲，并把 <c>_roared</c> 置真
    /// （分支靠它决定「开场第一动必是 ROAR」）。
    /// </summary>
    private static bool MawRoar(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        _ = plannedChoices;
        killedOwner = false;
        int duration = combat.GetMonsterStaticInt(move.Owner, "TerrifyDuration");
        if (simulator.State.GetCreature(player).IsAlive)
        {
            combat.Apply<WeakPower>(player, duration, move.Owner);
            combat.Apply<FrailPower>(player, duration, move.Owner);
        }
        combat.SetMonsterBool(move.Owner, "_roared", true);
        return true;
    }

    /// <summary>Maw.Drool：给自己 <c>StrUp</c> 点力量。</summary>
    private static bool MawDrool(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        _ = simulator;
        _ = player;
        _ = plannedChoices;
        killedOwner = false;
        combat.Apply<StrengthPower>(
            move.Owner,
            combat.GetMonsterStaticInt(move.Owner, "StrUp"),
            move.Owner);
        return true;
    }

    /// <summary>
    /// SpireGrowth.Constrict：给每个活着的玩家挂 <c>ConstrictAmount</c> 层 AFTP <c>ConstrictedPower</c>。
    /// </summary>
    private static bool SpireGrowthConstrict(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        _ = plannedChoices;
        killedOwner = false;
        if (!simulator.State.GetCreature(player).IsAlive)
            return true;
        combat.ApplyPower(
            _constrictedPowerType,
            player,
            combat.GetMonsterStaticInt(move.Owner, "ConstrictAmount"),
            move.Owner);
        return true;
    }

    /// <summary>
    /// AFTP <c>ConstrictedPower.AfterSideTurnEnd</c>（常规、非 Late）：自己那一方回合末，
    /// 持有者按层数吃一次 <c>Unpowered</c> 伤害。判据与源码逐字一致（<c>side == Owner.Side</c>）。
    /// </summary>
    private static void ConstrictedPowerTurnEnd(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        PowerModel power,
        CombatSide side,
        IReadOnlyCollection<Creature> participants)
    {
        _ = combat;
        _ = participants;
        if (side != power.Owner.Side)
            return;
        using (simulator.PushDamageSource(
            CombatDamageSource.For(CombatDamageSourceKind.Power, "ConstrictedPower")))
        {
            simulator.Damage(power.Owner, power.Amount, ValueProp.Unpowered, null);
        }
    }

    /// <summary>
    /// AFTP <c>ConstrictedPower.AfterDeath</c>：**施加者**死亡且不是「死亡被阻止」时把自己移除。
    /// </summary>
    private static void ConstrictedPowerAfterDeath(AbstractModel model, AfterDeathMirrorContext context)
    {
        PowerModel power = (PowerModel)model;
        if (context.WasRemovalPrevented || !ReferenceEquals(context.Creature, power.Applier))
            return;
        ICombatPredictionEffectSink effects = context.CombatState as ICombatPredictionEffectSink
            ?? throw new PredictionUnsupportedException("缠绕缺少可写的预测状态。");
        effects.ApplyPower(_constrictedPowerType, power.Owner, -power.Amount, power.Applier);
    }

    /// <summary>
    /// OrbWalker.Laser：攻击之后给每个目标塞两张 Burn——**弃牌堆底部一张、抽牌堆随机位置一张**
    /// （源码里是两次不同的 <c>AddGeneratedCardToCombat</c>）。
    /// </summary>
    private static bool OrbWalkerLaser(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        _ = combat;
        _ = move;
        _ = plannedChoices;
        killedOwner = false;
        simulator.AddToCombat<Burn>(player, PileType.Discard, 1, null, CardPilePosition.Bottom);
        simulator.AddToCombat<Burn>(player, PileType.Draw, 1, null, CardPilePosition.Random);
        return true;
    }

    /// <summary>
    /// AFTP <c>StrengthUpPower.AfterSideTurnEnd</c>（常规、非 Late）：自己那一方回合末按层数给自己加力量。
    /// </summary>
    /// <remarks>
    /// 源码的判据是 <c>side == Owner.Side</c>，这里逐字照抄；层数直接读 <c>power.Amount</c>
    /// （施加时的 <c>StrengthUpAmount</c> 由 <c>AfterAddedToRoom</c> 在根里就写好了）。
    /// </remarks>
    private static void StrengthUpPowerTurnEnd(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        PowerModel power,
        CombatSide side,
        IReadOnlyCollection<Creature> participants)
    {
        _ = simulator;
        _ = participants;
        if (side != power.Owner.Side)
            return;
        combat.Apply<StrengthPower>(power.Owner, power.Amount, power.Owner);
    }

    /// <summary>Repulsor.Daze：给每个活着的目标往抽牌堆随机位置塞 <c>DazeAmount</c> 张 Dazed。</summary>
    private static bool RepulsorDaze(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        _ = combat;
        _ = move;
        _ = plannedChoices;
        killedOwner = false;
        simulator.AddToCombat<Dazed>(
            player,
            PileType.Draw,
            _repulsorDazeAmount,
            null,
            CardPilePosition.Random);
        return true;
    }

    /// <summary>
    /// Spiker.BuffThorns：先自己记一次数（分支靠它判「超过 5 次就不再加」），再挂 <c>BuffAmount</c>
    /// 层荆棘。
    /// </summary>
    private static bool SpikerBuffThorns(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        _ = simulator;
        _ = player;
        _ = plannedChoices;
        killedOwner = false;
        combat.SetMonsterInt(
            move.Owner,
            "_thornsCount",
            combat.GetMonsterInt(move.Owner, "_thornsCount") + 1);
        combat.Apply<ThornsPower>(move.Owner, _spikerBuffAmount, move.Owner);
        return true;
    }

    /// <summary>SnakeDagger.WoundStab：攻击之后往弃牌堆塞 1 张 Wound。</summary>
    private static bool SnakeDaggerWoundStab(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        _ = combat;
        _ = move;
        _ = plannedChoices;
        killedOwner = false;
        simulator.AddToCombat<Wound>(player, PileType.Discard, 1, null);
        return true;
    }

    /// <summary>
    /// SnakeDagger.Explode：25 点攻击之后 <c>CreatureCmd.Kill(自己, false)</c>——攻击部分由通用攻击循环按
    /// 意图（<c>DeathBlowIntent</c> 派生自 <c>SingleAttackIntent</c>）结算，这里只补那一句自杀。
    /// </summary>
    private static bool SnakeDaggerExplode(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        _ = combat;
        _ = player;
        _ = plannedChoices;
        killedOwner = false;
        if (simulator.State.GetCreature(move.Owner).IsDead)
            return true;
        simulator.Kill(move.Owner);
        killedOwner = true;
        return true;
    }
}
