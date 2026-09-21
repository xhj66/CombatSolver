using CombatSolver.Engine.InCombat.Mirrors.Hooks.Death;

namespace CombatSolver.ActsFromThePastAdapter;

/// <summary>
/// 往昔之章第二幕（The City）怪物的**钩子**：只登记为忽略的那些重写。
/// </summary>
/// <remarks>
/// 判定链看的是 <c>PredictionGap.Method</c> 里是否含 «Death»（见
/// docs/AFTP_ACT4HEART_STATUS.md §3.6），所以任何没登记的死亡钩子都会让整场战斗给不出战损。
/// 两条都逐行反编译确认只有表现层副作用：
/// <list type="bullet">
/// <item><c>SphericGuardian.BeforeDeath</c>：<c>PlayDetectSfx</c>（<c>Rng.Chaotic</c> 抽音效编号）；</item>
/// <item><c>Snecko.BeforeDeath</c>：一句 <c>AFTPModAudio.Play("snecko", "snecko_death")</c>。</item>
/// </list>
/// 两条都没有命令、没有数值／状态／RNG 读写。
/// </remarks>
internal static class CityHooks
{
    public static void Verify()
    {
        AfpReflection.RequireOverride("SphericGuardian", "BeforeDeath", 1);
        AfpReflection.RequireOverride("Snecko", "BeforeDeath", 1);
    }

    public static void RegisterAll()
    {
        BeforeDeathMirrors.RegisterIgnored(AfpReflection.RequireType("ActsFromThePast.SphericGuardian"));
        BeforeDeathMirrors.RegisterIgnored(AfpReflection.RequireType("ActsFromThePast.Snecko"));
    }
}
