using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
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
    /// 第三方怪物行动的**攻击前**部分。用于「先给自己加格挡／先给玩家上状态，再打出攻击」这类行动：
    /// 求解器的通用攻击循环跑在行动效果之前，只靠 <see cref="MonsterMoveEffectHandler"/> 表达不了这个顺序。
    /// </summary>
    /// <remarks>
    /// 同一 (怪物, 行动) 同时登记攻击前与攻击后两段是允许的，各自按源码的时序运行。
    /// </remarks>
    public delegate void MonsterBeforeAttackHandler(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player);

    /// <summary>
    /// 第三方怪物自定义分支状态的解析。必须返回 <paramref name="stateLog"/> 中确实存在的行动 Id。
    /// </summary>
    /// <remarks>
    /// <paramref name="simulator"/> 用来读**模拟状态**里的数值（例如按队友已损失生命和判定要不要治疗：
    /// <c>simulator.State.GetCreature(teammate).CurrentHp</c>）。分支里**不得**读实机生物的血量、
    /// 手牌或任何会随实机推进变化的值——那正是「实机字段不随分支 Fork」要挡的事。
    /// </remarks>
    public delegate string MonsterBranchResolver(
        MonsterModel monster,
        string branchId,
        IReadOnlyList<string> stateLog,
        Rng rng,
        SimulatedCombatState combat,
        CombatPredictionSimulator simulator);

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

    /// <summary>
    /// 第三方 Power 重写的**非 Late** <c>AbstractModel.AfterSideTurnEnd</c> 的预测实现。
    /// </summary>
    /// <remarks>
    /// 求解器的回合末分两段：这一条（常规）与 <c>AfterSideTurnEndLate</c>（晚期，见
    /// <c>AfterSideTurnEndLateMirrors</c>）。原版那一批常规效果按类型写死在
    /// <c>EndTurnPowerSupport.TriggerRegular</c> 的 <c>switch</c> 里，第三方类型落在 <c>switch</c> 之外，
    /// 结果是「回合末该发生的事永远不发生」——数值不会报错，但整场预测都少一块。
    /// 需要它的适配 Mod 在这里登记；派发点是同一个 <c>switch</c> 之后、**同一轮循环内**，
    /// 所以相对其它 Power 的先后顺序与源码的模型遍历顺序一致。
    /// </remarks>
    public delegate void SideTurnEndPowerHandler(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        PowerModel power,
        CombatSide side,
        IReadOnlyCollection<Creature> participants);

    /// <summary>
    /// 第三方 Power 重写的 <c>AbstractModel.BeforeSideTurnStart</c> 的预测实现。
    /// </summary>
    /// <remarks>
    /// 这个阶段在求解器里有派发点（<c>TurnStartPowerSupport.TriggerBeforeSideTurnStart</c>），但原版那一批
    /// 效果按类型写死在里面（例如原版 <c>PlatingPower</c> 在第 1 回合玩家侧开始时给敌人格挡），第三方类型
    /// 落在那些循环之外。需要它的适配 Mod 在这里登记；处理函数在该阶段按**类型名**被调用，
    /// 未登记的类型与今天完全一样（不做任何事）。
    /// </remarks>
    public delegate void SideTurnStartPowerHandler(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        PowerModel power);

    private static readonly Dictionary<(string Type, string Id), MonsterMoveEffectHandler> MoveEffectTable = [];
    private static readonly Dictionary<(string Type, string Id), MonsterBeforeAttackHandler> MoveBeforeAttackTable = [];
    private static readonly Dictionary<(string Type, string Id), MonsterBranchResolver> BranchResolverTable = [];
    private static readonly HashSet<(string Type, string Id)> PureBranchSelectorTable = [];
    private static readonly Dictionary<string, string[]> StaticIntMemberTable = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string[]> ScalarStateMemberTable = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, TurnStartPowerHandler> TurnStartPowerTable = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, SideTurnEndPowerHandler> SideTurnEndPowerTable = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, SideTurnEndModelHandler> SideTurnEndModelTable = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, SideTurnStartPowerHandler> SideTurnStartPowerTable = new(StringComparer.Ordinal);
    private static readonly Dictionary<(string Type, string Id), MonsterMoveAttackResultHandler> MoveAttackResultTable = [];
    private static readonly HashSet<string> AllowedCombatSubscriberTypes = new(StringComparer.Ordinal);
    private static readonly HashSet<(string Type, string Id)> StableAttackTable = [];
    private static readonly Dictionary<(string Type, string Id), MonsterAttackValueResolver> DynamicAttackTable = [];
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

    /// <summary>
    /// 登记第三方 Power 自己的**常规回合末**（非 Late 的 <c>AfterSideTurnEnd</c>）预测实现。
    /// </summary>
    /// <remarks>
    /// 只登记逐行反编译核对过的实现：处理函数按源码顺序读写**模拟状态**（<c>combat</c> 的层数、格挡、
    /// 怪物标量），需要伤害/抽牌等可见效果时走 <c>simulator</c> 的对应入口。
    /// 没登记的类型与今天完全一样（不做任何事），所以这个入口不会改变原版与既有第三方内容的行为。
    /// </remarks>
    public static void RegisterSideTurnEndPower(string powerTypeName, SideTurnEndPowerHandler handler)
        => SideTurnEndPowerTable[powerTypeName] = handler;

    public static bool TryGetSideTurnEndPower(
        string powerTypeName,
        out SideTurnEndPowerHandler handler)
        => SideTurnEndPowerTable.TryGetValue(powerTypeName, out handler!);

    /// <summary>
    /// 第三方**非 Power 模型**（怪物、卡牌、遗物）重写的常规（非 Late）<c>AfterSideTurnEnd</c>。
    /// </summary>
    /// <remarks>
    /// <see cref="RegisterSideTurnEndPower"/> 只覆盖 Power（派发时遍历的是 <c>EffectivePowers()</c>）；
    /// 像往昔之章的复仇女神那样由**怪物自己**重写这个阶段、在敌人回合末切换无实体化的，需要这一条。
    /// 目前只在**敌人侧**回合末派发（覆盖战斗中的怪物）；玩家侧的模型还没开放，见
    /// docs/THIRD_PARTY_ADAPTERS.md §6。
    /// </remarks>
    public delegate void SideTurnEndModelHandler(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        AbstractModel model,
        CombatSide side,
        IReadOnlyList<Creature> participants);

    /// <summary>
    /// 登记第三方 Power 自己的 <c>BeforeSideTurnStart</c> 预测实现（每一方回合开始时按类型派发一次）。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="RegisterSideTurnEndPower"/> 同一条纪律：只登记逐行反编译核对过的实现，
    /// 处理函数只读写模拟状态；没登记的类型不做任何事，因此不改变原版与既有第三方内容的行为。
    /// </remarks>
    public static void RegisterSideTurnStartPower(string powerTypeName, SideTurnStartPowerHandler handler)
        => SideTurnStartPowerTable[powerTypeName] = handler;

    public static bool TryGetSideTurnStartPower(
        string powerTypeName,
        out SideTurnStartPowerHandler handler)
        => SideTurnStartPowerTable.TryGetValue(powerTypeName, out handler!);

    /// <summary>
    /// 一条「第三方 Power 在玩家身上 ⇒ 给牌挂某个病症」的登记项。
    /// <paramref name="CardType"/> 为 <c>null</c> 表示对任意牌生效，否则只对该类型的牌生效。
    /// </summary>
    public readonly record struct CardAfflictionSource(
        string PowerTypeName,
        Type AfflictionType,
        CardType? CardType);

    private static readonly List<CardAfflictionSource> CardAfflictionSourceTable = [];

    /// <summary>
    /// 登记第三方 Power 的「在玩家身上时给牌挂病症／消失时摘掉病症」语义，对应核心为原版
    /// <c>TangledPower</c>／<c>HexPower</c>／<c>RingingPower</c> 写死的那套规范化。
    /// </summary>
    /// <remarks>
    /// 原版这条语义在 <c>SimulatedCombatState.NormalizeCardAfflictions</c> 里按类型写死，它同时负责三件事：
    /// Power 在时给「还没有病症」的牌挂上、之后**新进入战斗**的牌也挂上、Power 消失后把病症清掉
    /// （原版那三个 Power 的 <c>AfterApplied</c>／<c>AfterCardEnteredCombat</c>／<c>AfterRemoved</c>
    /// 三个钩子在核心都没有通用分发点，全部由这套规范化等价表达）。第三方 Power 落在写死的名单外，
    /// 不登记的话这个 Power 在预测里就是**空的**：整回合的可打出性都会算错，而数值不会报错。
    ///
    /// <para>
    /// 登记方要自己复核三件事与源码一致：① 施加时给哪些牌挂（本入口用 <paramref name="cardType"/> 表达
    /// 「只给某一类牌」，挂的层数固定 1，与源码的 <c>CardCmd.Afflict&lt;T&gt;(card, 1m)</c> 一致）；
    /// ② 之后进入战斗的牌同样处理；③ Power 消失后病症被清掉。Power 自身的移除时机由登记方在
    /// <see cref="RegisterSideTurnEndPower"/> 一类的入口里表达——本入口只负责「Power 在不在 ⇒ 牌上的病症」。
    /// </para>
    ///
    /// <para>
    /// 病症实例由核心按 <paramref name="afflictionType"/> 从 <c>ModelDb</c> 取规范实例再复制（外部程序集
    /// 拿不到泛型入口）；取不到就在那一刻明确失败，而不是当成没登记。
    /// </para>
    /// </remarks>
    public static void RegisterCardAfflictionSource(
        string powerTypeName,
        Type afflictionType,
        CardType? cardType = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(powerTypeName);
        ArgumentNullException.ThrowIfNull(afflictionType);
        if (!typeof(AfflictionModel).IsAssignableFrom(afflictionType))
        {
            throw new ArgumentException(
                $"{afflictionType.FullName} 不是 AfflictionModel 类型。",
                nameof(afflictionType));
        }
        if (CardAfflictionSourceTable.Any(entry =>
                string.Equals(entry.PowerTypeName, powerTypeName, StringComparison.Ordinal)
                && entry.AfflictionType == afflictionType))
        {
            throw new InvalidOperationException(
                $"{powerTypeName} 已经登记过病症 {afflictionType.FullName} 了。");
        }
        CardAfflictionSourceTable.Add(new CardAfflictionSource(powerTypeName, afflictionType, cardType));
    }

    /// <summary>已登记的「Power ⇒ 卡牌病症」条目（按登记顺序派发；空表示核心不做额外规范化）。</summary>
    public static IReadOnlyList<CardAfflictionSource> CardAfflictionSources => CardAfflictionSourceTable;

    /// <summary>
    /// 第三方怪物行动的「攻击结算之后」部分：拿得到这次行动的**全部伤害结果**。
    /// </summary>
    /// <remarks>
    /// 求解器结算一个行动的顺序是「先攻击、再行动效果」，而 <see cref="MonsterMoveEffectHandler"/> 的形参
    /// 里没有伤害结果——像「按这次攻击造成的未被格挡伤害回血」这种行动表达不了（自己拿「伤害 − 攻击前格挡」
    /// 去凑是**近似**：易伤、无实体、虚弱都会改真实数值）。这个入口把逐段结果原样交给登记方，
    /// 派发点在攻击循环之后、行动效果之前，与源码回调用的是同一批数据。
    /// </remarks>
    public delegate void MonsterMoveAttackResultHandler(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        IReadOnlyList<DamageResult> results);

    /// <summary>
    /// 第三方「偷牌」Power 的判定：这个 Power 实例当前是否扣着某张牌。
    /// </summary>
    /// <remarks>
    /// 终局的「未追回战利品」按类型写死了原版 <c>SwipePower</c>（偷牌）与 <c>ThieveryPower</c>／
    /// <c>HeistPower</c>（偷金币）。第三方偷牌 Power 必须在这里登记，否则它的牌既不会进终局口径、
    /// 持有者死亡时也不会被核销——界面上会一直显示「丢了牌」，与实机不符。
    /// 判定交给登记方：被偷的牌存在哪由它自己决定（通常在预测状态里）。
    /// </remarks>
    public delegate bool StolenCardPowerHandler(CombatPredictionSimulator simulator, PowerModel power);

    private static readonly Dictionary<string, StolenCardPowerHandler> StolenCardPowerTable = new(StringComparer.Ordinal);

    public static void RegisterStolenCardPower(string powerTypeName, StolenCardPowerHandler hasStolenCard)
        => StolenCardPowerTable[powerTypeName] = hasStolenCard;

    public static bool TryGetStolenCardPower(
        string powerTypeName,
        out StolenCardPowerHandler handler)
        => StolenCardPowerTable.TryGetValue(powerTypeName, out handler!);

    /// <summary>这个 Power 类型是否登记过为第三方偷牌来源。</summary>
    public static bool IsRegisteredStolenCardPower(string powerTypeName)
        => StolenCardPowerTable.ContainsKey(powerTypeName);

    // === 第三方盗贼：死亡时归还携带的金币 ===

    private static readonly HashSet<string> DeathReturnedGoldTable = new(StringComparer.Ordinal);

    /// <summary>
    /// 声明「这个怪物死亡时，把它身上 <c>ThieveryPower</c> 携带的金币按奖励返还」。
    /// </summary>
    /// <remarks>
    /// 原版的「击杀盗贼拿回金币」由 <c>HeistPower.BeforeDeath</c> 实现：偷钱地精死亡时把赃款
    /// 转成一个新的 <c>HeistPower</c> 交给生成出来的同伴，杀掉那个同伴才结算奖励。第三方盗贼
    /// 用本体 <c>ThieveryPower</c> 偷钱、自己实现「死亡返还」（往昔之章的 Looter／Mugger 是在
    /// <c>Creature.Died</c> 事件里 <c>AddExtraReward(GoldReward(..., wasGoldStolenBack: true))</c>），
    /// 模拟器不触发 C# 事件，所以必须在这里登记；否则界面会一直显示「未追回金币」，
    /// 保资源排序也会去追一笔实机已经还回来的钱。
    /// </remarks>
    public static void RegisterDeathReturnedGold(string monsterTypeName)
        => DeathReturnedGoldTable.Add(monsterTypeName);

    public static bool ReturnsStolenGoldOnDeath(string monsterTypeName)
        => DeathReturnedGoldTable.Contains(monsterTypeName);

    /// <summary>
    /// 一条「第三方复活 Power」的登记项：持有者死亡时保留尸体、进入复活阶段，
    /// <paramref name="DeadMoveId"/> 那一回合什么都不做，之后 <paramref name="ReviveMoveId"/> 治疗
    /// <c>power.Amount</c> 点并复活。
    /// </summary>
    public readonly record struct RevivePowerRegistration(
        string PowerTypeName,
        string DeadMoveId,
        string ReviveMoveId);

    private static readonly Dictionary<string, RevivePowerRegistration> RevivePowerTable = new(StringComparer.Ordinal);

    /// <summary>
    /// 登记第三方 Power 的「死亡后保留尸体、稍后复活」语义（原版 <c>ReattachPower</c> 的第三方对应物）。
    /// </summary>
    /// <remarks>
    /// 原版这条语义在核心里按类型写死在四处：<c>ICombatPredictionCreatureSemantics.ShouldRemoveAfterDeath</c>
    /// （保留尸体）、<c>DeathPowerSupport.Trigger</c>（开始复活阶段 + 强制走「死亡回合」）、
    /// <c>SimulatedCombatState.ResolveReviveMove</c>（复活回合治疗并回到正常回合）、
    /// <c>RevivingEnemyHp</c>（复活中的尸体按待复活生命计入终局口径）。第三方类型落在写死的名单外，
    /// 结果是**死了就直接判赢**、或者**卡在死亡回合永远不回来**——数值不报错，但整场结论是错的。
    ///
    /// <para>
    /// 契约（只登记逐行核对过与 <c>ReattachPower</c> 同形的 Power）：**同侧队友里带同一个 Power 的个体**构成
    /// 一组；组里还有别人活着时，死者保留尸体并强制走 <paramref name="DeadMoveId"/>；之后
    /// <paramref name="ReviveMoveId"/> 那一回合若组里仍有活人，就治疗 <c>Amount</c> 点并复活，
    /// 否则保持死亡。组里最后一个也死了时，全组标记为永久死亡（战斗可以结束）。
    /// </para>
    ///
    /// <para>
    /// 登记方要自己核对：Power 的 <c>ShouldPowerBeRemovedAfterOwnerDeath()</c> 必须返回 <c>false</c>
    /// （否则尸体上的复活 Power 会被清掉），<c>ShouldAllowHitting</c> 的重写语义要与「复活中不可被打」
    /// 一致（核心按死亡阶段判，不看那个重写），以及死亡回合／复活回合的行动 Id 与源码一致。
    /// </para>
    /// </remarks>
    public static void RegisterRevivePower(string powerTypeName, string deadMoveId, string reviveMoveId)
    {
        ArgumentException.ThrowIfNullOrEmpty(powerTypeName);
        ArgumentException.ThrowIfNullOrEmpty(deadMoveId);
        ArgumentException.ThrowIfNullOrEmpty(reviveMoveId);
        if (RevivePowerTable.ContainsKey(powerTypeName))
            throw new InvalidOperationException($"{powerTypeName} 已经登记过复活语义了。");
        RevivePowerTable.Add(powerTypeName, new RevivePowerRegistration(powerTypeName, deadMoveId, reviveMoveId));
    }

    public static bool TryGetRevivePower(string powerTypeName, out RevivePowerRegistration registration)
        => RevivePowerTable.TryGetValue(powerTypeName, out registration);

    /// <summary>这个 Power 类型名是否登记过「死亡后保留尸体并复活」。</summary>
    public static bool IsRevivePowerName(string powerTypeName)
        => RevivePowerTable.ContainsKey(powerTypeName);

    /// <summary>
    /// 第三方重生 Power 在「重生回合」要做的全部事情（换最大生命、治疗、摘掉哪些 Power 等）。
    /// </summary>
    /// <remarks>
    /// 处理函数只读写**模拟状态**：它跑在死亡阶段里，此时该个体已经 0 血、<c>_deathPhases</c> 是
    /// <c>Reviving</c>；返回后核心把死亡阶段清成 <c>None</c>（也就是「活过来了」），所以处理函数
    /// **不需要**自己碰死亡阶段。
    /// </remarks>
    public delegate void RespawnResolveHandler(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        PowerModel power);

    /// <summary>
    /// 一条「第三方重生 Power」的登记项：持有者死亡时保留尸体，<paramref name="RespawnMoveId"/> 那一回合
    /// 由 <paramref name="Resolve"/> 完成重生；<paramref name="PendingHpMemberName"/> 是「复活后会以多少
    /// 生命回来」的怪物静态数值成员名（终局口径要用）。
    /// </summary>
    public readonly record struct RespawnPowerRegistration(
        string PowerTypeName,
        string RespawnMoveId,
        string PendingHpMemberName,
        RespawnResolveHandler Resolve);

    private static readonly Dictionary<string, RespawnPowerRegistration> RespawnPowerTable = new(StringComparer.Ordinal);

    /// <summary>
    /// 登记第三方 Power 的「死亡后重生到新阶段」语义（原版 <c>AdaptablePower</c>／测试体那一型的
    /// 第三方对应物；往昔之章的觉醒者是「一阶段死亡 ⇒ REBIRTH 换成 300／320 血复活」）。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="RegisterRevivePower"/> 的区别是**形状**：那条是「同一组里只要还有人活着就治疗
    /// <c>Amount</c> 点回来」（Reattach 型，需要死亡回合 + 复活回合两个行动），这条是「死亡后必走
    /// 一个指定行动，那个行动里自己换血复活」（Adaptable 型，一个行动就够，没有分组判据）。
    ///
    /// <para>
    /// 登记后核心会替这个 Power 回答四件事（都**不再**调模型自己的重写，因为那几个重写读的是实机状态）：
    /// ① <c>ShouldRemoveAfterDeath</c> ⇒ 保留尸体；② 死亡时进入复活阶段并强制走
    /// <paramref name="RespawnMoveId"/>；③ 那个行动执行时调 <paramref name="Resolve"/> 并清掉死亡阶段；
    /// ④ 复活中的尸体按 <paramref name="PendingHpMemberName"/> 的数值计入终局口径（战斗不能算赢）。
    /// 另外**只要这个 Power 还在（层数大于 0），战斗就不能结束**（<c>ShouldStopCombatFromEnding</c>
    /// 的等价物）——所以重生回合里必须把<strong>这个 Power 自己</strong>摘掉，否则战斗永远结束不了。
    /// </para>
    ///
    /// <para>
    /// 登记方要自己核对：Power 的 <c>ShouldPowerBeRemovedAfterOwnerDeath()</c> 必须返回 <c>false</c>、
    /// <c>ShouldOwnerDeathTriggerFatal()</c> 必须是「这一阶段不算真死」（核心替你回答为 <c>false</c>），
    /// 以及行动 Id 与源码一致。
    /// </para>
    /// </remarks>
    public static void RegisterRespawnPower(
        string powerTypeName,
        string respawnMoveId,
        string pendingHpMemberName,
        RespawnResolveHandler resolve)
    {
        ArgumentException.ThrowIfNullOrEmpty(powerTypeName);
        ArgumentException.ThrowIfNullOrEmpty(respawnMoveId);
        ArgumentException.ThrowIfNullOrEmpty(pendingHpMemberName);
        ArgumentNullException.ThrowIfNull(resolve);
        if (RespawnPowerTable.ContainsKey(powerTypeName))
            throw new InvalidOperationException($"{powerTypeName} 已经登记过重生语义了。");
        RespawnPowerTable.Add(
            powerTypeName,
            new RespawnPowerRegistration(powerTypeName, respawnMoveId, pendingHpMemberName, resolve));
    }

    public static bool TryGetRespawnPower(string powerTypeName, out RespawnPowerRegistration registration)
        => RespawnPowerTable.TryGetValue(powerTypeName, out registration);

    /// <summary>这个 Power 类型名是否登记过「死亡后重生到新阶段」。</summary>
    public static bool IsRespawnPowerName(string powerTypeName)
        => RespawnPowerTable.ContainsKey(powerTypeName);

    /// <summary>
    /// 登记第三方**非 Power 模型**（怪物这类）的常规（非 Late）<c>AfterSideTurnEnd</c>：敌人回合末按类型名派发。
    /// </summary>
    /// <remarks>
    /// 纯新增：没登记的类型与加这个入口之前完全一样。派发点在敌人侧回合末的既有链路里、
    /// 晚期 <c>AfterSideTurnEndLate</c> 之前（与源码的「常规在前、晚期在后」一致）。
    /// </remarks>
    public static void RegisterSideTurnEndModel(string modelTypeName, SideTurnEndModelHandler handler)
        => SideTurnEndModelTable[modelTypeName] = handler;

    public static bool TryGetSideTurnEndModel(
        string modelTypeName,
        out SideTurnEndModelHandler handler)
        => SideTurnEndModelTable.TryGetValue(modelTypeName, out handler!);

    /// <summary>
    /// 登记第三方怪物某个行动的「攻击结算之后」实现（拿得到这次行动的全部伤害结果）。
    /// </summary>
    /// <remarks>
    /// 派发点在 <c>MonsterMoveSemantics.ApplyForecastMove</c> 的攻击循环之后、行动效果之前；
    /// 只有这次行动真的打出了命中时才会派发（结果是空列表时与没登记一样，不做任何事）。
    /// </remarks>
    public static void RegisterMonsterMoveAttackResults(
        string monsterTypeName,
        string moveId,
        MonsterMoveAttackResultHandler handler)
        => MoveAttackResultTable.Add((monsterTypeName, moveId), handler);

    public static bool TryGetMonsterMoveAttackResults(
        string monsterTypeName,
        string moveId,
        out MonsterMoveAttackResultHandler? handler)
        => MoveAttackResultTable.TryGetValue((monsterTypeName, moveId), out handler);

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
    /// 登记某个第三方行动在**攻击之前**要做的部分。
    /// </summary>
    /// <remarks>
    /// 求解器按 <c>攻击 → 行动效果</c> 的顺序结算一个行动；源码里先加格挡、先上状态再攻击的行动
    /// （往昔之章球状守卫的 <c>HARDEN</c>：先 15 格挡再打）必须登记在这里，否则格挡会晚一拍，
    /// 与「挨打时反伤」这类效果交互起来结果不同。
    /// </remarks>
    public static void RegisterMonsterMoveBeforeAttack(
        string monsterTypeName,
        string moveId,
        MonsterBeforeAttackHandler handler)
        => MoveBeforeAttackTable.Add((monsterTypeName, moveId), handler);

    public static bool HasMoveBeforeAttack(string monsterTypeName, string moveId)
        => MoveBeforeAttackTable.ContainsKey((monsterTypeName, moveId));

    /// <summary>
    /// 按 (怪物类型, 行动 Id) 派发第三方行动的**攻击前**部分。返回是否命中登记。
    /// </summary>
    public static bool TryApplyMoveBeforeAttack(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player)
    {
        MonsterModel? monster = move.Owner.Monster;
        if (monster is null)
            return false;
        if (!MoveBeforeAttackTable.TryGetValue(
                (monster.GetType().Name, move.Move.Id),
                out MonsterBeforeAttackHandler? handler))
        {
            return false;
        }
        handler(simulator, combat, move, player);
        return true;
    }

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

    // === 怪物攻击意图：每次出手都要现算的行动 ===

    /// <summary>
    /// 某条第三方攻击行动在当前分支下的伤害与段数。
    /// </summary>
    /// <remarks>
    /// 只能读**模拟状态**（<paramref name="combat"/> 的怪物标量状态、玩家/生物血量等都从它取），
    /// 不得读实机字段：搜索里每个分支都是实机模型的一份共享引用。
    /// </remarks>
    public delegate BranchMonsterAttack MonsterAttackValueResolver(
        SimulatedCombatState combat,
        MonsterModel monster);

    /// <summary>
    /// 声明某条第三方攻击行动的伤害或段数**会随战斗推进变化**，由求解器每次出手前现算。
    /// </summary>
    /// <remarks>
    /// 根捕获时冻结的静态攻击值对这类行动是错的：往昔之书的多重刺击段数是
    /// <c>DynamicMultiAttackIntent(() =&gt; StabDamage, () =&gt; StabCount)</c>，而 <c>StabCount</c>
    /// 每回合都被分支选择函数 +1（第 1 回合 3 段、第 2 回合 4 段……），冻结值会让整场都按捕获那一刻算。
    /// 登记之后，搜索里这条行动的每一段都按**当前分支**的模拟状态重算；没登记的行动仍走冻结值
    /// （见 <see cref="RegisterStableAttack"/> 对另一类形状的说明）。
    ///
    /// <para>
    /// 与 <see cref="RegisterStableAttack"/> **互斥**：同一条行动不能既声明「数值在构造时固定」又声明
    /// 「每次出手现算」。两种声明混在一起时执行侧按动态值走、界面侧却被固定声明压掉「动态伤害」提示，
    /// 结果是一条自相矛盾的登记；这里当场失败，而不是让它带着矛盾跑。
    /// </para>
    /// </remarks>
    public static void RegisterMonsterAttackValues(
        string monsterTypeName,
        string moveId,
        MonsterAttackValueResolver resolver)
    {
        if (StableAttackTable.Contains((monsterTypeName, moveId)))
        {
            throw new InvalidOperationException(
                $"{monsterTypeName}.{moveId} 已登记为「数值在构造时固定」的攻击（RegisterStableAttack），" +
                "不能再登记动态攻击值；两种声明互斥。");
        }
        DynamicAttackTable.Add((monsterTypeName, moveId), resolver);
    }

    public static bool TryGetMonsterAttackValues(
        string monsterTypeName,
        string moveId,
        out MonsterAttackValueResolver? resolver)
        => DynamicAttackTable.TryGetValue((monsterTypeName, moveId), out resolver);

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
    {
        if (DynamicAttackTable.ContainsKey((monsterTypeName, moveId)))
        {
            throw new InvalidOperationException(
                $"{monsterTypeName}.{moveId} 已登记了动态攻击值（RegisterMonsterAttackValues），" +
                "不能再声明为固定攻击；两种声明互斥。");
        }
        StableAttackTable.Add((monsterTypeName, moveId));
    }

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
    /// 两类都算：逃跑／脱战，以及「先杀掉自己再留下生成物」的自杀式行动（史莱姆分裂就是这样）。
    /// 两侧都要这个事实，而且用的是两个不同的问题：行动效果侧在回放里把怪物移出 roster
    /// （逃跑由登记方调用 <c>CreatureEscaped</c>，自杀由 <c>killedOwner</c> 走死亡结算），
    /// 意图预测侧则在往后推算回合时让这个个体退出，不再给它排后续行动。只登记一半的话，
    /// 玩家会在它已经离场之后还看到一条不存在的意图。
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
        CombatPredictionSimulator simulator,
        out string resolved)
    {
        resolved = string.Empty;
        if (!BranchResolverTable.TryGetValue(
                (monster.GetType().Name, branchId),
                out MonsterBranchResolver? resolver))
        {
            return false;
        }
        resolved = resolver(monster, branchId, stateLog, rng, combat, simulator);
        if (string.IsNullOrEmpty(resolved))
        {
            throw new PredictionUnsupportedException(
                $"第三方怪物 {monster.GetType().FullName} 的分支状态 {branchId} 的登记实现返回了空行动 Id。");
        }
        return true;
    }
}
