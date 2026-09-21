using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Random;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver.ActsFromThePastAdapter;

/// <summary>
/// 往昔之章第一幕怪物的行动分支解析（<c>ActsFromThePast.ConditionalBranchState</c>）。
/// </summary>
/// <remarks>
/// 每条实现逐行对应 AFTP 源码里的 <c>SelectNextMove</c>／<c>SelectAfterCharge</c>，
/// 包括注释里的概率与短路条件。与源码的三点差别：
///
/// <list type="number">
/// <item>行动历史取自求解器的**预测分支** <c>stateLog</c>，不是实机状态机的
/// <c>StateLog</c>——实机日志对搜索里的每个分支都是同一份陈旧值。分支状态自身不进日志
/// （<c>ShouldAppearInLogs=false</c>），且状态机构造时会把初始行动预置进去，
/// 所以第 N 项就是第 N 次将要／已经执行的行动，和源码里 <c>LastMove</c> 的口径一致。</item>
/// <item>怪物标量状态走模拟状态（<c>GetMonsterInt</c>），随分支 Fork，不读实机私有字段。</item>
/// <item><c>rng</c> 由求解器传入，就是 AFTP 用的那条 <c>MonsterAi</c> 流；下面的实现逐条复制
/// 原实现的 <c>NextInt</c>／<c>NextFloat</c> 调用顺序与短路条件——不但同一条件下抽同样次数，
/// 「某条分支不抽 RNG」这一点也必须保持，否则后续回合的抽样会整体错位。</item>
/// </list>
///
/// **只登记能完整建模的分支。** 分支依赖了还没镜像的状态时（<c>SplitTriggered</c>、
/// <c>_isOpen</c> / <c>_pendingModeShift</c>、<c>_orbActiveCount</c>、<c>_usedEntangle</c>、
/// <c>IsAwake</c>）一律不登记：求解器会明确报「这个分支还没有预测实现」并停在门禁上，
/// 比给出一个看似可信的错路线好（docs/THIRD_PARTY_ADAPTERS.md §3.2）。
/// **例外**：GremlinShield 的分支只看队友数（本身可建模），卡住它的是同一只怪物的
/// <c>PROTECT</c> 行动要用**它自己那条 <c>MonsterModel.Rng</c>** 抽格挡目标，求解器不模拟这条流——
/// 所以整个怪物仍然不登记，缺口记在 docs/AFTP_ACT4HEART_STATUS.md §2.2。
/// </remarks>
internal static class ExordiumBranchResolvers
{
    /// <summary>初始化自检：确认要登记的怪物类型在对方程序集里存在。</summary>
    public static void Verify()
    {
        foreach (string typeName in RegisteredMonsterTypes)
            AfpReflection.RequireMonsterType(typeName);
    }

    public static void RegisterAll()
    {
        ThirdPartyAdapterRegistry.RegisterMonsterBranchResolver("AcidSlimeMedium", "MOVE_BRANCH", AcidSlimeMedium);
        ThirdPartyAdapterRegistry.RegisterMonsterBranchResolver("SpikeSlimeMedium", "MOVE_BRANCH", SpikeSlimeMedium);
        ThirdPartyAdapterRegistry.RegisterMonsterBranchResolver("FungiBeast", "MOVE_BRANCH", FungiBeast);
        ThirdPartyAdapterRegistry.RegisterMonsterBranchResolver("JawWorm", "MOVE_BRANCH", JawWorm);
        ThirdPartyAdapterRegistry.RegisterMonsterBranchResolver("GremlinNob", "MOVE_BRANCH", GremlinNob);
        ThirdPartyAdapterRegistry.RegisterMonsterBranchResolver("SlaverBlue", "MOVE_BRANCH", SlaverBlue);
        ThirdPartyAdapterRegistry.RegisterMonsterBranchResolver("SlaverRed", "MOVE_BRANCH", SlaverRed);
        ThirdPartyAdapterRegistry.RegisterMonsterBranchResolver("GremlinWizard", "AFTER_CHARGE", GremlinWizard);
        ThirdPartyAdapterRegistry.RegisterMonsterBranchResolver("Looter", "MUG_BRANCH", Looter);
        ThirdPartyAdapterRegistry.RegisterMonsterBranchResolver("AcidSlimeLarge", "MOVE_BRANCH", AcidSlimeLarge);
        ThirdPartyAdapterRegistry.RegisterMonsterBranchResolver("SpikeSlimeLarge", "MOVE_BRANCH", SpikeSlimeLarge);
        ThirdPartyAdapterRegistry.RegisterMonsterBranchResolver("SlimeBoss", "MOVE_BRANCH", SlimeBoss);
        ThirdPartyAdapterRegistry.RegisterMonsterBranchResolver("GremlinShield", "MOVE_BRANCH", GremlinShield);

        // 上面这些分支的选择函数都逐行复核过：只读自己的标量字段与实机 StateLog、只按源码顺序抽传入的
        // rng，不写实机状态、不下命令。声明之后预测器才能照旧在实机上调用它们推演后续回合；
        // 没声明的第三方分支（例如往昔之书那种每次选择都 StabCount++ 的）预测器一律不碰。
        foreach ((string monster, string branch) in PureSelectors)
            ThirdPartyAdapterRegistry.RegisterPureBranchSelector(monster, branch);
    }

    /// <summary>
    /// 逐行复核为「纯读取」的 (怪物, 分支) 选择函数。改动这条分支的实现前先回来重新核对。
    /// </summary>
    internal static readonly (string Monster, string Branch)[] PureSelectors =
    [
        ("AcidSlimeMedium", "MOVE_BRANCH"),
        ("SpikeSlimeMedium", "MOVE_BRANCH"),
        ("FungiBeast", "MOVE_BRANCH"),
        ("JawWorm", "MOVE_BRANCH"),
        ("GremlinNob", "MOVE_BRANCH"),
        ("SlaverBlue", "MOVE_BRANCH"),
        ("SlaverRed", "MOVE_BRANCH"),      // SelectNextMove：只读 _usedEntangle + 行动历史 + RNG
        ("GremlinWizard", "AFTER_CHARGE"),  // SelectAfterCharge：只读 _currentCharge
        ("Looter", "MUG_BRANCH"),           // SelectAfterMug：只读 _mugCount
        ("AcidSlimeLarge", "MOVE_BRANCH"),  // 只读 _splitTriggered + RNG + 行动历史
        ("SpikeSlimeLarge", "MOVE_BRANCH"),
        ("SlimeBoss", "MOVE_BRANCH"),
        ("GremlinShield", "MOVE_BRANCH"),   // 只读队友数
    ];

    internal static readonly string[] RegisteredMonsterTypes =
    [
        "AcidSlimeMedium",
        "SpikeSlimeMedium",
        "FungiBeast",
        "JawWorm",
        "GremlinNob",
        "SlaverBlue",
        "SlaverRed",
        "GremlinWizard",
        "Looter",
    ];

    // === 逐条对照 AFTP 源码 ===

    /// <summary>AcidSlimeMedium.SelectNextMove：40% Corrosive Spit／40% Tackle／20% Lick。</summary>
    private static string AcidSlimeMedium(
        MonsterModel monster,
        string branchId,
        IReadOnlyList<string> log,
        Rng rng,
        SimulatedCombatState combat,
        CombatPredictionSimulator simulator)
    {
        int num = rng.NextInt(100);
        if (num < 40)
        {
            if (LastTwoMoves(log, "CORROSIVE_SPIT"))
                return rng.NextFloat() < 0.5f ? "TACKLE" : "LICK";
            return "CORROSIVE_SPIT";
        }
        if (num < 80)
        {
            if (LastTwoMoves(log, "TACKLE"))
                return rng.NextFloat() < 0.5f ? "CORROSIVE_SPIT" : "LICK";
            return "TACKLE";
        }
        if (LastMove(log, "LICK"))
            return rng.NextFloat() < 0.4f ? "CORROSIVE_SPIT" : "TACKLE";
        return "LICK";
    }

    /// <summary>SpikeSlimeMedium.SelectNextMove：30% Flame Tackle／70% Lick。</summary>
    private static string SpikeSlimeMedium(
        MonsterModel monster,
        string branchId,
        IReadOnlyList<string> log,
        Rng rng,
        SimulatedCombatState combat,
        CombatPredictionSimulator simulator)
    {
        int num = rng.NextInt(100);
        if (num < 30)
            return LastTwoMoves(log, "FLAME_TACKLE") ? "LICK" : "FLAME_TACKLE";
        return LastMove(log, "LICK") ? "FLAME_TACKLE" : "LICK";
    }

    /// <summary>FungiBeast.SelectNextMove：60% Bite／40% Grow。</summary>
    private static string FungiBeast(
        MonsterModel monster,
        string branchId,
        IReadOnlyList<string> log,
        Rng rng,
        SimulatedCombatState combat,
        CombatPredictionSimulator simulator)
    {
        int num = rng.NextInt(100);
        if (num < 60)
            return LastTwoMoves(log, "BITE") ? "GROW" : "BITE";
        return LastMove(log, "GROW") ? "BITE" : "GROW";
    }

    /// <summary>JawWorm.SelectNextMove：25% Chomp／30% Thrash／45% Bellow。</summary>
    private static string JawWorm(
        MonsterModel monster,
        string branchId,
        IReadOnlyList<string> log,
        Rng rng,
        SimulatedCombatState combat,
        CombatPredictionSimulator simulator)
    {
        int num = rng.NextInt(100);
        if (num < 25)
        {
            if (LastMove(log, "CHOMP"))
                return rng.NextFloat() < 0.5625f ? "BELLOW" : "THRASH";
            return "CHOMP";
        }
        if (num < 55)
        {
            if (LastTwoMoves(log, "THRASH"))
                return rng.NextFloat() < 0.357f ? "CHOMP" : "BELLOW";
            return "THRASH";
        }
        if (LastMove(log, "BELLOW"))
            return rng.NextFloat() < 0.416f ? "CHOMP" : "THRASH";
        return "BELLOW";
    }

    /// <summary>
    /// GremlinNob.SelectNextMove：A18 行为——前两回合没打过 Skull Bash 就打它，否则 Rush
    /// （不连续两次）。这条分支不抽 RNG。
    /// </summary>
    private static string GremlinNob(
        MonsterModel monster,
        string branchId,
        IReadOnlyList<string> log,
        Rng rng,
        SimulatedCombatState combat,
        CombatPredictionSimulator simulator)
    {
        if (!LastMove(log, "SKULL_BASH") && !LastMoveBefore(log, "SKULL_BASH"))
            return "SKULL_BASH";
        return LastTwoMoves(log, "RUSH") ? "SKULL_BASH" : "RUSH";
    }

    /// <summary>SlaverBlue.SelectNextMove：60% Stab（不连续两次）／否则 Rake（不连续两次）。</summary>
    private static string SlaverBlue(
        MonsterModel monster,
        string branchId,
        IReadOnlyList<string> log,
        Rng rng,
        SimulatedCombatState combat,
        CombatPredictionSimulator simulator)
    {
        int num = rng.NextInt(100);
        if (num >= 40 && !LastTwoMoves(log, "STAB"))
            return "STAB";
        if (!LastMove(log, "RAKE"))
            return "RAKE";
        return "STAB";
    }

    /// <summary>
    /// SlaverRed.SelectNextMove：抽一次 `NextInt(100)`——≥75 且没用过缠网就走 ENTANGLE；
    /// ≥55 且用过缠网、且**最近两次都不是** STAB 就补一次 STAB；否则上一步不是 SCRAPE 就走 SCRAPE，
    /// 都不满足就 STAB。分支本身不写 <c>_usedEntangle</c>（那是 ENTANGLE 行动干的）。
    /// </summary>
    private static string SlaverRed(
        MonsterModel monster,
        string branchId,
        IReadOnlyList<string> log,
        Rng rng,
        SimulatedCombatState combat,
        CombatPredictionSimulator simulator)
    {
        int num = rng.NextInt(100);
        if (num >= 75 && !combat.GetMonsterBool(monster.Creature, "_usedEntangle"))
            return "ENTANGLE";
        if (num >= 55
            && combat.GetMonsterBool(monster.Creature, "_usedEntangle")
            && !LastTwoMoves(log, "STAB"))
        {
            return "STAB";
        }
        if (!LastMove(log, "SCRAPE"))
            return "SCRAPE";
        return "STAB";
    }

    /// <summary>GremlinWizard.SelectAfterCharge：充能满 3 就放炮，否则继续充能。</summary>
    private static string GremlinWizard(
        MonsterModel monster,
        string branchId,
        IReadOnlyList<string> log,
        Rng rng,
        SimulatedCombatState combat,
        CombatPredictionSimulator simulator)
        => combat.GetMonsterInt(monster.Creature, "_currentCharge") >= ExordiumMoveEffects.GremlinWizardChargeLimit
            ? "ULTIMATE_BLAST"
            : "CHARGING";

    /// <summary>
    /// Looter.SelectAfterMug：Mug 出手不满两次就再来一次，够了就交给随机分支
    /// <c>AFTER_SECOND_MUG</c>（50% SMOKE_BOMB／50% LUNGE，由求解器通用的随机分支逻辑按冻结权重抽）。
    /// 这条分支不抽 RNG。
    /// </summary>
    private static string Looter(
        MonsterModel monster,
        string branchId,
        IReadOnlyList<string> log,
        Rng rng,
        SimulatedCombatState combat,
        CombatPredictionSimulator simulator)
        => combat.GetMonsterInt(monster.Creature, "_mugCount") < 2 ? "MUG" : "AFTER_SECOND_MUG";

    /// <summary>
    /// AcidSlimeLarge.SelectNextMove：分裂已触发就直接 SPLIT；否则 40% 腐蚀喷吐／30% 冲撞／30% 舔舐，
    /// 各自带「连续两次就换招」的重抽。
    /// </summary>
    private static string AcidSlimeLarge(
        MonsterModel monster,
        string branchId,
        IReadOnlyList<string> log,
        Rng rng,
        SimulatedCombatState combat,
        CombatPredictionSimulator simulator)
    {
        if (combat.GetMonsterBool(monster.Creature, "_splitTriggered"))
            return "SPLIT";
        int num = rng.NextInt(100);
        if (num < 40)
        {
            if (LastTwoMoves(log, "CORROSIVE_SPIT"))
                return rng.NextFloat() < 0.6f ? "TACKLE" : "LICK";
            return "CORROSIVE_SPIT";
        }
        if (num < 70)
        {
            if (LastTwoMoves(log, "TACKLE"))
                return rng.NextFloat() < 0.6f ? "CORROSIVE_SPIT" : "LICK";
            return "TACKLE";
        }
        if (LastMove(log, "LICK"))
            return rng.NextFloat() < 0.4f ? "CORROSIVE_SPIT" : "TACKLE";
        return "LICK";
    }

    /// <summary>SpikeSlimeLarge.SelectNextMove：分裂已触发就 SPLIT；否则 30% 火焰冲撞／70% 舔舐。</summary>
    private static string SpikeSlimeLarge(
        MonsterModel monster,
        string branchId,
        IReadOnlyList<string> log,
        Rng rng,
        SimulatedCombatState combat,
        CombatPredictionSimulator simulator)
    {
        if (combat.GetMonsterBool(monster.Creature, "_splitTriggered"))
            return "SPLIT";
        int num = rng.NextInt(100);
        if (num < 30)
            return LastTwoMoves(log, "FLAME_TACKLE") ? "LICK" : "FLAME_TACKLE";
        return LastMove(log, "LICK") ? "FLAME_TACKLE" : "LICK";
    }

    /// <summary>SlimeBoss.SelectNextMove：分裂已触发就 SPLIT，否则一直是粘液喷吐。不抽 RNG。</summary>
    private static string SlimeBoss(
        MonsterModel monster,
        string branchId,
        IReadOnlyList<string> log,
        Rng rng,
        SimulatedCombatState combat,
        CombatPredictionSimulator simulator)
        => combat.GetMonsterBool(monster.Creature, "_splitTriggered") ? "SPLIT" : "GOOP_SPRAY";

    /// <summary>
    /// GremlinShield.SelectNextMove：场上还有别的队友就保护（PROTECT），否则直接盾击。
    /// 源码数的是 <c>GetTeammatesOf(Creature).Count</c>（**含已经倒下但还没离场的**），
    /// 求解器的同名入口是同一份「己方全体」列表。不抽 RNG。
    /// </summary>
    private static string GremlinShield(
        MonsterModel monster,
        string branchId,
        IReadOnlyList<string> log,
        Rng rng,
        SimulatedCombatState combat,
        CombatPredictionSimulator simulator)
        => combat.GetTeammatesOf(monster.Creature).Count > 1 ? "PROTECT" : "SHIELD_BASH";

    // === AFTP 各敌人自带的同名辅助函数 ===
    private static bool LastMove(IReadOnlyList<string> log, string moveId)
        => log.Count > 0 && string.Equals(log[^1], moveId, StringComparison.Ordinal);

    private static bool LastMoveBefore(IReadOnlyList<string> log, string moveId)
        => log.Count > 1 && string.Equals(log[^2], moveId, StringComparison.Ordinal);

    private static bool LastTwoMoves(IReadOnlyList<string> log, string moveId)
        => log.Count > 1
            && string.Equals(log[^1], moveId, StringComparison.Ordinal)
            && string.Equals(log[^2], moveId, StringComparison.Ordinal);
}
