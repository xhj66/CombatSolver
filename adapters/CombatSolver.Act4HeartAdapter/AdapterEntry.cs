using MegaCrit.Sts2.Core.Modding;

namespace CombatSolver.Act4HeartAdapter;

/// <summary>
/// 求解器 · 心脏适配的入口。
/// </summary>
/// <remarks>
/// 登记纪律（docs/THIRD_PARTY_ADAPTERS.md §3.1／§3.2）：
/// <list type="bullet">
/// <item>全部登记在 Mod 初始化期间一次做完，搜索期间不再动登记表；</item>
/// <item>自检不通过就**一个条目都不登记**——装一半比不装更糟，求解器会拿着一部分正确的数据
/// 给出看起来可信的路线。</item>
/// </list>
/// 自检内容：Act4Heart 程序集是否在场、要接管的怪物与 Power 类型是否存在、镜像的虚方法是否都被重写、
/// 适配层钉死的常量与反射路径是否仍然成立、以及订阅者放行名单是否与实机一致。
/// </remarks>
[ModInitializer(nameof(Initialize))]
public static class AdapterEntry
{
    public const string ModId = "CombatSolver-Heart";

    public static void Initialize()
    {
        try
        {
            HeartSubscribers.Verify();
            HeartBranchResolvers.Verify();
            HeartMoveEffects.Verify();
            HeartPowers.Verify();
            HeartIgnoredHooks.Verify();
        }
        catch (Exception ex)
        {
            Log($"[{ModId}] 自检失败，本次不登记任何条目：{ex.Message}");
            return;
        }

        HeartSubscribers.RegisterAll();
        HeartBranchResolvers.RegisterAll();
        HeartMoveEffects.RegisterAll();
        HeartPowers.RegisterAll();
        HeartIgnoredHooks.RegisterAll();

        string gaps = HeartPowers.KnownGaps.Length == 0
            ? string.Empty
            : "；已知缺口：" + string.Join("；", HeartPowers.KnownGaps);
        Log(
            $"[{ModId}] 心脏适配已登记："
            + $"{HeartBranchResolvers.RegisteredMonsterTypes.Length} 个怪物的 POST_ATTACK_BRANCH、"
            + "三名怪物各自的行动效果与冻结数值见 HeartMoveEffects，"
            + "死亡节拍／无敌／金属化三个 Power 见 HeartPowers，"
            + "腐化心脏那条只换音乐的死后钩子见 HeartIgnoredHooks，"
            + $"订阅者放行名单见 HeartSubscribers{gaps}");
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
