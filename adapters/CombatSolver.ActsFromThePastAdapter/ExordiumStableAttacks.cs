namespace CombatSolver.ActsFromThePastAdapter;

/// <summary>
/// 往昔之章第一幕怪物里「伤害在意图构造时就固定」的攻击行动。
/// </summary>
/// <remarks>
/// 意图预测器按「<c>DamageCalc</c> 是否绑定了实例」保守判断一次攻击是不是动态的，原版那批已复核的
/// 行动写死在 <c>IntentForecaster.IsKnownStableAttack</c> 里；第三方类型登记不进去，于是每一回合都会
/// 多报一条 <c>approximation=…:动态伤害</c>，把这条战斗的可信度压到「中等」。这里按
/// <c>ThirdPartyAdapterRegistry.RegisterStableAttack</c> 把第一幕这批补上。
///
/// **入选条件：反编译逐行核对过意图是「常量构造」。** 也就是
/// <c>new SingleAttackIntent(&lt;构造期就求值的值&gt;)</c> 或
/// <c>new MultiAttackIntent(&lt;构造期就求值的值&gt;, &lt;整数字面量&gt;)</c>，走的是
/// <c>SingleAttackIntent(int)</c> / <c>MultiAttackIntent(int, int)</c> 这两个构造函数：实参在
/// <c>GenerateMoveStateMachine</c> 里求值一次、被闭包捕下来，此后不受任何分支状态影响。
///
/// **刻意不登记的两类**（它们继续按「动态伤害」记近似）：
/// <list type="bullet">
/// <item><c>Hexaghost.DIVIDER</c> = <c>new DynamicMultiAttackIntent(() =&gt; _dividerDamage, 6)</c>，
/// 伤害由 <c>ACTIVATE</c> 按玩家血量现算；</item>
/// <item><c>LouseGreen.BITE</c> / <c>LouseRed.BITE</c> =
/// <c>new DynamicSingleAttackIntent(() =&gt; GetBiteDamage())</c>，读怪物自己入场时抽的那份伤害。</item>
/// </list>
/// 两者都是往昔之章<b>自己</b>的 <c>Dynamic*AttackIntent</c>（直接继承 <c>AttackIntent</c>），
/// 不属于本名单。
///
/// **适配层不抄数值。** 这些伤害在往昔之章里是 <c>private int X =&gt; AscensionHelper…</c> 形式的实例
/// 属性而不是 <c>const</c>，适配既无法在自检里核对、也不该复制成第二份真相；而预测器本来就会调用
/// 意图自己的 <c>DamageCalc</c> 取值。所以这条声明只承担「形状是常量构造」一个含义，由
/// <c>ThirdPartyAdapterRegistry.IsStableAttackShape</c> 在运行期现场核对意图实例：形状对不上就
/// 不认这条声明，近似清单照旧记一条。<b>段数不在该判据覆盖范围内</b>，所以下面只列反编译核对过
/// 用的是 <c>(int, int)</c> 那条重载的多段行动。
///
/// 登记与分支建模无关：某个怪物即使还因为缺状态而无法求解（Guardian、Hexaghost），
/// 它的静态攻击行动仍然是静态的，多登记不会让求解器接受一条它本来会拒绝的路线。
/// </remarks>
internal static class ExordiumStableAttacks
{
    // (怪物短名, 行动 Id)，逐条对应 AFTP 源码里 GenerateMoveStateMachine 的意图构造。
    private static readonly (string Monster, string Move)[] ConstantAttacks =
    [
        // 史莱姆
        ("AcidSlimeLarge", "CORROSIVE_SPIT"),   // SingleAttackIntent(CorrosiveSpitDamage)
        ("AcidSlimeLarge", "TACKLE"),           // SingleAttackIntent(TackleDamage)
        ("AcidSlimeMedium", "CORROSIVE_SPIT"),
        ("AcidSlimeMedium", "TACKLE"),
        ("AcidSlimeSmall", "TACKLE"),
        ("SpikeSlimeLarge", "FLAME_TACKLE"),    // SingleAttackIntent(FlameTackleDamage) + StatusIntent
        ("SpikeSlimeMedium", "FLAME_TACKLE"),
        ("SpikeSlimeSmall", "TACKLE"),
        ("SlimeBoss", "SLAM"),                  // SingleAttackIntent(SlamDamage)

        // 邪教徒 / 蘑菇 / 颚虫
        ("Cultist", "DARK_STRIKE"),             // SingleAttackIntent(AttackDamage)
        ("FungiBeast", "BITE"),                 // SingleAttackIntent(BiteDamage)
        ("JawWorm", "CHOMP"),                   // SingleAttackIntent(ChompDamage)
        ("JawWorm", "THRASH"),                  // SingleAttackIntent(ThrashDamage) + 格挡

        // 小鬼
        ("GremlinFat", "SMASH"),                // SingleAttackIntent(SmashDamage) + 减益
        ("GremlinMad", "SCRATCH"),              // SingleAttackIntent(ScratchDamage)
        ("GremlinNob", "RUSH"),                 // SingleAttackIntent(RushDamage)
        ("GremlinNob", "SKULL_BASH"),           // SingleAttackIntent(BashDamage) + 减益
        ("GremlinShield", "SHIELD_BASH"),       // SingleAttackIntent(ShieldBashDamage)
        ("GremlinSneaky", "PUNCTURE"),          // SingleAttackIntent(PunctureDamage)
        ("GremlinWizard", "ULTIMATE_BLAST"),    // SingleAttackIntent(UltimateDamage)

        // 强盗
        ("SlaverBlue", "STAB"),                 // SingleAttackIntent(StabDamage)
        ("SlaverBlue", "RAKE"),                 // SingleAttackIntent(RakeDamage) + 减益
        ("SlaverRed", "STAB"),                  // SingleAttackIntent(StabDamage)
        ("SlaverRed", "SCRAPE"),                // SingleAttackIntent(ScrapeDamage) + 减益
        ("Looter", "MUG"),                      // SingleAttackIntent(MugDamage)
        ("Looter", "LUNGE"),                    // SingleAttackIntent(LungeDamage)

        // 哨卫 / 拉格维林
        ("Sentry", "BEAM"),                     // SingleAttackIntent(BeamDamage)
        ("Lagavulin", "ATTACK"),                // SingleAttackIntent(AttackDamage)

        // 首领
        ("Guardian", "FIERCE_BASH"),            // SingleAttackIntent(FierceBashDamage)
        ("Guardian", "ROLL_ATTACK"),            // SingleAttackIntent(RollDamage)
        ("Guardian", "WHIRLWIND"),              // MultiAttackIntent(5, 4)
        ("Guardian", "TWIN_SLAM"),              // MultiAttackIntent(8, 2) + 减益
        ("Hexaghost", "TACKLE"),                // MultiAttackIntent(FireTackleDamage, 2)
        ("Hexaghost", "SEAR"),                  // SingleAttackIntent(6) + StatusIntent
        ("Hexaghost", "INFERNO"),               // MultiAttackIntent(InfernoDamage, 6) + 减益
        // Hexaghost.DIVIDER 是 DynamicMultiAttackIntent，刻意不登记。
    ];

    /// <summary>初始化自检：名单里的怪物类型必须在对方的程序集里存在。</summary>
    public static void Verify()
    {
        foreach (string typeName in ConstantAttacks.Select(entry => entry.Monster).Distinct(StringComparer.Ordinal))
            AfpReflection.RequireMonsterType(typeName);
    }

    /// <summary>登记条数，只用于初始化日志。</summary>
    internal static int RegisteredAttackCount => ConstantAttacks.Length;

    public static void RegisterAll()
    {
        foreach ((string monster, string move) in ConstantAttacks)
            ThirdPartyAdapterRegistry.RegisterStableAttack(monster, move);
    }
}
