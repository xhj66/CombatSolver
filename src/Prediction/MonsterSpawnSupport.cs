using CombatSolver.Engine.Common;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Modifiers;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal static class MonsterSpawnSupport
{
    public static Creature Spawn<T>(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        Creature source,
        string? slot,
        bool minion = false,
        Action<T>? configure = null)
        where T : MonsterModel
    {
        Creature creature = Create(simulator, combat, slot, configure);
        AddCreated(simulator, combat, source, creature, minion);
        return creature;
    }

    public static Creature Create<T>(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        string? slot,
        Action<T>? configure = null)
        where T : MonsterModel
    {
        T monster = (T)CanonicalModels.Monster<T>().ToMutable();
        configure?.Invoke(monster);
        return combat.CreatePredictedMonster(simulator, monster, CombatSide.Enemy, slot);
    }

    /// <summary>
    /// 按**运行时类型**生成一个第三方怪物（被适配 Mod 的类型不在编译期引用里，只有 <see cref="Type"/>）。
    /// </summary>
    /// <remarks>
    /// 与泛型 <see cref="Spawn{T}"/> 逐段对应：规范实例取自 <c>ModelDb</c>、克隆、按源码顺序
    /// <c>CreatePredictedMonster</c> 掷初始生命（消耗 <c>Rng.Niche</c>，与原生 <c>CreatureCmd.Add</c>
    /// 同一条流）、布点、入场能力与遗物联动、行动 AI 准备。
    ///
    /// <para>
    /// <paramref name="maxHpOverride" /> 对应源码里紧随 <c>CreatureCmd.Add</c> 之后的
    /// <c>SetMaxHp(n)</c> + <c>Heal(n, true)</c>：往昔之章分裂出来的史莱姆就是按**分裂那一刻的血量**
    /// 整只出现的（先掷一次初始生命再用 <c>SetMaxHp</c> 覆盖，掷的那次抽样不能省，
    /// 否则 <c>Rng.Niche</c> 的进度与实机错位）。
    /// </para>
    ///
    /// <para>
    /// **入场效果不会被自动执行。** 原生那批由 <see cref="ApplyNativeEntrancePowers" /> 按类型写死；
    /// 第三方的 <c>AfterAddedToRoom</c> 只能由适配方在返回后自己登记（例如往昔之章分裂出来的大型史莱姆
    /// 要自己再挂一份 <c>SplitPower</c>，否则它不会二次分裂）。
    /// </para>
    /// </remarks>
    public static Creature SpawnByType(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        Creature source,
        Type monsterType,
        string? slot,
        int? maxHpOverride = null,
        bool minion = false,
        Action<MonsterModel>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(monsterType);
        if (!typeof(MonsterModel).IsAssignableFrom(monsterType))
            throw new ArgumentException($"{monsterType.FullName} 不是 MonsterModel 类型。", nameof(monsterType));
        MonsterModel monster = (MonsterModel)ModelDb
            .GetById<MonsterModel>(ModelDb.GetId(monsterType))
            .ToMutable();
        configure?.Invoke(monster);
        Creature creature = combat.CreatePredictedMonster(simulator, monster, CombatSide.Enemy, slot);
        AddCreated(simulator, combat, source, creature, minion);
        if (maxHpOverride is { } maxHp)
        {
            SimCreatureState state = simulator.State.GetCreature(creature);
            state.SetMaxHp(maxHp);
            state.CurrentHp = maxHp;
        }
        return creature;
    }

    public static void AddCreated(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        Creature source,
        Creature creature,
        bool minion = false)
    {
        combat.AddPredictedMonster(creature);
        ApplyNativeEntrancePowers(combat, creature);
        if (creature.Side == CombatSide.Enemy && combat.Modifiers.Any(static modifier => modifier is Murderous))
            combat.Apply<StrengthPower>(creature, 3);
        ApplyCreatureAddedRelics(simulator, combat, creature);
        combat.PreparePredictedMonster(simulator, creature);
        // The summoning move applies Minion after CreatureCmd.Add and its entrance hooks finish.
        if (minion && combat.GetAmount<MinionPower>(creature) <= 0)
            combat.Apply<MinionPower>(creature, 1, source);
    }

    public static string? NextSlot(SimulatedCombatState combat)
        => combat.NextFreeSlot();

    public static string? LastFreeSlot(SimulatedCombatState combat)
        => combat.LastFreeSlot();

    private static void ApplyNativeEntrancePowers(SimulatedCombatState combat, Creature creature)
    {
        switch (creature.Monster)
        {
            case GasBomb:
                combat.Apply<MinionPower>(creature, 1, creature);
                break;
            case EyeWithTeeth or Parafright:
                combat.Apply<IllusionPower>(creature, 1, creature);
                combat.Apply<MinionPower>(creature, 1, null);
                break;
            case ToughEgg:
                combat.Apply<HatchPower>(creature, combat.CurrentSide == CombatSide.Enemy ? 2 : 1, creature);
                break;
            case Zapbot:
                combat.Apply<HighVoltagePower>(creature, 2, creature);
                break;
            case Axebot axebot when axebot.StockAmount > 0:
                combat.Apply<StockPower>(creature, axebot.StockAmount, null);
                break;
        }
    }

    private static void ApplyCreatureAddedRelics(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        Creature creature)
    {
        foreach (RelicModel relic in combat.Players
                     .SelectMany(combat.RelicsOf)
                     .Where(relic => !relic.IsMelted))
        {
            switch (relic)
            {
                case PhilosophersStone when creature.Side != relic.Owner.Creature.Side:
                    combat.Apply<StrengthPower>(creature, relic.DynamicVars["StrengthPower"].IntValue);
                    break;
                case FurCoat furCoat when creature.Side == CombatSide.Enemy:
                    if (combat.CurrentMapCoord is { } currentMapCoord
                        && furCoat.GetMarkedCoords()?.Contains(currentMapCoord) == true)
                        simulator.State.GetCreature(creature).CurrentHp = 1;
                    break;
            }
        }
    }
}
