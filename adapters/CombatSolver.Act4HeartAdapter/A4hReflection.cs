using System.Globalization;
using System.Reflection;
using MegaCrit.Sts2.Core.Models;
using CombatSolver.Engine.Common;

namespace CombatSolver.Act4HeartAdapter;

/// <summary>
/// 按类型名访问 Act4Heart 的模型、Power 与源码常量。
/// </summary>
/// <remarks>
/// 与往昔之章适配同一套纪律：**不做编译期引用**。Act4Heart 是外部 Mod，它的程序集只保证在运行期加载，
/// 因此这里按类型全名查找，并在初始化时把适配层钉死的数值与对方源码里的静态成员逐项核对——
/// 对方改了数值而没升版本号时，自检会失败并让整个适配拒绝登记，而不是算出一个看起来正常但错的结果
/// （见 docs/THIRD_PARTY_ADAPTERS.md §3.4）。
/// </remarks>
internal static class A4hReflection
{
    /// <summary>Act4Heart 的程序集名（与它的 <c>AssemblyName</c> 一致）。</summary>
    public const string AssemblyName = "Act4Heart";

    private const string MonsterNamespace = "Act4Heart";
    private const string PowerNamespace = "Act4Heart.Powers";
    private const string ModMainTypeName = "Act4Heart.ModMain";
    private const string ModelHookTypeName = "Dolso.ModelHook";

    /// <summary>无敌 Power 的私有嵌套状态类型（嵌套类型用 <c>+</c> 拼接）。</summary>
    private const string InvincibleDataTypeName = "Act4Heart.Powers.InvinciblePower+Data";

    /// <summary>无敌状态里那份「共享」的每回合累计伤害字段（单人战斗走这条）。</summary>
    private const string InvincibleSharedDamageField = "damage_recieved_this_turn_shared";

    /// <summary>无敌状态里那份「按玩家分摊」的字典字段（多人战斗走这条，适配不建模）。</summary>
    private const string InvincibleSplitDamageField = "damage_recieved_this_turn_split";

    private const string GetInternalDataMethodName = "GetInternalData";

    private const BindingFlags AllStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags AllInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static Assembly? _assembly;

    private static Assembly? LocateCore()
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (string.Equals(assembly.GetName().Name, AssemblyName, StringComparison.Ordinal))
                return assembly;
        }
        return null;
    }

    /// <summary>找到 Act4Heart 程序集；还没加载时返回 <c>null</c>。</summary>
    public static Assembly? TryLocate() => _assembly ??= LocateCore();

    public static Assembly Assembly => TryLocate()
        ?? throw new InvalidOperationException($"当前进程里找不到 {AssemblyName} 程序集，心脏适配无法初始化。");

    public static Type RequireType(string fullName)
        => Assembly.GetType(fullName, throwOnError: false)
            ?? throw new InvalidOperationException($"{fullName} 不存在，Act4Heart 版本可能已变动。");

    /// <summary>确认某个敌方怪物类型存在（只按全名，不做编译期引用）。</summary>
    public static Type RequireMonsterType(string typeName) => RequireType($"{MonsterNamespace}.{typeName}");

    /// <summary>确认某个第三方 Power 类型存在。</summary>
    public static Type RequirePowerType(string typeName) => RequireType($"{PowerNamespace}.{typeName}");

    /// <summary>
    /// 确认某个类型带着给定的形参表重写了一个基类虚方法。
    /// </summary>
    /// <remarks>
    /// 求解器的镜像注册表按「名称 + 精确形参类型」找覆写，找不到就抛异常；而登记不是事务性的，
    /// 中途抛异常会留下「装了一半」的登记表。所以在登记之前先把这一项查一遍，保证登记阶段不会失败
    /// （见 docs/THIRD_PARTY_ADAPTERS.md §3.2）。
    /// </remarks>
    public static void RequireOverride(Type type, string baseMethodName, params Type[] parameters)
    {
        MethodInfo? method = type.GetMethod(baseMethodName, AllInstance, binder: null, parameters, modifiers: null);
        if (method is null)
        {
            string signature = string.Join(", ", parameters.Select(parameter => parameter.Name));
            throw new InvalidOperationException(
                $"{type.FullName} 没有重写 {baseMethodName}({signature})，心脏适配的镜像假设已不成立。");
        }
    }

    /// <summary>确认某个实例成员（字段或属性）存在——怪物私有状态、Power 的隐藏状态都靠它。</summary>
    public static void RequireInstanceMember(Type type, string memberName)
    {
        if (type.GetField(memberName, AllInstance) is null
            && type.GetProperty(memberName, AllInstance) is null)
        {
            throw new InvalidOperationException(
                $"{type.FullName}.{memberName} 不存在，Act4Heart 版本可能已变动。");
        }
    }

    /// <summary>
    /// 核对并取回一个静态整数成员（源码里的 <c>private static int X =&gt; 常量</c>）。
    /// 返回值就是对方当前的值，适配层用它参与运算，避免把数值抄成第二份真相。
    /// </summary>
    public static int RequireStaticInt(Type type, string memberName, int expected)
    {
        object? raw;
        if (type.GetProperty(memberName, AllStatic) is { } property)
            raw = property.GetValue(obj: null);
        else if (type.GetField(memberName, AllStatic) is { } field)
            raw = field.GetValue(obj: null);
        else
            throw new InvalidOperationException($"{type.FullName}.{memberName} 不存在。");

        int actual = Convert.ToInt32(raw, CultureInfo.InvariantCulture);
        if (actual != expected)
        {
            throw new InvalidOperationException(
                $"{type.FullName}.{memberName} = {actual}，适配层钉死的是 {expected}；" +
                "Act4Heart 改了数值，适配需要重新核对。");
        }
        return actual;
    }

    /// <summary>
    /// 读取 Act4Heart 配置里的「盾兵把减益改成球位焦点下降的几率」。
    /// </summary>
    /// <remarks>
    /// 盾兵的 <c>bash_move</c> 会在「目标有球位」时按这个几率把 <c>Strength -1</c> 换成
    /// <c>Focus -1</c>，并且**只要目标有球位就会消耗一次 RNG**——不管最后是不是走了焦点分支。
    /// 所以要复刻这次抽样就必须拿到这个值；它属于对方 Mod 的运行期配置，不是常量。
    /// </remarks>
    public static float SpireShieldOrbsFocusDownOdds()
    {
        Type modMain = RequireType(ModMainTypeName);
        object? config = modMain.GetProperty("current_config", AllStatic)?.GetValue(obj: null)
            ?? modMain.GetField("current_config", AllStatic)?.GetValue(obj: null)
            ?? throw new InvalidOperationException(
                "读不到 Act4Heart 的 current_config；配置尚未加载时不应发起预测。");
        Type configType = config.GetType();
        if (configType.GetField("spire_shield_orbs_focus_down_odds", AllInstance) is { } field)
            return Convert.ToSingle(field.GetValue(config), CultureInfo.InvariantCulture);
        if (configType.GetProperty("spire_shield_orbs_focus_down_odds", AllInstance) is { } property)
            return Convert.ToSingle(property.GetValue(config), CultureInfo.InvariantCulture);
        throw new InvalidOperationException(
            $"{configType.FullName}.spire_shield_orbs_focus_down_odds 不存在，Act4Heart 配置结构已变动。");
    }

    /// <summary>
    /// 自检 <c>MonsterModel.Rng</c> 这个公开访问点还在；盾兵球位分支的抽样复刻完全靠它。
    /// </summary>
    /// <remarks>
    /// 早期版本靠反射读基类私有字段 <c>_rng</c>，但游戏提供了公开的 <c>MonsterModel.Rng</c> 属性，
    /// 走公开 API 就不必再依赖私有字段名。这里核对的正是这个属性；实际取流与复刻由求解器本体的
    /// <c>MonsterRngSupport</c> 负责（与往昔之章适配共用同一份实现）。
    /// </remarks>
    public static void RequireLiveMonsterRngField()
        => RequireInstanceMember(typeof(MonsterModel), nameof(MonsterModel.Rng));

    /// <summary>
    /// 枚举 Act4Heart 程序集里所有继承自 <c>Dolso.ModelHook</c> 的具体类型全名。
    /// </summary>
    /// <remarks>
    /// <c>Dolso.ModelHook</c> 是 Act4Heart 自己的运行期钩子基类，靠 <c>ShouldReceiveCombatHooks</c>
    /// 参与战斗钩子。求解器的根捕获门禁会拒绝「不认识的 ModHelper 订阅者」，所以适配层必须把它们
    /// 逐个放行；这份名单同时用于自检：对方新增一个 ModelHook 而适配没跟上时，自检失败、一个都不登记，
    /// 好过悄悄漏掉一个会改战斗的订阅者（见 docs/THIRD_PARTY_ADAPTERS.md §3.2）。
    /// </remarks>
    public static string[] ModelHookSubclassNames()
    {
        Type baseType = RequireType(ModelHookTypeName);
        List<string> names = [];
        foreach (Type type in Assembly.GetTypes())
        {
            if (type.IsAbstract || !baseType.IsAssignableFrom(type))
                continue;
            names.Add(type.FullName ?? type.Name);
        }
        names.Sort(StringComparer.Ordinal);
        return [.. names];
    }

    // === 无敌（InvinciblePower）的隐藏状态 ===

    /// <summary>
    /// 自检无敌那段反射路径：私有嵌套状态类型、共享/分摊两个字段、以及取回状态用的泛型方法都在。
    /// </summary>
    /// <remarks>
    /// 任何一项缺失都意味着「本回合最多掉多少血」的上限会以初值起算——数值不会报错，但预测会在
    /// 心脏被压到无敌上限之后整体偏乐观。宁可整个适配拒绝登记。
    /// </remarks>
    public static void RequireInvincibleInternalMembers()
    {
        Type dataType = RequireType(InvincibleDataTypeName);
        RequireInstanceMember(dataType, InvincibleSharedDamageField);
        RequireInstanceMember(dataType, InvincibleSplitDamageField);
        _ = RequireGetInternalDataMethod();
    }

    /// <summary>
    /// 读取无敌 Power 的「本回合已累计受到的伤害」。
    /// </summary>
    /// <remarks>
    /// <c>Data</c> 是对方的私有嵌套类型，适配层无法在编译期命名它，所以整个读取只能走反射：
    /// 先按名字取回 <c>PowerModel.GetInternalData&lt;T&gt;()</c>，再用运行期解析出的 <c>Data</c> 特化后调用。
    /// 多人分摊模式（<c>split</c>）下值分散在一张按玩家索引的字典里，适配只镜像单人共享计数——
    /// 与其猜，不如明确拒绝。
    /// </remarks>
    public static decimal ReadInvincibleDamageReceived(PowerModel power)
    {
        ArgumentNullException.ThrowIfNull(power);
        Type dataType = RequireType(InvincibleDataTypeName);
        object data = RequireInternalData(power, dataType);

        FieldInfo splitField = dataType.GetField(InvincibleSplitDamageField, AllInstance)
            ?? throw new InvalidOperationException(
                $"{InvincibleDataTypeName}.{InvincibleSplitDamageField} 不存在，Act4Heart 版本可能已变动。");
        if (splitField.GetValue(data) is not null)
        {
            throw new PredictionUnsupportedException(
                "心脏的无敌处于多人分摊模式（split），本适配只镜像单人共享计数。");
        }

        FieldInfo sharedField = dataType.GetField(InvincibleSharedDamageField, AllInstance)
            ?? throw new InvalidOperationException(
                $"{InvincibleDataTypeName}.{InvincibleSharedDamageField} 不存在，Act4Heart 版本可能已变动。");
        return Convert.ToDecimal(sharedField.GetValue(data), CultureInfo.InvariantCulture);
    }

    /// <summary>按运行期解析出的状态类型调用 <c>PowerModel.GetInternalData&lt;T&gt;()</c>。</summary>
    private static object RequireInternalData(PowerModel power, Type dataType)
    {
        MethodInfo method = RequireGetInternalDataMethod();
        object? data;
        try
        {
            data = method.MakeGenericMethod(dataType).Invoke(power, null);
        }
        catch (TargetInvocationException ex)
        {
            throw new InvalidOperationException(
                $"{power.GetType().FullName}.GetInternalData<{dataType.Name}>() 抛出了异常。", ex.InnerException);
        }
        return data ?? throw new InvalidOperationException(
            $"{power.GetType().FullName}.GetInternalData<{dataType.Name}>() 返回了 null。");
    }

    /// <summary>在 <see cref="PowerModel"/> 的继承链上找无参泛型方法 <c>GetInternalData&lt;T&gt;()</c>。</summary>
    private static MethodInfo RequireGetInternalDataMethod()
    {
        for (Type? type = typeof(PowerModel); type is not null; type = type.BaseType)
        {
            foreach (MethodInfo candidate in type.GetMethods(AllInstance))
            {
                if (candidate.Name == GetInternalDataMethodName
                    && candidate.IsGenericMethodDefinition
                    && candidate.GetParameters().Length == 0)
                {
                    return candidate;
                }
            }
        }
        throw new InvalidOperationException(
            "PowerModel.GetInternalData<T>() 不存在；第三方 Power 的隐藏状态无法读取。");
    }
}
