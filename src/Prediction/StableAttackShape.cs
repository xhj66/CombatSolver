using MegaCrit.Sts2.Core.MonsterMoves.Intents;

namespace CombatSolver;

/// <summary>
/// 「这个攻击意图的伤害是不是在构造时就固定」这一条声明的**唯一判据**。
/// </summary>
/// <remarks>
/// <c>IntentForecaster</c> 按 <c>AttackIntent.DamageCalc.Target != null</c> 保守判断一次攻击是动态伤害，
/// 原版那批已复核的行动写死在它自己的白名单里，第三方类型只能靠
/// <see cref="ThirdPartyAdapterRegistry.RegisterStableAttack" /> 声明。可是第三方适配层在初始化时
/// 既拿不到怪物实例、也拿不到那些 <c>private int X =&gt; AscensionHelper…</c> 属性的值——数字不是
/// <c>const</c>，抄一份进适配层只会变成第二份真相。所以「声明」必须能被**现场核对**，判据就是这个类。
///
/// <para>
/// 判据来自编译器。原版那两个构造函数是
/// <c>SingleAttackIntent(int damage) { DamageCalc = () =&gt; damage; }</c> 与
/// <c>MultiAttackIntent(int damage, int repeat) { DamageCalc = () =&gt; damage; _repeat = repeat; }</c>：
/// 捕的是**构造函数参数**，于是闭包显示类声明在意图类型自己内部
/// （<c>SingleAttackIntent.&lt;&gt;c__DisplayClass…</c>，<c>DeclaringType</c> 就是
/// <c>SingleAttackIntent</c>）。
/// </para>
///
/// <para>
/// 另一条 <c>SingleAttackIntent(Func&lt;decimal&gt;)</c> 重载拿的是**调用方**给的委托：只捕获
/// <c>this</c> 的 lambda 直接以怪物实例为 <c>Target</c>；捕获局部变量的 lambda 落在调用方的显示类里；
/// 往昔之章的 <c>DynamicSingleAttackIntent</c> / <c>DynamicMultiAttackIntent</c> 则在**自己**的构造函数里
/// 再造一层 <c>() =&gt; _damageFunc()</c>，闭包同样声明在它们自己内部。这几种的
/// <c>DeclaringType</c> 都不是 <c>SingleAttackIntent</c> / <c>MultiAttackIntent</c>，于是判定为不成立。
/// </para>
///
/// <para>
/// 捕获为空（<c>Target == null</c>）、或者根本没有计算器的情形，本来就不进近似清单
/// （既有判据第一件事就是 <c>DamageCalc?.Target != null</c>），这里一并按「成立」处理，
/// 保持与既有行为逐字一致。
/// </para>
///
/// <para>
/// <b>只管伤害。</b>段数走 <c>Repeats</c>，不在这个判据覆盖范围内——<c>MultiAttackIntent</c> 还有一条
/// <c>(int damage, Func&lt;int&gt; repeatCalc)</c> 重载，伤害是常量而段数不是。登记方仍须按反编译核对
/// 多段行动用的是 <c>(int, int)</c> 那条重载。
/// </para>
/// </remarks>
internal static class StableAttackShape
{
    public static bool IsConstantConstruction(AttackIntent attack)
    {
        ArgumentNullException.ThrowIfNull(attack);
        Func<decimal>? calc = attack.DamageCalc;
        if (calc is null || calc.Target is not { } target)
            return true;
        Type? declaring = target.GetType().DeclaringType;
        return declaring == typeof(SingleAttackIntent) || declaring == typeof(MultiAttackIntent);
    }
}
