using System.Reflection;

using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Random;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

/// <summary>
/// 怪物自己那条 RNG 流（<c>MonsterModel.Rng</c>）在预测里的分支副本。
/// </summary>
/// <remarks>
/// <para>
/// 这条流由 <c>CombatState.CreateCreature</c> 在战斗开始时按「run 种子 + 地图坐标 + CombatId」播种，
/// **每只怪物各自一条**，与 <c>RunRng.MonsterAi</c> 互不影响。求解器不模拟它：原版只拿它做外观，
/// 但第三方 Mod 会把它用在**行动效果**里（往昔之章小鬼盾兵的 <c>Protect</c> 用它抽格挡目标、
/// 心脏盾兵的球位分支用它抽几率），抽样次数会改变之后每一次抽样，所以必须照源码的顺序复刻。
/// </para>
/// <para>
/// 第一次取用时把**实机实例**上的流整份拷一份，Fork 时再整份拷走，此后与实机彻底脱钩。
/// 实机实例上的当前状态就是「本场战斗到目前为止抽过几次」的准确记录——前提是该流在战斗里
/// **只被这只怪物自己的行动推动**（适配方要逐行复核这一点），预测本身只读不写。
/// </para>
/// <para>
/// <see cref="Draws"/> 应当由适配方另外写进一个**自建的**怪物标量成员（例如
/// <c>adapter_xxx_rng_draws</c>，名字不要进 <c>RegisterMonsterStateMembers</c>——实机怪物身上没有这个
/// 成员，登记进名单会让根捕获去读一个不存在的字段）。指纹遍历的是整张标量状态表，于是
/// 「只差这条流进度」的两条分支不会再被去重成一条。
/// </para>
/// </remarks>
internal sealed class MonsterRngPredictionState : IPredictionStateForkable
{
    private readonly Rng _rng;

    public MonsterRngPredictionState(MonsterModel monster)
    {
        ArgumentNullException.ThrowIfNull(monster);
        Rng? live = monster.Rng;
        if (live is null)
            throw new PredictionUnsupportedException($"怪物 {monster.Id.Entry} 没有私有 RNG 流，无法复刻它的抽样。");
        _rng = live.CaptureState().ToRng();
    }

    private MonsterRngPredictionState(Rng rng, int draws)
    {
        _rng = rng;
        Draws = draws;
    }

    /// <summary>已经抽过几次——对应源码里这条流被推动的次数。</summary>
    public int Draws { get; private set; }

    /// <summary>源码里的 <c>Rng.NextInt(...)</c>：先推进，再取值。</summary>
    public int NextInt(int minInclusive, int maxExclusive)
    {
        Draws++;
        return _rng.NextInt(minInclusive, maxExclusive);
    }

    /// <summary>源码里的 <c>Rng.NextFloat(1f)</c>：先推进，再取值。</summary>
    public float NextFloat()
    {
        Draws++;
        return _rng.NextFloat(1f);
    }

    /// <summary>
    /// 复刻 <c>Rng.NextItem</c>：**空集合一次都不抽**，否则抽一次 <c>NextInt(0, count)</c>。
    /// </summary>
    /// <remarks>
    /// 调用方必须按源码的短路顺序先判「有没有候选」——「不抽」和「抽了但没走那条路」是两种不同的状态。
    /// </remarks>
    public T? NextItem<T>(IReadOnlyList<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
            return default;
        return items[NextInt(0, items.Count)];
    }

    public object Fork(PredictionForkContext context)
    {
        _ = context;
        return new MonsterRngPredictionState(_rng.CaptureState().ToRng(), Draws);
    }
}

/// <summary>
/// 第三方适配 Mod 取用怪物私有 RNG 流预测状态的入口。
/// </summary>
internal static class MonsterRngSupport
{
    /// <summary>
    /// 取这只怪物那条私有 RNG 流在当前分支上的副本；还没有就地按实机状态建一份。
    /// </summary>
    public static MonsterRngPredictionState State(CombatPredictionSimulator simulator, MonsterModel monster)
        => simulator.StateStore.Get(monster, static model => new MonsterRngPredictionState(model));

    /// <summary>
    /// 自检这条流依赖的公开访问点还在（<c>MonsterModel.Rng</c> 属性）。适配层在初始化时调用。
    /// </summary>
    public static void VerifyShape()
    {
        PropertyInfo? property = typeof(MonsterModel).GetProperty(
            nameof(MonsterModel.Rng),
            BindingFlags.Instance | BindingFlags.Public);
        if (property is null || property.PropertyType != typeof(Rng))
        {
            throw new InvalidOperationException(
                "MonsterModel.Rng 不再是公开的 Rng 属性，私有 RNG 镜像需要重新核对。");
        }
    }
}
