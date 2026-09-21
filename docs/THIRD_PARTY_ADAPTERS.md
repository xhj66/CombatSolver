# 第三方 Mod 适配手册

写给想让战斗路线求解器看懂自家 Mod 的作者。

求解器不认识任何第三方内容。它靠一套**镜像**（mirror）在自己的模拟里重现游戏行为，而镜像是
按类型登记的。你的牌、Power、遗物、药水没有登记，求解器就只能退化处理，路线会算错。

这份文档讲：默认会发生什么、有哪些登记点、登记的纪律、怎么验证自己做对了。

HeavenlyDrill 的 OnPlay 使用精确镜像，先解析分支 X 值及修正，再按卡牌 Energy 阈值决定攻击次数翻倍。修改该卡的阈值或攻击流程需提供对应语义登记，通用“X次攻击”推断不足以表达条件翻倍。EndOfDays 的领域补偿在每次施加灾厄后结算能力数量变化监听器和死亡，再判断处决；登记类似监听器时应保留原版 await 时点。

战利品、Adrenaline、Offering、Neurosurge 的完整 OnPlay 由 `CardDrawCardMirrors` 在共享注册表登记，按原版命令顺序处理铸造、扣血、返能、抽牌与能力施加。适配其效果时保留抽牌前后的边界：抽牌可以触发虚空失能量、自动出牌及满手限制。对应的 `CardEffectSpecRegistry` 后置补偿已经移除，第三方应在同一权威镜像内描述有序结算。

## 0. 先判断你要不要读下去

内置遗物目标新增 MeatOnTheBone 半血目标与 1～3 优先级，仍属于 RelicCounterCatalog 的封闭表。CardEnchantmentId 是路线显示元数据，当前额外展示原版 Inky；不代表未知附魔已获得战斗模拟支持。

| 你的 Mod | 要做什么 |
|---|---|
| 清单里 `affects_gameplay: false`（纯美术、UI、音效） | **什么都不用做**，自动放行 |
| 只改地图、进幕、事件、休息处、商店这类战斗外内容 | **什么都不用做**，求解器会判定它对战斗惰性 |
| 加了牌、Power、遗物、药水、敌人，或改了战斗数值 | 往下读 |

前两条是自动的。第三条不做适配的话，装上你的 Mod 之后求解器会直接停在
「检测到不兼容的第三方 Mod」，玩家用不了。

项目明确拒绝的玩法 Mod 优先于上述通用放行条件。当前 `WheelchairSpire`、`PengoTarot`、`BetterCharacterRelics` 按 Mod ID 或已加载程序集名识别，在根捕获时直接报告名称和不兼容原因；不依据 `affects_gameplay: false` 放行。本批不为这些 Mod 提供战斗适配。搜索、部署和回合准备捕获此异常时均使用专用提示，报告账本只记录不兼容类别，不引导玩家上传日志。包内仅出现其他 Mod 的名字或恢复环境不匹配，均不足以认定该 Mod 是某个偏差的原因。

Power 的原版克隆会重置 `_internalData`。跨根保留的数据必须从原生来源捕获：例如本批苍蓝星球的已触发标记，以及 DarkEmbrace 的虚无消耗延迟计数。DarkEmbrace 后续按实际事件累计并在回合末清零，不能用结束回合前的牌数代替此状态。

## 1. 求解器默认怎么对待未知内容

计算型动态变量必须有分支规则。第三方卡牌进入 `CalculatedVar` 求值且没有 `CalculatedVarSpecRegistry` 支持时，按卡牌所属 Mod 报不兼容，日志包含卡牌 ID；界面和报告账本不引导玩家上传。不能回退调用会读取 live 状态的原生计算器。20260911 的 `LIFEMASTERMOD-TENTACLES` 属于该情况，本次没有为该 Mod 提供适配。

Power 来源也是语义的一部分：精确镜像可通过 `ICombatPredictionEffectSink.ApplyPowerFromSource` 显式提供 `CardModel? cardSource`，原版传 null 时必须保持 null，避免能力附带效果被误判成外层卡牌直接效果。普通 `ApplyPower` 仍沿用当前卡牌作用域；两者不能按调用栈有无卡牌随意替代。

普通能力的 `Owner` 与可空 `Target` 不可混用：无显式目标的施加保持 Target=null，定向施加入口保留真实目标。临时力量族的回调使用经过修正的请求偏移，封顶后的净增量不能替代；其类型检查不扩大第三方能力支持面。内置 Weak/Vulnerable/Frail 的首 tick 标记进入精确状态比较，第三方持续能力仍须登记自己的状态与结算，不自动按这三个类型处理。

受伤唤醒在 `AfterDamageReceivedMirrors` 中立即结算：内置 AsleepPower 和 SlumberPower 对卡牌、遗物及回合效果共享同一 Hook。第三方伤害入口应调用模拟器 Damage，使受伤监听器随该次伤害执行；外层历史扫描不再承担这两个 Power 的唤醒。

原版 `PowerInstanceType.Instanced` 的通用/定向施加每次产生独立分支实例；`GetPower<T>` 与原版一致返回当前第一个实例，逐实例数量更新保持原引用。该行为不替代第三方 BeforeApplied/AfterApplied、内部状态及 Hook 的登记；InstancedPerApplier 的跨来源语义不在本项扩展内。

CrabRagePower 的同伴死亡结算由 `AfterDeathMirrors` 独占：力量、格挡与移除都发生在死亡 Hook 内，后续多段伤害立即消费新格挡。外层死亡清扫不重复该效果。

### 1.1 门禁：先让 Mod 进得来

求解器扫描所有 ModHelper 战斗 hook 订阅者。放行有三条路：

1. 清单 `affects_gameplay: false`；
2. `PredictionModHookSubscriberInertness.IsCombatInert` 判定为战斗惰性——只重写了战斗外的
   hook，或者只重写了战斗开始 / 战斗结束 hook（前者的效果已经落在被捕获的根状态里，后者在
   胜负判定之后才分发，求解器搜到战斗结束就停）；
3. 在 `PredictionModHookSubscriberCapture.KnownPreRootSubscriberTypeNames` 白名单里。

三条都不满足就抛 `IncompatibleGameplayModException`，整个求解器停摆。

第 3 条现在有公开登记入口（第三方适配 Mod 在初始化时调用，需 publicizer）：

```csharp
ThirdPartyAdapterRegistry.AllowCombatSubscriber("SomeMod.SomeHookType"); // 或 Type 重载
```

放行是**按类型全名精确匹配**的。放行某个类型等于向玩家承诺「这个订阅者不会改变战斗模拟」，
所以要先把它在 `AbstractModel` 上覆写的 **全部** hook 列出来逐一评估，并用自检钉死名单——
对方新增订阅者时自检失败、整个适配拒绝登记，比悄悄漏掉一个新订阅者好。

Act4Heart 就是这条路的实例：它的 `Dolso.ModelHook` 有两个具体子类，其中
`Act4Heart.Keys.GreenKeyHooks` 覆写了 `TryModifyRewards`（不在惰性清单里），
不放行就会让**装了它的任何一局都无法预测**。

### 1.2 镜像：进来之后每个类型的五种下场

每个被镜像的虚方法都有一张按**精确运行时类型**索引的注册表。查一个类型会得到五种结果之一
（`MirrorDispatchKind`）：

| 结果 | 含义 | 后果 |
|---|---|---|
| `NotOverridden` | 这个类型没有重写该方法 | 走基类行为，**正确**，不用管 |
| `Handled` | 有登记的镜像 | **正确**，这是你要达到的状态 |
| `Inferred` | 没登记，但结构上能推断出一个尽力而为的实现 | 可能对，求解器**记一条风险** |
| `Ignored` | 人工复核过，确认对预测无影响 | 正确，静默 |
| `Unsupported` | 有重写但没有安全的预测实现 | 求解器**记一条风险** |

**风险不是静默错误。** 求解器会在路线上打红字标明「这里有未镜像的效果」，玩家看得见，日志里
也有 `COVERAGE source=... method=... reason=...`。但红字**只是显示**——它不会把不可能的续接从
搜索里去掉。凡是会改变「接下来还能做什么」的效果（强制结束回合、额外回合、让某张牌打不出），
必须真的建模，只记风险不够。

### 1.3 只读 hook 会自动回落

`Modify*` / `Should*` 这类只读 hook 中，允许原实现回落的入口会调用 Mod 自己的实现。
适配者仍须核对读取的数据属于当前预测分支；只读方法也可能读到 live 手牌、Power 或费用。
姿态伤害倍率、费用修改等效果只有在完整分支差分通过后，才能认定原实现回落适用。

`CardIsPlayableMirrors` 使用独立的镜像分派：继承基类的牌返回 `true`；有显式登记的重写执行
对应镜像；未登记的重写记录 `MethodNotMirrored`，并返回调用方提供的 `true`。这条覆盖提示
用于暴露缺失语义，适配者必须登记真实可打出条件，才能保证搜索按预测手牌判断合法性。

## 2. 登记点总表

### 2.1 统一形状的镜像注册表（46 张）

绝大多数登记走同一个形状：

```csharp
XxxMirrors.Registry.Register<TYourType>(handler);
```

46 张注册表按域分布在 `src/Engine/InCombat/Mirrors/` 下：

死亡后生成单位的镜像应保持原生生成时点。例如补货由 `AfterDeathMirrors` 调用分支生成入口，旧个体仍在阵容中，其最大生命参与替补生命判重。把生成延后到阵容清理后，即使 RNG 调用次数相同也会改变抽样结果；登记镜像时应同步移除原领域补偿中的同一生成动作。

逐次出牌完成效果也应由 `AfterCardPlayedMirrors` 的对应分派独占。温柔在该 Hook 更新计数并扣除属性，回合末仍使用既有领域计数恢复；父牌的历史扫描可能包含已经结算的内层自动牌，不能再通过该范围给内层牌重复施加效果。属性施加需遵守每次原生命令的战斗结束条件。

历史敏感计算变量需要冻结根历史并加上分支新增事件。谋杀的实现读取 `RootCombatHistorySnapshot.CardsDrawn` 与模拟器抽牌事件；原生完成初始抽牌或后续动作后，旧预测根的倍率仍保持不变。只在实机停住时做一次差分会漏掉这类问题，验证时应包含根捕获后的实机推进与 Fork 隔离。

| 目录 | 注册表数 | 覆盖什么 | 你多半要用的 |
|---|---|---|---|
| `Hooks/` | 39 | 战斗 hook：攻击、格挡、伤害、死亡、卡牌、球体、回合边界 | 按你重写了哪个 hook 挑，例如 `AfterDamageGivenMirrors` |
| `Cards/` | 4 | 出牌、可打出性、回合结束留手、结算落点 | `CardOnPlayMirrors`、`CardIsPlayableMirrors` |
| `Potions/` | 1 | 药水使用 | `PotionOnUseMirrors` |
| `Enchantments/`、`Afflictions/` | 各 1 | 附魔与病症的出牌效果 | 少见 |

**回合开始重置能量之后的能力结算走 `Hooks/Resources/AfterEnergyResetMirrors`。** 这一张是从
`PersistentPowerSupport` 里那个写死五个原版类型的 switch 改过来的，所以以前第三方能力在这个
时点既没有登记入口，漏了也不报——别的钩子漏登记会记一条 `MethodNotMirrored` 风险，那个 switch
不经过注册表，只是静默跳过。重写了 `AfterEnergyReset` 的能力（每回合少一点能量、多一点能量、
多抽一张这一类）现在必须在这里登记。层数为零的能力不分发，和原版每个重写第一件事都是空转一致。

**注意目录里的文件数比注册表多。** `Cards/` 下有十几个 `*Mirrors.cs`，但注册表只有 4 张——
`BespokeCardMirrors`、`CardGenerationCardMirrors` 这些是**处理器文件**，它们往
`CardOnPlayMirrors.Registry` 这张共享注册表里登记，自己不持有注册表。找登记入口时认
`static readonly Registry Registry` 这个字段，不要认文件名。

**怎么知道自己要登记哪几个。** 把你的每个类型对基类虚方法的重写列出来，和这 46 张表逐一对照。
只重写了求解器不分发的方法，不用登记；重写了它分发的方法，就要登记。这一步不要靠印象，
要交叉核对——漏一个的表现是「效果看起来正常但其实没发生」。

同一张表里 `Register` 用的是 `Dictionary.Add`，**重复登记会抛异常**，不会静默覆盖。

Hook 分发会省略当前原版类型继承的默认空回调，但保留第三方/动态类型的完整回调顺序和既有登记流程。原生与领域监听表仍保留全部成员；关键字查询仅在所有接收者均未参与 `TryModifyKeywordsInCombat` 时省去原生空调用。每次根捕获重新检查相关 `AbstractModel` 基方法和 `Hook.ModifyKeywordsInCombat` 的 Harmony 补丁，有补丁或不透明 BaseLib CardModifier 时旁路。类型布局在同一根的有界表中复用，完整类型顺序逐项相等才命中，只存元数据、不保留任何分支 Model。原生监听表可在内部按前段与卡牌/球后段拼接，但顺序不变；不透明 CardModifier 仍完整重建，附着监听追加器拿到完整列表，不能把新增 Power 的插入位置限定在原生前段。该优化没有增加原本不支持的补丁或 subscriber 适配。

`PowerModel.GetTypeForAmount` 的局部 IL 优化只移除两处同类型枚举比较的装箱。虚拟 `StackType`、`Type`、`AllowNegative` getter 的次数与顺序及 decimal 分支保持原样；方法体不符合精确指令形状或比较内部存在控制流入口时保留原 IL。这没有增加 Power 登记点，也不缓存第三方 getter 的结果。

### 2.2 战略估值：会改变出牌顺序的 Power

`FirstAttackDamage` 是三层首领特化中填充的首张攻击潜力，普通政策为0，使用范围及限制见下方专文；登记签名与优先级保持。

```csharp
StrategicEffectMirrors.Register<TYourPower>(requirements, evaluate, host);
```

只有当你的 Power **收益取决于它和别的动作的先后关系**时才需要。详见
[第三方 Power 的战略估值登记](third-party-strategic-effects.md)。

外部战略登记表非空时，搜索仍按旧规则填充首领特化的 `FirstAttackDamage`，即使登记声明 `StrategicEffectRequirements.None`。仅原版且没有致命消费者时省略扫描；不要求已有外部登记新增需求标志，普通政策字段仍为0。

`StrategicEffectRequirements.AttackHits` 可请求可达攻击命中数；`StrategicEffectContext.AttackHits` 在请求后提供估值，未请求时为 null。它包括已审查的原版多段与小刀生成，第三方攻击使用普通单次命中估计，不能当作真实攻击结算。`ExhaustDrawPlays` 是黑暗之拥在禁抽、虚无顺序下的抽牌机会估值；这些字段只服务保路，不改变 Hook 镜像语义。

三层指定首领的内置联动估值额外填充 `Act3BossInteractions`、`ReachableCards` 及虚无抽牌、高费出牌、未来能量/抽牌的估计值。专用计数只在对应原版 Power 实际参与该分支时计算，第三方登记不能把默认 0 当作完整可达性分析；登记表仍优先于内置 Power 分支。见下方封闭入口清单。

不登记的后果：求解器按叠加层数记一点 `ScalingPotential` 兜底。对大多数 Power 够用；对
「自己不给甲、但让后续攻击给甲」这类会被排到错误位置。

### 2.3 药水的玩家选择

```csharp
PotionChoiceMirrors.Register<TYourPotion>(spec, apply);
```

只有当你的药水会让玩家当场做选择时才需要。不登记的后果很硬：`PotionChoiceSupport.RequiresChoice`
对第三方类型恒为 `false`，于是求解器**根本不为它开搜索分支**——它会把这瓶药当成一个没有收益的
动作，随手插在路线里的某个位置。药水自己的 `PotionOnUseMirrors` 镜像补不了这个：等那个钩子触发
的时候，「要不要开分支」早就已经被否决了。

两个委托：

- `spec(simulator, potion)` 返回一个 `CardChoiceSpec`：候选、上下界、效果。候选**必须是玩家在
  原生页面上真正看到的那几张，顺序也要一致**，否则部署时按卡牌令牌在页面上定位会错位。
  下界给 0 表示「可以一张都不选」。
- `apply(simulator, potion, choice)` 按选中的结果在模拟里施加效果，返回是否已经结算完
  （还有嵌套选择挂起时返回 `false`，和原版同一口径）。

效果用 `PlanChoiceEffect.ModDefined`。部署侧按卡牌令牌在原生页面上定位，本来就与效果无关；
这个值只是明确表示「结算由登记方负责」，别的效果分支不会误接手。求解器自己从不产生这个值。

登记之后，你的药水和原版带选择的药水走同一条通道：搜索按你的 spec 展开分支、把选中的结果记进
计划、部署时照常应答原生页面，而效果由你的 `apply` 施加——求解器不需要认识任何第三方效果。

**一个真实例子。** 观者的形态药剂让玩家在平静和愤怒之间二选一。原版实现里比的是引用相等
（`val == calmChoice`），但两张选项牌是两个不同的类型、各只有一张，所以按类型判完全等价。
不登记的代价实测过：鬼祟珊瑚群那一场，求解器第 1 回合 `max_block=14 actual_block=3`、掉 11 血；
手打是「爆发+ 进愤怒 → 停顿 3+9=12 甲 → 如水 → 药水选平静退出愤怒」，如水在回合结束因为平静
再给 5 甲，17 甲挡掉 14 点，0 掉血。求解器不肯进愤怒的判断在它自己的世界观里是对的——进去了
退不出来就是挨双倍伤害；它只是不知道那瓶药能退出来。

### 2.4 从给定牌堆候选中弃牌

`ICombatPredictionChoiceSink.ResolvePileDiscardChoice(simulator, sourceId, player, sourcePile, options, maxBranches)`
供镜像处理器提交可选弃牌请求。`options` 是效果当时真正展示的有序候选，例如抽牌堆顶的几张牌。
允许空选，选中牌进入弃牌堆，数量范围为 `0..options.Count`。返回 `false` 表示选择挂起，调用方
应向上传播未完成状态；搜索补齐选择后会从稳定父节点重放，返回 `true` 才继续后续效果。

该入口沿用已有动作选择、计划记录和原生页面部署通道。手牌之外的弃牌排序使用源牌堆平均牌值
减去被弃牌牌值，并加上弃牌触发收益。`maxBranches` 对排序后的候选设置保留上限，省略时沿用
现有枚举策略；传 `1` 只保留排序第一项，会牺牲其他选择路线，适配者应使用目标场景验证取舍。

### 2.5 卡牌的玩家选择

此入口随 PR #56 合入，并于 `0.32.0` 发布。使用此入口的适配 Mod 应将 CombatSolver 最低依赖设为 `0.32.0`。

```csharp
CardChoiceMirrors.Register<TYourCard>(spec, apply);
```

和药水那条是同一堵墙的两面。`CardChoiceSupport.GetSpec` 是按原版卡牌类型写死的 `switch`，
默认分支返回 `null`，也就是「这张牌没有选择」。第三方卡牌落到那里就是这个答案，于是它的选牌
效果**永远不会被展开成搜索分支**：牌照样打得出去，效果在模拟里静默变成空操作。卡牌自己的
`CardOnPlayMirrors` 补不了这个——选择的展开发生在出牌路径上，不在效果镜像里。

两个委托：

- `spec(simulator, playedCard, card)` 返回一个 `CardChoiceSpec`。
- `apply(simulator, combat, playedCard, card, choice)` 施加效果，返回是否已经结算完。

比药水那条多两件要注意的事：

1. **升级等级必须对。** 部署时按 CardId 加升级等级在原生页面上定位选项。三选一这类牌通常会让
   三张选项跟着本牌一起升级，`spec` 里就要把选项牌也升级，否则部署定位不到。
2. **选项牌不在任何模拟牌堆里。** 所以 `apply` 拿到的是计划里的 `PlanCardToken`，不是
   `PredictedCard`；按 CardId 自己认，求解器不会替你解析。数值要读就从选项牌自己的
   `DynamicVars` 上读，不要写死。

效果同样用 `PlanChoiceEffect.ModDefined`。求解器自己从不产生这个值；如果它出现在卡牌选牌上
而没有登记方认领，结算会直接抛，不会静默空操作。

**一个真实例子。** 观者的许愿是 3 费，打出后在「力量 +3」「多层护甲 6」「金币 25」之间三选一
（升级后 4 / 8 / 30，三张选项牌各自的 `MagicNumber` 就是这三个数）。三个选项的单位完全不同，
但都不需要新的估值刻度：力量和多层护甲本来就是 Power，金币走求解器现成的长期资源刻度
（`GainPlayerGold` 加 `RecordLongTermResource`，`贪婪之手` 就是面值直记）。登记成三个真分支之后，
「值不值这 3 点能量」和「三个里挑哪个」都由搜索自己比出来，不需要写任何策略规则。

### 2.6 Power 的隐藏状态进指纹

随 PR #58 于 `0.33.0` 发布。登记应在 Mod 初始化、任何根捕获和后台搜索之前完成；搜索期间保持登记表不变。依赖此入口的适配 Mod 应要求 CombatSolver `0.33.0`。

```csharp
// 状态在普通私有字段里：只要这一条。
PowerHiddenStateMirrors.Register<TYourPower>(
    "TotalMantraGained",
    (simulator, power) => power.某个私有计数);

// 状态在 _internalData 里：还要这一条，否则模拟一开始读到的是初值。
PowerHiddenStateMirrors.RegisterRootCapture<TYourPower>(
    (simulator, clone, original) =>
        simulator.StateStore.GetReadOnly(clone, () => new MyState(original)));
PowerHiddenStateMirrors.Register<TYourPower>(
    "InstanceCount",
    (simulator, power) => simulator.StateStore.Peek(power, static p => new MyState(p)).Count);
```

状态指纹里 Power 的通用部分只收 `DynamicVars`。把语义状态放在 `_internalData` 或普通私有字段里
的 Power 走的是另一条路：`AddTurnStartStates` 按原版类型 `switch`，从 `StateStore` 里的预测状态
取一个计数塞进指纹（虚空形态、硬化外壳、自动机、束缚锁链……）。那个 `switch` 没有第三方入口。

**后果和别的缺口不一样，要分清：**

- **续用核对尚未覆盖此状态。** 两侧通用 Power 字段一致不能证明隐藏状态一致；跨回合适配需要单独验证原生与预测状态。
- **对搜索去重有害。** 只在这个状态上不同的两条分支指纹相同，会被当成同一个状态**去掉一条**。
  你的镜像算出来的数值是对的，但搜索可能把算得对的那条丢了。

所以这不是「记个 `Unmirrored` 就行」的事——红字只是显示，不会让被去重掉的分支回来。

#### 续用核对边界

`PowerModel.DeepCloneFields` 会把 `_internalData` 重置成 `InitInternalData()`。续用核对若要覆盖隐藏状态，需要分别读取原生状态和已捕获的预测状态。本入口仅提供搜索指纹与根捕获登记，尚未提供这两侧的续用追加入口。

#### 靠 `_internalData` 的必须登记根捕获

同样因为克隆会重置，这类 Power 必须用 `RegisterRootCapture` 在根捕获时把实机实例的值搬进
`simulator.StateStore`，此后一律读预测状态，**不要再读克隆上的 `GetInternalData`**。这正是原版
`PowerPredictionStateSupport.CaptureRootState` 在做的事，照它的形状写即可。搜索途中新施加的实例
不走根捕获，它们的 `_internalData` 本来就是初值，预测状态首次取用时按初值起算就是对的。

状态放在普通私有字段里的 Power 不受影响（`MemberwiseClone` 会带过去），只登记读取函数就够了。

#### 三条约束

1. **只收整数。** 原版那个隐藏计数段里全部是整数或枚举；字符串只会出现在展示用的名字上，那类
   字段按 `SemanticStateFieldPolicy` 本来就不该进指纹。
2. **读取函数必须是纯读取。** 它在搜索热路径上被调用很多次，不得有副作用，也不要在里面分配。
3. **返回值只能取决于这个 Power 自己的状态**（含它在 `StateStore` 里的预测状态）。它参与状态
   等价判断，读别处会让等价判断不自洽。

登记多个状态就多调几次 `Register`，名字在同一类型内不得重复，下游按名字排序后依次进指纹。

**两个真实例子，都在观者。** 光辉的伤害等于牌面值加上本场战斗累计获得的真言，累计值在
`WatcherStatePower` 的一个普通私有 `int` 里，只需要读取函数；登记之后「先攒真言再打光辉」和
「直接打光辉」不再被当成同一个状态。天人形态的那个 Power 用 `_internalData` 存一个实例表，每回合
给「总和」点能量再把每个实例加一——总和就是 `Amount`，本来就在指纹里，缺的只是**实例个数**，
也就是下一回合总和的增量；它要根捕获加读取函数两条，登记一个 `InstanceCount` 就够了，不需要把
整张表塞进去。

### 2.7 局外成长来源的独立额度

下一版本的成长早停按逐来源的可证明实际可打次数判断。第三方登记新增可选 `opportunityTarget`；旧登记不需要修改，但命中旧登记时继续完整搜索，不推断完成次数。战损目标早停默认开启；成长来源仅在本场实际可用卡牌命中 `hasTarget` 且考虑局外收益时形成目标，只保存非零额度不算实际目标。
原版禁忌魔典已包含独立删牌收益额度，按每次成功增加战后删牌奖励计数。至亮之焰的单场最大生命消耗上限属于独立成本约束，不使用成长收益向量表示负收益，也不受 IgnoreLongTermRewards 影响；它不改变第三方成长来源登记接口。

尚未发布，登记入口在下一版本。登记应在 Mod 初始化、任何搜索之前完成；搜索期间保持登记表不变。

```csharp
// 加载时登记一次，把句柄存下来。
private static GrowthSourceHandle _diligence;

_diligence = GrowthSourceMirrors.Register(
    "YourMod.Diligence",                        // 持久化键，建议带 mod 前缀
    () => ModelDb.Card<YourDiligenceCard>(),    // 侧栏这一行的图标和标题，延迟调用
    card => card is YourDiligenceCard && card.DeckVersion != null,
    opportunityTarget: context => GrowthOpportunityTarget.Bounded(
        context.MatchingCards.Sum(card => 1 + card.FixedReplayCount)));

// 第四个参数是标题覆盖，也是延迟调用，参数就是上面那个函数取回来的牌。
// 只在「牌名说明不了这个来源」时才填，比如原版把黏稠强化那一行显示成「防御 + 强化名」。
_wishGold = GrowthSourceMirrors.Register(
    "YourMod.WishGold",
    () => ModelDb.Card<YourGoldWishOption>(),
    card => card is YourWishCard,
    card => ModelDb.Card<YourWishCard>().Title + "·" + card.Title);

// 收益真的到手时记一次。
combat.RecordGrowthReward(_diligence);
```

`opportunityTarget` 只收到冻结值：匹配牌的 id、运行时类型名、是否有永久牌组实例、牌自身固定重放次数，以及当前敌人数。它不能取得 `CombatState` 或实机 `CardModel`。能够证明有限上限时返回 `Bounded(非负次数)`；存在循环、动态重放、复制、生成或回收时返回 `Unbounded("稳定原因")`。返回负数或无效结果会在搜索开始前明确失败，不会替换成默认值。全局已知的动态次数风险由求解器统一判定，不能在具体成长牌的计算器里绕过。

成长策略解决的是这类问题：贪婪之手、巨镰、遗传算法这些牌，收益落在**这场战斗之外**——金币、
永久升级、局外强化。求解器默认只看本场战斗的血量与胜负，于是「多挨几点伤害换一次永久升级」
一律判成亏。侧栏让玩家给每个来源单独填一份「每次收益允许的额外战损」，搜索据此在打分里给这条
线路记一笔 HP 信用额度。

原版十个来源写死在 `GrowthSource` 枚举里，`GrowthValues` 是与之对应的十个 int 字段（疯狂科学仅能力／改进变体）。局外成长类
卡牌很多 mod 都有，它们全部落不进那个枚举：既拿不到自己的额度栏，收益也记不进
`SimulatedCombatState.GrowthRewards`。**表现不是「少了个选项」，而是搜索必然避开这张牌**——
付出的血看得见，换回来的东西在打分里根本不存在。

登记之后你会得到四样东西；提供可证明的目标计算器时还会启用第五项：

- 成长策略侧栏多一行，有自己的图标、标题和额度输入框，排在原版十行之后、按登记顺序；
- 额度按你给的 id 存进设置文件，也进问题包的有效策略和路线缓存；
- `GrowthValues.HasTarget` 认得你的牌，于是「打到可接受战损就提早收手」那条捷径会被关掉——
  否则搜索会在还没摸到你这张牌之前就收手；
- 计数进状态指纹，只在「有没有拿到这次收益」上不同的两条分支不会被当成同一个状态去重；
- 所有活动成长来源都有界并全部兑现后，允许在完整胜利及其余政策目标也满足时提前结束搜索。

#### 五条约束

1. **id 要稳定。** 它是持久化键，改 id 等于换来源，玩家原来填的额度不再生效。为此额度按 id
   存而不是按登记序号存：玩家临时停用你的 mod 时，那份额度会原样留在设置文件里，重新启用后
   还在，不需要再填一遍。
2. **`RecordGrowthReward` 只在收益真的到手时调用。** 额度是「每次成功收益」的单价，多记一次
   就等于凭空多出一份额度，搜索会拿它去换真实的血。原版的口径可以照抄：斩杀类要求满足致命
   条件（`WasFatalKill`），永久成长类要求那张牌有局外牌组实例（`card.DeckVersion != null`），
   炼制药水要求成功入槽。
3. **`hasTarget` 必须是纯判断。** 它会对玩家牌组里每张牌调用。永久成长一类记得跟原版一样要求
   `DeckVersion != null`——战斗里临时生成的副本升级了也带不出战斗。
4. **金币一类要两处都记。** 局外成长额度和长期资源刻度是两回事：`RecordLongTermResource` 记的是
   「这条线路带走了多少局外价值」，`RecordGrowthReward` 记的是「为这次收益可以额外付多少血」。
   原版贪婪之手两个都调，第三方的金币收益照做。
5. **目标次数必须是可证明上限。** 能力牌、消耗牌和固定重放可以按冻结实例计算；普通非消耗牌、
   动态重放、复制、生成同名牌和消耗回收不能按牌张数封顶。证明不了就返回 `Unbounded`，旧登记留空
   也具有相同的“不早停”语义。

取牌或取标题函数抛异常不会连带侧栏起不来：那一行退化成「没有图标、标题显示 id」，额度照样能
填、照样进搜索，日志里留一条 warn。这是这个入口唯一一处「装一半」，因为它只影响显示。

**两个真实例子，都在观者。** 勤学精进是永久升级，和原版遗传算法、巨镰同一类，直接登记就位。
许愿三选一里的金币那一支和贪婪之手同一类，除了原来就有的 `RecordLongTermResource` 还要补一次
`RecordGrowthReward`——只记长期资源的话，搜索知道这条线路带走了金币，却不知道玩家愿意为它付血。
### 2.8 移除估值的偏置

尚未发布，登记入口在下一版本。加载时登记一次即可。

```csharp
CardRemovalValueMirrors.Register<YourStrike>(-10d);
CardRemovalValueMirrors.Register<YourDefend>(-10d);
```

净化、洗炼这类**移除**选择按 `CardChoiceSupport.RemovalPriority` 从低到高排序，估值低的先被
移除。通用估值把伤害记满、格挡打八折，于是一张 6 伤害的起手打击得 `6.0`，比一张 5 格挡的起手
防御（`4.0`）还高——按通用估值排，先被烧掉的会是防御。原版五个角色的实战优先级正相反，所以
`BasicCardRemovalValue` 用一张按类型写死的表把这十张起手牌压回正确的相对位置。

那张表**只列原版十张**。它的注释里写明了理由：其他来源的打击、防御「强弱取决于各自的机制，
这里没有依据替它们排序」。这个判断对求解器成立，**对你不成立**——你知道自己那张牌是不是起手牌。
所以这里开一个登记点，让你自己声明。

**登记的是偏置，不是绝对值。** 最终估值 = 通用估值 + 你给的偏置，所以牌自身的梯度保住了：
升级过的起手打击伤害更高，加同一个偏置之后仍然比未升级的那张更靠后被烧。

**负偏置是这个入口的重点。** `ChoicePriority` 对消耗返回 `-Σ RemovalPriority` 并按降序取分支，
所有估值都是正数时，「一张都不选」（0）永远排第一——消耗在选择排序里从来只有「少亏一点」，
没有正收益。把一张真正的废牌压到负值，「烧它」这条分支才会排到「不烧」前面。

**为什么不是让你声明「这是起手打击」。** 原版那张表把起手防御排在起手打击之后（格挡 × 1.2、
伤害 × 2/3），因为原版五个角色留防御更划算。这个相对顺序**不通用**：观者靠姿态和心灵堡垒起甲，
一张普通防御比一张打击更该烧。类别抽象会把原版的假设强加给你，偏置不会——通用估值本来就把格挡
打了八折，同样偏置下防御自然排在打击前面。

**这个入口不怕被滥用。** 把自己的牌估低等于让求解器优先烧掉它，估高等于让它留在牌库里堵手，
两个方向的代价都由你自己承担。绝对值上限 `100`，够表达「这张牌白占位置」，又不至于一次手滑让
求解器烧光牌库。

**不登记的后果是静默的。** 你的起手打击按通用估值算成一张有伤害的好攻击牌，于是净化永远不会
先烧它——它不报错、不打红字，只是求解器再也不会替你压牌库。实测一场女王：玩家手打消耗掉三张
观者打击、把全知与内心宁静留在牌库里；求解器反过来消耗了全知、内心宁静、痛击，把四张打击留着。
两边同样有疾风连击 4，只有前者的牌库能持续转起来。

**负偏置还有第二个作用：那张牌按牌库杂质计。** 状态牌和诅咒本来就进 `liveDeckClutter`，只要还
占着牌堆就扣分，所以消耗掉它们是正收益。别的牌不进那一项——于是消耗一张非状态非诅咒的牌在打分
里的收益**正好是零**（`retainedAttackValue` 有上限，攻击牌多的时候早就顶满，少一张也不掉），
「打出净化消耗两张废牌」严格劣于「不打净化」，省下那点能量总是更划算。排序偏置排不出一个本来就
不存在的收益，所以负偏置同时表示「这张牌占着牌堆就是负担」。

原版那张写死的表优先：已经列进去的类型不会被登记表改写。登记表为空时下游一行都不多走。

**这个入口解决的是「别烧错、该烧的要烧」，不解决「为了压出无限而主动烧牌」。** 后者要的是对
「移除之后牌库能不能自持」的判断，那是求解器的估值主干，见第 6 节。

### 2.9 遗物与 Modifier 的分支状态

`ModelPredictionStateMirrors.RegisterRelic<TModel, TState>` 与 `RegisterModifier<TModel, TState>`
按精确类型登记根捕获、实机字段和预测字段。状态通过现有 `PredictionStateStore` Fork，
同一字段口径进入搜索指纹与 `ContinuationStamp`，按实例所属位置绑定，不合并同类型计数。
首次根或续用捕获后拒绝继续登记；未捕获状态不回落到 live 值。

此接口不放行 Mod、补丁或 Hook，不扩展遗物／Modifier 的中途增删。
卡牌引用可用 `PredictionCardReferences.RequireCard` / `Remap` 与 writer 的 `AddCard` / `AddCards`；
只支持当前五个战斗牌堆，位置索引按观察惰性建立，缺失或歧义拒绝。无序描述须显式声明。
完整签名、对象重映射、字段格式及验证边界见[模型状态适配](third-party-model-state.md)。
与其他内部镜像入口一样，外部程序集仍需要 publicizer；本接口尚未发布。

### 2.10 回合末晚期效果

`AfterSideTurnEndLateMirrors.Register<TModel>(handler)` 为精确运行时类型登记
`AbstractModel.AfterSideTurnEndLate` 的预测实现，适用于遗物、Modifier、Power 等模型。
玩家与敌方回合末共用入口，回调自行根据 `Side`、`Participants` 判断是否生效。
底层沿用 `MethodMirrorRegistry` 和覆盖描述元数据，外部仍需 publicizer。

登记必须在首次 `CombatRootSnapshot.Capture` 或本阶段分发之前完成，此后明确拒绝登记。
与多数旧镜像不同，本阶段遇到未登记且非纯表现的重写会记录风险并抛出
`NotSupportedException`，不会只标记风险后继续生成路线。
完整签名、暂停和状态约束见[回合阶段镜像](third-party-turn-phase-mirrors.md)。

### 2.11 已适配 OnPlay 补丁组合

`AdaptedCardOnPlayMirrors.Register<TCard>` 登记精确目标、完整补丁组合与唯一完整预测实现。
首次根／续用捕获后冻结；根选择通过标准 registry 分派，命中后不再执行原版 OnPlay/spec。
组合核对包含实际顺序、owner、优先级和 before／after；不放行未知来源或明确不兼容 Mod。
配置进入 continuation，旧根及路线沿既有边界核对失效。建根时冻结所有已补丁 OnPlay 方法，
并审完全部已登记的卡牌类型。战斗中首次出现的未登记类型只按冻结方法集合判定：
无补丁就交回普通镜像，有补丁则明确拒绝；worker 不读取实时 Harmony 表。
支持面、条件 descriptor、async／动态卡牌限制及测试见[OnPlay 补丁适配](third-party-onplay-patches.md)。

### 2.12 还没有登记入口的地方

见第 6 节。目前只能 Harmony 打补丁，或者等对应的扩展点合并。

### 2.13 第三方怪物与 Power：按运行时 `Type` 登记

上面 §2.1 那张「46 张注册表」里，绝大多数入口是泛型 `Register<TYourType>(handler)`。**外部程序集
拿不到泛型形参**——被适配的 Mod 不在编译期引用里，类型只能在运行期按全名查出来——所以凡是给
第三方用的入口都额外提供 `Type` 重载，两条路共用同一张表、同一套重复登记检查：

```csharp
XxxMirrors.Register(Type modelType, Action<AbstractModel, XxxMirrorContext> handler);
ModifyHpLostMirrors.RegisterAfterOstyLate(Type modelType, Func<AbstractModel, ModifyHpLostMirrorContext, decimal> handler);
BeforeSideTurnEndMirrors.RegisterEarly(Type modelType, Action<AbstractModel, BeforeSideTurnEndMirrorContext> handler);
PowerHiddenStateMirrors.Register(Type powerType, string name, Func<CombatPredictionSimulator, PowerModel, long> read);
PowerHiddenStateMirrors.RegisterRootCapture(Type powerType, Action<CombatPredictionSimulator, PowerModel, PowerModel> capture);
AfterAttackMirrors.Register(Type modelType, Action<AbstractModel, AfterAttackMirrorContext> handler);
BeforeCardPlayedMirrors.Register(Type modelType, Action<AbstractModel, BeforeCardPlayedMirrorContext> handler);
AfterDeathMirrors.Register(Type modelType, Action<AbstractModel, AfterDeathMirrorContext> handler);
AfterDeathMirrors.RegisterIgnored(Type modelType);
BeforeDeathMirrors.Register(Type modelType, Action<AbstractModel, BeforeDeathMirrorContext> handler);
BeforeDeathMirrors.RegisterIgnored(Type modelType);
```

处理器收到的是 `AbstractModel`，自己转成需要的基类（`PowerModel` 是公开类型，可用；
被适配 Mod 自己的具体类型仍只能反射）。**不要在处理器里做 `Type` 比较分派**——登记本身就是
分派，处理器只该处理那一个类型。

怪物那侧的入口集中在 `ThirdPartyAdapterRegistry`，同样只收字符串/`Type`：

| 入口 | 用途 |
|---|---|
| `RegisterMonsterMoveEffect(怪物类型名, 行动 Id, handler)` | 某个行动的**非攻击部分**（攻击仍由通用攻击循环按意图结算）。跑在攻击**之后** |
| `RegisterMonsterMoveBeforeAttack(怪物类型名, 行动 Id, handler)` | 某个行动在**攻击之前**要做的部分（先加格挡／先上状态再打）。同一行动可以两段都登记 |
| `RegisterMonsterMoveAttackResults(怪物类型名, 行动 Id, handler)` | 某个行动在**攻击结算之后**、行动效果之前的部分，形参里带这次行动的**全部伤害结果**。用于「按这次攻击的未被格挡伤害回血」这类行动——自己拿「伤害 − 攻击前格挡」去凑是近似（易伤／无实体／虚弱都会改真实数值） |
| `RegisterMonsterBranchResolver(怪物类型名, 分支 Id, resolver)` | 自定义分支状态的下一步选择。`resolver(monster, branchId, stateLog, rng, combat, simulator)`：行动历史与 RNG 逐条照抄源码，**数值一律从模拟状态读** |
| `RegisterPureBranchSelector(怪物类型名, 分支 Id)` | 声明该分支的选择函数是**纯读取**，预测器可以照旧在实机上调用它推演后续回合；见下 |
| `RegisterMonsterStateMembers(怪物类型名, 成员名…)` | 会变、且被分支/效果依赖的标量，根捕获时播种、随 Fork、进指纹 |
| `RegisterStaticIntMembers(怪物类型名, 成员名…)` | 只在根捕获读一次的静态数值 |
| `RegisterStableAttack(怪物类型名, 行动 Id)` | 该行动的攻击数值在意图构造时即已固定（`MultiAttackIntent(常量, 常量)` 一类），压掉预测器的「动态伤害」误报。**运行期会核对意图形状**，见下 |
| `RegisterMonsterAttackValues(怪物类型名, 行动 Id, resolver)` | 该行动的伤害／段数**每次出手都要按分支状态现算**（`Dynamic*AttackIntent` 一类）。`resolver(combat, monster)` 只读模拟状态，返回 `BranchMonsterAttack(伤害, 段数)`；与上一条**互斥**。见下 |
| `RegisterOwnerRemovingMove(怪物类型名, 行动 Id)` | 该行动把**施法者自己移出战斗**（逃跑／脱战，或先杀掉自己再留下生成物）。效果侧由登记方在处理器里调 `CreatureEscaped`（逃跑）或置 `killedOwner`（自杀），这一条负责让意图预测侧停止给它排后续回合 |
| `MonsterSpawnSupport.SpawnByType(…, Type 怪物类型, …)` | 按**运行时类型**生成第三方怪物（召唤／分裂／复活）。见下 |
| `MonsterRngSupport.State(simulator, monster)` | 怪物自己那条私有 RNG 流（`MonsterModel.Rng`）在预测里的分支副本，提供 `NextInt` / `NextFloat` / `NextItem` / `Draws`。见下 |
| `RegisterTurnStartPower(Power 类型名, handler)` | 第三方 Power 在自己那一方回合开始时的状态重置 |
| `RegisterSideTurnEndPower(Power 类型名, handler)` | 第三方 Power 重写的**常规（非 Late）`AfterSideTurnEnd`**。派发在 `EndTurnPowerSupport.TriggerRegular` 那个原版 `switch` 之后、同一轮循环内；没登记的类型与原来一样什么都不做，见下 |
| `RegisterSideTurnEndModel(模型类型名, handler)` | 第三方**非 Power 模型**（怪物这类）重写的常规（非 Late）`AfterSideTurnEnd`。派发在敌人侧回合末既有链路的末端、晚期 `AfterSideTurnEndLate` 之前。**玩家侧模型尚未开放**（见第 6 节） |
| `RegisterSideTurnStartPower(Power 类型名, handler)` | 第三方 Power 重写的 `BeforeSideTurnStart`。派发在 `TurnStartPowerSupport.TriggerBeforeSideTurnStart` 里、原版那些按类型写死的块之后；没登记的类型与原来一样什么都不做，见下 |
| `BeforeSideTurnEndMirrors.RegisterVeryEarly(模型类型名, handler)` | 第三方模型重写的 `BeforeSideTurnEndVeryEarly`（回合末的**最早**阶段）。与 `RegisterEarly` 分成两个入口是有意的：阶段顺序本身是语义的一部分（往昔之章的睡眠 Power 必须在这之前把金属化摘掉，否则同一回合末会多给一次格挡） |
| `RegisterStolenCardPower(Power 类型名, hasStolenCard)` | 第三方**偷牌** Power：终局的「未追回战利品」与「持有者死亡时核销」原先只认原版 `SwipePower`（偷牌）与 `ThieveryPower`／`HeistPower`（偷金币）。不登记的话，被偷的牌会一直算作丢失（界面与排序都会错）；`hasStolenCard(simulator, power)` 由登记方回答「这个实例现在扣着牌吗」 |
| `RegisterCardAfflictionSource(Power 类型名, 病症 Type, 牌型?)` | 第三方 Power 的「在玩家身上时给某类牌挂**病症**、消失时摘掉、之后进入战斗的牌同样处理」，对应核心为原版 `TangledPower`／`HexPower`／`RingingPower` 写死的那套规范化。挂的层数固定 1（与源码的 `CardCmd.Afflict<T>(card, 1m)` 一致）；Power 自身的移除时机另在 `RegisterSideTurnEndPower` 一类的入口表达，见下 |
| `RegisterRevivePower(Power 类型名, 死亡回合 Id, 复活回合 Id)` | 第三方 Power 的「死亡后**保留尸体**、稍后复活」（原版 `ReattachPower` 的对应物）：同侧队友里带同一个 Power 的个体构成一组，组里还有活人就保留尸体并强制走死亡回合，之后复活回合治疗 `Amount` 点并复活；全组都死才算真死。不登记的话「死了就直接判赢」或者「卡在死亡回合回不来」，见下 |
| `RegisterRespawnPower(Power 类型名, 重生行动 Id, 待复活血量成员名, 处理器)` | 第三方 Power 的「死亡后**重生到新阶段**」（原版 `AdaptablePower`／测试体的对应物）：死亡保留尸体、强制走一个指定行动，那个行动里换最大生命并满血回来；处理器由登记方写（换血、摘掉该摘的 Power）。只要这个 Power 还在，战斗就不能结束——所以重生回合必须把它自己摘掉。同一条还替它回答 `ShouldOwnerDeathTriggerFatal`（false），见下 |
| `AllowCombatSubscriber(类型全名/Type)` | 订阅者门禁放行，见 §1.1 |
| `RegisterDeathReturnedGold(怪物类型名)` | 第三方**偷金币**怪物：原版「击杀盗贼拿回赃款」由 `HeistPower.BeforeDeath` 实现（赃款先转给生成出来的同伴，再靠杀那个同伴结算奖励），第三方盗贼则常用**本体** `ThieveryPower` 偷钱、自己在死亡时归还（往昔之章 Looter／Mugger 在 `Creature.Died` 事件里 `AddExtraReward(GoldReward(..., wasGoldStolenBack: true))`）。模拟器不触发 C# 事件，这个事实只能由适配登记；不登记时该怪物死亡不核销未追回金币，界面一直显示「未追回」、保资源排序还会去追一笔实机已经还回来的钱。识别这场遭遇**不需要**额外登记：`TheftEncounterStrategy.IsApplicable` 按场上是否有人携带偷窃标记（本体 `ThieveryPower`／`HeistPower`／`SwipePower`、登记过的偷牌 Power，或登记过本入口的怪物）判定 |

#### 第三方分支状态的选择函数：默认**不在预测里调用**

意图预览（`IntentForecaster`）会从当前行动往后推演十几个回合，它看的还是**实机模型**。原版三种状态
里它按权重、按冻结选择走；碰到第三方自定义的分支状态时，它原来会直接调
`MonsterState.GetNextState` —— 也就是**你写在对方程序集里的那个委托**。那个委托跑在实机模型上，
**可以写实机字段**。往昔之书的 `SelectNextMove` 每次都 `StabCount++`，而它的攻击段数读的正是这个计数：

```csharp
new DynamicMultiAttackIntent(() => StabDamage, () => StabCount)   // 段数 = 实机 _stabCount
private string SelectNextMove(...) { ...; StabCount++; return "STAB"; }
```

后果不是「预览不准」，而是**求解器把玩家正在打的那场战斗改坏了**：一次预览推演 16 个回合就
`StabCount++` 十几次，游戏自己的意图显示于是变成 7×15，实际结算也照着 7×15 打。

所以现在**默认不调用**：命中未声明的第三方分支状态时，推演在那条怪物上停下、记一条
`unsupported`（界面显示「预览可能不完整」），绝不拿玩家的战斗去换一个更长的预览。

确实逐行复核过「只读自己的标量字段与实机 `StateLog`、只按源码顺序抽传入的 `rng`、不写实机状态、
不下命令」的选择函数，可以显式声明放行：

```csharp
ThirdPartyAdapterRegistry.RegisterPureBranchSelector("你的怪物类型名", "你的分支 Id");
```

声明前必须先登记同一 (怪物, 分支) 的 `RegisterMonsterBranchResolver`——只声明纯读取没有意义，
搜索侧仍然会拒绝这条分支。两条都登记之后，这条分支在**搜索**里走你的 `resolver`（用模拟状态重算），
在**预览**里走实机委托（读当前实机值），与适配前后的行为一致。

`RegisterPureBranchSelector` 与 `AfterDeathMirrors.RegisterIgnored` 是同一类「已复核事实」登记：
挡行为变化的是你自己的复核和你写的自检，不是运行期能判定的性质。

#### 攻击前／攻击后：行动的时序由两张表分开表达

求解器结算一个怪物行动的顺序是**先攻击、再行动效果**（`MonsterMoveSemantics.ApplyForecastMove`）。
源码里很多行动是「先给自己加格挡／先给玩家上状态，再打这次攻击」，用行动效果表达就会**晚一拍**——
格挡晚一拍会和「挨打反伤」这类效果交互出不同结果。这类行动要用攻击前那张表：

```csharp
ThirdPartyAdapterRegistry.RegisterMonsterMoveBeforeAttack("怪物类型名", "行动 Id", handler);
// handler(simulator, combat, move, player) —— 返回值版本不需要，它不下命令也不选目标
```

同一 (怪物, 行动) 同时登记两段是允许的：攻击前那段先跑，打完再跑行动效果那段，各自对应源码里的位置。
典型例子是往昔之章球状守卫的 `HARDEN`（先 15 格挡、再打出攻击）。

#### 分支解析器读的必须是模拟状态

`RegisterMonsterBranchResolver` 的解析函数拿到 6 样东西：

```csharp
string Resolve(MonsterModel monster, string branchId, IReadOnlyList<string> stateLog,
               Rng rng, SimulatedCombatState combat, CombatPredictionSimulator simulator)
```

- `stateLog` 是**当前预测分支**的行动历史（不是实机状态机的 `StateLog`）；
- `rng` 就是 `MonsterAi` 那条流，**调用顺序与短路条件都要照抄**——「某条分支不抽 RNG」也是语义；
- `combat` / `simulator` 用来读**模拟状态**里的数值：怪物标量走 `combat.GetMonsterInt/Bool`，
  生物血量走 `simulator.State.GetCreature(creature).CurrentHp / MaxHp`（例如「队友已损失生命和超过阈值
  就治疗」这种分支只能这么写）；
- 实机生物的血量、手牌、Power 一律**不许读**：它们不随分支 Fork，读到的会是同一份陈旧值。

#### 可变的「状态字节」不要直接用冻结的条件分支

原版 `ConditionalBranchState` 的选择函数可以读**可变私有字段**。求解器在根捕获时会把条件分支
的取值冻结成一份快照，快照是按**捕获那一刻**的字段值算的——之后每个行动推进字段都不会改变它。
直接用冻结值会从第一步就错，而且错得很安静。

正确做法是把那个字段登记成**怪物标量状态**（`RegisterMonsterStateMembers`，
于是它随分支 Fork、进状态指纹、进续用核对文本），再由 `RegisterMonsterBranchResolver`
按当前值解析。Act4Heart 的三个怪物（`CorruptHeart` / `SpireShield` / `SpireSpear`）都是这个形状：
一个 `private byte state` 由各行动的 `state |= 1` / `state |= 2` / `state = 0` 推进
（`SpireSpear` 的**初值是 2**，不是 0——这类初值必须逐个核对源码）。

三个条件都不命中时明确抛 `PredictionUnsupportedException`，对应实机「找不到下一个状态」的失败，
不要猜一个。

#### 只抽一次的私有 RNG：复刻，不要绕过

有些第三方怪物会用**每只怪物各自一条**的 RNG（`MonsterModel.Rng`，由
`CombatState.CreateCreature` 按「run 种子 + 坐标 + CombatId」播种），它与
`simulator.Rng.MonsterAi` 是两条互不影响的流。求解器不模拟这条流。

不要因为它「只是一次抽样」就跳过：那条流被推动的次数会改变之后每一次抽样，绕过会让整局错位。
做法是把实机实例上的流整份搬进一个可 Fork 的预测状态（`IPredictionStateForkable`），
在预测里**照源码的抽样顺序**复刻，并把已抽次数写进一个自建的怪物标量成员好让指纹看得见进度。
球位/几率这种条件一定要连**短路顺序**一起照抄——「不抽」和「抽了但没走那条路」是两种不同的状态。

#### 生成第三方怪物：`SpawnByType`

召唤、分裂、复活都要在模拟里凭空造出一只**你的**怪物。泛型入口 `<T>` 你用不了（类型只在运行期按全名
查得到），所以有按 `Type` 的版本：

```csharp
Creature child = MonsterSpawnSupport.SpawnByType(
    simulator,
    combat,
    source: move.Owner,          // 施法者，用于入场能力与遗物联动
    monsterType: mediumSlimeType, // 反射拿到的 Type
    slot: "acid_med_1",           // 遭遇布点表里的槽位，可为 null
    maxHpOverride: currentHp);    // 对应源码里 Add 之后的 SetMaxHp + Heal 到满
```

它与求解器自己用的泛型路径逐段相同：规范实例取自 `ModelDb`、克隆、`CreatePredictedMonster`
**掷一次初始生命**（消耗 `Rng.Niche`——源码的 `CreatureCmd.Add` 也会掷，所以这一步不能省，
否则随机流与实机错位）、布点、原版入场能力与遗物联动、行动 AI 准备。
`maxHpOverride` 在**掷完之后**覆盖最大生命并把血补满，与源码 `SetMaxHp(n)` + `Heal(n, true)` 等价。

三件要自己负责的事：

1. **入场效果不会被自动执行。** 求解器只对原版类型写死了入场能力；你的 `AfterAddedToRoom` 不会被调。
   需要它做的事（例如分裂出来的大史莱姆要再挂一份分裂能力）由你在生成后自己施加。
2. **布点自己按源码规则挑。** 冻结的布点表在 `SimulatedCombatState.EncounterSlots`；源码里
   「前缀匹配 + 排除存活队友已占用的槽」这套规则照抄即可，挑不到就传 `null`。
   槽位决定敌人的排序，进而影响「第一个敌人」这类取目标行为，别一律传给 `NextSlot()`。
3. **自己的离场自己结算。** 分裂／自爆这类「先杀掉自己再留下生成物」的行动，
   在处理器里 `simulator.Kill(owner)`（`force` 与源码的 `CreatureCmd.Kill(c, false)` 对齐）后
   把 `killedOwner` 置真，死亡结算才会照常跑；同时用 `RegisterOwnerRemovingMove` 声明出去，
   预览才不会在它死后继续给它排行动。

#### 私有 RNG 流：`MonsterRngSupport`

每只怪物还有一条**自己**的 RNG 流（`MonsterModel.Rng`，由 `CombatState.CreateCreature` 按
「run 种子 + 地图坐标 + CombatId」播种，与 `RunRng.MonsterAi` 是两条互不影响的流）。原版只拿它做外观，
但你的 Mod 可能把它用在行动效果里（抽目标、抽几率）。求解器不模拟这条流，所以要在预测里照源码顺序复刻：

```csharp
MonsterRngPredictionState rng = MonsterRngSupport.State(simulator, move.Owner.Monster!);
if (teammates.Count > 0)                       // ← 先按源码的短路判「有没有候选」
    target = rng.NextItem(teammates)!;         //   空集合一次都不抽，与 Rng.NextItem 一致
combat.SetMonsterInt(move.Owner, "adapter_your_mod_rng_draws", rng.Draws);
```

`State(...)` 在 `PredictionStateStore` 里按怪物实例建一份可 Fork 的副本：第一次取用时把**实机实例**
上的流整份拷下来，Fork 时再整份拷走，此后与实机脱钩；`NextInt` / `NextFloat` / `NextItem` 都会把
`Draws` 加一。三条纪律：

1. **只在源码真的抽了的时候抽。** 「不抽」和「抽了但没走那条路」是两种不同的状态，短路顺序必须照抄
   （`NextItem` 对空集合不抽，但源码自己还会先判一次 `Any()`，那一次判断决定的是**要不要走到** `NextItem`）。
2. **`Draws` 写进一个自建的怪物标量成员**，名字**不要**放进 `RegisterMonsterStateMembers`——实机怪物
   身上没有这个成员，登记进名单会让根捕获去读一个不存在的字段。指纹遍历的是整张标量状态表，
   于是只差这条流进度的两条分支不会被去重成一条。
3. **前提是这条流在战斗里只被这只怪物自己的行动推动。** 实机实例上的当前状态只有在那个前提下才是
   准确的起点（音效、动画、台词走的是 `Rng.Chaotic`，不算数）。登记前逐行复核这一点，并在自检里调
   `MonsterRngSupport.VerifyShape()` 钉住 `MonsterModel.Rng` 这个公开访问点。
   **已知局限**：状态是首次用到时才从实机拷一份，不是根捕获时冻结的；同一只怪在同一回合真的行动过之后
   再开始搜索，起点会偏后——这个局限与心脏盾兵球位的既有实现一致。

#### 常规回合末（非 Late）的 `AfterSideTurnEnd`

求解器的回合末分两段：**常规**（`AbstractModel.AfterSideTurnEnd`）与**晚期**
（`AfterSideTurnEndLate`，走 `AfterSideTurnEndLateMirrors`）。原版那一批常规效果按类型写死在
`EndTurnPowerSupport.TriggerRegular` 的 `switch` 里，第三方类型落在 `switch` 之外——结果是
「回合末该发生的事永远不发生」：不报错、不算未支持，但整场预测都少一块。这条路径现在对第三方开放：

```csharp
ThirdPartyAdapterRegistry.RegisterSideTurnEndPower(
    "你的Power类型名",
    static (simulator, combat, power, side, participants) =>
    {
        if (side != power.Owner.Side)      // 与源码同一个判据
            return;
        combat.Apply<StrengthPower>(power.Owner, combat.GetAmount<YourPowerType>(power.Owner), power.Owner);
    });
```

三条纪律：

1. **判据照抄源码**：`side == power.Owner.Side` 这类判断、层数读写顺序、以及「先清账再加值」的先后
   都要与反编译一致；处理函数跑在**同一轮循环内**，所以它相对其它 Power 的位置与源码的模型遍历顺序相同。
2. **只读写模拟状态**：层数用 `combat.GetAmount` / `combat.SetPowerAmount` / `combat.Apply`，
   需要伤害、抽牌等可见效果时走 `simulator` 的对应入口（与其它镜像同一条边界）。
3. **没登记就不做任何事**：这条入口是**纯新增**——未登记的类型与加这个入口之前完全一样，
   因此它不会改变原版或既有第三方内容的行为。**仍然封闭**的是玩家侧非 Power 的常规回合末特化
   （`BeforeSideTurnEnd` 系列之外的抽牌/手牌清理那一批）与 `BeforeSideTurnStart`（见第 6 节）。

#### 第三方 Power 驱动的卡牌病症（`RegisterCardAfflictionSource`）

原版有三个 Power 会给玩家的**牌**挂病症：`TangledPower`（缠绕，攻击牌 +1 费）、`HexPower`（虚无）、
`RingingPower`（鸣响）。它们的三个钩子——`AfterApplied`（给当前所有受影响牌挂上）、
`AfterCardEnteredCombat`（之后**新进入战斗**的牌也挂上）、`AfterRemoved`（Power 没了就摘掉）——
在求解器里**都没有通用分发点**，核心是用一套按类型写死的规范化
（`SimulatedCombatState.NormalizeCardAfflictions`）等价表达的：Power 在 ⇒ 牌上没有病症就挂上，
Power 不在 ⇒ 牌上是这个病症就摘掉。第三方 Power 落在写死的名单外，效果就是**这个 Power 在预测里是空的**：
数值不报错，但整回合的可打出性都算错（往昔之章的 `EntangledPower` 就是这一类：它在场时所有攻击牌
带 `Unplayable`）。

入口只收「Power 类型名 + 病症 `Type` + 可选的牌型」，三件事一次表达完：

```csharp
ThirdPartyAdapterRegistry.RegisterCardAfflictionSource(
    "你的Power类型名",
    typeof(你的Affliction),      // 必须是 AfflictionModel 子类
    CardType.Attack);            // null 表示对任意牌生效；源码只对攻击牌就写 Attack
// 别忘了 Power 自己在什么时机消失（例如源码的 AfterSideTurnEnd）：
ThirdPartyAdapterRegistry.RegisterSideTurnEndPower("你的Power类型名", 你的回合末处理器);
```

三条纪律：

1. **先复核三个钩子与源码一致**再登记：施加时给哪些牌挂、之后进入战斗的牌同样处理、Power 消失后摘掉。
   挂的层数固定 1（源码就是 `CardCmd.Afflict<T>(card, 1m)`）；源码只给某一类牌挂就用第三个参数表达，
   不要靠「反正 `CanAfflictCardType` 会挡」——那是一次静默的多挂／漏挂。
2. **Power 的移除时机不在这个入口里**。它只回答「Power 在不在 ⇒ 牌上有没有这个病症」，
   所以「回合末摘掉自己」要另外登记（上一条），否则病症会一直挂着，玩家整场都打不出攻击牌。
3. **病症实例由核心按 `Type` 从 `ModelDb` 取规范实例再复制**（外部程序集拿不到泛型入口）。
   自检里要确认这个病症类型确实存在于对方程序集，并且它确实是 `AfflictionModel`。

**仍然是纯新增**：没有登记项的进程里，这段规范化与加这个入口之前逐字节一致（不分配、不做额外扫描）。

#### 死亡后保留尸体并复活（`RegisterRevivePower`）

原版 `ReattachPower`（分裂蜈蚣的「重新接上」）与它的同类做的是：持有者死掉时**不**被移出战斗，
而是记成「复活中」，被强制去打一个什么都不做的死亡回合，再在下一个行动里治疗 `Amount` 点并回来；
只有同一组全死了，这些尸体才算真死、战斗才可以结束。核心把这条语义按类型写死在四处：

- `ICombatPredictionCreatureSemantics.ShouldRemoveAfterDeath`（保留尸体）；
- `DeathPowerSupport.Trigger`（开始复活阶段 + 强制走死亡回合，全组都死则整组永久死亡）；
- `SimulatedCombatState.ResolveReviveMove`（复活回合治疗 `Amount` 并回到正常回合）；
- `RevivingEnemyHp`（复活中的尸体按待复活的 `Amount` 计入终局口径）。

第三方类型落在写死的名单外，症状是**安静的错**：死了当场判赢，或者一直卡在死亡回合。登记只有一行：

```csharp
ThirdPartyAdapterRegistry.RegisterRevivePower("你的Power类型名", "DEAD_MOVE", "REATTACH_MOVE");
```

**契约（只登记与 `ReattachPower` 同形的 Power）。** 同侧队友里**带同一个 Power** 的个体构成一组
（与源码按 Power 分组同口径）；组里还有活人时保留尸体并强制走「死亡回合」，之后「复活回合」若组里仍有
活人就治疗 `Amount` 点并复活，否则保持死亡；组里最后一个也死时整组标成永久死亡。

四条纪律：

1. **逐行比过 `ReattachPower` 再登记。** 判据（组内其他人是否都死）、治疗的数值来源（`Amount`）、
   两个行动 Id、以及「复活中不可被打」都要与源码一致；行动 Id 写错不会报错，只会在运行到那一刻时
   明确失败（`ForceMonsterMove` 找不到那个行动）。
2. **Power 自己必须 `ShouldPowerBeRemovedAfterOwnerDeath() => false`**，否则尸体会被清掉、永远回不来。
3. **`ShouldAllowHitting` 的重写不用镜像**：核心按死亡阶段判「复活中不可被打」，不看那个重写。
   `ShouldOwnerDeathTriggerFatal` 也不用镜像，但**必须**是「其他队友是否都死」这条判据——核心对
   登记过的复活 Power 用模拟状态替你回答（源码那份实现读的是 `Owner.CombatState`，在预测里会读到实机值）。
4. **`AfterDeath` 那条重写仍然会在派发时记一条 `MethodNotMirrored` 风险**（核心对第三方重写没有通用
   `AfterDeath` 入口），但 `PredictionCoverage` 对登记过的复活 Power 把它记成**已补偿**——与原版
   `ReattachPower` 完全同一处理，所以不会连带把「这一局算不算打赢」压掉。

#### 死亡后重生到新阶段（`RegisterRespawnPower`）

另一型是原版 `AdaptablePower`（测试体）那一种：死亡后**必走**一个指定行动，那个行动里自己换最大生命
并满血回来（往昔之章的觉醒者是「一阶段死亡 ⇒ `REBIRTH` 换成 300／320 血」）。它与上一条的区别是形状：
没有「同一组里还有没有活人」这条判据，也不需要两个行动（死亡回合与重生回合是同一个）。核心把它写死在四处：

- `ShouldRemoveAfterDeath`（保留尸体）与 `DeathPowerSupport.Trigger`（进入复活阶段 + 强制走那个行动）；
- `ResolveReviveMove`（那一个行动执行时做重生）；
- `RevivingEnemyHp`（复活中的尸体按「回来时会有多少血」计入终局口径）；
- `IsCombatEnding` 里的 `ShouldStopCombatFromEnding`（**这个 Power 还在，战斗就不能结束**）。

```csharp
ThirdPartyAdapterRegistry.RegisterRespawnPower(
    "你的Power类型名",
    "重生行动Id",                 // 死亡时被强制走的那个行动
    "Phase2Hp",                  // 「回来时多少血」的怪物静态数值成员（已用 RegisterStaticIntMembers 声明）
    static (simulator, combat, power) =>
    {
        // 换最大生命 + 治疗满；然后**必须把这个 Power 自己**（以及该摘掉的其它 Power）SetPowerAmount(..., 0)，
        // 否则「Power 还在 ⇒ 战斗不能结束」会让战斗永远结束不了。
    });
```

四条纪律：

1. **Power 自己必须 `ShouldPowerBeRemovedAfterOwnerDeath() => false`**（否则尸体会被清掉），
   并且重生回合里要**自己摘掉它**——那是战斗能否结束的开关。
2. **`ShouldOwnerDeathTriggerFatal` / `ShouldStopCombatFromEnding` 都不要自己去调**：核心对登记过的
   重生 Power 分别回答「这一阶段不算真死（false）」与「Power 还在就不能结束」，因为源码那两份实现读的是
   实机（`owner.Monster` 的私有计数、`Owner.CombatState`、`IsDead`），在预测里会读到陈旧值。
3. **处理器只读写模拟状态**：换血用 `SimCreatureState.SetMaxHp` ＋ `simulator.Heal`（与 `CreatureCmd.Heal`
   同一条镜像），摘 Power 用 `SetPowerAmount(power, 0)`；返回后核心把死亡阶段清成「正常」，处理器不要碰它。
4. **`AfterDeath` 那条重写仍会记一条 `MethodNotMirrored` 风险**，但 `PredictionCoverage` 对登记过的
   重生 Power 记成**已补偿**（与原版 `AdaptablePower` 同一处理）。

#### 回合开始的 `BeforeSideTurnStart`

同一个阶段在求解器里**有**派发点（`TurnStartPowerSupport.TriggerBeforeSideTurnStart`，每一方回合开始时调用），
但原版那一批效果按类型写死在里面——例如原版 `PlatingPower` 在**第 1 回合**、玩家侧开始时给敌人补一次格挡
（判据就是 `CurrentSide == Player && RoundNumber <= 1`）。第三方类型落在那些循环之外，所以要么在这里登记，
要么就是「回合开始该发生的事永远不发生」。登记方式与回合末那条对称：

```csharp
ThirdPartyAdapterRegistry.RegisterSideTurnStartPower("你的Power类型名",
    static (simulator, combat, power) => { /* 只读写模拟状态；回合数用 combat.RoundNumber */ });
```

`combat.RoundNumber` 在模拟状态里是真实推进的（`CombatBeamSolver.RoundTransition` 自增，也进诊断与指纹），
所以「只在第 1 回合」这类判据可以原样表达。仍然是**纯新增**：未登记的类型不做任何事。

#### 阶段缺口要显式记下来

不是每个阶段都有登记入口。心脏的 `RegeneratePowerA4h` 走 `AbstractModel.AfterSideTurnEnd`，
而求解器只镜像 `AfterSideTurnEndLate`（§2.10），**没有** `AfterSideTurnEnd` 这个阶段，
因此无法建模。这种情况不要登记一个「近似」实现，把它写进适配模块的已知缺口常量并打进日志，
让缺席是可见的。见第 6 节。

#### 已复核但无玩法影响的重写：显式登记为忽略，不要留在风险里

未登记的重写会记一条 `MethodNotMirrored` 风险。**风险不只是显示。**只要那条 gap 的方法名里带
«Death»，`CombatBeamSolver` 就不承认这一局已经打赢——`uncertainVictory` 会把搜索边界改写成
`UnsupportedEffect`，而 `CombatBeamSolver.Terminal` 又要求 `BoundaryReason != UnsupportedEffect`
才肯记 `CombatEndedTurn`；`CombatEndedTurn` 是 `null`，界面就永远显示「预计战损 未知」，可信度也一并
掉到「低」。一条本来无害的钩子，代价是整场战斗给不出战损。

所以逐行反编译确认「没有命令、数值、RNG、状态读写」的重写，应当按精确类型登记为忽略：

```csharp
AfterDeathMirrors.RegisterIgnored(modelType);   // 语义同原生那批 RegisterIgnored<T>()
BeforeDeathMirrors.RegisterIgnored(modelType);  // 死亡前那一条同理，同样是「带 Death 就会吞掉胜利」
```

这与原生的 `RegisterIgnored<KinPriest>()` 是同一种结论：**人工复核**，不是运行期可判定的性质。
自检只能核对签名与成员是否还在，挡行为变化的是清单里的版本下限。心脏适配的
`Act4Heart.CorruptHeart.AfterDeath`（只换背景音乐）就是这么处理的，链式后果与实证见
[AFTP / Act4Heart 适配状态](AFTP_ACT4HEART_STATUS.md) §3.6。

**两条死亡钩子都要查，别只看 `AfterDeath`。** 判定链读的是 `PredictionGap.Method`，只要方法名里带
«Death» 就算，所以 `BeforeDeath` 上一条没登记的重写同样会让整场战斗给不出战损。往昔之章第一批适配
就是这么漏掉真菌兽的：它同时重写了 `BeforeDeath`（纯粒子特效，该登记为忽略）与挂了
`SporeCloudPower.AfterDeath`（死亡时给全体玩家易伤，该给真镜像），两条都缺，于是每一场打赢的真菌兽
战斗都停在「预计战损 未知」。批量排查办法：把对方每个 `CustomMonsterModel` / `CustomPowerModel`
的 `public override` 方法列出来，只挑名字含 «Death» 的，逐个归类为「真镜像」或「忽略」。

#### `DamageCalc.Target != null` 不等于「动态伤害」

预测器按「`DamageCalc` 是否绑定了实例」判断攻击是否动态，原版那批已复核的行动写死在
`IntentForecaster.IsKnownStableAttack` 里。这个判据对下面两个构造函数是**误报**：

```csharp
public MultiAttackIntent(int damage, int repeat) { DamageCalc = () => damage; _repeat = repeat; }
public SingleAttackIntent(int damage)            { DamageCalc = () => damage; }
```

它们把**构造实参**捕进闭包，`Target` 是编译器生成的显示类而不是怪物实例，段数也是普通只读字段。
第三方类型不在原版白名单里，只能靠 `RegisterStableAttack` 声明，否则每一回合都多报一条
`approximation=…:动态伤害`。声明前必须反编译核对行动确实用的是「常量构造」这一形状。

**声明是可以在运行期被证伪的，所以它会被现场核对。** 适配层在初始化时既拿不到怪物实例、也拿不到
那些 `private int X => AscensionHelper…` 属性的值——数字不是 `const`，抄一份进适配层只会变成
第二份真相。于是 `IntentForecaster` 命中第三方登记时还会调
`ThirdPartyAdapterRegistry.IsStableAttackShape`（判据实现在 `src/Prediction/StableAttackShape.cs`）：

| 意图的伤害闭包 | 判定 |
|---|---|
| `SingleAttackIntent(int)` / `MultiAttackIntent(int, int)` 捕下的构造实参——闭包显示类声明在**意图类型自己内部** | 成立 |
| 没有计算器，或委托 `Target` 为空（静态方法组、无捕获的静态缓存 lambda 之外的 `Target == null` 情形） | 成立（与既有 `DamageCalc?.Target != null` 判据一致，本来就不进近似清单） |
| 调用方传进来的委托：捕获 `this` 的 lambda（`Target` 就是怪物）、捕获调用方局部变量的闭包、调用方的无捕获 lambda | **不成立** |
| 派生意图在自己的构造函数里再造一层闭包（往昔之章的 `DynamicSingleAttackIntent` / `DynamicMultiAttackIntent`） | **不成立** |

不成立时**不认这条声明**，近似清单照旧记一条「动态伤害」，不会让界面声称算准了。
核对程序：`tools/StableAttackShapeChecks`（`ok=6`，含原版两条常量构造重载、调用方闭包与两种
派生意图形状）。

这条判据**只管伤害**。段数走 `Repeats`，而 `MultiAttackIntent` 还有一条
`(int damage, Func<int> repeatCalc)` 重载（伤害是常量、段数不是），它不在判据覆盖范围内——
多段行动仍须按反编译确认用的是 `(int, int)` 那条重载。

#### 数值真的会变的行动：`RegisterMonsterAttackValues`

`RegisterStableAttack` 声明的是「数值永远不会变」。往昔之书的 `STAB` 是另一回事：它读的是自己身上
**每次转移都会 +1** 的计数，冻结值会让整场都按捕获那一刻算。

```csharp
private string SelectNextMove(Creature owner, Rng rng, MonsterMoveStateMachine sm)
{
    int num = rng.NextInt(100);
    if (num < 15) { … StabCount++; return "BIG_STAB"; }
    …
    StabCount++; return "STAB";                      // 四条出口都 +1
}

new MoveState("STAB", Stab, new DynamicMultiAttackIntent(() => StabDamage, () => StabCount))
```

登记方式是给出一个**只读模拟状态**的解析器：

```csharp
ThirdPartyAdapterRegistry.RegisterStaticIntMembers("BookOfStabbing", "StabDamage");
ThirdPartyAdapterRegistry.RegisterMonsterStateMembers("BookOfStabbing", "_stabCount");
ThirdPartyAdapterRegistry.RegisterMonsterAttackValues(
    "BookOfStabbing",
    "STAB",
    static (combat, monster) => new BranchMonsterAttack(
        combat.GetMonsterStaticInt(monster.Creature, "StabDamage"),   // A9+ 7／否则 6，根捕获时冻结
        combat.GetMonsterInt(monster.Creature, "_stabCount")));       // 段数每次出手现算
```

四条纪律：

1. **解析器只读模拟状态。** 搜索里每个分支都是实机模型的一份共享引用，`GetMonsterInt` /
   `GetMonsterStaticInt` 读的是**这个分支**的值；直接读实机字段会让所有分支看到同一个数，
   而且拿不到未来回合的推演值。
2. **段数依赖的计数必须先播种。** 它是 `RegisterMonsterStateMembers` 的成员，根捕获时读一次实机值
   （实机此刻显示的就是它），之后随分支 Fork、进状态指纹。往昔之书**不能**在根捕获时再跑一次分支来
   「初始化」——实机已经跑过一次了，再跑一次就是 §2.13 那个 7×15 的第二份。
3. **登记之后不能再声明固定。** 同一条行动两边都登记会在初始化时直接抛错：执行侧会按动态值走，
   界面侧却被固定声明压掉「动态伤害」提示，是一条自相矛盾的登记。
4. **它同时改搜索结算与当前回合的意图显示。** `BranchMonsterAi.CurrentMove` 是两条路共同的入口；
   预览的后续回合仍然受 §2.13 那条「未声明的第三方分支不调用」约束。

往昔之书的完整清单与逐行对照见 [AFTP / Act4Heart 适配状态](AFTP_ACT4HEART_STATUS.md) §2.17。

## 3. 登记的纪律

这几条不是风格建议，是踩过的坑。

### 3.1 加载时一次性登记完

注册表**按精确运行时类型缓存查询结果，而且 `Register` 不会让缓存失效**。一旦某个类型被查过
一次（拿到 `Inferred` 或 `Unsupported`），之后再登记也不会生效，而且不报错。

所以：在 Mod 初始化时把所有登记做完，绝不在战斗中途登记。

`ModelPredictionStateMirrors` 不使用上述延迟分派缓存，而是在第一次根或续用捕获后冻结整张登记表；
迟到登记明确抛异常。两类入口的共同要求仍是初始化期间一次完成登记。

`AfterSideTurnEndLateMirrors.Register` 在标准 registry 外提供冻结检查，首次根捕获或分发后
也会明确拒绝迟到登记；外部调用此入口，不绕过它直接写入内部 registry。

### 3.2 失败要关死，不要装一半

自检不通过时**一个镜像都不要登记**。装一半比不装更糟：求解器会拿着一部分正确的镜像给出看起来
可信的路线，缺掉的那部分静默变成空操作。全都不装的话，求解器会明确停在门禁上并显示原因，
玩家至少知道出了事。

同理，解析不到 Harmony 目标方法就抛异常让整层注册失败，不要跳过继续。

### 3.3 按反编译出来的实现写，不要照卡面文字猜

卡面文字和实现经常不一致：触发时机、目标选择、数值来源、结算顺序。逐条对照反编译源码写，
一张牌一个方法、一行一效果、按原版的调用顺序排列，这样可以逐行复核。

典型的坑：变量键名。`PowerVar<T>` 单参数构造生成的键是 `typeof(T).Name`（例如
`VulnerablePower`），不是卡面上显示的那个词。写错会让整次搜索失败。

### 3.4 钉死你依赖的版本，并在运行期自检

求解器的内部接口会变。适配层应当：

- 构建期引用确定版本；
- 运行期核对自己用到的那几个方法签名和字段还在不在，不在就干净地拒绝加载。

同理，如果你在适配**别人的** Mod，按文件哈希钉死比按版本号更稳——作者不一定每次改动都升版本
号，而一个没升版本号的签名改动会让某张牌变成「没有效果但看起来正常」。

### 3.5 时机比数值更容易错

抽牌发生在触发它的那张牌离开出牌堆之前还是之后、Power 在这张牌自己结算之前还是之后到位、
「上一张牌」是本回合的还是整场的——这些一错，数值全对但结果不对。写注释说明你选的时机和依据。

## 4. 怎么验证自己做对了

### 4.1 两条验收标准

**不要用胜率或手感做验收。** 镜像低估自己的伤害会让求解器打得保守，于是活得久——这种路线能
通过手感检验，通不过严格 diff。

标准是：

1. **严格 diff 零差异**：模拟的终局状态和真实终局状态逐字段相等。
2. **`PredictionGaps` 里非补偿项为空**：求解器自己不报告任何未镜像效果。

胜率是在这两条都干净**之后**才有意义的指标，用来抓 diff 抓不到的东西，比如某个 Power 在估值
函数里定价错了。反过来先看胜率，会让你在错误的地方停下来。

### 4.2 夹具要能自己验算，而且要有反向对照

好夹具的判据落在能用算术自己验的量上——能量够不够打第二张牌、格挡数值、正好击杀的回合数——
而不是「跑起来不报错」。

**每条夹具都要做一次反向对照**：把你要验的那行登记注释掉重新构建，夹具必须不过；加回来必须
过。没做过反向对照的夹具证明不了任何事。

无头夹具的跑法见 [HEADLESS_TESTING.md](HEADLESS_TESTING.md)。

### 4.3 用玩家的问题包，不要只看描述

求解器自带问题包导出，里面有完整路线、逐检查点状态、日志和一份自动分类（例如
`BetterWorldline 预计战损 11 → 0` 就是「玩家手打比求解器的路线好，好 11 点血」）。
带问题包基本都能定位；只有文字描述通常不够。

## 5. 一个完整例子

观者 Mod 的「以手拒之」：打出后给目标挂一层反弹格挡，之后玩家每打中这个敌人一段就起
`Amount` 点甲。

**症状。** 手里以手拒之 + 两张打击，敌人这回合打 4 点。求解器给的顺序是
「打击 打击 以手拒之」，第 1 回合 `max_block=0`，白挨 4 点。第 2、3、4 回合都是
`max_block=4 actual_block=4`——层数一旦挂上去后面每回合都算得对，唯独挂上去的那一回合被浪费。

**排查。** 先确认镜像本身对不对：反弹格挡的钩子分发和逐条判定（目标判定、施加者判定、
`IsPoweredAttack`、`TotalDamage > 0`、受益者三级回退、`Unpowered` 不吃敏捷、不自减）都和反编译
出来的实现核对过，两条夹具锁住了这一半。**镜像是对的，坏的是排序。**

**根因。** `ClassifyActionOptionFamilies` 判一个动作算不算 `ImmediateDefense`，看四样：这次动作
的格挡增量、`ProjectedPlayerHp`、`PlayerHp`、`StrategicEffects.PreventionPotential`。以手拒之
打出的瞬间这四样一样都不动——它自己不给甲，而反弹格挡这层 Power 挂在**敌人身上**，设置估值那圈
原本只统计玩家自己身上的增益。于是它被归成一张纯 `ImmediateOffense`，和打击同族但伤害更低，
在族内代表里被打击压掉。

**修法。** 用 `StrategicEffectMirrors.Register<BlockReturnPower>(..., StrategicEffectHost.Enemy)`
登记估值。登记之后打出它会让 `PreventionPotential` 从 0 变正，于是它同时进 `ImmediateDefense`
族，不再被压掉。

**验证。** 夹具 `WATCHER-TALK-TO-THE-HAND-ORDERING`：以手拒之加两张打击、3 能量，判
`max_block >= 4`（以手拒之给 2 层，两张打击各 1 段，排最前面 = 4 甲，排中间 = 2，排最后 = 0）。
做过反向对照：注释掉那行登记，夹具不过。

**这个例子的一般教训**：现象是「AI 不会用这张牌」，根因既不在这张牌的镜像里，也不在搜索深度或
估值权重上，而在动作分类那一层。排查顺序应当是：先确认镜像对不对，再看它有没有被搜索看见，
最后才怀疑估值。

## 6. 已知的封闭开关

下面这些位置目前是按原版类型写死的开关，第三方登记不进去。要用只能 Harmony 打补丁，或者等
对应扩展点合并。列在这里是为了让你知道撞上了什么，而不是以为自己写错了。

| 位置 | 症状 | 状态 |
|---|---|---|
| `CombatPredictionSimulator.SupportsManualCardChoiceContinuation` / `PredictionStateStore.SupportsManualCardChoiceContinuation` | 自身选牌续执行覆盖清单中的41张原版单人卡，要求无附魔/污染、手动单次执行；已生成的请求、候选、历史与活动格挡计数有显式复制合同，不能据此接纳第三方选牌委托；拒绝不透明外部状态以及所有 `IPredictionForkBoundary` 状态（包括模型状态适配器包装）。不符合时保留原完整回放，已有第三方战斗支持范围不因此扩大；无注册入口 | 封闭性能特化 |
| `CombatPredictionSimulator.ExecutionContinuation` / `ExecutionDispatchScope` | 回合来源、抽牌、Hook及嵌套子出牌使用内部纯数据帧。未知派发未确认协议、未知历史、不可复制事务或不透明StateStore时拒绝捕获，继续既有完整回放；不会跳过游戏效果，也不把既有第三方登记等同于可复制回调。原Fork稳定断言保持；没有外部续跑注册入口 | 封闭性能特化 |
| `PotionChoiceContinuation.Supports` | 9种原版手动选牌药水的稳定前缀特化；第三方类型与通过PotionChoiceMirrors登记覆盖原版选择者继续完整重放，无额外注册入口。普通Fork/StateStore断言保持，不能用此入口接纳不透明回调或事务 | 封闭性能特化 |
| `SimulatedCombatState.AfterCardEnteredCombat` → `GhostSeedMirrors` | 幽灵种子按本地基础牌标签处理真实入场；已捕获根卡的关键词不会由后续归一化重新改写，入场镜像仍为原版封闭派发。**第三方 Power 的「新牌入场也挂病症」已开放**：`ThirdPartyAdapterRegistry.RegisterCardAfflictionSource`（§2.13），走的正是这个函数末尾的卡牌病症规范化 | 原版封闭派发；第三方病症已有入口 |
| `SimulatedCombatState.ApplyWithBeforeApplied` / `AfterCardEnteredCombat` → `PhantomBladesPowerMirrors` | 幻影之刃的首次施加和卡牌入场直接派发精确镜像体，尚未提供通用 Power.AfterApplied 注册入口；其他来源不得依赖全局归一化重新赋予关键词（**卡牌病症**那条「Power 在 ⇒ 挂病症」的规范化是另一条路，入口见 §2.13） | 原版封闭派发 |
| `CardChoiceSupport.Spec` / `CardChoiceSpec.IsImplicitAllSelection` | 原版固定数量选择在候选不足或恰好全部时，按候选顺序生成唯一计划。第三方使用原版隐式全选规则时必须设置该标记；普通手动确认选择保持自己的顺序策略，Runtime对隐式选择严格核对实例和顺序 | 原版特化；第三方选择已有入口 |
| `CombatBeamSolver.CaptureEnergyRefundWindow` / `StrategicEffectContext.RecurringEnergyGain` | 原版环绕轨道按花费余数、自动化按剩余抽牌数估计未来返能，包含自然抽牌；与可消费能量缺口共用上限。第三方仍通过 §2.2 登记，详见[估值上下文](third-party-strategic-effects.md) | 原版特化；第三方估值已有入口 |
| `RelicCounterCatalog` / `SimulatedCombatState.ReadRelicCounter` | 战斗末卡数仅覆盖已核对的十种原版计数；第三方显示计数只列出“尚未适配”，不会被自动当作跨战斗目标。见[计数策略说明](relic-counters.md) | 精确原版适配 |
| `SearchPolicySnapshot.IsAct3BossEncounter` / `CombatBeamSolver.CaptureAct3BossInteractionPotential` | 首领范围只含第三幕实验体、永世沙漏、女王；联动上下文只适配原版 Pagestorm、DanseMacabre、Demesne；StrategicEffectModel 对 PrepTimePower 按未来攻击与回合视野估计重复精力收益。这些不是通用第三方触发次数分析。第三方 Power 仍使用 §2.2 登记 | 原版特化；第三方估值已有入口 |
| `PredictionModHookSubscriberCapture.KnownPreRootSubscriberTypeNames` | 内置白名单本身仍按类型写死；第三方走 `ThirdPartyAdapterRegistry.AllowCombatSubscriber`（§1.1、§2.13）放行，本行只是记下原版那个集合仍然封闭 | 第三方已有入口 |
| `PredictionModPatchAudit.ValidateLoadedMods` | 明确拒绝 `WheelchairSpire`，没有外部放行入口 | 项目不兼容策略 |
| `NativeModelCloneConcurrency` | 预测克隆只放行已核对原版阶段、原版变量及 BaseLib/Ritsu 稀疏元数据复制补丁组合的普通原版卡牌；附魔/灾厄、第三方模型/变量和未知补丁保留原锁。Power 只放行已物化原版变量、继承默认克隆及 InitInternalData 的原版类型，同时核对基阶段与变量 getter 补丁；自定义初始化保持原锁。每个线程最外层模拟隔离域重新核对，不支持求解中安装补丁；原版 MutableClone 保护不变。没有新增外部注册入口 | 精确框架适配 |
| `RitsuEmptyCapabilityFastPathPatches` | 模拟隔离域的空 capability 集可直接保留原卡牌标签序列；不枚举/复制标签，不缓存分支值。非空贡献者与精确类型默认来源继续框架入口；晚注册刷新来源代次，已物化的空集合仍按框架语义处理。live 不旁路，无新增登记入口 | 精确框架适配 |
| `DynamicVarCloneMetadataPatches` | 模拟克隆只优化已核对为空默认值的 BaseLib 提示/升级字段与 Ritsu 提示工厂；非空值照常复制，live 调用保持原框架行为。其他附加字段继续原有克隆逻辑，不属于此优化入口 | 精确框架适配 |
| `CorePowerSupport.TriggerPlayerRegularSideTurnEndEffects`、`FlushPlayerHandAtTurnEnd`、`TurnStartPowerSupport.TriggerAfterPlayerTurnStart`、`SimulatedCombatState.TriggerRelicsAfterPlayerTurnStart` | 玩家侧非 Power 的常规回合末特化、玩家侧回合开始的特化与遗物触发尚无通用登记；晚期 `AfterSideTurnEndLate` 已开放（§2.10）。**第三方 Power 的常规（非 Late）`AfterSideTurnEnd` 已开放**：`ThirdPartyAdapterRegistry.RegisterSideTurnEndPower`（§2.13）。**第三方 Power 的 `BeforeSideTurnStart` 也已开放**：`ThirdPartyAdapterRegistry.RegisterSideTurnStartPower`（§2.13），派发在 `TurnStartPowerSupport.TriggerBeforeSideTurnStart` 里。**敌人侧非 Power 模型的常规回合末也已开放**（`RegisterSideTurnEndModel`，§2.13）。仍未开放的是玩家侧非 Power 的回合开始/回合末特化（`TurnStartPowerSupport.TriggerAfterPlayerTurnStart`／遗物触发／玩家侧模型那批） | 部分开放 |
| `SimulatedCombatState.TryPrepareExtraPlayerTurn` / `TryPrepareLiveExtraPlayerTurn` / `ConsumeExtraTurnSources` | 额外回合的来源硬编码，只认龙涎香和帕尔之眼 | 待做 |
| `CombatPredictionSimulator.OnPlayWrapper` | 出牌后补抽没有挂载点 | 待做 |
| `CardChoiceSupport.RemovalPriority` 的排序口径 | 移除类选择按**单卡**估值排，不看牌库其余部分；弃牌那一侧已经是「源牌堆平均值减本牌估值」的相对口径，消耗与转变没有。表现为求解器不会为了压出无限而主动烧牌。起手牌那一层已由 §2.7 打开，相对口径这一层仍然封闭 | 待做 |
| `ContinuationStamp.AppendCard` 的 `private=` 段与 `CombatBeamSolver.CaptureCardStateFingerprintForTesting` 的 `switch (preview)` | **卡牌**的隐藏字段按原版类型写死（利爪、基因算法、巨锤、狂暴、镰刀、疯狂科学），第三方卡牌的私有计数进不了指纹。Power 那一侧已有 `PowerHiddenStateMirrors`，见 §2.6 | 待做 |
| `SimulatedCombatState.AddTurnStartStates` 的 `switch (power)` | 原版 Power 隐藏计数按类型写死。第三方走 §2.6 的登记表进同一份指纹，本行只是记下原版那个 `switch` 本身仍然封闭 | 第三方已有入口 |
| `RelicPredictionStateSupport` 的原版类型分支 | 内置遗物状态仍按原实现处理；第三方遗物与 Modifier 的独立状态通过 §2.9 登记，不修改原版分支 | 第三方已有入口 |
| `GrowthSource` 枚举与 `SolverGrowthStrategyPanel.SourceCard` 的 `switch` | 原版十类成长来源按类型写死。第三方走 §2.7 的 `GrowthSourceMirrors` 拿独立额度、侧栏行和指纹，本行只是记下原版那个枚举本身仍然封闭 | 第三方已有入口 |

**这些开关新增或改动时，必须在同一个提交里更新这张表和本文档对应章节。** 见
[AGENTS.md](../AGENTS.md) 第 9 节。

## 7. 相关文档

- [架构与职责地图](ARCHITECTURE.md)：源码入口和所有权，`§4.2 Mirror` 是镜像层的位置。
- [战斗钩子覆盖目录](COMBAT_HOOK_COVERAGE.md)：求解器分发哪些 hook。
- [第三方 Power 的战略估值登记](third-party-strategic-effects.md)。
- [AFTP / Act4Heart 适配状态](AFTP_ACT4HEART_STATUS.md)：两个已落地的适配 Mod 的覆盖范围与已知缺口。
- [无头测试](HEADLESS_TESTING.md)：夹具怎么跑。
- [检查点回放](CHECKPOINT_REPLAY.md)：问题包怎么导入。
