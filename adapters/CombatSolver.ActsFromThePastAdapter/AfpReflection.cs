using System.Globalization;
using System.Reflection;

namespace CombatSolver.ActsFromThePastAdapter;

/// <summary>
/// 按类型名访问 Acts from the Past 的模型与源码常量。
/// </summary>
/// <remarks>
/// 适配层**不做编译期引用**：往昔之章是外部 Mod，它的程序集只保证在运行期加载。
/// 因此这里按类型全名查找，并在初始化时用 <see cref="RequireConst"/> 把适配层内钉死的
/// 数值与对方源码里的 <c>private const</c> 逐项核对——对方改了数值而没升版本号时，
/// 自检会失败并让整个适配拒绝登记，而不是算出一个看起来正常但错的结果
/// （见 docs/THIRD_PARTY_ADAPTERS.md §3.4）。
/// </remarks>
internal static class AfpReflection
{
    private const string AssemblyName = "ActsFromThePast";

    private static Assembly? _assembly;

    public static Assembly Assembly => _assembly ??= Locate();

    private static Assembly Locate()
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (string.Equals(assembly.GetName().Name, AssemblyName, StringComparison.Ordinal))
                return assembly;
        }
        throw new InvalidOperationException($"当前进程里找不到 {AssemblyName} 程序集，往昔之章适配无法初始化。");
    }

    /// <summary>确认某个敌方怪物类型存在（只按全名，不做编译期引用）。</summary>
    public static void RequireMonsterType(string typeName)
    {
        _ = Assembly.GetType($"ActsFromThePast.{typeName}", throwOnError: false)
            ?? throw new InvalidOperationException($"ActsFromThePast.{typeName} 不存在，往昔之章版本可能已变动。");
    }

    /// <summary>按全名取回一个类型（补丁类、配置类这类不落在 <c>ActsFromThePast.</c> 一层下的）。</summary>
    public static Type RequireType(string fullName)
    {
        return Assembly.GetType(fullName, throwOnError: false)
            ?? throw new InvalidOperationException($"{fullName} 不存在，往昔之章版本可能已变动。");
    }

    /// <summary>
    /// 反射调用游戏内部的 <c>AscensionHelper.HasAscension(AscensionLevel)</c>。
    /// </summary>
    /// <remarks>
    /// 往昔之章有些行动里写的是内联的 <c>AscensionHelper.HasAscension((AscensionLevel)9)</c>
    /// （例如蛇怪的 <c>TAIL_WHIP</c> 只在 A9 及以上再加虚弱）——那个辅助类型是游戏内部的、适配层
    /// 看不见，而把「A9」自己翻译成 <c>AscensionLevel &gt;= 9</c> 属于猜语义。所以照原样反射调用，
    /// 判据与游戏逐字相同；调用前由 <see cref="VerifyAscensionHelper"/> 钉死成员形状。
    /// </remarks>
    public static bool HasAscension(int level)
    {
        _ = _ascensionHasAscension ?? throw new InvalidOperationException(
            "AscensionHelper.HasAscension 还没核对过，请先调用 AfpReflection.VerifyAscensionHelper()。");
        object boxed = Enum.ToObject(
            _ascensionLevelType ?? throw new InvalidOperationException("AscensionLevel 枚举类型缺失。"),
            level);
        return (bool)(_ascensionHasAscension.Invoke(null, [boxed])
            ?? throw new InvalidOperationException("AscensionHelper.HasAscension 返回了 null。"));
    }

    private static readonly string[] AscensionHelperTypeNames =
    [
        "MegaCrit.Sts2.Core.Entities.Ascension.AscensionHelper",
        "MegaCrit.Sts2.Core.Helpers.AscensionHelper",
        "AscensionHelper",
    ];

    private static Type? _ascensionLevelType;
    private static MethodInfo? _ascensionHasAscension;

    /// <summary>
    /// 核对游戏里的 <c>AscensionHelper.HasAscension(AscensionLevel)</c> 还在，并缓存下来。
    /// </summary>
    public static void VerifyAscensionHelper()
    {
        Assembly game = LocateGameAssembly();
        _ascensionLevelType = game.GetType("MegaCrit.Sts2.Core.Entities.Ascension.AscensionLevel", false)
            ?? game.GetTypes().FirstOrDefault(type => type.IsEnum && type.Name == "AscensionLevel")
            ?? throw new InvalidOperationException("游戏程序集里找不到 AscensionLevel 枚举。");
        Type helper = AscensionHelperTypeNames
                .Select(name => game.GetType(name, throwOnError: false))
                .FirstOrDefault(type => type is not null)
            ?? game.GetTypes().FirstOrDefault(type => type.Name == "AscensionHelper")
            ?? throw new InvalidOperationException("游戏程序集里找不到 AscensionHelper。");
        _ascensionHasAscension = helper
                .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method => method.Name == "HasAscension"
                    && method.GetParameters().Length == 1
                    && method.GetParameters()[0].ParameterType == _ascensionLevelType)
            ?? throw new InvalidOperationException(
                $"{helper.FullName}.HasAscension(AscensionLevel) 不再存在，往昔之章的进阶分支需要重新核对。");
    }

    private static Assembly LocateGameAssembly()
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (string.Equals(assembly.GetName().Name, "sts2", StringComparison.Ordinal))
                return assembly;
        }
        throw new InvalidOperationException("当前进程里找不到 sts2 程序集，进阶判定无法核对。");
    }

    /// <summary>
    /// 核对某个类型**确实重写**了指定名字（与参数个数）的虚方法，并把这个类型取回来。
    /// </summary>
    /// <remarks>
    /// 适配层登记的是「往昔之章重写了这个钩子」这个事实本身。对方把重写删掉、或改成另一个签名时，
    /// 登记会变成一条对不存在的方法的声明，自检必须当场失败、整个适配拒绝登记
    /// （见 docs/THIRD_PARTY_ADAPTERS.md §3.4）。<paramref name="typeName"/> 不带点时按
    /// <c>ActsFromThePast.&lt;名字&gt;</c> 解析。
    /// </remarks>
    public static Type RequireOverride(string typeName, string methodName, int parameterCount)
    {
        Type type = typeName.Contains('.', StringComparison.Ordinal)
            ? RequireType(typeName)
            : Assembly.GetType($"ActsFromThePast.{typeName}", throwOnError: false)
                ?? throw new InvalidOperationException(
                    $"ActsFromThePast.{typeName} 不存在，往昔之章版本可能已变动。");
        foreach (MethodInfo method in type.GetMethods(
                     BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (!string.Equals(method.Name, methodName, StringComparison.Ordinal)
                || method.GetParameters().Length != parameterCount)
            {
                continue;
            }
            if (method.IsVirtual && method.GetBaseDefinition() != method)
                return type;
        }
        throw new InvalidOperationException(
            $"{type.FullName}.{methodName}（{parameterCount} 参）不再是重写，往昔之章版本可能已变动。");
    }

    /// <summary>
    /// 核对并取回一个 <c>private const int</c>。返回值就是对方当前的常量值，
    /// 适配层用它参与运算，避免把数值抄成第二份真相。
    /// </summary>
    public static int RequireConst(string typeName, string fieldName, int expected)
    {
        Type type = Assembly.GetType($"ActsFromThePast.{typeName}", throwOnError: false)
            ?? throw new InvalidOperationException($"ActsFromThePast.{typeName} 不存在，往昔之章版本可能已变动。");
        FieldInfo field = type.GetField(
                fieldName,
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"ActsFromThePast.{typeName}.{fieldName} 不存在。");
        if (!field.IsLiteral)
            throw new InvalidOperationException($"ActsFromThePast.{typeName}.{fieldName} 不再是常量。");
        object? raw = field.GetRawConstantValue();
        int actual = raw is null
            ? throw new InvalidOperationException($"ActsFromThePast.{typeName}.{fieldName} 没有常量值。")
            : Convert.ToInt32(raw, CultureInfo.InvariantCulture);
        if (actual != expected)
        {
            throw new InvalidOperationException(
                $"ActsFromThePast.{typeName}.{fieldName} = {actual}，适配层钉死的是 {expected}；" +
                "往昔之章改了数值，适配需要重新核对。");
        }
        return actual;
    }
}
