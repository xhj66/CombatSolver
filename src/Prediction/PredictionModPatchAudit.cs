using System.Reflection;
using CombatSolver.Engine.Common;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;

namespace CombatSolver;

/// <summary>
/// Root-capture guard against third-party Harmony patches that replace gameplay behavior the engine mirrors.
/// </summary>
/// <remarks>
/// Mirrors read live model data, so third-party patches to canonical data (energy cost, dynamic vars, keywords,
/// rarity) are followed automatically except for explicitly rejected gameplay mods. A replaced <see cref="CardModel.OnPlay"/>
/// is different in kind: <c>CardOnPlayInferrer</c> reads the original, unpatched IL by design, and the
/// bespoke mirrors are keyed on the vanilla card type. The engine therefore keeps executing the vanilla recipe it
/// was written against and silently produces a route for a card the game no longer plays that way, which the
/// project's "unknown semantics must fail explicitly" constraint forbids.
/// </remarks>
internal static class PredictionModPatchAudit
{
    private static readonly string[] IncompatibleModIds = ["WheelchairSpire", "PengoTarot", "BetterCharacterRelics"];

    internal readonly record struct ForeignPatch(string ModId, string ModName, string Description);

    /// <summary>
    /// Throws when any card reachable from the captured root has a third-party patch on its mirrored OnPlay.
    /// </summary>
    /// <remarks>
    /// This is a best-effort boundary: card types that only appear later through in-combat generation are not
    /// visible at capture time and are not audited here.
    /// </remarks>
    public static void ValidateCardOnPlay(IEnumerable<CardModel> cards)
        => CaptureCardOnPlay(cards);

    internal static AdaptedOnPlaySnapshot? CaptureCardOnPlay(IEnumerable<CardModel> cards)
    {
        ValidateLoadedMods(ModManager.GetLoadedMods());
        bool adapted = AdaptedCardOnPlayMirrors.Seal();
        Dictionary<Type, AdaptedCardOnPlayMirrors.Registration?>? selections = adapted ? [] : null;
        HashSet<Type> checkedTypes = [];
        foreach (CardModel card in cards)
        {
            // Harmony patches can be installed or removed between root captures.
            Type type = card.GetType();
            if (!checkedTypes.Add(type)) continue;
            AdaptedCardOnPlayMirrors.Registration? selected =
                AuditCardOnPlay(type, adapted, out ForeignPatch? firstForeign);
            if (selected is null && firstForeign is { } unsupported)
                throw new IncompatibleGameplayModException(unsupported.ModId, unsupported.ModName,
                    unsupported.Description, "combat", DescribeUnloadedAdapter(unsupported.ModId));
            selections?.Add(type, selected);
        }
        if (selections is null)
            return null;

        Dictionary<Type, string> deferredFailures = [];
        foreach (Type type in AdaptedCardOnPlayMirrors.RegisteredTypes())
        {
            if (!checkedTypes.Add(type)) continue;
            try
            {
                AdaptedCardOnPlayMirrors.Registration? selected =
                    AuditCardOnPlay(type, adapted: true, out ForeignPatch? firstForeign);
                if (selected is null && firstForeign is { } unsupported)
                    deferredFailures.Add(type, $"{unsupported.ModName} ({unsupported.ModId}) patches the OnPlay of "
                        + $"{type.FullName} without a matching adapter: {unsupported.Description}."
                        + (DescribeUnloadedAdapter(unsupported.ModId)?.DescribeLogSuffix() ?? string.Empty));
                else
                    selections.Add(type, selected);
            }
            catch (PredictionUnsupportedException error)
            {
                // The type is not reachable from this root. Keep its exact rejection for first use.
                deferredFailures.Add(type, error.Message);
            }
        }
        HashSet<MethodInfo> patchedOnPlayTargets = [];
        string stamp = AdaptedCardOnPlayMirrors.CaptureLiveStamp(patchedOnPlayTargets)!;
        return new(selections, stamp, patchedOnPlayTargets, deferredFailures);
    }

    /// <summary>
    /// Audits one card type exactly the way root capture does, leaving the caller to decide what a foreign
    /// patch means at that point.
    /// </summary>
    internal static AdaptedCardOnPlayMirrors.Registration? AuditCardOnPlay(
        Type type, bool adapted, out ForeignPatch? firstForeign)
    {
        MethodInfo target = AdaptedCardOnPlayMirrors.ResolveOnPlay(type)
            ?? throw new PredictionUnsupportedException($"Missing OnPlay for {type.FullName}.");
        Patches? patches = Harmony.GetPatchInfo(target);
        firstForeign = null;
        if (patches is not null)
            foreach (var group in AdaptedCardOnPlayMirrors.Groups(patches))
                foreach (Patch patch in group.Patches)
                {
                    // Resolve every source even when the full combination is registered.
                    ForeignPatch? foreign = TryDescribeForeignPatch(patch, target);
                    firstForeign ??= foreign;
                }
        return adapted ? AdaptedCardOnPlayMirrors.Select(type, target, patches) : null;
    }

    internal static void ValidateLoadedMods(IEnumerable<Mod> mods)
    {
        foreach (Mod mod in mods)
        {
            string? incompatibleId = IncompatibleModIds.FirstOrDefault(id =>
                string.Equals(mod.manifest?.id, id, StringComparison.OrdinalIgnoreCase)
                || mod.assemblies.Any(assembly => string.Equals(
                    assembly.GetName().Name, id, StringComparison.OrdinalIgnoreCase)));
            if (incompatibleId is null)
                continue;
            throw new IncompatibleGameplayModException(
                mod.manifest?.id ?? string.Empty,
                mod.manifest?.name ?? incompatibleId,
                $"{incompatibleId} gameplay changes",
                "combat");
        }
    }

    /// <summary>
    /// 找出「同时依赖求解器与 <paramref name="foreignModId"/>、但本次没有加载」的求解器适配 Mod。
    /// </summary>
    /// <remarks>
    /// 适配 Mod 是独立 Mod，它登记的那些合成语义只在自己加载时才存在。被停用、被重复来源顶掉或
    /// 加载失败时，被打补丁的镜像 OnPlay 会照常被拒——可日志与界面只会说「不兼容的第三方 Mod」，
    /// 看不出真正的原因是**适配没加载**，玩家于是去卸载那个无辜的玩法 Mod。
    /// 这里按 Mod 清单声明的依赖关系把缺的那一环补上，不硬编码任何 Mod 名：之所以要求它**同时**
    /// 依赖求解器（<see cref="Entry.ModId"/>）与被拒 Mod，是为了不把「碰巧依赖同一个玩法 Mod 的
    /// 其它停用 Mod」误报成适配；代价是适配若没声明对求解器的依赖就得不到提示（只是少一条提示，
    /// 不影响拒绝本身）。
    /// 数据源是 <see cref="ModManager.Mods"/>，它含被跳过的 Mod（状态为
    /// <see cref="ModLoadState.Disabled"/> 等），而 <see cref="ModManager.GetLoadedMods"/> 只有已加载的。
    /// </remarks>
    private static UnloadedAdapterHint? DescribeUnloadedAdapter(string foreignModId)
    {
        foreach (Mod mod in ModManager.Mods)
        {
            if (mod.state == ModLoadState.Loaded)
                continue;
            ModManifest? manifest = mod.manifest;
            if (manifest?.id is not { Length: > 0 } adapterId
                || string.Equals(adapterId, foreignModId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            // 适配 Mod 的判据：**同时**依赖求解器本身与被拒的那个 Mod。只依赖被拒 Mod 的停用
            // Mod（例如某个玩法扩展）不是适配，不能拿来当解释。
            if (manifest.dependencies is not { } dependencies)
                continue;
            bool dependsOnSolver = false;
            bool dependsOnForeignMod = false;
            foreach (ModDependency dependency in dependencies)
            {
                dependsOnSolver |= string.Equals(dependency.id, Entry.ModId, StringComparison.OrdinalIgnoreCase);
                dependsOnForeignMod |= string.Equals(
                    dependency.id, foreignModId, StringComparison.OrdinalIgnoreCase);
            }
            if (!dependsOnSolver || !dependsOnForeignMod)
                continue;
            return new UnloadedAdapterHint(manifest.name ?? string.Empty, adapterId, mod.state);
        }
        return null;
    }

    private static ForeignPatch? TryDescribeForeignPatch(Patch patch, MethodInfo target)
    {
        Type? patchType = patch.PatchMethod.DeclaringType;
        if (patchType == null)
            throw new PredictionUnsupportedException(
                $"Unknown Harmony patch {patch.PatchMethod} (owner={patch.owner}) on {target}.");

        var mod = AssemblyInfo.ModForType(patchType, out bool isBaseGame);
        if (isBaseGame)
            return null;
        // Same policy as the ModHelper subscriber audit: mods that declare themselves gameplay-neutral are trusted.
        if (mod?.manifest?.affectsGameplay is false)
            return null;
        if (mod?.manifest?.id is not { Length: > 0 } modId)
            throw new PredictionUnsupportedException(
                $"Unknown Harmony patch {patchType.FullName}.{patch.PatchMethod.Name} " +
                $"(owner={patch.owner}) on mirrored {target.DeclaringType?.FullName}.{target.Name}.");
        if (string.Equals(modId, Entry.ModId, StringComparison.OrdinalIgnoreCase))
            return null;

        return new ForeignPatch(
            modId,
            mod.manifest.name ?? string.Empty,
            $"Harmony patch {patchType.FullName}.{patch.PatchMethod.Name} on mirrored "
            + $"{target.DeclaringType?.FullName}.{target.Name}");
    }
}
