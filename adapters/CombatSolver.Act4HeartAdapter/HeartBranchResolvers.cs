using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Random;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver.Act4HeartAdapter;

/// <summary>
/// Act4Heart 三个怪物 <c>POST_ATTACK_BRANCH</c> 的解析，以及它们那条 <c>private byte state</c> 的口径。
/// </summary>
/// <remarks>
/// 三个怪物的状态机形状相同：一个起始行动 → 攻击 → <c>POST_ATTACK_BRANCH</c> → 随机分支，
/// 其中 <c>POST_ATTACK_BRANCH</c> 按一个**可变的 <c>byte state</c>** 决定下一步去哪。
/// 求解器在根捕获时会把条件分支的取值冻结成一份快照，而那个值是按<b>捕获那一刻</b>的 <c>state</c>
/// 算出来的，之后每个分支推进 <c>state</c> 都不会改变它——直接用冻结值会从第一步就错。
/// 所以这里把 <c>state</c> 登记成怪物标量状态（随分支 Fork、进状态指纹），
/// 再由本文件的解析函数按当前值判断，与源码里那三个 lambda 逐条对应。
///
/// <para>
/// 源码里的三段条件都是「依次短路」：
/// </para>
/// <list type="number">
/// <item><description>腐化心脏：<c>(state &amp; 1) == 0</c> → 血弹；否则 <c>(state &amp; 2) == 0</c> → 回响；否则 <c>(state &amp; 3) == 3</c> → 强化。</description></item>
/// <item><description>盾兵：<c>(state &amp; 1) == 0</c> → 猛击；否则 <c>(state &amp; 2) == 0</c> → 加固；否则 <c>(state &amp; 3) == 3</c> → 猛砸。</description></item>
/// <item><description>矛兵：<c>(state &amp; 1) == 0</c> → 灼烧打击；否则 <c>(state &amp; 2) == 0</c> → 穿刺；否则 <c>(state &amp; 3) == 3</c> → 串刺。</description></item>
/// </list>
/// 三个都不命中时实机的 <c>ConditionalBranchState</c> 会抛「找不到下一个状态」；这里同样明确抛出，
/// 而不是猜一个——<c>state</c> 只被本适配写进 0/1/2/3，落在这里说明两边已经对不上。
///
/// <para>
/// 这三条分支都**不抽 RNG**，所以解析函数一律不碰 <c>rng</c>：少抽一次会让之后所有回合的
/// 抽样整体错位。
/// </para>
/// </remarks>
internal static class HeartBranchResolvers
{
    /// <summary>三个怪物身上那个可变的 <c>private byte state</c>。</summary>
    internal const string StateMember = "state";

    /// <summary>腐化心脏的强化计数器 <c>private int buff_counter</c>。</summary>
    internal const string BuffCounterMember = "buff_counter";

    /// <summary>
    /// 适配层自己往怪物标量状态里塞的一项：盾兵那条私有 RNG 流已经抽过几次。
    /// </summary>
    /// <remarks>
    /// 这个名字**不在** <see cref="RegisterMonsterStateMembers"/> 的名单里——实机怪物身上并没有这个成员，
    /// 登记进名单会让根捕获去读一个不存在的字段。它只在预测过程中由适配层写入，
    /// 一样会进状态指纹（指纹遍历的是整张标量状态表）。
    /// </remarks>
    internal const string ShieldOrbRollsMember = "adapter_spire_shield_orb_rolls";

    internal const string PostAttackBranch = "POST_ATTACK_BRANCH";

    private const string CorruptHeart = "CorruptHeart";
    private const string SpireShield = "SpireShield";
    private const string SpireSpear = "SpireSpear";

    /// <summary>本适配接管 <c>POST_ATTACK_BRANCH</c> 的怪物类型；自检与登记共用这一份名单。</summary>
    internal static readonly string[] RegisteredMonsterTypes = [CorruptHeart, SpireShield, SpireSpear];

    /// <summary>
    /// 每个怪物的 <c>POST_ATTACK_BRANCH</c> 里，那个可变 <c>byte state</c> 之外还需要一起播种的标量成员。
    /// </summary>
    private static readonly Dictionary<string, string[]> StateMembersByType = new(StringComparer.Ordinal)
    {
        [CorruptHeart] = [StateMember, BuffCounterMember],
        [SpireShield] = [StateMember],
        [SpireSpear] = [StateMember],
    };

    /// <summary>初始化自检：要接管的怪物类型、以及依靠的私有成员都还在。</summary>
    public static void Verify()
    {
        foreach (string typeName in RegisteredMonsterTypes)
        {
            Type monster = A4hReflection.RequireMonsterType(typeName);
            foreach (string member in StateMembersByType[typeName])
                A4hReflection.RequireInstanceMember(monster, member);
        }
    }

    public static void RegisterAll()
    {
        foreach (string typeName in RegisteredMonsterTypes)
        {
            // state 与 buff_counter 都是「随战斗推进变化、且分支/效果依赖」的标量：根捕获时按实机值播种，
            // 之后在模拟里推进，并进状态指纹与续用核对文本。
            ThirdPartyAdapterRegistry.RegisterMonsterStateMembers(typeName, StateMembersByType[typeName]);
            ThirdPartyAdapterRegistry.RegisterMonsterBranchResolver(
                typeName,
                PostAttackBranch,
                ResolverFor(typeName));
        }
    }

    private static ThirdPartyAdapterRegistry.MonsterBranchResolver ResolverFor(string monsterTypeName)
        => monsterTypeName switch
        {
            CorruptHeart => ResolveCorruptHeart,
            SpireShield => ResolveSpireShield,
            SpireSpear => ResolveSpireSpear,
            _ => throw new InvalidOperationException($"{monsterTypeName} 没有注册 POST_ATTACK_BRANCH 解析器。"),
        };

    // === 逐条对照 Act4Heart 源码里的 lambda ===

    private static string ResolveCorruptHeart(
        MonsterModel monster,
        string branchId,
        IReadOnlyList<string> stateLog,
        Rng rng,
        SimulatedCombatState combat,
        CombatPredictionSimulator simulator)
    {
        int state = combat.GetMonsterInt(monster.Creature, StateMember);
        if ((state & 1) == 0)
            return HeartMoveEffects.BloodShotsMove;
        if ((state & 2) == 0)
            return HeartMoveEffects.EchoMove;
        if ((state & 3) == 3)
            return HeartMoveEffects.BuffMove;
        return Unreachable(CorruptHeart, branchId, state);
    }

    private static string ResolveSpireShield(
        MonsterModel monster,
        string branchId,
        IReadOnlyList<string> stateLog,
        Rng rng,
        SimulatedCombatState combat,
        CombatPredictionSimulator simulator)
    {
        int state = combat.GetMonsterInt(monster.Creature, StateMember);
        if ((state & 1) == 0)
            return HeartMoveEffects.BashMove;
        if ((state & 2) == 0)
            return HeartMoveEffects.FortifyMove;
        if ((state & 3) == 3)
            return HeartMoveEffects.SmashMove;
        return Unreachable(SpireShield, branchId, state);
    }

    private static string ResolveSpireSpear(
        MonsterModel monster,
        string branchId,
        IReadOnlyList<string> stateLog,
        Rng rng,
        SimulatedCombatState combat,
        CombatPredictionSimulator simulator)
    {
        int state = combat.GetMonsterInt(monster.Creature, StateMember);
        if ((state & 1) == 0)
            return HeartMoveEffects.BurnStrikeMove;
        if ((state & 2) == 0)
            return HeartMoveEffects.PiercerMove;
        if ((state & 3) == 3)
            return HeartMoveEffects.SkewerMove;
        return Unreachable(SpireSpear, branchId, state);
    }

    private static string Unreachable(string monsterType, string branchId, int state)
        => throw new PredictionUnsupportedException(
            $"{monsterType}.{branchId} 的 state={state} 三个条件都不命中；"
            + "这对应实机 ConditionalBranchState 的「找不到下一个状态」，说明适配的 state 口径已经对不上。");
}
