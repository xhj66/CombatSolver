# AFTP / Act4Heart 适配状态

本文记录「为 CombatSolver 增加往昔之章（Acts from the Past, AFTP）怪物与对心脏（Act4Heart）
战斗支持」这一任务的**已落地部分、验证边界与后续路线**。约定与术语见
`docs/THIRD_PARTY_ADAPTERS.md`，改动的语义纪律见 `.agents/skills/combat-semantic-change/SKILL.md`。

范围约定（已与需求方确认）：**地基 + 第一幕 + 心脏**。

---

## 1. 本次落地的地基（已编译验证）

### 1.1 第三方 `MonsterState` 子类型分派

原版只有三种行动状态，求解器分别按「落到行动节点」「按权重抽分支」「按冻结的根选择」处理
（`BranchMonsterAi.Advance` / `RollInitial`）。第三方 Mod 可以注册第四种：AFTP 的
`ActsFromThePast.ConditionalBranchState` 继承自 `MonsterState`（**不是**原版
`MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine.ConditionalBranchState`），
它把 `Func<Creature, Rng, MonsterMoveStateMachine, string>` 当分支函数。

这类状态原先会落到 `Advance`/`RollInitial` 的 `default` 分支并抛
`PredictionUnsupportedException` —— 这是 AFTP 全量怪物「一进战斗就硬失败」的根因。

新增 `src/Prediction/ThirdPartyMonsterBranches.cs`：

- `IsForeignBranchState(state)` —— 以**命名空间**判定：命名空间等于原版状态机命名空间的
  一律交回既有逻辑（含将来原版新增的状态），其余用 `AssemblyInfo.ModForType` 确认非本体。
- `Resolve(branch, source, combat, simulator)` —— 按 `(怪物类型全名, 分支 Id)` 分派到
  **用模拟状态重实现**的选择函数；未登记的键抛 `PredictionUnsupportedException`。

`Advance`/`RollInitial` 各加一条带守卫的 `case var foreign when IsForeignBranchState(foreign):`，
排在三种原版状态之后、`default` 之前。

**为什么不直接调用委托。** 委托跑在实机模型上：它读 `owner.Monster` 的私有字段
（`_isOpen`、`_orbActiveCount`、`_mugCount`……）和*实机*状态机的 `StateLog`。搜索里每个分支
都是实机模型的一份共享引用，实机字段不随分支 Fork，实机 `StateLog` 也不等于当前预测分支的
行动历史；直接调用会让所有分支读到同一份陈旧值，还会白白吃掉一次 RNG。

**为什么不整体退回近似。** 沿用求解器对原版怪物的既有做法：把标量状态镜像进
`SimulatedCombatState`，再用模拟状态重算选择；做不到就抛异常，不退化。

### 1.2 RNG 与行动历史对齐

- `rng` 就是 `simulator.Rng.MonsterAi`，与 `Advance`/`RollInitial` 用的是同一条流。
  重实现**逐条复制**原实现的 `NextInt`/`NextFloat` 调用顺序与短路条件：不但在同一条件下抽
  同样的次数，连「某条分支不抽 RNG」也必须保持，否则后续回合的抽样整体错位。
- `LastMove` / `LastMoveBefore` / `LastTwoMoves` 对应 AFTP 各敌人自带的同名辅助函数，
  语义是**实机 `StateLog` 的末项/倒数第二项**。求解器传入的 `source.StateLog` 已经包含
  当回合刚执行的行动（与实机在 `SelectNextMove` 被调用时的 `StateLog` 一致），
  且分支状态本身不进 `StateLog`（`ShouldAppearInLogs=false`）。

### 1.3 根状态播种

`SimulatedCombatState.MaterializeRoot` 会在 `_rootMaterialized = true` **之前**对每个怪物执行
`DescribePredictedMonsterState`；该类型 switch 既是「预测指纹/续行」的描述，也是
`GetMonsterInt`/`GetMonsterBool` 的**播种点**（根物化之后再对一个未捕获字段做惰性读取会硬失败）。
本次为 `GremlinWizard._currentCharge` 补了这条。

---

## 2. 第一幕覆盖现状

### 2.1 已支持分支选择的怪物（12）

| 怪物 | 分支 Id | 依赖 |
| --- | --- | --- |
| AcidSlimeMedium | MOVE_BRANCH | StateLog + MonsterAi RNG |
| SpikeSlimeMedium | MOVE_BRANCH | StateLog + MonsterAi RNG |
| FungiBeast | MOVE_BRANCH | StateLog + MonsterAi RNG |
| JawWorm | MOVE_BRANCH | StateLog + MonsterAi RNG |
| GremlinNob | MOVE_BRANCH | StateLog |
| SlaverBlue | MOVE_BRANCH | StateLog + MonsterAi RNG |
| GremlinWizard | AFTER_CHARGE | `_currentCharge`（已播种；CHARGING 自增 / ULTIMATE_BLAST 归零） |
| Looter | MUG_BRANCH | `_mugCount`（已播种；MUG／LUNGE 各 +1），见 §2.8 |
| AcidSlimeLarge | MOVE_BRANCH | `_splitTriggered`（已播种；`SplitPower` 受伤镜像置位），见 §2.11 |
| SpikeSlimeLarge | MOVE_BRANCH | 同上 |
| SlimeBoss | MOVE_BRANCH | 同上（另外整场 `GOOP_SPRAY`，见 §2.11） |
| GremlinShield | MOVE_BRANCH | 队友数（源码数的是含死者在内的己方全体），见 §2.12 |

以上分支逻辑逐字对照 AFTP 源码（`SelectNextMove` / `SelectAfterCharge` / `SelectAfterMug`）复核，
行动 Id 常量与分支节点 Id 均已核对字面值。

### 2.2 走安全失败路径的怪物（4）

下列怪物的分支**依赖尚未建模的状态**，因此刻意不登记，保持抛
`PredictionUnsupportedException`（装一半比不装更糟）：

| 怪物 | 分支 Id | 还缺什么 |
| --- | --- | --- |
| SlaverRed | MOVE_BRANCH | `_usedEntangle`；更要紧的是 `EntangledPower`（给玩家手牌打 `EntangledOriginal` 病症，禁止打出攻击牌），求解器没有对应的可打出性镜像 |
| Hexaghost | MOVE_BRANCH | `_orbActiveCount`（可建模）＋ `DIVIDER` 的伤害由 `ACTIVATE` 按玩家血量现算（`DynamicMultiAttackIntent`），以及 `INFERNO` 的「升级全部 Burn + 再塞 3 张升级 Burn」，后者涉及预测期卡牌升级 |
| Guardian | OFFENSIVE_BRANCH | `_isOpen` / `CloseUpTriggered` / `_pendingModeShift` + `ModeShiftPower`（受伤累计到阈值触发）、`SharpHidePower`，以及模式切换时的 `SetMoveImmediate(_closeUpState)` |
| Lagavulin | MAIN_BRANCH | `IsAwake` / `StartsAwake` / `DebuffTurnCount` + `AsleepLagavulinPower`；`WakeUpFromDamage` 走 `CreatureCmd.Stun(…, "ATTACK")` |

### 2.3 无分支的怪物（10）

`Cultist`、`Sentry`、`LouseRed`、`LouseGreen`、`GremlinFat`、`GremlinMad`、`GremlinSneaky`、
`AcidSlimeSmall`、`SpikeSlimeSmall` 等没有 `ConditionalBranchState`，不会硬失败。

它们的非攻击行动效果已由适配 Mod 登记（`ExordiumMoveEffects.RegisterAll`，共 19 个
(怪物, 行动) 组合，覆盖 13 个怪物类型），例如 `Cultist.INCANTATION`、`Sentry.BOLT`、
`LouseRed.GROW`、`LouseGreen.SPIT_WEB`、`GremlinFat.SMASH`、`AcidSlimeSmall.LICK`。
仍未登记的行动保持「预览可能不完整」的软缺口，不静默当成空操作。

### 2.4 短名键控的碰撞核对

`MonsterMoveEffects` 用 `monster.GetType().Name` 作键。已核对本体 `sts2.dll`（6643 个类型）
**没有**任何与 AFTP 第一幕怪物同名的类型（本体是 `CalcifiedCultist`/`DampCultist`/
`FatGremlin`/`SneakyGremlin`/`LeafSlimeM`/`TwigSlimeM`/`LagavulinMatriarch` 这类），
故短名键控在本幕是安全的。

### 2.5 本体卡牌补丁：`Slimed.OnPlay` 的 classic 改写

往昔之章除怪物外还改写了一张**本体卡牌**：`ClassicSlimedOnPlayPatch`
（`ActsFromThePast.Patches.Cards`，`[HarmonyPatch(typeof(Slimed), "OnPlay")]`，一个 Prefix，
owner `actsfromthepast.actsfromthepast`，无 `[HarmonyPriority]`、无 before/after）。求解器把
「被第三方改写过 OnPlay 的卡牌」一律当不可预测，所以不登记这个合成语义就会**拒绝整场战斗**
（`PredictionModPatchAudit`）。适配在 `ExordiumCardPatches`：

- `RegisterAll` 按已复核的组合身份钉死登记 `AdaptedCardOnPlayMirrors.Register<Slimed>`，
  schema `aftp-classic-slimed-v1`；
- `PlaySlimed` 复刻本体 OnPlay（`Slimed` 1 费、消耗、状态牌，抽牌数恒为 1）。视觉不在预测范围内。

补丁的语义是**逐卡**的：

```csharp
if (!ClassicSlimedTracker.IsClassicSlimed.Get(card)) return true;  // 本体：原版抽 1 张
...VFX...; __result = Task.CompletedTask; return false;            // 经典：跳过原版，什么都不做
```

判定要点（已用反编译核实，见 `ExordiumClassicSlimed.cs`）：

| 事实 | 后果 |
| --- | --- |
| `ClassicSlimedTracker.IsClassicSlimed` 是 `SpireField<CardModel,bool>`（BaseLib），即 `ConditionalWeakTable`，默认 `false` | 标记**按卡牌实例**索引 |
| `TagClassicSlimedPatch` 在 `CardModel.ToMutable` 后缀里打标；`CreatingClassicSlimed` 只在怪物「塞史莱姆」期间等于 `ActsFromThePastConfig.LegacyEnemiesGiveClassicSlimed` | 标记只落在**实机**实例上；配置默认 `false`；作用点只有往昔之章**自己怪物**的行动，**不跨 Mod** |
| `SpireField` 只在调用过 `CopyOnClone()` 后才随 `AbstractModel.MutableClone` 复制，往昔之章**没有**调用 | 求解器 `MemberwiseClone` 出来的 Preview 拿不到标记 |

因此判据分两路，键都用 `PredictedCard.Original`（Fork 之间保持不变）：实机已有的牌直接读
往昔之章的标记；求解器在预测里生成的牌，由 `AddSlimedToCombat` 在生成时按同一份配置登记
（走 `CreateAndAddGeneratedCardsToCombat` + 复刻 `AddToCombat` 的守卫；入堆位置实测同为
`CardPilePosition.Bottom`，AFTP 写的是 `(CardPilePosition)1`）。

**边界**：史莱姆的类别**没有**进入状态指纹——卡片级状态不在适配接口的范围内，指纹只为挂在
卡牌上的 BaseLib `CardModifier` 留了通道（`PredictionModModelSupport.AppendBaseLibCardModifierState`），
而往昔之章把类别存在 `SpireField` 上（见 [third-party-model-state.md](third-party-model-state.md) 的「范围」）。
两张同名史莱姆若类别不同而指纹相同，去重会把它们当成同一张。

**因此判据逐张看来源，不拿配置去反推。** 这个开关的作用点只有往昔之章怪物行动里那两句
`CardPileCmd.AddToCombatAndPreview<Slimed>`，**只作用于它自己的怪**：本体、其它 Mod 产出的
史莱姆一律是本体语义，**不跨 Mod 生效**。适配的前提是同一场战斗里史莱姆只有一个来源
（一场遭遇打的是同一种怪），所以只在两种类别**确实并存**时才抛
`PredictionUnsupportedException` 拒绝整场（`RejectCoexistingClasses` 扫当前**所有玩家**的
五个牌堆；异常中止整次求解，不会交出错的路线）。跨状态的那种混用（两个搜索分支各自只持有
一种类别）在本地观察不到，本适配挡不住。

反例（曾经写错过）：拿 `LegacyEnemiesGiveClassicSlimed` 去判定一张**已有**牌，会在心脏战斗里
误伤——心脏的血弹包也会塞一张 `Slimed`（`HeartMoveEffects` 里的 5 张状态牌），而心脏不是
往昔之章的怪（往昔之章没做第四层），那张牌是**本体**语义，配置却是 `True`，于是整场被拒。
**心脏那张牌不得登记成经典。**

配置项 `LegacyEnemiesGiveClassicSlimed` 现在只用于**生成侧**：`AddSlimedToCombat` 登记新生成的
史莱姆时按它决定类别（`ClassicSlimed.ConfigGivesClassicSlimed` 已收成 private）。
**刻意不在 `Verify()` 里读**：配置要等往昔之章自己的初始化跑完才装载，而两个 Mod 的初始化
先后不作保证。首次使用时读一次并冻结，保证同一场战斗里「生成」与「判定」用的是同一个值。

---

### 2.6 回归：读标记**不能**用 `SpireField.Get`（并行下会炸掉整次搜索）

**症状**：心脏战斗里自动开算（`reason=AutoTurnStart`）跑了一百多秒后报
`SEARCH_FAILURE`，界面拿不到任何路线，也就**给不出战损**；手点「计算」重来一次又能出结果。

**根因**：BaseLib 的 `SpireField.Get` 是「先查后插」的**复合**操作，不是 `AddOrUpdate`：

```csharp
public TVal? Get(TKey obj)
{
    if (_table.TryGetValue(obj, out object value)) return (TVal)value;
    _table.Add(obj, value = _defaultVal(obj));   // ← 命中不了就写表
    ...
}
```

求解器并行展开（`searchMaxDegreeOfParallelism` 默认 4，本局实跑 `parallel_max_concurrency=2`）。
两路 worker 同时首次触到**同一张**未打标的史莱姆时，各自都先 `TryGetValue` 未命中、再各自
`Add`，后到的那个抛
`ArgumentException: An item with the same key has already been added`，从
`PlaySlimed` 一路冒到 `CombatBeamSolver.Replay`，被包成 `SearchTransitionException` 并升级为
`SEARCH_FAILURE`。

除竞态外这条路径还有两个毛病：它是**往对方 Mod 的 live 状态里写**，违反
[third-party-onplay-patches.md](third-party-onplay-patches.md)「写入只通过分支状态或 MutablePreview；
不得执行原生补丁、捕获 live 状态」。

**证据**（`CombatSolver-BugReports` 的 01:06 与 01:08 两份，同一场战斗 `seed=W9SCC7427GYP`）：

| 时间 | 事件 |
| --- | --- |
| 01:03:52 | `SEARCH_REQUEST generation=1 reason=AutoTurnStart` |
| 01:06:40 | `Evidence FAILED_CANDIDATE stage=action_replay … kind=PlayCard card=SLIMED occurrence=0`（前缀是 黑暗之拥+/EndTurn/燃烧契约+） |
| 01:06:40 | `SEARCH_FAILURE generation=1`，栈里有 `ConditionalWeakTable.Add` ← `PlaySlimed` |
| 01:06:54 | `SEARCH_REQUEST generation=2 reason=Manual`（玩家手点计算） |
| 01:08:09 | `SEARCH_ROUTE_ADOPTED generation=2` + `RESULT`（这次活下来了，战损 26） |

两份包的失败候选带**完全相同**的 `parent_state=StateFingerprint{5218856411484337805, 15348019209647483066}`，
但同状态的第二次搜索没炸——这正是竞态的特征：波次切分变了，两路就没撞上。

**修法**：不调 `Get`，直接绑 `_table` 的 `TryGetValue`（`ConditionalWeakTable<CardModel, object>`），
**纯读、不插入、无竞态**；未命中时用 `SpireField` 自己的默认值取值——与 `Get` 的返回值逐字等价，
只是不把默认值缓存进对方的表。`Verify()` 里额外钉死两件事：`_table`/`_defaultVal` 的形状没变，
以及默认值仍是 `false`（「未打标的史莱姆就是本体语义」是整份适配的前提，变了必须关死）。
`RecordGenerated` 用 `AddOrUpdate`（幂等），而不是 `Add`。

**为什么 worker 读它仍然成立**：这个标记在卡牌创建时一次性写好、此后不变，且以卡牌实机身份
为键；`PredictedCard.Fork` 无论是否隔离已挂模型都保留原 `Original`，所以各分支读到的是同一个值。
准则禁止的是捕获**随分支变化**的 live 状态，以及从 worker **写入** live 状态——只读且确定的不在此列。
真正缺的是逐卡状态通道：适配接口只有怪物级标量（`RegisterMonsterStateMembers`，会进根播种与
状态指纹），卡牌级没有对应入口。

---

### 2.7 第一幕的「常量构造」攻击：压掉动态伤害误报

往昔之章第一幕的怪物几乎全部用 `new SingleAttackIntent(<实例属性>)` 或
`new MultiAttackIntent(<实例属性>, <整数字面量>)` 构造攻击意图。这两个构造函数把**构造实参**捕进闭包
（`DamageCalc = () => damage`），值在 `GenerateMoveStateMachine` 里求值一次，此后不受任何分支状态影响；
但 `DamageCalc.Target != null` 这条保守判据照样会把它们报成 `approximation=…:动态伤害`，
把整场战斗的可信度压到「中等」。适配新增 `ExordiumStableAttacks`（第一幕 **35 条**）与
`LaterActsStableAttacks`（第二、三幕 **65 条**），合计 100 条 (怪物, 行动)。

**刻意不登记的两条**（继续按动态记近似）：

| 行动 | 形状 | 为什么不算常量 |
| --- | --- | --- |
| `Hexaghost.DIVIDER` | `new DynamicMultiAttackIntent(() => _dividerDamage, 6)` | 伤害由 `ACTIVATE` 按出手时玩家血量现算 |
| `LouseGreen.BITE` / `LouseRed.BITE` | `new DynamicSingleAttackIntent(() => GetBiteDamage())` | 读怪物入场时在**该怪物自己的** `RunRng.MonsterAi` 上抽到的那份伤害 |

`Dynamic*AttackIntent` 是往昔之章自己的类型（直接继承 `AttackIntent`，不是 `SingleAttackIntent` 子类），
所以它们与「常量构造」在形状上可区分。

**数值不抄第二份。** 这些伤害在往昔之章里是 `private int X => AscensionHelper.GetValueIfAscension(...)`
形式的实例属性而不是 `const`，适配层在初始化时既拿不到怪物实例、也拿不到属性值，抄一份进来只会变成
第二份真相。因此这条声明只承担「形状是常量构造」一个含义，由**求解器在运行期现场核对意图实例**
（`ThirdPartyAdapterRegistry.IsStableAttackShape` → `src/Prediction/StableAttackShape.cs`）：
形状对不上就不认这条声明，近似清单照旧记一条。判据、四种成立／不成立的情形与核对程序见
[第三方适配手册](THIRD_PARTY_ADAPTERS.md) §2.13。

**第二、三幕的同一批（`LaterActsStableAttacks`，65 条）**：范围扩到全部怪物之后一次补上，
覆盖第二幕 20 个、第三幕 17 个类型里所有「伤害与段数都在意图构造时固定」的攻击行动。
刻意**不登记**的是三类：段数或伤害现算的 `Dynamic*AttackIntent`（`BookOfStabbing.STAB`、
`Maw.NOMNOMNOM_MULTI`、`Darkling.NIP`、`GiantHead.IT_IS_TIME`、`Transient.ATTACK`）、
第二、三幕里那些只含 `SummonIntent`/`DefendIntent`/`HealIntent`/`DeathBlowIntent`/空意图表的行动，
以及 `MultiAttackIntent(int, Func<int>)` 那条**伤害恒定但段数现算**的重载——后者不在
`IsStableAttackShape` 的覆盖范围内（判据只管伤害），所以名单里第二个实参一律不是 lambda。
类型短名的碰撞按 §2.4 的同一办法核过：37 个第二、三幕类型名在本体 `sts2.dll` 的类型表里都不存在。

登记与分支建模**互不影响**：Guardian、Hexaghost 这类分支仍被拒的怪物，其静态攻击行动照样是静态的，
多登记不会让求解器接受一条它本来会拒绝的路线；反过来，缺这条登记也不改变任何模拟数值，
只影响界面上的可信度标注。

---

### 2.8 Looter（强盗）：偷金币与逃跑

Looter 是本次新解锁的第八个分支怪物，分支与效果都完整建模：

| 环节 | AFTP 源码 | 适配 |
| --- | --- | --- |
| `MUG_BRANCH` | `_mugCount < 2 ? "MUG" : "AFTER_SECOND_MUG"`，不抽 RNG | `ExordiumBranchResolvers` 按 `combat.GetMonsterInt(creature, "_mugCount")` 重算 |
| `AFTER_SECOND_MUG` | 原版 `RandomBranchState`，SMOKE_BOMB／LUNGE 各 50% | 走求解器通用的随机分支逻辑（权重在根捕获时冻结） |
| `_mugCount` | `Mug`／`Lunge` 各 `_mugCount++` | 登记为**怪物标量状态**（随分支 Fork、进状态指纹与续用核对文本） |
| `MUG`／`LUNGE` | 攻击 → `StealGold()` → `_mugCount++` | `combat.RecordThievery`（与原版 `GremlinMerc` 同一权威实现）＋ `SetMonsterInt("_mugCount", +1)` |
| `SMOKE_BOMB` | `CreatureCmd.GainBlock(Creature, EscapeBlock, ValueProp.Move, null, false)` | `simulator.GainBlock(owner, 6, ValueProp.Move)`；`EscapeBlock` 已用 `AfpReflection.RequireConst` 钉死 |
| `ESCAPE` | `CreatureCmd.Escape(Creature, true)` | `combat.CreatureEscaped(owner)`，并按 `RegisterOwnerRemovingMove` 声明「施法者自己离场」——效果侧与意图预测侧都要这个事实，只登记一半会让玩家在它跑了之后还看到一条不存在的意图 |

`ThieveryPower` 是**本体**能力（`MegaCrit.Sts2.Core.Models.Powers`）：Looter 在 `AfterAddedToRoom`
里按 `GoldAmount` 给自己挂一份、`Target` 指向玩家。求解器本体已有这条口径（偷取上限为
`min(Amount, 玩家金币)`、未追回账本 `OutstandingStolenResource`、击杀后的奖励补偿），
适配不重复实现，直接复用 `RecordThievery`。
`_hasSpoken` 与死亡台词的 `Rng.Chaotic` 抽样只影响对白，不进预测。

**未覆盖的同型内容**：`Mugger`（第二幕，同样是 `MUG_BRANCH` + 偷金币形状）不在本批约定范围
（地基 + 第一幕 + 心脏）内，仍然走安全失败路径。

---

### 2.9 回归：意图预览把**实机**分支计数推高了（往昔之书 7×15）

**症状**（玩家报告）：往昔之书的「多重刺击」第一回合显示并打出 **7×15**，正常应是 7×3。

**根因**：`IntentForecaster`（意图预览）在推演后续回合时，对**第三方**分支状态直接调用了
`MonsterState.GetNextState` —— 也就是往昔之章自己那个委托，它跑在**实机模型**上并且会写实机字段：

```csharp
// ActsFromThePast.BookOfStabbing
private string SelectNextMove(...) { ...; StabCount++; return "STAB"; }   // 四条路径各自 StabCount++
new MoveState("STAB", Stab, new DynamicMultiAttackIntent(() => StabDamage, () => StabCount));
```

段数读的就是这个实机 `_stabCount`，而 `Stab` 行动是 `DamageCmd.Attack(StabDamage).WithHitCount(StabCount)`。
预览一次要看 `SolverWeights.SetupValueHorizonTurns = 16` 个回合，每回合调一次委托就
`StabCount++` 一格：从入场时的 1（`AfterAddedToRoom`）被推到 15 上下，于是**游戏自己**的意图显示
和实际结算都变成 7×15。这不是「预览不准」，是求解器改坏了玩家正在打的那场战斗，
直接违反「不得读取会随实机推进而变化的 live 值，也不得修改真实战斗」。

同一段代码对 `default:` 分支（第三方 `MonsterState` 子类都落在这里）与 `ConditionalBranchState`
分支都会调用实机委托；往昔之章的 `ConditionalBranchState` 继承自 `MonsterState`、不是原版那个类型，
所以走的是 `default:`。`BranchMonsterStaticSnapshot.Capture` 里的同名调用有
`!IsForeignBranchState(...)` 守卫，搜索侧的 `ThirdPartyMonsterBranches.Resolve` 走登记表——只有
预览这一条路漏了。

**修法（两条一起上）**：

1. `IntentForecaster.RollNext` 遇到第三方分支状态时**默认不调用**：记一条
   `unsupported`（`<怪物>.<分支>:第三方分支状态`）并让这条怪物退出推演，返回 `null`，
   调用方 `cursor.Active = false`。宁可少一段预览，也不拿玩家的战斗去换。
2. 新增 `ThirdPartyAdapterRegistry.RegisterPureBranchSelector(怪物类型名, 分支 Id)`：
   逐行复核过「只读自己的标量字段与实机 `StateLog`、按源码顺序抽传入的 `rng`、不写实机状态、
   不下命令」的选择函数可以显式声明放行，预览行为与适配前完全一致。登记前必须先有同一
   (怪物, 分支) 的 `RegisterMonsterBranchResolver`（核心会拒绝只声明一半）。
   往昔之章已声明 §2.1 表里的 8 条（`AcidSlimeMedium` / `SpikeSlimeMedium` / `FungiBeast` /
   `JawWorm` / `GremlinNob` / `SlaverBlue` 的 `MOVE_BRANCH`、`GremlinWizard` 的 `AFTER_CHARGE`、
   `Looter` 的 `MUG_BRANCH`），见 `ExordiumBranchResolvers.PureSelectors`。

**边界**：`RegisterPureBranchSelector` 是「已复核事实」，判不了真伪；往昔之章再改这些选择函数的
实现时要回来重新核对。往昔之书、史莱姆分裂等**没有声明**的分支现在一律不调用，因此它们所在战斗的
预览会在第一次分支处停下并显示「预览可能不完整」——这是刻意的，它们本来也还没有预测实现。

**未处理的同类点**：`SimulatedCombatState.GetNextMoveIdFromStateLog` 也会调
`MonsterState.GetNextState`，目前唯一调用方是振翅 Power 的镜像，而振翅只挂在原版跳跳虫身上，
够不到第三方分支状态；如果将来第三方怪物也能拿到这类能力，这里要一并加守卫。

---

### 2.10 第一幕的能力与死亡钩子：补上「打赢了却报未知战损」的第二处来源

第一幕适配第一版只登记了分支、行动效果与卡牌补丁，**没有登记任何 Power 与死亡钩子**。而判定链看的是
`PredictionGap.Method` 里是否含 «Death»（§3.6），于是下面这些没登记的重写让对应的战斗一律给不出战损：

| 重写 | 归属 | 复核结论 | 处理 |
| --- | --- | --- | --- |
| `SporeCloudPower.AfterDeath` | 真菌兽（入场时给自己挂 2 层） | owner 死亡且未被拦截时，给**所有活着的玩家**各 `Amount` 层易伤（applier = null）；一个活人都没有则直接返回 | 真镜像：`AfterDeathMirrors.Register` |
| `FungiBeast.BeforeDeath` | 真菌兽 | 只有 `NSporeImpactVfx.Create` + Godot 定时器播粒子，无命令、无数值/状态/RNG | 登记为忽略 |
| `Cultist.BeforeDeath` | 邪教徒 | 死亡音效（`Rng.Chaotic` 抽编号）+ 条件式台词气泡 + `Cmd.Wait(2.5)`，不下命令 | 登记为忽略 |
| `Hexaghost.AfterDeath` | 六角幽魂 | `_visuals.HideAllOrbs/Dispose` + `NGame.ScreenShake` | 登记为忽略（其分支仍未适配，见 §2.2） |
| `AngryPower.AfterDamageReceived` | 疯狂小鬼（入场时挂 1/2 层） | 自己挨到**有来源的攻击伤害**且真掉血时，给自己加 `Amount` 点力量；源码写的是 `props.HasFlag(Move) && !props.HasFlag(Unpowered)` | 真镜像：`AfterDamageReceivedMirrors.Register` |

新增 `ExordiumHooks`（自检用新加的 `AfpReflection.RequireOverride` 核对「对方确实还重写着这个签名」）。
求解器本体为第三方补了两个入口：`AfterDeathMirrors.Register(Type, handler)` 与
`BeforeDeathMirrors.Register(Type, handler)` / `RegisterIgnored(Type)`；两条死亡镜像登记表都补了
「首次分发后拒绝迟到登记」的冻结检查（与 `AfterDamageReceivedMirrors` 一致）。

**批量排查方法（本轮就是这么找出上面这五条的）**：把对方每个 `CustomMonsterModel` / `CustomPowerModel`
的 `public override` 方法列出来（脚本按类边界切文本即可），只挑名字含 «Death» 的逐个归类。
第一幕的完整清单是：`Cultist.BeforeDeath`、`FungiBeast.AfterAddedToRoom+BeforeDeath`、
`Guardian.BeforeDeath`（**真效果**，见下）、`Hexaghost.AfterAddedToRoom+AfterDeath`、
`JawWorm.AfterAddedToRoom+BeforeSideTurnStart`（见下）——其中 `AfterAddedToRoom` 跑在根捕获之前，
它的效果本来就在捕获到的根里，不需要镜像。

**本轮刻意没做的两条（都在第一幕里）**：

- `Guardian.BeforeDeath`：死者是守卫者且 `SharpHidePower.AttackInProgress` 时，对**攻击来源**造成
  `Amount` 点无来源伤害。这是真效果，但守卫者本身的分支还没适配（§2.2），补偿它不会让这场战斗可解，
  留到守護者那一批一起做。
- `JawWorm.BeforeSideTurnStart` + `_hardModeBlockApplied`：`HardMode` 为真时第一幕之外的遭遇
  （`TheBeyond` 的硬模式爪虫，`jawWorm.HardMode = true`）会在玩家方回合开始获得 `BellowBlock` 点格挡，
  且开场行动取自分支状态。求解器**没有 `BeforeSideTurnStart` 这个分发点**，要做需要先补阶段镜像。
  第一幕的爪虫 `HardMode` 为假，本条不影响第一幕。

---

### 2.11 史莱姆三件套：分裂（第三方怪物生成的第一批）

`AcidSlimeLarge` / `SpikeSlimeLarge` / `SlimeBoss` 共用一套形状：入场时各挂一份 `SplitPower`，
血量掉到**一半或以下**时把 `_splitTriggered` 置位、并把当前行动强制改写成 `SPLIT`，
执行 `SPLIT` 时杀掉自己、按「分裂那一刻的血量」生成子史莱姆。

| 环节 | AFTP 源码 | 适配 |
| --- | --- | --- |
| `SplitPower.AfterDamageReceived` | `target == Owner && UnblockedDamage > 0 && CurrentHp <= MaxHp / 2` → `SplitTriggered = true` + `SetMoveImmediate(SplitState, true)` | `AfterDamageReceivedMirrors.Register`：模拟状态上比血量（整数除法），`SetMonsterBool("_splitTriggered")` + `ForceMonsterMove("SPLIT")` |
| `MOVE_BRANCH` | 三个都是「`SplitTriggered` → SPLIT」，另两个再按源码概率选招（0.6f／0.4f／30% 的重抽顺序逐条照抄） | `ExordiumBranchResolvers` 三个解析器（都复核为纯读取，已声明放行给预览） |
| `_splitTriggered` | 三个怪物各自的私有 bool | 登记为**怪物标量状态**（随分支 Fork、进指纹与续用核对） |
| `SPLIT` | `CreatureCmd.Kill(自己)` → 按遭遇布点表的前缀挑空位 → `CreatureCmd.Add` + `SetMaxHp(当前血量)` + `Heal(当前血量)` | `SplitInto`：`simulator.Kill(owner)`（`force` 缺省，与源码的 `Kill(c, false)` 一致）→ 前缀挑空位 → `MonsterSpawnSupport.SpawnByType(..., maxHpOverride: 当前血量)`；`killedOwner = true` 让死亡结算照常跑 |
| 子代血量的两次抽样 | 先由 `CreatureCmd.Add` 按 `Min/MaxInitialHp` 掷一次初始生命，随后才被 `SetMaxHp` 覆盖 | `SpawnByType` 里的 `CreatePredictedMonster` 掷同一次（消耗 `Rng.Niche`，与实机同一条流），**不能省**——省掉会让 Niche 流与实机错位 |
| 二次分裂 | 生成出来的大型史莱姆靠自己的 `AfterAddedToRoom` 再挂一份 `SplitPower` | 求解器不跑第三方 `AfterAddedToRoom`，`SplitInto` 对带 `InheritsSplitPower` 的子代显式 `ApplyPower(SplitPower, 1)` |
| 布点 | `Encounter.Slots.FirstOrDefault(s => s.StartsWith("acid_med") && !occupied)` | 新增 `SimulatedCombatState.EncounterSlots`（根捕获时冻结的数组），前缀与占用规则逐字照抄；挑不到就传 `null`（源码那个分支只影响摆放位置） |
| 顺带补的招 | `GOOP_SPRAY`（`SlimedCount` 张 Slimed）、`CORROSIVE_SPIT`/`FLAME_TACKLE`（各 2 张）、`LICK`（Weak 2／Frail `FrailTurns`）、`PREP_SLAM`（纯表演，登记成空操作） | `ExordiumMoveEffects`；`SlimedCount` 与 `FrailTurns` 是按进阶冻结的实例属性，走 `RegisterStaticIntMembers`，三个字面量走 `RequireConst` 钉死 |

**求解器本体新增能力**：`MonsterSpawnSupport.SpawnByType(…, Type monsterType, …, int? maxHpOverride, …)`——
按**运行时类型**生成第三方怪物（规范实例取自 `ModelDb`），与泛型 `Spawn<T>` 走同一条路。
这是「第三方怪物生成／召唤」这一整类的第一批用户：第二幕的机械傀儡球、收集者、小鬼头目与
第三幕的复活／召唤都等它（§4.1 组 2）。

**未覆盖**：`SlimeBoss` 的 `ShouldStopCombatFromEnding => true`（分裂前不让战斗结束）**没有**单独镜像，
因为求解器那一处走的是原生 `Hook.ShouldStopCombatFromEnding(State.CombatState)`，会在**模拟的**
监听者列表上分发，`SplitPower` 的实现是纯返回 `true`，本来就读得对。

---

### 2.12 小鬼盾兵与「私有 RNG 流」的共享镜像

`GremlinShield`（小鬼盾兵）的分支只看队友数，真正难点在行动：
`Protect` 用**这只怪物自己那条 RNG 流**（`MonsterModel.Rng`）在所有存活队友里抽一个目标。

| 环节 | AFTP 源码 | 适配 |
| --- | --- | --- |
| `MOVE_BRANCH` | `GetTeammatesOf(Creature).Count > 1 ? "PROTECT" : "SHIELD_BASH"`（含已经倒下但还没离场的） | 按求解器的同名入口数己方全体；已复核为纯读取并声明放行给预览 |
| `PROTECT` | `teammates.Any() ? Rng.NextItem(teammates) : Creature` → `GainBlock(target, ProtectBlock, ValueProp.Move, null, false)` | 先建存活队友列表（**空列表一次都不抽**，与源码的 `Any()` 短路一致），再 `MonsterRngSupport.State(...).NextItem(...)`；`ProtectBlock`（A8+ 11／否则 7）走静态数值成员 |
| 已抽次数 | 无对应字段 | 写进自建标量成员 `adapter_gremlin_shield_rng_draws`（**不进** `RegisterMonsterStateMembers`——实机怪物没有这个成员），指纹遍历整张标量表，于是只差这条流进度的分支不会被去重 |

**共享实现**：这条流原来只有心脏适配在自己的文件里复刻了一份（`ShieldOrbRngPredictionState`）。
本轮把它提到求解器本体：`src/Prediction/MonsterRngSupport.cs` 的
`MonsterRngPredictionState` + `MonsterRngSupport.State(simulator, monster)`，提供
`NextInt` / `NextFloat` / `NextItem`（镜像 `Rng.NextItem` 的「空集合不抽」）与 Fork，
心跳适配的盾兵球位改用同一份、原地的那个类删掉（`docs/THIRD_PARTY_ADAPTERS.md` §6 的
「同一战斗语义只能有一个权威实现」）。`MonsterRngSupport.VerifyShape()` 核对
`MonsterModel.Rng` 这个公开访问点还在，两个适配的自检都会调它。

**成立前提（适配方必须逐行复核）**：实机实例上的当前状态只有在「这条流在战斗里只被这只怪物
自己的行动推动」时才是准确起点。盾兵与小鬼盾兵都满足（音效与动画走的是 `Rng.Chaotic`，
种子只由 `CreateCreature` 写过）。**已知局限**：状态是**首次用到时**才从实机拷一份，
不是根捕获时冻结的——搜索期间实机若自己推动了这条流（同一只怪在同一回合真的行动了），
拷贝的起点会偏后；与心脏适配原来的行为一致，改成根捕获播种需要给怪物侧补一个
「根捕获时播种」的入口（见 §4.1 组 3）。

---

### 2.13 第二幕开篇：Centurion 与 Mystic

第一幕剩下四个（§2.2）都需要新的本体能力，先把第二幕开起来——`Centurion` 与 `Mystic` 是同一场
遭遇（「百夫长与秘术师」）的两个成员，两个都不适配这场遭遇就仍然算不出来，所以成对做。

| 环节 | AFTP 源码 | 适配 |
| --- | --- | --- |
| `Centurion.MOVE_BRANCH` | `rng.NextInt(100)`；`count = GetTeammatesOf(Creature).Count`；`num >= 65 && !LastTwoMoves(PROTECT) && !LastTwoMoves(FURY)` → 有队友 `PROTECT` 否则 `FURY`；否则 `!LastTwoMoves(SLASH)` → `SLASH`；再不然同上 | `CityBranchResolvers`，RNG 调用顺序与短路逐条照抄；已声明为纯读取 |
| `Centurion.PROTECT` | `teammates.Any() ? Rng.NextItem(teammates) : Creature` → `GainBlock(target, ProtectBlock, Move)`，`ProtectBlock` = A8+ 20／否则 15 | 与小鬼盾兵的 `Protect` 同型：空队友列表**一次都不抽**，抽到就写 `adapter_centurion_rng_draws` |
| `Mystic.MOVE_BRANCH` | **先**把队友（含自己）的 `MaxHp - CurrentHp` 求和，`> HealThreshold` 且没连治两次 → `HEAL`（这一步不抽 RNG）；否则 `rng.NextInt(100)`：`>= 40` 且上一步不是 `ATTACK` → `ATTACK`；没连强两次 → `BUFF`；再不然 `ATTACK` | 和用**模拟状态**的 `GetCreature(...).MaxHp/CurrentHp`（见下）；已声明为纯读取 |
| `Mystic.ATTACK` | 攻击 + 给全体目标 2 层 Frail | `ApplyFromMonster<FrailPower>(player, 2, owner)`（源码不过滤存活，这里只在玩家还活着时施加——玩家已死则战斗已结束） |
| `Mystic.HEAL` | 给所有存活队友（含自己）回复 `HealAmount` | `simulator.Heal(teammate, HealAmount)` |
| `Mystic.BUFF` | 给所有存活队友（含自己）`StrengthAmount` 点力量 | `combat.Apply<StrengthPower>(teammate, amount, owner)` |

`ProtectBlock` / `HealAmount` / `HealThreshold` / `StrengthAmount` 都是 `AscensionHelper` 或
`20 * Players.Count` 形式的实例属性 → `RegisterStaticIntMembers`，根捕获读一次
（单人局里 `HealAmount`/`HealThreshold` 都是 20）。

**本体新增能力：分支解析器能读模拟状态。** `MonsterBranchResolver` 的签名补了最后一个
`CombatPredictionSimulator` 参数——`Mystic` 那条分支要看**队友已损失生命和**，而它只能在模拟状态上算
（`simulator.State.GetCreature(teammate).MaxHp / CurrentHp`）。实机血量在 worker 里既不能读（会随实机推进
变化）也不该读（不随分支 Fork）。两个适配的全部 15 个解析器同步补了形参；这次签名变化记在
[第三方适配手册](THIRD_PARTY_ADAPTERS.md) §2.13。

---

### 2.14 第二幕：熊与尖刺、监工、强盗

| 怪物 | 分支 | 适配内容 |
| --- | --- | --- |
| `Bear` | 无（固定循环 BEAR_HUG → LUNGE → MAUL → LUNGE） | `BEAR_HUG`：给活着的目标 `-DexReduction`（4/2）点敏捷；`LUNGE`：攻击 + 给自己 `LungeBlock`（const 9，`RequireConst` 钉死）点格挡；`MAUL` 是纯攻击，不需要行动效果 |
| `Pointy` | 无（只有一个纯攻击行动 STAB） | **什么都不用登记**：`MultiAttackIntent(AttackDamage, 2)` 已在常量攻击表里；它的 `AfterAddedToRoom` 只是给熊的死亡事件挂了一句台词。这是第一只「零登记即完整」的第二、三幕怪物 |
| `Taskmaster` | 无（只有一个行动） | `SCOURING_WHIP`：攻击 + `WoundCount`（3/1）张 `Wound` 进弃牌堆（`CardPilePosition.Bottom`，与源码的 `(CardPilePosition)1` 一致）；A9 及以上再给自己 1 点力量——源码写的是 `GainsStrength` = `HasAscension(A9)`，这是**整场不变**的运行期属性，按 `GremlinFat.AppliesFrail` 的既有读法直接读同一实例属性 |
| `Mugger` | `MUG_BRANCH` + 随机分支 `AFTER_SECOND_MUG` | 与第一幕 `Looter` **同型**：`_mugCount` 播种、`SelectAfterMug` 重算、`MUG`／`BIG_SWIPE` 走 `RecordThievery` + 计数、`SMOKE_BOMB` 给 `EscapeBlock`（A8+ 17／否则 11，属性 → 静态数值成员）点格挡、`ESCAPE` 走 `CreatureEscaped` + owner-removing 声明 |

四只都不需要新的本体能力（`Mugger` 复用第一幕那套、`Bear`/`Taskmaster` 用既有的一般入口），
因此这一批的风险全在「数值/顺序有没有抄错」，逐条对照见上表。

---

### 2.15 第二幕：Romeo、球状守卫、蛇怪（含本体新增「攻击前」钩子）

| 怪物 | 分支 | 适配内容 |
| --- | --- | --- |
| `Romeo` | `MOVE_BRANCH`（`!LastTwoMoves(CROSS_SLASH) ? CROSS_SLASH : AGONIZING_SLASH`，不抽 RNG） | `MOCK`（开场纯台词，`UnknownIntent`）登记成空操作，免得被记成「未支持意图」；`AGONIZING_SLASH`：攻击 + `WeakAmount`（const 3）层虚弱 |
| `SphericGuardian` | 无（固定循环 ACTIVATE → FRAIL_ATTACK → SLAM ⇄ HARDEN） | `ACTIVATE`：给自己 `ActivateBlock`（A8+ 35／否则 25）；`FRAIL_ATTACK`：攻击 + `FrailAmount`（const 5）层破甲；**`HARDEN`：先 `HardenBlock`（const 15）格挡再打**——走本轮的「攻击前」钩子；`SLAM` 是纯攻击。开场那 40 点格挡、`BarricadePower` 与 `ArtifactPower` 都发生在 `AfterAddedToRoom`（根捕获之前），已在根里 |
| `Snecko` | 原版 `RandomBranchState`（60% BITE／40% TAIL_WHIP，通用逻辑已覆盖） | `GLARE`：给活着的目标 1 层困惑；`TAIL_WHIP`：2 层易伤，**A9 及以上**再加 2 层虚弱；`BITE` 是纯攻击 |

**本体新增能力：行动的攻击前部分（`RegisterMonsterMoveBeforeAttack`）。**
求解器按「攻击 → 行动效果」结算一个行动，而源码里 `HARDEN` 是「先加格挡再打」。只靠行动效果表达不了
这个顺序（格挡会晚一拍，与挨打反伤这类效果交互起来结果不同），所以补了这段登记：

```csharp
ThirdPartyAdapterRegistry.RegisterMonsterMoveBeforeAttack("SphericGuardian", "HARDEN", handler);
```

派发点在 `MonsterMoveSemantics.ApplyForecastMove` 里、原版 `MonsterMoveEffects.ApplyBeforeAttack` 之后、
攻击上下文建立之前；同一 (怪物, 行动) 同时登记前后两段是允许的，各自按源码时序跑。
这也是后续 `Guardian`（模式切换要读「正在执行行动」）与 `ShelledParasite` 需要的那块拼图。

**进阶判据用反射，不猜语义。** 蛇怪的尾鞭写的是内联的 `AscensionHelper.HasAscension((AscensionLevel)9)`，
而那个辅助类型是**游戏内部的**、适配层看不见。把「A9」自己翻译成 `AscensionLevel >= 9` 属于猜语义，
所以 `AfpReflection` 新增 `VerifyAscensionHelper()` + `HasAscension(9)`：按名字找到
`AscensionHelper` 与 `AscensionLevel` 枚举、核对 `HasAscension(AscensionLevel)` 这个静态方法还在，
再原样反射调用——判据与游戏逐字相同；方法不在时自检失败、整个适配拒绝登记。

顺带补了这两只的死亡钩子（否则每场打赢都不给战损，§3.6 同型）：`SphericGuardian.BeforeDeath`
（`PlayDetectSfx`）与 `Snecko.BeforeDeath`（一句死亡音效）逐行复核为纯表现层，登记为忽略
（`CityHooks`）。

---

### 2.16 第二幕：Chosen（灾祸）与 Champ（冠军，首领）

| 怪物 | 分支 | 适配内容 |
| --- | --- | --- |
| `Chosen` | `MOVE_BRANCH`：**开场必 HEX 并把 `UsedHex` 置位**；之后「上一步不是 DEBILITATE／DRAIN」就各半概率二选一，否则 40% ZAP／60% POKE | `_usedHex` 播种；`HEX` 给玩家 1 层灾祸；`DEBILITATE` 攻击 + 2 层易伤；`DRAIN` 3 层虚弱 + 自己 3 点力量；`ZAP`／`POKE` 是纯攻击 |
| `Champ` | `MOVE_BRANCH`：回合计数 +1 → 掉到半血以下且未触发过 ⇒ `ANGER`；触发过且最近两次不是 `EXECUTE` ⇒ `EXECUTE`；第 4 回合且未触发 ⇒ 计数清零 + `TAUNT`；否则抽 RNG（30% 以下优先锻炉，最多 2 次且不连出；再 `GLOAT`；再 55% 以下 `FACE_SLAP`；否则 `HEAVY_SLASH`） | `_numTurns`／`_forgeTimes`／`_thresholdReached` 播种；`DEFENSIVE_STANCE` 给 `BlockAmount` 格挡 + `ForgeAmount` 层金属化；`FACE_SLAP` 2 破甲 + 2 易伤；`TAUNT` 2 虚弱 + 2 易伤；`GLOAT` `StrengthAmount` 力量；`ANGER` **先清掉自己所有减益**、再加 `StrengthAmount × 3` 力量 |

**两条分支都会写自己的计数**（`Chosen._usedHex`、`Champ._numTurns` / `_thresholdReached` / `_forgeTimes`），
所以它们**刻意不进**「纯读取」名单：预览默认不调用未声明的第三方选择函数，正是为了不让这种
「选择即记账」的委托去改实机状态（往昔之书的 `StabCount++` 就是这么把真实战斗改成 7×15 的，见 §2.9）。
代价是这两只的预览会在分支处停下并显示「预览可能不完整」，搜索侧照常按登记的解析器算。
`Champ` 的半血判定读的是**模拟状态**的 `CurrentHp`／`MaxHp`（§2.13 给解析器补的那个参数）。

**两个能力镜像**（都只是把源码那三五行搬到模拟状态上）：

- `MetallicizePower.BeforeSideTurnEndEarly`（锻炉）：自己那一方回合末按层数获得格挡，`ValueProp.Unpowered`
  → `BeforeSideTurnEndMirrors.RegisterEarly`（这张表本来就有，心脏适配的 `MetallicizePowerA4h` 用的是同一个入口）；
- `HexOriginalPower.AfterCardPlayed`（灾祸）：出牌者就是持有者且打出的**不是攻击牌**时，往抽牌堆塞
  `Amount` 张 `Dazed`（随机位置）→ `AfterCardPlayedMirrors.Register`。

顺带把这两只的死亡钩子（`Chosen.BeforeDeath` 一句音效、`Champ.BeforeDeath` 屏幕震动 + 音效）
登记为忽略。

---

### 2.17 第二幕：往昔之书（`BookOfStabbing`，精英）与「动态攻击值」登记点

§2.9 修掉的是**预览不去动实机**这一半；这一节补上另一半：这只怪的段数本来就该随回合涨，
求解器必须**按当前分支现算**，而不是用根捕获那一刻冻结的值。

| 部位 | 源码（`ActsFromThePast.BookOfStabbing`） | 适配 |
| --- | --- | --- |
| 初始行动 | 状态机的初始状态是 `MOVE_BRANCH`，开场就走一次 `SelectNextMove`；`_stabCount` 由 `AfterAddedToRoom` 置 1，随后每次转移 +1 | 根捕获按实机值播种 `_stabCount`（`RegisterMonsterStateMembers`），**不**在捕获时再跑一次分支 |
| `MOVE_BRANCH` | 抽一次 `rng.NextInt(100)`；`< 15` 时最近一步是 `BIG_STAB` ⇒ `STAB`、否则 `BIG_STAB`；最近连出两次 `STAB` ⇒ `BIG_STAB`；否则 `STAB`。四条出口**都先 `StabCount++`** | `CityBranchResolvers.BookOfStabbing`（RNG 顺序照抄）；因为它写自己的计数，**不在**「纯读取」名单里 |
| `STAB` | `DynamicMultiAttackIntent(() => StabDamage, () => StabCount)`：伤害按 A9 冻（7／6），段数现算 | `RegisterMonsterAttackValues("BookOfStabbing", "STAB", …)`：伤害取 `GetMonsterStaticInt("StabDamage")`、段数取 `GetMonsterInt("_stabCount")`，每次出手重算 |
| `BIG_STAB` | `SingleAttackIntent(BigStabDamage)`（A9 24／21，常量构造） | `LaterActsStableAttacks` 已按冻结值登记 |
| 开场能力 | `AfterAddedToRoom` 给自己挂 1 层 `PainfulStabsPower` | **原版 Power**，攻击后镜像（`AfterAttackMirrors.HandlePainfulStabsPower`：按每个玩家的未格挡命中数往弃牌堆塞 `Wound`）核心里已有，实机实例在根捕获时已带该 Power |
| 死亡 | `OnDeath` 只播一句音效（C# 事件，不是钩子） | 无需处理；模拟侧不触发实机事件 |

**新增的核心能力：`RegisterMonsterAttackValues`（动态攻击值）**。求解器原来只有两种口径——
`RegisterStableAttack`（数值在意图构造时固定，压掉「动态伤害」误报）和「冻结值」。`Dynamic*AttackIntent`
属于第三类：数值确实会变，冻结值会让**整场都按捕获那一刻算**（往昔之书会在整场都用同一个段数）。
现在适配层可以登记一个 `(combat, monster) => BranchMonsterAttack(伤害, 段数)`，`BranchMonsterAi.CurrentMove`
每次取当前行动时先查这张表：命中的行动按模拟状态重算，未命中的仍走冻结值。两条纪律：

1. 解析器**只读模拟状态**（`GetMonsterInt` / `GetMonsterStaticInt`）。段数依赖的计数必须先用
   `RegisterMonsterStateMembers` 播种，否则根捕获之后读未播种成员会当场抛（`GetMonsterInt` 的既有语义）。
2. 与 `RegisterStableAttack` **互斥**：同一条行动两边都登记会在初始化时直接抛错。混在一起时执行侧按
   动态值走、界面侧却被固定声明压掉「动态伤害」提示，是自相矛盾的登记。

这条登记同时作用于**搜索结算**（`MonsterMoveSemantics.ApplyForecastMove` 逐段取 `move.AttackHits`）与
**当前回合的意图显示**（`IntentForecaster` 当前回合也读实机意图，值本来就与实机一致）；预览的后续回合
仍按 §2.9 在分支处停下。

**一处已知的、非往昔之章独有的差异**：`PainfulStabsPower.ShouldCreatureBeRemovedFromCombatAfterDeath`
对**持有者自己**返回 `false`（书死了以后尸体留在场上，StS1 同款表现），而求解器的死亡生命周期根本没有
分发这个钩子（核心只镜像了 `ShouldPowerBeRemovedAfterOwnerDeath`），模拟里死掉的敌人一律移出阵容。
对往昔之书这场单体精英战斗而言，影响只落在「阵容里是否还留着一具尸体」：它已经死了，不参与行动、
不再触发这个 Power 的攻击后效果，胜负判定看的是「还有没有活着的敌人」。这是**原版** Power 的既有差异
（原版实验体身上挂着同一个 Power，同样如此），不是这次适配引入的；要补就得动核心死亡生命周期，
影响原版语义，不在本批范围内，因此这里只记下来。

---

### 2.18 第二幕：火炬头（`TorchHead`，零登记即完整）

`TorchHead` 整只怪只有一个行动，而且**不需要任何登记**（与第一幕的 `Pointy` 同型，第二、三幕里的
第二只）：

| 部位 | 源码（`ActsFromThePast.TorchHead`） | 是否需要适配 |
| --- | --- | --- |
| 行动状态机 | 单个 `MoveState("TACKLE", …, SingleAttackIntent(7))`，`FollowUpState` 指向自己，初始状态也是它 | 攻击已在 `LaterActsStableAttacks`（`TorchHead.TACKLE`＝`SingleAttackIntent(7)`） |
| `AfterAddedToRoom` | `Creature.Died += OnDeath`（把 `_alive` 置假）＋ `StartFireLoop()`：往骨架上挂一串火焰粒子 | **不需要**：`_alive` 只被那个粒子循环读（`SpawnFireParticle` 里的早退），`Died` 事件的另一头也是它；整条链只有节点、Tween、音效与 `Rng.Chaotic`（外观流，不是九条战斗流之一） |
| 分支 / 标量状态 / 行动效果 / Power | 都没有 | — |

按 §4.1 的五项验收口径，它是第二只「五项都不需要」的怪物：能算出的路线就是完整的路线。
**未验证**：没有在游戏内打过「收集者」遭遇（火炬头只出现在那里），这条结论来自逐个成员的反编译阅读。

---

### 2.19 第三幕开篇：Repulsor 与蛇匕首（`SnakeDagger`）

第三幕的前两只都**不需要任何新的本体能力**，只用既有的行动效果、分支解析器与「施法者自己离场」入口。

| 怪物 | 分支 | 适配内容 |
| --- | --- | --- |
| `Repulsor` | `MOVE_BRANCH`：抽一次 `NextInt(100)`；`< 20` 且最近一步不是 `ATTACK` ⇒ `ATTACK`，否则 `DAZE`（只读 rng 与行动历史，**已声明为纯读取**） | `DAZE`：给每个活着的目标往**抽牌堆随机位置**塞 `DazeAmount`(2) 张 `Dazed`；`ATTACK` 是常量构造攻击，已在第二三幕的常量表里 |
| `SnakeDagger` | 无分支：`WOUND_STAB` → `EXPLODE` → `EXPLODE` 自循环 | `WOUND_STAB`：9 点攻击（常量表已登记）之后往**弃牌堆底部**塞 1 张 `Wound`；`EXPLODE`：25 点攻击由通用攻击循环按意图结算，效果侧只补源码最后那句 `CreatureCmd.Kill(自己, false)`，并声明 `RegisterOwnerRemovingMove` |

顺手纠正常量攻击表里的一句注释：`DeathBlowIntent : SingleAttackIntent`（反编译
`MegaCrit.Sts2.Core.MonsterMoves.Intents.DeathBlowIntent` 确认），所以它的伤害**是**攻击意图的伤害、会被
`BranchMonsterAi` 一并冻结；那里「`DeathBlowIntent` 没有攻击意图可声明」的说法只对「声明为**常量构造**」
成立——`new DeathBlowIntent(() => 25m)` 的闭包是调用方 lambda，形状核对会拒绝这种声明，而伤害本身照旧可用。

**未验证**：没有在游戏内打过「排斥者」或「蛇匕首」遭遇，`DAZE` 的随机入堆位置与 `EXPLODE` 的自杀
都**未实机验证**；也没有最小差分夹具（AFTP 程序集接不进无人测试的隔离进程）。

---

### 2.20 第三幕：尖刺者（`Spiker`）

`Spiker` 同样**不需要新的本体能力**：开场那 `StartingThorns`（A9+ 7／否则 4）层荆棘发生在
`AfterAddedToRoom`，根捕获时已经在它身上；要补的只有「再加一次荆棘」那一步与它自己的计数。

| 部位 | 源码（`ActsFromThePast.Spiker`） | 适配 |
| --- | --- | --- |
| 标量状态 | `_thornsCount`：`AfterAddedToRoom` 置 0，每次 `BUFF_THORNS` +1；分支读它判「超过 5 次」 | `RegisterMonsterStateMembers("Spiker", "_thornsCount")`（根捕获播种、随 Fork、进指纹） |
| `MOVE_BRANCH` | **`_thornsCount > 5` 就直接 `ATTACK` 且一次 RNG 都不抽**；否则抽一次 `NextInt(100)`，`< 50` 且最近一步不是 `ATTACK` ⇒ `ATTACK`，否则 `BUFF_THORNS` | `BeyondBranchResolvers.Spiker`（先判计数再抽 RNG，短路照抄）；只读自己的标量与行动历史，**已声明为纯读取** |
| `BUFF_THORNS` | `ThornsCount++` 之后给自己挂 2 层 `ThornsPower`（`BuffAmount`） | `BeyondMoveEffects.SpikerBuffThorns`：`SetMonsterInt("_thornsCount", +1)` + `combat.Apply<ThornsPower>(owner, BuffAmount, owner)`（原版 Power）；`BuffAmount` 用 `RequireConst` 钉死 2 |
| `ATTACK` | `SingleAttackIntent(AttackDamage)` | 已在第二三幕常量表里 |

阈值 5 是源码里的字面量（`ThornsCount > 5`），没有可钉的常量；它只决定分支走向，不参与任何数值。

**未验证**：没有在游戏内打过「尖刺者」遭遇，荆棘层数的叠加、`> 5` 那次短路后的行动序列都**未实机验证**；
也没有最小差分夹具。

---

### 2.21 第三幕：球体行者（`OrbWalker`）——常规回合末入口的第一家用户

`OrbWalker` 是 §2.20 那个新入口（常规、非 Late 的 `AfterSideTurnEnd`）的**第一家用户**，除此之外只用既有能力。

| 部位 | 源码（`ActsFromThePast.OrbWalker`） | 适配 |
| --- | --- | --- |
| 开场 | `AfterAddedToRoom` 挂 AFTP 自己的 `StrengthUpPower`，层数 `StrengthUpAmount`（A9+ 5／否则 3） | **不需要代码**：发生在根捕获之前，实机实例上已经带着它 |
| `StrengthUpPower` 的回合末 | `AfterSideTurnEnd`（**非 Late**）：`side == Owner.Side` 时给自己加 `Amount` 点力量 | `RegisterSideTurnEndPower("StrengthUpPower", …)`：判据逐字照抄，`combat.Apply<StrengthPower>(owner, power.Amount, owner)` |
| `MOVE_BRANCH` | 抽一次 `NextInt(100)`；`< 40` 时最近**连着两次**不是 `CLAW` 就 `CLAW`、否则 `LASER`；否则最近连着两次不是 `LASER` 就 `LASER`、否则 `CLAW` | `BeyondBranchResolvers.OrbWalker`（只读 rng 与行动历史，**已声明为纯读取**） |
| `LASER` | 攻击 + **两张 Burn 分两种入堆**：弃牌堆底部 1 张、抽牌堆随机位置 1 张 | `OrbWalkerLaser`：两次 `AddToCombat<Burn>`（`Discard`+`Bottom`、`Draw`+`Random`） |
| `CLAW` | `SingleAttackIntent(ClawDamage)` | 已在第二三幕常量表 |

**未验证**：没有在游戏内打过「球体行者」遭遇，`StrengthUpPower` 的每回合加力量与两张 Burn 的入堆位置
都**未实机验证**；也没有最小差分夹具。

---

## 3. 心脏（Act4Heart）适配：已落地

Act4Heart 是闭源 Mod（创意工坊 `3747537811`，`id=Act4Heart`、`version=1.1.7`、
`affects_gameplay=true`），已反编译（`_ref/Act4Heart/`）。战斗内容：

- 怪物：`CorruptHeart`（HP 750/800；Debilitate → Blood Shots ×15 / Echo 45 → Buff 轮换，
  `state` 字节驱动 `POST_ATTACK_BRANCH`）、`SpireShield`（HP 110/125）、`SpireSpear`（HP 160/180）。
- 能力：`BeatOfDeathPower`（出牌反伤）、`InvinciblePower`（每回合受伤封顶）、
  `MetallicizePowerA4h`（回合末早期获得格挡）、`RegeneratePowerA4h`（回合末回复）。

适配产物是两个独立 Mod：`CombatSolver-AFTP`（往昔之章）与 `CombatSolver-Heart`（心脏）。
两者都引用 `CombatSolver.dll`、走 publicizer、在 Mod 初始化时向求解器的登记表写入条目；
自检不通过则**一个条目都不登记**。

### 3.1 门禁（原「硬阻断」，已解决）

`Dolso.ModelHook : AbstractModel`（`ShouldReceiveCombatHooks => true`）是 ModHelper 订阅者，
会被 `PredictionModHookSubscriberCapture.ValidateSubscriber` 审。原型里两个具体子类：

| 订阅者 | 覆写的 hook | 是否落在 `PredictionInertHookNames` |
| --- | --- | --- |
| `Act4Heart.Keys.GreenKeyHooks` | `ModifyGeneratedMapLate`、`BeforeCombatStart`、`TryModifyRewards` | 前两个在内，**`TryModifyRewards` 不在** |
| `Act4Heart.Keys.RedKeyHooks` | `TryModifyRestSiteOptions` | 在 |

因此 `GreenKeyHooks` 会被判为「参与战斗」并让整局预测抛
`IncompatibleGameplayModException`。求解器为此提供了公开登记入口
`ThirdPartyAdapterRegistry.AllowCombatSubscriber(string/Type)`，心脏适配在初始化时按类型全名
精确放行这两个订阅者（逐一审过它们的全部覆写：地图生成、战斗开始、休息处选项、战后奖励，
没有一项落在战斗模拟期间）。

`HeartSubscribers.Verify()` 会枚举 `Dolso.ModelHook` 的全部具体子类并与审过的名单逐项比对：
Act4Heart 新增订阅者时自检失败、整个适配拒绝登记——求解器于是照常拒绝这条不可预测的路线，
而不是悄悄漏掉新订阅者的效果。

（`Act4Hooks` / `GeneralHooks` / `HeartHooks` 是 `static class`，用 Harmony `[HookBefore]`
直接打补丁在**本体**类型上（如 `Creature.ScaleHpForMultiplayer`、`DoomPower.DoomKill`），
不是 ModelHook 订阅者，因此不经过订阅者门禁；求解器的补丁审计只审卡牌 `OnPlay`，也不涉及它们。）

### 3.2 `state` 字节与 `POST_ATTACK_BRANCH`

三个怪物共用一个形状：起始行动 → 攻击 → `POST_ATTACK_BRANCH` → 随机分支。`POST_ATTACK_BRANCH`
是按一个**可变 `private byte state`** 判断的原版 `ConditionalBranchState`，而求解器在根捕获时
把条件分支的取值冻结成一份快照——捕获那一刻 `state` 多半是 0，之后每个分支推进它都不会改变
冻结值，直接用会从第一步就错。

做法：把 `state`（腐化心脏另加 `buff_counter`）登记为**怪物标量状态**
（`RegisterMonsterStateMembers`，随分支 Fork、进状态指纹与续用核对文本），再由
`RegisterMonsterBranchResolver` 按当前值解析。三条分支都**不抽 RNG**，解析函数一律不碰 `rng`。

| 怪物 | 初值 | 推进 | 解析 |
| --- | --- | --- | --- |
| CorruptHeart | 0 | `debilitate` 归零；`blood_shots` `\|=1`；`echo` `\|=2`；`buff` 归零 | `&1==0`→血弹；`&2==0`→回响；`&3==3`→强化 |
| SpireShield | 0 | `bash` `\|=1`；`fortify` `\|=2`；`smash` 归零 | `&1==0`→猛击；`&2==0`→加固；`&3==3`→猛砸 |
| SpireSpear | **2** | `burn_strike` `\|=1`；`piercer` `\|=2`；`skewer` 归零 | `&1==0`→灼烧打击；`&2==0`→穿刺；`&3==3`→串刺 |

三个条件都不命中时明确抛 `PredictionUnsupportedException`（对应实机的「找不到下一个状态」）。

### 3.3 行动效果（10 条，逐行对照）

`HeartMoveEffects` 登记了 10 个行动的非攻击部分：腐化心脏的削弱（Vulnerable/Weak/Frail **2/2/2**
＋ 5 张状态牌进**抽牌堆随机位**）、`state` 推进；盾兵的 `bash`（球位/几率分支）、`fortify`
（`GetTeammatesOf` 每人 30 格挡，`ValueProp.Move`）、`smash`（A9+ 固定 99 格挡，否则按本次砸击
**实际伤害**等量，`ValueProp.Unpowered`）；矛兵的 `burn_strike`（A9+ 进抽牌堆、否则进弃牌堆，
位置 Top）、`piercer`（每人 +2 力量）、`state` 推进。

砸击的「实际伤害」从预测历史尾部回找最近一条由该怪物发起的攻击记录求和；历史随分支 Fork，
读到的一定是当前分支的结果。

**盾兵 `bash` 的球位分支**是唯一需要照抄私有 RNG 的地方。源码条件是
`!target.IsPlayer || capacity <= 0 || !(Rng.NextFloat(1f) < odds)`：单人无球位时短路、
**一次 RNG 都不抽**；有球位才抽，且抽了就必须用掉。这里的 `Rng` 是
`MonsterModel.Rng`——**每只怪物各自一条**的流（由 `CombatState.CreateCreature` 按
「run 种子 + 坐标 + CombatId」播种），与 `RunRng.MonsterAi` 互不影响。求解器不模拟这条流，
因此适配层把实机实例上的当前状态整份搬进一个可 Fork 的分支状态
（`MonsterRngSupport` / `MonsterRngPredictionState`，**两套适配共用同一份实现**，见 §2.12），
在预测里照着源码的抽样顺序复刻，并把已抽次数写进一个自建的怪物标量成员
（`adapter_spire_shield_orb_rolls`）好让状态指纹看得见这条流的进度。

### 3.4 Power（3 个镜像 + 1 个已知缺口）

心脏进场（`AfterAddedToRoom`）就给自己挂 `BeatOfDeathPower` 与 `InvinciblePower`，
**从第一个回合起它们就在场**，所以两者都是硬需求。

| Power | 镜像点 | 说明 |
| --- | --- | --- |
| `BeatOfDeathPower` | `AfterCardPlayedMirrors.Register(Type,…)` | 对**出牌者**造成 `Amount` 点伤害（`ValueProp.Unpowered`）。源码不过滤目标，适配也不过滤 |
| `InvinciblePower` | `AfterDamageReceivedMirrors.Register(Type,…)` | 本回合累计未格挡伤害；达到 `Amount` 时把血量显示切成「无限带数字」 |
| `InvinciblePower` | `ModifyHpLostMirrors.RegisterAfterOstyLate(Type,…)` | 把血量损失截到 `Amount − 累计` |
| `InvinciblePower` | `PowerHiddenStateMirrors.Register/RegisterRootCapture(Type,…)` | 状态在 `_internalData` 里、**克隆会重置**，必须根捕获；累计值进状态指纹 |
| `InvinciblePower` | `ThirdPartyAdapterRegistry.RegisterTurnStartPower` | 自己那一方回合开始时清零并恢复血量显示 |
| `MetallicizePowerA4h` | `BeforeSideTurnEndMirrors.RegisterEarly(Type,…)` | 回合末早期给持有者 `Amount` 格挡（`ValueProp.Unpowered`） |

单人战斗里 `InvinciblePower` 的 `split` 为假，源码的下标器直接返回共享字段、**与 dealer 无关**，
所以是一个共享计数而非按人字典。多人分摊模式（`split`）下适配明确抛
`PredictionUnsupportedException`，不猜。

**已知缺口：`RegeneratePowerA4h`。** 它走 `AfterSideTurnEnd`，而求解器只镜像
`AfterSideTurnEnd**Late**`，没有 `AfterSideTurnEnd` 这个阶段，也没有给第三方补阶段的入口，
因此**无法建模**，适配刻意不登记。好在它只由绿钥匙精英使用，不在心脏战斗里出现。
名字记在 `HeartPowers.KnownGaps` 里，日志会跟着打出来。

`InvinciblePower` 的隐藏状态读取全程走反射：`Data` 是对方的私有嵌套类型
（`Act4Heart.Powers.InvinciblePower+Data`），适配层无法在编译期命名它，所以先按名字取回
`PowerModel.GetInternalData<T>()`、再用运行期解析出的 `Data` 特化后调用，最后读
`damage_recieved_this_turn_shared` 字段。这套路径（嵌套类型 + 两个字段 + 泛型方法）都在
`HeartPowers.Verify()` 里预先核对过。

### 3.5 清单与依赖

`CombatSolver-Heart.json` 依赖 `CombatSolver ≥ 0.43.2` 与 `Act4Heart ≥ 1.1.7`；
后者既用来限定版本，也保证 Act4Heart 的程序集先于适配本体加载
（适配在初始化时按程序集名查找，查不到就整体拒绝登记）。

`CombatSolver-AFTP.json` 依赖 `CombatSolver ≥ 0.43.2` 与 `ActsFromThePast ≥ 1.0.5`。
下限曾是 `1.0.6`，但实测安装的 `1.0.5` 与 `1.0.6` 在适配用到的全部内容上语义一致
（14 个怪物类型、7 个 `private const`（`SlimedCount`/`WeakTurns`/`FrailTurns`/`WeakAmount`/
`FrailAmount`/`ChargeLimit` 等）、配置类与补丁类齐全，Harmony owner 字符串相同），
门槛过严会让适配在可用的版本上**整个不加载**（`godot.log`：
`Tried to load mod CombatSolver-AFTP, but it depends on mods which have not been loaded`），
于是未适配的 `Slimed.OnPlay` 补丁照样把战斗拒掉。故下调为 `1.0.5`。

### 3.6 死亡钩子与攻击意图数值：两条「第三方可声明的事实」

2026-09-21 的问题包（`db6a045a…`，`CORRUPT_HEART_BOSS`）暴露了两个缺口：求解器的判定表里有两类
「已复核事实」，原生写死、第三方却登记不进去，于是心脏战斗既显示不出战损、又显示低可信度。
两条都在本批补上（登记入口见 [THIRD_PARTY_ADAPTERS.md](THIRD_PARTY_ADAPTERS.md) §2.13）。

#### 腐化心脏的 `AfterDeath`：只换背景音乐，登记为忽略

`Act4Heart.CorruptHeart.AfterDeath` 反编译后整段只有一个分支——死者是这颗心脏且未被移除拦截时，
取 `NRunMusicController.Instance`、调一次 `UpdateMusic()`，返回已完成的 `Task`。没有命令、数值、
RNG 或生命／格挡／能力读写。背景音乐不在预测范围内，所以这条重写对预测的确切影响是「无」。

它此前没被登记，落进 `MirrorDispatchKind.Unsupported` 并记一条
`COVERAGE source=CORRUPT_HEART method=AfterDeath reason=MethodNotMirrored compensated=False`。
代价远不止一行红字：

| 环节 | 代码 | 后果 |
|---|---|---|
| 未补偿 gap 里方法名带 «Death» | `CombatBeamSolver.StateEvaluation.HasUncompensatedDeathGap` | `uncertainVictory = true` |
| 该节点边界被改写 | 同上，`boundary = UnsupportedEffect` | 胜利不再被承认 |
| 该节点被排除出「战斗结束」 | `CombatBeamSolver.Terminal`：`IsCompleteVictory(…) && BoundaryReason != UnsupportedEffect` | `CombatEndedTurn` 保持 `null` |
| 界面取不到结束回合 | `SolverOverlaySnapshot`：`projectedBattleHpLossKnown = CombatEndedTurn.HasValue` | **「预计战损 未知」** |
| 可信度判定 | `SolverOverlaySnapshot.ConfidenceText`：任一未补偿 gap → 低可信度 | **「低可信度」** |

实证：那份问题包的路线行是 `final_enemy_hp=0 final_hp=37 combat_ended_turn=- boundary=UnsupportedEffect`
——敌人血已空、玩家活着，却既不判结束也不给战损。而 `boundary=UnsupportedEffect` 在**全仓库只有
`StateEvaluation` 那一处**会产生，其前置条件正是 `won == true`。所以「确实赢了、只是不认」是硬结论，
不是推测。

修法是把这条重写按精确类型登记为忽略（`HeartIgnoredHooks`），语义复用原生那批
`RegisterIgnored<T>()`（§1.2 的 `Ignored`：人工复核过、对预测无影响、静默）。自检确认该重写仍在、
签名逐参数相符；挡在中间的是清单里 `Act4Heart ≥ 1.1.7` 的版本门。

#### 攻击意图的「动态伤害」：闭包显示类的假象

意图预测器按「`DamageCalc` 是否绑定了实例」保守判断攻击是否动态，原版那批已复核的行动写死在
`IntentForecaster.IsKnownStableAttack` 里。第三方类型登记不进去，于是心脏每回合都多报一条
`approximation=CORRUPT_HEART.BLOOD_SHOTS_MOVE:动态伤害`。

但游戏本体的那两个构造函数其实是：

```csharp
public MultiAttackIntent(int damage, int repeat) { DamageCalc = () => damage; _repeat = repeat; }
public SingleAttackIntent(int damage)            { DamageCalc = () => damage; }
```

捕的是**构造实参**，段数是普通只读字段——`DamageCalc.Target != null` 只是编译器生成的闭包显示类，
并不是在读实例状态。所以这些行动的伤害与段数在意图构造时就已固定。

修法是新增 `ThirdPartyAdapterRegistry.RegisterStableAttack(怪物类型名, 行动 Id)`（判定与原版白名单
合流），由适配层声明三个怪物的六条攻击行动；`HeartMoveEffects.Verify()` 同时把源码里的
`blood_shots_damage=2`／`blood_shots_count=15`／`echo_damage=45`／`bash_damage=14`／`smash_damage=38`／
`burn_strike_damage=6`／`burn_strike_count=2`／`skewer_damage=10`／`skewer_count=4` 逐个钉死：
数值一变就整体拒绝登记（见 §3.3／§3.4 的纪律）。

#### 还剩一条近似，是刻意的

修完之后这场心脏战斗的可信度是**中等**而非「高」，因为 `POST_ATTACK_BRANCH:条件分支` 仍在近似清单里。
这不是心脏适配的缺陷：预测器只推进 AI 游标、**不推进**对方那个私有 `state` 字节，所以一回合以后的
行动取不到可信值，只能标近似。**原生怪同样如此**——原生女王／火炬头聚合体那一局
（`docs/performance/general-allocation-20260914.json`）同样带两条 `QUEEN.*_BRANCH:条件分支`、
同样 `modeled_damage_exact=False`，而它的 `combat_ended_turn=10` 说明这两件事互不影响。
要让带条件分支的战斗升到「高可信度」，需要让预测器能推进分支状态——那是求解器本体的改动。

---

## 4. 下一步（按优先级）

1. **运行期验证**：两个适配目前只过了编译与自检路径复核，尚**未在游戏里跑通**。
   需要一局能走到 Act 4 / 心脏，核对：心脏三轮循环的分支、死亡节拍的反伤、
   无敌封顶后的截断，以及盾兵球位分支的抽样次数。Act4Heart 已作为创意工坊条目安装，
   具备运行期验证条件。

   往昔之章一侧的验证前提见 §5.2：**两个依赖 Mod 必须处于启用状态**，否则适配整个不加载，
   未适配的补丁会把战斗拒掉。2026-09-20 的问题包（`CORRUPT_HEART_BOSS`，
   `IncompatibleGameplayModException: ... ClassicSlimedOnPlayPatch.Prefix ...`）就是这个组合：
   `ActsFromThePast` 在设置里被停用、且适配清单下限 `1.0.6` 高于实装的 `1.0.5`，
   于是适配没加载、审计照样拦下补丁。两条都已在 §3.5 与本节记录的处理中修掉；
   心脏适配本身在更早一局里 `won=True`，那次失败与它无关。
   之后 01:06 那份包暴露了第三个问题：适配**已加载**，但读标记用了 `SpireField.Get`，
   并行下抛 `ArgumentException` 把整次搜索打死（见 §2.6）。修法是把读路径换成纯读的
   `TryGetValue`。
   下一步要在游戏里复核的史莱姆场景有两处：① `LegacyEnemiesGiveClassicSlimed=True` 时，
   往昔之章战斗里的经典史莱姆出牌**不抽牌**、连打两张不会凭空多抽；② 心脏战斗里的 `Slimed`
   （本体语义，而配置同为 `True`）**照常抽 1 张、整场能解**——这正是 2026-09-21 修掉的误伤
   （见 §2.5 的反例）：配置只能决定**以后生成的**是哪一种，不能反过来判定一张已有的牌。
   ③ 一场带 `Slimed` 的战斗里**自动开算不再报 `SEARCH_FAILURE`**（§2.6 的回归）。
   ④ 心脏战斗应能给出**具体战损**（不再是「未知」），可信度为**中等**（不是「低」），
   近似清单里只剩 `POST_ATTACK_BRANCH:条件分支` 一条——完整判定链、实证与预期取值见 §3.6。
   ⑤ 往昔之章第一幕的战斗里，攻击行动**不再出现 `approximation=…:动态伤害`**（§2.7）；
   Hexaghost 的 `DIVIDER` 与虱子的 `BITE` 是刻意保留的两条。
   ⑥ 强盗（Looter）战斗能正常求解：三次 `MUG_BRANCH` 分支按 `_mugCount` 推进、
   `AFTER_SECOND_MUG` 二选一、`SMOKE_BOMB` 的 6 点格挡、`ESCAPE` 之后该个体不再排后续行动，
   且偷走的金币计入未追回账本（§2.8）。
2. **第一幕剩余分支**：还剩 2.2 表里的 9 个怪物。按「缺什么」分类，最省的是
   **GremlinShield**（分支只看队友数，缺的是 `PROTECT` 目标那一次 `MonsterModel.Rng` 抽样，
   可照 §3.3 盾兵球位那套私有 RNG 镜像做）与 **SlimeBoss / AcidSlimeLarge / SpikeSlimeLarge**
   （`SplitPower.AfterDamageReceived` → `SplitTriggered` + `ForceMonsterMove(splitState)` 都已具备，
   缺的是 `SPLIT` 里按当前血量生成两个小史莱姆，需要给第三方怪物开一条生成入口）。
   Hexaghost（`DIVIDER` 现算伤害 + `INFERNO` 升级全部 Burn）、Guardian（`ModeShiftPower` +
   `SharpHidePower` + 模式切换改写当前行动）、Lagavulin（`AsleepLagavulinPower` 走
   `AfterSideTurnEnd`，而求解器只镜像 `AfterSideTurnEndLate`）、SlaverRed（`EntangledPower`
   给手牌打病症、需要可打出性镜像）成本更高。
3. **行动效果补齐**：第一幕仍有（怪物, 行动）没有登记效果（当前表现为「预览可能不完整」的软缺口）。
4. **差分验证**：按 `combat-semantic-change` 技能要求，对每个新登记的 (怪物, 动作) 产出最小差分夹具，
   保证 `PredictionGaps` 为空且实机/模拟零差异；并同步 `PredictionCoverage.Baseline.cs`。
   适配层自身的回归（§2.8 的 Looter、§2.7 的形状判据）目前也还没有自动化夹具：
   `UnattendedTestRunner.AdaptedOnPlayIntegration.cs` 跑在隔离游戏进程里、接不了真实 AFTP 程序集。
5. **第二、三幕（范围已扩展）**：2026-09-21 需求方指示「先把所有怪都做好，然后再慢慢找 bug 修」，
   范围从「地基 + 第一幕 + 心脏」扩到**全部 62 个 `CustomMonsterModel`**（第一幕 25、第二幕 20、
   第三幕 17）。工作面与依赖见 §4.1；两幕此前一律走安全失败路径，按下面的队列逐批做，
   每一批都要过「分支 + 状态 + 行动效果 + 力量镜像 + 常量攻击」五项核对。

### 4.1 全部怪物的工作面

清单由反编译源码按类边界切文本生成（`_build/extract_aftp_inventory.ps1` /
`extract_aftp_powers.ps1`，输出在第一幕怪物 2508–7500 行、第二幕 9985–14300 行、
第三幕 35612–39200 行、能力 23639–24757 行）。按**所需能力**分组，从依赖最少到最多：

| 组 | 需要什么 | 第一幕 | 第二幕 | 第三幕 |
| --- | --- | --- | --- | --- |
| 0 | 只有常量攻击 + 无分支 | SpikeSlimeSmall、GremlinSneaky | ✔ Pointy（§2.14，零登记即完整）、✔ TorchHead（§2.18，同型） | ✔ SnakeDagger（§2.19，自身离场走 `RegisterOwnerRemovingMove`） |
| 1 | 分支只读自身标量／队友数 | ✔ GremlinShield（§2.12） | ✔ Centurion、GremlinLeader（同缺私有 RNG → 已有该能力）、✔ Mystic（§2.13）、✔ Mugger（§2.14）、✔ BookOfStabbing（§2.17，分支写自身计数 + 动态攻击值） | ✔ Repulsor、✔ Spiker（§2.20）、✔ OrbWalker（§2.21，常规回合末入口的第一家用户）、Exploder |
| 1b | 无分支但行动带效果 | — | ✔ Bear、✔ Taskmaster（§2.14）、✔ Chosen、✔ Champ（§2.16） | — |
| 2 | 第三方怪物生成（召唤／分裂／复活） | AcidSlimeLarge、SpikeSlimeLarge、SlimeBoss（SPLIT） | BronzeAutomaton、Collector、GremlinLeader、Byrd（复活？） | AwakenedOne（REBIRTH）、Darkling（REATTACH）、Reptomancer |
| 3 | 私有 `MonsterModel.Rng` 镜像（照 §3.3 盾兵球位那套） | ✔ GremlinShield（§2.12） | Centurion、GremlinLeader | WrithingMass（`Rng?`） |
| 4 | 新 Power 镜像（第三幕居多） | SplitPower、ModeShiftPower、SharpHidePower、AsleepLagavulinPower、EntangledPower | AngryPower✔、SporeCloudPower✔、PainfulStabsPower✔（**原版** Power，镜像早已在核心里，§2.17）、StasisPower、HexOriginalPower、MetallicizePower、PlatedArmorPower、MalleablePower、FlightPower | LifeLinkPower（含内部数据 + 5 个 Should*）、UnawakenedPower、ReactivePower、ShiftingPower、StrengthUpPower、RegenEnemyPower、CuriosityPower、TimeWarpPower、DrawReductionPower、ConstrictedPower、FadingPower |
| 5 | 强制改写当前行动 / 眩晕 | Guardian（`SetMoveImmediate` + `ModeShiftPower`；**攻击前钩子已就位**） | ShelledParasite（同类；攻击前钩子已就位） | AwakenedOne |
| 6 | 缺失的回合阶段 | Hexaghost（`AfterSideTurnEnd` 非 Late 的旧缺口见 §3.4） | JawWorm `HardMode` 的 `BeforeSideTurnStart`（§2.10） | — |
| 7 | 病症／可打出性镜像 | SlaverRed（`EntangledPower` + `EntangledOriginal` 病症） | — | — |
| 8 | 预测期卡牌操作 | Hexaghost（`INFERNO` 升级全部 Burn 再塞 3 张） | — | — |
| 9 | 非战斗内容（事件／遗物同名类，**不是怪物**） | — | — | TorchHead 之外的条目见 `_build/_aftp_model_hooks.txt` |

**已经补上的核心能力**：动态攻击值登记（`RegisterMonsterAttackValues`，见 §2.17；往昔之书是第一家，
第五幕的 `Maw.NOMNOMNOM_MULTI`、`Hexaghost.DIVIDER` 这类 `Dynamic*AttackIntent` 之后沿用同一条路）、
**第三方 Power 的常规（非 Late）`AfterSideTurnEnd` 登记**（`RegisterSideTurnEndPower`，派发在
`EndTurnPowerSupport.TriggerRegular` 的原版 `switch` 之后；解锁 `SnakePlant` 的 `MalleablePower`、
`OrbWalker` 的 `StrengthUpPower`、`Nemesis` 与 `Hexaghost`，见 [第三方适配](THIRD_PARTY_ADAPTERS.md) §2.13）。

**求解器本体仍要补的能力（按解锁怪物数排序）**：① 第三方怪物生成/召唤入口（组 2，8 个怪物）；
② 私有 `MonsterModel.Rng` 的通用镜像入口（组 3，4 个）；③ `BeforeSideTurnStart` 分发点（**非 Late 的
`AfterSideTurnEnd` 已经在 2026-09-21 补上**，见上）；④ 手牌病症与可打出性镜像（组 7）；
⑤ 「强制改写当前行动 + 眩晕」的第三方入口（组 5，Guardian/Lagavulin/ShelledParasite/AwakenedOne）。
每补一项都要按 `combat-semantic-change` 的纪律给出最小差分夹具，并在
[第三方适配](THIRD_PARTY_ADAPTERS.md) §2.13／§6 登记。

#### ⚠️ 未适配的怪物不等于「被拒绝」

第二、三幕里**没有**自定义分支状态的怪物（`Bear`、`Pointy`、`SphericGuardian`、`Taskmaster`、
`TorchHead`、`Deca`、`Donu`、`SnakeDagger`、`Transient` …）今天就已经能算出路线——搜索**不会**
因为某个行动没登记效果就拒绝它：`MonsterMoveEffects.Apply` 走的是「命中登记表就用，否则继续往下
按原版 switch 匹配，再不然什么都不做」。所以这些战斗的**模拟会静默漏掉没登记的效果**
（例如 `Taskmaster.SCOURING_WHIP` 的 Wound、`SphericGuardian.ACTIVATE` 的格挡），
界面只靠 `IntentForecaster` 的「未支持意图」红字和「低可信度」提示这件事。

**因此「能出路线」不能当成「适配好了」。** 逐个怪物的验收标准仍然是 §4.1 那五项
（分支 / 状态 / 行动效果 / 力量镜像 / 常量攻击）都核对完，而不是「求解器没报错」。

### 4.2 第二幕剩余八只的确切缺口（侦察结论，2026-09-21）

逐类反编译读完之后，第二幕剩下这八只**各自卡在什么地方**已经明确（第一幕的 `SlaverRed`／
`Hexaghost`／`Guardian`／`Lagavulin`、第三幕 17 只同理待办）。按「要先补哪个本体能力」排列：

| 怪物 | 需要的本体能力 | 该怪自己要写的部分 |
| --- | --- | --- |
| `BronzeAutomaton` | 无 | `MOVE_BRANCH`（写 `_numTurns`）+ `SPAWN_ORBS`（按 `orb` 前缀槽位生成 `BronzeOrb`，`minion: true`）+ `BOOST`（`GainBlock(BlockAmount, Move)` + `StrAmount` 力量）；`BeforeDeath` 只有震屏与「杀掉存活队友」——后者是**原版规则**（`CreatureCmd.KillWithoutCheckingWinCondition`：主敌死亡时杀掉存活的 secondary 队友），核心已镜像，因此这条登记为忽略 |
| `BronzeOrb` | 无（但工作量大） | `MOVE_BRANCH`（写 `_usedStasis`）+ `SUPPORT_BEAM`（给存活的正牌自动机 12 格挡）+ `STASIS`（洗牌抽/弃牌堆、偷最高稀有度的牌、`StasisPower.Capture`）＋ `StasisPower` 镜像（私有字段存被偷的牌，死亡时归还）——**被偷牌是对象引用**，`PowerHiddenStateMirrors` 只存标量，需要一个能随 Fork 重映射的预测状态 |
| `GremlinLeader` | 「随从随首领死亡而逃跑」：小鬼的 `AfterAddedToRoom` 订阅了 `GremlinLeaderHelper` 的 C# 事件 | `MOVE_BRANCH`＋`NumAliveGremlins`＋`RALLY`（按槽位召唤随机小鬼）＋`ENCOURAGE`（给队友格挡与力量）＋`STAB` |
| `Collector` | 自身复活（`REVIVE`）＋随从 | `_turnsTaken`／`_ultUsed`／`_initialSpawn`＋`SPAWN`＋`MEGA_DEBUFF`＋`BUFF`＋`REVIVE`＋`BeforeDeath` |
| `ShelledParasite` | 「强制改写当前行动 + 眩晕」的第三方入口（`SetMoveImmediate` 系列） | 分支（带 `min` 参数的重载）＋`FELL`／`DOUBLE_STRIKE`／`LIFE_SUCK`／`STUNNED`＋`BeforeDeath` |
| `SnakePlant` | **`AfterSideTurnEnd`（非 Late）分发点** | `MOVE_BRANCH`（只读，可声明纯读取）＋`SPORES`（2 破甲 + 2 虚弱）＋`MalleablePower` 镜像（`ModifyDamage`／`AfterDamageReceived` 累加＋`AfterAttack` 给格挡＋回合末清零与回滚层数，私有 `_pendingBlock` 要进指纹） |
| `Byrd` | `BeforeSideTurnStart`（非 Late）分发点＋第三方 `ModifyDamageMultiplicative` 入口＋`AfterRemoved`（Power 被移除）入口 | `FIRST_MOVE_BRANCH`／`FLYING_BRANCH`（都只读，可声明纯读取）＋`CAW`（1 力量）＋`GO_AIRBORNE`（按玩家人数施加 `FlightPower`）＋`FlightPower` 镜像（飞行时受到的 `Move` 伤害 ×0.5、按未格挡命中数减层、层数在回合开始回滚、归零被移除时把 Byrd 打落到 `HEADBUTT` 眩晕） |

结论：**第二幕剩下这八只里没有一只是「零登记」或纯登记活的**，`BronzeAutomaton` 与 `BronzeOrb` 是同一场
遭遇、必须一起做；`TorchHead`（§2.18）已经完整。这个顺序也说明为什么先把「动态攻击值」
（§2.17）这类**不依赖新阶段**的通用能力补齐更划算。

### 4.3 第三幕 17 只的初判（读完源码的与只看了成员清单的分开记）

第三幕已经从「成员清单 + 意图形状」过了一遍，并完整读了 `Repulsor`、`SnakeDagger`（§2.19，已适配）、
`Exploder`、`Transient`、`Deca`。结论分三档：

| 档 | 怪物 | 依据 |
| --- | --- | --- |
| **已适配** | `Repulsor`、`SnakeDagger`（§2.19）、`Spiker`（§2.20）、`OrbWalker`（§2.21） | 完整读过 |
| 只缺「已有能力」的登记活 | `SpireGrowth`（分支 + `QUICK_TACKLE`/`CONSTRICT`/`SMASH`，`ConstrictPower` 是原版）、`Reptomancer`（分支带重掷 + `SPAWN_DAGGER` 用已有的按类型生成入口 + 两个攻击） | 成员清单与常量攻击表；**尚未逐行读完**，动手前要按 §3.3 复核 |
| 卡在本体能力上 | `Exploder`：源码的 `EXPLODE` 用 **`CreatureCmd.Damage`（直伤）**而不是 `DamageCmd.Attack`，而它的 `DeathBlowIntent` 是攻击意图、会被通用攻击循环当攻击结算——要精确复刻就得有「这条第三方行动的意图伤害不由通用攻击循环结算」的入口；`Transient`：`ShiftingPower` 要给自己的 `TemporaryStrengthPower` 子类施加负力量（需要按 `Type` 施加临时力量的入口；`FadingPower` 用的 `BeforeSideTurnEndEarly` 与动态攻击值都已就绪）；`Deca`/`Donu`：AFTP 自己的 `PlatedArmorPower` 要 `BeforeSideTurnStart`（第 1 回合给格挡）；`Maw`/`GiantHead`：`NOMNOMNOM_MULTI`/`IT_IS_TIME` 是 `Dynamic*AttackIntent`（动态攻击值已就绪，但两只都还要 `BeforeDeath` 与分支）；`Nemesis`：自身 `AfterSideTurnEnd` 重写（入口已就绪）+ 无实体化；`WrithingMass`：分支用私有 RNG 抽行动；`Darkling`/`AwakenedOne`：复活/重生（`DEAD_MOVE`/`REATTACH_MOVE`/`REBIRTH` 与内部数据）；`TimeEater`：`TimeWarpPower` 的回合计数与 `HASTE` | 完整读过 `Exploder`／`Transient`／`Deca`／`OrbWalker`；其余为成员清单初判 |

---

## 5. 本地编译

**用标准命令构建，不要覆盖构建目标。**

```powershell
# 求解器本体（含 net48 的内存释放辅助程序，会一起拷进 mods\CombatSolver）
& <dotnet9>\dotnet.exe build CombatSolver\CombatSolver.csproj -c Release -p:CopyModOnBuild=true

# 适配 Mod（成功后自动拷进 mods\<id>）
& <dotnet9>\dotnet.exe build CombatSolver\adapters\CombatSolver.Act4HeartAdapter\CombatSolver.Act4HeartAdapter.csproj -c Release
& <dotnet9>\dotnet.exe build CombatSolver\adapters\CombatSolver.ActsFromThePastAdapter\CombatSolver.ActsFromThePastAdapter.csproj -c Release
```

最近一次结果：三处均 `Build succeeded. 0 Warning(s) 0 Error(s)`，并已部署到
`mods/CombatSolver`、`mods/CombatSolver-AFTP`、`mods/CombatSolver-Heart`。

### 5.1 曾经的 `_build/dev.targets` 变通（已退役）

`tools/CombatSolver.MemoryCleaner` 是 **net48** 的独立 exe，求解器的「深度释放系统内存」
就是在 `mods/CombatSolver/` 里启动它（`SystemMemoryReleaseService`，带 UAC 提权）。
它引用了 `Microsoft.NETFramework.ReferenceAssemblies.net48`，**不需要** .NET Framework 4.8
开发包就能编译。

早期这台机器上它没编译出 exe，外层 `CopyMod` 拷不到就报 `MSB3030` 让整次构建失败。当时的
变通是 `_build/dev.targets`：把 `BuildMemoryCleaner` 置空、并把 `CopyMod` 重写成
「只拷 dll 与清单」——**顺手删掉了拷 helper 的那一行**。于是构建「成功」了，但部署出来的
模组目录缺 `CombatSolver.MemoryCleaner.exe`，游戏里点内存释放必然
`FileNotFoundException`。教训：**绕过一个失败步骤时，别把被它保护的功能一起绕掉**；
`Copy` 只增不删，所以缺失不会自愈。

现在该文件内容已清空，保留只为兼容历史命令行里的 `-p:DirectoryBuildTargetsPath`。
本地安装目录应与 [README 的安装清单](../../README.md) 一致：
`CombatSolver.dll`、`CombatSolver.json`、`CombatSolver.MemoryCleaner.exe`、
`LICENSE`、`THIRD_PARTY_NOTICES.md`。

### 5.2 依赖 Mod 的运行期前提

适配 Mod 的清单把依赖钉成硬依赖，因此**依赖 Mod 被禁用或版本不足时，适配整体不加载**：

- `CombatSolver-AFTP` 需要 `ActsFromThePast ≥ 1.0.5` 与 `CombatSolver ≥ 0.43.2`；
  创意工坊 `3746969593` 当前就是 `1.0.5`（与 `1.0.6` 已做过符号差分，语义一致）。
- `CombatSolver-Heart` 需要 `Act4Heart ≥ 1.1.7` 与 `CombatSolver ≥ 0.43.2`
  （创意工坊 `3747537811` 为 `1.1.7`，满足）。

还要分清**三条**互不相同的失败日志——2026-09-20/21 的两次问题包正是各踩了一条：

| 现象 | 日志 | 含义 |
| --- | --- | --- |
| 适配 Mod 自己被停用 | `Skipping loading mod CombatSolver-AFTP, it is set to disabled in settings` | **装了还要在设置里启用**；依赖装了也没用 |
| 依赖 Mod 被停用 | `Tried to load mod CombatSolver-AFTP, but it depends on mods which have not been loaded: ActsFromThePast!` | 去启用依赖 |
| 依赖版本不足 | 同上（清单下限未满足） | 更新依赖，或核对该下限是否确实必要 |

开关的持久化位置：`%APPDATA%\SlayTheSpire2\steam\<steamid>\settings.save` 的
`mod_settings.mod_list[]`（每项 `id` / `is_enabled` / `source`）。同一 Mod 同时装了创意工坊
与本地两份时，加载器保留本地那份、把创意工坊那份标成 `DisabledDuplicate`
（日志：`Disabling the Steam workshop version.`），所以本地开发构建优先。

**适配没加载的后果与诊断**：适配 Mod 是独立 Mod，它要登记的那些合成语义只在自己加载时才存在。
它没加载 ⇒ 被打补丁的镜像 OnPlay 照常被拒 ⇒ 整场战斗 `DEPLOY_FAILURE`，而报错只说
「不兼容的第三方 Mod」，看不出真正原因是**适配没加载**，玩家于是去卸载那个无辜的玩法 Mod。
现在 `PredictionModPatchAudit.DescribeUnloadedAdapter` 会按 Mod 清单的 `dependencies` 找出
「**同时**依赖求解器与被拒 Mod、但本次没有加载」的那一个（要求同时依赖求解器，是为了不把
「碰巧依赖同一个玩法 Mod 的其它停用 Mod」误报成适配），把名字、id 与加载状态带进异常、日志和
界面提示，文案也改口为「去启用适配 Mod」而不是「卸载该 Mod」。
界面侧只改了一处 `SolverController.FormatIncompatibleModFailure`（搜索失败／部署失败／
回合准备失败三条路径都汇到它）。
