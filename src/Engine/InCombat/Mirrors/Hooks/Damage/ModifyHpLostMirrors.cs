using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.Common.Mirrors;

namespace CombatSolver.Engine.InCombat.Mirrors.Hooks.Damage;

using Registry = MethodMirrorRegistry<AbstractModel, ModifyHpLostMirrorContext, decimal>;

/// <summary>
/// Mirrors the four phase-specific listener passes inside <see cref="Hook.ModifyHpLost"/>.
/// </summary>
internal static class ModifyHpLostMirrors
{
    private static readonly Type[] ModifyHpLostParamTypes =
    [
        typeof(Creature),
        typeof(decimal),
        typeof(ValueProp),
        typeof(Creature),
        typeof(CardModel)
    ];

    private static readonly MirrorMethodSpec ModifyHpLostBeforeOsty = MirrorMethodSpec.Hook(
        nameof(AbstractModel.ModifyHpLostBeforeOsty),
        ModifyHpLostParamTypes);

    private static readonly MirrorMethodSpec ModifyHpLostBeforeOstyLate = MirrorMethodSpec.Hook(
        nameof(AbstractModel.ModifyHpLostBeforeOstyLate),
        ModifyHpLostParamTypes);

    private static readonly MirrorMethodSpec ModifyHpLostAfterOsty = MirrorMethodSpec.Hook(
        nameof(AbstractModel.ModifyHpLostAfterOsty),
        ModifyHpLostParamTypes);

    private static readonly MirrorMethodSpec ModifyHpLostAfterOstyLate = MirrorMethodSpec.Hook(
        nameof(AbstractModel.ModifyHpLostAfterOstyLate),
        ModifyHpLostParamTypes);

    private static readonly Registry BeforeOstyRegistry = new(ModifyHpLostBeforeOsty);
    private static readonly Registry BeforeOstyLateRegistry = CreateBeforeOstyLateRegistry();
    private static readonly Registry AfterOstyRegistry = CreateAfterOstyRegistry();
    private static readonly Registry AfterOstyLateRegistry = CreateAfterOstyLateRegistry();
    private static readonly object RegistrationLock = new();
    private static bool _sealed;

    /// <summary>
    /// 第三方适配 Mod 按运行时类型登记 <see cref="AbstractModel.ModifyHpLostAfterOstyLate"/> 的预测实现。
    /// </summary>
    /// <remarks>
    /// 这一阶段最常见的就是「每回合最多掉多少血」的上限（原版硬化外壳、第三方 Invincible）。
    /// 不登记时调用方会回落到实机重写，而实机重写读的是它自己的隐藏计数——克隆之后是初值，
    /// 于是上限看起来永远没被消耗过，预测会高估自己的输出。登记须在任何根捕获或首次分发之前完成。
    /// </remarks>
    public static void RegisterAfterOstyLate(
        Type modelType,
        Func<AbstractModel, ModifyHpLostMirrorContext, decimal> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (modelType.IsAbstract)
            throw new ArgumentException("血量损失镜像需要具体运行时类型。", nameof(modelType));
        lock (RegistrationLock)
        {
            if (_sealed)
                throw new InvalidOperationException("ModifyHpLostAfterOstyLate 镜像必须在根捕获或首次分发之前登记。");
            ThirdPartyMirrorRegistration.RegisterResult(AfterOstyLateRegistry, modelType, handler);
        }
    }

    private static void Seal()
    {
        if (Volatile.Read(ref _sealed))
            return;
        lock (RegistrationLock)
            Volatile.Write(ref _sealed, true);
    }

    public static decimal InvokeBeforeOsty(AbstractModel listener, ModifyHpLostMirrorContext context)
    {
        return BeforeOstyRegistry.TryInvokeRegistered(listener, context, out var result)
            ? result.Value
            : InvokeOriginalBeforeOsty(listener, context);
    }

    public static decimal InvokeBeforeOstyLate(AbstractModel listener, ModifyHpLostMirrorContext context)
    {
        return BeforeOstyLateRegistry.TryInvokeRegistered(listener, context, out var result)
            ? result.Value
            : InvokeOriginalBeforeOstyLate(listener, context);
    }

    public static decimal InvokeAfterOsty(AbstractModel listener, ModifyHpLostMirrorContext context)
    {
        return AfterOstyRegistry.TryInvokeRegistered(listener, context, out var result)
            ? result.Value
            : InvokeOriginalAfterOsty(listener, context);
    }

    public static decimal InvokeAfterOstyLate(AbstractModel listener, ModifyHpLostMirrorContext context)
    {
        Seal();
        return AfterOstyLateRegistry.TryInvokeRegistered(listener, context, out var result)
            ? result.Value
            : InvokeOriginalAfterOstyLate(listener, context);
    }

    private static decimal InvokeOriginalBeforeOsty(
        AbstractModel listener,
        ModifyHpLostMirrorContext context)
    {
        return listener.ModifyHpLostBeforeOsty(
            context.Target,
            context.Amount,
            context.Props,
            context.Dealer,
            context.CardSource?.Preview);
    }

    private static decimal InvokeOriginalBeforeOstyLate(
        AbstractModel listener,
        ModifyHpLostMirrorContext context)
    {
        return listener.ModifyHpLostBeforeOstyLate(
            context.Target,
            context.Amount,
            context.Props,
            context.Dealer,
            context.CardSource?.Preview);
    }

    private static decimal InvokeOriginalAfterOsty(
        AbstractModel listener,
        ModifyHpLostMirrorContext context)
    {
        return listener.ModifyHpLostAfterOsty(
            context.Target,
            context.Amount,
            context.Props,
            context.Dealer,
            context.CardSource?.Preview);
    }

    private static decimal InvokeOriginalAfterOstyLate(
        AbstractModel listener,
        ModifyHpLostMirrorContext context)
    {
        return listener.ModifyHpLostAfterOstyLate(
            context.Target,
            context.Amount,
            context.Props,
            context.Dealer,
            context.CardSource?.Preview);
    }

    private static Registry CreateBeforeOstyLateRegistry()
    {
        var registry = new Registry(ModifyHpLostBeforeOstyLate);

        registry.Register<HardenedShellPower>(HandleHardenedShellPower);

        return registry;
    }

    private static Registry CreateAfterOstyRegistry()
    {
        var registry = new Registry(ModifyHpLostAfterOsty);

        registry.Register<BeatingRemnant>(HandleBeatingRemnant);
        registry.Register<SlipperyPower>(HandleSlipperyPower);

        return registry;
    }

    private static Registry CreateAfterOstyLateRegistry()
    {
        var registry = new Registry(ModifyHpLostAfterOstyLate);

        registry.Register<BufferPower>(HandleBufferPower);

        return registry;
    }

    private static decimal HandleHardenedShellPower(
        HardenedShellPower power,
        ModifyHpLostMirrorContext context)
    {
        if (context.Target != power.Owner || context.Amount == 0m)
        {
            return context.Amount;
        }

        var state = context.StateStore.Get(power, () => new HardenedShellPredictionState(power));
        return Math.Min(context.Amount, power.Amount - state.DamageReceivedThisTurn);
    }

    private static decimal HandleBeatingRemnant(
        BeatingRemnant relic,
        ModifyHpLostMirrorContext context)
    {
        if (context.Target != relic.Owner.Creature)
        {
            return context.Amount;
        }

        var state = context.StateStore.Get(relic, () => new BeatingRemnantPredictionState(relic));
        var damageCap = relic.DynamicVars[BeatingRemnant._maxHpLossKey].BaseValue;
        return Math.Min(context.Amount, damageCap - state.DamageReceivedThisTurn);
    }

    private static decimal HandleSlipperyPower(
        SlipperyPower power,
        ModifyHpLostMirrorContext context)
    {
        return context.StateStore.GetPowerAmount(power).IsActive
            ? InvokeOriginalAfterOsty(power, context)
            : context.Amount;
    }

    private static decimal HandleBufferPower(
        BufferPower power,
        ModifyHpLostMirrorContext context)
    {
        return power.Amount > 0
            ? InvokeOriginalAfterOstyLate(power, context)
            : context.Amount;
    }
}

internal sealed class ModifyHpLostMirrorContext : CombatMirrorContext
{
    public required Creature Target { get; init; }

    public required decimal Amount { get; set; }

    public required ValueProp Props { get; init; }

    public required Creature? Dealer { get; init; }

    public required PredictedCard? CardSource { get; init; }
}
