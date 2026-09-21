using CombatSolver.Engine.Common;

namespace CombatSolver.Act4HeartAdapter;

/// <summary>
/// 把 Act4Heart 的 ModelHook 订阅者放进求解器的战斗订阅者放行名单。
/// </summary>
/// <remarks>
/// <para>
/// Act4Heart 通过 <c>Dolso.ModelHook</c>（一个 <c>AbstractModel</c>）用
/// <c>ModHelper.SubscribeForRunStateHooks</c> 订阅每局 hook。求解器的根捕获门禁会逐个审这些订阅者：
/// 只有「放行名单里」「模组声明 <c>affectsGameplay:false</c>」或「与战斗无关」三类能通过，
/// 其余一律让整个预测拒绝运行。
/// </para>
/// <para>
/// Act4Heart 的两个订阅者里，<c>RedKeyHooks</c> 只覆写休息处选项（战斗无关），但 <c>GreenKeyHooks</c>
/// 除了地图生成与战斗开始之外还覆写了 <c>TryModifyRewards</c>——它在求解器的「战斗无关」清单之外，
/// 于是会被判为参与战斗并直接拒绝。**没有这一步放行，装了 Act4Heart 就无法预测任何战斗。**
/// 我们逐条审过这两个类型的全部覆写（地图生成、战斗开始、休息处选项、战后奖励），没有一项落在
/// 战斗模拟期间，因此放行是安全的。
/// </para>
/// <para>
/// 放行是**按类型全名精确匹配**的，而且是「白名单」而非「通配」：Act4Heart 新增一个订阅者时，
/// <see cref="Verify"/> 会因为名单对不上而让整个适配拒绝登记——求解器于是照常拒绝这条不可预测的路线，
/// 而不是悄悄跳过新订阅者的效果。
/// </para>
/// </remarks>
internal static class HeartSubscribers
{
    /// <summary>
    /// 已逐条审过、可以放行的 ModelHook 订阅者（按 Ordinal 排序，与
    /// <see cref="A4hReflection.ModelHookSubclassNames"/> 的输出口径一致）。
    /// </summary>
    private static readonly string[] AuditedSubclasses =
    [
        "Act4Heart.Keys.GreenKeyHooks",
        "Act4Heart.Keys.RedKeyHooks",
    ];

    public static void Verify()
    {
        string[] actual = A4hReflection.ModelHookSubclassNames();
        if (actual.Length != AuditedSubclasses.Length
            || !actual.SequenceEqual(AuditedSubclasses, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "Act4Heart 的 ModelHook 订阅者集合与适配审过的名单不一致：实际 ["
                + string.Join(", ", actual) + "]，审过 ["
                + string.Join(", ", AuditedSubclasses) + "]；"
                + "新增的订阅者可能参与战斗，需要先审过再放行。");
        }
    }

    public static void RegisterAll()
    {
        foreach (string typeName in AuditedSubclasses)
            ThirdPartyAdapterRegistry.AllowCombatSubscriber(typeName);
    }
}
