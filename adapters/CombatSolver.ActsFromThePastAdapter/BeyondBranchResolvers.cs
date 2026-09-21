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
        ThirdPartyAdapterRegistry.RegisterMonsterBranchResolver("Reptomancer", "MOVE_BRANCH", Reptomancer);
        ThirdPartyAdapterRegistry.RegisterMonsterBranchResolver("Exploder", "MOVE_BRANCH", Exploder);
        ThirdPartyAdapterRegistry.RegisterMonsterBranchResolver("SnakePlant", "MOVE_BRANCH", SnakePlant);
        ThirdPartyAdapterRegistry.RegisterMonsterBranchResolver(
            "BronzeAutomaton",
            "MOVE_BRANCH",
            BronzeAutomaton);
        ThirdPartyAdapterRegistry.RegisterMonsterBranchResolver("BronzeOrb", "MOVE_BRANCH", BronzeOrb);
        ThirdPartyAdapterRegistry.RegisterMonsterBranchResolver(
            "ShelledParasite",
            "MOVE_BRANCH",
            ShelledParasite);
        ThirdPartyAdapterRegistry.RegisterMonsterBranchResolver("Collector", "MOVE_BRANCH", Collector);
        ThirdPartyAdapterRegistry.RegisterMonsterBranchResolver(
            "GremlinLeader",
            "MOVE_BRANCH",
            GremlinLeader);
        ThirdPartyAdapterRegistry.RegisterMonsterBranchResolver("Byrd", "FIRST_MOVE_BRANCH", ByrdFirstMove);
        ThirdPartyAdapterRegistry.RegisterMonsterBranchResolver("Byrd", "FLYING_BRANCH", ByrdFlying);
        ThirdPartyAdapterRegistry.RegisterMonsterBranchResolver("Nemesis", "MOVE_BRANCH", Nemesis);
        ThirdPartyAdapterRegistry.RegisterMonsterBranchResolver("Lagavulin", "MAIN_BRANCH", Lagavulin);
        ThirdPartyAdapterRegistry.RegisterMonsterBranchResolver("WrithingMass", "MOVE_BRANCH", WrithingMass);
        foreach ((string monster, string branch) in PureSelectors)
            ThirdPartyAdapterRegistry.RegisterPureBranchSelector(monster, branch);
    }

    internal static readonly string[] RegisteredMonsterTypes =
        ["Repulsor", "Spiker", "OrbWalker", "SpireGrowth", "Maw", "GiantHead", "Reptomancer", "Exploder",
         "SnakePlant", "BronzeAutomaton", "BronzeOrb", "ShelledParasite", "Collector", "GremlinLeader",
         "Byrd", "Nemesis", "Lagavulin", "WrithingMass"];

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
        ("Reptomancer", "MOVE_BRANCH"),
        ("SnakePlant", "MOVE_BRANCH"),
        ("ShelledParasite", "MOVE_BRANCH"),
        ("GremlinLeader", "MOVE_BRANCH"),
        ("Byrd", "FIRST_MOVE_BRANCH"),
        ("Byrd", "FLYING_BRANCH"),
        ("Lagavulin", "MAIN_BRANCH"),
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
    /// SnakePlant.SelectNextMove：抽一次 <c>NextInt(100)</c>；`&lt; 65` 时最近**连着两次**不是 CHOMP 就
    /// CHOMP、否则 SPORES；否则只要「上一步不是 SPORES 且上上步也不是 SPORES」就 SPORES，再不然 CHOMP。
    /// </summary>
    /// <remarks>
    /// 只读 rng 与行动历史，不写任何状态，所以**已声明为纯读取**。注意源码里那条 `LastMoveBefore` 判的是
    /// **上上步**（`stateLog[^2]`），不是「连续两次」——它允许「SPORES、CHOMP、SPORES」这种间隔。
    /// </remarks>
    private static string SnakePlant(
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
        if (num < 65)
            return LastTwoMoves(log, "CHOMP") ? "SPORES" : "CHOMP";
        return !LastMove(log, "SPORES") && !LastMoveBefore(log, "SPORES") ? "SPORES" : "CHOMP";
    }

    private static bool LastMoveBefore(IReadOnlyList<string> log, string moveId)
        => log.Count > 1 && string.Equals(log[^2], moveId, StringComparison.Ordinal);

    /// <summary>
    /// BronzeAutomaton.SelectNextMove：**一次 RNG 都不抽**——计数到 4 就清零并 HYPER_BEAM；上一步是
    /// HYPER_BEAM 就 BOOST；否则计数 +1，上一步既不是 BOOST 也不是 SPAWN_ORBS 就 BOOST，再不然 FLAIL。
    /// </summary>
    /// <remarks>它写自己的计数（开场 0 由 `AfterAddedToRoom` 写入，已在根里），**不在**纯读取名单里。</remarks>
    private static string BronzeAutomaton(
        MonsterModel monster,
        string branchId,
        IReadOnlyList<string> log,
        Rng rng,
        SimulatedCombatState combat,
        CombatPredictionSimulator simulator)
    {
        _ = branchId;
        _ = rng;
        _ = simulator;
        int numTurns = combat.GetMonsterInt(monster.Creature, "_numTurns");
        if (numTurns == 4)
        {
            combat.SetMonsterInt(monster.Creature, "_numTurns", 0);
            return "HYPER_BEAM";
        }
        if (LastMove(log, "HYPER_BEAM"))
            return "BOOST";
        combat.SetMonsterInt(monster.Creature, "_numTurns", numTurns + 1);
        if (!LastMove(log, "BOOST") && !LastMove(log, "SPAWN_ORBS"))
            return "BOOST";
        return "FLAIL";
    }

    /// <summary>
    /// BronzeOrb.SelectNextMove：抽一次 <c>NextInt(100)</c>；还没定住过牌且 `&gt;= 25` ⇒ STASIS（并把
    /// <c>_usedStasis</c> 置位）；`&gt;= 70` 且最近没连出两次 SUPPORT_BEAM ⇒ SUPPORT_BEAM；否则最近没连出
    /// 两次 BEAM ⇒ BEAM、再不然 SUPPORT_BEAM。
    /// </summary>
    /// <remarks>它写 <c>_usedStasis</c>，**不在**纯读取名单里；抽样顺序与那两处短路照抄。</remarks>
    private static string BronzeOrb(
        MonsterModel monster,
        string branchId,
        IReadOnlyList<string> log,
        Rng rng,
        SimulatedCombatState combat,
        CombatPredictionSimulator simulator)
    {
        _ = branchId;
        _ = simulator;
        int num = rng.NextInt(100);
        if (!combat.GetMonsterBool(monster.Creature, "_usedStasis") && num >= 25)
        {
            combat.SetMonsterBool(monster.Creature, "_usedStasis", true);
            return "STASIS";
        }
        if (num >= 70 && !LastTwoMoves(log, "SUPPORT_BEAM"))
            return "SUPPORT_BEAM";
        return LastTwoMoves(log, "BEAM") ? "SUPPORT_BEAM" : "BEAM";
    }

    /// <summary>
    /// ShelledParasite.SelectNextMove：抽 <c>NextInt(100)</c>；`&lt; 20` 时上一步不是 FELL ⇒ FELL，
    /// 否则**再抽一次**（区间 `[20,100)` 的重载）；`&lt; 60` 时最近没连出两次 DOUBLE_STRIKE ⇒ 它，
    /// 否则 LIFE_SUCK；再不然最近没连出两次 LIFE_SUCK ⇒ 它，否则 DOUBLE_STRIKE。
    /// </summary>
    /// <remarks>
    /// 那条重载会**多抽一次**（`rng.NextInt(min, 100)`），区间也必须照抄——抽少了后续回合整体错位。
    /// 它只读 rng 与行动历史，不写状态，**已声明为纯读取**。
    /// </remarks>
    private static string ShelledParasite(
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
        if (num < 20)
        {
            if (!LastMove(log, "FELL"))
                return "FELL";
            return ShelledParasiteReroll(log, rng, 20);
        }
        if (num < 60)
            return LastTwoMoves(log, "DOUBLE_STRIKE") ? "LIFE_SUCK" : "DOUBLE_STRIKE";
        return LastTwoMoves(log, "LIFE_SUCK") ? "DOUBLE_STRIKE" : "LIFE_SUCK";
    }

    /// <summary>ShelledParasite 的重载分支：在 <c>[min, 100)</c> 里再抽一次，之后只剩两路判断。</summary>
    private static string ShelledParasiteReroll(IReadOnlyList<string> log, Rng rng, int min)
    {
        int num = rng.NextInt(min, 100);
        if (num < 60)
            return LastTwoMoves(log, "DOUBLE_STRIKE") ? "LIFE_SUCK" : "DOUBLE_STRIKE";
        return LastTwoMoves(log, "LIFE_SUCK") ? "DOUBLE_STRIKE" : "LIFE_SUCK";
    }

    /// <summary>
    /// Collector.SelectNextMove：回合计数 +1；`_initialSpawn` 还没清 ⇒ SPAWN（不抽 RNG）；≥ 3 回合且大招
    /// 没用过 ⇒ MEGA_DEBUFF（也不抽 RNG）；否则抽 `NextInt(100)`，`&lt;= 25` 且有火炬头死了且上一步不是
    /// REVIVE ⇒ REVIVE；`&lt;= 70` 且最近没连出两次 FIREBALL ⇒ FIREBALL；再不然上一步不是 BUFF ⇒ BUFF，
    /// 否则 FIREBALL。
    /// </summary>
    /// <remarks>
    /// 三处「不抽 RNG」的短路与 `IsMinionDead`（存活火炬头 < 布点表里 `torch` 槽数）都逐条照抄；
    /// 它写自己的三个标量，**不在**纯读取名单里。
    /// </remarks>
    private static string Collector(
        MonsterModel monster,
        string branchId,
        IReadOnlyList<string> log,
        Rng rng,
        SimulatedCombatState combat,
        CombatPredictionSimulator simulator)
    {
        _ = branchId;
        int turnsTaken = combat.GetMonsterInt(monster.Creature, "_turnsTaken") + 1;
        combat.SetMonsterInt(monster.Creature, "_turnsTaken", turnsTaken);
        if (combat.GetMonsterBool(monster.Creature, "_initialSpawn"))
            return "SPAWN";
        if (turnsTaken >= 3 && !combat.GetMonsterBool(monster.Creature, "_ultUsed"))
            return "MEGA_DEBUFF";
        int num = rng.NextInt(100);
        if (num <= 25 && IsCollectorMinionDead(monster, combat, simulator) && !LastMove(log, "REVIVE"))
            return "REVIVE";
        if (num <= 70 && !LastTwoMoves(log, "FIREBALL"))
            return "FIREBALL";
        return LastMove(log, "BUFF") ? "FIREBALL" : "BUFF";
    }

    /// <summary>Collector.IsMinionDead：存活队友（不含自己）少于布点表里 `torch` 开头的槽位数。</summary>
    private static bool IsCollectorMinionDead(
        MonsterModel monster,
        SimulatedCombatState combat,
        CombatPredictionSimulator simulator)
    {
        int alive = 0;
        foreach (Creature teammate in combat.GetTeammatesOf(monster.Creature))
        {
            if (teammate != monster.Creature && simulator.State.GetCreature(teammate).IsAlive)
                alive++;
        }
        int torchSlots = 0;
        foreach (string slot in combat.EncounterSlots)
        {
            if (slot.StartsWith("torch", StringComparison.Ordinal))
                torchSlots++;
        }
        return alive < torchSlots;
    }

    /// <summary>
    /// GremlinLeader.SelectNextMove：`num` ＝存活小鬼数，`num2` ＝**先抽**的 <c>NextInt(100)</c>；
    /// `num == 0` 时 `num2 &lt; 75` ⇒ RALLY（上一步是 RALLY 就 STAB）、否则 STAB（上一步是 STAB 就 RALLY）；
    /// `num &lt; 2` 时 `num2 &lt; 50` ⇒ RALLY（上一步是 RALLY 就走 <see cref="GremlinLeaderUpper"/>）、
    /// 否则直接走 <see cref="GremlinLeaderUpper"/>；其余 `num2 &lt; 66` ⇒ ENCOURAGE（上一步是 ENCOURAGE 就
    /// STAB）、否则 STAB（上一步是 STAB 就 ENCOURAGE）。
    /// </summary>
    /// <remarks>
    /// 那只 <c>num2</c> 在三条大分支之前就抽掉了，短路只影响后面是否再抽；只读 rng、行动历史与模拟状态里
    /// 的存活小鬼数，不写状态，**已声明为纯读取**。
    /// </remarks>
    private static string GremlinLeader(
        MonsterModel monster,
        string branchId,
        IReadOnlyList<string> log,
        Rng rng,
        SimulatedCombatState combat,
        CombatPredictionSimulator simulator)
    {
        _ = branchId;
        int alive = 0;
        foreach (Creature teammate in combat.GetTeammatesOf(monster.Creature))
        {
            if (teammate != monster.Creature && simulator.State.GetCreature(teammate).IsAlive)
                alive++;
        }
        int num = rng.NextInt(100);
        if (alive == 0)
        {
            if (num < 75)
                return LastMove(log, "RALLY") ? "STAB" : "RALLY";
            return LastMove(log, "STAB") ? "RALLY" : "STAB";
        }
        if (alive < 2)
        {
            if (num < 50 && !LastMove(log, "RALLY"))
                return "RALLY";
            return GremlinLeaderUpper(log, rng);
        }
        if (num < 66)
            return LastMove(log, "ENCOURAGE") ? "STAB" : "ENCOURAGE";
        return LastMove(log, "STAB") ? "ENCOURAGE" : "STAB";
    }

    /// <summary>
    /// Byrd.SelectFromUpperRange：再抽一次 <c>NextInt(100)</c>；`&lt; 60` ⇒ ENCOURAGE（上一步是它
    /// 就 STAB）；否则上一步不是 STAB 就 STAB，只有上一步**是** STAB 时才再抽 <c>NextInt(80)</c>：
    /// `&lt; 50` ⇒ RALLY（上一步是 RALLY 就 ENCOURAGE）、否则 ENCOURAGE（上一步是 ENCOURAGE 就 STAB）。
    /// </summary>
    private static string GremlinLeaderUpper(IReadOnlyList<string> log, Rng rng)
    {
        int num = rng.NextInt(100);
        if (num < 60)
            return LastMove(log, "ENCOURAGE") ? "STAB" : "ENCOURAGE";
        if (!LastMove(log, "STAB"))
            return "STAB";
        int num2 = rng.NextInt(80);
        if (num2 < 50)
            return LastMove(log, "RALLY") ? "ENCOURAGE" : "RALLY";
        return LastMove(log, "ENCOURAGE") ? "STAB" : "ENCOURAGE";
    }

    /// <summary>
    /// Byrd.SelectFirstMove（开场那个分支状态，是状态机的初始状态）：抽一次 <c>NextFloat(1)</c>，
    /// `&lt; 0.375` ⇒ CAW，否则 PECK。只读 rng，**已声明为纯读取**。
    /// </summary>
    private static string ByrdFirstMove(
        MonsterModel monster,
        string branchId,
        IReadOnlyList<string> log,
        Rng rng,
        SimulatedCombatState combat,
        CombatPredictionSimulator simulator)
    {
        _ = monster;
        _ = branchId;
        _ = log;
        _ = combat;
        _ = simulator;
        return rng.NextFloat(1f) < 0.375f ? "CAW" : "PECK";
    }

    /// <summary>
    /// Byrd.SelectFlyingMove：抽 <c>NextInt(100)</c>；`&lt; 50` 时最近连着两次 PECK 就再抽一次
    /// <c>NextFloat(1)</c>（`&lt; 0.4` ⇒ SWOOP，否则 CAW）、不然 PECK；`&lt; 70` 时上一步是 SWOOP 就再抽
    /// <c>NextFloat(1)</c>（`&lt; 0.375` ⇒ CAW，否则 PECK）、不然 SWOOP；其余上一步是 CAW 就再抽
    /// <c>NextFloat(1)</c>（`&lt; 0.2857` ⇒ SWOOP，否则 PECK），再不然 CAW。
    /// </summary>
    /// <remarks>只读 rng 与行动历史，**已声明为纯读取**；那三次条件抽样的短路顺序照抄。</remarks>
    private static string ByrdFlying(
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
        if (num < 50)
        {
            if (!LastTwoMoves(log, "PECK"))
                return "PECK";
            return rng.NextFloat(1f) < 0.4f ? "SWOOP" : "CAW";
        }
        if (num < 70)
        {
            if (!LastMove(log, "SWOOP"))
                return "SWOOP";
            return rng.NextFloat(1f) < 0.375f ? "CAW" : "PECK";
        }
        if (!LastMove(log, "CAW"))
            return "CAW";
        return rng.NextFloat(1f) < 0.2857f ? "SWOOP" : "PECK";
    }

    /// <summary>
    /// Nemesis.SelectNextMove：**先把镰刀冷却 -1**；`_firstMove` 时置假并抽 <c>NextInt(100)</c>
    /// （`&lt; 50` ⇒ TRI_ATTACK 否则 TRI_BURN）；否则抽 `NextInt(100)`：`&lt; 30` 时上一步不是 SCYTHE 且冷却
    /// ≤ 0 ⇒ SCYTHE（并把冷却置 2），再抽 `NextFloat(1)`（`&lt; 0.5` ⇒ 最近没连出两次 TRI_ATTACK 就 TRI_ATTACK、
    /// 否则 TRI_BURN；不然上一步不是 TRI_BURN 就 TRI_BURN、否则 TRI_ATTACK）；`&lt; 65` 时最近没连出两次
    /// TRI_ATTACK ⇒ TRI_ATTACK，再抽 `NextFloat(1)`（`&lt; 0.5` ⇒ 冷却 ≤ 0 就 SCYTHE 否则 TRI_BURN），否则
    /// TRI_BURN；其余上一步不是 TRI_BURN ⇒ TRI_BURN，再抽 `NextFloat(1)`（`&lt; 0.5` 且冷却 ≤ 0）⇒ SCYTHE，
    /// 否则 TRI_ATTACK。
    /// </summary>
    /// <remarks>
    /// 冷却递减、`_firstMove` 与三处「先判后抽」的顺序都逐条照抄；它写自己的两个标量，
    /// **不在**纯读取名单里。
    /// </remarks>
    private static string Nemesis(
        MonsterModel monster,
        string branchId,
        IReadOnlyList<string> log,
        Rng rng,
        SimulatedCombatState combat,
        CombatPredictionSimulator simulator)
    {
        _ = branchId;
        _ = simulator;
        int cooldown = combat.GetMonsterInt(monster.Creature, "_scytheCooldown") - 1;
        combat.SetMonsterInt(monster.Creature, "_scytheCooldown", cooldown);
        if (combat.GetMonsterBool(monster.Creature, "_firstMove"))
        {
            combat.SetMonsterBool(monster.Creature, "_firstMove", false);
            return rng.NextInt(100) < 50 ? "TRI_ATTACK" : "TRI_BURN";
        }
        int num = rng.NextInt(100);
        if (num < 30)
        {
            if (!LastMove(log, "SCYTHE") && cooldown <= 0)
            {
                combat.SetMonsterInt(monster.Creature, "_scytheCooldown", 2);
                return "SCYTHE";
            }
            if (rng.NextFloat(1f) < 0.5f)
                return LastTwoMoves(log, "TRI_ATTACK") ? "TRI_BURN" : "TRI_ATTACK";
            return LastMove(log, "TRI_BURN") ? "TRI_ATTACK" : "TRI_BURN";
        }
        if (num < 65)
        {
            if (!LastTwoMoves(log, "TRI_ATTACK"))
                return "TRI_ATTACK";
            if (rng.NextFloat(1f) < 0.5f)
            {
                if (cooldown <= 0)
                {
                    combat.SetMonsterInt(monster.Creature, "_scytheCooldown", 2);
                    return "SCYTHE";
                }
                return "TRI_BURN";
            }
            return "TRI_BURN";
        }
        if (!LastMove(log, "TRI_BURN"))
            return "TRI_BURN";
        if (rng.NextFloat(1f) < 0.5f && cooldown <= 0)
        {
            combat.SetMonsterInt(monster.Creature, "_scytheCooldown", 2);
            return "SCYTHE";
        }
        return "TRI_ATTACK";
    }

    /// <summary>
    /// Lagavulin.SelectNextMove（分支 Id 是 <c>MAIN_BRANCH</c>）：**一次 RNG 都不抽**——还没醒且身上
    /// 还挂着睡眠 Power ⇒ SLEEP；`StartsAwake` 且行动历史为空 ⇒ DEBUFF；减益计数 ≥ 2 ⇒ DEBUFF；
    /// 最近连出两次 ATTACK ⇒ DEBUFF；否则 ATTACK。
    /// </summary>
    /// <remarks>
    /// 只读模拟状态（`_isAwake`／`_debatTurnCount`… 与睡眠 Power 是否存在）与行动历史，不写状态，
    /// **已声明为纯读取**。`StartsAwake` 是**遭遇**决定的整场固定值（另一个遭遇
    /// <c>DeadAdventurerLagavulin</c> 会把新实例的 `StartsAwake` 置真），根捕获时已播种。
    /// </remarks>
    private static string Lagavulin(
        MonsterModel monster,
        string branchId,
        IReadOnlyList<string> log,
        Rng rng,
        SimulatedCombatState combat,
        CombatPredictionSimulator simulator)
    {
        _ = branchId;
        _ = rng;
        _ = simulator;
        bool asleep = combat.EffectivePowers().Any(power =>
            power.Amount > 0
            && ReferenceEquals(power.Owner, monster.Creature)
            && string.Equals(power.GetType().Name, "AsleepLagavulinPower", StringComparison.Ordinal));
        if (!combat.GetMonsterBool(monster.Creature, "_isAwake") && asleep)
            return "SLEEP";
        if (combat.GetMonsterBool(monster.Creature, "_startsAwake") && log.Count == 0)
            return "DEBUFF";
        if (combat.GetMonsterInt(monster.Creature, "_debuffTurnCount") >= 2)
            return "DEBUFF";
        return LastTwoMoves(log, "ATTACK") ? "DEBUFF" : "ATTACK";
    }

    /// <summary>
    /// WrithingMass.SelectNextMove：`_firstMove` 时置假并按 `NextInt(100)` 三选一（&lt; 33 多段／&lt; 66
    /// 攻防／否则减益）；否则抽 `NextInt(100)`，落进 10／20／40／70 四个档位，每档先试自己的候选，
    /// 失败才**再抽**（`10 + NextInt(90)`／`NextFloat(1)`／`20 + NextInt(80)`／`40 + NextInt(60)`／
    /// `NextInt(70)`）继续判——每一处抽样与短路都照抄源码。
    /// </summary>
    /// <remarks>它写 `_firstMove`／`_usedMegaDebuff`，**不在**纯读取名单里。</remarks>
    private static string WrithingMass(
        MonsterModel monster,
        string branchId,
        IReadOnlyList<string> log,
        Rng rng,
        SimulatedCombatState combat,
        CombatPredictionSimulator simulator)
    {
        _ = branchId;
        _ = simulator;
        if (combat.GetMonsterBool(monster.Creature, "_firstMove"))
        {
            combat.SetMonsterBool(monster.Creature, "_firstMove", false);
            int first = rng.NextInt(100);
            return first < 33 ? "MULTI_HIT" : first < 66 ? "ATTACK_BLOCK" : "ATTACK_DEBUFF";
        }
        int num = rng.NextInt(100);
        if (num < 10)
        {
            if (!LastMove(log, "BIG_HIT"))
                return "BIG_HIT";
            num = 10 + rng.NextInt(90);
        }
        if (num < 20)
        {
            if (!combat.GetMonsterBool(monster.Creature, "_usedMegaDebuff") && !LastMove(log, "MEGA_DEBUFF"))
            {
                combat.SetMonsterBool(monster.Creature, "_usedMegaDebuff", true);
                return "MEGA_DEBUFF";
            }
            if (rng.NextFloat(1f) < 0.1f && !LastMove(log, "BIG_HIT"))
                return "BIG_HIT";
            num = 20 + rng.NextInt(80);
        }
        if (num < 40)
        {
            if (!LastMove(log, "ATTACK_DEBUFF"))
                return "ATTACK_DEBUFF";
            if (rng.NextFloat(1f) < 0.4f && !LastMove(log, "BIG_HIT"))
                return "BIG_HIT";
            num = 40 + rng.NextInt(60);
        }
        if (num < 70)
        {
            if (!LastMove(log, "MULTI_HIT"))
                return "MULTI_HIT";
            if (rng.NextFloat(1f) < 0.3f)
                return "ATTACK_BLOCK";
            return LastMove(log, "ATTACK_DEBUFF") ? "BIG_HIT" : "ATTACK_DEBUFF";
        }
        if (!LastMove(log, "ATTACK_BLOCK"))
            return "ATTACK_BLOCK";
        num = rng.NextInt(70);
        if (num < 10 && !LastMove(log, "BIG_HIT"))
            return "BIG_HIT";
        if (num < 40 && !LastMove(log, "ATTACK_DEBUFF"))
            return "ATTACK_DEBUFF";
        return "MULTI_HIT";
    }

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
    /// Reptomancer.SelectNextMove：抽一次 <c>NextInt(100)</c>；`&lt; 33` 时上一步不是 SNAKE_STRIKE 就直接
    /// SNAKE_STRIKE、否则<b>按重掷区间再抽</b>；`&lt; 66` 时最近没连出两次 SPAWN_DAGGER 且存活匕首少于 4
    /// 就 SPAWN_DAGGER、否则 SNAKE_STRIKE；其余上一步不是 BIG_BITE 就 BIG_BITE、否则再重掷。
    /// 重掷函数是**递归**的，每次抽的是区间内的数（区间每次不同），必须连同区间一起照抄。
    /// </summary>
    /// <remarks>
    /// 它只读行动历史、rng 与模拟状态里队友的存活数（`CanSpawn`），不写任何东西，**已声明为纯读取**。
    /// </remarks>
    private static string Reptomancer(
        MonsterModel monster,
        string branchId,
        IReadOnlyList<string> log,
        Rng rng,
        SimulatedCombatState combat,
        CombatPredictionSimulator simulator)
    {
        _ = branchId;
        int num = rng.NextInt(100);
        if (num < 33)
        {
            if (!LastMove(log, "SNAKE_STRIKE"))
                return "SNAKE_STRIKE";
            return ReptomancerReroll(monster, log, rng, combat, simulator, 33, 99);
        }
        if (num < 66)
        {
            if (!LastTwoMoves(log, "SPAWN_DAGGER") && CanSpawnDagger(monster, combat, simulator))
                return "SPAWN_DAGGER";
            return "SNAKE_STRIKE";
        }
        if (!LastMove(log, "BIG_BITE"))
            return "BIG_BITE";
        return ReptomancerReroll(monster, log, rng, combat, simulator, 0, 65);
    }

    /// <summary>Reptomancer.SelectFromReroll：区间内抽一次后走同一套三路判断，必要时继续递归重掷。</summary>
    private static string ReptomancerReroll(
        MonsterModel monster,
        IReadOnlyList<string> log,
        Rng rng,
        SimulatedCombatState combat,
        CombatPredictionSimulator simulator,
        int min,
        int max)
    {
        int num = rng.NextInt(max - min + 1) + min;
        if (num < 33)
        {
            if (!LastMove(log, "SNAKE_STRIKE"))
                return "SNAKE_STRIKE";
            return ReptomancerReroll(monster, log, rng, combat, simulator, 33, 99);
        }
        if (num < 66)
        {
            if (!LastTwoMoves(log, "SPAWN_DAGGER") && CanSpawnDagger(monster, combat, simulator))
                return "SPAWN_DAGGER";
            return "SNAKE_STRIKE";
        }
        if (!LastMove(log, "BIG_BITE"))
            return "BIG_BITE";
        return ReptomancerReroll(monster, log, rng, combat, simulator, 0, 65);
    }

    /// <summary>Reptomancer.CanSpawn：存活匕首（不含自己）少于 4 只。</summary>
    private static bool CanSpawnDagger(
        MonsterModel monster,
        SimulatedCombatState combat,
        CombatPredictionSimulator simulator)
    {
        int alive = 0;
        foreach (Creature teammate in combat.GetTeammatesOf(monster.Creature))
        {
            if (teammate != monster.Creature && simulator.State.GetCreature(teammate).IsAlive)
                alive++;
        }
        return alive < 4;
    }

    /// <summary>
    /// Exploder.SelectNextMove：**一次 RNG 都不抽**——回合计数 +1，还没到 <c>ExplosiveCountdown</c>（3）就打一下，
    /// 到了就自爆。它写自己的计数，所以**不在**纯读取名单里。
    /// </summary>
    private static string Exploder(
        MonsterModel monster,
        string branchId,
        IReadOnlyList<string> log,
        Rng rng,
        SimulatedCombatState combat,
        CombatPredictionSimulator simulator)
    {
        _ = branchId;
        _ = log;
        _ = rng;
        _ = simulator;
        int turnCount = combat.GetMonsterInt(monster.Creature, "_turnCount") + 1;
        combat.SetMonsterInt(monster.Creature, "_turnCount", turnCount);
        return turnCount < BeyondMoveEffects.ExploderCountdown ? "ATTACK" : "EXPLODE";
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
