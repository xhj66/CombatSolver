using System.Collections.Concurrent;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Afflictions;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Orbs;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;

namespace CombatSolver.Engine.Common;

/// <summary>
/// <see cref="ModelDb"/> 的泛型查询每次都用正则把类型名 slug 化成 <c>ModelId</c> 再查字典
/// （<c>StringHelper.Slugify</c> 三次 <c>Regex.Replace</c>）。规范实例在 <c>ModelDb.Init</c>
/// 之后不会变化，搜索热路径（<c>SetAmount</c>、<c>Tick</c>、生成牌）按封闭泛型类型缓存即可。
/// 缓存只在首次访问时填充，不在类型初始化器里查询，避免 ModelDb 尚未就绪时把失败固化。
/// </summary>
internal static class CanonicalModels
{
    public static T Power<T>() where T : PowerModel => PowerCache<T>.Value;

    public static T Card<T>() where T : CardModel => CardCache<T>.Value;

    public static T Relic<T>() where T : RelicModel => RelicCache<T>.Value;

    public static T Potion<T>() where T : PotionModel => PotionCache<T>.Value;

    public static T Monster<T>() where T : MonsterModel => MonsterCache<T>.Value;

    public static T Affliction<T>() where T : AfflictionModel => AfflictionCache<T>.Value;

    /// <summary>
    /// 按运行时类型取规范病症实例（第三方适配的登记项用）。外部程序集拿不到泛型形参，核心这边也不能
    /// 用泛型；取不到就在调用点明确失败，而不是让调用方去猜。
    /// </summary>
    public static AfflictionModel Affliction(Type afflictionType)
        => AfflictionTypeCache.GetOrAdd(afflictionType, static type =>
            ModelDb.GetByIdOrNull<AfflictionModel>(ModelDb.GetId(type))
            ?? throw new InvalidOperationException(
                $"{type.FullName} 不在 ModelDb 里（或不是病症），第三方卡牌病症无法实例化。"));

    public static T Enchantment<T>() where T : EnchantmentModel => EnchantmentCache<T>.Value;

    public static T Orb<T>() where T : OrbModel => OrbCache<T>.Value;

    /// <summary>
    /// 第三方类型无法用泛型缓存，按 <see cref="Type"/> 缓存（首次访问时查 <c>ModelDb</c>，
    /// 与泛型入口同一条「不在类型初始化器里查询、避免把 ModelDb 未就绪固化」的纪律）。
    /// </summary>
    private static readonly ConcurrentDictionary<Type, AfflictionModel> AfflictionTypeCache = new();

    private static class PowerCache<T> where T : PowerModel
    {
        private static T? _value;
        public static T Value => _value ??= ModelDb.Power<T>();
    }

    private static class CardCache<T> where T : CardModel
    {
        private static T? _value;
        public static T Value => _value ??= ModelDb.Card<T>();
    }

    private static class RelicCache<T> where T : RelicModel
    {
        private static T? _value;
        public static T Value => _value ??= ModelDb.Relic<T>();
    }

    private static class PotionCache<T> where T : PotionModel
    {
        private static T? _value;
        public static T Value => _value ??= ModelDb.Potion<T>();
    }

    private static class MonsterCache<T> where T : MonsterModel
    {
        private static T? _value;
        public static T Value => _value ??= ModelDb.Monster<T>();
    }

    private static class AfflictionCache<T> where T : AfflictionModel
    {
        private static T? _value;
        public static T Value => _value ??= ModelDb.Affliction<T>();
    }

    private static class EnchantmentCache<T> where T : EnchantmentModel
    {
        private static T? _value;
        public static T Value => _value ??= ModelDb.Enchantment<T>();
    }

    private static class OrbCache<T> where T : OrbModel
    {
        private static T? _value;
        public static T Value => _value ??= ModelDb.Orb<T>();
    }
}
