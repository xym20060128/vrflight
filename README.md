# vrflight

Unity / PICO VR 仿生飞行项目，支持持续划水、摇杆、前倾辅助和划手巡航四种控制方式。

## 环境与启动

- Unity **2022.3.62f3c1**，安装 Android Build Support、SDK/NDK 和 OpenJDK。
- PICO Unity Integration SDK **3.4.0**、PICO Unity Live Preview Plugin **1.0.5**。
- 两个 PICO 包目前在 `Packages/manifest.json` 中使用本机路径 `D:/pico-unity-process/…`。在另一台电脑打开之前，安装对应 SDK 并把这两项 `file:` 路径改为实际位置；不要只复制 `Library` 缓存。
- 打开 `Assets/JapaneseCity/Scenes/JapaneseCity_Demo.unity`。原场景仍默认进入旧划水模式。

## 操作

- 左手 **X** 或模式按钮切换控制方式。
- 划手巡航：先收手回到回收区，再向后划动，动作结束增加一次目标速度；停手保持巡航。反向划先减速，再通过后续动作后退。
- 巡航按住左手 **Y** 刹车。任意 **Trigger** 允许升降划动，松开后竖直巡航平滑停止。
- 手臂模式按住任意 **Grip** 停止平移并原地转向，松开后重新划动起飞。
- 右手 **A** 保留喷水；前倾模式右手 **B** 保留校准。

巡航参数位于 `SwimFlightControllerV2 > Cruise Settings`。初值：单次增量最多 0.45m/s，水平/竖直/合成限速 2/0.8/2.1m/s，加速/减速上限 0.8/1.5m/s²，刹车目标减速率 1.2m/s²。参数需头显实测调整。太慢调映射系数和单次增量，太快降低增量或限速，难停提高刹车速率和减速上限。

## 代码与验证

- `Assets/script/SwimFlight`：输入、速度估计、坐标转换、划手映射、巡航、动力学、碰撞和模式切换。
- `Assets/script/OpticFlow`：现有视觉反馈。
- `Assets/forest`：现有场景及消防功能。
- `Assets/Editor`：打包、场景配置和回归检查。

旧反作用力、空间门控和转向算法仍被保留模式调用，不能作为“无用旧文件”删除。

Unity 菜单 `SwimFlight/Validate stroke cruise` 运行确定性检查，报告写入 `build/stroke-cruise-validation.txt`。检查通过不代表真实 VR 手感已经验证。

Unity 菜单 `SwimFlight/Build flightvr APK` 生成 `build/flightvr.apk`。构建场景和包名由 `Assets/Editor/BatchBuild.cs` 指定。

## GitHub

提交 `Assets`（含 `.meta`）、`Packages`、`ProjectSettings` 和本仓库配置。缓存、APK、日志、本机配置及导入用 `.unitypackage` 已忽略；APK 可作为单独的发布附件。

二进制资源已配置 Git LFS，第一次暂存前运行 `git lfs install`。第三方素材和 SDK 继续遵循各自原有许可；本项目不替它们授予新的许可。
