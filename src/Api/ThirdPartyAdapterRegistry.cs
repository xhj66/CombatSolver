using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.MonsterMoves;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.Random;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

/// <summary>
/// 第三方适配 Mod 的登记入口。求解器本体不认识第三方内容，适配 Mod 在初始化时把自己的
/// 怪物行动效果、行动分支与状态口径登记到这里，求解器再按精确类型/行动 Id 派发。
/// </summary>
/// <remarks>
/// **调用时机。** 只在 Mod 初始化期间登记，任何根捕获和后台搜索之前完成；搜索期间登记表不变。
/// 理由与其余镜像注册表相同：查询结果按精确类型缓存，迟到登记不会生效且不报错
/// （见 docs/THIRD_PARTY_ADAPTERS.md §3.1）。
///
/// **失败要关死。** 适配 Mod 自检不通过时应一个条目都不登记，让求解器停在门禁上并显示原因，
/// 而不是装一半（§3.2）。
///
/// **外部程序集。** 本类与其委托参数类型都是 internal，适配程序集需要 publicizer
/// （与 §2.9、§2.10 的既有内部入口一致）。
/// </remarks>
internal static class ThirdPartyAdapterRegistry
{
    /// <summary>
    /// 第三方怪物的行动效果。<paramref name="killedOwner"/> 表示这个行动把施法者自己移除出场
    /// （原版 <see cref="MonsterMoveEffects.RemovesOwner"/> 的第三方对应物）。
    /// </summary>
    public delegate bool MonsterMoveEffectHandler(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner);

    /// <summary>
    /// 第三方怪物自定义分支状态的解析。必须返回 <paramref name="stateLog"/> 中确实存在的行动 Id。
    /// </summary>
    public delegate string MonsterBranchResolver(
        MonsterModel monster,
        string branchId,
        IReadOnlyList<string> stateLog,
        Rng rng,
        SimulatedCombatState combat);

    /// <summary>
    /// 第三方 Power 在自己那一方的回合开始时需要做的重置。
    /// 只负责把预测状态（<c>StateStore</c>）里的每回合计数归零一类的动作，不产生新的原生命令；
    /// 需要造成伤害、抽牌等可见效果时应改走对应的 Hook 镜像。
    /// </summary>
    public delegate void TurnStartPowerHandler(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        PowerModel power,
        IReadOnlyList<Creature> participants);

    private static readonly Dictionary<(string Type, string Id), MonsterMoveEffectHandler> MoveEffectTable = [];
    private static readonly Dictionary<(string Type, string Id), MonsterBranchResolver> BranchResolverTable = [];
    private static readonly HashSet<(string Type, string Id)> PureBranchSelectorTable = [];
    private static readonly Dictionary<string, string[]> StaticIntMemberTable = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string[]> ScalarStateMemberTable = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, TurnStartPowerHandler> TurnStartPowerTable = new(StringComparer.Ordinal);
    private static readonly HashSet<string> AllowedCombatSubscriberTypes = new(StringComparer.Ordinal);
    private static readonly HashSet<(string Type, string Id)> StableAttackTable = [];
    private static readonly HashSet<(string Type, string Id)> OwnerRemovingMoveTable = [];

    // === 门禁：ModHelper 战斗订阅者放行 ===

    /// <summary>
    /// 把某个 <c>AbstractModel</c> 订阅者类型加入根捕获前的放行名单，等价于
    /// <c>KnownPreRootSubscriberTypeNames</c> 里的内置条目。
    /// </summary>
    public static void AllowCombatSubscriber(Type subscriberType)
        => AllowCombatSubscriber(subscriberType.FullName ?? subscriberType.Name);

    /// <summary>按类型全名放行一个订阅者类型。</summary>
    public static void AllowCombatSubscriber(string subscriberTypeFullName)
    {
        ArgumentException.ThrowIfNullOrEmpty(subscriberTypeFullName);
        AllowedCombatSubscriberTypes.Add(subscriberTypeFullName);
    }

    public static bool IsAllowedCombatSubscriber(Type subscriberType)
        => AllowedCombatSubscriberTypes.Contains(subscriberType.FullName ?? string.Empty);

    // === 怪物静态数值成员 ===

    /// <summary>
    /// 声明第三方怪物身上需要冻结的静态数值成员（对应 <c>GetMonsterStaticInt</c>）。
    /// 只在根捕获时读取一次，模拟期间不变；成员必须是实例字段或属性。
    /// </summary>
    public static void RegisterStaticIntMembers(string monsterTypeName, params string[] memberNames)
        => StaticIntMemberTable[monsterTypeName] = memberNames;

    public static bool TryGetStaticIntMembers(string monsterTypeName, out string[] memberNames)
        => StaticIntMemberTable.TryGetValue(monsterTypeName, out memberNames!);

    // === 怪物标量状态（进根播种与状态指纹） ===

    /// <summary>
    /// 声明第三方怪物身上会随战斗推进变化、且分支/效果依赖的标量成员名。
    /// 名单里的成员会在根捕获时播种进模拟状态，并进入状态指纹与续用核对文本。
    /// </summary>
    /// <remarks>
    /// 没登记的成员仍可在效果里用 <c>GetMonsterInt</c> 惰性读取，但根捕获之后对根生物读取
    /// 未播种成员会明确失败——这正是要避免的「装一半」。
    /// </remarks>
    public static void RegisterMonsterStateMembers(string monsterTypeName, params string[] memberNames)
        => ScalarStateMemberTable[monsterTypeName] = memberNames;

    public static bool TryGetMonsterStateMembers(string monsterTypeName, out string[] memberNames)
        => ScalarStateMemberTable.TryGetValue(monsterTypeName, out memberNames!);

    // === 第三方 Power 的回合开始重置 ===

    /// <summary>
    /// 登记第三方 Power 在自己那一方回合开始时的预测状态重置。
    /// </summary>
    /// <remarks>
    /// 原版这条路径按类型写死（硬化外壳、懒惰、虚空形态……），第三方类型落在 <c>switch</c> 外，
    /// 结果是「每回合计数永远不清零」——数值不会报错，但从第二回合起整个预测都是错的。
    /// 需要这类重置的适配 Mod 必须在这里登记，否则不要声称自己镜像了这个 Power。
    /// </remarks>
    public static void RegisterTurnStartPower(string powerTypeName, TurnStartPowerHandler handler)
        => TurnStartPowerTable.Add(powerTypeName, handler);

    public static bool TryGetTurnStartPower(string powerTypeName, out TurnStartPowerHandler handler)
        => TurnStartPowerTable.TryGetValue(powerTypeName, out handler!);

    // === 怪物行动效果 ===

    public static void RegisterMonsterMoveEffect(
        string monsterTypeName,
        string moveId,
        MonsterMoveEffectHandler handler)
        => MoveEffectTable.Add((monsterTypeName, moveId), handler);

    public static void RegisterMonsterMoveEffects(
        string monsterTypeName,
        IEnumerable<string> moveIds,
        MonsterMoveEffectHandler handler)
    {
        foreach (string moveId in moveIds)
            RegisterMonsterMoveEffect(monsterTypeName, moveId, handler);
    }

    public static bool HasMoveEffect(string monsterTypeName, string moveId)
        => MoveEffectTable.ContainsKey((monsterTypeName, moveId));

    /// <summary>
    /// 按 (怪物类型, 行动 Id) 派发第三方行动效果。
    /// 返回是否命中登记；<paramref name="applied"/> 是处理器自己的结论。
    /// </summary>
    public static bool TryApplyMove(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool applied,
        out bool killedOwner)
    {
        applied = false;
        killedOwner = false;
        MonsterModel? monster = move.Owner.Monster;
        if (monster is null)
            return false;
        if (!MoveEffectTable.TryGetValue((monster.GetType().Name, move.Move.Id), out MonsterMoveEffectHandler? handler))
            return false;
        applied = handler(simulator, combat, move, player, plannedChoices, out killedOwner);
        return true;
    }

    // === 怪物攻击意图：数值在构造时固定的行动 ===

    /// <summary>
    /// 声明第三方怪物的某个攻击行动，其伤害与段数在意图构造时即已固定，不读怪物身上的可变状态。
    /// </summary>
    /// <remarks>
    /// 意图预测器按「<c>DamageCalc</c> 是否绑定了实例」保守判断攻击是否动态，这与原版那批已知稳定行动
    /// 用同一张判定（<c>IntentForecaster.IsKnownStableAttack</c>）。原版类型写死在那张名单里，第三方类型
    /// 只能在这里登记，否则每一回合都会多报一条「动态伤害」近似。典型是
    /// <c>new MultiAttackIntent(固定伤害, 固定段数)</c> / <c>new SingleAttackIntent(固定伤害)</c>——
    /// 这两个构造函数把实参捕进闭包（<c>DamageCalc = () =&gt; damage</c>），段数是普通只读字段，
    /// 并不是在读实例状态。
    ///
    /// <para>
    /// 只登记**反编译逐行核对过**的行动，并在适配自己的自检里把用到的常量钉死：数值一变就整体拒绝登记，
    /// 而不是照旧声称已知稳定（见 docs/THIRD_PARTY_ADAPTERS.md §3.3／§3.4）。
    /// </para>
    /// </remarks>
    public static void RegisterStableAttack(string monsterTypeName, string moveId)
        => StableAttackTable.Add((monsterTypeName, moveId));

    public static bool HasStableAttack(string monsterTypeName, string moveId)
        => StableAttackTable.Contains((monsterTypeName, moveId));

    /// <summary>
    /// 核对一次「稳定攻击」声明是否真的成立：这个意图的伤害闭包必须是
    /// <c>SingleAttackIntent(int)</c> / <c>MultiAttackIntent(int, int)</c> 那两个构造函数捕下来的
    /// **构造实参**，而不是调用方传进来的委托。判据与理由见 <see cref="StableAttackShape"/>。
    /// </summary>
    /// <remarks>
    /// 登记方声明的是「数值在意图构造时即已固定」，可它自己在初始化时既拿不到怪物实例、也拿不到
    /// 那些 <c>private int X =&gt; AscensionHelper…</c> 属性的值，所以自检钉不住这件事——数字不是
    /// <c>const</c>，抄一份进适配层只会变成第二份真相。判定交给运行期，形状对不上就不认这条声明，
    /// 近似清单照旧记一条「动态伤害」，而不是让界面声称算准了。
    /// </remarks>
    public static bool IsStableAttackShape(AttackIntent attack)
        => StableAttackShape.IsConstantConstruction(attack);

    // === 怪物离场行动（逃跑／脱战） ===

    /// <summary>
    /// 声明第三方怪物的某个行动会把**施法者自己移出战斗**（原版
    /// <see cref="MonsterMoveEffects.RemovesOwner"/> 那张写死的表）。
    /// </summary>
    /// <remarks>
    /// 两侧都要这个事实，而且用的是两个不同的问题：行动效果侧在回放里把怪物移出 roster
    /// （由登记方自己在处理器里调用 <c>CreatureEscaped</c>），意图预测侧则在往后推算回合时
    /// 让这个个体退出，不再给它排后续行动。只登记一半的话，玩家会在它跑了之后还看到一条
    /// 不存在的意图。
    /// </remarks>
    public static void RegisterOwnerRemovingMove(string monsterTypeName, string moveId)
        => OwnerRemovingMoveTable.Add((monsterTypeName, moveId));

    public static bool RemovesOwner(string monsterTypeName, string moveId)
        => OwnerRemovingMoveTable.Contains((monsterTypeName, moveId));

    // === 怪物行动分支 ===

    public static void RegisterMonsterBranchResolver(
        string monsterTypeName,
        string branchId,
        MonsterBranchResolver resolver)
        => BranchResolverTable.Add((monsterTypeName, branchId), resolver);

    public static bool HasBranchResolver(string monsterTypeName, string branchId)
        => BranchResolverTable.ContainsKey((monsterTypeName, branchId));

    // === 第三方分支状态：预测器可否直接调用它的选择函数 ===

    /// <summary>
    /// 声明某个第三方分支状态的**选择函数是纯读取**，因而 <see cref="IntentForecaster"/> 推演后续回合时
    /// 可以照原样在实机模型上调用它。
    /// </summary>
    /// <remarks>
    /// 第三方 <c>ConditionalBranchState.GetNextState</c> 的委托是在**实机模型**上跑的，它可以写实机字段：
    /// 往昔之书的 <c>SelectNextMove</c> 每次都 <c>StabCount++</c>，而它的攻击段数正是读这个计数
    /// （<c>DynamicMultiAttackIntent(() =&gt; StabDamage, () =&gt; StabCount)</c>）。预测器一次推演要看 16 个
    /// 回合，未声明就把实机计数推高十几格——界面显示 7×15，**真实战斗**也会照着 7×15 打。
    ///
    /// <para>
    /// 所以默认**不调用**：命中未声明的第三方分支状态时，推演在那条怪物上停下并记一条
    /// <c>unsupported</c>，而不是让预测去改动玩家的实际战斗。声明是「已复核事实」，与
    /// <c>RegisterIgnored</c> 同一类：只有逐行反编译确认选择函数只读字段／只读实机 StateLog 与传入的
    /// <c>rng</c>、不写任何实机状态、不下命令，才允许登记。声明前必须先登记同一 (怪物, 分支) 的
    /// <see cref="RegisterMonsterBranchResolver"/>——两者缺一，这条分支要么算不出来、要么会把战斗改坏。
    /// </para>
    /// </remarks>
    public static void RegisterPureBranchSelector(string monsterTypeName, string branchId)
    {
        ArgumentException.ThrowIfNullOrEmpty(monsterTypeName);
        ArgumentException.ThrowIfNullOrEmpty(branchId);
        if (!BranchResolverTable.ContainsKey((monsterTypeName, branchId)))
        {
            throw new InvalidOperationException(
                $"({monsterTypeName}, {branchId}) 还没登记分支解析实现；" +
                "只声明选择函数是纯读取没有意义，搜索侧仍会拒绝这条分支。");
        }
        PureBranchSelectorTable.Add((monsterTypeName, branchId));
    }

    public static bool IsBranchSelectorInvocationAllowed(string monsterTypeName, string branchId)
        => PureBranchSelectorTable.Contains((monsterTypeName, branchId));

    public static bool TryResolveBranch(
        MonsterModel monster,
        string branchId,
        IReadOnlyList<string> stateLog,
        Rng rng,
        SimulatedCombatState combat,
        out string resolved)
    {
        resolved = string.Empty;
        if (!BranchResolverTable.TryGetValue(
                (monster.GetType().Name, branchId),
                out MonsterBranchResolver? resolver))
        {
            return false;
        }
        resolved = resolver(monster, branchId, stateLog, rng, combat);
        if (string.IsNullOrEmpty(resolved))
        {
            throw new PredictionUnsupportedException(
                $"第三方怪物 {monster.GetType().FullName} 的分支状态 {branchId} 的登记实现返回了空行动 Id。");
        }
        return true;
    }
}
