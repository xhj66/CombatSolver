namespace CombatSolver.ActsFromThePastAdapter;

/// <summary>
/// 往昔之章**第二、三幕**怪物里「伤害在意图构造时就固定」的攻击行动（第一幕见
/// <see cref="ExordiumStableAttacks"/>）。
/// </summary>
/// <remarks>
/// 入选条件与第一幕那份完全相同，逐条反编译核对过：意图走的是
/// <c>SingleAttackIntent(&lt;构造期就求值的值&gt;)</c> 或
/// <c>MultiAttackIntent(&lt;构造期就求值的值&gt;, &lt;整数字面量或按进阶冻结的 int 属性&gt;)</c>，
/// 而不是 <c>SingleAttackIntent(Func&lt;decimal&gt;)</c> / <c>MultiAttackIntent(int, Func&lt;int&gt;)</c>
/// 或往昔之章自己的 <c>Dynamic*AttackIntent</c>。
///
/// <para>
/// 两条安全网：① 求解器在运行期会用
/// <c>ThirdPartyAdapterRegistry.IsStableAttackShape</c> 核对**伤害**闭包确实来自那两个 int 构造函数，
/// 形状不对就不认这条声明（所以把 <c>&lt;expr&gt;</c> 记成 lambda 这类笔误不会变成静默错误）；
/// ② 但**段数不在该判据覆盖范围内**——<c>MultiAttackIntent(int, Func&lt;int&gt;)</c> 那条重载的伤害同样是
/// 常量构造，所以下面**刻意只列第二个实参不是 lambda 的多段行动**。因此本名单里**没有**：
/// </remarks>
/// <list type="bullet">
/// <item><c>BookOfStabbing.STAB</c>、<c>Maw.NOMNOMNOM_MULTI</c>、<c>Hexaghost.DIVIDER</c> ——
/// 段数或伤害由 <c>Dynamic*AttackIntent</c> 现算；</item>
/// <item><c>Darkling.NIP</c>、<c>GiantHead.IT_IS_TIME</c>、<c>Transient.ATTACK</c> ——
/// <c>DynamicSingleAttackIntent</c>（虱子 BITE 同型）；</item>
/// <item>所有只含 <c>SummonIntent</c> / <c>DefendIntent</c> / <c>HealIntent</c> / <c>DeathBlowIntent</c> /
/// 空意图表的行动 —— 没有攻击意图可声明。</item>
/// </list>
/// <para>
/// 登记与「这只怪物有没有被完整适配」无关：静态攻击行动本来就是静态的，多登记不会让求解器接受
/// 一条它本来会拒绝的路线，也不会改变任何模拟数值，只影响界面上的可信度标注
/// （见 docs/AFTP_ACT4HEART_STATUS.md §2.7）。
/// </para>
/// </remarks>
internal static class LaterActsStableAttacks
{
    // (怪物短名, 行动 Id)，逐条对应 AFTP 源码里 GenerateMoveStateMachine 的意图构造。
    private static readonly (string Monster, string Move)[] ConstantAttacks =
    [
        // === 第二幕（The City）===
        ("Bear", "MAUL"),                       // SingleAttackIntent(MaulDamage)
        ("Bear", "LUNGE"),                      // SingleAttackIntent(LungeDamage) + DefendIntent
        ("BookOfStabbing", "BIG_STAB"),         // SingleAttackIntent(BigStabDamage)（STAB 是 DynamicMulti ✗）
        ("BronzeAutomaton", "FLAIL"),           // MultiAttackIntent(FlailDamage, 2)
        ("BronzeAutomaton", "HYPER_BEAM"),      // SingleAttackIntent(BeamDamage)
        ("BronzeOrb", "BEAM"),                  // SingleAttackIntent(8)
        ("Byrd", "PECK"),                       // MultiAttackIntent(PeckDamage, PeckCount)
        ("Byrd", "SWOOP"),                      // SingleAttackIntent(SwoopDamage)
        ("Byrd", "HEADBUTT"),                   // SingleAttackIntent(3)
        ("Centurion", "SLASH"),                 // SingleAttackIntent(SlashDamage)
        ("Centurion", "FURY"),                  // MultiAttackIntent(FuryDamage, 3)
        ("Champ", "HEAVY_SLASH"),               // SingleAttackIntent(SlashDamage)
        ("Champ", "EXECUTE"),                   // MultiAttackIntent(ExecuteDamage, 2)
        ("Champ", "FACE_SLAP"),                 // SingleAttackIntent(SlapDamage) + DebuffIntent
        ("Chosen", "DEBILITATE"),               // SingleAttackIntent(DebilitateDamage) + DebuffIntent
        ("Chosen", "ZAP"),                      // SingleAttackIntent(ZapDamage)
        ("Chosen", "POKE"),                     // MultiAttackIntent(PokeDamage, 2)
        ("Collector", "FIREBALL"),              // SingleAttackIntent(FireballDamage)
        ("GremlinLeader", "STAB"),              // MultiAttackIntent(6, 3)
        ("Mugger", "MUG"),                      // SingleAttackIntent(MugDamage)
        ("Mugger", "BIG_SWIPE"),                // SingleAttackIntent(BigSwipeDamage)
        ("Mystic", "ATTACK"),                   // SingleAttackIntent(MagicDamage) + DebuffIntent
        ("Pointy", "STAB"),                     // MultiAttackIntent(AttackDamage, 2)
        ("Romeo", "CROSS_SLASH"),               // SingleAttackIntent(CrossSlashDamage)
        ("Romeo", "AGONIZING_SLASH"),           // SingleAttackIntent(AgonizeDamage) + DebuffIntent
        ("ShelledParasite", "FELL"),            // SingleAttackIntent(FellDamage) + DebuffIntent
        ("ShelledParasite", "DOUBLE_STRIKE"),   // MultiAttackIntent(DoubleStrikeDamage, 2)
        ("ShelledParasite", "LIFE_SUCK"),       // SingleAttackIntent(SuckDamage) + HealIntent
        ("SnakePlant", "CHOMP"),                // MultiAttackIntent(ChompDamage, 3)
        ("Snecko", "BITE"),                     // SingleAttackIntent(BiteDamage)
        ("Snecko", "TAIL_WHIP"),                // SingleAttackIntent(TailDamage) + DebuffIntent
        ("SphericGuardian", "FRAIL_ATTACK"),    // SingleAttackIntent(AttackDamage) + DebuffIntent
        ("SphericGuardian", "SLAM"),            // MultiAttackIntent(AttackDamage, 2)
        ("SphericGuardian", "HARDEN"),          // SingleAttackIntent(AttackDamage) + DefendIntent
        ("Taskmaster", "SCOURING_WHIP"),        // SingleAttackIntent(ScouringWhipDamage) + StatusIntent
        ("TorchHead", "TACKLE"),                // SingleAttackIntent(7)

        // === 第三幕（The Beyond）===
        ("AwakenedOne", "SLASH"),               // SingleAttackIntent(20)
        ("AwakenedOne", "SOUL_STRIKE"),         // MultiAttackIntent(6, 4)
        ("AwakenedOne", "DARK_ECHO"),           // SingleAttackIntent(40)
        ("AwakenedOne", "SLUDGE"),              // SingleAttackIntent(18) + StatusIntent
        ("AwakenedOne", "TACKLE"),              // MultiAttackIntent(10, 3)
        ("Darkling", "CHOMP"),                  // MultiAttackIntent(ChompDamage, 2)（NIP 是 DynamicSingle ✗）
        ("Deca", "BEAM"),                       // MultiAttackIntent(BeamDamage, 2) + StatusIntent
        ("Donu", "BEAM"),                       // MultiAttackIntent(BeamDamage, 2)
        ("Exploder", "ATTACK"),                 // SingleAttackIntent(AttackDamage)
        ("GiantHead", "COUNT"),                 // SingleAttackIntent(13) + DebuffIntent
        ("Maw", "SLAM"),                        // SingleAttackIntent(SlamDamage)
        ("Maw", "NOMNOMNOM_SINGLE"),            // SingleAttackIntent(5)
        ("Nemesis", "TRI_ATTACK"),              // MultiAttackIntent(FireDamage, 3)
        ("Nemesis", "SCYTHE"),                  // SingleAttackIntent(45)
        ("OrbWalker", "LASER"),                 // SingleAttackIntent(LaserDamage) + StatusIntent
        ("OrbWalker", "CLAW"),                  // SingleAttackIntent(ClawDamage)
        ("Reptomancer", "SNAKE_STRIKE"),        // MultiAttackIntent(SnakeStrikeDamage, 2) + DebuffIntent
        ("Reptomancer", "BIG_BITE"),            // SingleAttackIntent(BigBiteDamage)
        ("Repulsor", "ATTACK"),                 // SingleAttackIntent(AttackDamage)
        ("SnakeDagger", "WOUND_STAB"),          // SingleAttackIntent(9) + StatusIntent
        ("Spiker", "ATTACK"),                   // SingleAttackIntent(AttackDamage)
        ("SpireGrowth", "QUICK_TACKLE"),        // SingleAttackIntent(TackleDamage)
        ("SpireGrowth", "SMASH"),               // SingleAttackIntent(SmashDamage)
        ("TimeEater", "REVERBERATE"),           // MultiAttackIntent(ReverbDamage, 3)
        ("TimeEater", "HEAD_SLAM"),             // SingleAttackIntent(HeadSlamDamage) + Debuff/Status
        ("WrithingMass", "BIG_HIT"),            // SingleAttackIntent(BigHitDamage)
        ("WrithingMass", "MULTI_HIT"),          // MultiAttackIntent(MultiHitDamage, 3)
        ("WrithingMass", "ATTACK_BLOCK"),       // SingleAttackIntent(AttackBlockDamage) + DefendIntent
        ("WrithingMass", "ATTACK_DEBUFF"),      // SingleAttackIntent(AttackDebuffDamage) + DebuffIntent
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
