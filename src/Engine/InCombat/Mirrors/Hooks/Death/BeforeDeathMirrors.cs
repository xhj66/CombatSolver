using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Powers;
using CombatSolver.Engine.Common.Mirrors;

namespace CombatSolver.Engine.InCombat.Mirrors.Hooks.Death;

using Registry = MethodMirrorRegistry<AbstractModel, BeforeDeathMirrorContext>;

// Mirrors the prediction-relevant parts of Hook.BeforeDeath.
internal static class BeforeDeathMirrors
{
    private static readonly MirrorMethodSpec BeforeDeath = MirrorMethodSpec.Hook(
        nameof(AbstractModel.BeforeDeath),
        [typeof(Creature)]);

    private static readonly Registry Registry = CreateRegistry();
    private static readonly object RegistrationLock = new();
    private static bool _sealed;

    /// <summary>
    /// 第三方适配 Mod 按运行时类型登记 <see cref="AbstractModel.BeforeDeath"/> 的预测实现。
    /// </summary>
    /// <remarks>
    /// 登记须在任何根捕获或首次分发之前完成，之后明确拒绝。未登记的第三方重写会在分发时记一条
    /// <c>MethodNotMirrored</c>；方法名含 «Death» 时那条风险会让整场战斗给不出战损
    /// （见 docs/THIRD_PARTY_ADAPTERS.md §2.13）。
    /// </remarks>
    public static void Register(Type modelType, Action<AbstractModel, BeforeDeathMirrorContext> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (modelType.IsAbstract)
            throw new ArgumentException("死亡前镜像需要具体运行时类型。", nameof(modelType));
        lock (RegistrationLock)
        {
            if (_sealed)
                throw new InvalidOperationException("BeforeDeath 镜像必须在根捕获或首次分发之前登记。");
            ThirdPartyMirrorRegistration.Register(Registry, modelType, handler);
        }
    }

    /// <summary>
    /// 登记一条人工复核过、对预测无影响的第三方 <see cref="AbstractModel.BeforeDeath"/> 重写。
    /// </summary>
    /// <remarks>
    /// 判据与原生那批 <c>RegisterIgnored&lt;T&gt;()</c> 相同：逐行反编译确认只有音效、动画、对白这类
    /// 表现层副作用。**不能**用它掩盖会改数值、状态或 RNG 的重写——那类必须提供真正的镜像。
    /// </remarks>
    public static void RegisterIgnored(Type modelType)
    {
        ArgumentNullException.ThrowIfNull(modelType);
        lock (RegistrationLock)
        {
            if (_sealed)
                throw new InvalidOperationException("BeforeDeath 镜像必须在根捕获或首次分发之前登记。");
            Registry.RegisterIgnored(modelType);
        }
    }

    public static void Invoke(AbstractModel listener, BeforeDeathMirrorContext context)
    {
        Seal();
        Registry.Invoke(listener, context);
    }

    private static void Seal()
    {
        if (Volatile.Read(ref _sealed))
            return;
        lock (RegistrationLock)
            Volatile.Write(ref _sealed, true);
    }

    private static Registry CreateRegistry()
    {
        var registry = new Registry(BeforeDeath);

        registry.RegisterIgnored<Crusher>();
        registry.RegisterIgnored<Rocket>();
        registry.RegisterIgnored<HeistPower>();
        registry.RegisterIgnored<SwipePower>();

        return registry;
    }
}

internal sealed class BeforeDeathMirrorContext : CombatMirrorContext
{
    public required Creature Creature { get; init; }
}
