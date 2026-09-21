using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

/// <summary>
/// 第三方 Mod 自定义 <see cref="MonsterState"/> 子类型（分支状态）的解析。
/// </summary>
/// <remarks>
/// 原版只有三种行动状态：<see cref="MoveState"/>、<see cref="RandomBranchState"/> 和
/// <see cref="ConditionalBranchState"/>，求解器分别按「落到行动节点」「按权重抽分支」
/// 「按冻结的根选择」处理。第三方 Mod 可以注册第四种：例如 Acts from the Past 的
/// <c>ActsFromThePast.ConditionalBranchState</c>，它把一个
/// <c>Func&lt;Creature, Rng, MonsterMoveStateMachine, string&gt;</c> 委托当作分支函数。
///
/// <para>**为什么不直接调用那个委托。** 委托是在实机模型上运行的：它读 <c>owner.Monster</c> 的私有字段
/// （<c>_isOpen</c>、<c>_orbActiveCount</c>、<c>_mugCount</c>……）和 *实机* 状态机的
/// <c>StateLog</c>。搜索里的每个分支都是实机模型的一份共享引用，实机字段既不随分支 Fork，
/// 实机 <c>StateLog</c> 也不等于当前预测分支的行动历史，所以直接调用会让所有分支读到同一份
/// 陈旧值，而且会白白吃掉一次 RNG。求解器对原版怪物的既有做法是把这些标量镜像进
/// <see cref="SimulatedCombatState"/> 的模拟状态，再用模拟状态重算选择；第三方适配 Mod 沿用
/// 同一条路，通过 <see cref="ThirdPartyAdapterRegistry.RegisterMonsterBranchResolver"/> 登记。</para>
///
/// <para>**RNG 对齐。** 委托用的 <c>rng</c> 就是 <c>MonsterAi</c> 流，求解器在
/// <c>Advance</c>/<c>RollInitial</c> 里传的也是这一条，所以登记的实现在同一条件下必须抽
/// 同样次数的 <c>NextInt</c>/<c>NextFloat</c>：连「某条分支不抽 RNG」这一点也要保持，
/// 否则后续回合的抽样会整体错位。</para>
///
/// <para>**失败语义。** 没有登记的（怪物类型, 分支 Id）一律抛
/// <see cref="PredictionUnsupportedException"/>，不退化、不近似。只有把分支依赖的全部状态
/// 都镜像进模拟状态、并且该分支引用的效果也都建模之后，才允许登记；
/// 装一半比不装更糟（见 docs/THIRD_PARTY_ADAPTERS.md §3.2）。</para>
/// </remarks>
internal static class ThirdPartyMonsterBranches
{
    private const string BaseStateNamespace = "MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine";

    /// <summary>
    /// 是否是求解器不认识的第三方分支状态。原版三种状态与基类命名空间内的类型都返回 false，
    /// 交给既有的分支处理；它们仍走原来的失败路径。
    /// </summary>
    public static bool IsForeignBranchState(MonsterState state)
    {
        Type type = state.GetType();
        // 原版三种状态都在基类命名空间内；将来原版新增的状态也一并交给既有分支逻辑，
        // 保持「不认识的第三方状态才走这里」这一条边界。
        if (string.Equals(type.Namespace, BaseStateNamespace, StringComparison.Ordinal))
            return false;
        _ = AssemblyInfo.ModForType(type, out bool isBaseGame);
        return !isBaseGame;
    }

    public static string Resolve(
        MonsterState branch,
        BranchMonsterAiState source,
        SimulatedCombatState combat,
        CombatPredictionSimulator simulator)
    {
        MonsterModel monster = source.Monster;
        if (ThirdPartyAdapterRegistry.TryResolveBranch(
                monster,
                branch.Id,
                source.StateLog,
                simulator.Rng.MonsterAi,
                combat,
                simulator,
                out string resolved))
        {
            return resolved;
        }
        throw new PredictionUnsupportedException(
            $"第三方怪物 {monster.GetType().FullName} 的分支状态 {branch.Id} 还没有预测实现。");
    }
}
