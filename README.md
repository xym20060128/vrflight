# vrflight

基于 Unity / PICO 的 VR 飞行项目，在同一场景中对比持续划水、摇杆、倾身辅助和划手巡航四种控制方式。

## 运行项目

使用 Unity **2022.3.62f3c1**，打开主场景 `Assets/JapaneseCity/Scenes/JapaneseCity_Demo.unity`，默认进入持续划水模式。

依赖 PICO Unity Integration SDK **3.4.0** 和 PICO Unity Live Preview Plugin **1.0.5**。`Packages/manifest.json` 中这两个包使用 `D:/pico-unity-process/…` 本机路径；换电脑后需要安装相应 SDK，并修改对应的 `file:` 路径。Android 打包需要 Android Build Support、SDK/NDK 和 OpenJDK。

## 四种控制方式

左手 **X** 循环切换模式，也可以使用头显内的模式按钮。

| 模式 | 平移操作 | 停止与转向 |
| --- | --- | --- |
| 持续划水 | 手臂划动产生反作用力，经过动力学积分推动飞行 | 停手后由阻尼减速；按住任意 Grip 停止平移，再用手臂动作原地转向 |
| 摇杆 | 左摇杆控制前后、左右，右摇杆上下控制升降 | 松杆后减速；右摇杆左右转向 |
| 倾身辅助（界面显示“前倾加速”） | 左摇杆前推配前倾向前加速，后拉配后倾向后加速；左右平移和升降仍由摇杆控制 | 直立、松杆或倾斜方向不匹配时减速；反向输入先刹停；右摇杆左右转向 |
| 划手巡航 | 每次有效划动结束后增加一次目标速度，停手保持巡航；反向划动先减速，再通过后续动作后退 | 按住左手 Y 刹车；按住任意 Grip 停止平移，再用手臂原地转向 |

手臂方案使用 **Trigger** 允许升降输入。巡航模式松开 Trigger 后，竖直目标速度平滑归零。右手 **A** 喷水。

倾身辅助在头显追踪有效后记录中立姿态。站直后按右手 **B** 可重新校准。倾身信号取头显相对中立位置的前后位移，不是头部俯仰角。

## 参数怎么调

在场景中找到 `FlightControlModeSwitcher`，其 Inspector 集中显示模式引用和参数。

### 公共转向设置

四种模式的转向上限由 `Comparison` 提供，主场景当前配置为：

| 参数 | 当前值 | 作用 |
| --- | --- | --- |
| `maxYawSpeed` | 20°/s | 人工转向角速度上限 |
| `yawAcceleration` | 40°/s² | 转向角加速度上限 |
| `stickDeadzone` | 0.15 | 摇杆输入死区 |

调整转速时修改这里。运行时公共设置会覆盖摇杆和倾身控制器的 `turnSpeed`，并换算为手臂动力学使用的 rad/s。各方案转向输入不同，统一的是速度与角加速度上限。真实转头继续由头显追踪控制。

### 倾身辅助

参数位于 `IntentionalHeadMotionFlightController`：

- `leanDeadzone` 默认 **0.025m**：中立姿态附近的前后晃动不触发加速。
- `fullLeanDistance` 默认 **0.18m**：沿摇杆前后方向倾身达到此距离时获得完整加速输入；死区到此距离之间逐渐增加。
- `leanSmoothingTime` 默认 **0.08s**：倾身信号平滑时间。

例如左摇杆后拉时，前倾不会产生后退加速，必须后倾。速度和加速度由切换器的公共设置提供，单独修改控制器上的同名速度参数会在运行时被覆盖。

### 划手巡航

参数位于 `SwimFlightControllerV2 > Cruise Settings`，以下为代码默认值：

| 参数 | 默认值 | 作用 |
| --- | --- | --- |
| `minimumStrokeDistance` | 0.08m | 有效划动的最小距离，排除短促抖动 |
| `startSpeed` / `endSpeed` | 0.35 / 0.12m/s | 划动开始和结束的速度阈值 |
| `recoveryTime` | 0.08s | 收手回到回收区后，重新允许划动的等待时间 |
| `minimumStrokeInterval` | 0.35s | 两次速度增量的最短间隔 |
| `distanceToSpeed` | 2/s | 有效划动距离到目标速度增量的映射系数 |
| `maximumStrokeIncrement` | 0.45m/s | 单次划动最多增加的目标速度 |
| `maximumHorizontalSpeed` | 2m/s | 水平巡航限速 |
| `maximumVerticalSpeed` | 0.8m/s | 竖直巡航限速 |
| `maximumCombinedSpeed` | 2.1m/s | 水平与竖直合成后的限速 |
| `acceleration` / `deceleration` | 0.8 / 1.5m/s² | 实际速度追踪目标速度的加减速上限 |
| `brakeRate` | 1.2m/s² | 按住刹车时目标速度的降低速率 |

一次划动结算后必须先收手，才能触发下一次。同步双手动作按较大的动作量结算，不把两手增量直接相加。

单次划动提速不足时调整 `distanceToSpeed` 和 `maximumStrokeIncrement`；最高速度过快时降低对应限速；刹车距离过长时检查 `brakeRate` 和 `deceleration`。巡航平移使用独立的 `Cruise Settings`，公共转向设置仍然生效。

## 检查与打包

Unity 菜单 `SwimFlight/Validate turn and directional lean` 检查前后倾与摇杆方向匹配、倾身死区、前后对称性，以及公共转向限速和角加速度。

`SwimFlight/Validate stroke cruise` 检查巡航行为，详细报告写入 `build/stroke-cruise-validation.txt`。头显实测重点检查转向舒适度、后倾后退、反向刹停和巡航停止距离。

`SwimFlight/Build flightvr APK` 构建主场景，输出 `build/flightvr.apk`，包名为 `com.makelo.flightvr`。构建入口是 `Assets/Editor/BatchBuild.cs`。
