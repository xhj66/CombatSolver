using System.Reflection;
using MegaCrit.Sts2.Core.Models;

namespace CombatSolver.Engine.Common.Mirrors;

/// <summary>
/// 把「按运行时类型登记一张镜像」这件事从泛型搬到一个可以用 <see cref="Type"/> 调用的入口。
/// </summary>
/// <remarks>
/// 求解器自己的镜像在静态初始化时用 <c>Register&lt;TModel&gt;</c> 登记，编译期就知道类型。第三方适配
/// Mod 只拿得到 <see cref="Type"/>（它在运行期用反射找到对方程序集里的类型），没法直接写泛型形参，
/// 所以这里提供一个把 <paramref name="modelType"/> 变成泛型实参的薄壳。
///
/// <para>**为什么用中间的泛型方法而不是直接调 <c>Register&lt;TModel&gt;</c>。** 直接
/// <c>MakeGenericMethod(modelType).Invoke(registry, [handler])</c> 要求把
/// <c>Action&lt;AbstractModel, TContext&gt;</c> 绑到 <c>Action&lt;TModel, TContext&gt;</c> 形参上，
/// 靠的是委托逆变；那属于「碰巧成立」的写法。这里改成经一个
/// <c>where TModel : AbstractModel</c> 的中间方法收口，参数类型逐字相等，不依赖逆变。</para>
///
/// <para>登记责任仍在调用方：必须在任何根捕获与分发之前完成，之后登记要么被上层冻结检查拒绝，
/// 要么因精确类型查询缓存已建立而静默失效（见 docs/THIRD_PARTY_ADAPTERS.md §3.1）。</para>
/// </remarks>
internal static class ThirdPartyMirrorRegistration
{
    private static readonly MethodInfo RegisterCoreMethod = typeof(ThirdPartyMirrorRegistration)
        .GetMethod(nameof(RegisterCore), BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(typeof(ThirdPartyMirrorRegistration).FullName, nameof(RegisterCore));

    private static readonly MethodInfo RegisterResultCoreMethod = typeof(ThirdPartyMirrorRegistration)
        .GetMethod(nameof(RegisterResultCore), BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(
            typeof(ThirdPartyMirrorRegistration).FullName,
            nameof(RegisterResultCore));

    /// <summary>
    /// 把 <paramref name="handler"/> 登记给 <paramref name="modelType"/> 这张镜像注册表。
    /// 类型不是该基类的具体子类、或没有重写对应虚方法时，底层注册表会抛异常。
    /// </summary>
    public static void Register<TContext>(
        MethodMirrorRegistry<AbstractModel, TContext> registry,
        Type modelType,
        Action<AbstractModel, TContext> handler)
        where TContext : IMethodMirrorContext<AbstractModel>
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(modelType);
        ArgumentNullException.ThrowIfNull(handler);
        _ = RegisterCoreMethod
            .MakeGenericMethod(modelType, typeof(TContext))
            .Invoke(null, [registry, handler]);
    }

    /// <summary>返回值版本的登记；<paramref name="modelType"/> 必须重写带结果的原生虚方法。</summary>
    public static void RegisterResult<TContext, TResult>(
        MethodMirrorRegistry<AbstractModel, TContext, TResult> registry,
        Type modelType,
        Func<AbstractModel, TContext, TResult> handler)
        where TContext : IMethodMirrorContext<AbstractModel>
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(modelType);
        ArgumentNullException.ThrowIfNull(handler);
        _ = RegisterResultCoreMethod
            .MakeGenericMethod(modelType, typeof(TContext), typeof(TResult))
            .Invoke(null, [registry, handler]);
    }

    private static void RegisterCore<TModel, TContext>(
        MethodMirrorRegistry<AbstractModel, TContext> registry,
        Action<AbstractModel, TContext> handler)
        where TModel : AbstractModel
        where TContext : IMethodMirrorContext<AbstractModel>
        => registry.Register<TModel>((model, context) => handler(model, context));

    private static void RegisterResultCore<TModel, TContext, TResult>(
        MethodMirrorRegistry<AbstractModel, TContext, TResult> registry,
        Func<AbstractModel, TContext, TResult> handler)
        where TModel : AbstractModel
        where TContext : IMethodMirrorContext<AbstractModel>
        => registry.Register<TModel>((model, context) => handler(model, context));
}
