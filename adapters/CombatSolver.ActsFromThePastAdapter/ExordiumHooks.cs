using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Damage;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Death;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver.ActsFromThePastAdapter;

/// <summary>
/// 往昔之章第一幕的**能力与死亡钩子**：怪物和 Power 覆写的战斗钩子。
/// </summary>
/// <remarks>
/// 求解器按精确运行时类型分派这些钩子，未登记的第三方重写会记一条 <c>MethodNotMirrored</c>；
/// 只要方法名里带 «Death»，那条风险就会让整场战斗<b>给不出战损</b>（`uncertainVictory` 把边界改写成
/// <c>UnsupportedEffect</c>，`Terminal` 于是不写 `CombatEndedTurn`）。所以两件事必须做：
///
/// <list type="number">
/// <item>**真正有玩法影响的**重写要给出预测实现——真菌兽的孢子云（死亡时给全体玩家易伤）、
/// 疯狂小鬼的愤怒（挨到攻击就加力量）；</item>
/// <item>**只有表现层副作用**的重写要显式登记为忽略——邪教徒与真菌兽的 `BeforeDeath`（音效、台词、
/// 粒子特效）、六角幽魂的 `AfterDeath`（收尾动画与屏幕震动）。</item>
/// </list>
///
/// 两条判据都要逐行反编译核对，见 docs/THIRD_PARTY_ADAPTERS.md §2.13。
/// </remarks>
internal static class ExordiumHooks
{
    private static Type _sporeCloud = null!;
    private static Type _angry = null!;

    /// <summary>
    /// <c>SplitPower</c> 的类型，供分裂行动在生成子史莱姆之后按入场效果补挂（见 <c>SplitInto</c>）。
    /// </summary>
    internal static Type SplitPowerType { get; private set; } = null!;

    public static void Verify()
    {
        _sporeCloud = AfpReflection.RequireOverride("SporeCloudPower", "AfterDeath", 4);
        _angry = AfpReflection.RequireOverride("AngryPower", "AfterDamageReceived", 6);
        SplitPowerType = AfpReflection.RequireOverride("SplitPower", "AfterDamageReceived", 6);
        // 只登记为忽略的三条：确认它们「还重写着」就够了，内容由下面的复核结论背书。
        AfpReflection.RequireOverride("FungiBeast", "BeforeDeath", 1);
        AfpReflection.RequireOverride("Cultist", "BeforeDeath", 1);
        AfpReflection.RequireOverride("Hexaghost", "AfterDeath", 4);
    }

    public static void RegisterAll()
    {
        AfterDeathMirrors.Register(_sporeCloud, HandleSporeCloudDeath);
        AfterDamageReceivedMirrors.Register(_angry, HandleAngryDamageReceived);
        AfterDamageReceivedMirrors.Register(SplitPowerType, HandleSplitPower);

        // --- 已复核：只有表现层副作用 ---
        // FungiBeast.BeforeDeath：CreatureNode 与 NSporeImpactVfx.Create，加一个 Godot 定时器播粒子，
        // 没有命令、没有数值／状态／RNG 读写。
        BeforeDeathMirrors.RegisterIgnored(AfpReflection.RequireType("ActsFromThePast.FungiBeast"));
        // Cultist.BeforeDeath：PlayDeathSfx（Rng.Chaotic 抽音效编号）+ 条件式台词气泡 + Cmd.Wait(2.5)
        // 的动画等待，不下命令、不读不写战斗状态。
        BeforeDeathMirrors.RegisterIgnored(AfpReflection.RequireType("ActsFromThePast.Cultist"));
        // Hexaghost.AfterDeath：_visuals.HideAllOrbs/Dispose 与 NGame.ScreenShake，纯表现。
        AfterDeathMirrors.RegisterIgnored(AfpReflection.RequireType("ActsFromThePast.Hexaghost"));
    }

    /// <summary>
    /// <c>SplitPower.AfterDamageReceived</c>：持有者（大型史莱姆／史莱姆王）挨到真掉血的伤害、
    /// 且血量掉到**一半或以下**时，把 <c>SplitTriggered</c> 置位并把当前行动强制改写成 <c>SPLIT</c>——
    /// 逐字复刻源码里的 <c>SetMoveImmediate(SplitState, true)</c>。源码用整数除法比较
    /// （<c>CurrentHp &gt; MaxHp / 2</c>），这里也用模拟状态上的两个整数。
    /// </summary>
    private static void HandleSplitPower(AbstractModel model, AfterDamageReceivedMirrorContext context)
    {
        PowerModel power = (PowerModel)model;
        if (context.Target != power.Owner || context.Result.UnblockedDamage <= 0)
            return;
        SimCreatureState creature = context.State.GetCreature(power.Owner);
        if (creature.CurrentHp > creature.MaxHp / 2)
            return;
        if (context.CombatState is not ICombatPredictionMonsterStateSink monsterState)
            throw new InvalidOperationException("分裂缺少可写的预测怪物状态。");
        if (monsterState.GetMonsterBool(power.Owner, "_splitTriggered"))
            return;
        monsterState.SetMonsterBool(power.Owner, "_splitTriggered", true);
        monsterState.ForceMonsterMove(power.Owner, "SPLIT");
    }

    /// <summary>
    /// <c>SporeCloudPower.AfterDeath</c>（真菌兽入场时给自己挂 2 层）：owner 死亡且未被拦截时，
    /// 给**所有活着的玩家**各 <c>Amount</c> 层易伤（施加者为 null，与源码逐字一致）；
    /// 一个活人都没有时源码直接返回，连闪光都不做，这里照抄。
    /// </summary>
    private static void HandleSporeCloudDeath(AbstractModel model, AfterDeathMirrorContext context)
    {
        PowerModel power = (PowerModel)model;
        if (context.WasRemovalPrevented || context.Creature != power.Owner)
            return;
        List<Creature> alivePlayers = [];
        foreach (Creature player in context.CombatState.PlayerCreatures)
        {
            if (context.State.GetCreature(player).IsAlive)
                alivePlayers.Add(player);
        }
        if (alivePlayers.Count == 0)
            return;
        ICombatPredictionEffectSink effects = context.CombatState as ICombatPredictionEffectSink
            ?? throw new InvalidOperationException("孢子云缺少可写的预测状态。");
        foreach (Creature player in alivePlayers)
            effects.ApplyPower(typeof(VulnerablePower), player, power.Amount);
    }

    /// <summary>
    /// <c>AngryPower.AfterDamageReceived</c>（疯狂小鬼入场时给自己挂 1/2 层）：自己挨到**有来源的
    /// 攻击伤害**且真掉血时，给自己加 <c>Amount</c> 点力量。
    /// 源码写的是 <c>props.HasFlag(ValueProp.Move) &amp;&amp; !props.HasFlag(ValueProp.Unpowered)</c>，
    /// 这里照字面写，不换成帮助函数。
    /// </summary>
    private static void HandleAngryDamageReceived(AbstractModel model, AfterDamageReceivedMirrorContext context)
    {
        PowerModel power = (PowerModel)model;
        if (context.Target != power.Owner
            || context.Dealer is null
            || context.Result.UnblockedDamage <= 0
            || !context.Props.HasFlag(ValueProp.Move)
            || context.Props.HasFlag(ValueProp.Unpowered))
        {
            return;
        }
        ICombatPredictionEffectSink effects = context.CombatState as ICombatPredictionEffectSink
            ?? throw new InvalidOperationException("愤怒缺少可写的预测状态。");
        effects.ApplyPower(typeof(StrengthPower), power.Owner, power.Amount, power.Owner);
    }
}
