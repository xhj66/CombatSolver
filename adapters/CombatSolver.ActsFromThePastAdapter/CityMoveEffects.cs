using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver.ActsFromThePastAdapter;

/// <summary>
/// 往昔之章第二幕（The City）怪物的行动效果（非攻击部分）。
/// </summary>
/// <remarks>
/// 与第一幕（<see cref="ExordiumMoveEffects"/>）同一条纪律：逐行对照 AFTP 源码里那个 <c>MoveState</c>
/// 的回调，顺序也一致；攻击部分仍由求解器的通用攻击循环按意图结算。
/// </remarks>
internal static class CityMoveEffects
{
    /// <summary>
    /// 适配层自己往怪物标量状态里塞的一项：百夫长那条私有 RNG 流已经抽过几次。
    /// </summary>
    /// <remarks>
    /// 这个名字**不在** <see cref="ThirdPartyAdapterRegistry.RegisterMonsterStateMembers"/> 的名单里——
    /// 实机怪物身上并没有这个成员，登记进名单会让根捕获去读一个不存在的字段。它只在预测过程中由
    /// 适配层写入，一样会进状态指纹（指纹遍历的是整张标量状态表）。
    /// </remarks>
    internal const string CenturionRngDrawsMember = "adapter_centurion_rng_draws";

    private static readonly string[] MonsterTypes = ["Centurion", "Mystic"];

    public static void Verify()
    {
        foreach (string typeName in MonsterTypes)
            AfpReflection.RequireMonsterType(typeName);
        // 百夫长的 Protect 要用它自己那条私有 RNG 流抽目标，核对该访问点还在。
        MonsterRngSupport.VerifyShape();
    }

    public static void RegisterAll()
    {
        // 都是 AscensionHelper 形式的实例属性（A8+ 20／否则 15；治疗量、治疗阈值、力量见下），
        // 根捕获时读一次，之后整场不变。
        ThirdPartyAdapterRegistry.RegisterStaticIntMembers("Centurion", "ProtectBlock");
        ThirdPartyAdapterRegistry.RegisterStaticIntMembers(
            "Mystic",
            "HealAmount",
            "HealThreshold",
            "StrengthAmount");

        // Centurion.Protect：给一只随机的存活队友（一只都没有就给自己）ProtectBlock 点格挡
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("Centurion", "PROTECT", CenturionProtect);
        // Mystic.Attack：攻击 + 全体目标 Frail 2
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("Mystic", "ATTACK", MysticAttack);
        // Mystic.Heal：给所有存活队友（含自己）回复 HealAmount
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("Mystic", "HEAL", MysticHeal);
        // Mystic.Buff：给所有存活队友（含自己）StrengthAmount 点力量
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("Mystic", "BUFF", MysticBuff);
    }

    /// <summary>
    /// Centurion.Protect：与小鬼盾兵的 <c>Protect</c> 同型——在所有存活队友（不含自己）里用这只怪物
    /// 自己那条 RNG 流抽一只，给它 <c>ProtectBlock</c> 点格挡；一只存活队友都没有时格挡落在自己身上，
    /// 且**一次都不抽**（源码先 <c>teammates.Any()</c> 再进 <c>NextItem</c>）。
    /// </summary>
    private static bool CenturionProtect(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        killedOwner = false;
        List<Creature> teammates = [];
        foreach (Creature candidate in combat.GetTeammatesOf(move.Owner))
        {
            if (candidate != move.Owner && simulator.State.GetCreature(candidate).IsAlive)
                teammates.Add(candidate);
        }
        Creature target = move.Owner;
        if (teammates.Count > 0)
        {
            MonsterRngPredictionState rng = MonsterRngSupport.State(
                simulator,
                move.Owner.Monster ?? throw new PredictionUnsupportedException("百夫长缺少怪物模型。"));
            target = rng.NextItem(teammates)!;
            combat.SetMonsterInt(move.Owner, CenturionRngDrawsMember, rng.Draws);
        }
        simulator.GainBlock(
            target,
            combat.GetMonsterStaticInt(move.Owner, "ProtectBlock"),
            ValueProp.Move);
        return true;
    }

    /// <summary>Mystic.Attack：攻击之后给目标挂 2 层 Frail（源码对 <c>targets</c> 全体施加）。</summary>
    private static bool MysticAttack(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        killedOwner = false;
        if (simulator.State.GetCreature(player).IsAlive)
            combat.ApplyFromMonster<FrailPower>(player, 2, move.Owner);
        return true;
    }

    /// <summary>Mystic.Heal：给所有存活队友（含自己）各回复 <c>HealAmount</c>。</summary>
    private static bool MysticHeal(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        killedOwner = false;
        int amount = combat.GetMonsterStaticInt(move.Owner, "HealAmount");
        foreach (Creature teammate in combat.GetTeammatesOf(move.Owner))
        {
            if (simulator.State.GetCreature(teammate).IsAlive)
                simulator.Heal(teammate, amount);
        }
        return true;
    }

    /// <summary>Mystic.Buff：给所有存活队友（含自己）各 <c>StrengthAmount</c> 点力量。</summary>
    private static bool MysticBuff(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        killedOwner = false;
        int amount = combat.GetMonsterStaticInt(move.Owner, "StrengthAmount");
        foreach (Creature teammate in combat.GetTeammatesOf(move.Owner))
        {
            if (simulator.State.GetCreature(teammate).IsAlive)
                combat.Apply<StrengthPower>(teammate, amount, move.Owner);
        }
        return true;
    }
}
