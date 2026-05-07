using UnityEngine;
using UnityEngine.UI;
using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using UnityEngine.InputSystem;
using System.Collections.Generic;

[System.Serializable]
public class VRStateData
{
    public int divMode;
    public int colorIdx;
    public int leftNasal;
    public int leftTemporal;
    public int rightNasal;
    public int rightTemporal;
    public int q0, q1, q2, q3;
    public string uiText;
    public int currentEyeTarget;
    public bool isFlipMode;
    public bool isLeftEyeShown;
    public float leftFlipAdj;
    public float cfgScale;
    public float cfgDistance;
    public float cfgFlipInterval;
    public int cfgDefaultBright;
    public int cfgTargetBright;
    public int cfgBgBright;
    public string cfgColorOrder;
    public int cfgBrightStep;
    public float cfgSpotSize;
    public int cfgTargetShape;
    public float cfgCircleScale;
    public float cfgCircleDistance;
}

public class SimpleEyeTest : MonoBehaviour
{
    [Header("서버 설정")]
    public string serverIP = "10.2.52.8";
    public int serverPort = 12346;

    [Header("OVRCameraRig > LeftEyeAnchor / RightEyeAnchor")]
    public Camera leftCamera;
    public Camera rightCamera;

    [Header("UI")]
    public Text statusText;
    public GameObject crosshair;

    [Header("레이어 (Tags and Layers에서 30, 31번 추가 필요)")]
    public int leftLayer = 30;
    public int rightLayer = 31;

    private float quadScale = 2.0f;
    private float quadDistance = 2.0f;
    private float circleScale = 1.8f;
    private float circleDistance = 5.0f;
    private float flipInterval = 1.5f;
    private int defaultBright = 10;
    private int targetBright = 30;
    private int bgBright = 80;
    private int[] colorOrder = { 0, 1, 2, 3 };
    private int brightStep = 5;
    private float spotSize = 80f;

    private GameObject grpL_Whole, grpR_Whole;
    private Renderer[] rendL_Whole, rendR_Whole;

    private GameObject grpL_Split, grpR_Split;
    private Renderer[] rendL_Split, rendR_Split;

    private GameObject grpL_Cross, grpR_Cross;
    private Renderer[] rendL_Cross, rendR_Cross;

    private GameObject grpL_Circles, grpR_Circles;
    private Renderer[] rendL_Circles, rendR_Circles;
    public int cfgTargetShape = 0;

    private int leftNasal = 10, leftTemporal = 10;
    private int rightNasal = 10, rightTemporal = 10;
    private int[] quadBright = { 10, 10, 10, 10 };

    private enum AppState { ModeSelection, Testing }
    private AppState appState = AppState.ModeSelection;

    private int divisionMode = 2;
    private int currentEyeTarget = 2;
    private int currentColorIndex = 0;
    private int adjustMode = 0;

    private Color[] masterColors = { Color.red, Color.green, Color.blue, Color.white };
    private Color[] activeColors;

    private string[] targetNames = { "왼쪽 눈", "오른쪽 눈", "양쪽 눈" };
    private string[] quadNames = { "우상단", "우하단", "좌상단", "좌하단" };

    private bool isFlipMode = false;
    private float nextFlipTime = 0f;
    private bool isLeftEyeShown = true;
    private float leftFlipAdj = 1.0f;
    private int leftMask, rightMask;

    private bool prevA, prevB, prevT, prevG;

    private ClientWebSocket ws;
    private ConcurrentQueue<string> cmdQueue = new ConcurrentQueue<string>();
    private string lastStatus = "";
    private bool registered, sending;
    private bool pendingStateSend;
    private float lastSendTime;

    void Start()
    {
        defaultBright = 10;
        targetBright = 30;
        bgBright = 80;

        leftNasal = 10; leftTemporal = 10; rightNasal = 10; rightTemporal = 10;
        for (int i = 0; i < 4; i++) quadBright[i] = 10;

        leftMask = (1 << leftLayer) | (1 << 0) | (1 << 5);
        rightMask = (1 << rightLayer) | (1 << 0) | (1 << 5);

        // ============================================================
        // ★★★ 원본(잘 작동하던 코드) 그대로 복구 ★★★
        // 자식 막대에 작은 절대 크기를 박고, 부모 십자가에는 비율만 적용
        // ============================================================
        if (crosshair == null)
        {
            crosshair = new GameObject("AutoCrosshair");
            crosshair.transform.SetParent(this.transform);

            // 가로 막대 (원본 그대로 - 매우 작은 절대 크기)
            var h = GameObject.CreatePrimitive(PrimitiveType.Quad);
            h.transform.SetParent(crosshair.transform);
            h.transform.localPosition = Vector3.zero;
            h.transform.localScale = new Vector3(0.05f, 0.005f, 1f);
            Destroy(h.GetComponent<Collider>());

            // 세로 막대 (원본 그대로)
            var v = GameObject.CreatePrimitive(PrimitiveType.Quad);
            v.transform.SetParent(crosshair.transform);
            v.transform.localPosition = Vector3.zero;
            v.transform.localScale = new Vector3(0.005f, 0.05f, 1f);
            Destroy(v.GetComponent<Collider>());

            Material crossMat = new Material(Shader.Find("Unlit/Color"));
            crossMat.color = Color.white;
            h.GetComponent<Renderer>().material = crossMat;
            v.GetComponent<Renderer>().material = crossMat;

            SetLayerAll(crosshair, 0);
        }

        crosshair.SetActive(false);

        var center = GameObject.Find("CenterEyeAnchor");
        if (center != null)
        {
            var cam = center.GetComponent<Camera>();
            if (cam != null) cam.enabled = false;
        }

        RebuildColors();

        InitCamera(leftCamera, leftMask);
        InitCamera(rightCamera, rightMask);

        CreateAllQuads();
        HideAllGroups();
        ResetBrightness();

        lastStatus = "서버 접속 시도 중...";
        if (statusText != null) statusText.text = lastStatus;

        DiscoverAndConnectLoop();
    }

    async void DiscoverAndConnectLoop()
    {
        while (true)
        {
            if (ws != null && ws.State == WebSocketState.Open)
            {
                await Task.Delay(5000);
                continue;
            }

            UpdateStatusInVR("서버 연결 시도 중...");
            await ConnectServer();

            if (ws == null || ws.State != WebSocketState.Open)
            {
                UpdateStatusInVR("서버 탐색 중 (UDP)...");
                await DiscoverServer();
                await ConnectServer();
            }

            if (ws == null || ws.State != WebSocketState.Open)
            {
                UpdateStatusInVR("연결 실패 (3초 후 재시도)");
                await Task.Delay(3000);
            }
            else
            {
                UpdateStatusInVR("서버 연결 성공!");
                await Task.Delay(5000);
            }
        }
    }

    void UpdateStatusInVR(string msg)
    {
        lastStatus = msg;
        if (statusText != null) statusText.text = msg;
    }

    void InitCamera(Camera cam, int mask)
    {
        if (cam == null) return;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = Color.black;
        cam.cullingMask = mask;
        cam.enabled = true;
    }

    void RebuildColors()
    {
        activeColors = new Color[colorOrder.Length];
        for (int i = 0; i < colorOrder.Length; i++)
            activeColors[i] = masterColors[Mathf.Clamp(colorOrder[i], 0, 3)];
    }

    void ResetBrightness()
    {
        int d = defaultBright;
        leftNasal = d; leftTemporal = d; rightNasal = d; rightTemporal = d;
        for (int i = 0; i < 4; i++) quadBright[i] = d;
    }

    void CreateAllQuads()
    {
        grpL_Whole = MakeGroup("L_Whole", leftCamera, leftLayer, 1, out rendL_Whole);
        grpR_Whole = MakeGroup("R_Whole", rightCamera, rightLayer, 1, out rendR_Whole);

        grpL_Split = MakeGroup("L_Split", leftCamera, leftLayer, 2, out rendL_Split);
        grpR_Split = MakeGroup("R_Split", rightCamera, rightLayer, 2, out rendR_Split);

        grpL_Cross = MakeGroup("L_Cross", leftCamera, leftLayer, 4, out rendL_Cross);
        grpR_Cross = MakeGroup("R_Cross", rightCamera, rightLayer, 4, out rendR_Cross);

        grpL_Circles = MakeCircleGrid("L_Circles", leftCamera, leftLayer, out rendL_Circles);
        grpR_Circles = MakeCircleGrid("R_Circles", rightCamera, rightLayer, out rendR_Circles);
    }

    GameObject MakeGroup(string name, Camera parent, int layer, int count, out Renderer[] rends)
    {
        rends = new Renderer[count];
        var grp = new GameObject(name);

        if (parent != null)
        {
            grp.transform.parent = parent.transform;
            grp.transform.localPosition = new Vector3(0, 0, quadDistance);
            grp.transform.localEulerAngles = Vector3.zero;
        }
        grp.layer = layer;

        for (int i = 0; i < count; i++)
        {
            var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
            q.name = $"Q{i}";
            q.layer = layer;
            q.transform.SetParent(grp.transform);
            q.transform.localRotation = Quaternion.identity;

            var r = q.GetComponent<Renderer>();
            var sh = Shader.Find("Unlit/Color") ?? Shader.Find("Sprites/Default");
            r.material = new Material(sh);
            r.material.color = Color.black;

            Destroy(q.GetComponent<Collider>());
            rends[i] = r;
        }

        SetLayerAll(grp, layer);
        grp.SetActive(false);
        return grp;
    }

    GameObject MakeCircleGrid(string name, Camera parent, int layer, out Renderer[] rends)
    {
        int count = 256;
        rends = new Renderer[count];
        var grp = new GameObject(name);

        if (parent != null)
        {
            grp.transform.parent = parent.transform;
            grp.transform.localPosition = new Vector3(0, 0, circleDistance);
            grp.transform.localEulerAngles = Vector3.zero;
        }
        grp.layer = layer;

        for (int i = 0; i < count; i++)
        {
            var q = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            q.name = $"S{i}";
            q.layer = layer;
            q.transform.SetParent(grp.transform);
            q.transform.localRotation = Quaternion.identity;

            var r = q.GetComponent<Renderer>();
            var sh = Shader.Find("Unlit/Color") ?? Shader.Find("Sprites/Default");
            r.material = new Material(sh);
            r.material.color = Color.black;

            Destroy(q.GetComponent<Collider>());
            rends[i] = r;
        }

        SetLayerAll(grp, layer);
        grp.SetActive(false);
        return grp;
    }

    void UpdateDistances()
    {
        var normalGroups = new[] { grpL_Whole, grpR_Whole, grpL_Split, grpR_Split,
                                   grpL_Cross, grpR_Cross };
        foreach (var g in normalGroups)
            if (g != null) g.transform.localPosition = new Vector3(0, 0, quadDistance);

        var circleGroups = new[] { grpL_Circles, grpR_Circles };
        foreach (var g in circleGroups)
            if (g != null) g.transform.localPosition = new Vector3(0, 0, circleDistance);
    }

    void HideAllGroups()
    {
        var all = new[] { grpL_Whole, grpR_Whole, grpL_Split, grpR_Split,
                          grpL_Cross, grpR_Cross,
                          grpL_Circles, grpR_Circles };
        foreach (var g in all)
            if (g != null) g.SetActive(false);
    }

    void ShowActiveGroups()
    {
        HideAllGroups();

        if (divisionMode == 2)
        {
            if (currentEyeTarget == 2)
            {
                if (grpL_Whole) grpL_Whole.SetActive(true);
                if (grpR_Whole) grpR_Whole.SetActive(true);
            }
            else
            {
                if (cfgTargetShape == 1)
                {
                    if (grpL_Circles) grpL_Circles.SetActive(true);
                    if (grpR_Circles) grpR_Circles.SetActive(true);
                }
                else
                {
                    if (grpL_Split) grpL_Split.SetActive(true);
                    if (grpR_Split) grpR_Split.SetActive(true);
                }
            }
        }
        else if (divisionMode == 4)
        {
            if (cfgTargetShape == 1)
            {
                if (grpL_Circles) grpL_Circles.SetActive(true);
                if (grpR_Circles) grpR_Circles.SetActive(true);
            }
            else
            {
                if (grpL_Cross) grpL_Cross.SetActive(true);
                if (grpR_Cross) grpR_Cross.SetActive(true);
            }
        }
    }

    void LayoutAll()
    {
        float s = quadScale;
        LayoutSingle(rendL_Whole, s);
        LayoutSingle(rendR_Whole, s);
        Layout2Split(rendL_Split, s, true);
        Layout2Split(rendR_Split, s, false);
        LayoutCross4(rendL_Cross, s);
        LayoutCross4(rendR_Cross, s);

        LayoutCircleGrid(rendL_Circles, circleScale);
        LayoutCircleGrid(rendR_Circles, circleScale);

        // ★★★ 원본 그대로: scaleFactor = baseScale / 1.8f ★★★
        // 일반 모드: quadScale 기준
        // 원 모드: circleScale 기준
        if (crosshair != null)
        {
            float baseScale = (cfgTargetShape == 1) ? circleScale : quadScale;
            float scaleFactor = baseScale / 1.8f;
            crosshair.transform.localScale = new Vector3(scaleFactor, scaleFactor, 1f);
        }
    }

    void LayoutSingle(Renderer[] q, float s)
    {
        if (q == null || q.Length < 1) return;
        q[0].transform.localPosition = Vector3.zero;
        q[0].transform.localScale = new Vector3(2 * s, 2 * s, 1);
    }

    void Layout2Split(Renderer[] q, float s, bool isLeft)
    {
        if (q == null || q.Length < 2) return;
        float nasalX = isLeft ? s / 2f : -s / 2f;
        float tempX = isLeft ? -s / 2f : s / 2f;

        q[0].transform.localPosition = new Vector3(nasalX, 0, 0);
        q[0].transform.localScale = new Vector3(s, 2 * s, 1);
        q[1].transform.localPosition = new Vector3(tempX, 0, 0);
        q[1].transform.localScale = new Vector3(s, 2 * s, 1);
    }

    /// <summary>
    /// ★★★ 원본 그대로 복구 ★★★
    /// z=0.001로 Sphere를 납작하게 만들어 원판처럼 보이게 함
    /// </summary>
    void LayoutCircleGrid(Renderer[] q, float s)
    {
        if (q == null || q.Length < 256) return;

        float cell = s / 8f;
        float circleCellScale = cell * (spotSize / 100f);

        for (int i = 0; i < 256; i++)
        {
            int x_idx = i % 16;
            int y_idx = i / 16;

            float px = -s + (cell / 2f) + (x_idx * cell);
            float py = -s + (cell / 2f) + (y_idx * cell);

            q[i].transform.localPosition = new Vector3(px, py, 0);
            q[i].transform.localScale = new Vector3(circleCellScale, circleCellScale, 0.001f);
        }
    }

    void LayoutCross4(Renderer[] q, float s)
    {
        if (q == null || q.Length < 4) return;
        float o = s / 2f;
        q[0].transform.localPosition = new Vector3(o, o, 0);
        q[1].transform.localPosition = new Vector3(o, -o, 0);
        q[2].transform.localPosition = new Vector3(-o, o, 0);
        q[3].transform.localPosition = new Vector3(-o, -o, 0);
        for (int i = 0; i < 4; i++)
            q[i].transform.localScale = new Vector3(s, s, 1);
    }

    void Blackout(Camera cam, bool black, int mask)
    {
        if (cam == null) return;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = Color.black;
        cam.cullingMask = black ? 0 : mask;
    }

    void Apply()
    {
        ShowActiveGroups();
        LayoutAll();

        bool leftOn = (currentEyeTarget == 0 || currentEyeTarget == 2);
        bool rightOn = (currentEyeTarget == 1 || currentEyeTarget == 2);

        if (isFlipMode && divisionMode == 2 && currentEyeTarget == 2)
        {
            leftOn = isLeftEyeShown;
            rightOn = !isLeftEyeShown;
        }

        Blackout(leftCamera, !leftOn, leftMask);
        Blackout(rightCamera, !rightOn, rightMask);

        Color bc = (activeColors != null && currentColorIndex < activeColors.Length)
            ? activeColors[currentColorIndex] : Color.white;

        if (cfgTargetShape == 1 && (divisionMode == 4 || (divisionMode == 2 && currentEyeTarget != 2)))
        {
            ColorCircles(rendL_Circles, bc, leftOn, true);
            ColorCircles(rendR_Circles, bc, rightOn, false);
        }
        else if (divisionMode == 2)
        {
            if (currentEyeTarget == 2)
            {
                SetColor(rendL_Whole, 0, leftNasal, bc, leftOn);
                SetColor(rendR_Whole, 0, rightNasal, bc, rightOn);
            }
            else
            {
                SetColor(rendL_Split, 0, leftNasal, bc, leftOn);
                SetColor(rendL_Split, 1, leftTemporal, bc, leftOn);
                SetColor(rendR_Split, 0, rightNasal, bc, rightOn);
                SetColor(rendR_Split, 1, rightTemporal, bc, rightOn);
            }
        }
        else if (divisionMode == 4)
        {
            for (int i = 0; i < 4; i++)
            {
                SetColor(rendL_Cross, i, quadBright[i], bc, leftOn);
                SetColor(rendR_Cross, i, quadBright[i], bc, rightOn);
            }
        }

        // ============================================================
        // ★★★ 원본 동작 그대로 복구 ★★★
        // 단안 검사일 때만 십자가 표시
        // 부모 그룹의 자식으로 편입하여 quad 앞(-0.02f)에 배치
        // ============================================================
        if (crosshair != null)
        {
            bool showCross = (appState == AppState.Testing) &&
                             (divisionMode == 2 || divisionMode == 4) &&
                             (currentEyeTarget != 2);
            crosshair.SetActive(showCross);

            if (showCross)
            {
                GameObject parentGrp = null;
                int activeLayer = 5;

                // 원 모드 우선 분기
                if (cfgTargetShape == 1)
                {
                    parentGrp = (currentEyeTarget == 1) ? grpR_Circles : grpL_Circles;
                    activeLayer = (currentEyeTarget == 1) ? rightLayer : leftLayer;
                }
                else if (divisionMode == 4)
                {
                    parentGrp = (currentEyeTarget == 1) ? grpR_Cross : grpL_Cross;
                    activeLayer = (currentEyeTarget == 1) ? rightLayer : leftLayer;
                }
                else
                {
                    parentGrp = (currentEyeTarget == 1) ? grpR_Split : grpL_Split;
                    activeLayer = (currentEyeTarget == 1) ? rightLayer : leftLayer;
                }

                if (parentGrp != null)
                {
                    crosshair.transform.SetParent(parentGrp.transform, false);
                    crosshair.transform.localPosition = new Vector3(0, 0, -0.2f);
                    crosshair.transform.localRotation = Quaternion.identity;
                    SetLayerAll(crosshair, activeLayer);
                }
            }
        }

        UpdateStatusText();
    }

    void ColorCircles(Renderer[] q, Color bc, bool isOn, bool isLeft)
    {
        if (q == null || q.Length < 256) return;
        for (int i = 0; i < 256; i++)
        {
            int x_idx = i % 16;
            int y_idx = i / 16;
            int bval = defaultBright;

            if (divisionMode == 2)
            {
                bool isRightHalf = (x_idx >= 8);
                if (isLeft) bval = isRightHalf ? leftNasal : leftTemporal;
                else bval = isRightHalf ? rightTemporal : rightNasal;
            }
            else if (divisionMode == 4)
            {
                if (x_idx >= 8 && y_idx >= 8) bval = quadBright[0];
                else if (x_idx >= 8 && y_idx < 8) bval = quadBright[1];
                else if (x_idx < 8 && y_idx >= 8) bval = quadBright[2];
                else bval = quadBright[3];
            }

            SetColor(q, i, bval, bc, isOn);
        }
    }

    void SetColor(Renderer[] q, int idx, int bright, Color bc, bool on)
    {
        if (q == null || idx >= q.Length || q[idx] == null) return;
        if (!on) { q[idx].material.color = Color.black; return; }

        float intensity = Mathf.Pow(bright / 100f, 2.2f);

        if (isFlipMode && q[idx].gameObject.layer == leftLayer)
        {
            intensity *= leftFlipAdj;
        }

        Color c = bc * intensity;
        c.a = 1f;
        q[idx].material.color = c;
    }

    void SwitchMode(int newDiv, int newEye = -1)
    {
        divisionMode = newDiv;
        if (newEye >= 0) currentEyeTarget = newEye;
        adjustMode = 0;
        ResetBrightness();
        Apply();
    }

    void SetAdjustMode(int m)
    {
        adjustMode = m;
        if (m == 0) return;

        int t = targetBright;
        int b = bgBright;

        if (divisionMode == 2)
        {
            if (currentEyeTarget == 2)
            {
                if (m == 1) { leftNasal = t; leftTemporal = t; rightNasal = b; rightTemporal = b; }
                else if (m == 2) { leftNasal = b; leftTemporal = b; rightNasal = t; rightTemporal = t; }
            }
            else
            {
                if (m == 1)
                {
                    leftNasal = t; rightNasal = t;
                    leftTemporal = b; rightTemporal = b;
                }
                else if (m == 2)
                {
                    leftNasal = b; rightNasal = b;
                    leftTemporal = t; rightTemporal = t;
                }
            }
        }
        else if (divisionMode == 3 || divisionMode == 4)
        {
            if (m > 0 && m <= 4)
            {
                for (int i = 0; i < 4; i++) quadBright[i] = b;
                quadBright[m - 1] = t;
            }
        }
    }

    void ChangeBrightness(int amt)
    {
        bool aL = (currentEyeTarget == 0 || currentEyeTarget == 2);
        bool aR = (currentEyeTarget == 1 || currentEyeTarget == 2);

        int effMode = adjustMode;
        if (isFlipMode && divisionMode == 2 && currentEyeTarget == 2 && adjustMode == 0)
        {
            effMode = isLeftEyeShown ? 1 : 2;
        }

        if (divisionMode == 2)
        {
            if (currentEyeTarget == 2)
            {
                if (effMode == 0 || effMode == 1)
                {
                    leftNasal = Cl(leftNasal + amt);
                    leftTemporal = leftNasal;
                }
                if (effMode == 0 || effMode == 2)
                {
                    rightNasal = Cl(rightNasal + amt);
                    rightTemporal = rightNasal;
                }
            }
            else
            {
                if (effMode == 0 || effMode == 1)
                {
                    if (aL) leftNasal = Cl(leftNasal + amt);
                    if (aR) rightNasal = Cl(rightNasal + amt);
                }
                if (effMode == 0 || effMode == 2)
                {
                    if (aL) leftTemporal = Cl(leftTemporal + amt);
                    if (aR) rightTemporal = Cl(rightTemporal + amt);
                }
            }
        }
        else if (divisionMode == 3 || divisionMode == 4)
        {
            if (adjustMode == 0)
            {
                for (int i = 0; i < 4; i++) quadBright[i] = Cl(quadBright[i] + amt);
            }
            else
            {
                quadBright[adjustMode - 1] = Cl(quadBright[adjustMode - 1] + amt);
            }
        }
    }

    int Cl(int v) => Mathf.Clamp(v, 0, 100);

    void Update()
    {
        while (cmdQueue.TryDequeue(out string cmd))
            ProcessCommand(cmd);

        if (isFlipMode && divisionMode == 2 && currentEyeTarget == 2 && Time.time >= nextFlipTime)
        {
            isLeftEyeShown = !isLeftEyeShown;
            nextFlipTime = Time.time + flipInterval;
            Apply();
        }

        HandleInput();

        if (pendingStateSend && !sending && (Time.time - lastSendTime) >= 0.1f)
        {
            SendState();
        }
    }

    void HandleInput()
    {
        var kb = Keyboard.current;

        bool cA = false, cB = false, cT = false, cG = false;
        Vector2 st = Vector2.zero;

        var devs = new List<UnityEngine.XR.InputDevice>();
        UnityEngine.XR.InputDevices.GetDevicesWithCharacteristics(
            UnityEngine.XR.InputDeviceCharacteristics.Right | UnityEngine.XR.InputDeviceCharacteristics.Controller, devs);

        if (devs.Count > 0)
        {
            var d = devs[0];
            d.TryGetFeatureValue(UnityEngine.XR.CommonUsages.primaryButton, out cA);
            d.TryGetFeatureValue(UnityEngine.XR.CommonUsages.secondaryButton, out cB);
            d.TryGetFeatureValue(UnityEngine.XR.CommonUsages.triggerButton, out cT);
            d.TryGetFeatureValue(UnityEngine.XR.CommonUsages.gripButton, out cG);
            d.TryGetFeatureValue(UnityEngine.XR.CommonUsages.primary2DAxis, out st);
        }

        bool bA = cA && !prevA, bB = cB && !prevB, bT = cT && !prevT, bG = cG && !prevG;
        prevA = cA; prevB = cB; prevT = cT; prevG = cG;

        if (kb == null) kb = InputSystem.GetDevice<Keyboard>();

        if (appState == AppState.ModeSelection)
        {
            bool kA = kb != null && (kb.enterKey.wasPressedThisFrame || kb.cKey.wasPressedThisFrame || kb.leftCtrlKey.wasPressedThisFrame);
            bool kB = kb != null && kb.spaceKey.wasPressedThisFrame;
            if (bA || kA) { SwitchMode(2, 2); StartTest(); }
            else if (bB || kB) { SwitchMode(3, 2); StartTest(); }
            return;
        }

        // 조이스틱 위/아래 = 밝기 +/- (그 외 버튼 기능은 사용자 요청으로 제거됨)
        if (st.y > 0.5f && Time.frameCount % 10 == 0) ProcessCommand("BRIGHT_UP");
        else if (st.y < -0.5f && Time.frameCount % 10 == 0) ProcessCommand("BRIGHT_DOWN");

        if (kb != null)
        {
            if (kb.upArrowKey.wasPressedThisFrame) ProcessCommand("BRIGHT_UP");
            if (kb.downArrowKey.wasPressedThisFrame) ProcessCommand("BRIGHT_DOWN");
        }
    }

    void StartTest()
    {
        appState = AppState.Testing;
        if (statusText != null) statusText.gameObject.SetActive(false);
        Apply();
    }

    void ProcessCommand(string cmd)
    {
        if (string.IsNullOrEmpty(cmd)) return;
        cmd = cmd.Trim();

        if (cmd.StartsWith("DEVICE_LIST:") || cmd.StartsWith("{")) return;

        if (cmd.StartsWith("SET_MODE_EYE:"))
        {
            var p = cmd.Split(':');
            if (p.Length == 3 && int.TryParse(p[1], out int d) && int.TryParse(p[2], out int e))
            {
                SwitchMode(d, e);
                StartTest();
            }
            return;
        }

        if (cmd == "MODE_2") { SwitchMode(2, 2); StartTest(); return; }
        if (cmd == "MODE_4") { SwitchMode(4, 2); StartTest(); return; }

        if (appState == AppState.ModeSelection) { SwitchMode(2, 2); StartTest(); }

        if (cmd.StartsWith("CFG:")) { ApplyCfg(cmd); return; }

        if (cmd.StartsWith("SET_LEFT_FLIP_ADJ:"))
        {
            var p = cmd.Split(':');
            if (p.Length == 2 && float.TryParse(p[1], out float v))
            {
                leftFlipAdj = Mathf.Clamp01(v / 100f);
            }
            Apply(); return;
        }

        if (cmd.StartsWith("SET_ADJUST_MODE:"))
        {
            var p = cmd.Split(':');
            if (p.Length == 2 && int.TryParse(p[1], out int m))
            {
                SetAdjustMode(m);
                Apply(); SendState();
            }
            return;
        }

        if (cmd.StartsWith("SET_RAPD_PRESET:"))
        {
            if (int.TryParse(cmd.Split(':')[1], out int mode))
            {
                SwitchMode(2, 2);
                if (mode == 1)
                {
                    leftNasal = leftTemporal = 30;
                    rightNasal = rightTemporal = 80;
                }
                else if (mode == 2)
                {
                    leftNasal = leftTemporal = 80;
                    rightNasal = rightTemporal = 30;
                }
                SetAdjustMode(mode);
                Apply(); SendState();
            }
            return;
        }

        if (cmd.StartsWith("SET_VAL:"))
        {
            var p = cmd.Split(':');
            if (p.Length == 3 && int.TryParse(p[2], out int parsed))
            {
                int v = Cl(parsed);
                switch (p[1])
                {
                    case "L_NASAL": leftNasal = v; break;
                    case "L_TEMP": leftTemporal = v; break;
                    case "R_NASAL": rightNasal = v; break;
                    case "R_TEMP": rightTemporal = v; break;
                    case "L_ALL": leftNasal = leftTemporal = v; break;
                    case "R_ALL": rightNasal = rightTemporal = v; break;
                    case "Q0": quadBright[0] = v; break;
                    case "Q1": quadBright[1] = v; break;
                    case "Q2": quadBright[2] = v; break;
                    case "Q3": quadBright[3] = v; break;
                }
            }
            Apply(); return;
        }

        if (cmd.StartsWith("SET_FLIP_INTERVAL:"))
        {
            var p = cmd.Split(':');
            if (p.Length == 2 && float.TryParse(p[1], out float v))
            {
                flipInterval = Mathf.Max(0.1f, v);
                nextFlipTime = Time.time + flipInterval;
            }
            return;
        }

        switch (cmd)
        {
            case "TOGGLE_FLIP":
                isFlipMode = !isFlipMode;
                nextFlipTime = Time.time + flipInterval;
                isLeftEyeShown = true;
                break;
            case "EYE_LEFT":
                currentEyeTarget = 0; break;
            case "EYE_RIGHT":
                currentEyeTarget = 1; break;
            case "EYE_BOTH":
                currentEyeTarget = 2; break;
            case "EYE_TARGET_TOGGLE":
                currentEyeTarget = (currentEyeTarget + 1) % 3; break;
            case "CHANGE_COLOR":
                currentColorIndex = (currentColorIndex + 1) % (activeColors?.Length ?? 4);
                break;
            case "CHANGE_TARGET":
                int max = (divisionMode == 2) ? 3 : 5;
                SetAdjustMode((adjustMode + 1) % max);
                break;
            case "BRIGHT_UP":
                ChangeBrightness(brightStep); break;
            case "BRIGHT_DOWN":
                ChangeBrightness(-brightStep); break;
            case "BRIGHT_UP_L":
                leftNasal = Cl(leftNasal + brightStep); leftTemporal = Cl(leftTemporal + brightStep); break;
            case "BRIGHT_DOWN_L":
                leftNasal = Cl(leftNasal - brightStep); leftTemporal = Cl(leftTemporal - brightStep); break;
            case "BRIGHT_UP_R":
                rightNasal = Cl(rightNasal + brightStep); rightTemporal = Cl(rightTemporal + brightStep); break;
            case "BRIGHT_DOWN_R":
                rightNasal = Cl(rightNasal - brightStep); rightTemporal = Cl(rightTemporal - brightStep); break;
        }
        Apply();
    }

    void ApplyCfg(string cmd)
    {
        var p = cmd.Split(':');
        if (p.Length < 3) return;

        switch (p[1])
        {
            case "SCALE":
            case "scale":
                if (float.TryParse(p[2], out float sc))
                { quadScale = Mathf.Max(0.5f, sc); LayoutAll(); }
                break;
            case "DISTANCE":
            case "distance":
                if (float.TryParse(p[2], out float di))
                { quadDistance = Mathf.Max(1f, di); UpdateDistances(); }
                break;
            case "CIRCLE_SCALE":
            case "circleScale":
                if (float.TryParse(p[2], out float csc))
                { circleScale = Mathf.Max(0.5f, csc); LayoutAll(); }
                break;
            case "CIRCLE_DISTANCE":
            case "circleDistance":
                if (float.TryParse(p[2], out float cdi))
                { circleDistance = Mathf.Max(1f, cdi); UpdateDistances(); }
                break;
            case "FLIP_INTERVAL":
            case "flipInterval":
                if (float.TryParse(p[2], out float fi))
                    flipInterval = Mathf.Max(0.1f, fi);
                break;
            case "DEFAULT_BRIGHT":
            case "defaultBright":
                if (int.TryParse(p[2], out int db))
                    defaultBright = Mathf.Clamp(db, 0, 100);
                break;
            case "TARGET_BRIGHT":
            case "targetBright":
                if (int.TryParse(p[2], out int tb))
                    targetBright = Mathf.Clamp(tb, 0, 100);
                break;
            case "BG_BRIGHT":
            case "bgBright":
                if (int.TryParse(p[2], out int bb))
                    bgBright = Mathf.Clamp(bb, 0, 100);
                break;
            case "COLOR_ORDER":
            case "colorOrder":
                var parts = p[2].Split(',');
                int[] order = new int[parts.Length];
                for (int i = 0; i < parts.Length; i++)
                    int.TryParse(parts[i].Trim(), out order[i]);
                colorOrder = order;
                RebuildColors();
                currentColorIndex = 0;
                break;
            case "BRIGHT_STEP":
            case "brightStep":
                if (int.TryParse(p[2], out int bs)) brightStep = Mathf.Clamp(bs, 1, 100);
                break;
            case "SPOT_SIZE":
            case "spotSize":
                if (float.TryParse(p[2], out float ss)) { spotSize = Mathf.Clamp(ss, 1f, 100f); LayoutAll(); }
                break;
            case "TARGET_SHAPE":
                if (int.TryParse(p[2], out int ts))
                {
                    cfgTargetShape = ts;
                    LayoutAll();
                    UpdateDistances();
                    Apply();
                }
                break;
        }
        Apply();
        SendState();
    }

    void UpdateStartScreenText()
    {
        lastStatus = "<b>[ 검사 대기 중 ]</b>\nA버튼/Enter: 양안 | B버튼/Space: 세로4분";
        if (statusText != null) statusText.text = lastStatus;
        SendState();
    }

    void UpdateStatusText()
    {
        if (appState == AppState.ModeSelection) return;

        string modeStr;
        if (divisionMode == 4)
            modeStr = adjustMode == 0 ? "전체 조절" : $"{quadNames[adjustMode - 1]} 조절";
        else
        {
            if (currentEyeTarget == 2)
                modeStr = adjustMode == 0 ? "양안 전체" : (adjustMode == 1 ? "왼눈 조절" : "오른눈 조절");
            else
                modeStr = adjustMode == 0 ? "양쪽 제어" : (adjustMode == 1 ? "코쪽 제어" : "귀쪽 제어");
        }

        string flip = isFlipMode ? " | [FLIP]" : "";
        lastStatus = $"<b>{modeStr}</b> | {targetNames[currentEyeTarget]}{flip}";
        if (statusText != null) statusText.text = lastStatus;
        SendState();
    }

    async Task DiscoverServer()
    {
        if (ws != null && ws.State == WebSocketState.Open) return;
        using (var udp = new UdpClient())
        {
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, 50002));
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
            {
                try
                {
                    var task = udp.ReceiveAsync();
                    if (await Task.WhenAny(task, Task.Delay(-1, cts.Token)) == task)
                    {
                        string msg = Encoding.UTF8.GetString((await task).Buffer);
                        if (msg.StartsWith("EYE_SERVER:"))
                        {
                            var s = msg.Split(':');
                            serverIP = s[1];
                            if (s.Length > 2) int.TryParse(s[2], out serverPort);
                        }
                    }
                }
                catch { }
            }
        }
    }

    async Task ConnectServer()
    {
        bool ok = await TryWs($"ws://127.0.0.1:{serverPort}/ws");
        if (!ok) ok = await TryWs($"ws://{serverIP}:{serverPort}/ws");
        if (!ok && serverPort != 80) ok = await TryWs($"ws://{serverIP}/vr/ws");

        if (ok)
        {
            try
            {
                await ws.SendAsync(Seg($"REG:{SystemInfo.deviceUniqueIdentifier}"),
                    WebSocketMessageType.Text, true, CancellationToken.None);
                registered = true;
                lastSendTime = Time.time;
                UpdateStartScreenText();
                SendState();
                RecvLoop();
            }
            catch { }
        }
    }

    async Task<bool> TryWs(string url)
    {
        try
        {
            ws = new ClientWebSocket();
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1.5)))
            {
                await ws.ConnectAsync(new Uri(url), cts.Token);
                return ws.State == WebSocketState.Open;
            }
        }
        catch { return false; }
    }

    async void RecvLoop()
    {
        byte[] buf = new byte[2048];
        while (ws != null && ws.State == WebSocketState.Open)
        {
            try
            {
                var r = await ws.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None);
                if (r.MessageType == WebSocketMessageType.Text)
                {
                    string msg = Encoding.UTF8.GetString(buf, 0, r.Count);
                    if (!string.IsNullOrEmpty(msg))
                        cmdQueue.Enqueue(msg);
                }
                else if (r.MessageType == WebSocketMessageType.Close) break;
            }
            catch { break; }
        }
    }

    async void SendState()
    {
        if (!registered || ws == null || ws.State != WebSocketState.Open) return;

        if (sending || (Time.time - lastSendTime) < 0.1f)
        {
            pendingStateSend = true;
            return;
        }

        sending = true;
        lastSendTime = Time.time;
        pendingStateSend = false;

        try
        {
            var data = new VRStateData
            {
                divMode = divisionMode,
                colorIdx = currentColorIndex,
                leftNasal = leftNasal,
                leftTemporal = leftTemporal,
                rightNasal = rightNasal,
                rightTemporal = rightTemporal,
                q0 = quadBright[0],
                q1 = quadBright[1],
                q2 = quadBright[2],
                q3 = quadBright[3],
                uiText = lastStatus,
                currentEyeTarget = currentEyeTarget,
                isFlipMode = isFlipMode,
                isLeftEyeShown = isLeftEyeShown,
                leftFlipAdj = leftFlipAdj * 100f,
                cfgScale = quadScale,
                cfgDistance = quadDistance,
                cfgFlipInterval = flipInterval,
                cfgDefaultBright = defaultBright,
                cfgTargetBright = targetBright,
                cfgBgBright = bgBright,
                cfgColorOrder = string.Join(",", colorOrder),
                cfgBrightStep = brightStep,
                cfgSpotSize = spotSize,
                cfgTargetShape = cfgTargetShape,
                cfgCircleScale = circleScale,
                cfgCircleDistance = circleDistance
            };

            string json = JsonUtility.ToJson(data);
            if (!string.IsNullOrEmpty(json))
                await ws.SendAsync(Seg(json), WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch { }
        finally { sending = false; }
    }

    ArraySegment<byte> Seg(string s) => new ArraySegment<byte>(Encoding.UTF8.GetBytes(s));

    async void OnDestroy()
    {
        if (ws != null && ws.State == WebSocketState.Open)
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None);
    }

    void SetLayerAll(GameObject obj, int layer)
    {
        if (obj == null) return;
        obj.layer = layer;
        foreach (Transform c in obj.transform)
            SetLayerAll(c.gameObject, layer);
    }
}