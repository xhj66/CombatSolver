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
        ThirdPartyAdapterRegistry.RegisterMonsterBranchResolver("Mugger", "MUG_BRANCH", Mugger);
        ThirdPartyAdapterRegistry.RegisterMonsterBranchResolver("Romeo", "MOVE_BRANCH", Romeo);
        ThirdPartyAdapterRegistry.RegisterMonsterBranchResolver("Chosen", "MOVE_BRANCH", Chosen);
        ThirdPartyAdapterRegistry.RegisterMonsterBranchResolver("Champ", "MOVE_BRANCH", Champ);
        ThirdPartyAdapterRegistry.RegisterMonsterBranchResolver(
            "BookOfStabbing",
            "MOVE_BRANCH",
            BookOfStabbing);
        foreach ((string monster, string branch) in PureSelectors)
            ThirdPartyAdapterRegistry.RegisterPureBranchSelector(monster, branch);
    }

    internal static readonly string[] RegisteredMonsterTypes =
        ["Centurion", "Mystic", "Mugger", "Romeo", "Chosen", "Champ", "BookOfStabbing"];

    /// <summary>
    /// 逐行复核为「纯读取」的 (怪物, 分支)：都只读实机 StateLog、传入的 rng 与自己的标量字段，
    /// 不写实机状态、不下命令；<c>Mystic</c> 那条另外读**模拟状态**里队友的血量（也不是实机字段）。
    /// </summary>
    /// <remarks>
    /// **`Chosen`、`Champ` 与 `BookOfStabbing` 刻意不在名单里**：它们的选择函数会写自己的计数器
    /// （`Chosen._usedHex`、`Champ._numTurns` / `_thresholdReached` / `_forgeTimes`、
    /// `BookOfStabbing._stabCount`）。预览默认不调用未声明的第三方分支，正是为了不让这种
    /// 「选择即记账」的委托去改实机状态——往昔之书的 `StabCount++` 在预览里被反复调用时，
    /// 就是这么把真实战斗改成 7×15 的（§2.9）。这三只的预览会在分支处停下
    /// 并显示「预览可能不完整」，搜索侧照常按登记的解析器算。
    /// </remarks>
    internal static readonly (string Monster, string Branch)[] PureSelectors =
    [
        ("Centurion", "MOVE_BRANCH"),
        ("Mystic", "MOVE_BRANCH"),
        ("Mugger", "MUG_BRANCH"),
        ("Romeo", "MOVE_BRANCH"),
    ];

    /// <summary>
    /// BookOfStabbing.SelectNextMove：先抽一次 RNG；15 以下时最近一步是 BIG_STAB 就 STAB、
    /// 否则 BIG_STAB；最近连出两次 STAB 就 BIG_STAB；其余 STAB。**四条出口全都先
    /// <c>_stabCount++</c>**——这个计数就是每次 <c>STAB</c> 的段数（见
    /// <c>CityMoveEffects</c> 里登记的动态攻击值），所以这条分支属于「选择即记账」，
    /// 不在 <see cref="PureSelectors"/> 里。
    /// </summary>
    private static string BookOfStabbing(
        MonsterModel monster,
        string branchId,
        IReadOnlyList<string> log,
        Rng rng,
        SimulatedCombatState combat,
        CombatPredictionSimulator simulator)
    {
        _ = simulator;
        int num = rng.NextInt(100);
        string move = num < 15
            ? LastMove(log, "BIG_STAB") ? "STAB" : "BIG_STAB"
            : LastTwoMoves(log, "STAB") ? "BIG_STAB" : "STAB";
        // 与源码同序：RNG 先抽，四条出口各自 StabCount++（无分支差异，等价于出口前统一 +1）。
        combat.SetMonsterInt(
            monster.Creature,
            "_stabCount",
            combat.GetMonsterInt(monster.Creature, "_stabCount") + 1);
        return move;
    }

    /// <summary>
    /// Chosen.SelectNextMove：开场**必定**先来一次 HEX（并把 <c>UsedHex</c> 置位——这一步会写状态，
    /// 所以它不在纯读取名单里）；之后若上一步不是 DEBILITATE／DRAIN 就各半概率二选一，否则
    /// 40% ZAP／60% POKE。RNG 调用顺序与短路逐条照抄（走 HEX 那条一次都不抽）。
    /// </summary>
    private static string Chosen(
        MonsterModel monster,
        string branchId,
        IReadOnlyList<string> log,
        Rng rng,
        SimulatedCombatState combat,
        CombatPredictionSimulator simulator)
    {
        _ = simulator;
        if (!combat.GetMonsterBool(monster.Creature, "_usedHex"))
        {
            combat.SetMonsterBool(monster.Creature, "_usedHex", true);
            return "HEX";
        }
        if (!LastMove(log, "DEBILITATE") && !LastMove(log, "DRAIN"))
            return rng.NextInt(100) < 50 ? "DEBILITATE" : "DRAIN";
        return rng.NextInt(100) < 40 ? "ZAP" : "POKE";
    }

    /// <summary>
    /// Champ.SelectNextMove：先把回合计数 +1；血量掉到**一半以下**且还没触发过就置位并 ANGER；
    /// 触发过之后只要最近两次都不是 EXECUTE 就 EXECUTE；第 4 回合且还没触发过就清零计数并 TAUNT；
    /// 否则抽一次 RNG，30% 以下优先锻炉（最多 <c>ForgeThreshold</c> 次、不连出）、再 GLOAT、
    /// 再 55% 以下 FACE_SLAP、否则 HEAVY_SLASH／FACE_SLAP。
    /// </summary>
    private static string Champ(
        MonsterModel monster,
        string branchId,
        IReadOnlyList<string> log,
        Rng rng,
        SimulatedCombatState combat,
        CombatPredictionSimulator simulator)
    {
        int numTurns = combat.GetMonsterInt(monster.Creature, "_numTurns") + 1;
        combat.SetMonsterInt(monster.Creature, "_numTurns", numTurns);
        SimCreatureState creature = simulator.State.GetCreature(monster.Creature);
        bool thresholdReached = combat.GetMonsterBool(monster.Creature, "_thresholdReached");
        if (creature.CurrentHp < creature.MaxHp / 2 && !thresholdReached)
        {
            combat.SetMonsterBool(monster.Creature, "_thresholdReached", true);
            return "ANGER";
        }
        if (thresholdReached && !LastMove(log, "EXECUTE") && !LastMoveBefore(log, "EXECUTE"))
            return "EXECUTE";
        if (numTurns == 4 && !thresholdReached)
        {
            combat.SetMonsterInt(monster.Creature, "_numTurns", 0);
            return "TAUNT";
        }
        int forgeTimes = combat.GetMonsterInt(monster.Creature, "_forgeTimes");
        int num = rng.NextInt(100);
        if (!LastMove(log, "DEFENSIVE_STANCE") && forgeTimes < champForgeThreshold && num < 30)
        {
            combat.SetMonsterInt(monster.Creature, "_forgeTimes", forgeTimes + 1);
            return "DEFENSIVE_STANCE";
        }
        if (!LastMove(log, "GLOAT") && !LastMove(log, "DEFENSIVE_STANCE") && num < 30)
            return "GLOAT";
        if (!LastMove(log, "FACE_SLAP") && num < 55)
            return "FACE_SLAP";
        return LastMove(log, "HEAVY_SLASH") ? "FACE_SLAP" : "HEAVY_SLASH";
    }

    /// <summary>Champ 的锻炉次数上限（AFTP <c>ForgeThreshold</c>），由 <c>CityMoveEffects.Verify</c> 钉死。</summary>
    internal static int champForgeThreshold = 2;

    private static bool LastMoveBefore(IReadOnlyList<string> log, string moveId)
        => log.Count > 1 && string.Equals(log[^2], moveId, StringComparison.Ordinal);

    /// <summary>
    /// Mugger.SelectAfterMug：与第一幕 Looter 同型——Mug／BigSwipe 出手不满两次就再来一次 Mug，
    /// 够了就交给随机分支 <c>AFTER_SECOND_MUG</c>（50% SMOKE_BOMB／50% BIG_SWIPE，由求解器通用的
    /// 随机分支逻辑按冻结权重抽）。这条分支不抽 RNG。
    /// </summary>
    private static string Mugger(
        MonsterModel monster,
        string branchId,
        IReadOnlyList<string> log,
        Rng rng,
        SimulatedCombatState combat,
        CombatPredictionSimulator simulator)
    {
        _ = simulator;
        return combat.GetMonsterInt(monster.Creature, "_mugCount") < 2 ? "MUG" : "AFTER_SECOND_MUG";
    }

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

    /// <summary>
    /// Romeo.SelectNextMove：只要最近没有连出两次交叉斩就继续交叉斩，否则痛苦斩。不抽 RNG
    /// （开场那一下 MOCK 的后继固定是痛苦斩，由状态机本身表达）。
    /// </summary>
    private static string Romeo(
        MonsterModel monster,
        string branchId,
        IReadOnlyList<string> log,
        Rng rng,
        SimulatedCombatState combat,
        CombatPredictionSimulator simulator)
    {
        _ = simulator;
        return LastTwoMoves(log, "CROSS_SLASH") ? "AGONIZING_SLASH" : "CROSS_SLASH";
    }

    private static bool LastMove(IReadOnlyList<string> log, string moveId)
        => log.Count > 0 && string.Equals(log[^1], moveId, StringComparison.Ordinal);

    private static bool LastTwoMoves(IReadOnlyList<string> log, string moveId)
        => log.Count > 1
            && string.Equals(log[^1], moveId, StringComparison.Ordinal)
            && string.Equals(log[^2], moveId, StringComparison.Ordinal);
}
