using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Card;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Damage;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.TurnEnd;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver.Act4HeartAdapter;

/// <summary>
/// 心脏战斗里那几个第三方 Power 的预测实现。
/// </summary>
/// <remarks>
/// <para>
/// 心脏（<c>CorruptHeart</c>）在进场时就给自己挂上 <c>BeatOfDeathPower</c> 与 <c>InvinciblePower</c>
/// （见 <c>CorruptHeart.AfterAddedToRoom</c>），所以从第一个回合起它们就在场：
/// </para>
/// <list type="bullet">
/// <item><description><b>死亡节拍</b>：每打出一张牌，就让出牌者的生物掉 <c>Amount</c> 点血（<c>ValueProp</c> 4 = 无来源加成的伤害）。
/// 不镜像的话，预测会高估自己每一张牌的净收益。</description></item>
/// <item><description><b>无敌</b>：本回合从这个来源收到的伤害累计到 <c>Amount</c> 就封顶，之后的血量损失被截到 0。
/// 状态放在 <c>_internalData</c>，**克隆会重置**，所以必须根捕获，否则求解器会以为心脏能被一波打穿。</description></item>
/// </list>
/// <para>
/// <c>MetallicizePowerA4h</c> 不属于心脏战斗（它由钥匙子系统的绿钥匙精英使用），但它的镜像只有三行、
/// 且登记后严格更好，所以一并登记。<c>RegeneratePowerA4h</c> 走的是 <c>AfterSideTurnEnd</c>，
/// 求解器没有镜像这个阶段（只有 <c>AfterSideTurnEnd**Late**</c>），**无法建模**——见
/// <see cref="KnownGaps"/>。
/// </para>
/// </remarks>
internal static class HeartPowers
{
    // === 类型名（自检与登记共用同一份真相） ===

    internal const string BeatOfDeathType = "BeatOfDeathPower";
    internal const string InvincibleType = "InvinciblePower";
    internal const string MetallicizeType = "MetallicizePowerA4h";

    /// <summary>无敌的每回合累计伤害进状态指纹用的槽名。</summary>
    private const string InvincibleDamageSlot = "invincible_damage_received_this_turn";

    // === 自检：与 Act4Heart 源码逐项核对 ===

    /// <summary>
    /// 心脏战斗里唯一无法建模的第三方 Power。
    /// </summary>
    /// <remarks>
    /// <c>RegeneratePowerA4h.AfterSideTurnEnd</c> 每回合末给持有者回 <c>Amount</c> 点血。求解器只镜像
    /// <c>AfterSideTurnEndLate</c>，没有 <c>AfterSideTurnEnd</c> 这个阶段，也没有给第三方补阶段的入口。
    /// 名字写在这里是为了让下一个人一眼看到：它不是「漏了」，是**当前底座接不上**。
    /// 好在这个 Power 只由绿钥匙精英使用，不在心脏战斗里出现。
    /// </remarks>
    internal static readonly string[] KnownGaps =
    [
        "Act4Heart.Powers.RegeneratePowerA4h：走 AfterSideTurnEnd，求解器没有镜像该阶段。"
    ];

    /// <summary>Power 要镜像的虚方法：名字 + 精确形参表（供登记前核对重写存在）。</summary>
    private static readonly (string Name, Type[] Parameters)[] MirroredOverrides =
    [
        ("AfterCardPlayed",
            [typeof(PlayerChoiceContext), typeof(CardPlay)]),
        ("AfterDamageReceived",
            [typeof(PlayerChoiceContext), typeof(Creature), typeof(DamageResult), typeof(ValueProp),
                typeof(Creature), typeof(CardModel)]),
        ("ModifyHpLostAfterOstyLate",
            [typeof(Creature), typeof(decimal), typeof(ValueProp), typeof(Creature), typeof(CardModel)]),
        ("BeforeSideTurnEndEarly",
            [typeof(PlayerChoiceContext), typeof(CombatSide), typeof(IEnumerable<Creature>)]),
    ];

    public static void Verify()
    {
        Type beatOfDeath = A4hReflection.RequirePowerType(BeatOfDeathType);
        Type invincible = A4hReflection.RequirePowerType(InvincibleType);
        Type metallicize = A4hReflection.RequirePowerType(MetallicizeType);

        // 登记表按「名称 + 精确形参」找重写，找不到就抛；登记不是事务性的，所以先把每一项查一遍，
        // 保证 RegisterAll 不会中途失败留下「装了一半」的登记表（docs/THIRD_PARTY_ADAPTERS.md §3.2）。
        RequireOverride(beatOfDeath, "AfterCardPlayed");
        RequireOverride(invincible, "AfterDamageReceived");
        RequireOverride(invincible, "ModifyHpLostAfterOstyLate");
        RequireOverride(metallicize, "BeforeSideTurnEndEarly");

        // 无敌的隐藏状态要靠反射读 _internalData 与它里面的字段；提前确认这条路走得通。
        A4hReflection.RequireInvincibleInternalMembers();
    }

    public static void RegisterAll()
    {
        // --- 死亡节拍：每次出牌都让出牌者掉 Amount 点血 ---
        AfterCardPlayedMirrors.Register(
            A4hReflection.RequirePowerType(BeatOfDeathType),
            HandleBeatOfDeath);

        // --- 无敌：累计每回合受到的伤害，达到 Amount 后把后续血量损失截断 ---
        AfterDamageReceivedMirrors.Register(
            A4hReflection.RequirePowerType(InvincibleType),
            HandleInvincibleDamageReceived);
        ModifyHpLostMirrors.RegisterAfterOstyLate(
            A4hReflection.RequirePowerType(InvincibleType),
            HandleInvincibleHpCap);
        // 克隆会把 _internalData 重置，根捕获时必须把实机实例上的累计值搬进预测状态。
        PowerHiddenStateMirrors.RegisterRootCapture(
            A4hReflection.RequirePowerType(InvincibleType),
            (simulator, clone, original) =>
                _ = simulator.StateStore.GetReadOnly(clone, () => new InvinciblePredictionState(original)));
        // 每回合计数要进指纹：只在累计值上不同的两条分支否则会被当成同一个状态去重掉一条。
        PowerHiddenStateMirrors.Register(
            A4hReflection.RequirePowerType(InvincibleType),
            InvincibleDamageSlot,
            static (simulator, power) => (long)simulator.StateStore
                .Peek(power, () => new InvinciblePredictionState(power))
                .DamageReceivedThisTurn);
        // 自己在自己那一方回合开始清零，并把血量显示恢复成常规。
        ThirdPartyAdapterRegistry.RegisterTurnStartPower(InvincibleType, ResetInvincible);

        // --- 金属化：回合结束给持有者等量格挡（绿钥匙精英用，登记进来严格更好） ---
        BeforeSideTurnEndMirrors.RegisterEarly(
            A4hReflection.RequirePowerType(MetallicizeType),
            HandleMetallicize);
    }

    // === 逐条对照 Act4Heart 源码 ===

    /// <summary>
    /// <c>BeatOfDeathPower.AfterCardPlayed</c>：
    /// <c>CreatureCmd.Damage(ctx, cardPlay.Card.Owner.Creature, Amount, (ValueProp)4, Owner)</c>。
    /// </summary>
    /// <remarks>源码对目标不做任何过滤——谁出的牌就打谁，所以这里也不过滤：漏掉一次会造成实打实的低估。</remarks>
    private static void HandleBeatOfDeath(AbstractModel model, AfterCardPlayedMirrorContext context)
    {
        PowerModel power = (PowerModel)model;
        context.Simulator.Damage(
            context.CardPlay.Card.Owner.Creature,
            power.Amount,
            ValueProp.Unpowered,
            power.Owner);
    }

    /// <summary>
    /// <c>InvinciblePower.AfterDamageReceived</c>：本回合累计受到的未格挡伤害。
    /// </summary>
    /// <remarks>
    /// 单人战斗里 <c>split</c> 为假，源码的下标器直接返回共享字段，**与 dealer 无关**，
    /// 所以这里就是一个共享计数；累计到 <c>Amount</c> 时把血量显示切成「无限带数字」
    /// （原版硬化外壳同款），<c>HellraiserPower</c> 之类的判定会读它。
    /// </remarks>
    private static void HandleInvincibleDamageReceived(AbstractModel model, AfterDamageReceivedMirrorContext context)
    {
        PowerModel power = (PowerModel)model;
        if (context.Target != power.Owner || context.Result.WasFullyBlocked)
            return;

        InvinciblePredictionState state = context.StateStore.Get(power, () => new InvinciblePredictionState(power));
        state.DamageReceivedThisTurn += context.Result.UnblockedDamage;
        if (state.DamageReceivedThisTurn >= power.Amount)
            context.State.GetCreature(power.Owner).HpDisplay = HpDisplay.InfiniteWithNumbers;
    }

    /// <summary>
    /// <c>InvinciblePower.ModifyHpLostAfterOstyLate</c>：把这次血量损失截到剩余额度。
    /// </summary>
    private static decimal HandleInvincibleHpCap(AbstractModel model, ModifyHpLostMirrorContext context)
    {
        PowerModel power = (PowerModel)model;
        if (context.Target != power.Owner || context.Amount == 0m)
            return context.Amount;

        InvinciblePredictionState state = context.StateStore.Get(power, () => new InvinciblePredictionState(power));
        return Math.Min(context.Amount, power.Amount - state.DamageReceivedThisTurn);
    }

    /// <summary>
    /// <c>InvinciblePower.BeforeSideTurnStart</c>：自己那一方回合开始时清零。
    /// </summary>
    /// <remarks>
    /// <c>participants</c> 是**正在开始回合**的那一方的生物，因此 <c>participants.Contains(Owner)</c>
    /// 与源码的 <c>side == Owner.Side</c> 等价。
    /// </remarks>
    private static void ResetInvincible(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        PowerModel power,
        IReadOnlyList<Creature> participants)
    {
        if (!participants.Contains(power.Owner))
            return;
        _ = combat;
        simulator.StateStore.Get(power, () => new InvinciblePredictionState(power)).DamageReceivedThisTurn = 0m;
        simulator.State.GetCreature(power.Owner).HpDisplay = HpDisplay.Normal;
    }

    /// <summary>
    /// <c>MetallicizePowerA4h.BeforeSideTurnEndEarly</c>：
    /// <c>CreatureCmd.GainBlock(Owner, Amount, (ValueProp)4 /*Unpowered*/, null, false)</c>。
    /// </summary>
    private static void HandleMetallicize(AbstractModel model, BeforeSideTurnEndMirrorContext context)
    {
        PowerModel power = (PowerModel)model;
        if (context.Participants.Contains(power.Owner))
            context.Simulator.GainBlock(power.Owner, power.Amount, ValueProp.Unpowered);
    }

    private static void RequireOverride(Type powerType, string methodName)
    {
        foreach ((string name, Type[] parameters) in MirroredOverrides)
        {
            if (string.Equals(name, methodName, StringComparison.Ordinal))
            {
                A4hReflection.RequireOverride(powerType, name, parameters);
                return;
            }
        }
        // 名字写错时不能静默跳过——那会让「登记前先核对重写」这道防线形同虚设。
        throw new InvalidOperationException(
            $"{methodName} 不在 MirroredOverrides 名单里，自检代码与镜像登记已经对不上。");
    }
}

/// <summary>
/// 心脏 <c>InvinciblePower</c> 在预测里的每回合累计伤害。
/// </summary>
/// <remarks>
/// 状态原本躺在 <c>_internalData</c> 的私有嵌套类型 <c>Data</c> 里，而且是**浮点数**（<c>decimal</c>），
/// 所以两种构造方式都必须从**实机实例**读初值：
/// <list type="bullet">
/// <item><description>根捕获时传实机 Power（<c>PowerHiddenStateMirrors.RegisterRootCapture</c>）；</description></item>
/// <item><description>模拟途中首次取用、还没有捕获值时传的其实就是那株克隆，这时按 <c>0</c> 起算。</description></item>
/// </list>
/// <see cref="DamageReceivedThisTurn"/> 会被写进状态指纹（截断成整数），所以只差这条计数的两条分支
/// 不会再被去重成一条。
/// </remarks>
internal sealed class InvinciblePredictionState : IPredictionStateForkable
{
    public InvinciblePredictionState(PowerModel power)
        => DamageReceivedThisTurn = A4hReflection.ReadInvincibleDamageReceived(power);

    public decimal DamageReceivedThisTurn { get; set; }

    public object Fork(PredictionForkContext context) => MemberwiseClone();
}
