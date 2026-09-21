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

### 2.1 已支持分支选择的怪物（8）

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

以上分支逻辑逐字对照 AFTP 源码（`SelectNextMove` / `SelectAfterCharge` / `SelectAfterMug`）复核，
行动 Id 常量与分支节点 Id 均已核对字面值。

### 2.2 走安全失败路径的怪物（9）

下列怪物的分支**依赖尚未建模的状态**，因此刻意不登记，保持抛
`PredictionUnsupportedException`（装一半比不装更糟）：

| 怪物 | 分支 Id | 还缺什么 |
| --- | --- | --- |
| AcidSlimeLarge / SpikeSlimeLarge / SlimeBoss | MOVE_BRANCH | `SplitTriggered`（≤50% HP 触发，需要伤害钩子镜像）+ 行动被强制改写成 `SPLIT` |
| SlaverRed | MOVE_BRANCH | `_usedEntangle`；更要紧的是 `EntangledPower`（给玩家手牌打 `EntangledOriginal` 病症，禁止打出攻击牌），求解器没有对应的可打出性镜像 |
| Hexaghost | MOVE_BRANCH | `_orbActiveCount`（可建模）＋ `DIVIDER` 的伤害由 `ACTIVATE` 按玩家血量现算（`DynamicMultiAttackIntent`），以及 `INFERNO` 的「升级全部 Burn + 再塞 3 张升级 Burn」，后者涉及预测期卡牌升级 |
| Guardian | OFFENSIVE_BRANCH | `_isOpen` / `CloseUpTriggered` / `_pendingModeShift` + `ModeShiftPower`（受伤累计到阈值触发）、`SharpHidePower`，以及模式切换时的 `SetMoveImmediate(_closeUpState)` |
| Lagavulin | MAIN_BRANCH | `IsAwake` / `StartsAwake` / `DebuffTurnCount` + `AsleepLagavulinPower`；`WakeUpFromDamage` 走 `CreatureCmd.Stun(…, "ATTACK")` |
| GremlinShield | MOVE_BRANCH | 分支本身只看**队友数**（`GetTeammatesOf(...).Count > 1`，已可建模）；缺的是 `PROTECT` 的格挡目标由**怪物自己的 Rng**（`MonsterModel.Rng`）抽取，求解器不模拟这条流（与心脏盾兵球位同型，需要 §3.3 那套私有 RNG 镜像） |

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
把整场战斗的可信度压到「中等」。适配新增 `ExordiumStableAttacks`，按
`ThirdPartyAdapterRegistry.RegisterStableAttack` 登记第一幕 **35 条** (怪物, 行动)。

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
（`ShieldOrbRngPredictionState`），在预测里照着源码的抽样顺序复刻，并把已抽次数写进
一个自建的怪物标量成员（`adapter_spire_shield_orb_rolls`）好让状态指纹看得见这条流的进度。

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
5. **往昔之章第二、三幕（未做）**：本批约定范围是「地基 + 第一幕 + 心脏」。`Mugger`、`BookOfStabbing`
   等第二幕与第三幕怪物一律走安全失败路径；要扩展时按同一套纪律逐个来（分支、状态、行动效果、
   常量攻击四条一起核对），不要一次全猜。

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
