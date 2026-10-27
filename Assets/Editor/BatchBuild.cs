using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>
/// 命令行打包入口 —— 生成 Pico/Android 原生 APK (不走串流)。
///
/// 为什么需要这个文件:
///   Unity 没有内置的"命令行打包"开关, 必须用 -executeMethod 指定一个编辑器方法。
///   另外本工程的 EditorBuildSettings 场景列表是空的 (m_Scenes: []),
///   所以这里**显式指定场景**, 不依赖 Build Settings 窗口里勾了什么。
///
/// 用法:
///   Unity.exe -quit -batchmode -projectPath D:\makelo\test \
///             -executeMethod BatchBuild.BuildAndroid \
///             -logFile D:\makelo\test\build\build.log
///
/// 打包前请**先关闭已经打开本工程的 Unity 编辑器**, 否则批处理会因工程被占用而失败。
/// </summary>
public static class BatchBuild
{
    private const string ScenePath = "Assets/JapaneseCity/Scenes/JapaneseCity_Demo.unity";
    private const string OutputApk = "build/PicoTest.apk";

    [MenuItem("SwimFlight/打包 Android APK (Pico 原生)")]
    public static void BuildAndroid()
    {
        BuildAndroidTo(OutputApk);
    }

    [MenuItem("SwimFlight/Build flightvr APK")]
    public static void BuildFlightVR()
    {
        PlayerSettings.productName = "flightvr";
        PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Android, "com.makelo.flightvr");
        PlayerSettings.bundleVersion = "2026.10.27.2";
        PlayerSettings.Android.bundleVersionCode = 20261028;
        AssetDatabase.SaveAssets();
        BuildAndroidTo("build/flightvr.apk");
    }

    public static void BuildAndroidIndependent()
    {
        string previousName = PlayerSettings.productName;
        string previousId = PlayerSettings.GetApplicationIdentifier(BuildTargetGroup.Android);
        string previousVersion = PlayerSettings.bundleVersion;
        int previousCode = PlayerSettings.Android.bundleVersionCode;
        try
        {
            PlayerSettings.productName = "2026.10.11";
            PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Android, "com.makelo.swimflight.v20261011");
            PlayerSettings.bundleVersion = "2026.10.11";
            PlayerSettings.Android.bundleVersionCode = 20261011;
            BuildAndroidTo("build/2026.10.11.apk");
        }
        finally
        {
            PlayerSettings.productName = previousName;
            PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Android, previousId);
            PlayerSettings.bundleVersion = previousVersion;
            PlayerSettings.Android.bundleVersionCode = previousCode;
            AssetDatabase.SaveAssets();
        }
    }

    private static void BuildAndroidTo(string apkPath)
    {
        string output = Path.GetFullPath(apkPath);
        Directory.CreateDirectory(Path.GetDirectoryName(output));

        if (!File.Exists(ScenePath))
        {
            Fail($"场景不存在: {ScenePath}");
            return;
        }

        if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
        {
            Debug.Log("[BatchBuild] 切换 Build Target -> Android");
            if (!EditorUserBuildSettings.SwitchActiveBuildTarget(
                    BuildTargetGroup.Android, BuildTarget.Android))
            {
                Fail("切换 Android 构建目标失败");
                return;
            }
        }

        Debug.Log("[BatchBuild] ===== 打包参数核对 =====");
        Debug.Log($"[BatchBuild] 场景       : {ScenePath}");
        Debug.Log($"[BatchBuild] 输出       : {output}");
        Debug.Log($"[BatchBuild] 包名       : "
                  + PlayerSettings.GetApplicationIdentifier(BuildTargetGroup.Android));
        Debug.Log($"[BatchBuild] 脚本后端   : "
                  + PlayerSettings.GetScriptingBackend(BuildTargetGroup.Android)
                  + " (IL2CPP=1)");
        Debug.Log($"[BatchBuild] 目标架构   : {PlayerSettings.Android.targetArchitectures}"
                  + " (ARM64=2)");
        Debug.Log($"[BatchBuild] minSdk     : {PlayerSettings.Android.minSdkVersion}");
        Debug.Log("[BatchBuild] XR loader  : 应为 PXR_Loader (Assets/XR/Loaders/PXR_Loader.asset)。"
                  + "若是 Live Preview loader, 打出来的包会尝试连 PC 串流而不是独立运行。");
        Debug.Log("[BatchBuild] ========================");

        var options = new BuildPlayerOptions
        {
            scenes = new[] { ScenePath },
            locationPathName = output,
            target = BuildTarget.Android,
            options = BuildOptions.None,
        };

        BuildReport report = BuildPipeline.BuildPlayer(options);
        BuildSummary summary = report.summary;

        if (summary.result == BuildResult.Succeeded)
        {
            Debug.Log($"[BatchBuild] 打包成功: {output} "
                      + $"({summary.totalSize / (1024f * 1024f):F1} MB, "
                      + $"耗时 {summary.totalTime.TotalMinutes:F1} 分钟)");
        }
        else
        {
            Debug.LogError($"[BatchBuild] 打包失败: {summary.result}, "
                           + $"错误 {summary.totalErrors} 个。详见上面的日志。");
            Fail($"Android build failed: {summary.result}, errors: {summary.totalErrors}");
        }
    }

    private static void Fail(string message)
    {
        Debug.LogError("[BatchBuild] " + message);
        // Let the independent build's finally restore the original app identity before quitting.
        throw new System.InvalidOperationException(message);
    }
}
