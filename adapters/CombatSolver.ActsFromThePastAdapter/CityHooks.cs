using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Card;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Death;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.TurnEnd;

namespace CombatSolver.ActsFromThePastAdapter;

/// <summary>
/// 往昔之章第二幕（The City）怪物的**能力与钩子**。
/// </summary>
/// <remarks>
/// 两类内容：
/// <list type="number">
/// <item>**真正有玩法影响的能力重写**要给出预测实现——`MetallicizePower.BeforeSideTurnEndEarly`
/// （冠军锻炉：自己回合末按层数获得格挡）与 `HexOriginalPower.AfterCardPlayed`（灾祸：打出非攻击牌时
/// 往抽牌堆塞 `Amount` 张 Dazed）；</item>
/// <item>**只有表现层副作用**的死亡钩子登记为忽略——`Chosen.BeforeDeath`（一句死亡音效）、
/// `Champ.BeforeDeath`（屏幕震动 + 死亡音效）。判定链看方法名里是否含 «Death»（§3.6），
/// 不登记就会让这两场打赢后给不出战损。</item>
/// </list>
/// </remarks>
internal static class CityHooks
{
    private static Type _metallicize = null!;
    private static Type _hex = null!;

    /// <summary>
    /// <c>MetallicizePower</c>（冠军锻炉）与 <c>HexOriginalPower</c>（灾祸）的类型，
    /// 供行动效果按 <c>Type</c> 施加（它们是往昔之章自己的类型，适配层没有编译期引用）。
    /// </summary>
    internal static Type MetallicizeType => _metallicize;

    internal static Type HexType => _hex;

    public static void Verify()
    {
        AfpReflection.RequireOverride("SphericGuardian", "BeforeDeath", 1);
        AfpReflection.RequireOverride("Snecko", "BeforeDeath", 1);
        AfpReflection.RequireOverride("Chosen", "BeforeDeath", 1);
        AfpReflection.RequireOverride("Champ", "BeforeDeath", 1);
        _metallicize = AfpReflection.RequireOverride("MetallicizePower", "BeforeSideTurnEndEarly", 3);
        _hex = AfpReflection.RequireOverride("HexOriginalPower", "AfterCardPlayed", 2);
    }

    public static void RegisterAll()
    {
        BeforeSideTurnEndMirrors.RegisterEarly(_metallicize, HandleMetallicizeTurnEnd);
        AfterCardPlayedMirrors.Register(_hex, HandleHexCardPlayed);

        BeforeDeathMirrors.RegisterIgnored(AfpReflection.RequireType("ActsFromThePast.SphericGuardian"));
        BeforeDeathMirrors.RegisterIgnored(AfpReflection.RequireType("ActsFromThePast.Snecko"));
        BeforeDeathMirrors.RegisterIgnored(AfpReflection.RequireType("ActsFromThePast.Chosen"));
        BeforeDeathMirrors.RegisterIgnored(AfpReflection.RequireType("ActsFromThePast.Champ"));
    }

    /// <summary>
    /// <c>MetallicizePower.BeforeSideTurnEndEarly</c>：自己那一方回合结束时按层数获得格挡
    /// （<c>ValueProp.Unpowered</c>，等同源码里的 <c>(ValueProp)4</c>）。
    /// </summary>
    private static void HandleMetallicizeTurnEnd(AbstractModel model, BeforeSideTurnEndMirrorContext context)
    {
        PowerModel power = (PowerModel)model;
        if (context.Side != power.Owner.Side)
            return;
        context.Simulator.GainBlock(power.Owner, power.Amount, ValueProp.Unpowered);
    }

    /// <summary>
    /// <c>HexOriginalPower.AfterCardPlayed</c>：出牌者就是持有者、且打出的**不是攻击牌**时，
    /// 往其抽牌堆塞 <c>Amount</c> 张 Dazed（随机位置）。
    /// </summary>
    private static void HandleHexCardPlayed(AbstractModel model, AfterCardPlayedMirrorContext context)
    {
        PowerModel power = (PowerModel)model;
        CardModel card = context.CardPlay.Card;
        if (card.Owner is not { } owner
            || owner.Creature != power.Owner
            || card.Type == CardType.Attack)
        {
            return;
        }
        context.Simulator.CreateAndAddGeneratedCardsToCombat<Dazed>(
            owner,
            PileType.Draw,
            power.Amount,
            creator: null,
            CardPilePosition.Random);
    }
}
