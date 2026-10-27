using UnityEngine;
using UnityEngine.UI;

public class UITogglePanel : MonoBehaviour
{
    [Tooltip("需要控制显示隐藏的面板/UI物体")]
    public GameObject targetUI;
    [Tooltip("切换UI的快捷键")]
    public KeyCode toggleKey = KeyCode.Tab;
    private bool uiIsShow = true;

    void Start()
    {
        if (targetUI != null)
            targetUI.SetActive(uiIsShow);
    }

    void Update()
    {
        if (Input.GetKeyDown(toggleKey))
        {
            ToggleUI();
        }
    }

    public void ToggleUI()
    {
        // 先翻转状态，再赋值，保证逻辑不会乱
        uiIsShow = !uiIsShow;
        if (targetUI != null)
        {
            targetUI.SetActive(uiIsShow);
        }
    }
}