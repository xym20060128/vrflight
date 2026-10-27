using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

// Keep the default UGUI shader available to UI created or repaired at runtime.
public sealed class HeadsetUIBuildPreparation : IPreprocessBuildWithReport
{
    public int callbackOrder => -100;

    public void OnPreprocessBuild(BuildReport report)
    {
        var shader = Shader.Find("UI/Default");
        if (shader == null) throw new BuildFailedException("Cannot find the UI/Default shader for headset UI.");
        var assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset");
        if (assets.Length == 0) throw new BuildFailedException("Cannot read GraphicsSettings.asset.");
        var settings = new SerializedObject(assets[0]);
        var shaders = settings.FindProperty("m_AlwaysIncludedShaders");
        for (int i = 0; i < shaders.arraySize; i++)
            if (shaders.GetArrayElementAtIndex(i).objectReferenceValue == shader) return;
        int index = shaders.arraySize;
        shaders.InsertArrayElementAtIndex(index);
        shaders.GetArrayElementAtIndex(index).objectReferenceValue = shader;
        settings.ApplyModifiedPropertiesWithoutUndo();
        AssetDatabase.SaveAssets();
    }
}
