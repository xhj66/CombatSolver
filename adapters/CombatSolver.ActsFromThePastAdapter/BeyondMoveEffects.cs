using System.Reflection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.MonsterMoves;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Attack;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Card;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Damage;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Death;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.TurnEnd;
using CombatSolver.Engine.InCombat.Simulation;
namespace CombatSolver.ActsFromThePastAdapter;

/// <summary>
/// 往昔之章第三幕（The Beyond）怪物的行动效果（非攻击部分）。
/// </summary>
/// <remarks>
/// 与第一、二幕同一条纪律：逐行对照 AFTP 源码里那个 <c>MoveState</c> 的回调，顺序也一致；
/// 攻击部分仍由求解器的通用攻击循环按意图结算。
/// </remarks>
internal static class BeyondMoveEffects
{
    private static readonly string[] MonsterTypes =
    [
        "Repulsor",
        "SnakeDagger",
        "Spiker",
        "OrbWalker",
        "SpireGrowth",
        "Maw",
        "GiantHead",
        "Reptomancer",
        "Exploder",
        "Donu",
        "Deca",
        "SnakePlant",
        "BronzeAutomaton",
        "BronzeOrb",
        "ShelledParasite",
        "Collector",
        "GremlinLeader",
        "Byrd",
        "Nemesis",
        "Transient",
        "Lagavulin",
        "WrithingMass",
        "TimeEater",
        "Hexaghost",
        "Guardian",
    ];

    /// <summary>AFTP 自己的 <c>ModeShiftPower</c>（守护者的形态切换阈值）与 <c>SharpHidePower</c>（尖刺外壳）。</summary>
    private static Type _modeShiftType = null!;
    private static Type _sharpHideType = null!;

    /// <summary>守护者本体的四个数值常量（AFTP <c>Guardian</c> 的 <c>ChargeUpBlock</c> 等）。</summary>
    private static int _guardianChargeUpBlock;
    private static int _guardianDefensiveBlock;
    private static int _guardianThresholdIncrease;
    private static int _guardianVentDebuff;

    /// <summary>AFTP 自己的 <c>TimeWarpPower</c>（时间吞噬者的「时间扭曲」）与 <c>DrawReductionPower</c>。</summary>
    private static Type _timeWarpPowerType = null!;
    private static Type _drawReductionType = null!;
    /// <summary>时间吞噬者 RIPPLE 的三减益层数（AFTP <c>DebuffTurns</c>）与 HEAD_SLAM 的 Slimed 张数。</summary>
    private static int _timeEaterDebuffTurns;
    private static int _timeEaterSlimedCount;

    /// <summary>AFTP 自己的 <c>ReactivePower</c>（蠕动肉块的「反应」）。</summary>
    private static Type _reactivePowerType = null!;

    /// <summary>蠕动肉块 ATTACK_DEBUFF 的虚弱／易伤层数（AFTP <c>NormalDebuffAmount</c>）。</summary>
    private static int _writhingNormalDebuff;

    /// <summary>AFTP 自己的 <c>AsleepLagavulinPower</c>（睡着的拉瓦格林）与它的金属化。</summary>
    private static Type _asleepLagavulinType = null!;

    /// <summary>AFTP 的 <c>FadingPower</c>／<c>ShiftingPower</c>／<c>ShiftingStrengthDownPower</c>（第三幕瞬逝者）。</summary>
    private static Type _fadingPowerType = null!;
    private static Type _shiftingPowerType = null!;
    private static Type _shiftingStrengthDownType = null!;

    /// <summary>瞬逝者每回合给自己加的伤害（AFTP <c>IncrementDmg</c>）。</summary>
    private static int _transientIncrementDamage;

    /// <summary>Nemesis 的 TRI_BURN 塞的 Burn 张数（AFTP <c>BurnAmount</c>）。</summary>
    private static int _nemesisBurnAmount;

    /// <summary>AFTP 自己的 <c>FlightPower</c>（鸟的飞行）。</summary>
    private static Type _flightPowerType = null!;

    /// <summary>Byrd 的 CAW 给自己的力量（AFTP <c>CawStrength</c>）。</summary>
    private static int _byrdCawStrength;

    /// <summary>AFTP 的火炬头类型（收集者召唤用）与五只小鬼类型（首领召唤用）。</summary>
    private static Type _torchHeadType = null!;
    private static Type _gremlinMadType = null!;
    private static Type _gremlinSneakyType = null!;
    private static Type _gremlinFatType = null!;
    private static Type _gremlinShieldType = null!;
    private static Type _gremlinWizardType = null!;

    /// <summary>首领那条私有 RNG 流抽过几次（照 §2.12 的纪律写进自建标量，进指纹但不进状态名单）。</summary>
    internal const string GremlinLeaderRngDrawsMember = "adapter_gremlin_leader_rng_draws";

    /// <summary>甲壳寄生虫 FELL 给的破甲层数（AFTP <c>FellFrailAmount</c>）。</summary>
    private static int _shelledParasiteFellFrail;

    /// <summary>AFTP 自己的 <c>StasisPower</c>（铜制球体偷牌用）。</summary>
    private static Type _stasisPowerType = null!;

    /// <summary>AFTP 的铜制球体类型（自动机召唤用）。</summary>
    private static Type _bronzeOrbType = null!;

    /// <summary>蛇草 SPORES 给的虚弱／破甲层数（AFTP <c>DebuffAmount</c>）。</summary>
    private static int _snakePlantDebuffAmount;

    /// <summary>AFTP 自己的 <c>PlatedArmorPower</c>（与原版 <c>PlatingPower</c> 是两个类型）。</summary>
    private static Type _platedArmorType = null!;

    /// <summary>AFTP 自己的 <c>MalleablePower</c>（蛇草挂的那一个）。</summary>
    private static Type _malleableType = null!;

    /// <summary>它的私有 <c>_pendingBlock</c>：每次挨打累加，下一次攻击或回合末清空。</summary>
    private static FieldInfo _malleablePendingBlock = null!;

    /// <summary>读实机／影子实例上的 <c>_pendingBlock</c>（越界或字段缺失都显式失败）。</summary>
    internal static int ReadMalleablePendingBlock(PowerModel power)
        => _malleablePendingBlock.GetValue(power) is decimal value
            ? (int)value
            : throw new InvalidOperationException("MalleablePower._pendingBlock 不是 decimal。");

    /// <summary>Deca 的「保护之方」给每个存活队友的格挡与镀甲层数。</summary>
    private static int _decaProtectBlock;
    private static int _decaProtectPlatedArmor;

    /// <summary>Donu 的「保护之环」给每个存活队友的力量（AFTP <c>CircleStrengthAmount</c>）。</summary>
    private static int _donuCircleStrengthAmount;

    /// <summary>Exploder 自爆前的回合数（AFTP <c>ExplosiveCountdown</c>）；分支解析器也用它。</summary>
    internal static int ExploderCountdown = 3;

    /// <summary>AFTP 自己的 <c>ConstrictedPower</c>（与原版 <c>ConstrictPower</c> 是两个类型）。</summary>
    private static Type _constrictedPowerType = null!;

    /// <summary>GiantHead 每次 COUNT 递减后给 IT_IS_TIME 加的伤害（AFTP <c>IncrementDmg</c>）。</summary>
    private static int _giantHeadIncrementDmg;

    /// <summary>GiantHead 的 GLARE 给的虚弱层数（AFTP <c>GlareDuration</c>）。</summary>
    private static int _giantHeadGlareDuration;

    /// <summary>Repulsor 的 Daze 张数（AFTP <c>DazeAmount</c>）。</summary>
    private static int _repulsorDazeAmount;

    /// <summary>Spiker 每次加荆棘的层数（AFTP <c>BuffAmount</c>）。</summary>
    private static int _spikerBuffAmount;

    public static void Verify()
    {
        foreach (string typeName in MonsterTypes)
            AfpReflection.RequireMonsterType(typeName);
        _repulsorDazeAmount = AfpReflection.RequireConst("Repulsor", "DazeAmount", 2);
        _spikerBuffAmount = AfpReflection.RequireConst("Spiker", "BuffAmount", 2);
        // ConstrictAmount 是 A9 分支的运行期属性（没有可钉的常量），所以它走静态数值成员：
        // 适配层的自检只核对类型与两个钩子的签名，数值由根捕获读一次。
        _constrictedPowerType = AfpReflection.RequireType("ActsFromThePast.ConstrictedPower");
        _ = AfpReflection.RequireOverride("ConstrictedPower", "AfterSideTurnEnd", 3);
        _ = AfpReflection.RequireOverride("ConstrictedPower", "AfterDeath", 4);
        _giantHeadIncrementDmg = AfpReflection.RequireConst("GiantHead", "IncrementDmg", 5);
        _giantHeadGlareDuration = AfpReflection.RequireConst("GiantHead", "GlareDuration", 1);
        _snakeDaggerType = AfpReflection.RequireType("ActsFromThePast.SnakeDagger");
        ExploderCountdown = AfpReflection.RequireConst("Exploder", "ExplosiveCountdown", 3);
        _donuCircleStrengthAmount = AfpReflection.RequireConst("Donu", "CircleStrengthAmount", 3);
        _platedArmorType = AfpReflection.RequireType("ActsFromThePast.PlatedArmorPower");
        _ = AfpReflection.RequireOverride("PlatedArmorPower", "BeforeSideTurnStart", 4);
        _ = AfpReflection.RequireOverride("PlatedArmorPower", "BeforeSideTurnEndEarly", 3);
        _ = AfpReflection.RequireOverride("PlatedArmorPower", "AfterDamageReceived", 6);
        _decaProtectBlock = AfpReflection.RequireConst("Deca", "ProtectBlock", 16);
        _decaProtectPlatedArmor = AfpReflection.RequireConst("Deca", "ProtectPlatedArmorAmount", 3);
        _malleableType = AfpReflection.RequireType("ActsFromThePast.MalleablePower");
        _ = AfpReflection.RequireOverride("MalleablePower", "AfterDamageReceived", 6);
        _ = AfpReflection.RequireOverride("MalleablePower", "AfterAttack", 2);
        _ = AfpReflection.RequireOverride("MalleablePower", "AfterSideTurnEnd", 3);
        _malleablePendingBlock = _malleableType.GetField(
                "_pendingBlock",
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "ActsFromThePast.MalleablePower._pendingBlock 不存在，往昔之章版本可能已变动。");
        _snakePlantDebuffAmount = AfpReflection.RequireConst("SnakePlant", "DebuffAmount", 2);
        _stasisPowerType = AfpReflection.RequireType("ActsFromThePast.StasisPower");
        _ = AfpReflection.RequireOverride("StasisPower", "BeforeDeath", 1);
        _bronzeOrbType = AfpReflection.RequireType("ActsFromThePast.BronzeOrb");
        _shelledParasiteFellFrail = AfpReflection.RequireConst("ShelledParasite", "FellFrailAmount", 2);
        _torchHeadType = AfpReflection.RequireType("ActsFromThePast.TorchHead");
        _gremlinMadType = AfpReflection.RequireType("ActsFromThePast.GremlinMad");
        _gremlinSneakyType = AfpReflection.RequireType("ActsFromThePast.GremlinSneaky");
        _gremlinFatType = AfpReflection.RequireType("ActsFromThePast.GremlinFat");
        _gremlinShieldType = AfpReflection.RequireType("ActsFromThePast.GremlinShield");
        _gremlinWizardType = AfpReflection.RequireType("ActsFromThePast.GremlinWizard");
        MonsterRngSupport.VerifyShape();
        _flightPowerType = AfpReflection.RequireType("ActsFromThePast.FlightPower");
        _ = AfpReflection.RequireOverride("FlightPower", "BeforeSideTurnStart", 4);
        _ = AfpReflection.RequireOverride("FlightPower", "AfterDamageReceived", 6);
        _ = AfpReflection.RequireOverride("FlightPower", "AfterRemoved", 1);
        _byrdCawStrength = AfpReflection.RequireConst("Byrd", "CawStrength", 1);
        _nemesisBurnAmount = AfpReflection.RequireConst("Nemesis", "BurnAmount", 5);
        _fadingPowerType = AfpReflection.RequireType("ActsFromThePast.FadingPower");
        _shiftingPowerType = AfpReflection.RequireType("ActsFromThePast.ShiftingPower");
        _shiftingStrengthDownType = AfpReflection.RequireType("ActsFromThePast.ShiftingStrengthDownPower");
        _ = AfpReflection.RequireOverride("FadingPower", "BeforeSideTurnEndEarly", 3);
        _ = AfpReflection.RequireOverride("ShiftingPower", "AfterDamageReceived", 6);
        _transientIncrementDamage = AfpReflection.RequireConst("Transient", "IncrementDmg", 10);
        _asleepLagavulinType = AfpReflection.RequireType("ActsFromThePast.AsleepLagavulinPower");
        _ = AfpReflection.RequireOverride("AsleepLagavulinPower", "AfterDamageReceived", 6);
        _ = AfpReflection.RequireOverride("AsleepLagavulinPower", "BeforeSideTurnStart", 4);
        _ = AfpReflection.RequireOverride("AsleepLagavulinPower", "BeforeSideTurnEndVeryEarly", 3);
        _ = AfpReflection.RequireOverride("AsleepLagavulinPower", "AfterSideTurnEnd", 3);
        _reactivePowerType = AfpReflection.RequireType("ActsFromThePast.ReactivePower");
        _ = AfpReflection.RequireOverride("ReactivePower", "AfterDamageReceived", 6);
        _writhingNormalDebuff = AfpReflection.RequireConst("WrithingMass", "NormalDebuffAmount", 2);
        _timeWarpPowerType = AfpReflection.RequireType("ActsFromThePast.TimeWarpPower");
        _ = AfpReflection.RequireOverride("TimeWarpPower", "AfterCardPlayed", 2);
        _timeEaterDebuffTurns = AfpReflection.RequireConst("TimeEater", "DebuffTurns", 1);
        _timeEaterSlimedCount = AfpReflection.RequireConst("TimeEater", "SlimedCount", 2);
        _drawReductionType = AfpReflection.RequireType("ActsFromThePast.DrawReductionPower");
        _ = AfpReflection.RequireOverride("DrawReductionPower", "ModifyHandDraw", 2);
        _ = AfpReflection.RequireOverride("DrawReductionPower", "AfterSideTurnEnd", 3);
        _modeShiftType = AfpReflection.RequireType("ActsFromThePast.ModeShiftPower");
        _ = AfpReflection.RequireOverride("ModeShiftPower", "AfterDamageReceived", 6);
        _sharpHideType = AfpReflection.RequireType("ActsFromThePast.SharpHidePower");
        _ = AfpReflection.RequireOverride("SharpHidePower", "BeforeCardPlayed", 1);
        _ = AfpReflection.RequireOverride("SharpHidePower", "AfterCardPlayed", 2);
        // 守护者本体：四个数值走常量核对（形态切换的两个数值也钉在这里），行动回调的顺序见本文件
        // 对应处理器；BeforeDeath 是名字带 Death 的重写，不登记镜像会让整场给不出战损（§2.13）。
        _guardianChargeUpBlock = AfpReflection.RequireConst("Guardian", "ChargeUpBlock", 9);
        _guardianDefensiveBlock = AfpReflection.RequireConst("Guardian", "DefensiveBlock", 20);
        _guardianThresholdIncrease = AfpReflection.RequireConst("Guardian", "DmgThresholdIncrease", 10);
        _guardianVentDebuff = AfpReflection.RequireConst("Guardian", "VentDebuffAmount", 2);
        _ = AfpReflection.RequireOverride("Guardian", "BeforeDeath", 1);
        AfpReflection.VerifyAscensionHelper();
    }

    public static void RegisterAll()
    {
        // Repulsor.Daze：往**抽牌堆的随机位置**塞 DazeAmount 张 Dazed（意图是 StatusIntent，攻击部分没有）。
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("Repulsor", "DAZE", RepulsorDaze);

        // Spiker：开场的 StartingThorns 层荆棘发生在 AfterAddedToRoom（已在根里），这里只补 BuffThorns
        // 那一步（自己记一次数 + 再挂 BuffAmount 层荆棘）；分支要读这个计数，所以它进状态名单。
        ThirdPartyAdapterRegistry.RegisterMonsterStateMembers("Spiker", "_thornsCount");
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("Spiker", "BUFF_THORNS", SpikerBuffThorns);

        // SnakeDagger（ Reptomancer 召唤的蛇匕首）：
        // WoundStab 那次 9 点攻击已由通用攻击循环按意图结算，这里只补 1 张 Wound 进弃牌堆；
        // Explode 的 25 点攻击同理（DeathBlowIntent 就是 SingleAttackIntent 的派生），这里只补自杀。
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("SnakeDagger", "WOUND_STAB", SnakeDaggerWoundStab);
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("SnakeDagger", "EXPLODE", SnakeDaggerExplode);
        ThirdPartyAdapterRegistry.RegisterOwnerRemovingMove("SnakeDagger", "EXPLODE");

        // OrbWalker：LASER 的单次攻击由通用攻击循环结算，这里补它塞的两张 Burn——**一张进弃牌堆底部、
        // 一张进抽牌堆随机位置**（源码就是两次不同的 AddGeneratedCardToCombat，不是同一种入堆）。
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("OrbWalker", "LASER", OrbWalkerLaser);

        // OrbWalker 开场挂的那个 AFTP StrengthUpPower 重写的是**常规（非 Late）AfterSideTurnEnd**：
        // 自己那一方回合末按层数给自己加力量。开场的施加发生在 AfterAddedToRoom（已在根里），
        // 这里只登记它在回合末的行为——这也是本轮新入口的第一家用户。
        ThirdPartyAdapterRegistry.RegisterSideTurnEndPower("StrengthUpPower", StrengthUpPowerTurnEnd);

        // --- 塔蔓（SpireGrowth） ---
        // CONSTRICT：给每个活着的玩家挂 ConstrictAmount 层 AFTP 自己的 ConstrictedPower；
        // 那个 Power 的两个钩子分别登记成「自己那一方回合末按层数吃 Unpowered 伤害」与
        // 「施加者死亡时移除自己」——后者是名字带 Death 的重写，不登记会让整场给不出战损。
        ThirdPartyAdapterRegistry.RegisterStaticIntMembers("SpireGrowth", "ConstrictAmount");
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("SpireGrowth", "CONSTRICT", SpireGrowthConstrict);
        ThirdPartyAdapterRegistry.RegisterSideTurnEndPower("ConstrictedPower", ConstrictedPowerTurnEnd);
        AfterDeathMirrors.Register(_constrictedPowerType, ConstrictedPowerAfterDeath);

        // --- 大嘴（Maw） ---
        // 开场 _turnCount = 1、_roared = false 是字段初值（根捕获时读实机实例）；分支每回合 +1 并读这两个值。
        ThirdPartyAdapterRegistry.RegisterMonsterStateMembers("Maw", "_turnCount", "_roared");
        ThirdPartyAdapterRegistry.RegisterStaticIntMembers("Maw", "TerrifyDuration", "StrUp");
        // ROAR：给每个活着的目标 TerrifyDuration 层虚弱与破甲，然后把自己标记成「已咆哮」。
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("Maw", "ROAR", MawRoar);
        // DROOL：给自己 StrUp 点力量。
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("Maw", "DROOL", MawDrool);
        // NOMNOMNOM_MULTI 的段数是 TurnCount / 2 现算的（NOMNOMNOM_SINGLE 是常量 5，已在常量表里）。
        ThirdPartyAdapterRegistry.RegisterMonsterAttackValues("Maw", "NOMNOMNOM_MULTI", MawNomNomNom);
        // BeforeDeath 只有一句死亡音效，登记为忽略（名字带 Death，不登记会让整场给不出战损）。
        BeforeDeathMirrors.RegisterIgnored(AfpReflection.RequireType("ActsFromThePast.Maw"));

        // --- 巨头（GiantHead） ---
        // 开场 _count = 4（A8+）／5 与 1 层 SlowPower 都发生在 AfterAddedToRoom（已在根里）；
        // 分支每回合减一次计数，所以它进状态名单。
        ThirdPartyAdapterRegistry.RegisterMonsterStateMembers("GiantHead", "_count");
        ThirdPartyAdapterRegistry.RegisterStaticIntMembers("GiantHead", "StartingDeathDmg");
        // GLARE：给每个活着的目标 GlareDuration 层虚弱。
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("GiantHead", "GLARE", GiantHeadGlare);
        // COUNT：意图表里挂了一个 DebuffIntent，但源码回调只打 13 点（没有任何减益实现），
        // 所以登记成空操作——它表达的是「这条行动的非攻击部分就是没有」，免得界面报一条假的未支持意图。
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("GiantHead", "COUNT", NoNonAttackEffect);
        // IT_IS_TIME 的伤害是现算的：StartingDeathDmg - Count * IncrementDmg（Count 为负时继续变大）。
        ThirdPartyAdapterRegistry.RegisterMonsterAttackValues("GiantHead", "IT_IS_TIME", GiantHeadItIsTime);
        // BeforeDeath 只有一句死亡音效，登记为忽略（名字带 Death，不登记会让整场给不出战损）。
        BeforeDeathMirrors.RegisterIgnored(AfpReflection.RequireType("ActsFromThePast.GiantHead"));

        // --- 蛇怪术士（Reptomancer） ---
        // 开场的 SPAWN_DAGGER 是初始行动（MoveState，不是分支）；它带来的匕首在 AfterAddedToRoom 里被
        // 挂上 MinionPower（已在根里）。这里要补的是战斗中召唤的那一步与 SNAKE_STRIKE 的虚弱。
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("Reptomancer", "SPAWN_DAGGER", ReptomancerSpawnDagger);
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("Reptomancer", "SNAKE_STRIKE", ReptomancerSnakeStrike);

        // --- 自爆虫（Exploder） ---
        // 开场 _turnCount = 0（AfterAddedToRoom，已在根里）；分支每回合 +1，所以它进状态名单。
        ThirdPartyAdapterRegistry.RegisterMonsterStateMembers("Exploder", "_turnCount");
        // EXPLODE：DeathBlowIntent 就是攻击意图（SingleAttackIntent 的派生），30 点伤害由通用攻击循环
        // 按意图结算，这里只补源码最后那句 CreatureCmd.Kill(自己, false)。
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("Exploder", "EXPLODE", ExploderExplode);
        ThirdPartyAdapterRegistry.RegisterOwnerRemovingMove("Exploder", "EXPLODE");

        // --- 多努（Donu，与 Deca 同场） ---
        // 初始行动是 CIRCLE_OF_PROTECTION、之后与 BEAM 交替（没有分支）；开场的 Artifact 发生在
        // AfterAddedToRoom（已在根里）。要补的只有「保护之环」那一下给全体存活队友的力量。
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect(
            "Donu",
            "CIRCLE_OF_PROTECTION",
            DonuCircleOfProtection);

        // --- 戴卡（Deca，与 Donu 同场） ---
        // 行动在 BEAM 与 SQUARE_OF_PROTECTION 之间交替（没有分支）；开场的 Artifact 在 AfterAddedToRoom。
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("Deca", "BEAM", DecaBeam);
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect(
            "Deca",
            "SQUARE_OF_PROTECTION",
            DecaSquareOfProtection);
        // AFTP 自己的 PlatedArmorPower（Deca 的「保护之方」挂的那一个）：三个钩子逐一登记。
        ThirdPartyAdapterRegistry.RegisterSideTurnStartPower("PlatedArmorPower", PlatedArmorStart);
        BeforeSideTurnEndMirrors.RegisterEarly(_platedArmorType, PlatedArmorTurnEnd);
        AfterDamageReceivedMirrors.Register(_platedArmorType, PlatedArmorDamageReceived);

        // --- AFTP MalleablePower（蛇草的「可塑」；蛇草本体下一批接） ---
        // 它的私有 _pendingBlock 会在克隆时丢掉，所以按心脏无敌那套：根捕获把实机值搬进预测状态，
        // 并登记一个指纹槽（只在累计值上不同的两条分支否则会被去重掉一条）。
        AfterDamageReceivedMirrors.Register(_malleableType, MalleableDamageReceived);
        AfterAttackMirrors.Register(_malleableType, MalleableAfterAttack);
        ThirdPartyAdapterRegistry.RegisterSideTurnEndPower("MalleablePower", MalleableSideTurnEnd);
        PowerHiddenStateMirrors.RegisterRootCapture(
            _malleableType,
            (simulator, clone, original) => _ = simulator.StateStore.GetReadOnly(
                clone,
                () => new MalleablePendingBlockState(original)));
        PowerHiddenStateMirrors.Register(
            _malleableType,
            "pendingBlock",
            static (simulator, power) => simulator.StateStore
                .Peek(power, () => new MalleablePendingBlockState(power))
                .PendingBlock);

        // --- 蛇草（SnakePlant） ---
        // 开场的 3 层 MalleablePower 发生在 AfterAddedToRoom（已在根里，镜像见上）；
        // CHOMP 是常量构造的三段攻击；这里只补 SPORES。
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("SnakePlant", "SPORES", SnakePlantSpores);

        // --- 铜制自动机（BronzeAutomaton，第二幕首领）与它召唤的铜制球体（BronzeOrb） ---
        // 自动机：初始行动就是 SPAWN_ORBS；开场 _numTurns = 0 与 3 层 Artifact 都在 AfterAddedToRoom
        // （已在根里）。分支每回合改计数，所以它进状态名单；BOOST 的两个数值是 A8/A9 的运行期属性。
        ThirdPartyAdapterRegistry.RegisterMonsterStateMembers("BronzeAutomaton", "_numTurns");
        ThirdPartyAdapterRegistry.RegisterStaticIntMembers("BronzeAutomaton", "BlockAmount", "StrAmount");
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect(
            "BronzeAutomaton",
            "SPAWN_ORBS",
            BronzeAutomatonSpawnOrbs);
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("BronzeAutomaton", "BOOST", BronzeAutomatonBoost);
        // BeforeDeath：震屏 + 杀掉存活队友。后半是**原版规则**（主敌死亡时杀掉存活的 secondary 队友，
        // 核心已镜像，球体都带 MinionPower），所以按「已复核无剩余玩法影响」登记为忽略。
        BeforeDeathMirrors.RegisterIgnored(AfpReflection.RequireType("ActsFromThePast.BronzeAutomaton"));

        // 球体：分支读写的 _usedStasis 进状态名单；SUPPORT_BEAM 要给正牌自动机加格挡；
        // STASIS 偷牌（被偷的牌存在预测状态里，球体死亡时由 StasisPower 的镜像归还）。
        ThirdPartyAdapterRegistry.RegisterMonsterStateMembers("BronzeOrb", "_usedStasis");
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("BronzeOrb", "SUPPORT_BEAM", BronzeOrbSupportBeam);
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("BronzeOrb", "STASIS", BronzeOrbStasis);
        BeforeDeathMirrors.Register(_stasisPowerType, StasisPowerBeforeDeath);
        // 终局口径：被偷的牌要算进「未追回战利品」，球体死亡时核销（原版只认 SwipePower／Thief 那类）。
        ThirdPartyAdapterRegistry.RegisterStolenCardPower(
            "StasisPower",
            static (simulator, power) => simulator.StateStore
                .Get(power, static () => new StasisStolenCardState())
                .StolenCard is not null);
        // 被偷的牌要进指纹：只在「偷了哪张牌」上不同的两条分支不能被去重成一条。
        PowerHiddenStateMirrors.Register(
            _stasisPowerType,
            "stolenCard",
            static (simulator, power) => simulator.StateStore
                .Peek(power, static () => new StasisStolenCardState())
                .CardIdentity);

        // --- 甲壳寄生虫（ShelledParasite） ---
        // 开场 14 层 PlatedArmorPower 在 AfterAddedToRoom（已在根里，镜像见上）。
        ThirdPartyAdapterRegistry.RegisterStaticIntMembers("ShelledParasite", "SuckDamage");
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("ShelledParasite", "FELL", ShelledParasiteFell);
        // LIFE_SUCK 要按这次攻击的**未被格挡伤害**回血——行动效果拿不到伤害结果，所以走「攻击结算之后」那张表。
        ThirdPartyAdapterRegistry.RegisterMonsterMoveAttackResults(
            "ShelledParasite",
            "LIFE_SUCK",
            ShelledParasiteLifeSuck);
        // BeforeDeath 是空重写（只有基类调用），登记为忽略（名字带 Death，不登记会让整场给不出战损）。
        BeforeDeathMirrors.RegisterIgnored(AfpReflection.RequireType("ActsFromThePast.ShelledParasite"));

        // --- 收集者（Collector，第二幕精英／首领级） ---
        // 开场 _turnsTaken=0／_ultUsed=false／_initialSpawn=true 与「火焰粒子循环」都在 AfterAddedToRoom
        // （前三个已在根里播种；粒子循环是纯表现）。分支读写这三个标量，所以都进状态名单。
        ThirdPartyAdapterRegistry.RegisterMonsterStateMembers(
            "Collector",
            "_turnsTaken",
            "_ultUsed",
            "_initialSpawn");
        ThirdPartyAdapterRegistry.RegisterStaticIntMembers(
            "Collector",
            "BlockAmount",
            "StrengthAmount",
            "MegaDebuffAmount");
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("Collector", "SPAWN", CollectorSpawn);
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("Collector", "BUFF", CollectorBuff);
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("Collector", "MEGA_DEBUFF", CollectorMegaDebuff);
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("Collector", "REVIVE", CollectorRevive);
        // BeforeDeath：震屏 + 杀掉存活火炬头。后半是**原版规则**（主敌死亡时杀掉存活的 secondary 队友，
        // 火炬头都带 MinionPower，核心已镜像）⇒ 登记为忽略。
        BeforeDeathMirrors.RegisterIgnored(AfpReflection.RequireType("ActsFromThePast.Collector"));

        // --- 小鬼首领（GremlinLeader） ---
        // 开场给队友挂 MinionPower 在 AfterAddedToRoom（已在根里）；ENCOURAGE 的两个数值是 A9 运行期属性。
        ThirdPartyAdapterRegistry.RegisterStaticIntMembers("GremlinLeader", "StrengthAmount", "BlockAmount");
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("GremlinLeader", "RALLY", GremlinLeaderRally);
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("GremlinLeader", "ENCOURAGE", GremlinLeaderEncourage);
        // BeforeDeath：先把存活小鬼的 MinionPower 摘掉（源码如此），再让它们**逃跑**——小鬼的
        // AfterAddedToRoom 订阅的是首领的 Died C# 事件，而模拟器不触发 C# 事件，所以这条语义由首领侧
        // 一次做完（逃跑＋摘 MinionPower）。摘掉 MinionPower 同时避免了「主敌死亡杀掉存活 secondary 队友」
        // 那条原版规则误杀它们——这正是源码的顺序。
        BeforeDeathMirrors.Register(
            AfpReflection.RequireType("ActsFromThePast.GremlinLeader"),
            GremlinLeaderBeforeDeath);

        // --- 鸟（Byrd，与 §2.12 的史莱姆三件套同幕） ---
        // 开场挂 FlightPower（层数 FlightAmount）在 AfterAddedToRoom（已在根里）；这里补 CAW 与 GO_AIRBORNE，
        // 以及 FlightPower 自己的三条语义。
        ThirdPartyAdapterRegistry.RegisterStaticIntMembers("Byrd", "FlightAmount");
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("Byrd", "CAW", ByrdCaw);
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("Byrd", "GO_AIRBORNE", ByrdGoAirborne);
        // FlightPower：飞行时受到的 Move 伤害 ×0.5 走的是 ModifyDamageMultiplicative 的**原版回退调用**
        // （核心里这条入口对未登记类型会调用监听者自己的实现，而那个实现只读 Owner 与 props、不改状态），
        // 所以不需要新入口。这里只登记「挨打减层 + 层数归零打落眩晕」与「自己那一方回合开始回滚层数」。
        AfterDamageReceivedMirrors.Register(_flightPowerType, FlightPowerDamageReceived);
        ThirdPartyAdapterRegistry.RegisterSideTurnStartPower("FlightPower", FlightPowerTurnStart);
        // BeforeDeath 只有一句死亡音效 ⇒ 登记为忽略（名字带 Death，不登记会让整场给不出战损）。
        BeforeDeathMirrors.RegisterIgnored(AfpReflection.RequireType("ActsFromThePast.Byrd"));

        // --- 复仇女神（Nemesis，第三幕） ---
        // 开场只有 Died 事件与火焰粒子（纯表现）；AfterPowerAmountChanged 也只改透明度。
        ThirdPartyAdapterRegistry.RegisterMonsterStateMembers(
            "Nemesis",
            "_firstMove",
            "_scytheCooldown",
            "_shouldApplyIntangible");
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("Nemesis", "TRI_BURN", NemesisTriBurn);
        // 它**自己**（怪物模型，不是 Power）重写了常规（非 Late）AfterSideTurnEnd：敌人回合末在
        // 「有无实体化」之间切换。这条走本轮新增的「非 Power 模型」入口。
        ThirdPartyAdapterRegistry.RegisterSideTurnEndModel("Nemesis", NemesisSideTurnEnd);
        BeforeDeathMirrors.RegisterIgnored(AfpReflection.RequireType("ActsFromThePast.Nemesis"));

        // --- 瞬逝者（Transient，第三幕） ---
        // 开场挂 FadingPower（A8+ 6／否则 5）与 1 层 ShiftingPower 在 AfterAddedToRoom（已在根里）。
        // 只有一个 ATTACK 行动：伤害 = StartingDeathDmg + _count * IncrementDmg（现算），打完 _count++。
        ThirdPartyAdapterRegistry.RegisterMonsterStateMembers("Transient", "_count");
        ThirdPartyAdapterRegistry.RegisterStaticIntMembers("Transient", "StartingDeathDmg");
        ThirdPartyAdapterRegistry.RegisterMonsterAttackValues("Transient", "ATTACK", TransientAttack);
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("Transient", "ATTACK", TransientAttackEffect);
        // FadingPower.BeforeSideTurnEndEarly：自己那一方回合末递减，最后一层时直接死掉（源码里还有烟雾特效）。
        BeforeSideTurnEndMirrors.RegisterEarly(_fadingPowerType, FadingPowerTurnEnd);
        // ShiftingPower.AfterDamageReceived：挨到任何真伤害就按 TotalDamage 给自己叠一层负数临时力量
        // （ShiftingStrengthDownPower，TemporaryStrengthPower 的子类；核心按运行时类型的施加入口已存在）。
        AfterDamageReceivedMirrors.Register(_shiftingPowerType, ShiftingPowerDamageReceived);

        // --- 拉瓦格林（Lagavulin，第一幕精英） ---
        // 开场：StartsAwake 时直接醒着；否则挂 8 层 AFTP MetallicizePower（镜像早已在 CityHooks）与
        // 3 层 AsleepLagavulinPower（都在 AfterAddedToRoom，已在根里）。分支读的四个标量都进状态名单。
        ThirdPartyAdapterRegistry.RegisterMonsterStateMembers(
            "Lagavulin",
            "_isAwake",
            "_debuffTurnCount",
            "_sleepTurnCount",
            "_startsAwake");
        ThirdPartyAdapterRegistry.RegisterStaticIntMembers("Lagavulin", "DebuffAmount");
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("Lagavulin", "SLEEP", LagavulinSleep);
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("Lagavulin", "ATTACK", LagavulinAttack);
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("Lagavulin", "DEBUFF", LagavulinDebuff);
        // AsleepLagavulinPower 的四条钩子（它自己有 BeforeSideTurnStart／VeryEarly／常规回合末／受伤唤醒）。
        AfterDamageReceivedMirrors.Register(_asleepLagavulinType, AsleepLagavulinDamageReceived);
        ThirdPartyAdapterRegistry.RegisterSideTurnStartPower("AsleepLagavulinPower", AsleepLagavulinTurnStart);
        BeforeSideTurnEndMirrors.RegisterVeryEarly(_asleepLagavulinType, AsleepLagavulinTurnEndVeryEarly);
        ThirdPartyAdapterRegistry.RegisterSideTurnEndPower("AsleepLagavulinPower", AsleepLagavulinTurnEnd);

        // --- 蠕动肉块（WrithingMass，第三幕） ---
        // 开场挂 ReactivePower 1 与 MalleablePower 3（都在 AfterAddedToRoom，已在根里；后者镜像见 §2.29）。
        ThirdPartyAdapterRegistry.RegisterMonsterStateMembers("WrithingMass", "_firstMove", "_usedMegaDebuff");
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("WrithingMass", "ATTACK_BLOCK", WrithingAttackBlock);
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("WrithingMass", "ATTACK_DEBUFF", WrithingAttackDebuff);
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("WrithingMass", "MEGA_DEBUFF", WrithingMegaDebuff);
        // ReactivePower.AfterDamageReceived：挨打后**随机改掉自己下一个行动**（用共享的 MonsterAi 流，
        // 候选来自它自己的行动表，排除当前行动与 MOVE_BRANCH，用过 MEGA_DEBUFF 就排除它）。
        AfterDamageReceivedMirrors.Register(_reactivePowerType, ReactivePowerDamageReceived);

        // --- 时间吞噬者（TimeEater，第三幕首领） ---
        // 开场挂 1 层 TimeWarpPower 在 AfterAddedToRoom（已在根里）。分支读写的 _usedHaste 与 _firstTurn 进状态名单。
        ThirdPartyAdapterRegistry.RegisterMonsterStateMembers("TimeEater", "_usedHaste", "_firstTurn");
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("TimeEater", "RIPPLE", TimeEaterRipple);
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("TimeEater", "HEAD_SLAM", TimeEaterHeadSlam);
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("TimeEater", "HASTE", TimeEaterHaste);
        // TimeWarpPower.AfterCardPlayed：每打一张牌计数 +1，数到 12 就清零、强制结束玩家回合、给所有敌人 2 力量。
        AfterCardPlayedMirrors.Register(_timeWarpPowerType, TimeWarpCardPlayed);
        // 卡牌计数决定「什么时候强制结束回合」，所以必须进指纹；根捕获把实机值搬进预测状态。
        PowerHiddenStateMirrors.RegisterRootCapture(
            _timeWarpPowerType,
            (simulator, clone, original) => _ = simulator.StateStore.GetReadOnly(
                clone,
                () => new TimeWarpPredictionState(original)));
        PowerHiddenStateMirrors.Register(
            _timeWarpPowerType,
            "cardCount",
            static (simulator, power) => simulator.StateStore
                .Peek(power, static () => new TimeWarpPredictionState())
                .CardCount);
        // DrawReductionPower（HEAD_SLAM 挂的「每回合少抽一张」）：
        // 它的 ModifyHandDraw 由核心的原版钩子路径直接调用**影子状态**里的 Power（与 ModifyDamage 同一机制），
        // 不需要镜像；只有持续时间递减要走我们自己的常规回合末入口（TickDurations 只认原版那几个类型）。
        ThirdPartyAdapterRegistry.RegisterSideTurnEndPower("DrawReductionPower", DrawReductionTurnEnd);

        // --- 六角幽魂（Hexaghost，第一幕首领） ---
        // 开场 _activated/_burnUpgraded/_orbActiveCount 都在 AfterAddedToRoom（已在根里）；初始行动是 ACTIVATE，
        // 之后被强制走 DIVIDER，再进 MOVE_BRANCH。四个标量都进状态名单。
        ThirdPartyAdapterRegistry.RegisterMonsterStateMembers(
            "Hexaghost",
            "_activated",
            "_burnUpgraded",
            "_orbActiveCount",
            "_dividerDamage");
        ThirdPartyAdapterRegistry.RegisterStaticIntMembers("Hexaghost", "StrengthAmount", "SearBurnCount");
        // DIVIDER 的段数固定 6、伤害是 ACTIVATE 当时按玩家平均生命算出来的 _dividerDamage（现算）。
        ThirdPartyAdapterRegistry.RegisterMonsterAttackValues("Hexaghost", "DIVIDER", HexaghostDivider);
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("Hexaghost", "ACTIVATE", HexaghostActivate);
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("Hexaghost", "DIVIDER", HexaghostDeactivateOrbs);
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("Hexaghost", "TACKLE", HexaghostActivateOrb);
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("Hexaghost", "INFLAME", HexaghostInflame);
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("Hexaghost", "SEAR", HexaghostSear);
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("Hexaghost", "INFERNO", HexaghostInferno);
        // AfterDeath 早已在 ExordiumHooks 登记为忽略（只隐藏球体与震屏）；它同样没有 BeforeDeath 重写。

        // --- 守护者（Guardian，第一幕精英）：两个 Power 的镜像（本体行动下一批接） ---
        // 形态切换要读写的标量（_isOpen／_closeUpTriggered／_pendingModeShift／_isExecutingMove／_nextThreshold）
        // 先声明进状态名单；它们的播种发生在 AfterAddedToRoom（根捕获），所以下一批接本体时不需要补播种代码。
        ThirdPartyAdapterRegistry.RegisterMonsterStateMembers(
            "Guardian",
            "_nextThreshold",
            "_isOpen",
            "_closeUpTriggered",
            "_pendingModeShift",
            "_isExecutingMove");
        ThirdPartyAdapterRegistry.RegisterStaticIntMembers(
            "Guardian",
            "DmgThresholdBase",
            "SharpHideThorns");
        AfterDamageReceivedMirrors.Register(_modeShiftType, ModeShiftDamageReceived);
        BeforeCardPlayedMirrors.Register(_sharpHideType, SharpHideBeforeCardPlayed);
        AfterCardPlayedMirrors.Register(_sharpHideType, SharpHideAfterCardPlayed);
        // 守护者本体：分支 OFFENSIVE_BRANCH 之外，七个行动的效果与「延迟形态切换」的检查点。
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("Guardian", "CHARGE_UP", GuardianChargeUp);
        ThirdPartyAdapterRegistry.RegisterMonsterMoveBeforeAttack(
            "Guardian",
            "FIERCE_BASH",
            GuardianBeginMove);
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect(
            "Guardian",
            "FIERCE_BASH",
            GuardianEndMove);
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("Guardian", "VENT_STEAM", GuardianVentSteam);
        ThirdPartyAdapterRegistry.RegisterMonsterMoveBeforeAttack(
            "Guardian",
            "WHIRLWIND",
            GuardianBeginMove);
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect(
            "Guardian",
            "WHIRLWIND",
            GuardianEndMove);
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("Guardian", "CLOSE_UP", GuardianCloseUp);
        ThirdPartyAdapterRegistry.RegisterMonsterMoveBeforeAttack(
            "Guardian",
            "TWIN_SLAM",
            GuardianBeginTwinSlam);
        ThirdPartyAdapterRegistry.RegisterMonsterMoveEffect("Guardian", "TWIN_SLAM", GuardianEndTwinSlam);
        // BeforeDeath：死亡时如果正好在一次攻击过程中，按尖刺外壳的层数给攻击者补一刀（Unpowered）。
        BeforeDeathMirrors.Register(AfpReflection.RequireType("ActsFromThePast.Guardian"), GuardianBeforeDeath);
    }

    /// <summary>Guardian.CheckPendingModeShift：行动收尾时把「执行中攒下的」形态切换补上。</summary>
    private static void GuardianCheckPendingModeShift(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        Creature guardian)
    {
        if (!combat.GetMonsterBool(guardian, "_pendingModeShift"))
            return;
        combat.SetMonsterBool(guardian, "_pendingModeShift", false);
        combat.SetMonsterBool(guardian, "_closeUpTriggered", true);
        GuardianTransitionToDefensiveMode(simulator, combat, guardian, setMove: false);
    }

    /// <summary>Guardian.ChargeUp：自己 9 格挡（`Move`），收尾检查延迟的形态切换。</summary>
    private static bool GuardianChargeUp(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        _ = player;
        _ = plannedChoices;
        killedOwner = false;
        simulator.GainBlock(move.Owner, _guardianChargeUpBlock, ValueProp.Move);
        GuardianCheckPendingModeShift(simulator, combat, move.Owner);
        return true;
    }

    /// <summary>Guardian.FierceBash／Whirlwind 的**攻击前**部分：把 `_isExecutingMove` 置真（阈值归零时改为延迟切换）。</summary>
    private static void GuardianBeginMove(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player)
    {
        _ = simulator;
        _ = player;
        combat.SetMonsterBool(move.Owner, "_isExecutingMove", true);
    }

    /// <summary>攻击收尾：清掉 `_isExecutingMove` 并检查延迟的形态切换。</summary>
    private static bool GuardianEndMove(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        _ = player;
        _ = plannedChoices;
        killedOwner = false;
        combat.SetMonsterBool(move.Owner, "_isExecutingMove", false);
        GuardianCheckPendingModeShift(simulator, combat, move.Owner);
        return true;
    }

    /// <summary>Guardian.VentSteam：给每个活着的目标 2 层虚弱与 2 层易伤，收尾检查延迟的形态切换。</summary>
    private static bool GuardianVentSteam(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        _ = plannedChoices;
        killedOwner = false;
        if (simulator.State.GetCreature(player).IsAlive)
        {
            combat.Apply<WeakPower>(player, _guardianVentDebuff, move.Owner);
            combat.Apply<VulnerablePower>(player, _guardianVentDebuff, move.Owner);
        }
        GuardianCheckPendingModeShift(simulator, combat, move.Owner);
        return true;
    }

    /// <summary>Guardian.CloseUp：给自己挂 <c>SharpHideThorns</c> 层尖刺外壳（后续 ROLL_ATTACK→TWIN_SLAM 是固定链）。</summary>
    private static bool GuardianCloseUp(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        _ = simulator;
        _ = player;
        _ = plannedChoices;
        killedOwner = false;
        combat.ApplyPower(
            _sharpHideType,
            move.Owner,
            combat.GetMonsterStaticInt(move.Owner, "SharpHideThorns"),
            move.Owner);
        return true;
    }

    /// <summary>
    /// Guardian.TwinSlam 的**攻击前**部分：置 `_isExecutingMove`，再转回攻击形态（源码就是在攻击前调
    /// <c>TransitionToOffensiveMode</c>——顺序反了会让「攻击过程中挨反伤」落到错误的形态上）。
    /// </summary>
    private static void GuardianBeginTwinSlam(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player)
    {
        _ = player;
        combat.SetMonsterBool(move.Owner, "_isExecutingMove", true);
        GuardianTransitionToOffensiveMode(simulator, combat, move.Owner);
    }

    /// <summary>Guardian.TwinSlam 的攻击后部分：摘掉尖刺外壳，收尾并检查延迟切换。</summary>
    private static bool GuardianEndTwinSlam(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        _ = player;
        _ = plannedChoices;
        killedOwner = false;
        foreach (PowerModel power in combat.EffectivePowers())
        {
            if (ReferenceEquals(power.Owner, move.Owner)
                && power.GetType() == _sharpHideType
                && power.Amount > 0)
            {
                combat.SetPowerAmount(power, 0);
            }
        }
        combat.SetMonsterBool(move.Owner, "_isExecutingMove", false);
        GuardianCheckPendingModeShift(simulator, combat, move.Owner);
        return true;
    }

    /// <summary>
    /// Guardian.BeforeDeath：死亡瞬间若尖刺外壳记录着「正在进行的攻击」且来源还活着，就按外壳层数给那个来源
    /// 补一刀 <c>Unpowered</c> 伤害（源码在 <c>BeforeDeath</c> 里直接查那次攻击的记录）。
    /// </summary>
    private static void GuardianBeforeDeath(AbstractModel model, BeforeDeathMirrorContext context)
    {
        MonsterModel guardian = (MonsterModel)model;
        if (!ReferenceEquals(context.Creature, guardian.Creature))
            return;
        if (context.CombatState is not SimulatedCombatState combat)
            throw new PredictionUnsupportedException("守护者的死亡补刀缺少可写的预测状态。");
        foreach (PowerModel power in combat.EffectivePowers())
        {
            if (!ReferenceEquals(power.Owner, guardian.Creature)
                || power.GetType() != _sharpHideType
                || power.Amount <= 0)
            {
                continue;
            }
            SharpHideAttackState state = context.StateStore
                .Peek(power, static () => new SharpHideAttackState());
            if (!state.AttackInProgress
                || state.AttackSource is not { } source
                || !context.Simulator.State.GetCreature(source).IsAlive)
            {
                continue;
            }
            using (context.Simulator.PushDamageSource(
                CombatDamageSource.For(CombatDamageSourceKind.Power, "SharpHidePower")))
            {
                context.Simulator.Damage(source, power.Amount, ValueProp.Unpowered, null);
            }
        }
    }

    /// <summary>
    /// AFTP <c>ModeShiftPower.AfterDamageReceived</c>：把**未被格挡的伤害**从阈值里扣掉（层数就是剩余阈值），
    /// 扣到 0 就转防御形态——正在执行行动时先记 <c>_pendingModeShift</c>，等这次行动收尾再切。
    /// </summary>
    private static void ModeShiftDamageReceived(AbstractModel model, AfterDamageReceivedMirrorContext context)
    {
        PowerModel power = (PowerModel)model;
        if (context.Target != power.Owner || context.Result.UnblockedDamage <= 0)
            return;
        if (context.CombatState is not SimulatedCombatState combat)
            throw new PredictionUnsupportedException("形态切换缺少可写的预测状态。");
        string monsterTypeName = power.Owner.Monster?.GetType().Name ?? string.Empty;
        if (!string.Equals(monsterTypeName, "Guardian", StringComparison.Ordinal)
            || combat.GetMonsterBool(power.Owner, "_isOpen") is false
            || combat.GetMonsterBool(power.Owner, "_closeUpTriggered")
            || context.Simulator.State.GetCreature(power.Owner).IsDead)
        {
            return;
        }
        ICombatPredictionEffectSink effects = context.CombatState as ICombatPredictionEffectSink
            ?? throw new PredictionUnsupportedException("形态切换缺少可写的预测状态。");
        int remaining = Math.Max(0, power.Amount - context.Result.UnblockedDamage);
        effects.SetPowerAmount(power, remaining);
        if (remaining > 0)
            return;
        combat.SetMonsterBool(power.Owner, "_closeUpTriggered", true);
        if (combat.GetMonsterBool(power.Owner, "_isExecutingMove"))
        {
            combat.SetMonsterBool(power.Owner, "_pendingModeShift", true);
            return;
        }
        // 源码这里调的是 TransitionToDefensiveMode()（setMove 默认 true）：玩家回合内把阈值打空会让
        // 守护者**当场**把下一个行动改成 CLOSE_UP，意图随之改变；延迟那条路径才用 setMove: false。
        GuardianTransitionToDefensiveMode(context.Simulator, combat, power.Owner, setMove: true);
    }

    /// <summary>
    /// Guardian.TransitionToDefensiveMode：摘掉 `ModeShiftPower`、阈值 +10、自己 20 格挡（<c>Move</c>）、
    /// 置 `_isOpen = false`；<paramref name="setMove"/> 为真时把当前行动强制改成 CLOSE_UP
    /// （源码是 <c>SetMoveImmediate(_closeUpState, true)</c>；CLOSE_UP 的后继链是固定的 ROLL_ATTACK→TWIN_SLAM，
    /// 所以核心这个没有 must-perform 标志的强制入口在这里等价）。
    /// </summary>
    internal static void GuardianTransitionToDefensiveMode(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        Creature guardian,
        bool setMove)
    {
        foreach (PowerModel power in combat.EffectivePowers())
        {
            if (ReferenceEquals(power.Owner, guardian) && power.GetType() == _modeShiftType && power.Amount > 0)
                combat.SetPowerAmount(power, 0);
        }
        combat.SetMonsterInt(
            guardian,
            "_nextThreshold",
            combat.GetMonsterInt(guardian, "_nextThreshold") + _guardianThresholdIncrease);
        simulator.GainBlock(guardian, _guardianDefensiveBlock, ValueProp.Move);
        combat.SetMonsterBool(guardian, "_isOpen", false);
        if (setMove)
            combat.ForceMonsterMove(guardian, "CLOSE_UP");
    }

    /// <summary>
    /// Guardian.TransitionToOffensiveMode：按当前阈值重新挂 `ModeShiftPower`、清空自己的格挡、
    /// 置 `_isOpen = true` 并把 `_closeUpTriggered` 复位（源码 TWIN_SLAM 里调它）。
    /// </summary>
    internal static void GuardianTransitionToOffensiveMode(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        Creature guardian)
    {
        combat.ApplyPower(
            _modeShiftType,
            guardian,
            combat.GetMonsterInt(guardian, "_nextThreshold"),
            guardian);
        SimCreatureState state = simulator.State.GetCreature(guardian);
        if (state.Block > 0)
            state.LoseBlock(state.Block);
        combat.SetMonsterBool(guardian, "_isOpen", true);
        combat.SetMonsterBool(guardian, "_closeUpTriggered", false);
    }

    /// <summary>
    /// AFTP <c>SharpHidePower.BeforeCardPlayed</c>：打出的是**攻击牌**时记下来源，供 <c>AfterCardPlayed</c>
    /// 与本体的 <c>BeforeDeath</c>（死亡时补一刀）使用。状态放预测状态里（随 Fork 复制）。
    /// </summary>
    private static void SharpHideBeforeCardPlayed(
        AbstractModel model,
        BeforeCardPlayedMirrorContext context)
    {
        PowerModel power = (PowerModel)model;
        if (context.CardPlay.Card.Type != CardType.Attack)
            return;
        SharpHideAttackState state = context.Simulator.StateStore
            .Get(power, static () => new SharpHideAttackState());
        state.AttackInProgress = true;
        state.AttackSource = context.CardPlay.Card.Owner?.Creature;
    }

    /// <summary>
    /// AFTP <c>SharpHidePower.AfterCardPlayed</c>：清掉记录的来源；只要打出的是攻击牌，就让出牌者吃
    /// <c>Amount</c> 点 <c>Unpowered</c> 伤害（与是否打到守护者无关）。
    /// </summary>
    private static void SharpHideAfterCardPlayed(
        AbstractModel model,
        AfterCardPlayedMirrorContext context)
    {
        PowerModel power = (PowerModel)model;
        SharpHideAttackState state = context.Simulator.StateStore
            .Get(power, static () => new SharpHideAttackState());
        state.AttackInProgress = false;
        state.AttackSource = null;
        if (context.CardPlay.Card.Type != CardType.Attack)
            return;
        Creature? player = context.CardPlay.Card.Owner?.Creature;
        if (player is null || !context.Simulator.State.GetCreature(player).IsAlive)
            return;
        using (context.Simulator.PushDamageSource(
            CombatDamageSource.For(CombatDamageSourceKind.Power, "SharpHidePower")))
        {
            context.Simulator.Damage(player, power.Amount, ValueProp.Unpowered, null);
        }
    }

    /// <summary>
    /// AFTP <c>SharpHidePower</c> 的两个私有标记（哪次攻击正在进行、来源是谁）在预测里的分支副本；
    /// 本体死亡时要用它补那一刀，所以必须随 Fork 复制。
    /// </summary>
    internal sealed class SharpHideAttackState : IPredictionStateForkable
    {
        public bool AttackInProgress { get; set; }

        public Creature? AttackSource { get; set; }

        public object Fork(PredictionForkContext context) => MemberwiseClone();
    }

    /// <summary>Hexaghost.DIVIDER：段数固定 6，伤害是当时算好的 <c>_dividerDamage</c>。</summary>
    private static BranchMonsterAttack HexaghostDivider(
        SimulatedCombatState combat,
        MonsterModel monster)
        => new(combat.GetMonsterInt(monster.Creature, "_dividerDamage"), 6);

    /// <summary>
    /// Hexaghost.Activate：置 `_activated`、把球体计数拉到 6，并按**存活玩家的当前生命**算 DIVIDER 伤害
    /// （源码 <c>(int)(平均生命 / 12) + 1</c>；本 Mod 只支持单人，平均即该玩家）。
    /// </summary>
    private static bool HexaghostActivate(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        _ = plannedChoices;
        killedOwner = false;
        combat.SetMonsterBool(move.Owner, "_activated", true);
        combat.SetMonsterInt(move.Owner, "_orbActiveCount", 6);
        int hp = simulator.State.GetCreature(player).CurrentHp;
        combat.SetMonsterInt(move.Owner, "_dividerDamage", hp / 12 + 1);
        return true;
    }

    /// <summary>Hexaghost.Divider / Inferno 收尾：把所有球体熄灭（计数归零）。</summary>
    private static bool HexaghostDeactivateOrbs(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        _ = simulator;
        _ = player;
        _ = plannedChoices;
        killedOwner = false;
        combat.SetMonsterInt(move.Owner, "_orbActiveCount", 0);
        return true;
    }

    /// <summary>Hexaghost.Tackle / Sear / Inflame 收尾：点亮一个球体（计数 +1）。</summary>
    private static bool HexaghostActivateOrb(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        _ = simulator;
        _ = player;
        _ = plannedChoices;
        killedOwner = false;
        combat.SetMonsterInt(
            move.Owner,
            "_orbActiveCount",
            combat.GetMonsterInt(move.Owner, "_orbActiveCount") + 1);
        return true;
    }

    /// <summary>Hexaghost.Inflame：自己 12 格挡（`Move`）＋ `StrengthAmount` 点力量，然后点亮一个球体。</summary>
    private static bool HexaghostInflame(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        killedOwner = false;
        simulator.GainBlock(move.Owner, 12, ValueProp.Move);
        combat.Apply<StrengthPower>(
            move.Owner,
            combat.GetMonsterStaticInt(move.Owner, "StrengthAmount"),
            move.Owner);
        HexaghostActivateOrb(simulator, combat, move, player, plannedChoices, out _);
        return true;
    }

    /// <summary>
    /// Hexaghost.Sear：攻击之后往弃牌堆底部塞 <c>SearBurnCount</c> 张 Burn；**已经升级过 Burn 之后**
    /// （`_burnUpgraded`）塞的是**已升级**的 Burn，再点亮一个球体。
    /// </summary>
    private static bool HexaghostSear(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        killedOwner = false;
        HexaghostAddBurns(
            simulator,
            combat,
            player,
            combat.GetMonsterStaticInt(move.Owner, "SearBurnCount"),
            combat.GetMonsterBool(move.Owner, "_burnUpgraded"));
        HexaghostActivateOrb(simulator, combat, move, player, plannedChoices, out _);
        return true;
    }

    /// <summary>
    /// Hexaghost.Inferno：攻击之后把玩家手牌／抽牌堆／弃牌堆里**所有可升级的 Burn** 升级，再塞 3 张
    /// **已升级**的 Burn 进弃牌堆底部，置 `_burnUpgraded`，最后熄灭所有球体。
    /// </summary>
    /// <remarks>
    /// 源码是先 <c>UpgradeInternal</c> 再入堆；这里用「入堆后立刻升级」表达——两者对牌堆内容等价，
    /// 差别只在「生成牌」那条钩子在升级前被派发（已在 docs/AFTP_ACT4HEART_STATUS.md §2.42 记明）。
    /// </remarks>
    private static bool HexaghostInferno(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        killedOwner = false;
        Player? targetPlayer = player.Player ?? player.PetOwner;
        if (targetPlayer is null)
            return true;
        SimPlayerCombatState playerState = simulator.State.GetPlayerCombatState(targetPlayer);
        List<PredictedCard> burns =
        [
            .. playerState.Hand.Cards.Where(static card => card.Preview is Burn),
            .. playerState.DrawPile.Cards.Where(static card => card.Preview is Burn),
            .. playerState.DiscardPile.Cards.Where(static card => card.Preview is Burn),
        ];
        foreach (PredictedCard burn in burns)
        {
            burn.MutablePreview.UpgradeInternal();
            burn.MutablePreview.FinalizeUpgradeInternal();
        }
        HexaghostAddBurns(simulator, combat, player, 3, upgraded: true);
        combat.SetMonsterBool(move.Owner, "_burnUpgraded", true);
        HexaghostDeactivateOrbs(simulator, combat, move, player, plannedChoices, out _);
        return true;
    }

    /// <summary>
    /// 往弃牌堆底部塞 <paramref name="count"/> 张 Burn；<paramref name="upgraded"/> 为真时逐张升级
    /// （源码在 <c>BurnUpgradePatch.AllowBurnUpgrade</c> 打开时创建的就是升级版 Burn）。
    /// </summary>
    private static void HexaghostAddBurns(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        Creature player,
        int count,
        bool upgraded)
    {
        _ = combat;
        Player? targetPlayer = player.Player ?? player.PetOwner;
        if (targetPlayer is null || count <= 0)
            return;
        IReadOnlyList<SimCardPileAddResult> added = simulator.CreateAndAddGeneratedCardsToCombat<Burn>(
            targetPlayer,
            PileType.Discard,
            count,
            null,
            CardPilePosition.Bottom);
        if (!upgraded)
            return;
        foreach (SimCardPileAddResult result in added)
        {
            result.CardAdded.MutablePreview.UpgradeInternal();
            result.CardAdded.MutablePreview.FinalizeUpgradeInternal();
        }
    }

    /// <summary>
    /// AFTP <c>DrawReductionPower.AfterSideTurnEnd</c>（常规、非 Late）：自己那一方回合末减 1 层
    /// （源码走的是 <c>PowerCmd.TickDownDuration</c>，即持续时间递减，归零就移除）。
    /// </summary>
    private static void DrawReductionTurnEnd(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        PowerModel power,
        CombatSide side,
        IReadOnlyCollection<Creature> participants)
    {
        _ = simulator;
        _ = participants;
        if (side != power.Owner.Side)
            return;
        ICombatPredictionEffectSink effects = combat as ICombatPredictionEffectSink
            ?? throw new PredictionUnsupportedException("抽牌减少缺少可写的预测状态。");
        effects.SetPowerAmount(power, Math.Max(0, power.Amount - 1));
    }

    /// <summary>时间吞噬者 RIPPLE：自己 20 格挡（`Move`），再给每个活着的目标 1 层易伤／虚弱／破甲。</summary>
    private static bool TimeEaterRipple(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        _ = plannedChoices;
        killedOwner = false;
        simulator.GainBlock(move.Owner, 20, ValueProp.Move);
        if (!simulator.State.GetCreature(player).IsAlive)
            return true;
        combat.Apply<VulnerablePower>(player, _timeEaterDebuffTurns, move.Owner);
        combat.Apply<WeakPower>(player, _timeEaterDebuffTurns, move.Owner);
        combat.Apply<FrailPower>(player, _timeEaterDebuffTurns, move.Owner);
        return true;
    }

    /// <summary>
    /// 时间吞噬者 HEAD_SLAM：攻击之后给每个活着的目标 1 层「抽牌减少」，再往弃牌堆底部塞 2 张 Slimed
    /// （生成的牌按适配层既有口径登记经典／普通，见 <see cref="ClassicSlimed.RecordGenerated"/>）。
    /// </summary>
    private static bool TimeEaterHeadSlam(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        _ = plannedChoices;
        killedOwner = false;
        if (simulator.State.GetCreature(player).IsAlive)
            combat.ApplyPower(_drawReductionType, player, 1, move.Owner);
        Player? targetPlayer = player.Player ?? player.PetOwner;
        if (targetPlayer is null)
            return true;
        IReadOnlyList<SimCardPileAddResult> added = simulator.CreateAndAddGeneratedCardsToCombat<Slimed>(
            targetPlayer,
            PileType.Discard,
            _timeEaterSlimedCount,
            null,
            CardPilePosition.Bottom);
        foreach (SimCardPileAddResult result in added)
            ClassicSlimed.RecordGenerated(result.CardAdded);
        return true;
    }

    /// <summary>
    /// 时间吞噬者 HASTE：清掉自己身上**所有减益**，血量回到上限一半（不足才回），再按 HEAD_SLAM 的伤害值
    /// 给自己格挡（源码用的是同一个 <c>HeadSlamDamage</c> 属性）。
    /// </summary>
    private static bool TimeEaterHaste(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        _ = player;
        _ = plannedChoices;
        killedOwner = false;
        foreach (PowerModel power in combat.EffectivePowers())
        {
            if (ReferenceEquals(power.Owner, move.Owner)
                && power.Amount > 0
                && power.Type == PowerType.Debuff)
            {
                combat.SetPowerAmount(power, 0);
            }
        }
        SimCreatureState creature = simulator.State.GetCreature(move.Owner);
        int healAmount = creature.MaxHp / 2 - creature.CurrentHp;
        if (healAmount > 0)
            simulator.Heal(move.Owner, healAmount);
        simulator.GainBlock(
            move.Owner,
            combat.GetMonsterStaticInt(move.Owner, "HeadSlamDamage"),
            ValueProp.Move);
        return true;
    }

    /// <summary>
    /// AFTP <c>TimeWarpPower.AfterCardPlayed</c>：计数 +1；数到 <c>Countdown</c>（单人 12）就清零、
    /// **强制结束玩家回合**、再给所有存活敌人 2 点力量。
    /// </summary>
    /// <remarks>
    /// 源码用的是 <c>PlayerCmd.EndTurn</c>（中途立刻结束回合），预测里用
    /// <c>SimulatedCombatState.RequestPlayerTurnEnd</c>——核心既有的「请求结束回合」入口，会在当前这张牌
    /// 的出牌流程结束后收口，与「立刻结束」在单次出牌粒度上等价。
    /// </remarks>
    private static void TimeWarpCardPlayed(AbstractModel model, AfterCardPlayedMirrorContext context)
    {
        PowerModel power = (PowerModel)model;
        if (context.CombatState is not SimulatedCombatState combat)
            throw new PredictionUnsupportedException("时间扭曲缺少可写的预测状态。");
        TimeWarpPredictionState state = context.Simulator.StateStore
            .Get(power, static () => new TimeWarpPredictionState());
        state.CardCount++;
        int countdown = power.DynamicVars["Countdown"].IntValue;
        if (state.CardCount < countdown)
            return;
        state.CardCount = 0;
        combat.RequestPlayerTurnEnd();
        foreach (Creature enemy in combat.KnownEnemies)
        {
            if (context.Simulator.State.GetCreature(enemy).IsAlive)
                combat.Apply<StrengthPower>(enemy, 2, power.Owner);
        }
    }

    /// <summary>
    /// AFTP <c>TimeWarpPower</c> 的卡牌计数在预测里的分支副本（数到 12 会强制结束回合，所以要进指纹）。
    /// </summary>
    internal sealed class TimeWarpPredictionState : IPredictionStateForkable
    {
        public TimeWarpPredictionState()
        {
        }

        public TimeWarpPredictionState(PowerModel power)
            => CardCount = power.DynamicVars["CardCount"].IntValue;

        public int CardCount { get; set; }

        public object Fork(PredictionForkContext context) => MemberwiseClone();
    }

    /// <summary>WrithingMass.AttackBlock：攻击之外给自己 <c>AttackBlockBlock</c> 点格挡（<c>Move</c>）。</summary>
    private static bool WrithingAttackBlock(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        _ = player;
        _ = plannedChoices;
        killedOwner = false;
        simulator.GainBlock(
            move.Owner,
            combat.GetMonsterStaticInt(move.Owner, "AttackBlockBlock"),
            ValueProp.Move);
        return true;
    }

    /// <summary>WrithingMass.AttackDebuff：攻击之后给每个活着的目标 2 层虚弱与 2 层易伤（`NormalDebuffAmount`）。</summary>
    private static bool WrithingAttackDebuff(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        _ = plannedChoices;
        killedOwner = false;
        if (!simulator.State.GetCreature(player).IsAlive)
            return true;
        combat.Apply<WeakPower>(player, _writhingNormalDebuff, move.Owner);
        combat.Apply<VulnerablePower>(player, _writhingNormalDebuff, move.Owner);
        return true;
    }

    /// <summary>
    /// WrithingMass.MegaDebuff：把 `_usedMegaDebuff` 置位（分支与 ReactivePower 都读它）。
    /// </summary>
    /// <remarks>
    /// 源码这一招的另一半是 `CardPileCmd.AddCurseToDeck&lt;Parasite&gt;(玩家)`——**牌组级**（跨战斗）的改动，
    /// 不在战斗求解器的状态模型里，也不影响本场战斗的任何数值。这一条按「战斗内无效果」登记，
    /// 牌组后果记在 docs/AFTP_ACT4HEART_STATUS.md §4.4 的已知边界里，不假装建模。
    /// </remarks>
    private static bool WrithingMegaDebuff(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        _ = simulator;
        _ = player;
        _ = plannedChoices;
        killedOwner = false;
        combat.SetMonsterBool(move.Owner, "_usedMegaDebuff", true);
        return true;
    }

    /// <summary>
    /// AFTP <c>ReactivePower.AfterDamageReceived</c>：持有者挨到未被格挡的 <c>Move</c> 伤害（非
    /// <c>Unpowered</c>）且还活着时，从自己的行动表里抽一个候选**改掉下一个行动**。
    /// </summary>
    /// <remarks>
    /// 候选＝行动表里所有 <c>IsMove</c> 的状态，排除**当前**行动与 `MOVE_BRANCH`；用过 `MEGA_DEBUFF`
    /// 就把它也排除。抽样走的是 **`RunRng.MonsterAi`（共享的怪物 AI 流）**，不是这只怪的私有流——
    /// 这一点必须照抄，否则整场抽样错位。
    /// </remarks>
    private static void ReactivePowerDamageReceived(
        AbstractModel model,
        AfterDamageReceivedMirrorContext context)
    {
        PowerModel power = (PowerModel)model;
        if (context.Target != power.Owner
            || context.Props.HasFlag(ValueProp.Unpowered)
            || !context.Props.HasFlag(ValueProp.Move)
            || context.Result.UnblockedDamage <= 0
            || context.Simulator.State.GetCreature(power.Owner).CurrentHp <= 0)
        {
            return;
        }
        if (context.CombatState is not SimulatedCombatState combat
            || power.Owner.Monster is not { } monster)
        {
            throw new PredictionUnsupportedException("反应缺少可写的预测状态。");
        }
        if (monster.MoveStateMachine is not { } machine)
        {
            throw new PredictionUnsupportedException("反应需要怪物身上已捕获的行动状态机。");
        }
        string currentMoveId = combat.CurrentMonsterMove(power.Owner).Move.Id;
        List<MoveState> candidates = [];
        foreach (MonsterState state in machine.States.Values)
        {
            if (state is not MoveState move
                || string.Equals(move.Id, currentMoveId, StringComparison.Ordinal)
                || string.Equals(move.Id, "MOVE_BRANCH", StringComparison.Ordinal))
            {
                continue;
            }
            if (string.Equals(move.Id, "MEGA_DEBUFF", StringComparison.Ordinal)
                && combat.GetMonsterBool(power.Owner, "_usedMegaDebuff"))
            {
                continue;
            }
            candidates.Add(move);
        }
        if (candidates.Count == 0)
            return;
        MoveState next = candidates[context.Simulator.Rng.MonsterAi.NextInt(candidates.Count)];
        combat.ForceMonsterMove(power.Owner, next.Id);
    }

    /// <summary>Lagavulin.Sleep：睡眠计数 +1（台词与音效不在适配范围）。</summary>
    private static bool LagavulinSleep(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        _ = simulator;
        _ = player;
        _ = plannedChoices;
        killedOwner = false;
        combat.SetMonsterInt(
            move.Owner,
            "_sleepTurnCount",
            combat.GetMonsterInt(move.Owner, "_sleepTurnCount") + 1);
        return true;
    }

    /// <summary>Lagavulin.Attack：减益计数 +1（攻击本身由通用攻击循环按意图结算）。</summary>
    private static bool LagavulinAttack(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        _ = simulator;
        _ = player;
        _ = plannedChoices;
        killedOwner = false;
        combat.SetMonsterInt(
            move.Owner,
            "_debuffTurnCount",
            combat.GetMonsterInt(move.Owner, "_debuffTurnCount") + 1);
        return true;
    }

    /// <summary>
    /// Lagavulin.Debuff：把减益计数清零，再给每个活着的目标 <c>DebuffAmount</c> 点敏捷与力量（源码是
    /// **负数**，A9+ -2／否则 -1）。
    /// </summary>
    private static bool LagavulinDebuff(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        _ = plannedChoices;
        killedOwner = false;
        combat.SetMonsterInt(move.Owner, "_debuffTurnCount", 0);
        int amount = combat.GetMonsterStaticInt(move.Owner, "DebuffAmount");
        if (simulator.State.GetCreature(player).IsAlive)
        {
            combat.Apply<DexterityPower>(player, amount, move.Owner);
            combat.Apply<StrengthPower>(player, amount, move.Owner);
        }
        return true;
    }

    /// <summary>AFTP 金属化在持有者身上的层数（没有则 0）。</summary>
    private static int MetallicizeAmountOn(SimulatedCombatState combat, Creature owner)
    {
        foreach (PowerModel power in combat.EffectivePowers())
        {
            if (ReferenceEquals(power.Owner, owner)
                && power.Amount > 0
                && string.Equals(power.GetType().Name, "MetallicizePower", StringComparison.Ordinal))
            {
                return power.Amount;
            }
        }
        return 0;
    }

    /// <summary>把持有者身上 AFTP 的金属化摘掉（层数归零）。</summary>
    private static void RemoveMetallicize(SimulatedCombatState combat, Creature owner)
    {
        foreach (PowerModel power in combat.EffectivePowers())
        {
            if (ReferenceEquals(power.Owner, owner)
                && power.Amount > 0
                && string.Equals(power.GetType().Name, "MetallicizePower", StringComparison.Ordinal))
            {
                combat.SetPowerAmount(power, 0);
                return;
            }
        }
    }

    /// <summary>
    /// AFTP <c>AsleepLagavulinPower.AfterDamageReceived</c>：持有者挨到**非零**未被格挡伤害时——
    /// 摘掉金属化、把拉瓦格林唤醒（<c>WakeUpFromDamage</c>＝置醒 ＋ 眩晕到 ATTACK）、再把自己移除。
    /// </summary>
    private static void AsleepLagavulinDamageReceived(
        AbstractModel model,
        AfterDamageReceivedMirrorContext context)
    {
        PowerModel power = (PowerModel)model;
        if (context.Target != power.Owner || context.Result.UnblockedDamage == 0)
            return;
        if (context.CombatState is not SimulatedCombatState combat)
        {
            throw new PredictionUnsupportedException("睡眠缺少可写的预测状态。");
        }
        RemoveMetallicize(combat, power.Owner);
        combat.SetMonsterBool(power.Owner, "_isAwake", true);
        combat.ForceStunnedMove(power.Owner, "ATTACK");
        ICombatPredictionEffectSink effects = context.CombatState as ICombatPredictionEffectSink
            ?? throw new PredictionUnsupportedException("睡眠缺少可写的预测状态。");
        effects.SetPowerAmount(power, 0);
    }

    /// <summary>
    /// AFTP <c>AsleepLagavulinPower.BeforeSideTurnStart</c>：第 1 回合、玩家侧开始时，若持有者还有金属化，
    /// 就按它的层数补一次 <c>Unpowered</c> 格挡（源码判据是 `side == Player &amp;&amp; RoundNumber == 1`）。
    /// </summary>
    private static void AsleepLagavulinTurnStart(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        PowerModel power)
    {
        if (combat.CurrentSide != CombatSide.Player || combat.RoundNumber != 1)
            return;
        int amount = MetallicizeAmountOn(combat, power.Owner);
        if (amount > 0)
            simulator.GainBlock(power.Owner, amount, ValueProp.Unpowered);
    }

    /// <summary>
    /// AFTP <c>AsleepLagavulinPower.BeforeSideTurnEndVeryEarly</c>：自己那一方回合末的**最早**阶段，
    /// 睡眠只剩最后一层时先把金属化摘掉（否则同一回合末的 Early 阶段还会多给一次格挡）。
    /// </summary>
    private static void AsleepLagavulinTurnEndVeryEarly(
        AbstractModel model,
        BeforeSideTurnEndMirrorContext context)
    {
        PowerModel power = (PowerModel)model;
        if (context.Side != power.Owner.Side || power.Amount > 1)
            return;
        if (context.CombatState is not SimulatedCombatState combat)
        {
            throw new PredictionUnsupportedException("睡眠缺少可写的预测状态。");
        }
        RemoveMetallicize(combat, power.Owner);
    }

    /// <summary>
    /// AFTP <c>AsleepLagavulinPower.AfterSideTurnEnd</c>（常规、非 Late）：自己那一方回合末减 1 层；
    /// 减到 0 就把拉瓦格林自然唤醒（置醒，不眩晕）。
    /// </summary>
    private static void AsleepLagavulinTurnEnd(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        PowerModel power,
        CombatSide side,
        IReadOnlyCollection<Creature> participants)
    {
        _ = simulator;
        _ = participants;
        if (side != power.Owner.Side)
            return;
        ICombatPredictionEffectSink effects = combat as ICombatPredictionEffectSink
            ?? throw new PredictionUnsupportedException("睡眠缺少可写的预测状态。");
        int remaining = power.Amount - 1;
        effects.SetPowerAmount(power, remaining);
        if (remaining <= 0)
            combat.SetMonsterBool(power.Owner, "_isAwake", true);
    }

    /// <summary>
    /// Transient.Attack：伤害 = <c>StartingDeathDmg + _count * IncrementDmg</c>（单段；单人下那个
    /// 多人倍率恒为 1，源码只在玩家人数 &gt; 1 时才改它）。
    /// </summary>
    private static BranchMonsterAttack TransientAttack(
        SimulatedCombatState combat,
        MonsterModel monster)
        => new(
            combat.GetMonsterStaticInt(monster.Creature, "StartingDeathDmg")
                + combat.GetMonsterInt(monster.Creature, "_count") * _transientIncrementDamage,
            1);

    /// <summary>Transient.Attack 的行动效果：攻击结算之后把 <c>_count</c> +1（下一次打得更疼）。</summary>
    private static bool TransientAttackEffect(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        _ = simulator;
        _ = player;
        _ = plannedChoices;
        killedOwner = false;
        combat.SetMonsterInt(
            move.Owner,
            "_count",
            combat.GetMonsterInt(move.Owner, "_count") + 1);
        return true;
    }

    /// <summary>
    /// AFTP <c>FadingPower.BeforeSideTurnEndEarly</c>：自己那一方回合末；层数 &lt;= 1 时（只要还活着）
    /// 直接把它杀死，否则减 1 层。
    /// </summary>
    private static void FadingPowerTurnEnd(AbstractModel model, BeforeSideTurnEndMirrorContext context)
    {
        PowerModel power = (PowerModel)model;
        if (context.Side != power.Owner.Side)
            return;
        if (power.Amount > 1)
        {
            ICombatPredictionEffectSink effects = context.CombatState as ICombatPredictionEffectSink
                ?? throw new PredictionUnsupportedException("瞬逝缺少可写的预测状态。");
            effects.SetPowerAmount(power, power.Amount - 1);
            return;
        }
        if (context.State.GetCreature(power.Owner).IsDead)
            return;
        context.Simulator.Kill(power.Owner);
    }

    /// <summary>
    /// AFTP <c>ShiftingPower.AfterDamageReceived</c>：持有者挨到 <c>TotalDamage &gt; 0</c> 的伤害时，
    /// 按这个数值给自己叠一层 <c>ShiftingStrengthDownPower</c>（负数临时力量，回合末恢复）。
    /// </summary>
    private static void ShiftingPowerDamageReceived(
        AbstractModel model,
        AfterDamageReceivedMirrorContext context)
    {
        PowerModel power = (PowerModel)model;
        if (context.Target != power.Owner || context.Result.TotalDamage <= 0)
            return;
        if (context.CombatState is not SimulatedCombatState combat)
        {
            throw new PredictionUnsupportedException("漂流缺少可写的预测状态。");
        }
        combat.ApplyTemporaryStrengthLoss(
            _shiftingStrengthDownType,
            power.Owner,
            context.Result.TotalDamage,
            power.Owner,
            null);
    }

    /// <summary>Nemesis.TriBurn：攻击意图之外就是往弃牌堆底部塞 <c>BurnAmount</c> 张 Burn。</summary>
    private static bool NemesisTriBurn(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        _ = combat;
        _ = move;
        _ = plannedChoices;
        killedOwner = false;
        simulator.AddToCombat<Burn>(
            player,
            PileType.Discard,
            _nemesisBurnAmount,
            null,
            CardPilePosition.Bottom);
        return true;
    }

    /// <summary>
    /// AFTP <c>Nemesis.AfterSideTurnEnd</c>（常规、非 Late）：自己那一方回合末翻转
    /// <c>_shouldApplyIntangible</c>——翻到真就给 1 层原版 <c>IntangiblePower</c>，翻到假且身上还有就把它移除。
    /// </summary>
    private static void NemesisSideTurnEnd(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        AbstractModel model,
        CombatSide side,
        IReadOnlyList<Creature> participants)
    {
        _ = side;
        MonsterModel nemesis = (MonsterModel)model;
        bool applies = !combat.GetMonsterBool(nemesis.Creature, "_shouldApplyIntangible");
        combat.SetMonsterBool(nemesis.Creature, "_shouldApplyIntangible", applies);
        if (applies)
        {
            if (simulator.State.GetCreature(nemesis.Creature).IsAlive)
                combat.Apply<IntangiblePower>(nemesis.Creature, 1, nemesis.Creature);
            return;
        }
        if (participants.Contains(nemesis.Creature) && combat.GetAmount<IntangiblePower>(nemesis.Creature) > 0)
            combat.SetAmount<IntangiblePower>(nemesis.Creature, 0);
    }

    /// <summary>Byrd.Caw：给自己 <c>CawStrength</c> 点力量（台词与音效不在适配范围）。</summary>
    private static bool ByrdCaw(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        _ = simulator;
        _ = player;
        _ = plannedChoices;
        killedOwner = false;
        combat.Apply<StrengthPower>(move.Owner, _byrdCawStrength, move.Owner);
        return true;
    }

    /// <summary>Byrd.GoAirborne：给自己挂 <c>FlightAmount</c> 层 <c>FlightPower</c>（重新起飞）。</summary>
    private static bool ByrdGoAirborne(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        _ = player;
        _ = plannedChoices;
        killedOwner = false;
        combat.ApplyPower(
            _flightPowerType,
            move.Owner,
            combat.GetMonsterStaticInt(move.Owner, "FlightAmount"),
            move.Owner);
        return true;
    }

    /// <summary>
    /// AFTP <c>FlightPower.AfterDamageReceived</c>：持有者吃到未被格挡的 <c>Move</c> 伤害（非
    /// <c>Unpowered</c>）且还活着时减 1 层；**层数归零时**源码靠 <c>AfterRemoved</c> 调
    /// <c>Byrd.OnFlightBroken()</c>（换外观 + `CreatureCmd.Stun(自己, "HEADBUTT")`）。
    /// </summary>
    /// <remarks>
    /// 这只 Power 在实战里只会因为这条减层而归零，所以「移除时打落」在这里就地表达：
    /// 换成 <c>ForceStunnedMove(owner, "HEADBUTT")</c>（核心合成的 STUNNED 行动 FollowUp 指向 HEADBUTT，
    /// 与源码 <c>CreatureCmd.Stun</c> 同型）；换外观那半是纯表现。
    /// </remarks>
    private static void FlightPowerDamageReceived(AbstractModel model, AfterDamageReceivedMirrorContext context)
    {
        PowerModel power = (PowerModel)model;
        if (context.Target != power.Owner
            || context.Result.UnblockedDamage <= 0
            || !context.Props.HasFlag(ValueProp.Move)
            || context.Props.HasFlag(ValueProp.Unpowered)
            || context.Simulator.State.GetCreature(power.Owner).CurrentHp <= 0)
        {
            return;
        }
        ICombatPredictionEffectSink effects = context.CombatState as ICombatPredictionEffectSink
            ?? throw new PredictionUnsupportedException("飞行缺少可写的预测状态。");
        int remaining = power.Amount - 1;
        effects.SetPowerAmount(power, remaining);
        if (remaining > 0
            || !string.Equals(power.Owner.Monster?.GetType().Name, "Byrd", StringComparison.Ordinal))
        {
            return;
        }
        if (context.CombatState is not SimulatedCombatState simulatedCombat)
        {
            throw new PredictionUnsupportedException("飞行被打落时缺少可写的预测状态。");
        }
        simulatedCombat.ForceStunnedMove(power.Owner, "HEADBUTT");
    }

    /// <summary>
    /// AFTP <c>FlightPower.BeforeSideTurnStart</c>：自己那一方回合开始时把层数回滚到施加时的值。
    /// </summary>
    /// <remarks>
    /// 源码读的是 <c>DynamicVars["StoredAmount"]</c>（<c>AfterApplied</c> 里写进去的施加层数）；
    /// 这只 Power 的两处施加（开场的 <c>AfterAddedToRoom</c> 与 <c>GO_AIRBORNE</c>）传的都是
    /// <c>FlightAmount</c>，所以这里直接读那个静态数值成员，等价且不依赖第三方 DynamicVar 在预测里的物化。
    /// </remarks>
    private static void FlightPowerTurnStart(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        PowerModel power)
    {
        _ = simulator;
        if (combat.CurrentSide != power.Owner.Side)
            return;
        ICombatPredictionEffectSink effects = combat as ICombatPredictionEffectSink
            ?? throw new PredictionUnsupportedException("飞行缺少可写的预测状态。");
        effects.SetPowerAmount(power, combat.GetMonsterStaticInt(power.Owner, "FlightAmount"));
    }

    /// <summary>
    /// GremlinLeader.Rally：最多两次，每次取布点表里**最后一个**既不是 `leader` 又没被存活队友占用的槽位，
    /// 用它自己那条私有 RNG 抽 <c>NextInt(8)</c> 决定召唤哪只小鬼（0-1 疯／2-3 潜／4-5 肥／6 盾／7 巫），
    /// 每只挂 `MinionPower`（由 `minion: true` 做掉）。抽到哪一只、抽了几次都照抄源码。
    /// </summary>
    private static bool GremlinLeaderRally(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        _ = player;
        _ = plannedChoices;
        killedOwner = false;
        HashSet<string> occupied = [];
        foreach (Creature teammate in combat.GetTeammatesOf(move.Owner))
        {
            if (simulator.State.GetCreature(teammate).IsAlive && teammate.SlotName is { } occupiedSlot)
                occupied.Add(occupiedSlot);
        }
        MonsterRngPredictionState rng = MonsterRngSupport.State(
            simulator,
            move.Owner.Monster ?? throw new PredictionUnsupportedException("小鬼首领缺少怪物模型。"));
        for (int index = 0; index < 2; index++)
        {
            string? emptySlot = null;
            foreach (string slot in combat.EncounterSlots)
            {
                if (!string.Equals(slot, "leader", StringComparison.Ordinal) && !occupied.Contains(slot))
                    emptySlot = slot;
            }
            if (emptySlot is null)
                break;
            int roll = rng.NextInt(0, 8);
            Type childType = roll switch
            {
                0 or 1 => _gremlinMadType,
                2 or 3 => _gremlinSneakyType,
                4 or 5 => _gremlinFatType,
                6 => _gremlinShieldType,
                _ => _gremlinWizardType,
            };
            MonsterSpawnSupport.SpawnByType(
                simulator,
                combat,
                move.Owner,
                childType,
                emptySlot,
                maxHpOverride: null,
                minion: true);
            occupied.Add(emptySlot);
        }
        combat.SetMonsterInt(move.Owner, GremlinLeaderRngDrawsMember, rng.Draws);
        return true;
    }

    /// <summary>
    /// GremlinLeader.Encourage：先给自己 <c>StrengthAmount</c> 点力量，再给每个**存活的其他**队友
    /// <c>StrengthAmount</c> 点力量与 <c>BlockAmount</c> 点格挡（<c>Move</c>）。
    /// </summary>
    private static bool GremlinLeaderEncourage(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        _ = player;
        _ = plannedChoices;
        killedOwner = false;
        int strength = combat.GetMonsterStaticInt(move.Owner, "StrengthAmount");
        int block = combat.GetMonsterStaticInt(move.Owner, "BlockAmount");
        combat.Apply<StrengthPower>(move.Owner, strength, move.Owner);
        foreach (Creature teammate in combat.GetTeammatesOf(move.Owner))
        {
            if (teammate == move.Owner || !simulator.State.GetCreature(teammate).IsAlive)
                continue;
            combat.Apply<StrengthPower>(teammate, strength, move.Owner);
            simulator.GainBlock(teammate, block, ValueProp.Move);
        }
        return true;
    }

    /// <summary>
    /// 小鬼首领的 <c>BeforeDeath</c>：摘掉存活小鬼的 <c>MinionPower</c>，再让它们逃跑
    /// （源码那条是各小鬼订阅首领 <c>Died</c> 事件后 <c>CreatureCmd.Escape</c>；模拟器不触发 C# 事件，
    /// 因此在这里一次做完，语义等价）。
    /// </summary>
    private static void GremlinLeaderBeforeDeath(AbstractModel model, BeforeDeathMirrorContext context)
    {
        MonsterModel leader = (MonsterModel)model;
        if (!ReferenceEquals(context.Creature, leader.Creature))
            return;
        if (context.CombatState is not SimulatedCombatState combat)
        {
            throw new PredictionUnsupportedException("小鬼首领的死亡处理缺少可写的预测状态。");
        }
        foreach (Creature teammate in combat.GetTeammatesOf(leader.Creature))
        {
            if (teammate == leader.Creature || !context.State.GetCreature(teammate).IsAlive)
                continue;
            combat.SetAmount<MinionPower>(teammate, 0);
            combat.CreatureEscaped(teammate);
        }
    }

    /// <summary>
    /// Collector.Spawn：清掉 `_initialSpawn`，并给布点表里每个 `torch` 开头的槽位生成一只火炬头
    /// （源码不做占用检查，这里照抄），每只挂 `MinionPower`（由 `minion: true` 做掉）。
    /// </summary>
    private static bool CollectorSpawn(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        _ = player;
        _ = plannedChoices;
        killedOwner = false;
        combat.SetMonsterBool(move.Owner, "_initialSpawn", false);
        foreach (string slot in combat.EncounterSlots)
        {
            if (!slot.StartsWith("torch", StringComparison.Ordinal))
                continue;
            MonsterSpawnSupport.SpawnByType(
                simulator,
                combat,
                move.Owner,
                _torchHeadType,
                slot,
                maxHpOverride: null,
                minion: true);
        }
        return true;
    }

    /// <summary>
    /// Collector.Buff：自己 <c>BlockAmount</c> 格挡（<c>Move</c>），再给每个活着的队友（含自己）
    /// <c>StrengthAmount</c> 力量。
    /// </summary>
    private static bool CollectorBuff(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        _ = player;
        _ = plannedChoices;
        killedOwner = false;
        simulator.GainBlock(
            move.Owner,
            combat.GetMonsterStaticInt(move.Owner, "BlockAmount"),
            ValueProp.Move);
        int strength = combat.GetMonsterStaticInt(move.Owner, "StrengthAmount");
        foreach (Creature teammate in combat.GetTeammatesOf(move.Owner))
        {
            if (simulator.State.GetCreature(teammate).IsAlive)
                combat.Apply<StrengthPower>(teammate, strength, move.Owner);
        }
        return true;
    }

    /// <summary>
    /// Collector.MegaDebuff：给每个活着的目标 <c>MegaDebuffAmount</c> 层虚弱、易伤与破甲，然后把
    /// `_ultUsed` 置位（分支靠它保证这招只出一次）。
    /// </summary>
    private static bool CollectorMegaDebuff(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        _ = plannedChoices;
        killedOwner = false;
        int amount = combat.GetMonsterStaticInt(move.Owner, "MegaDebuffAmount");
        if (simulator.State.GetCreature(player).IsAlive)
        {
            combat.Apply<WeakPower>(player, amount, move.Owner);
            combat.Apply<VulnerablePower>(player, amount, move.Owner);
            combat.Apply<FrailPower>(player, amount, move.Owner);
        }
        combat.SetMonsterBool(move.Owner, "_ultUsed", true);
        return true;
    }

    /// <summary>
    /// Collector.Revive：给布点表里**空的**（没有被存活队友占用的）`torch` 槽位各生成一只火炬头——
    /// 与 SPAWN 的区别就在这里：这一条要查占用。
    /// </summary>
    private static bool CollectorRevive(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        _ = player;
        _ = plannedChoices;
        killedOwner = false;
        HashSet<string> occupied = [];
        foreach (Creature teammate in combat.GetTeammatesOf(move.Owner))
        {
            if (simulator.State.GetCreature(teammate).IsAlive && teammate.SlotName is { } occupiedSlot)
                occupied.Add(occupiedSlot);
        }
        foreach (string slot in combat.EncounterSlots)
        {
            if (!slot.StartsWith("torch", StringComparison.Ordinal) || occupied.Contains(slot))
                continue;
            MonsterSpawnSupport.SpawnByType(
                simulator,
                combat,
                move.Owner,
                _torchHeadType,
                slot,
                maxHpOverride: null,
                minion: true);
            occupied.Add(slot);
        }
        return true;
    }

    /// <summary>ShelledParasite.Fell：攻击后给每个活着的目标 <c>FellFrailAmount</c> 层破甲。</summary>
    private static bool ShelledParasiteFell(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        _ = plannedChoices;
        killedOwner = false;
        if (simulator.State.GetCreature(player).IsAlive)
            combat.Apply<FrailPower>(player, _shelledParasiteFellFrail, move.Owner);
        return true;
    }

    /// <summary>
    /// ShelledParasite.LifeSuck：按这次攻击全部命中里**未被格挡的伤害**之和给自己回血（源码就是把
    /// <c>Results.SelectMany(...).Sum(r =&gt; r.UnblockedDamage)</c> 拿去 <c>CreatureCmd.Heal(…, true)</c>）。
    /// </summary>
    private static void ShelledParasiteLifeSuck(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        IReadOnlyList<DamageResult> results)
    {
        _ = combat;
        int totalUnblocked = 0;
        foreach (DamageResult result in results)
            totalUnblocked += result.UnblockedDamage;
        if (totalUnblocked > 0)
            simulator.Heal(move.Owner, totalUnblocked);
    }

    /// <summary>
    /// BronzeAutomaton.SpawnOrbs：按遭遇布点表里**以 `orb` 开头**的槽位各生成一只 <c>BronzeOrb</c>
    /// （源码不做占用检查，这里照抄），每只都挂 1 层 <c>MinionPower</c>（由 <c>minion: true</c> 这条路做掉）。
    /// </summary>
    private static bool BronzeAutomatonSpawnOrbs(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        _ = player;
        _ = plannedChoices;
        killedOwner = false;
        foreach (string slot in combat.EncounterSlots)
        {
            if (!slot.StartsWith("orb", StringComparison.Ordinal))
                continue;
            MonsterSpawnSupport.SpawnByType(
                simulator,
                combat,
                move.Owner,
                _bronzeOrbType,
                slot,
                maxHpOverride: null,
                minion: true);
        }
        return true;
    }

    /// <summary>
    /// BronzeAutomaton.Boost：给自己 <c>BlockAmount</c> 点格挡（<c>Move</c>）与 <c>StrAmount</c> 点力量。
    /// </summary>
    private static bool BronzeAutomatonBoost(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        _ = player;
        _ = plannedChoices;
        killedOwner = false;
        simulator.GainBlock(
            move.Owner,
            combat.GetMonsterStaticInt(move.Owner, "BlockAmount"),
            ValueProp.Move);
        combat.Apply<StrengthPower>(
            move.Owner,
            combat.GetMonsterStaticInt(move.Owner, "StrAmount"),
            move.Owner);
        return true;
    }

    /// <summary>BronzeOrb.SupportBeam：给**存活的正牌自动机**（队友里 `Monster is BronzeAutomaton`）12 点格挡。</summary>
    private static bool BronzeOrbSupportBeam(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        _ = player;
        _ = plannedChoices;
        killedOwner = false;
        foreach (Creature teammate in combat.GetTeammatesOf(move.Owner))
        {
            if (teammate.Monster?.GetType().Name != "BronzeAutomaton"
                || !simulator.State.GetCreature(teammate).IsAlive)
            {
                continue;
            }
            simulator.GainBlock(teammate, 12, ValueProp.Move);
        }
        return true;
    }

    /// <summary>
    /// BronzeOrb.Stasis：把玩家抽牌堆（空则弃牌堆）按源码那套「先排序再 Fisher–Yates」稳定洗牌，
    /// 依次按 稀有 → 罕见 → 普通 → 任意 挑一张，移出战斗、记一笔「被偷的牌」，再把 <c>StasisPower</c>
    /// 挂到自己身上；被偷的牌存进预测状态，等球体死亡时由 <see cref="StasisPowerBeforeDeath"/> 归还。
    /// </summary>
    private static bool BronzeOrbStasis(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        _ = plannedChoices;
        killedOwner = false;
        if (player.Player is not { } targetPlayer || !simulator.State.GetCreature(player).IsAlive)
            return true;
        SimPlayerCombatState playerState = simulator.State.GetPlayerCombatState(targetPlayer);
        List<PredictedCard> draw = [.. playerState.DrawPile.Cards];
        List<PredictedCard> discard = [.. playerState.DiscardPile.Cards];
        if (draw.Count == 0 && discard.Count == 0)
            return true;
        List<PredictedCard> pool = draw.Count > 0 ? draw : discard;
        // 源码 `ListExtensions.StableShuffle`：先 Sort()（卡自己的 IComparable）再 Fisher–Yates。
        pool.Sort(static (left, right) => left.Preview.CompareTo(right.Preview));
        Rng rng = simulator.Rng.CombatCardGeneration;
        for (int index = pool.Count - 1; index > 0; index--)
        {
            int swapIndex = rng.NextInt(index + 1);
            (pool[index], pool[swapIndex]) = (pool[swapIndex], pool[index]);
        }
        PredictedCard? stolen =
            pool.FirstOrDefault(static card => card.Preview.Rarity == CardRarity.Rare)
            ?? pool.FirstOrDefault(static card => card.Preview.Rarity == CardRarity.Uncommon)
            ?? pool.FirstOrDefault(static card => card.Preview.Rarity == CardRarity.Common)
            ?? pool.FirstOrDefault();
        if (stolen is null)
            return true;
        List<PredictedCard> allCards = [.. playerState.AllCards];
        int identity = 0;
        for (int index = 0; index < allCards.Count; index++)
        {
            if (ReferenceEquals(allCards[index], stolen))
            {
                identity = index + 1;
                break;
            }
        }
        simulator.RemoveFromCombat(stolen);
        combat.RecordStolenCard(simulator);
        combat.ApplyPower(_stasisPowerType, move.Owner, 1, move.Owner);
        PowerModel? stasis = combat.EffectivePowers().FirstOrDefault(power =>
            power.GetType() == _stasisPowerType && ReferenceEquals(power.Owner, move.Owner));
        if (stasis is null)
        {
            throw new PredictionUnsupportedException(
                "STASIS 挂上的 StasisPower 没有出现在预测状态里。");
        }
        StasisStolenCardState state = simulator.StateStore
            .Get(stasis, static () => new StasisStolenCardState());
        state.StolenCard = stolen;
        state.CardIdentity = identity;
        return true;
    }

    /// <summary>
    /// AFTP <c>StasisPower.BeforeDeath</c>：持有者（球体）死亡时把被偷的牌放回**手牌**（源码
    /// <c>CardPileCmd.Add(StolenCard, PileType 2, CardPilePosition 1, null, false)</c>＝手牌底部）。
    /// </summary>
    /// <remarks>
    /// 归还前要先把 <c>HasBeenRemovedFromState</c> 复位（源码也是先写这个标记），否则预测器会拒绝入堆。
    /// </remarks>
    private static void StasisPowerBeforeDeath(AbstractModel model, BeforeDeathMirrorContext context)
    {
        PowerModel power = (PowerModel)model;
        if (!ReferenceEquals(context.Creature, power.Owner))
            return;
        StasisStolenCardState state = context.Simulator.StateStore
            .Get(power, static () => new StasisStolenCardState());
        if (state.StolenCard is not { } card)
            return;
        state.StolenCard = null;
        card.MutablePreview.HasBeenRemovedFromState = false;
        context.Simulator.AddToPile(card, PileType.Hand, CardPilePosition.Bottom);
    }

    /// <summary>
    /// 被 <c>StasisPower</c> 扣住的那张牌在预测里的分支副本。
    /// </summary>
    /// <remarks>
    /// 牌是**对象引用**：Fork 时必须克隆一份，否则一条分支把牌还回手里会污染另一条分支。
    /// <c>CardIdentity</c> 只是给指纹用的稳定标识（有 <c>CombatId</c> 就用它）。
    /// </remarks>
    internal sealed class StasisStolenCardState : IPredictionStateForkable
    {
        public PredictedCard? StolenCard { get; set; }

        public int CardIdentity { get; set; }

        public object Fork(PredictionForkContext context)
            => new StasisStolenCardState
            {
                StolenCard = StolenCard?.CreateClone(),
                CardIdentity = CardIdentity,
            };
    }

    /// <summary>SnakePlant.Spores：给每个活着的目标 <c>DebuffAmount</c> 层破甲与虚弱（施加者是蛇草）。</summary>
    private static bool SnakePlantSpores(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        _ = plannedChoices;
        killedOwner = false;
        if (!simulator.State.GetCreature(player).IsAlive)
            return true;
        combat.Apply<FrailPower>(player, _snakePlantDebuffAmount, move.Owner);
        combat.Apply<WeakPower>(player, _snakePlantDebuffAmount, move.Owner);
        return true;
    }

    /// <summary>
    /// AFTP <c>MalleablePower.AfterDamageReceived</c>：持有者吃到未被格挡的 <c>Move</c> 伤害（非
    /// <c>Unpowered</c>）且还活着时，把**当前层数**累加进 <c>_pendingBlock</c>，然后自己的层数 +1。
    /// </summary>
    private static void MalleableDamageReceived(AbstractModel model, AfterDamageReceivedMirrorContext context)
    {
        PowerModel power = (PowerModel)model;
        if (context.Target != power.Owner
            || context.Result.UnblockedDamage <= 0
            || !context.Props.HasFlag(ValueProp.Move)
            || context.Props.HasFlag(ValueProp.Unpowered)
            || context.Simulator.State.GetCreature(power.Owner).CurrentHp <= 0)
        {
            return;
        }
        ICombatPredictionEffectSink effects = context.CombatState as ICombatPredictionEffectSink
            ?? throw new PredictionUnsupportedException("可塑缺少可写的预测状态。");
        MalleablePendingBlockState state = context.Simulator.StateStore
            .Get(power, () => new MalleablePendingBlockState(power));
        state.PendingBlock += power.Amount;
        effects.SetPowerAmount(power, power.Amount + 1);
    }

    /// <summary>
    /// AFTP <c>MalleablePower.AfterAttack</c>：只要还有累计值就把它们换成 <c>Unpowered</c> 格挡并清零。
    /// </summary>
    /// <remarks>源码对「是谁打的」不加任何条件——任何一次攻击命令都会把它兑现，这里照抄。</remarks>
    private static void MalleableAfterAttack(AbstractModel model, AfterAttackMirrorContext context)
    {
        PowerModel power = (PowerModel)model;
        MalleablePendingBlockState state = context.Simulator.StateStore
            .Get(power, () => new MalleablePendingBlockState(power));
        if (state.PendingBlock <= 0)
            return;
        context.Simulator.GainBlock(power.Owner, state.PendingBlock, ValueProp.Unpowered);
        state.PendingBlock = 0;
    }

    /// <summary>
    /// AFTP <c>MalleablePower.AfterSideTurnEnd</c>（常规、非 Late）：自己那一方回合末先兑现剩余累计值，
    /// 再把层数回滚到施加时的 <c>BaseAmount</c>。
    /// </summary>
    private static void MalleableSideTurnEnd(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        PowerModel power,
        CombatSide side,
        IReadOnlyCollection<Creature> participants)
    {
        _ = participants;
        if (side != power.Owner.Side)
            return;
        MalleablePendingBlockState state = simulator.StateStore
            .Get(power, () => new MalleablePendingBlockState(power));
        if (state.PendingBlock > 0)
        {
            simulator.GainBlock(power.Owner, state.PendingBlock, ValueProp.Unpowered);
            state.PendingBlock = 0;
        }
        int baseAmount = power.DynamicVars["BaseAmount"].IntValue;
        if (power.Amount == baseAmount)
            return;
        ICombatPredictionEffectSink effects = combat as ICombatPredictionEffectSink
            ?? throw new PredictionUnsupportedException("可塑缺少可写的预测状态。");
        effects.SetPowerAmount(power, baseAmount);
    }

    /// <summary>
    /// AFTP <c>MalleablePower</c> 的私有 <c>_pendingBlock</c> 在预测里的分支副本。
    /// </summary>
    internal sealed class MalleablePendingBlockState : IPredictionStateForkable
    {
        public MalleablePendingBlockState(PowerModel power)
            => PendingBlock = ReadMalleablePendingBlock(power);

        public int PendingBlock { get; set; }

        public object Fork(PredictionForkContext context) => MemberwiseClone();
    }

    /// <summary>Deca.Beam：两段攻击由通用攻击循环结算，这里补攻击后塞进弃牌堆底部的 2 张 Dazed。</summary>
    private static bool DecaBeam(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        _ = combat;
        _ = move;
        _ = plannedChoices;
        killedOwner = false;
        simulator.AddToCombat<Dazed>(player, PileType.Discard, 2, null, CardPilePosition.Bottom);
        return true;
    }

    /// <summary>
    /// Deca.SquareOfProtection：给每个存活队友（含自己）<c>ProtectBlock</c> 点格挡（<c>Move</c>）与
    /// <c>ProtectPlatedArmorAmount</c> 层 AFTP <c>PlatedArmorPower</c>。
    /// </summary>
    private static bool DecaSquareOfProtection(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        _ = player;
        _ = plannedChoices;
        killedOwner = false;
        foreach (Creature teammate in combat.GetTeammatesOf(move.Owner))
        {
            if (!simulator.State.GetCreature(teammate).IsAlive)
                continue;
            simulator.GainBlock(teammate, _decaProtectBlock, ValueProp.Move);
            combat.ApplyPower(_platedArmorType, teammate, _decaProtectPlatedArmor, move.Owner);
        }
        return true;
    }

    /// <summary>
    /// AFTP <c>PlatedArmorPower.BeforeSideTurnStart</c>：只在**第 1 回合、玩家侧开始时**，
    /// 敌人身上的镀甲按层数补一次 <c>Unpowered</c> 格挡（判据与源码逐字一致：
    /// <c>Owner.Side == Enemy &amp;&amp; side == Player &amp;&amp; RoundNumber == 1</c>）。
    /// </summary>
    private static void PlatedArmorStart(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        PowerModel power)
    {
        if (combat.CurrentSide != CombatSide.Player || combat.RoundNumber != 1 || !power.Owner.IsEnemy)
            return;
        simulator.GainBlock(power.Owner, power.Amount, ValueProp.Unpowered);
    }

    /// <summary>AFTP <c>PlatedArmorPower.BeforeSideTurnEndEarly</c>：自己那一方回合末按层数获得格挡。</summary>
    private static void PlatedArmorTurnEnd(AbstractModel model, BeforeSideTurnEndMirrorContext context)
    {
        PowerModel power = (PowerModel)model;
        if (context.Side != power.Owner.Side)
            return;
        context.Simulator.GainBlock(power.Owner, power.Amount, ValueProp.Unpowered);
    }

    /// <summary>
    /// AFTP <c>PlatedArmorPower.AfterDamageReceived</c>：持有者吃到**未被格挡的 <c>Move</c> 伤害**（不是
    /// <c>Unpowered</c>）时减 1 层。
    /// </summary>
    /// <remarks>
    /// 源码在层数归零且持有者是**甲壳寄生虫**时还会调 <c>OnArmorBreak()</c>；那只怪还没适配，
    /// 所以这里**显式失败**而不是静默跳过——装一半比不装更糟。
    /// </remarks>
    private static void PlatedArmorDamageReceived(
        AbstractModel model,
        AfterDamageReceivedMirrorContext context)
    {
        PowerModel power = (PowerModel)model;
        if (context.Target != power.Owner
            || context.Result.UnblockedDamage <= 0
            || !context.Props.HasFlag(ValueProp.Move)
            || context.Props.HasFlag(ValueProp.Unpowered))
        {
            return;
        }
        ICombatPredictionEffectSink effects = context.CombatState as ICombatPredictionEffectSink
            ?? throw new PredictionUnsupportedException("镀甲缺少可写的预测状态。");
        effects.ApplyPower(_platedArmorType, power.Owner, -1, power.Applier);
        if (power.Amount - 1 > 0
            || !string.Equals(
                power.Owner.Monster?.GetType().Name,
                "ShelledParasite",
                StringComparison.Ordinal))
        {
            return;
        }
        // 层数归零的甲壳寄生虫会破甲：源码 OnArmorBreak 最后是 SetMoveImmediate(_stunnedState, true)，
        // 对应核心的 ForceStunnedMove（合成的 STUNNED 行动 FollowUp 指回 FELL，与源码那个状态同型）。
        if (context.CombatState is not SimulatedCombatState simulatedCombat)
        {
            throw new PredictionUnsupportedException(
                "镀甲层数归零时的甲壳寄生虫破甲缺少可写的预测状态。");
        }
        simulatedCombat.ForceStunnedMove(power.Owner, "FELL");
    }

    /// <summary>
    /// Donu.CircleOfProtection：给每个存活队友（含自己）挂 <c>CircleStrengthAmount</c> 点力量；
    /// <c>Beam</c> 的两段攻击由通用攻击循环按意图结算，没有非攻击部分。
    /// </summary>
    private static bool DonuCircleOfProtection(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        _ = player;
        _ = plannedChoices;
        killedOwner = false;
        foreach (Creature teammate in combat.GetTeammatesOf(move.Owner))
        {
            if (simulator.State.GetCreature(teammate).IsAlive)
                combat.Apply<StrengthPower>(teammate, _donuCircleStrengthAmount, move.Owner);
        }
        return true;
    }

    /// <summary>
    /// Exploder.Explode：伤害由通用攻击循环按 <c>DeathBlowIntent(30)</c> 结算，效果侧只补自杀
    /// （与 <c>SnakeDagger.EXPLODE</c> 同型）。
    /// </summary>
    /// <remarks>
    /// 源码这一下用的是 <c>CreatureCmd.Damage</c>（直伤）而不是 <c>DamageCmd.Attack</c>；求解器按意图把它
    /// 当攻击命中结算。两条路在这个核心里走的是同一套伤害管线（同样的 <c>ValueProp.Move</c>、同样的
    /// <c>BeforeDamageReceived</c> 反伤），差别只落在「会不会派发 <c>AfterAttack</c>」——而核心登记的那些
    /// <c>AfterAttack</c> 镜像要么要求攻击者自己持有 Power、要么只对卡牌来源生效，所以对自爆虫这场战斗
    /// 的结果没有可观察差别。已在 TEST_MATRIX 记为已知差异。
    /// </remarks>
    private static bool ExploderExplode(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        _ = combat;
        _ = player;
        _ = plannedChoices;
        killedOwner = false;
        if (simulator.State.GetCreature(move.Owner).IsDead)
            return true;
        simulator.Kill(move.Owner);
        killedOwner = true;
        return true;
    }

    /// <summary>AFTP 的蛇匕首类型（Reptomancer 召唤用）。</summary>
    private static Type _snakeDaggerType = null!;

    /// <summary>
    /// Reptomancer.SpawnDagger：按遭遇布点表里**除 `reptomancer` 之外**的空槽依次召唤，最多 2 只；
    /// 每只都挂 1 层原版 <c>MinionPower</c>（与源码一致，也由 <c>minion: true</c> 这条路做掉）。
    /// </summary>
    private static bool ReptomancerSpawnDagger(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        _ = player;
        _ = plannedChoices;
        killedOwner = false;
        HashSet<string> occupied = [];
        foreach (Creature teammate in combat.GetTeammatesOf(move.Owner))
        {
            if (simulator.State.GetCreature(teammate).IsAlive && teammate.SlotName is { } occupiedSlot)
                occupied.Add(occupiedSlot);
        }
        int spawned = 0;
        foreach (string slot in combat.EncounterSlots)
        {
            if (spawned >= 2)
                break;
            if (string.Equals(slot, "reptomancer", StringComparison.Ordinal) || occupied.Contains(slot))
                continue;
            MonsterSpawnSupport.SpawnByType(
                simulator,
                combat,
                move.Owner,
                _snakeDaggerType,
                slot,
                maxHpOverride: null,
                minion: true);
            occupied.Add(slot);
            spawned++;
        }
        return true;
    }

    /// <summary>Reptomancer.SnakeStrike：两段攻击由通用攻击循环结算，这里补攻击后给每个活着的目标 1 层虚弱。</summary>
    private static bool ReptomancerSnakeStrike(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        _ = plannedChoices;
        killedOwner = false;
        if (simulator.State.GetCreature(player).IsAlive)
            combat.Apply<WeakPower>(player, 1, move.Owner);
        return true;
    }

    /// <summary>
    /// GiantHead.ItIsTime：单段伤害 = <c>StartingDeathDmg - Count * IncrementDmg</c>
    /// （<c>StartingDeathDmg</c> 按 A9 冻结，<c>IncrementDmg</c> 自检里钉死）。
    /// </summary>
    private static BranchMonsterAttack GiantHeadItIsTime(
        SimulatedCombatState combat,
        MonsterModel monster)
        => new(
            combat.GetMonsterStaticInt(monster.Creature, "StartingDeathDmg")
                - combat.GetMonsterInt(monster.Creature, "_count") * _giantHeadIncrementDmg,
            1);

    /// <summary>GiantHead.Glare：给每个活着的目标 <c>GlareDuration</c> 层虚弱。</summary>
    private static bool GiantHeadGlare(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        _ = plannedChoices;
        killedOwner = false;
        if (simulator.State.GetCreature(player).IsAlive)
            combat.Apply<WeakPower>(player, _giantHeadGlareDuration, move.Owner);
        return true;
    }

    /// <summary>
    /// 「这条行动没有非攻击部分」：源码回调里除了攻击什么都不做时用它登记，
    /// 压掉预测器里由意图表带出来的假「未支持意图」。
    /// </summary>
    private static bool NoNonAttackEffect(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        _ = simulator;
        _ = combat;
        _ = move;
        _ = player;
        _ = plannedChoices;
        killedOwner = false;
        return true;
    }

    /// <summary>
    /// Maw.NomNomNom 的多段版本：伤害固定 5，段数 = <c>TurnCount / 2</c>（向下取整，源码 <c>NomHitCount</c>）。
    /// </summary>
    private static BranchMonsterAttack MawNomNomNom(
        SimulatedCombatState combat,
        MonsterModel monster)
        => new(
            5,
            combat.GetMonsterInt(monster.Creature, "_turnCount") / 2);

    /// <summary>
    /// Maw.Roar：给每个活着的目标挂 <c>TerrifyDuration</c> 层虚弱与破甲，并把 <c>_roared</c> 置真
    /// （分支靠它决定「开场第一动必是 ROAR」）。
    /// </summary>
    private static bool MawRoar(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        _ = plannedChoices;
        killedOwner = false;
        int duration = combat.GetMonsterStaticInt(move.Owner, "TerrifyDuration");
        if (simulator.State.GetCreature(player).IsAlive)
        {
            combat.Apply<WeakPower>(player, duration, move.Owner);
            combat.Apply<FrailPower>(player, duration, move.Owner);
        }
        combat.SetMonsterBool(move.Owner, "_roared", true);
        return true;
    }

    /// <summary>Maw.Drool：给自己 <c>StrUp</c> 点力量。</summary>
    private static bool MawDrool(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        _ = simulator;
        _ = player;
        _ = plannedChoices;
        killedOwner = false;
        combat.Apply<StrengthPower>(
            move.Owner,
            combat.GetMonsterStaticInt(move.Owner, "StrUp"),
            move.Owner);
        return true;
    }

    /// <summary>
    /// SpireGrowth.Constrict：给每个活着的玩家挂 <c>ConstrictAmount</c> 层 AFTP <c>ConstrictedPower</c>。
    /// </summary>
    private static bool SpireGrowthConstrict(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        _ = plannedChoices;
        killedOwner = false;
        if (!simulator.State.GetCreature(player).IsAlive)
            return true;
        combat.ApplyPower(
            _constrictedPowerType,
            player,
            combat.GetMonsterStaticInt(move.Owner, "ConstrictAmount"),
            move.Owner);
        return true;
    }

    /// <summary>
    /// AFTP <c>ConstrictedPower.AfterSideTurnEnd</c>（常规、非 Late）：自己那一方回合末，
    /// 持有者按层数吃一次 <c>Unpowered</c> 伤害。判据与源码逐字一致（<c>side == Owner.Side</c>）。
    /// </summary>
    private static void ConstrictedPowerTurnEnd(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        PowerModel power,
        CombatSide side,
        IReadOnlyCollection<Creature> participants)
    {
        _ = combat;
        _ = participants;
        if (side != power.Owner.Side)
            return;
        using (simulator.PushDamageSource(
            CombatDamageSource.For(CombatDamageSourceKind.Power, "ConstrictedPower")))
        {
            simulator.Damage(power.Owner, power.Amount, ValueProp.Unpowered, null);
        }
    }

    /// <summary>
    /// AFTP <c>ConstrictedPower.AfterDeath</c>：**施加者**死亡且不是「死亡被阻止」时把自己移除。
    /// </summary>
    private static void ConstrictedPowerAfterDeath(AbstractModel model, AfterDeathMirrorContext context)
    {
        PowerModel power = (PowerModel)model;
        if (context.WasRemovalPrevented || !ReferenceEquals(context.Creature, power.Applier))
            return;
        ICombatPredictionEffectSink effects = context.CombatState as ICombatPredictionEffectSink
            ?? throw new PredictionUnsupportedException("缠绕缺少可写的预测状态。");
        effects.ApplyPower(_constrictedPowerType, power.Owner, -power.Amount, power.Applier);
    }

    /// <summary>
    /// OrbWalker.Laser：攻击之后给每个目标塞两张 Burn——**弃牌堆底部一张、抽牌堆随机位置一张**
    /// （源码里是两次不同的 <c>AddGeneratedCardToCombat</c>）。
    /// </summary>
    private static bool OrbWalkerLaser(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        _ = combat;
        _ = move;
        _ = plannedChoices;
        killedOwner = false;
        simulator.AddToCombat<Burn>(player, PileType.Discard, 1, null, CardPilePosition.Bottom);
        simulator.AddToCombat<Burn>(player, PileType.Draw, 1, null, CardPilePosition.Random);
        return true;
    }

    /// <summary>
    /// AFTP <c>StrengthUpPower.AfterSideTurnEnd</c>（常规、非 Late）：自己那一方回合末按层数给自己加力量。
    /// </summary>
    /// <remarks>
    /// 源码的判据是 <c>side == Owner.Side</c>，这里逐字照抄；层数直接读 <c>power.Amount</c>
    /// （施加时的 <c>StrengthUpAmount</c> 由 <c>AfterAddedToRoom</c> 在根里就写好了）。
    /// </remarks>
    private static void StrengthUpPowerTurnEnd(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        PowerModel power,
        CombatSide side,
        IReadOnlyCollection<Creature> participants)
    {
        _ = simulator;
        _ = participants;
        if (side != power.Owner.Side)
            return;
        combat.Apply<StrengthPower>(power.Owner, power.Amount, power.Owner);
    }

    /// <summary>Repulsor.Daze：给每个活着的目标往抽牌堆随机位置塞 <c>DazeAmount</c> 张 Dazed。</summary>
    private static bool RepulsorDaze(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        _ = combat;
        _ = move;
        _ = plannedChoices;
        killedOwner = false;
        simulator.AddToCombat<Dazed>(
            player,
            PileType.Draw,
            _repulsorDazeAmount,
            null,
            CardPilePosition.Random);
        return true;
    }

    /// <summary>
    /// Spiker.BuffThorns：先自己记一次数（分支靠它判「超过 5 次就不再加」），再挂 <c>BuffAmount</c>
    /// 层荆棘。
    /// </summary>
    private static bool SpikerBuffThorns(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        _ = simulator;
        _ = player;
        _ = plannedChoices;
        killedOwner = false;
        combat.SetMonsterInt(
            move.Owner,
            "_thornsCount",
            combat.GetMonsterInt(move.Owner, "_thornsCount") + 1);
        combat.Apply<ThornsPower>(move.Owner, _spikerBuffAmount, move.Owner);
        return true;
    }

    /// <summary>SnakeDagger.WoundStab：攻击之后往弃牌堆塞 1 张 Wound。</summary>
    private static bool SnakeDaggerWoundStab(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        _ = combat;
        _ = move;
        _ = plannedChoices;
        killedOwner = false;
        simulator.AddToCombat<Wound>(player, PileType.Discard, 1, null);
        return true;
    }

    /// <summary>
    /// SnakeDagger.Explode：25 点攻击之后 <c>CreatureCmd.Kill(自己, false)</c>——攻击部分由通用攻击循环按
    /// 意图（<c>DeathBlowIntent</c> 派生自 <c>SingleAttackIntent</c>）结算，这里只补那一句自杀。
    /// </summary>
    private static bool SnakeDaggerExplode(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        ForecastMove move,
        Creature player,
        IReadOnlyList<PlanCardChoice>? plannedChoices,
        out bool killedOwner)
    {
        _ = combat;
        _ = player;
        _ = plannedChoices;
        killedOwner = false;
        if (simulator.State.GetCreature(move.Owner).IsDead)
            return true;
        simulator.Kill(move.Owner);
        killedOwner = true;
        return true;
    }
}
