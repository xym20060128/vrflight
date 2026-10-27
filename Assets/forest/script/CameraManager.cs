//using UnityEngine;
//using UnityEngine.UI;

//public class CameraSwitchManager : MonoBehaviour
//{
//    [Header("直升机相机")]
//    public Camera helicopterCam;
//    [Header("灭火喷水相机")]
//    public Camera fireCam;
//    [Header("灭火器移动脚本组件")]
//    public FireCameraMove fireMoveCtrl;

//    [Header("火灾传送点")]
//    public Transform fireSpawnPoint;
//    [Header("呼叫灭火按钮")]
//    public Button callFireBtn;

//    private bool isGroundFireMode = false;

//    void Start()
//    {
//        if (helicopterCam == null || fireCam == null || fireMoveCtrl == null)
//        {
//            Debug.LogError("直升机相机、灭火相机、FireCameraMove 必须全部赋值！");
//            return;
//        }

//        helicopterCam.enabled = true;
//        fireCam.enabled = false;
//        fireMoveCtrl.SetActiveControl(false);

//        helicopterCam.depth = 0;
//        fireCam.depth = 1;

//        if (callFireBtn != null)
//            callFireBtn.onClick.AddListener(SwitchToFireCamera);
//    }

//    public void SwitchToFireCamera()
//    {
//        if (fireSpawnPoint == null) return;

//        fireCam.transform.SetPositionAndRotation(fireSpawnPoint.position, fireSpawnPoint.rotation);

//        helicopterCam.enabled = false;
//        fireCam.enabled = true;
//        fireMoveCtrl.SetActiveControl(true);
//        isGroundFireMode = true;
//    }

//    void Update()
//    {
//        if (Input.GetKeyDown(KeyCode.Escape) && isGroundFireMode)
//        {
//            // 1. 关闭操控权限，停止移动逻辑
//            fireMoveCtrl.SetActiveControl(false);

//            // 2. 强制固定消防相机当前位置与旋转，杜绝后续漂移
//            Transform fireTf = fireCam.transform;
//            fireTf.position = fireTf.position;
//            fireTf.rotation = fireTf.rotation;

//            fireCam.enabled = false;
//            helicopterCam.enabled = true;
//            isGroundFireMode = false;
//        }
//    }
//}

using UnityEngine;
using UnityEngine.UI;

public class CameraSwitchManager : MonoBehaviour
{
    [Header("直升机相机 父物体（拖相机所在GameObject）")]
    public GameObject helicopterCamObj;
    [Header("灭火喷水相机 父物体（拖相机所在GameObject）")]
    public GameObject fireCamObj;
    [Header("灭火器移动脚本组件")]
    public FireCameraMove fireMoveCtrl;

    [Header("火灾传送点")]
    public Transform fireSpawnPoint;
    [Header("呼叫灭火按钮")]
    public Button callFireBtn;

    private bool isGroundFireMode = false;

    void Start()
    {
        if (helicopterCamObj == null || fireCamObj == null || fireMoveCtrl == null)
        {
            Debug.LogError("直升机相机物体、灭火相机物体、FireCameraMove 必须全部赋值！");
            return;
        }

        // 初始：直升机开启，灭火相机直接取消激活
        helicopterCamObj.SetActive(true);
        fireCamObj.SetActive(false);
        fireMoveCtrl.SetActiveControl(false);

        if (callFireBtn != null)
            callFireBtn.onClick.AddListener(SwitchToFireCamera);
    }

    public void SwitchToFireCamera()
    {
        if (fireSpawnPoint == null) return;

        // 移动灭火相机到出生点
        fireCamObj.transform.SetPositionAndRotation(fireSpawnPoint.position, fireSpawnPoint.rotation);

        // 切换激活状态：灭火相机打开，直升机相机直接取消勾选(SetActive=false)
        helicopterCamObj.SetActive(false);
        fireCamObj.SetActive(true);
        fireMoveCtrl.SetActiveControl(true);
        isGroundFireMode = true;
    }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.Escape) && isGroundFireMode)
        {
            // 关闭水枪操控
            fireMoveCtrl.SetActiveControl(false);

            // ESC切回直升机：灭火相机物体直接取消激活，直升机开启
            fireCamObj.SetActive(false);
            helicopterCamObj.SetActive(true);
            isGroundFireMode = false;
        }
    }
}