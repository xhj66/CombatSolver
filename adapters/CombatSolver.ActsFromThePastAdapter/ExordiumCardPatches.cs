using System.Reflection;
using CombatSolver.Engine.InCombat.Mirrors.Cards.OnPlay;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models.Cards;

namespace CombatSolver.ActsFromThePastAdapter;

/// <summary>
/// 往昔之章打在**本体卡牌**上的那一个 <c>OnPlay</c> 补丁的适配。
/// </summary>
/// <remarks>
/// 求解器把「被第三方改写过 OnPlay 的卡牌」一律当成不可预测：<c>CardOnPlayInferrer</c>
/// 读的是**补丁前**的原始 IL，照它算出来的路线会是一张游戏已经不那么打的牌
/// （见 <c>PredictionModPatchAudit</c> 的说明）。往昔之章的
/// <c>ClassicSlimedOnPlayPatch</c> 正补在 <c>Slimed.OnPlay</c> 上，所以只要装了往昔之章，
/// 不登记这个合成语义，求解器就会拒绝整场战斗。
///
/// 补丁的语义是**逐卡**的（见 <see cref="ClassicSlimed"/>）：
/// <code>
/// if (!ClassicSlimedTracker.IsClassicSlimed.Get(card)) return true;  // 本体：原版抽 1 张
/// ...VFX...; __result = Task.CompletedTask; return false;            // 经典：跳过原版，什么都不做
/// </code>
/// 本体实现是「放一次视觉 + 抽 <c>DynamicVars.Cards</c> 张牌」（<c>Slimed</c> 是 1 费、消耗、
/// 状态牌，抽牌数恒为 1）。视觉不在预测范围内，这里只复刻抽牌。
///
/// 经典与否**不在状态指纹里**（逐卡状态没有进入指纹的通道，见 docs/third-party-model-state.md
/// 「范围」）。本适配的前提是同一场战斗里史莱姆只有一个来源，因此出牌时只在两种类别
/// **确实并存**的情况下拒绝（见 <see cref="ClassicSlimed.RejectCoexistingClasses"/>）——
/// 异常会中止整次求解，不会把一条错路线交出去。
///
/// 判定**逐张看来源**，不拿配置去反推：<c>LegacyEnemiesGiveClassicSlimed</c> 这个开关只作用于
/// 往昔之章**自己的怪**。心脏等其它来源产出的史莱姆是本体语义，不跨 Mod 生效，不能登记成经典。
/// </remarks>
internal static class ExordiumCardPatches
{
    private const string PatchTypeName = "ActsFromThePast.Patches.Cards.ClassicSlimedOnPlayPatch";

    /// <summary>
    /// 已复核的 Harmony 组合身份。往昔之章在 <c>ActsFromThePastInitializer</c> 里用
    /// <c>new Harmony("actsfromthepast.actsfromthepast")</c> 加 <c>PatchAll</c> 打补丁，
    /// 该补丁没有声明 <c>[HarmonyPriority]</c>、也没有 before/after，因此是默认的
    /// <c>Priority.Normal</c> 且前后顺序为空。身份变了就意味着组合变了，必须重新核对。
    /// </summary>
    private const string HarmonyOwner = "actsfromthepast.actsfromthepast";

    private const string Schema = "aftp-classic-slimed-v1";

    private static MethodInfo _prefix = null!;

    /// <summary>初始化自检：补丁类型、补丁方法签名、以及判据用到的标记表与配置项都还在。</summary>
    public static void Verify()
    {
        Type patchType = AfpReflection.RequireType(PatchTypeName);
        _prefix = patchType.GetMethod("Prefix", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"{PatchTypeName}.Prefix 不存在。");
        ParameterInfo[] parameters = _prefix.GetParameters();
        if (_prefix.ReturnType != typeof(bool)
            || parameters.Length != 4
            || parameters[0].ParameterType != typeof(Slimed)
            || parameters[1].ParameterType != typeof(PlayerChoiceContext)
            || parameters[2].ParameterType != typeof(CardPlay)
            || parameters[3].ParameterType != typeof(Task).MakeByRefType())
        {
            throw new InvalidOperationException(
                $"{PatchTypeName}.Prefix 的签名已变动，史莱姆的合成语义需要重新核对。");
        }

        ClassicSlimed.Verify();
        RejectChangedComposition();
    }

    /// <summary>
    /// 若补丁此刻已经打上，就地比对真实组合是否与已复核的一致。
    /// </summary>
    /// <remarks>
    /// 登记用的是**钉死**的组合：求解器在每次根捕获时都会把当场的真实组合与登记项逐字节对齐，
    /// 不一致就抛 <c>PredictionUnsupportedException</c>（关死）。这里只是把同一个判定提前到
    /// 初始化，好让日志里出现一条说得清的失败原因。
    /// 往昔之章与本适配的初始化先后不作保证，因此补丁还没打上时不作判断。
    /// </remarks>
    private static void RejectChangedComposition()
    {
        MethodInfo target = AdaptedCardOnPlayMirrors.ResolveOnPlay(typeof(Slimed))
            ?? throw new InvalidOperationException("本体 Slimed.OnPlay 不存在。");
        Patches? live = Harmony.GetPatchInfo(target);
        if (live is null)
            return;

        if (live.InnerPrefixes.Count != 0 || live.InnerPostfixes.Count != 0
            || live.Prefixes.Count != 1 || live.Postfixes.Count != 0
            || live.Transpilers.Count != 0 || live.Finalizers.Count != 0)
        {
            throw new InvalidOperationException(
                "Slimed.OnPlay 上的补丁组合与已复核的不一致（期望恰好一个 Prefix，且没有其它修补）。");
        }

        Patch prefix = live.Prefixes[0];
        if (prefix.PatchMethod != _prefix
            || !string.Equals(prefix.owner, HarmonyOwner, StringComparison.Ordinal)
            || prefix.priority != Priority.Normal
            || prefix.before.Length != 0
            || prefix.after.Length != 0)
        {
            throw new InvalidOperationException(
                "Slimed.OnPlay 上的 Prefix 身份、优先级或前后顺序已变动，史莱姆适配需要重新核对。");
        }
    }

    public static void RegisterAll()
    {
        MethodInfo target = AdaptedCardOnPlayMirrors.ResolveOnPlay(typeof(Slimed))
            ?? throw new InvalidOperationException("本体 Slimed.OnPlay 不存在。");
        AdaptedCardOnPlayMirrors.Register<Slimed>(
            Schema,
            target,
            [new(HarmonyPatchType.Prefix, _prefix, HarmonyOwner, Priority.Normal, [], [])],
            static (card, context) => PlaySlimed(card, context));
    }

    /// <summary>本体 <c>Slimed.OnPlay</c> 与往昔之章 classic 补丁的合成语义。</summary>
    private static void PlaySlimed(Slimed card, CardOnPlayMirrorContext context)
    {
        bool classic = ClassicSlimed.IsClassic(context.Card.Original);
        ClassicSlimed.RejectCoexistingClasses(context.State, classic);

        // 经典：只播一次视觉，不抽牌。视觉不在预测范围内。
        if (classic)
            return;

        context.Simulator.Draw(card.Owner, card.DynamicVars.Cards.BaseValue);
    }
}
