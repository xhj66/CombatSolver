using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private static async Task AssertTheftRecoveryPolicyAsync(CombatState combat)
    {
        AssertRequiredPotionAuditSelectionAndTotals();
        SolverInterimResult escape = new(true, 20, 0, 0, 0, 0, 0, 100, 2)
        { TheftPolicy = SolverTheftPolicy.PreserveResources };
        SolverInterimResult recovered = new(true, 0, 15, 15, 18, 2, 0, 0, 4)
        { TheftPolicy = SolverTheftPolicy.PreserveResources };
        if (!SolverInterimResultOrdering.IsBetter(recovered, escape)
            || !SolverInterimResultOrdering.CanPromoteDisplayedResult(recovered, escape)
            || SolverInterimResultOrdering.IsBetter(escape, recovered)
            || CombatSearchCoordinator.IsBetterPotionPolicyResult(SolverTheftPolicy.LetEscape, recovered, escape)
            || SolverInterimResultOrdering.IsBetter(recovered with { TheftPolicy = SolverTheftPolicy.LetEscape },
                escape with { TheftPolicy = SolverTheftPolicy.LetEscape })
            || TheftEncounterStrategy.RecoverySatisfied(SolverTheftPolicy.PreserveResources, 20)
            || !TheftEncounterStrategy.RecoverySatisfied(SolverTheftPolicy.LetEscape, 20)
            || SolverInterimResultOrdering.IsBetter(recovered with { Won = false }, escape))
            throw new InvalidOperationException("Theft policy mixed recovery, HP, potion, early-stop or victory priorities.");

        SearchPolicySnapshot policy = SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat,
            includeTurnSetup: false, theftPolicy: SolverTheftPolicy.PreserveResources);
        SolverSearchProfile profile = policy.Profile with { MaxExpandedNodes = 256, SoftTimeBudgetMilliseconds = 1500 };
        policy = policy with
        {
            Profile = profile, FixedBudget = true,
            BudgetOverrideMilliseconds = 1500,
            VerifyIncrementalSearch = false,
        };
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        SolverDisplayNames names = SolverDisplayNames.Capture(combat);
        BattleDamageSnapshot damage = BattleDamageTracker.Observe(combat);
        foreach (SolverTheftPolicy theft in Enum.GetValues<SolverTheftPolicy>())
        {
            SearchPolicySnapshot selectedPolicy = policy with { TheftPolicy = theft };
            SolverResult result = await Task.Run(() => CombatSearchCoordinator.Solve(root, names, damage,
                selectedPolicy, CancellationToken.None, progressCallback: null));
            if (theft == SolverTheftPolicy.PreserveResources && result.OutstandingStolenResource > 0
                && CombatSearchCoordinator.HasReachedAcceptableBattleHpLoss(selectedPolicy, result))
                throw new InvalidOperationException("Unrecovered loot incorrectly satisfied HP early stop.");
            Entry.Logger.Info($"[CombatSolver/Test] THEFT_RECOVERY_POLICY policy={theft} outstanding={result.OutstandingStolenResource} hp_loss={result.ProjectedBattleHpLost} potions={result.PotionCount} ended={result.CombatEndedTurn}");
        }
    }

    /// <summary>
    /// 第三方盗贼的识别与赃款核销：按**本体偷窃标记**判定遭遇，按登记核销死亡归还的金币。
    /// </summary>
    /// <remarks>
    /// 往昔之章的 Looter／Mugger 用本体 <c>ThieveryPower</c> 偷钱，自己（<c>Creature.Died</c> 事件）把赃款
    /// 当奖励还回来。原判据只认原版盗贼怪物与两个遭遇 Id，于是这场抢劫既不显示「保牌/保钱／放走」、
    /// 也没有默认保资源策略，击杀后界面还一直显示「未追回 60 金币」（问题包 8896276f）。
    /// 本夹具注入本体标记来复现这两个语义，不依赖任何第三方程序集。
    /// </remarks>
    private async Task AssertTheftPowerMarkerAsync(CombatState combat, Player player)
    {
        foreach (var power in combat.Creatures.SelectMany(c => c.Powers).ToArray())
            await PowerCmd.Remove(power);
        Creature thief = combat.Enemies[0];
        await CreatureCmd.SetCurrentHp(thief, 200);
        player.Gold = 137;
        ThieveryPower? thievery = await PowerCmd.Apply<ThieveryPower>(
            new ThrowingPlayerChoiceContext(), thief, 30m, thief, null);
        if (thievery is null)
            throw new InvalidOperationException("偷窃标记没有被施加。");
        thievery.Target = player.Creature;
        await thievery.Steal();
        if (player.Gold != 107)
            throw new InvalidOperationException($"偷窃没有扣掉金币：{player.Gold}。");

        // ① 只有本体标记、没有原版盗贼怪物时也必须认成偷窃遭遇。
        if (!TheftEncounterStrategy.IsApplicable(combat))
            throw new InvalidOperationException("带 ThieveryPower 的第三方盗贼没有被认成偷窃遭遇。");
        if (SolverController.ResolveTheftPolicy(combat) != SolverTheftPolicy.PreserveResources)
            throw new InvalidOperationException("偷窃遭遇的默认策略不是保资源。");

        // ② 未登记的第三方盗贼：赃款按原版 ThieveryPower 语义留在场上（不算归还）。
        if (OutstandingStolenGoldAfterKill(combat, player, thief) != 30)
            throw new InvalidOperationException("未登记的盗贼死亡时不该核销赃款。");

        // ③ 登记「死亡归还」后（往昔之章 Looter／Mugger 的 OnDeath 语义）：击杀即核销。
        ThirdPartyAdapterRegistry.RegisterDeathReturnedGold(
            thief.Monster?.GetType().Name
                ?? throw new InvalidOperationException("敌人没有怪物模型。"));
        if (OutstandingStolenGoldAfterKill(combat, player, thief) != 0)
            throw new InvalidOperationException("登记过的盗贼死亡时没有核销赃款。");

        _completedChecks.Add("TheftPowerMarker:ApplicableByNativeMarker:DeathReturnedGoldClearsOutstanding");
    }

    private static int OutstandingStolenGoldAfterKill(CombatState combat, Player player, Creature thief)
    {
        CombatPredictionSimulator simulator = CombatRootSnapshot.Capture(combat).ForkSimulator();
        var shadow = (SimulatedCombatState)simulator.State.CombatState;
        int before = shadow.OutstandingStolenResource(simulator);
        if (before != 30)
            throw new InvalidOperationException($"赃款初始计数不是 30：{before}。");
        simulator.Damage(thief, 999, ValueProp.Unpowered, player.Creature);
        if (!CorePowerSupport.ApplyEnemyDeathPowers(simulator, shadow, shadow.KnownEnemies, new HashSet<uint>()))
            throw new InvalidOperationException("偷窃夹具的死亡结算请求了选择。");
        return shadow.OutstandingStolenResource(simulator);
    }
}
