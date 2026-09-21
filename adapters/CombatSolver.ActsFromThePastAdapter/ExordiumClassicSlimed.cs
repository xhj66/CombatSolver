using System.Reflection;
using System.Runtime.CompilerServices;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;

namespace CombatSolver.ActsFromThePastAdapter;

/// <summary>
/// 「经典史莱姆」的判定：往昔之章打在每张牌上的标记，加上适配层自己在预测里生成的那些。
/// </summary>
/// <remarks>
/// 往昔之章把史莱姆分成两种：**本体史莱姆**出牌抽 1 张，**经典史莱姆**出牌什么都不做
/// （只播一次视觉）。区别记在 <c>ClassicSlimedTracker.IsClassicSlimed</c> 上——一个
/// <c>SpireField&lt;CardModel, bool&gt;</c>，也就是**按卡牌实例**索引的弱引用表，默认 <c>false</c>。
/// 往昔之章的 <c>TagClassicSlimedPatch</c> 在 <c>CardModel.ToMutable</c> 的后缀里给新实例打标，
/// 而 <c>CreatingClassicSlimed</c> 只在怪物「把史莱姆塞进牌堆」的行动期间、被设为
/// <c>ActsFromThePastConfig.LegacyEnemiesGiveClassicSlimed</c>。
///
/// **这个开关只作用于往昔之章自己的怪，不跨 Mod 生效。** 它唯一的作用点是往昔之章怪物行动里
/// 那两句 <c>CardPileCmd.AddToCombatAndPreview&lt;Slimed&gt;</c>；本体、其它 Mod（例如心脏——
/// 往昔之章没做第四层，心脏不是它的怪）产出的史莱姆都不经过那里，一律是本体语义。
/// 所以**不能拿配置去反推一张已有牌的类别**，只能逐张看它自己的来源。
///
/// 求解器预测时会把卡牌克隆成新实例（<see cref="PredictedCard"/> 的 Preview 走
/// <c>MemberwiseClone</c>），而 <c>SpireField</c> 按实例索引、往昔之章也没有调用
/// <c>CopyOnClone()</c>，所以**标记不会跟着克隆走**。判据因此分两路：
/// <list type="bullet">
/// <item>实机已有的牌：读往昔之章的标记（只读，见下）；</item>
/// <item>求解器在预测里生成的牌：由适配层在生成时登记（<see cref="RecordGenerated"/>），
/// 那几个生成点全在往昔之章的怪物行动里。</item>
/// </list>
/// 两路都以 <c>PredictedCard.Original</c> 为键——它在 Fork 之间保持不变（<c>PredictedCard.Fork</c>
/// 无论是否隔离已挂模型都保留原 <c>Original</c>），Preview 则会换新实例。
///
/// **为什么不调用 <c>SpireField.Get</c>。** BaseLib 的实现是「先查后插」：
/// <code>
/// if (_table.TryGetValue(obj, out value)) return value;
/// _table.Add(obj, value = _defaultVal(obj));   // 命中不了就写表，且不是 AddOrUpdate
/// </code>
/// 求解器是并行展开的（<c>searchMaxDegreeOfParallelism</c> 默认 4）。两路 worker 同时首次
/// 触到同一张牌时会各自走到 <c>Add</c>，后到的那个抛
/// <c>ArgumentException: An item with the same key has already been added</c>——整次搜索因此中止，
/// 界面拿不到任何路线，也就**给不出战损**。而且那一下是**往对方 Mod 的 live 状态里写**，
/// 违反 `docs/third-party-onplay-patches.md`「写入只通过分支状态或 MutablePreview」。实测证据见
/// `docs/AFTP_ACT4HEART_STATUS.md` §2.6。
///
/// 因此这里直接绑 <c>_table</c> 的 <c>TryGetValue</c>：**纯读、不插入、无竞态**，缺席时用
/// <c>SpireField</c> 自己的默认值取值——与 <c>Get</c> 的返回值逐字等价，只是不把默认值缓存进对方的表。
/// 这个标记在卡牌创建时一次性写好、此后不变，且以卡牌实机身份为键，所以 worker 读它**在 Fork 之间
/// 是确定的**（准则禁止的是捕获随分支变化的 live 状态）。
/// </remarks>
internal static class ClassicSlimed
{
    private const string TrackerTypeName = "ActsFromThePast.ClassicSlimedTracker";
    private const string TrackerFieldName = "IsClassicSlimed";
    private const string ConfigTypeName = "ActsFromThePast.ActsFromThePastConfig";
    private const string ConfigMemberName = "LegacyEnemiesGiveClassicSlimed";

    /// <summary>BaseLib <c>SpireField&lt;TKey, TVal&gt;</c> 的内部表字段名。</summary>
    private const string TableFieldName = "_table";

    /// <summary>BaseLib <c>SpireField&lt;TKey, TVal&gt;</c> 的默认值字段名。</summary>
    private const string DefaultFieldName = "_defaultVal";

    /// <summary><c>ConditionalWeakTable&lt;CardModel, object&gt;.TryGetValue</c> 的绑定签名。</summary>
    private delegate bool TryReadTag(CardModel key, out object? value);

    private static TryReadTag _tryReadTag = null!;
    private static Func<CardModel, bool> _readDefault = null!;
    private static PropertyInfo _configProperty = null!;
    private static bool? _configValue;

    /// <summary>适配层在预测里生成的史莱姆，按卡牌实机身份登记其类别。</summary>
    private static readonly ConditionalWeakTable<CardModel, object> GeneratedCards = new();

    private static readonly object ClassicMarker = new();
    private static readonly object PlainMarker = new();

    /// <summary>
    /// 初始化自检：标记表、只读访问入口与配置项都还在，且形状没变。任何一项变了就抛异常，
    /// 由调用方放弃整份适配。
    /// </summary>
    public static void Verify()
    {
        Type trackerType = AfpReflection.RequireType(TrackerTypeName);
        FieldInfo tagField = trackerType.GetField(
                TrackerFieldName,
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"{TrackerTypeName}.{TrackerFieldName} 不存在。");
        object tracker = tagField.GetValue(null)
            ?? throw new InvalidOperationException($"{TrackerTypeName}.{TrackerFieldName} 尚未初始化。");

        BindReadOnlyTag(tracker);
        BindConfig();
    }

    /// <summary>把标记表的**只读**入口与默认值入口绑成委托。</summary>
    private static void BindReadOnlyTag(object tracker)
    {
        Type fieldType = tracker.GetType();
        FieldInfo tableField = fieldType.GetField(
                TableFieldName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                $"{TrackerTypeName}.{TrackerFieldName} 不再是 BaseLib SpireField（找不到 {TableFieldName}）。");
        Type tableType = tableField.FieldType;
        if (!tableType.IsGenericType
            || tableType.GetGenericTypeDefinition() != typeof(ConditionalWeakTable<,>)
            || tableType.GetGenericArguments() is not [Type keyType, Type valueType]
            || keyType != typeof(CardModel)
            || valueType != typeof(object))
        {
            throw new InvalidOperationException(
                $"{TrackerTypeName}.{TrackerFieldName}.{TableFieldName} 不再是 "
                + $"ConditionalWeakTable<CardModel, object>，标记表的形状已变动。");
        }

        object table = tableField.GetValue(tracker)
            ?? throw new InvalidOperationException($"{TrackerTypeName}.{TrackerFieldName}.{TableFieldName} 为空。");
        MethodInfo tryGetValue = tableType.GetMethod(
                "TryGetValue",
                BindingFlags.Instance | BindingFlags.Public,
                binder: null,
                types: [typeof(CardModel), typeof(object).MakeByRefType()],
                modifiers: null)
            ?? throw new InvalidOperationException(
                $"{TrackerTypeName}.{TrackerFieldName}.{TableFieldName} 不再是可按卡牌读取的表。");
        if (tryGetValue.ReturnType != typeof(bool))
            throw new InvalidOperationException(
                $"{TrackerTypeName}.{TrackerFieldName}.{TableFieldName}.TryGetValue 不再返回 bool。");
        try
        {
            _tryReadTag = (TryReadTag)tryGetValue.CreateDelegate(typeof(TryReadTag), table);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"{TrackerTypeName}.{TrackerFieldName} 的只读入口无法绑定成 TryReadTag：{ex.Message}",
                ex);
        }

        FieldInfo defaultField = fieldType.GetField(
                DefaultFieldName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                $"{TrackerTypeName}.{TrackerFieldName} 不再是 BaseLib SpireField（找不到 {DefaultFieldName}）。");
        if (defaultField.GetValue(tracker) is not Func<CardModel, bool> readDefault)
        {
            throw new InvalidOperationException(
                $"{TrackerTypeName}.{TrackerFieldName}.{DefaultFieldName} 不再是 Func<CardModel, bool>。");
        }
        _readDefault = readDefault;

        // 整份适配建立在「没打过标的史莱姆就是本体语义」上。默认值若变成按卡取值、或变成 true，
        // 这条前提就不成立，必须关死而不是带着错的假设继续算。
        if (!DefaultIsPlainSlimed())
        {
            throw new InvalidOperationException(
                $"{TrackerTypeName}.{TrackerFieldName} 的默认值不再是 false："
                + "「未打标的史莱姆是本体语义」这一前提已不成立，史莱姆的合成语义需要重新核对。");
        }
    }

    private static bool DefaultIsPlainSlimed()
    {
        try
        {
            // 往昔之章的默认值是常量 false（`new SpireField<CardModel, bool>(() => false)`），
            // 与传入的键无关；若日后改成按卡取值，这里会抛，适配整体拒绝加载。
            return !_readDefault(null!) && !_readDefault(null!);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"{TrackerTypeName}.{TrackerFieldName} 的默认值不再与卡牌无关：{ex.Message}", ex);
        }
    }

    private static void BindConfig()
    {
        Type configType = AfpReflection.RequireType(ConfigTypeName);
        _configProperty = configType.GetProperty(
                ConfigMemberName,
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"{ConfigTypeName}.{ConfigMemberName} 不存在。");
        if (_configProperty.PropertyType != typeof(bool))
            throw new InvalidOperationException($"{ConfigTypeName}.{ConfigMemberName} 不再是 bool。");
    }

    /// <summary>
    /// 「往昔之章**自己的怪**产出经典史莱姆」开关。
    /// </summary>
    /// <remarks>
    /// **刻意不在 <see cref="Verify"/> 里读**：配置要等往昔之章自己的初始化跑完才装载，而两个 Mod 的
    /// 初始化先后不作保证，那时读到的可能还是默认的 <c>false</c>。首次使用时读一次并冻结，好让
    /// 同一场战斗里「生成一张史莱姆」与「判定一张史莱姆」用的是同一个值。
    ///
    /// 只用来说明**以后生成的**史莱姆是哪一种，**永远不用来判定一张已经存在的牌**：
    /// 这个开关管不到别的 Mod 产的史莱姆。
    /// </remarks>
    private static bool ConfigGivesClassicSlimed
    {
        get
        {
            if (_configValue is bool cached)
                return cached;
            bool value = _configProperty.GetValue(null) is bool enabled && enabled;
            _configValue = value;
            return value;
        }
    }

    /// <summary>
    /// 登记一张由适配层在预测里生成的史莱姆。往昔之章是在生成期间依同一份配置打标的，这里照做——
    /// 否则这张牌出牌时会按本体语义多抽一张。
    /// </summary>
    /// <remarks>
    /// **只有往昔之章怪物行动那条生成路径可以调用**（<c>ExordiumMoveEffects.AddSlimedToCombat</c>）。
    /// 本体、其它 Mod（例如心脏的血弹包）产出的史莱姆是本体语义，不得登记成经典。
    ///
    /// 用 <c>AddOrUpdate</c> 而不是 <c>Add</c>：并行的搜索分支可能对同一张牌重复走到这里，
    /// 写的是同一个值，重复登记必须无害。
    /// </remarks>
    public static void RecordGenerated(PredictedCard card)
        => GeneratedCards.AddOrUpdate(
            card.Original,
            ConfigGivesClassicSlimed ? ClassicMarker : PlainMarker);

    /// <summary>
    /// 这张史莱姆是不是「经典」的。
    /// </summary>
    /// <remarks>
    /// 入参必须是卡牌的实机身份（<c>PredictedCard.Original</c>）：预测克隆身上读不到标记，
    /// 往昔之章的标记表只认原实例。登记过的按登记值算；没登记的读实机标记——对求解器生成的牌
    /// 那只可能是默认值 <c>false</c>，正合本体语义。
    ///
    /// 读取是纯的：只查表、不插入，因此可以从多个 worker 线程并发调用。
    /// </remarks>
    public static bool IsClassic(CardModel original)
    {
        if (GeneratedCards.TryGetValue(original, out object? marker))
            return ReferenceEquals(marker, ClassicMarker);
        return _tryReadTag(original, out object? raw) && raw is bool flag
            ? flag
            : _readDefault(original);
    }

    /// <summary>
    /// 同一份状态里若同时存在两种史莱姆，明确拒绝。
    /// </summary>
    /// <remarks>
    /// 史莱姆的类别**不在状态指纹里**：逐卡状态没有进入指纹的通道——卡片级状态不在适配接口的
    /// 范围内（见 docs/third-party-model-state.md「范围」），而指纹只为挂在卡牌上的 BaseLib
    /// <c>CardModifier</c> 留了通道（<c>PredictionModModelSupport.AppendBaseLibCardModifierState</c>），
    /// 往昔之章却把类别存在 <c>SpireField</c> 上。两张同名牌一旦类别不同、指纹却相同，
    /// 去重就会把它们当成同一张，打出去的结果（抽 1 张／不抽牌）与实际不符。
    ///
    /// 单一来源时这个问题不存在：**同一场战斗里史莱姆只有一个来源**——一场遭遇打的是同一种怪，
    /// 往昔之章只在它自己的怪身上让史莱姆变经典，心脏等其它来源的怪在同一场里不会出现。
    /// 因此这里只在**两种类别确实并存**时才拒绝：异常会中止整次求解，最坏是明确拒绝，
    /// 不会交出一条错路线。
    ///
    /// 覆盖范围是**当前所有玩家**的五个牌堆（与指纹的覆盖范围一致）。跨状态的那种混用
    /// （两个搜索分支各自只持有一种类别）在本地观察不到，这里也挡不住。
    /// </remarks>
    public static void RejectCoexistingClasses(CombatPredictionState state, bool playedIsClassic)
    {
        foreach (Player player in state.Players)
        {
            foreach (PredictedCard card in state.GetPlayerCombatState(player).AllCards)
            {
                if (card.Original is not Slimed)
                    continue;
                if (IsClassic(card.Original) == playedIsClassic)
                    continue;
                throw new PredictionUnsupportedException(MixMessage);
            }
        }
    }

    private const string MixMessage =
        "同一场战斗里同时存在经典史莱姆（出牌不抽牌）与本体史莱姆（出牌抽 1 张）。"
        + "往昔之章的经典标记只由它自己怪物的行动打上，本体或其它 Mod 产出的史莱姆一律是本体语义；"
        + "两者并存时，史莱姆的类别不在状态指纹里，两张同名牌在去重时会被当成同一张，路线不可信。"
        + "请先打掉其中一种史莱姆再求解。";
}
