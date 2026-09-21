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
