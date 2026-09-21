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
        ThirdPartyAdapterRegistry.RegisterMonsterBranchResolver("OrbWalker", "MOVE_BRANCH", OrbWalker);
        ThirdPartyAdapterRegistry.RegisterMonsterBranchResolver(
            "SpireGrowth",
            "MOVE_BRANCH",
            SpireGrowth);
        ThirdPartyAdapterRegistry.RegisterMonsterBranchResolver("Maw", "MOVE_BRANCH", Maw);
        ThirdPartyAdapterRegistry.RegisterMonsterBranchResolver("GiantHead", "MOVE_BRANCH", GiantHead);
        foreach ((string monster, string branch) in PureSelectors)
            ThirdPartyAdapterRegistry.RegisterPureBranchSelector(monster, branch);
    }

    internal static readonly string[] RegisteredMonsterTypes =
        ["Repulsor", "Spiker", "OrbWalker", "SpireGrowth", "Maw", "GiantHead"];

    /// <summary>
    /// 逐行复核为「纯读取」的 (怪物, 分支)：只读传入的 <c>rng</c>、实机 StateLog 与自己的只读标量，
    /// 不写实机状态、不下命令。
    /// </summary>
    internal static readonly (string Monster, string Branch)[] PureSelectors =
    [
        ("Repulsor", "MOVE_BRANCH"),
        ("Spiker", "MOVE_BRANCH"),
        ("OrbWalker", "MOVE_BRANCH"),
        ("SpireGrowth", "MOVE_BRANCH"),
    ];

    /// <summary>
    /// SpireGrowth.SelectNextMove：先看**玩家身上有没有 AFTP 自己的 <c>ConstrictedPower</c>**
    /// （源码取的是 <c>CombatState.Players.FirstOrDefault()</c>，本 Mod 只支持单人，所以「任一玩家」
    /// 与「第一个玩家」等价）；没被缠绕且最近一步不是 CONSTRICT 就 **不抽 RNG** 直接缠绕；否则抽
    /// <c>NextInt(100)</c>，50 以下且最近没连出两次 QUICK_TACKLE 就 QUICK_TACKLE；再判一次缠绕；
    /// 再判最近没连出两次 SMASH 就 SMASH；否则 QUICK_TACKLE。
    /// </summary>
    /// <remarks>
    /// 读的是**模拟状态**里的 Power（<c>combat.EffectivePowers()</c>），不是实机字段；
    /// 这条分支不写任何状态，所以也登记为纯读取（预览在实机上跑同一份判据，读到的就是实机当前值）。
    /// 「没被缠绕」那两次短路都发生在抽 RNG **之前/之间**，顺序必须保持。
    /// </remarks>
    private static string SpireGrowth(
        MonsterModel monster,
        string branchId,
        IReadOnlyList<string> log,
        Rng rng,
        SimulatedCombatState combat,
        CombatPredictionSimulator simulator)
    {
        _ = monster;
        _ = branchId;
        _ = simulator;
        bool constricted = combat.EffectivePowers().Any(power =>
            power.Amount > 0
            && power.Owner.Player != null
            && string.Equals(power.GetType().Name, "ConstrictedPower", StringComparison.Ordinal));
        if (!constricted && !LastMove(log, "CONSTRICT"))
            return "CONSTRICT";
        int num = rng.NextInt(100);
        if (num < 50 && !LastTwoMoves(log, "QUICK_TACKLE"))
            return "QUICK_TACKLE";
        if (!constricted && !LastMove(log, "CONSTRICT"))
            return "CONSTRICT";
        if (!LastTwoMoves(log, "SMASH"))
            return "SMASH";
        return "QUICK_TACKLE";
    }

    /// <summary>
    /// OrbWalker.SelectNextMove：先抽一次 RNG；40 以下时最近**连着两次**不是 CLAW 就 CLAW、否则 LASER；
    /// 40 及以上时最近连着两次不是 LASER 就 LASER、否则 CLAW。不写任何自己的标量（纯读取）。
    /// </summary>
    private static string OrbWalker(
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
        if (num < 40)
            return LastTwoMoves(log, "CLAW") ? "LASER" : "CLAW";
        return LastTwoMoves(log, "LASER") ? "CLAW" : "LASER";
    }

    private static bool LastTwoMoves(IReadOnlyList<string> log, string moveId)
        => log.Count > 1
            && string.Equals(log[^1], moveId, StringComparison.Ordinal)
            && string.Equals(log[^2], moveId, StringComparison.Ordinal);

    /// <summary>
    /// Maw.SelectNextMove：先把回合计数 +1；还没咆哮过就 **不抽 RNG** 直接 ROAR；否则抽一次
    /// <c>NextInt(100)</c>，50 以下且上一步不是两种啃咬就按「段数 &gt; 1」选多段／单段啃咬；
    /// 再不然上一步不是 SLAM 且不是啃咬就 SLAM；否则流口水。
    /// </summary>
    /// <remarks>
    /// 它写自己的两个标量（<c>_turnCount</c> 每回合 +1、ROAR 时置 <c>_roared</c>），所以**不在**
    /// 纯读取名单里；ROAR 那条路径不抽 RNG 的短路必须保持，否则后续回合抽样整体错位。
    /// 段数 <c>NomHitCount = TurnCount / 2</c> 由动态攻击值那侧现算（见 BeyondMoveEffects）。
    /// </remarks>
    private static string Maw(
        MonsterModel monster,
        string branchId,
        IReadOnlyList<string> log,
        Rng rng,
        SimulatedCombatState combat,
        CombatPredictionSimulator simulator)
    {
        _ = branchId;
        _ = simulator;
        int turnCount = combat.GetMonsterInt(monster.Creature, "_turnCount") + 1;
        combat.SetMonsterInt(monster.Creature, "_turnCount", turnCount);
        if (!combat.GetMonsterBool(monster.Creature, "_roared"))
            return "ROAR";
        int num = rng.NextInt(100);
        bool lastNom = LastMove(log, "NOMNOMNOM_SINGLE") || LastMove(log, "NOMNOMNOM_MULTI");
        if (num < 50 && !lastNom)
            return turnCount / 2 <= 1 ? "NOMNOMNOM_SINGLE" : "NOMNOMNOM_MULTI";
        if (!LastMove(log, "SLAM") && !lastNom)
            return "SLAM";
        return "DROOL";
    }

    /// <summary>
    /// GiantHead.SelectNextMove：计数 <c>&lt;= 1</c> 时**一次 RNG 都不抽**、直接 `IT_IS_TIME`
    /// （并且只在计数 <c>&gt; -6</c> 时再减一次，源码就是这条下界）；否则先减计数，再抽一次
    /// <c>NextInt(100)</c>：50 以下且最近没连出两次 GLARE 就 GLARE、反之 COUNT；50 及以上且最近没连出
    /// 两次 COUNT 就 COUNT、反之 GLARE。
    /// </summary>
    /// <remarks>
    /// 开场那 4／5 的计数由 <c>AfterAddedToRoom</c> 写入（根捕获时已在实机实例上），这里每回合减一次；
    /// 它写自己的计数，所以**不在**纯读取名单里。计数为负会让 <c>IT_IS_TIME</c> 的伤害继续变大
    /// （<c>StartingDeathDmg - Count * 5</c>），这段下界逻辑必须照抄。
    /// </remarks>
    private static string GiantHead(
        MonsterModel monster,
        string branchId,
        IReadOnlyList<string> log,
        Rng rng,
        SimulatedCombatState combat,
        CombatPredictionSimulator simulator)
    {
        _ = branchId;
        _ = simulator;
        int count = combat.GetMonsterInt(monster.Creature, "_count");
        if (count <= 1)
        {
            if (count > -6)
                combat.SetMonsterInt(monster.Creature, "_count", count - 1);
            return "IT_IS_TIME";
        }
        combat.SetMonsterInt(monster.Creature, "_count", count - 1);
        int num = rng.NextInt(100);
        if (num < 50)
            return LastTwoMoves(log, "GLARE") ? "COUNT" : "GLARE";
        return LastTwoMoves(log, "COUNT") ? "GLARE" : "COUNT";
    }

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
