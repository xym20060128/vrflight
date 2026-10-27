using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// 打包时剔除立体渲染（Multiview / Single Pass Instanced）的 shader 变体。
///
/// 为什么需要它：
///   内置渲染管线的 Surface Shader（NatureManufacture 的草/树等第三方 shader）在
///   STEREO_MULTIVIEW_ON 变体下会生成非法的顶点输入签名，报错：
///     "Non system-generated input signature parameter () cannot appear after a system generated value"
///   项目当前用的是 Multi Pass，这些变体运行时压根不会被用到，
///   但 Unity 依然会为它们做编译，于是打包直接失败。
///
///   顺带把变体总数砍掉一大半，能明显缓解 shader 编译器
///   "out of memory during compilation / while parsing" 的问题，打包也会快很多。
///
/// 生效条件：
///   仅当 Player Settings 的 Stereo Rendering Mode 为 Multi Pass 时才剔除。
///   以后若切回 Multiview / Single Pass Instanced，本脚本会自动停止剔除
///   （届时需要改用支持 Multiview 的 shader，否则同样的报错会回来）。
/// </summary>
class StereoShaderVariantStripper : IPreprocessShaders, IPreprocessBuildWithReport, IPostprocessBuildWithReport
{
    public int callbackOrder => 0;

    static int s_StrippedCount;
    static bool s_Active;

    public void OnPreprocessBuild(BuildReport report)
    {
        s_StrippedCount = 0;
        s_Active = PlayerSettings.stereoRenderingPath == StereoRenderingPath.MultiPass;

        Debug.Log(s_Active
            ? "[StereoShaderVariantStripper] Multi Pass —— 将剔除 STEREO_MULTIVIEW_ON / STEREO_INSTANCING_ON 变体。"
            : $"[StereoShaderVariantStripper] 当前 Stereo Rendering Mode = {PlayerSettings.stereoRenderingPath}，不剔除立体变体。");
    }

    public void OnProcessShader(Shader shader, ShaderSnippetData snippet, IList<ShaderCompilerData> data)
    {
        if (!s_Active) return;

        var multiview = new ShaderKeyword("STEREO_MULTIVIEW_ON");
        var instancing = new ShaderKeyword("STEREO_INSTANCING_ON");

        for (int i = data.Count - 1; i >= 0; i--)
        {
            var keywords = data[i].shaderKeywordSet;
            if (keywords.IsEnabled(multiview) || keywords.IsEnabled(instancing))
            {
                data.RemoveAt(i);
                s_StrippedCount++;
            }
        }
    }

    public void OnPostprocessBuild(BuildReport report)
    {
        if (s_Active)
            Debug.Log($"[StereoShaderVariantStripper] 共剔除 {s_StrippedCount} 个立体渲染 shader 变体。");
    }
}
