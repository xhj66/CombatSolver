using MegaCrit.Sts2.Core.Modding;

namespace CombatSolver.ActsFromThePastAdapter;

/// <summary>
/// 求解器 · 往昔之章适配的入口。
/// </summary>
/// <remarks>
/// 登记纪律（docs/THIRD_PARTY_ADAPTERS.md §3.1／§3.2）：
/// <list type="bullet">
/// <item>全部登记在 Mod 初始化期间一次做完，搜索期间不再动登记表；</item>
/// <item>自检不通过就**一个条目都不登记**——装一半比不装更糟，求解器会拿着一部分正确的
/// 数据给出看起来可信的路线。</item>
/// </list>
/// 自检内容：往昔之章程序集是否在场、要接管的怪物类型是否存在、适配层钉死的常量是否
/// 与对方源码里的一致。
/// </remarks>
[ModInitializer(nameof(Initialize))]
public static class AdapterEntry
{
    public const string ModId = "CombatSolver-AFTP";

    public static void Initialize()
    {
        try
        {
            ExordiumBranchResolvers.Verify();
            ExordiumMoveEffects.Verify();
            ExordiumHooks.Verify();
            ExordiumStableAttacks.Verify();
            LaterActsStableAttacks.Verify();
            ExordiumCardPatches.Verify();
        }
        catch (Exception ex)
        {
            Log($"[{ModId}] 自检失败，本次不登记任何条目：{ex.Message}");
            return;
        }

        ExordiumBranchResolvers.RegisterAll();
        ExordiumMoveEffects.RegisterAll();
        ExordiumHooks.RegisterAll();
        ExordiumStableAttacks.RegisterAll();
        LaterActsStableAttacks.RegisterAll();
        ExordiumCardPatches.RegisterAll();
        Log(
            $"[{ModId}] 往昔之章适配已登记："
            + $"行动分支 {ExordiumBranchResolvers.RegisteredMonsterTypes.Length} 个怪物、"
            + $"常量构造攻击 {ExordiumStableAttacks.RegisteredAttackCount + LaterActsStableAttacks.RegisteredAttackCount} 条"
            + $"（第一幕 {ExordiumStableAttacks.RegisteredAttackCount}／第二三幕 {LaterActsStableAttacks.RegisteredAttackCount}）、"
            + "行动效果见 ExordiumMoveEffects、能力与死亡钩子见 ExordiumHooks、"
            + "本体卡牌补丁见 ExordiumCardPatches（Slimed 的 classic 出牌补丁）。");
    }

    private static void Log(string message)
    {
        try
        {
            CombatSolver.Entry.Logger?.Info(message);
            return;
        }
        catch (Exception)
        {
            // 求解器尚未初始化时退回 Godot 控制台。
        }
        Godot.GD.Print(message);
    }
}
