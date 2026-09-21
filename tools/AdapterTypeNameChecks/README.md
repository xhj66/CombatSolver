# 往昔之章适配登记名核对

独立进程，不启动游戏、不加载任何 Mod 程序集，只读 PE 元数据。

```powershell
# 仓库根目录；程序集与适配源码目录都可省略（省略时自动发现）
dotnet run --project tools/AdapterTypeNameChecks/AdapterTypeNameChecks.csproj -c Release
dotnet run --project tools/AdapterTypeNameChecks/AdapterTypeNameChecks.csproj -c Release -- <ActsFromThePast.dll> <适配源码目录>
```

程序集路径先用第一个参数，其次 `COMBATSOLVER_AFTP_ASSEMBLY`，最后从 Steam 常见位置
（`mods/ActsFromThePast`、创意工坊 `2868840`、`.combatsolver-precombat/*/startup-mods/*_ActsFromThePast`）
里取最新的一份。源码目录默认从可执行文件向上找到 `adapters/CombatSolver.ActsFromThePastAdapter`。

## 核对什么

对着 `AfpReflection` 的查找口径，把适配源码里的每一处登记声明核对一遍：

| 声明 | 判据 |
| --- | --- |
| `RequireMonster` / `RequireMonsterType` | `ActsFromThePast.<名字>` 或 `ActsFromThePast.Acts.TheBeyond.Enemies.<名字>` 恰好命中一个 |
| `RequirePowerType` | `ActsFromThePast.Powers.<名字>` |
| `RequireAfflictionType` | `ActsFromThePast.Afflictions.<名字>` |
| `RequireType("全名")` / `RequireType(常量)` | 该全名存在 |
| `RequireOverride(类型, 方法, 参数个数)` | 该类型确实重写了这个方法（virtual 且非 NewSlot），形参个数按 Param 表序号非 0 的行数 |
| `RequireConst(类型, 常量, 期望值)` | 该静态字面量 int 常量仍在，且取值与适配钉死的一致 |
| `RegisterMonsterStateMembers` / `RegisterStaticIntMembers` | 名单里的每个成员都在该怪物类型上存在（字段或属性）；这两个入口本身不做校验，名字写错要等根捕获时才炸 |

成功输出一行 `ADAPTER_TYPE_NAMES_OK …` 并返回 0；失败逐条打印并返回 1。

## 为什么需要它

2026-09-21 的问题包（`f9350de8…`）就是这类漂移：适配层用平铺全名去找能力与第三幕怪物，
而往昔之章把它们放在 `ActsFromThePast.Powers` / `ActsFromThePast.Acts.TheBeyond.Enemies`。
自检在第一个能力上失败，`AdapterEntry` 按纪律一个条目都不登记，往昔之章遭遇于是整场搜索失败。
这类回归在没有启动游戏时也能被发现，因此单列一条离线门禁。

## 边界

- 不验证登记表的**语义**（分支实现是否与对方源码逐行等价、行动效果是否正确），
  也不替代游戏内实机验证；它只回答「适配声明的这些名字、形状和常量在已安装版本里还成不成立」。
- 命名空间常量与 `adapters/CombatSolver.ActsFromThePastAdapter/AfpReflection.cs` 里那份手工保持一致；
  适配改查找入口时两边一起改。
- Act4Heart 适配不在范围内（它本来就按命名空间取类型）。
