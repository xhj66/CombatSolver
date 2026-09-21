using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal enum PredictedDeathPhase
{
    None,
    Reviving,
    PermanentlyDead,
}

internal sealed partial class SimulatedCombatState
{
    private ForkableDictionary<Creature, PredictedDeathPhase>? _deathPhases;

    public void SpawnStockReplacement(CombatPredictionSimulator simulator, StockPower power)
        => MonsterSpawnSupport.Spawn<Axebot>(simulator, this, power.Owner, power.Owner.SlotName,
            configure: axebot =>
            {
                axebot.ShouldPlaySpawnAnimation = true;
                axebot.StockAmount = power.Amount - 1;
            });

    private static ForkableDictionary<Creature, PredictedDeathPhase>? BuildInitialDeathPhases(
        IReadOnlyList<Creature> enemies)
    {
        ForkableDictionary<Creature, PredictedDeathPhase>? phases = null;
        foreach (Creature enemy in enemies)
        {
            if (enemy.CurrentHp > 0)
                continue;
            (phases ??= [])[enemy] = (PredictedDeathPhase)LiveDeathPhase(enemy);
        }
        return phases;
    }

    public bool CanPerformMonsterMove(CombatPredictionSimulator simulator, Creature creature)
        => simulator.State.GetCreature(creature).IsAlive
            || _deathPhases?.GetValueOrDefault(creature) == PredictedDeathPhase.Reviving;

    private int RevivingEnemyHp(Creature creature, int capturedMaxHp)
    {
        if (_deathPhases?.GetValueOrDefault(creature) != PredictedDeathPhase.Reviving)
            return 0;
        if (creature.Monster is DecimillipedeSegment)
        {
            bool hasSurvivingSegment = GetTeammatesOf(creature)
                .Any(candidate => candidate != creature
                    && GetAmount<ReattachPower>(candidate) > 0
                    && _deathPhases?.GetValueOrDefault(candidate) != PredictedDeathPhase.PermanentlyDead);
            return hasSurvivingSegment ? Math.Max(0, GetAmount<ReattachPower>(creature)) : 0;
        }
        if (creature.Monster is TestSubject)
            return RemainingTestSubjectFormHp(creature, currentHp: 0);
        if (RegisteredRevivePower(creature) is { } revivePower)
        {
            // 与 ReattachPower 那条同形：组里还有没被永久判死的队友时，尸体按待复活的层数计入终局口径。
            bool hasRevivingSibling = RegisteredReviveSiblings(creature, revivePower.GetType().Name)
                .Any(candidate => _deathPhases?.GetValueOrDefault(candidate) != PredictedDeathPhase.PermanentlyDead);
            return hasRevivingSibling ? Math.Max(0, revivePower.Amount) : 0;
        }
        return GetAmount<IllusionPower>(creature) > 0 ? capturedMaxHp : 0;
    }

    public int RemainingTestSubjectFormHp(Creature creature, int currentHp)
    {
        if (creature.Monster is not TestSubject
            || _deathPhases?.GetValueOrDefault(creature) == PredictedDeathPhase.PermanentlyDead)
        {
            return Math.Max(0, currentHp);
        }

        int remaining = Math.Max(0, currentHp);
        if (GetAmount<AdaptablePower>(creature) <= 0)
            return remaining;

        int respawns = GetMonsterInt(creature, "_respawns");
        if (respawns < 1)
            remaining += ScaleTestSubjectFormHp(creature, "SecondFormHp");
        if (respawns < 2)
            remaining += ScaleTestSubjectFormHp(creature, "ThirdFormHp");
        return remaining;
    }

    private int ScaleTestSubjectFormHp(Creature creature, string member)
        => (int)Creature.ScaleHpForMultiplayer(
            GetMonsterInt(creature, member),
            Encounter,
            Players.Count,
            _currentActIndex);

    public void BeginAdaptableRevive(Creature creature)
    {
        SetDeathPhase(creature, PredictedDeathPhase.Reviving);
        ForceMonsterMove(creature, "RESPAWN_MOVE");
    }

    public void BeginIllusionRevive(Creature creature)
    {
        if (_deathPhases?.GetValueOrDefault(creature) == PredictedDeathPhase.Reviving)
            return;
        BranchMonsterAiState ai = GetMonsterAiState(creature);
        string? followUp = GetPower<IllusionPower>(creature)?.FollowUpStateId
            ?? ai.StateLog.LastOrDefault(moveId => moveId != "REVIVE_MOVE");
        if (followUp == null)
        {
            throw new InvalidOperationException(
                $"幻象 {creature.Name} 进入复活时没有可恢复的正式行动记录。");
        }
        MoveState revive = new("REVIVE_MOVE", _ => Task.CompletedTask, new HealIntent())
        {
            FollowUpStateId = followUp,
            MustPerformOnceBeforeTransitioning = true,
        };
        SetDeathPhase(creature, PredictedDeathPhase.Reviving);
        ForceMonsterMove(creature, revive);
    }

    public void BeginReattach(CombatPredictionSimulator simulator, Creature creature)
    {
        Creature[] otherSegments = GetTeammatesOf(creature)
            .Where(candidate => candidate != creature && GetAmount<ReattachPower>(candidate) > 0)
            .ToArray();
        bool allDead = otherSegments.All(candidate => simulator.State.GetCreature(candidate).IsDead);
        if (allDead)
        {
            foreach (Creature segment in otherSegments.Append(creature))
                SetDeathPhase(segment, PredictedDeathPhase.PermanentlyDead);
            return;
        }
        SetDeathPhase(creature, PredictedDeathPhase.Reviving);
        ForceMonsterMove(creature, "DEAD_MOVE");
    }

    /// <summary>这个生物身上已登记的第三方复活 Power（层数大于 0），没有就是 <c>null</c>。</summary>
    private PowerModel? RegisteredRevivePower(Creature creature)
    {
        IReadOnlyList<PowerModel> powers = EffectivePowers();
        for (int index = 0; index < powers.Count; index++)
        {
            PowerModel power = powers[index];
            if (power.Amount > 0
                && ReferenceEquals(power.Owner, creature)
                && ThirdPartyAdapterRegistry.IsRevivePowerName(power.GetType().Name))
            {
                return power;
            }
        }
        return null;
    }

    private bool HasRegisteredRevivePowerNamed(Creature creature, string powerTypeName)
    {
        IReadOnlyList<PowerModel> powers = EffectivePowers();
        for (int index = 0; index < powers.Count; index++)
        {
            PowerModel power = powers[index];
            if (power.Amount > 0
                && ReferenceEquals(power.Owner, creature)
                && string.Equals(power.GetType().Name, powerTypeName, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>同侧队友里带同一个第三方复活 Power 的个体（与源码按 Power 分组同口径）。</summary>
    private Creature[] RegisteredReviveSiblings(Creature creature, string powerTypeName)
        => GetTeammatesOf(creature)
            .Where(candidate => candidate != creature && HasRegisteredRevivePowerNamed(candidate, powerTypeName))
            .ToArray();

    /// <summary>
    /// 第三方复活 Power 的持有者死亡：组里还剩活人就保留尸体并强制走它的「死亡回合」，
    /// 全组都死了就把整组标成永久死亡（战斗可以结束）。与 <see cref="BeginReattach"/> 同形。
    /// </summary>
    public void BeginRegisteredRevive(
        CombatPredictionSimulator simulator,
        Creature creature,
        PowerModel power)
    {
        if (!ThirdPartyAdapterRegistry.TryGetRevivePower(power.GetType().Name, out var registration))
        {
            throw new PredictionUnsupportedException(
                $"{power.GetType().Name} 没有登记复活语义，无法开始复活阶段。");
        }
        Creature[] siblings = RegisteredReviveSiblings(creature, registration.PowerTypeName);
        bool allDead = siblings.All(candidate => simulator.State.GetCreature(candidate).IsDead);
        if (allDead)
        {
            foreach (Creature candidate in siblings.Append(creature))
                SetDeathPhase(candidate, PredictedDeathPhase.PermanentlyDead);
            return;
        }
        SetDeathPhase(creature, PredictedDeathPhase.Reviving);
        ForceMonsterMove(creature, registration.DeadMoveId);
    }

    /// <summary>
    /// 第三方复活 Power 组里其他人是不是都死了（对应源码 <c>ShouldOwnerDeathTriggerFatal</c> 的判据）。
    /// 读的是**模拟状态**的血量：源码那份实现走 <c>Owner.CombatState</c>，在预测里读到的是实机值。
    /// </summary>
    public bool AreAllRegisteredReviveSiblingsDead(
        CombatPredictionSimulator simulator,
        Creature creature,
        string powerTypeName)
        => RegisteredReviveSiblings(creature, powerTypeName)
            .All(candidate => simulator.State.GetCreature(candidate).IsDead);

    public void CompleteDeathPhase(Creature creature)
    {
        if (_deathPhases?.GetValueOrDefault(creature) is not PredictedDeathPhase.Reviving)
            SetDeathPhase(creature, PredictedDeathPhase.PermanentlyDead);
    }

    public bool HasCompletedDeathEffects(Creature creature)
        => _deathPhases?.GetValueOrDefault(creature) is PredictedDeathPhase.Reviving or PredictedDeathPhase.PermanentlyDead;

    private bool CanReceivePredictedPowers(Creature creature)
    {
        PredictedDeathPhase phase = _deathPhases?.GetValueOrDefault(creature)
            ?? PredictedDeathPhase.None;
        return phase == PredictedDeathPhase.None;
    }

    public void ResolveReviveMove(
        CombatPredictionSimulator simulator,
        Creature creature,
        string moveId)
    {
        switch (creature.Monster)
        {
            case TestSubject when moveId == "RESPAWN_MOVE":
                ResolveTestSubjectRevive(simulator, creature);
                break;
            case DecimillipedeSegment when moveId == "REATTACH_MOVE":
            {
                bool allOthersDead = GetTeammatesOf(creature)
                    .Where(candidate => candidate != creature && GetAmount<ReattachPower>(candidate) > 0)
                    .All(candidate => !CanPerformMonsterMove(simulator, candidate));
                if (!allOthersDead)
                {
                    simulator.Heal(creature, GetAmount<ReattachPower>(creature));
                    SetDeathPhase(creature, PredictedDeathPhase.None);
                }
                break;
            }
            default:
                if (moveId == "REVIVE_MOVE")
                {
                    SimCreatureState state = simulator.State.GetCreature(creature);
                    state.CurrentHp = state.MaxHp;
                    SetDeathPhase(creature, PredictedDeathPhase.None);
                }
                else
                {
                    ResolveRegisteredReviveMove(simulator, creature, moveId);
                }
                break;
        }
    }

    /// <summary>
    /// 第三方复活 Power 的「复活回合」：组里还有活人就治疗 <c>Amount</c> 点并复活，否则保持死亡。
    /// 与源码 <c>DoReattach</c> 的判据一致（全组都死了就什么都不做）。
    /// </summary>
    private void ResolveRegisteredReviveMove(
        CombatPredictionSimulator simulator,
        Creature creature,
        string moveId)
    {
        if (_deathPhases?.GetValueOrDefault(creature) != PredictedDeathPhase.Reviving)
            return;
        if (RegisteredRevivePower(creature) is not { } power
            || !ThirdPartyAdapterRegistry.TryGetRevivePower(power.GetType().Name, out var registration)
            || !string.Equals(registration.ReviveMoveId, moveId, StringComparison.Ordinal))
        {
            return;
        }
        if (AreAllRegisteredReviveSiblingsDead(simulator, creature, registration.PowerTypeName))
            return;
        simulator.Heal(creature, Math.Max(0, power.Amount));
        SetDeathPhase(creature, PredictedDeathPhase.None);
    }

    public void RemovePowersAfterDeath(Creature creature)
    {
        bool hasIllusionHook = EffectivePowers().Any(power =>
            power is IllusionPower
            && power.Amount > 0
            && ReferenceEquals(power.Owner, creature));
        foreach (PowerModel power in EffectivePowers()
                     .Where(power => power.Owner == creature && power.Amount != 0)
                     .ToArray())
        {
            bool keep = !power.ShouldPowerBeRemovedAfterOwnerDeath();
            if (hasIllusionHook)
            {
                keep = power.Type != PowerType.Debuff || power is ITemporaryPower;
            }
            if (!keep)
                SetPowerAmount(power, 0);
        }
        if (!ContainsCreature(creature))
        {
            foreach (PowerModel power in EffectivePowers()
                         .Where(power => power.Owner == creature && power.Amount != 0)
                         .ToArray())
            {
                SetPowerAmount(power, 0);
            }
        }
    }

    private void ResolveTestSubjectRevive(CombatPredictionSimulator simulator, Creature creature)
    {
        int respawns = GetMonsterInt(creature, "_respawns") + 1;
        SetMonsterInt(creature, "_respawns", respawns);
        int hp = respawns switch
        {
            1 => GetMonsterInt(creature, "SecondFormHp"),
            2 => GetMonsterInt(creature, "ThirdFormHp"),
            _ => throw new InvalidOperationException($"测试体出现未知复活阶段 {respawns}。"),
        };
        hp = (int)Creature.ScaleHpForMultiplayer(hp, Encounter, Players.Count, _currentActIndex);
        SimCreatureState state = simulator.State.GetCreature(creature);
        state.SetMaxHp(hp);
        state.CurrentHp = hp;
        SetDeathPhase(creature, PredictedDeathPhase.None);
        if (respawns == 1)
        {
            Apply<PainfulStabsPower>(creature, 1, creature);
        }
        else
        {
            Apply<NemesisPower>(creature, 1, creature);
            SetAmount<AdaptablePower>(creature, 0);
            SetAmount<PainfulStabsPower>(creature, 0);
        }
    }

    private void SetDeathPhase(Creature creature, PredictedDeathPhase phase)
        => (_deathPhases ??= [])[creature] = phase;

    private void AppendDeathLifecycleFingerprint(ref StateFingerprintBuilder fingerprint)
    {
        if (_deathPhases == null)
            return;
        foreach ((Creature creature, PredictedDeathPhase phase) in _deathPhases
                     .OrderBy(entry => entry.Key.CombatId))
        {
            fingerprint.Add('L');
            fingerprint.Add(creature.CombatId ?? uint.MaxValue);
            fingerprint.Add((int)phase);
        }
    }
}
