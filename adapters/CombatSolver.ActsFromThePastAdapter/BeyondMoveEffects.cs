using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models.Cards;
using CombatSolver.Engine.Common;
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
    ];

    /// <summary>Repulsor 的 Daze 张数（AFTP <c>DazeAmount</c>）。</summary>
    private static int _repulsorDazeAmount;

    public static void Verify()
    {
        foreach (string typeName in MonsterTypes)
            AfpReflection.RequireMonsterType(typeName);
        _repulsorDazeAmount = AfpReflection.RequireConst("Repulsor", "DazeAmount", 2);
    }

    public static void RegisterAll()
    {
        // Repulsor.Daze：往**抽牌堆的随机位置**塞 DazeAmount 张 Dazed（意图是 StatusIntent，攻击部分没有）。
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("Repulsor", "DAZE", RepulsorDaze);

        // SnakeDagger（ Reptomancer 召唤的蛇匕首）：
        // WoundStab 那次 9 点攻击已由通用攻击循环按意图结算，这里只补 1 张 Wound 进弃牌堆；
        // Explode 的 25 点攻击同理（DeathBlowIntent 就是 SingleAttackIntent 的派生），这里只补自杀。
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("SnakeDagger", "WOUND_STAB", SnakeDaggerWoundStab);
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("SnakeDagger", "EXPLODE", SnakeDaggerExplode);
        ThirdPartyAdapterRegistry.RegisterOwnerRemovingMove("SnakeDagger", "EXPLODE");
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
