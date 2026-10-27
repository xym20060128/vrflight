# 森林火灾蔓延系统 — 算法与实现文档

> 技术栈：Unity / C#
> 方法论：**Object-based Cellular Automata（对象化元胞自动机）** + **Simplified Rothermel（简化 Rothermel 地表火模型）**

---

## 目录

1. [系统总览](#1-系统总览)
2. [架构与模块](#2-架构与模块)
3. [核心概念](#3-核心概念)
4. [状态机](#4-状态机)
5. [Tick 流水线](#5-tick-流水线)
6. [邻居图构建](#6-邻居图构建)
7. [传播模型 SimplifiedRothermel](#7-传播模型-simplifiedrothermel)
8. [火锋推进机制 FrontProgress](#8-火锋推进机制-frontprogress)
9. [垂直燃烧与 Torching](#9-垂直燃烧与-torching)
10. [灭火与免疫](#10-灭火与免疫)
11. [视觉渲染层](#11-视觉渲染层)
12. [数据导出](#12-数据导出)
13. [关键参数表](#13-关键参数表)
14. [已知问题与调参说明](#14-已知问题与调参说明)

---

## 1. 系统总览

本系统模拟森林火灾在空间中的蔓延。核心思想是：

- **每棵树 = 一个对象化元胞（FireCell）**，持有自己的状态、温度、燃料、含水率和邻居列表。
- 元胞状态在 `Idle → Burning → Burned` 之间转换，由统一的 `OnStateChanged` 事件驱动视觉层。
- 火灾传播不是"概率点火"，而是**物理化的火锋推进**：燃烧的树通过 Rothermel 模型计算向每个邻居的传播速率，火锋（fire front）以"累计推进距离"的形式逐步逼近邻居，**累计距离 ≥ 实际距离时，邻居被点燃**。

一句话数据流：

```
FireCell A 燃烧
   ↓ 树内火焰向上攀升（BurnHeight ↑）
   ↓ 对每个空间邻居计算 Rothermel 传播速率 R
   ↓ 火锋累积 advance（FrontProgress[source][target] += R·dt）
   ↓ 累积距离 acc ≥ 实际距离 dist
   ↓ target.Ignite() → target 在自己的世界坐标生成火焰
   ↓ target 成为新的 source，继续传播
```

---

## 2. 架构与模块

代码位于 `Assets/forest/script/FireSimulation/`（核心算法）与 `Assets/forest/script/`（视觉/交互层）。

| 文件 | 类 | 职责 |
|---|---|---|
| `Core/FireCell.cs` | `FireCell` | 对象化元胞：状态、温度、燃料、含水率、邻居、垂直燃烧高度 |
| `Core/FireCellState.cs` | `FireCellState` / `FuelType` | 状态枚举 + 燃料类型枚举 |
| `Core/ForestFireManager.cs` | `ForestFireManager` | 管理器：发现元胞、构建邻居图、连接子系统、订阅事件、初始点火 |
| `Spread/FireSpreadSystem.cs` | `FireSpreadSystem` | 蔓延引擎：Tick 驱动"燃烧 → 传播 → 熄灭"完整生命周期 |
| `Spread/SimplifiedRothermel.cs` | `SimplifiedRothermel` / `SpreadResult` | Rothermel 传播模型（纯静态计算，无状态） |
| `Extinguish/FireExtinguishManager.cs` | `FireExtinguishManager` | 灭火：喷水 → 对范围内 FireCell 降温 |
| `Core/FireSpreadRecorder.cs` | `FireSpreadRecorder` | 数据记录：定期快照全场景，导出 JSON |
| `../FireAreaSpread.cs` | `FireAreaSpread` | 火焰视觉：对象池 + 距离 LOD + 火锋粒子 |
| `../TreeBurnReceiver.cs` | `TreeBurnReceiver` | 烧焦材质/着色器同步（垂直燃烧的视觉表现） |

### 职责边界（关键设计）

- **FireCell 只做数据容器 + 状态切换**，不计算温度演化、不发现邻居、不调用视觉层，只发事件。
- **FireSpreadSystem 只做状态推演**，不创建任何视觉对象。
- **FireAreaSpread 只做视觉**，读取 `FireSpreadSystem.GetActiveFronts()` 和每个 `FireCell.CurrentState` 来渲染，不参与传播判定。
- **SimplifiedRothermel 是纯函数**，无内部状态，输入（source/target/风/坡度/dt/burnHeight）→ 输出 `SpreadResult`。

---

## 3. 核心概念

### 3.1 FireCell（对象化元胞）

每棵树是一个 `FireCell`（MonoBehaviour 组件），关键运行时字段：

| 字段 | 含义 |
|---|---|
| `_currentState` | 状态：Idle / Burning / Burned |
| `_temperature` | 温度（℃） |
| `_fuelRemaining` | 剩余燃料（0~1 归一化） |
| `_burnHeight` | 树内火焰攀升高度比例（0=底部刚燃，1=完全吞没） |
| `neighbours` | 空间邻居列表（由 ForestFireManager 构建） |
| `WorldPosition` | `transform.position`（世界坐标） |
| `TreeBaseY` | 树底真实 Y（从 Renderer 包围盒检测，非 pivot） |

### 3.2 燃料类型 FuelType

| 类型 | 说明 | baseSpreadRate (R₀) | receptivity | verticalBurnRate |
|---|---|---|---|---|
| Grass | 草地，极易燃、烧得快 | 0.12 | 1.5 | 0.12 |
| Shrub | 灌木 | 0.08 | 1.2 | 0.06 |
| Hardwood | 硬木/阔叶，难点燃、烧得慢 | 0.04 | 0.8 | 0.025 |
| Softwood | 软木/针叶，含油脂、易爆燃 | 0.07 | 1.1 | 0.045 |
| Litter | 地表枯枝落叶，火势猛 | 0.18 | 1.8 | 0.02 |

---

## 4. 状态机

```
            Ignite() / IgniteAtHeight(h)
  Idle ───────────────────────────────────▶ Burning
   ▲                                          │
   │              Extinguish()                │  BurnOut()
   │      (温度 < ignitionThreshold)          │  (燃料归零)
   └──────────────────────────────────────────┼──────────────▶ Burned
```

- **Ignite()**：`Idle → Burning`，从底部开始（`burnHeight=0`）。仅当 `CurrentState == Idle` 才生效。
- **IgniteAtHeight(h)**：`Idle → Burning`，从指定高度比例开始（用于"火焰舔到树冠"的直接点燃场景）。
- **Extinguish()**：`Burning → Idle`，温度降到燃点以下（喷水灭火），温度重置为 25℃。
- **BurnOut()**：`Burning → Burned`，燃料耗尽。

每次状态转换都触发 `OnStateChanged(cell, oldState, newState)`，由 ForestFireManager 订阅并协调视觉层。

> 重要：`IgniteAtHeight` 有 `if (_currentState != Idle) return;` 守卫，**Burning 状态下不会重复触发状态转换**。

---

## 5. Tick 流水线

`FireSpreadSystem.Tick(dt)` 每个 tick（默认 0.5s）按序执行 6 个阶段：

```
Phase 0   清理过期灭火免疫；清理 _activeBurningCells 中的非燃烧 cell；清理死火锋
Phase 1   燃料消耗：ConsumeFuel(burnRate × dt)
Phase 1.5 垂直火焰攀升：AdvanceBurnHeight(verticalRate × windBoost × dt)
Phase 2   温度更新（被喷水冷却的 cell 跳过自动升温）
Phase 3   BurnOut（燃料归零）或 Extinguish（温度 < 燃点）
Phase 4   邻居传播：SpreadToNeighbours(cell, dt)
Phase 5   清理所有指向已点燃目标的冗余火锋
```

### Phase 1 燃料消耗

```
burnRate = burnRateMultiplier / max(0.1, maxBurnTime)
fuelRemaining -= burnRate × dt
```

默认 `maxBurnTime = 300s`，即一棵树约 5 分钟烧尽。

### Phase 1.5 垂直攀升

```
verticalRate = GetVerticalBurnRate(fuelType)   // 燃料表查表，或 Inspector 覆盖
windBoost = 1 + windSpeed × 0.05               // 风加速树内攀升
burnHeight += verticalRate × windBoost × dt
```

### Phase 2 温度更新

```
peak = ignitionThreshold × peakTemperatureMultiplier   // 260 × 2.5 = 650℃
t = Lerp(ambient, peak, fuelRemaining)                 // 燃料越多温度越高
temperature = max(t, ignitionThreshold)                // 未受外部冷却时恒 ≥ 燃点
```

> 设计意图：只有喷水（`_hasExternalCooling`）能让温度跌破燃点触发 Extinguish；正常燃烧不会自然熄灭。

---

## 6. 邻居图构建

`ForestFireManager.BuildNeighbours()` 使用**空间哈希网格**加速（`cellSize = neighbourSearchRadius = 20m`，每格只查相邻 9 格）：

1. 把所有 FireCell 按 `(x/cellSize, z/cellSize)` 分桶。
2. 对每个 cell，遍历相邻 9 格，排除自己（`o == cell` 以及 `o.gameObject == cell.gameObject`），计算距离。
3. 距离 ≤ 20m 的作为候选，按距离升序排序，取前 `maxNeighboursPerCell = 30` 个。

---

## 7. 传播模型 SimplifiedRothermel

### 7.1 核心方程

```
R = R₀ × φ_wind × φ_slope × φ_fuel × φ_moist × φ_dist × φ_vert × φ_flame × burstMul
```

`R` 单位 m/s，是源树向某个目标方向的火锋传播速率。

### 7.2 各因子

| 因子 | 公式 | 说明 |
|---|---|---|
| **R₀** | 查燃料表 | 基础水平传播速率（源树燃料类型） |
| **φ_wind** | `exp(0.30 × windSpeed × cosθ)` | 顺风加速、逆风抑制；cosθ 钳制 ≥ −0.2 |
| **φ_slope** | `exp(1.2 × tanβ)` | 上坡加速、下坡抑制；tanβ 钳制 [−0.6, 10] |
| **φ_fuel** | 目标 receptivity | 目标树的可燃物易感性 |
| **φ_moist** | `max(0.01, 1 − moisture/0.60)` | 越湿越难燃，钳制下限 0.01 |
| **φ_dist** | `exp(−0.05 × d)` | 距离衰减 |
| **φ_vert** | `clamp(exp(1.0 × Δh/d), 0.03, 20)` | 烟囱效应：向上加速、向下抑制 |
| **φ_flame** | `sqrt(max(0.05, burnHeight))` | 火焰越高辐射越强 |
| **burstMul** | torching ? 2 : 1 | 完全吞没瞬间的热脉冲增强 |

### 7.3 输出 SpreadResult

```
spreadRate     R (m/s)
firelineIntensity  I (kW/m)，Byram 火线强度
frontAdvance   R × dt（本 tick 前进距离，米）
flameHeight    火焰高度（米）
canDirectIgnite  是否可直接点燃
preHeatMoistureDelta / convectiveMoistureDelta  预干燥量
```

### 7.4 Byram 火线强度

```
I = H × w × R × 0.001 × φ_flame × burstMul
```

`H` = heatContent（kJ/kg），`w` = fuelLoad（kg/m²）。

### 7.5 火焰高度

```
flameHeight = treeHeight × burnHeight × (0.5 + 0.008 × I)
```

### 7.6 直接点燃判定（canDirectIgnite）

```
sourceBurnWorldY = source.TreeBaseY + flameHeight
targetCrownBase  = target.TreeBaseY + crownBaseHeightRatio × target.treeHeight
canDirectIgnite  = sourceBurnWorldY ≥ targetCrownBase && dist < flameHeight × 1.5
```

即：源树火焰顶端超过目标树冠底部、且距离足够近时，可跳过火锋累积直接点燃（"火焰舔到枝叶"）。

---

## 8. 火锋推进机制 FrontProgress

### 8.1 数据结构

```csharp
// _frontProgress[源][目标] = 累计推进距离（米）
Dictionary<FireCell, Dictionary<FireCell, float>> _frontProgress
```

- **跨 Tick 持久化**：`acc` 不会每 Tick 重建归零。
- 多火源独立推进同一目标；目标点燃后，所有指向它的火锋一并清理。

### 8.2 推进逻辑

```csharp
if (!tgtDict.TryGetValue(target, out float acc)) acc = 0f;
acc += r.frontAdvance × spreadRateMultiplier;   // 累积
tgtDict[target] = acc;

if (acc >= dist) {                              // 达到实际距离 → 点燃
    target.SetTemperature(target.ignitionThreshold);
    target.Ignite();
    _activeBurningCells.Add(target);
    // 清理所有指向该目标的火锋
}
```

### 8.3 直接点燃捷径

```csharp
bool directIgnite = r.canDirectIgnite
    && directIgniteReachScale > 0f
    && dist < r.flameHeight × 1.5f × directIgniteReachScale;
```

`directIgniteReachScale = 0` 可完全关闭直接点燃，让所有传播都走火锋累积。

---

## 9. 垂直燃烧与 Torching

- 树被点燃后，`burnHeight` 从 0 逐步攀升到 1（完全吞没）。
- `burnHeight` 通过 `φ_flame = sqrt(burnHeight)` 和火焰高度 `flameHeight` 影响传播速率——**火焰越高，向邻居的辐射越强**。
- 当 `burnHeight` 首次达到 1.0，触发 **Torching 爆发态**（持续 2 tick = 1 秒），期间 `burstMul = 2`，对邻居的辐射和火锋推进速率 ×2。

---

## 10. 灭火与免疫

- `FireExtinguishManager.SprayAt(pos, dt)` 用物理球查找到范围内 FireCell，调用 `cell.RemoveHeat(amount)` 降温，并标记 `_hasExternalCooling`。
- `FireSpreadSystem` 在 Phase 2 检测到外部冷却时**跳过温度覆写**，让降温效果保留；Phase 3 判断 `Temperature < ignitionThreshold` 时执行 `Extinguish()`。
- 被扑灭的树进入**免疫期**（默认 8 秒），期间不会被邻居重新点燃，且所有指向它的火锋被清除。

---

## 11. 视觉渲染层

`FireAreaSpread` 是唯一生成火焰视觉对象的脚本。

### 11.1 性能策略

1. **视锥剔除**：屏幕外的树不生成粒子。
2. **距离 LOD**：近（0-60m）全粒子，中（60-150m）减半，远（150-250m）发光代替，超远剔除。
3. **对象池**：预热复用，零运行时 Instantiate/Destroy。
4. **持久粒子**：每棵树维持固定粒子系统，只更新位置/参数。
5. **火锋预算**：每帧上限 40 个火锋粒子。

### 11.2 火焰位置

树冠火焰位置由 `cell.WorldPosition`（x/z）+ `cell.TreeBaseY + yOffset + 树干分布`（y）决定——**每个元胞使用自己的世界坐标**，不存在统一到一个点的逻辑。

### 11.3 火锋粒子

火锋粒子沿 `source → target` 方向，从源到当前 `advance` 距离处生成，生命周期 1 秒。

---

## 12. 数据导出

`FireSpreadRecorder` 定期（默认 1s）快照全场景，导出 JSON 到 `<项目根>/fire_data/`，包含每棵树的坐标/状态/燃烧高度/温度/燃料/含水率，以及所有活跃火锋的推进进度，供外部 Python 可视化。

---

## 13. 关键参数表

### FireSpreadSystem

| 参数 | 默认值 | 含义 |
|---|---|---|
| tickInterval | 0.5 | Tick 间隔（秒） |
| autoTick | true | 是否自动 Tick |
| ambientTemperature | 25 | 环境温度（℃） |
| peakTemperatureMultiplier | 2.5 | 峰值温度 = 燃点 × 此值 |
| burnRateMultiplier | 1.0 | 燃料消耗倍率 |
| spreadRateMultiplier | 1.0 | 火锋推进总倍率（1=原速） |
| directIgniteReachScale | 1.0 | 直接点燃判定距离倍率（0=关闭） |
| extinguishImmunityDuration | 8 | 灭火免疫时间（秒） |

### FireCell

| 参数 | 默认值 | 含义 |
|---|---|---|
| fuelType | Hardwood | 可燃物类型 |
| moisture | 0.3 | 含水率（0~1） |
| ignitionThreshold | 260 | 燃点（℃） |
| maxBurnTime | 300 | 最大燃烧时间（秒） |
| maxFireReach | 15 | 火锋最大有效传播距离（米） |
| treeHeight / treeHeightScale | 0 / 0.6 | 树高（0=自动检测）及缩放 |
| verticalBurnRate | 0 | 垂直攀升速率（0=用燃料表默认） |
| crownBaseHeightRatio | 0.4 | 树冠底部高度比例 |

### ForestFireManager

| 参数 | 默认值 | 含义 |
|---|---|---|
| neighbourSearchRadius | 20 | 邻居搜索半径（米） |
| maxNeighboursPerCell | 30 | 每元胞最多邻居数 |
| autoIgniteOnStart | true | 启动时点燃初始火源 |

### SimplifiedRothermel 模型常数

| 常数 | 默认值 | 说明 |
|---|---|---|
| windCoefficient | 0.30 | 风因子系数 |
| slopeCoefficient | 1.2 | 坡度因子系数 |
| distanceDecayRate | 0.05 | 距离衰减系数 |
| moistureExtinction | 0.60 | 含水率熄灭阈值 |
| verticalCoefficient | 1.0 | 垂直烟囱效应系数 |
| flameHeightCoeff | 0.008 | 火焰高度系数 |
| dryingCoefficient | 0.003 | 辐射预干燥系数 |
| convectiveDryingCoeff | 0.002 | 对流干燥系数 |

---

## 14. 已知问题与调参说明

### 14.1 spreadRateMultiplier 被场景序列化误设（已修复）

曾发现 `spreadRateMultiplier` 在 Inspector 中被误设成 ~0.035（而非代码默认 1.0），导致火锋推进被压 ~30 倍、`BurningCells` 恒为 1、火永远传不到第二棵树。

**修复**：在 `Awake()` 中把该参数强制回 1.0，并在运行时对非法值（<0.1）做防御性纠正。`spreadRateMultiplier` 的语义是"1=原速"，这是明确的调参基准。

### 14.2 传播速率的标定偏差（未改，留待决定）

Rothermel 基础速率偏低：Hardwood 无风 3m 邻居原速约 217 秒，而代码注释的校准目标是"顺风 3m 邻居 30~60 秒"。若要达到该目标，可把 `spreadRateMultiplier` 设为 3~5（有依据的标定，非盲目放大）。这属于**全局倍率标定**，会同时放大 Grass/Litter 等本就快速的燃料，故未默认开启。

### 14.3 重叠树 + 大粒子导致的视觉糊团

森林中存在大量重叠/极近的树（0.1~0.4m），加上 Fire_01.prefab 内部 3 个发射器的粒子尺寸高达 7~25 单位，导致相邻树的火焰在视觉上互相覆盖，看起来"火焰没传播"。这是**视觉/场景问题**，不是传播算法 bug。修复方向（待做）：缩小 prefab 粒子、拉开重叠树、或削减火锋粒子数量。

---

*文档基于当前代码库（含调试期诊断日志）整理。*
