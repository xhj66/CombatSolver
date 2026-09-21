using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Random;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver.ActsFromThePastAdapter;

/// <summary>
/// 往昔之章第三幕（The Beyond）怪物的行动分支解析。
/// </summary>
/// <remarks>
/// 与第一、二幕（<see cref="ExordiumBranchResolvers"/>／<see cref="CityBranchResolvers"/>）同一套纪律：
/// 逐行对照 AFTP 源码的 <c>SelectNextMove</c>，RNG 的**调用次数、顺序与短路**都要保持，分支里一律读
/// **模拟状态**，不碰实机字段。
/// </remarks>
internal static class BeyondBranchResolvers
{
    /// <summary>初始化自检：要接管的怪物类型都在对方程序集里。</summary>
    public static void Verify()
    {
        foreach (string typeName in RegisteredMonsterTypes)
            AfpReflection.RequireMonsterType(typeName);
    }

    public static void RegisterAll()
    {
        ThirdPartyAdapterRegistry.RegisterMonsterBranchResolver("Repulsor", "MOVE_BRANCH", Repulsor);
        ThirdPartyAdapterRegistry.RegisterMonsterBranchResolver("Spiker", "MOVE_BRANCH", Spiker);
        foreach ((string monster, string branch) in PureSelectors)
            ThirdPartyAdapterRegistry.RegisterPureBranchSelector(monster, branch);
    }

    internal static readonly string[] RegisteredMonsterTypes = ["Repulsor", "Spiker"];

    /// <summary>
    /// 逐行复核为「纯读取」的 (怪物, 分支)：只读传入的 <c>rng</c>、实机 StateLog 与自己的只读标量，
    /// 不写实机状态、不下命令。
    /// </summary>
    internal static readonly (string Monster, string Branch)[] PureSelectors =
    [
        ("Repulsor", "MOVE_BRANCH"),
        ("Spiker", "MOVE_BRANCH"),
    ];

    /// <summary>
    /// Spiker.SelectNextMove：先看自己的荆棘次数——**超过 5 次就直接攻击且一次 RNG 都不抽**；
    /// 否则抽一次 RNG，50 以下且最近一步不是 ATTACK 就攻击，否则加荆棘。
    /// </summary>
    /// <remarks>
    /// 「超过 5」这一条短路必须保持：那条路径不抽 RNG，抽了会让后续回合的抽样整体错位。
    /// 这只怪的阈值 5 是源码里的字面量（<c>ThornsCount &gt; 5</c>），没有可钉的常量；
    /// 它只影响分支走向，不影响任何数值。
    /// </remarks>
    private static string Spiker(
        MonsterModel monster,
        string branchId,
        IReadOnlyList<string> log,
        Rng rng,
        SimulatedCombatState combat,
        CombatPredictionSimulator simulator)
    {
        _ = branchId;
        _ = simulator;
        if (combat.GetMonsterInt(monster.Creature, "_thornsCount") > 5)
            return "ATTACK";
        int num = rng.NextInt(100);
        if (num < 50 && !LastMove(log, "ATTACK"))
            return "ATTACK";
        return "BUFF_THORNS";
    }

    /// <summary>
    /// Repulsor.SelectNextMove：先抽一次 RNG；20 以下且最近一步不是 ATTACK 就打一下，否则 DAZE。
    /// 不写任何自己的标量（因此可以声明为纯读取，预览可以直接调用它）。
    /// </summary>
    private static string Repulsor(
        MonsterModel monster,
        string branchId,
        IReadOnlyList<string> log,
        Rng rng,
        SimulatedCombatState combat,
        CombatPredictionSimulator simulator)
    {
        _ = monster;
        _ = branchId;
        _ = combat;
        _ = simulator;
        int num = rng.NextInt(100);
        if (num < 20 && !LastMove(log, "ATTACK"))
            return "ATTACK";
        return "DAZE";
    }

    private static bool LastMove(IReadOnlyList<string> log, string moveId)
        => log.Count > 0 && string.Equals(log[^1], moveId, StringComparison.Ordinal);
}
