using MegaCrit.Sts2.Core.Modding;

namespace CombatSolver;

/// <summary>
/// 声明依赖被拒 Mod、但本次**没有加载**的求解器适配 Mod。
/// </summary>
/// <remarks>
/// 适配 Mod 是独立 Mod：它登记的那些合成语义只在自己加载时才存在。被停用、被重复来源顶掉或
/// 加载失败时，被打补丁的镜像 OnPlay 会照常被拒，而日志与界面只会说「不兼容的第三方 Mod」——
/// 真正的原因是**适配没加载**，玩家却会去卸载那个无辜的玩法 Mod。这个提示把缺的那一环说出来，
/// 由 <c>PredictionModPatchAudit</c> 按 Mod 清单声明的依赖关系查得，不硬编码任何 Mod 名。
/// </remarks>
internal sealed record UnloadedAdapterHint(string AdapterName, string AdapterId, ModLoadState LoadState)
{
    public string PlayerFacingAdapter
        => string.IsNullOrWhiteSpace(AdapterName)
            || string.Equals(AdapterName, AdapterId, StringComparison.OrdinalIgnoreCase)
            ? AdapterId
            : $"{AdapterName}（{AdapterId}）";

    /// <summary>日志用的描述，与项目里其它审计消息同语言。</summary>
    public string DescribeLoadStateForLog() => LoadState switch
    {
        ModLoadState.Disabled => "disabled in the mod settings",
        ModLoadState.DisabledDuplicate => "disabled because the same mod is installed twice",
        ModLoadState.Failed => "it failed to load",
        ModLoadState.AddedAtRuntime => "added after startup",
        _ => "not loaded",
    };

    /// <summary>界面用的描述。</summary>
    public string DescribeLoadStateForPlayer() => LoadState switch
    {
        ModLoadState.Disabled => "在 Mod 设置里被停用",
        ModLoadState.DisabledDuplicate => "因同一 Mod 装了两份而被停用",
        ModLoadState.Failed => "加载失败（见 godot.log）",
        ModLoadState.AddedAtRuntime => "是启动之后才加入的",
        _ => "没有加载",
    };

    /// <summary>
    /// 接在任何「为什么被拒」句子后面的补充说明（英文）。异常的 Message 与推迟到首次使用才报的
    /// 拒绝原因共用这一句，免得两处措辞走散。
    /// </summary>
    public string DescribeLogSuffix()
        => " A mod that adapts it for the solver is installed but was not loaded:"
            + $" {PlayerFacingAdapter} ({DescribeLoadStateForLog()})."
            + " The adapter must be enabled in the mod settings and the game restarted.";
}

internal sealed class IncompatibleGameplayModException : NotSupportedException
{
    public string ModId { get; }
    public string ModName { get; }

    /// <summary>What was found, e.g. a ModHelper subscriber type or a Harmony patch on a mirrored method.</summary>
    public string Subject { get; }

    /// <summary>Where it was found, e.g. <c>run</c> or <c>combat</c>.</summary>
    public string Scope { get; }

    /// <summary>
    /// 声明依赖该 Mod、但本次没有加载的求解器适配 Mod。非 null 时，「拒绝」其实是因为适配没启用，
    /// 界面与日志都应改口说「去启用适配 Mod」，而不是笼统地建议卸载。
    /// </summary>
    public UnloadedAdapterHint? UnloadedAdapter { get; }

    public IncompatibleGameplayModException(
        string modId,
        string modName,
        string subject,
        string scope,
        UnloadedAdapterHint? unloadedAdapter = null)
        : base(BuildMessage(modId, modName, subject, scope, unloadedAdapter))
    {
        ModId = modId;
        ModName = modName;
        Subject = subject;
        Scope = scope;
        UnloadedAdapter = unloadedAdapter;
    }

    public string PlayerFacingModName => DescribeMod(ModName, ModId);

    private static string BuildMessage(
        string modId,
        string modName,
        string subject,
        string scope,
        UnloadedAdapterHint? unloadedAdapter)
        => $"Unsupported gameplay {scope} extension {subject} from mod {DescribeMod(modName, modId)}."
            + (unloadedAdapter?.DescribeLogSuffix() ?? string.Empty);

    private static string DescribeMod(string modName, string modId)
    {
        if (string.IsNullOrWhiteSpace(modName))
            return modId;
        return string.Equals(modName, modId, StringComparison.OrdinalIgnoreCase)
            ? modName
            : $"{modName}（{modId}）";
    }
}
