using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Random;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver.ActsFromThePastAdapter;

/// <summary>
/// 往昔之章第二幕（The City）怪物的行动分支解析。
/// </summary>
/// <remarks>
/// 与第一幕（<see cref="ExordiumBranchResolvers"/>）同一套纪律：逐行对照 AFTP 源码的
/// <c>SelectNextMove</c>，包括 RNG 的**调用顺序与短路条件**——「某条分支不抽 RNG」也必须保持，
/// 否则后续回合的抽样整体错位。分支里一律读**模拟状态**，不碰实机字段。
/// </remarks>
internal static class CityBranchResolvers
{
    /// <summary>初始化自检：要接管的怪物类型都在对方程序集里。</summary>
    public static void Verify()
    {
        foreach (string typeName in RegisteredMonsterTypes)
            AfpReflection.RequireMonsterType(typeName);
    }

    public static void RegisterAll()
    {
        ThirdPartyAdapterRegistry.RegisterMonsterBranchResolver("Centurion", "MOVE_BRANCH", Centurion);
        ThirdPartyAdapterRegistry.RegisterMonsterBranchResolver("Mystic", "MOVE_BRANCH", Mystic);
        foreach ((string monster, string branch) in PureSelectors)
            ThirdPartyAdapterRegistry.RegisterPureBranchSelector(monster, branch);
    }

    internal static readonly string[] RegisteredMonsterTypes = ["Centurion", "Mystic"];

    /// <summary>
    /// 逐行复核为「纯读取」的 (怪物, 分支)：都只读实机 StateLog、传入的 rng 与自己的标量字段，
    /// 不写实机状态、不下命令；<c>Mystic</c> 那条另外读**模拟状态**里队友的血量（也不是实机字段）。
    /// </summary>
    internal static readonly (string Monster, string Branch)[] PureSelectors =
    [
        ("Centurion", "MOVE_BRANCH"),
        ("Mystic", "MOVE_BRANCH"),
    ];

    /// <summary>
    /// Centurion.SelectNextMove：65% 以上且最近没有连出两次保护／狂怒时，有队友就保护、没队友就狂怒；
    /// 否则只要不是连出两次斩击就斩击，再不然还是有队友就保护、没队友就狂怒。
    /// </summary>
    private static string Centurion(
        MonsterModel monster,
        string branchId,
        IReadOnlyList<string> log,
        Rng rng,
        SimulatedCombatState combat,
        CombatPredictionSimulator simulator)
    {
        _ = simulator;
        int num = rng.NextInt(100);
        bool hasAlly = combat.GetTeammatesOf(monster.Creature).Count > 1;
        if (num >= 65 && !LastTwoMoves(log, "PROTECT") && !LastTwoMoves(log, "FURY"))
            return hasAlly ? "PROTECT" : "FURY";
        if (!LastTwoMoves(log, "SLASH"))
            return "SLASH";
        return hasAlly ? "PROTECT" : "FURY";
    }

    /// <summary>
    /// Mystic.SelectNextMove：**先把**队友（含自己）已损失生命求和，超过 <c>HealThreshold</c> 且最近
    /// 没有连治两次就治疗（这一步不抽 RNG）；否则 60% 攻击（不连续两次）、再不然强化（不连续两次）。
    /// </summary>
    private static string Mystic(
        MonsterModel monster,
        string branchId,
        IReadOnlyList<string> log,
        Rng rng,
        SimulatedCombatState combat,
        CombatPredictionSimulator simulator)
    {
        int missingHp = 0;
        foreach (Creature teammate in combat.GetTeammatesOf(monster.Creature))
        {
            SimCreatureState state = simulator.State.GetCreature(teammate);
            if (state.IsAlive)
                missingHp += state.MaxHp - state.CurrentHp;
        }
        if (missingHp > combat.GetMonsterStaticInt(monster.Creature, "HealThreshold")
            && !LastTwoMoves(log, "HEAL"))
        {
            return "HEAL";
        }
        int num = rng.NextInt(100);
        if (num >= 40 && !LastMove(log, "ATTACK"))
            return "ATTACK";
        if (!LastTwoMoves(log, "BUFF"))
            return "BUFF";
        return "ATTACK";
    }

    private static bool LastMove(IReadOnlyList<string> log, string moveId)
        => log.Count > 0 && string.Equals(log[^1], moveId, StringComparison.Ordinal);

    private static bool LastTwoMoves(IReadOnlyList<string> log, string moveId)
        => log.Count > 1
            && string.Equals(log[^1], moveId, StringComparison.Ordinal)
            && string.Equals(log[^2], moveId, StringComparison.Ordinal);
}
