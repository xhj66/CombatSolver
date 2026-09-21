using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Death;

namespace CombatSolver.Act4HeartAdapter;

/// <summary>
/// 心脏适配：把「人工复核过、对预测无影响」的第三方钩子覆写登记为忽略。
/// </summary>
/// <remarks>
/// 求解器的镜像注册表按**精确运行时类型**分派，没登记的重写会落到 <c>Unsupported</c> 并记一条
/// <c>MethodNotMirrored</c> 风险。风险不是静默错误，但它的代价比「显示一条红字」大得多：
/// 只要那条 gap 的方法名里带 «Death»，<c>CombatBeamSolver</c> 就不承认这一局已经打赢
/// （<c>uncertainVictory</c> 会把搜索边界改写成 <c>UnsupportedEffect</c>），于是战斗永远不判结束，
/// 界面上「预计战损」只能显示「未知」。所以**确实没有玩法影响**的重写必须显式声明为忽略，
/// 而不是留在风险里（见 docs/THIRD_PARTY_ADAPTERS.md §1.2 的 <c>Ignored</c> 一行）。
///
/// <para>
/// <b>腐化心脏的 <c>AfterDeath</c>（Act4Heart 1.1.7，反编译逐行核对）。</b>整段只有一个分支：
/// 死者是这颗心脏且未被移除拦截时，取 <c>NRunMusicController.Instance</c>、调一次 <c>UpdateMusic()</c>，
/// 然后返回已完成的 <c>Task</c>。没有命令、没有数值、没有 RNG、没有生命／格挡／能力的读写。
/// 背景音乐不在预测范围内，所以这条重写对预测的确切影响是「无」。
/// </para>
///
/// <para>
/// <b>这是人工复核结论，不是运行期可判定的性质。</b>自检只能确认「该重写仍在、签名逐参数相符」，
/// 证明不了方法体没变；挡在中间的是清单里 <c>Act4Heart ≥ 1.1.7</c> 的版本门。对方若给这个钩子加上
/// 玩法效果而签名不变，需要重新复核——原生那批 <c>RegisterIgnored</c> 条目也是同一性质的结论。
/// </para>
/// </remarks>
internal static class HeartIgnoredHooks
{
    /// <summary>已复核为只换背景音乐的重写：腐化心脏的 <c>AfterDeath</c>。</summary>
    private const string CorruptHeartType = "CorruptHeart";

    public static void Verify()
    {
        Type heart = A4hReflection.RequireMonsterType(CorruptHeartType);
        A4hReflection.RequireOverride(
            heart,
            nameof(AbstractModel.AfterDeath),
            typeof(PlayerChoiceContext),
            typeof(Creature),
            typeof(bool),
            typeof(float));
    }

    public static void RegisterAll()
        => AfterDeathMirrors.RegisterIgnored(A4hReflection.RequireMonsterType(CorruptHeartType));
}
