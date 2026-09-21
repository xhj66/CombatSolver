using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Death;
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
    ];

    /// <summary>AFTP 自己的 <c>ConstrictedPower</c>（与原版 <c>ConstrictPower</c> 是两个类型）。</summary>
    private static Type _constrictedPowerType = null!;

    /// <summary>SpireGrowth 每次缠绕的层数（AFTP <c>ConstrictAmount</c>，A9+ 12／否则 10）。</summary>
    private static int _spireGrowthConstrictAmount;

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
