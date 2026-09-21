using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Powers;

namespace CombatSolver;

internal enum SolverTheftPolicy
{
    PreserveResources,
    LetEscape,
}

internal static class TheftEncounterStrategy
{
    public static int CompareRecovery(SolverTheftPolicy? policy,
        bool candidateWon, int candidateOutstanding, bool currentWon, int currentOutstanding)
    {
        int victory = currentWon.CompareTo(candidateWon);
        return victory != 0 ? victory : policy == SolverTheftPolicy.PreserveResources
            ? candidateOutstanding.CompareTo(currentOutstanding) : 0;
    }

    public static bool RecoverySatisfied(SolverTheftPolicy? policy, int outstanding)
        => policy != SolverTheftPolicy.PreserveResources || outstanding == 0;

    /// <summary>
    /// 这场战斗是不是「有东西会被偷」的遭遇：按**本体标记**判定，而不是按怪物写死的名单。
    /// </summary>
    /// <remarks>
    /// 原先只认 <c>GremlinMerc</c>／<c>ThievingHopper</c>／<c>FatGremlin</c> 与两个本体遭遇 Id，
    /// 于是用本体 <c>ThieveryPower</c> 偷钱的第三方盗贼（往昔之章的 Looter／Mugger）既不显示
    /// 「保牌/保钱／放走」，也不会默认进入保资源策略——问题包 8896276f 就是这场抢劫的战斗。
    /// 判据改成「场上有人携带偷窃标记」：本体 <c>ThieveryPower</c>（赃款）／<c>HeistPower</c>
    /// （赃款载体）／<c>SwipePower</c>（赃牌）、登记过的第三方偷牌 Power，或登记过的
    /// 「死亡归还金币」怪物（<see cref="ThirdPartyAdapterRegistry.ReturnsStolenGoldOnDeath"/>）。
    /// </remarks>
    public static bool IsApplicable(CombatState state)
    {
        if (state.Encounter?.Id.Entry is "GREMLIN_MERC_NORMAL" or "THIEVING_HOPPER_WEAK")
            return true;
        foreach (Creature creature in state.Creatures)
        {
            if (creature.Monster is GremlinMerc or ThievingHopper or FatGremlin)
                return true;
            if (creature.Monster is { } monster
                && ThirdPartyAdapterRegistry.ReturnsStolenGoldOnDeath(monster.GetType().Name))
            {
                return true;
            }
            foreach (PowerModel power in creature.Powers)
            {
                if (power is ThieveryPower or HeistPower or SwipePower
                    || ThirdPartyAdapterRegistry.IsRegisteredStolenCardPower(power.GetType().Name))
                {
                    return true;
                }
            }
        }
        return false;
    }

    public static int OutstandingStolenResource(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat)
        => combat.OutstandingStolenResource(simulator);
}
