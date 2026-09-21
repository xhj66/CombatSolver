using CombatSolver;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;

int checks = 0;
void Check(bool condition, string message)
{
    if (!condition)
        throw new Exception(message);
    checks++;
}

// 原版两条「常量构造」重载：闭包显示类声明在意图类型自己内部，判定成立。
Check(
    StableAttackShape.IsConstantConstruction(new SingleAttackIntent(7)),
    "SingleAttackIntent(int) 必须判定为常量构造。");
Check(
    StableAttackShape.IsConstantConstruction(new MultiAttackIntent(3, 2)),
    "MultiAttackIntent(int, int) 必须判定为常量构造。");

// 调用方给的委托：闭包落在调用方（这里是本程序的 Program 类型），判定不成立。
Check(
    !StableAttackShape.IsConstantConstruction(CapturingSingle(9)),
    "捕获了调用方局部变量的 lambda 不能判定为常量构造。");
Check(
    !StableAttackShape.IsConstantConstruction(new SingleAttackIntent(static () => 4m)),
    "调用方的无捕获 lambda 也不在认证范围内（保守方向）。");

// 往昔之章 Dynamic*AttackIntent 的形状：派生意图在自己的构造函数里再造一层闭包。
Check(
    !StableAttackShape.IsConstantConstruction(new StandInDynamicSingleAttackIntent(() => 5)),
    "派生意图自带闭包（DynamicSingleAttackIntent 形状）不能判定为常量构造。");
Check(
    !StableAttackShape.IsConstantConstruction(new StandInDynamicMultiAttackIntent(() => 5, 3)),
    "派生意图自带闭包（DynamicMultiAttackIntent 形状）不能判定为常量构造。");

Console.WriteLine($"STABLE_ATTACK_SHAPE_CHECKS ok={checks}");
return 0;

static AttackIntent CapturingSingle(int seed)
    => new SingleAttackIntent(() => seed + 1);

/// <summary>往昔之章 <c>DynamicSingleAttackIntent</c> 的形状替身（那边是 <c>AttackIntent</c> 的直接子类）。</summary>
internal sealed class StandInDynamicSingleAttackIntent : AttackIntent
{
    private readonly Func<int> _damageFunc;

    public StandInDynamicSingleAttackIntent(Func<int> damageFunc)
    {
        _damageFunc = damageFunc;
        DamageCalc = () => _damageFunc();
    }

    protected override LocString IntentLabelFormat => new("intents", "FORMAT_DAMAGE_SINGLE");

    public override int Repeats => 1;

    public override int GetTotalDamage(IEnumerable<Creature> targets, Creature owner)
        => GetSingleDamage(targets, owner);
}

/// <summary>往昔之章 <c>DynamicMultiAttackIntent</c> 的形状替身。</summary>
internal sealed class StandInDynamicMultiAttackIntent : AttackIntent
{
    private readonly Func<int> _damageFunc;
    private readonly int _repeat;

    public StandInDynamicMultiAttackIntent(Func<int> damageFunc, int repeat)
    {
        _damageFunc = damageFunc;
        _repeat = repeat;
        DamageCalc = () => _damageFunc();
    }

    protected override LocString IntentLabelFormat => new("intents", "FORMAT_DAMAGE_MULTI");

    public override int Repeats => _repeat;

    public override int GetTotalDamage(IEnumerable<Creature> targets, Creature owner)
        => GetSingleDamage(targets, owner) * Repeats;
}
