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
    public int leftNasal, leftTemporal, rightNasal, rightTemporal;
    public int q0, q1, q2, q3;
    public int leftTop, leftBot, rightTop, rightBot;
    public string uiText;
    public int currentEyeTarget;
    public bool isFlipMode;
    public bool isLeftEyeShown;
}

public class VRController : MonoBehaviour
{
    [Header("서버 설정")]
    public string serverIP = "10.2.52.8";
    public int serverPort = 12346;

    [Header("OVR 2-카메라 정석 세팅 (OVRCameraRig의 Left/Right를 꽂으세요)")]
    public Camera leftCamera;
    public Camera rightCamera;
    public GameObject leftEyeGroup;
    public GameObject rightEyeGroup;

    private Renderer[] leftQuads = new Renderer[4];
    private Renderer[] rightQuads = new Renderer[4];

    [Header("UI 연결 (아무거나 1개만)")]
    public Text statusText;
    public GameObject crosshair;

    [Header("가로/세로 독립 크기(스케일) 설정")]
    [Tooltip("상자들의 총 가로 너비")] public float targetWidth = 3.6f;
    [Tooltip("상자들의 총 세로 높이")] public float targetHeight = 3.6f;
    [Tooltip("카메라에서 상자까지의 물리적 거리")] public float targetDistance = 5.0f;
    
    public int leftLayer = 30; // LeftEye 레이어
    public int rightLayer = 31; // RightEye 레이어

    private enum AppState { ModeSelection, Testing }
    private AppState currentState = AppState.ModeSelection;

    private int divisionMode = 2;
    private int leftNasal = 50, leftTemporal = 50, rightNasal = 50, rightTemporal = 50;
    private int[] quadBrightness = new int[] { 50, 50, 50, 50 };
    private int leftTop = 50, leftBot = 50, rightTop = 50, rightBot = 50;

    private int currentEyeTarget = 2;
    private int currentColorIndex = 0;
    private int adjustMode = 0;

    private Color[] baseColors = new Color[] { Color.red, Color.green, Color.blue, Color.white };
    private string[] targetNames = new string[] { "왼쪽 눈", "오른쪽 눈", "양쪽 눈" };
    private string[] binocularModes = new string[] { "양안 통째로", "왼쪽 통째로", "오른쪽 통째로" };
    private string[] quadNames = new string[] { "우상단", "우하단", "좌상단", "좌하단" };
    private string[] verticalGroupNames = new string[] { "1-3번 띠", "2-4번 띠", "1-4번 띠", "2-3번 띠" };

    private bool isFlipMode = false;
    private float flipInterval = 1.0f;
    private float nextFlipTime = 0f;
    private bool isLeftEyeShown = true;

    private bool prevBtnA = false, prevBtnB = false, prevTrigger = false, prevGrip = false;

    private ClientWebSocket websocket;
    private ConcurrentQueue<string> commandQueue = new ConcurrentQueue<string>();
    private string lastStatus = "";
    private bool isRegistered = false;
    private bool isSending = false;
    private float lastSendTime = 0f;
    private const float sendInterval = 0.1f;

    private Dictionary<Camera, int> originalCullingMasks = new Dictionary<Camera, int>();
    private Dictionary<Camera, CameraClearFlags> originalClearFlags = new Dictionary<Camera, CameraClearFlags>();

    async void Start()
    {
        if (leftEyeGroup != null) leftEyeGroup.SetActive(true);
        if (rightEyeGroup != null) rightEyeGroup.SetActive(true);
        if (crosshair != null) crosshair.SetActive(false);

        // 1. [OVRCameraRig 완벽 격리] 카메라 원본 세팅 저장 및 블랙아웃용 초기화
        if (leftCamera != null)
        {
            SaveOriginalSettings(leftCamera);
            leftCamera.clearFlags = CameraClearFlags.SolidColor;
            leftCamera.backgroundColor = Color.black;

            if (leftEyeGroup != null)
            {
                leftEyeGroup.transform.parent = leftCamera.transform; // 왼쪽 카메라에 종속
                leftEyeGroup.transform.localPosition = new Vector3(0, 0, targetDistance);
                leftEyeGroup.transform.localEulerAngles = Vector3.zero;
            }
        }

        if (rightCamera != null)
        {
            SaveOriginalSettings(rightCamera);
            rightCamera.clearFlags = CameraClearFlags.SolidColor;
            rightCamera.backgroundColor = Color.black;

            if (rightEyeGroup != null)
            {
                rightEyeGroup.transform.parent = rightCamera.transform; // 오른쪽 카메라에 종속
                rightEyeGroup.transform.localPosition = new Vector3(0, 0, targetDistance);
                rightEyeGroup.transform.localEulerAngles = Vector3.zero;
            }
        }

        // 2. 쿼드 세팅
        leftQuads = AutoCreateQuads(leftEyeGroup, leftLayer);
        rightQuads = AutoCreateQuads(rightEyeGroup, rightLayer);

        SetLayerRecursively(leftEyeGroup, leftLayer);
        SetLayerRecursively(rightEyeGroup, rightLayer);

        lastStatus = "서버 접속 시도 중...";
        UpdateStartScreenText();

        await DiscoverServerIP();
        await ConnectToServer();
    }

    void SaveOriginalSettings(Camera cam)
    {
        if (cam != null && !originalCullingMasks.ContainsKey(cam))
        {
            originalCullingMasks.Add(cam, cam.cullingMask);
            originalClearFlags.Add(cam, cam.clearFlags);
        }
    }

    void SetCameraBlackout(Camera cam, bool isBlackout)
    {
        if (cam == null) return;

        if (isBlackout)
        {
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
            cam.cullingMask = 0;
        }
        else
        {
            if (originalCullingMasks.ContainsKey(cam))
            {
                cam.clearFlags = originalClearFlags[cam];
                cam.cullingMask = originalCullingMasks[cam];
            }
        }
    }

    private Renderer[] AutoCreateQuads(GameObject group, int targetLayer)
    {
        if (group == null) return new Renderer[4];

        for (int c = group.transform.childCount - 1; c >= 0; c--)
            Destroy(group.transform.GetChild(c).gameObject);

        Renderer[] quads = new Renderer[4];
        for (int i = 0; i < 4; i++)
        {
            GameObject quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = "Q" + i;
            quad.layer = targetLayer;
            quad.transform.SetParent(group.transform);
            quad.transform.localRotation = Quaternion.identity;

            Renderer rend = quad.GetComponent<Renderer>();
            Shader unlit = Shader.Find("Unlit/Color");
            if (unlit == null) unlit = Shader.Find("Sprites/Default");
            Material mat = new Material(unlit);
            mat.color = Color.black;
            rend.material = mat;
            Destroy(quad.GetComponent<Collider>());

            quads[i] = rend;
        }
        return quads;
    }

    private void SetLayerRecursively(GameObject obj, int newLayer)
    {
        if (null == obj) return;
        obj.layer = newLayer;
        foreach (Transform child in obj.transform)
            SetLayerRecursively(child.gameObject, newLayer);
    }

    private async Task DiscoverServerIP()
    {
        if (websocket != null && websocket.State == WebSocketState.Open) return;
        using (UdpClient udpClient = new UdpClient())
        {
            udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, 50002));
            using (CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                try
                {
                    var receiveTask = udpClient.ReceiveAsync();
                    var completedTask = await Task.WhenAny(receiveTask, Task.Delay(-1, cts.Token));
                    if (completedTask == receiveTask)
                    {
                        var result = await receiveTask;
                        string message = Encoding.UTF8.GetString(result.Buffer);
                        if (message.StartsWith("EYE_SERVER:"))
                        {
                            string[] parts = message.Split(':');
                            serverIP = parts[1];
                            if (parts.Length > 2) int.TryParse(parts[2], out serverPort);
                        }
                    }
                }
                catch { }
            }
        }
    }

    private async Task ConnectToServer()
    {
        bool connected = await TryConnect($"ws://127.0.0.1:{serverPort}/ws");
        if (!connected) connected = await TryConnect($"ws://{serverIP}:{serverPort}/ws");
        if (!connected && serverPort != 80) connected = await TryConnect($"ws://{serverIP}/vr/ws");

        if (connected)
        {
            try
            {
                string deviceId = SystemInfo.deviceUniqueIdentifier;
                byte[] regMsg = Encoding.UTF8.GetBytes($"REG:{deviceId}");
                await websocket.SendAsync(new ArraySegment<byte>(regMsg), WebSocketMessageType.Text, true, CancellationToken.None);
                isRegistered = true;
                lastSendTime = Time.time;
                UpdateStartScreenText();
                SendStateToServer();
                ReceiveMessages();
            }
            catch { }
        }
    }

    private async Task<bool> TryConnect(string url)
    {
        try
        {
            websocket = new ClientWebSocket();
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
            {
                await websocket.ConnectAsync(new Uri(url), cts.Token);
                return websocket.State == WebSocketState.Open;
            }
        }
        catch { return false; }
    }

    private async void ReceiveMessages()
    {
        byte[] buffer = new byte[2048];
        while (websocket != null && websocket.State == WebSocketState.Open)
        {
            try
            {
                var result = await websocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Text)
                {
                    string message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    if (!string.IsNullOrEmpty(message)) commandQueue.Enqueue(message);
                }
                else if (result.MessageType == WebSocketMessageType.Close) break;
            }
            catch { break; }
        }
    }

    void Update()
    {
        while (commandQueue.TryDequeue(out string command)) ProcessCommand(command);

        if (isFlipMode && divisionMode == 2 && currentEyeTarget == 2)
        {
            if (Time.time >= nextFlipTime)
            {
                isLeftEyeShown = !isLeftEyeShown;
                nextFlipTime = Time.time + flipInterval;
                Apply();
            }
        }

        HandlePhysicalInput();
    }

    void HandlePhysicalInput()
    {
        var keyboard = Keyboard.current;
        var mouse = Mouse.current;

        bool currA = false, currB = false, currTrigger = false, currGrip = false;
        Vector2 stick = Vector2.zero;

        List<UnityEngine.XR.InputDevice> xrDevices = new List<UnityEngine.XR.InputDevice>();
        UnityEngine.XR.InputDevices.GetDevicesWithCharacteristics(
            UnityEngine.XR.InputDeviceCharacteristics.Right | UnityEngine.XR.InputDeviceCharacteristics.Controller, xrDevices);

        if (xrDevices.Count > 0)
        {
            var device = xrDevices[0];
            device.TryGetFeatureValue(UnityEngine.XR.CommonUsages.primaryButton, out currA);
            device.TryGetFeatureValue(UnityEngine.XR.CommonUsages.secondaryButton, out currB);
            device.TryGetFeatureValue(UnityEngine.XR.CommonUsages.triggerButton, out currTrigger);
            device.TryGetFeatureValue(UnityEngine.XR.CommonUsages.gripButton, out currGrip);
            device.TryGetFeatureValue(UnityEngine.XR.CommonUsages.primary2DAxis, out stick);
        }

        bool btnA = currA && !prevBtnA;
        bool btnB = currB && !prevBtnB;
        bool trigger = currTrigger && !prevTrigger;
        bool grip = currGrip && !prevGrip;

        prevBtnA = currA; prevBtnB = currB; prevTrigger = currTrigger; prevGrip = currGrip;

        if (keyboard == null) keyboard = InputSystem.GetDevice<Keyboard>();
        bool keyStart2 = keyboard != null && (keyboard.enterKey.wasPressedThisFrame || keyboard.cKey.wasPressedThisFrame || keyboard.leftCtrlKey.wasPressedThisFrame);
        bool keyStart4 = keyboard != null && keyboard.spaceKey.wasPressedThisFrame;

        if (currentState == AppState.ModeSelection)
        {
            if (btnA || keyStart2) { divisionMode = 2; StartTest(); }
            else if (btnB || keyStart4) { divisionMode = 3; currentEyeTarget = 2; StartTest(); }
            return;
        }

        if (btnA) ProcessCommand("CHANGE_COLOR");
        if (btnB) ProcessCommand("CHANGE_TARGET");
        if (trigger) ProcessCommand("TOGGLE_FLIP");
        if (grip) ProcessCommand("EYE_TARGET_TOGGLE");

        if (stick.y > 0.5f && Time.frameCount % 10 == 0) ProcessCommand("BRIGHT_UP");
        else if (stick.y < -0.5f && Time.frameCount % 10 == 0) ProcessCommand("BRIGHT_DOWN");

        if (stick.x < -0.7f && Time.frameCount % 20 == 0) ProcessCommand("EYE_LEFT");
        else if (stick.x > 0.7f && Time.frameCount % 20 == 0) ProcessCommand("EYE_RIGHT");

        if (keyboard != null)
        {
            if (keyboard.upArrowKey.wasPressedThisFrame) ProcessCommand("BRIGHT_UP");
            if (keyboard.downArrowKey.wasPressedThisFrame) ProcessCommand("BRIGHT_DOWN");
            if (keyboard.leftCtrlKey.wasPressedThisFrame) ProcessCommand("EYE_TARGET_TOGGLE");
            if (keyboard.spaceKey.wasPressedThisFrame) ProcessCommand("CHANGE_COLOR");
            if (keyboard.escapeKey.wasPressedThisFrame) ProcessCommand("CHANGE_TARGET");
        }

        if (mouse != null)
        {
            float scroll = mouse.scroll.y.ReadValue();
            if (scroll > 0.1f) ProcessCommand("BRIGHT_UP");
            else if (scroll < -0.1f) ProcessCommand("BRIGHT_DOWN");
        }
    }

    void StartTest()
    {
        currentState = AppState.Testing;
        if (statusText != null) statusText.gameObject.SetActive(false);
        if (crosshair != null) crosshair.SetActive(true);
        Apply();
    }

    void ProcessCommand(string cmd)
    {
        if (cmd.StartsWith("DEVICE_LIST:") || cmd.StartsWith("{")) return;

        if (cmd == "MODE_2") { divisionMode = 2; StartTest(); return; }
        if (cmd == "MODE_4") { divisionMode = 4; StartTest(); return; }
        if (cmd == "MODE_4V") { divisionMode = 3; currentEyeTarget = 2; StartTest(); return; }

        if (currentState == AppState.ModeSelection) StartTest();

        if (cmd.StartsWith("SET_VAL:"))
        {
            string[] parts = cmd.Split(':');
            if (parts.Length == 3)
            {
                string target = parts[1];
                int value = Mathf.Clamp(int.Parse(parts[2]), 0, 100);
                switch (target)
                {
                    case "L_NASAL": leftNasal = value; break;
                    case "L_TEMP": leftTemporal = value; break;
                    case "R_NASAL": rightNasal = value; break;
                    case "R_TEMP": rightTemporal = value; break;
                    case "L_ALL": leftNasal = leftTemporal = value; break;
                    case "R_ALL": rightNasal = rightTemporal = value; break;
                    case "L_TOP": leftTop = value; break;
                    case "L_BOT": leftBot = value; break;
                    case "R_TOP": rightTop = value; break;
                    case "R_BOT": rightBot = value; break;
                    case "Q0": quadBrightness[0] = value; break;
                    case "Q1": quadBrightness[1] = value; break;
                    case "Q2": quadBrightness[2] = value; break;
                    case "Q3": quadBrightness[3] = value; break;
                }
            }
            Apply();
            return;
        }

        if (cmd.StartsWith("SET_FLIP_INTERVAL:"))
        {
            string[] parts = cmd.Split(':');
            if (parts.Length == 2 && float.TryParse(parts[1], out float val))
            {
                flipInterval = Mathf.Max(0.1f, val);
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
            case "EYE_LEFT": currentEyeTarget = 0; break;
            case "EYE_RIGHT": currentEyeTarget = 1; break;
            case "EYE_BOTH": currentEyeTarget = 2; break;
            case "EYE_TARGET_TOGGLE": currentEyeTarget = (currentEyeTarget + 1) % 3; break;
            case "CHANGE_COLOR": currentColorIndex = (currentColorIndex + 1) % 4; break;
            case "CHANGE_TARGET":
                int maxModes = (divisionMode == 2) ? 3 : 5;
                SetAdjustMode((adjustMode + 1) % maxModes);
                break;
            case "BRIGHT_UP": ChangeBrightness(2); break;
            case "BRIGHT_DOWN": ChangeBrightness(-2); break;
            case "BRIGHT_UP_L": ChangeBrightnessSingle(true, 2); break;
            case "BRIGHT_DOWN_L": ChangeBrightnessSingle(true, -2); break;
            case "BRIGHT_UP_R": ChangeBrightnessSingle(false, 2); break;
            case "BRIGHT_DOWN_R": ChangeBrightnessSingle(false, -2); break;
        }
        Apply();
    }

    void SetAdjustMode(int newMode)
    {
        adjustMode = newMode;
        if (divisionMode == 2)
        {
            if (currentEyeTarget == 2) // 양안 단색 매칭 모드 (왼쪽 vs 오른쪽 전체)
            {
                // 낮은 것을 올려서 맞추기 (타겟 30, 배경 80 시작)
                if (adjustMode == 1) { leftNasal = 30; leftTemporal = 30; rightNasal = 80; rightTemporal = 80; } // 왼쪽 전체를 올려 오른쪽(80)에 맞추기
                else if (adjustMode == 2) { leftNasal = 80; leftTemporal = 80; rightNasal = 30; rightTemporal = 30; } // 오른쪽 전체를 올려 왼쪽(80)에 맞추기
                else { leftNasal = 50; rightNasal = 50; leftTemporal = 50; rightTemporal = 50; }
            }
            else // 단안(코/귀) 매칭 모드
            {
                if (adjustMode == 1) { leftTemporal = 80; rightTemporal = 80; leftNasal = 30; rightNasal = 30; } // 코쪽 조절(30->80)
                else if (adjustMode == 2) { leftNasal = 80; rightNasal = 80; leftTemporal = 30; rightTemporal = 30; } // 귀쪽 조절(30->80)
                else { leftNasal = 50; rightNasal = 50; leftTemporal = 50; rightTemporal = 50; }
            }
        }
        else if (divisionMode == 3)
        {
            if (adjustMode != 0)
            {
                // 타겟을 30, 배경을 80으로 역산
                leftTop = (adjustMode == 1 || adjustMode == 3) ? 30 : 80;
                leftBot = (adjustMode == 2 || adjustMode == 4) ? 30 : 80;
                rightTop = (adjustMode == 1 || adjustMode == 4) ? 30 : 80;
                rightBot = (adjustMode == 2 || adjustMode == 3) ? 30 : 80;
            }
            else { leftTop = 50; leftBot = 50; rightTop = 50; rightBot = 50; }
        }
        else
        {
            if (adjustMode != 0)
            {
                int targetIdx = adjustMode - 1;
                for (int i = 0; i < 4; i++) quadBrightness[i] = (i == targetIdx) ? 30 : 80;
            }
            else { for (int i = 0; i < 4; i++) quadBrightness[i] = 50; }
        }
    }

    void ChangeBrightness(int amount)
    {
        bool affectLeft = (currentEyeTarget == 0 || currentEyeTarget == 2);
        bool affectRight = (currentEyeTarget == 1 || currentEyeTarget == 2);

        if (divisionMode == 2)
        {
            if (currentEyeTarget == 2)
            {
                // 양안 모드: 한쪽 눈 조절 시 통째로(Nasal+Temporal 동기화) 조절
                if (adjustMode == 0 || adjustMode == 1) { 
                    leftNasal = Mathf.Clamp(leftNasal + amount, 0, 100); 
                    leftTemporal = leftNasal; 
                }
                if (adjustMode == 0 || adjustMode == 2) { 
                    rightNasal = Mathf.Clamp(rightNasal + amount, 0, 100); 
                    rightTemporal = rightNasal; 
                }
            }
            else
            {
                // 단안 모드: 코/귀 독립 조절
                if (adjustMode == 0 || adjustMode == 1)
                {
                    if (affectLeft) leftNasal = Mathf.Clamp(leftNasal + amount, 0, 100);
                    if (affectRight) rightNasal = Mathf.Clamp(rightNasal + amount, 0, 100);
                }
                if (adjustMode == 0 || adjustMode == 2)
                {
                    if (affectLeft) leftTemporal = Mathf.Clamp(leftTemporal + amount, 0, 100);
                    if (affectRight) rightTemporal = Mathf.Clamp(rightTemporal + amount, 0, 100);
                }
            }
        }
        else if (divisionMode == 3)
        {
            if (adjustMode == 0 || adjustMode == 1 || adjustMode == 3) leftTop = Mathf.Clamp(leftTop + amount, 0, 100);
            if (adjustMode == 0 || adjustMode == 2 || adjustMode == 4) leftBot = Mathf.Clamp(leftBot + amount, 0, 100);
            if (adjustMode == 0 || adjustMode == 1 || adjustMode == 4) rightTop = Mathf.Clamp(rightTop + amount, 0, 100);
            if (adjustMode == 0 || adjustMode == 2 || adjustMode == 3) rightBot = Mathf.Clamp(rightBot + amount, 0, 100);
        }
        else
        {
            if (adjustMode == 0) for (int i = 0; i < 4; i++) quadBrightness[i] = Mathf.Clamp(quadBrightness[i] + amount, 0, 100);
            else { int idx = adjustMode - 1; quadBrightness[idx] = Mathf.Clamp(quadBrightness[idx] + amount, 0, 100); }
        }
    }

    void ChangeBrightnessSingle(bool isLeft, int amount)
    {
        if (isLeft)
        {
            leftNasal = Mathf.Clamp(leftNasal + amount, 0, 100);
            leftTemporal = leftNasal;
            leftTop = Mathf.Clamp(leftTop + amount, 0, 100);
            leftBot = Mathf.Clamp(leftBot + amount, 0, 100);
        }
        else
        {
            rightNasal = Mathf.Clamp(rightNasal + amount, 0, 100);
            rightTemporal = rightNasal;
            rightTop = Mathf.Clamp(rightTop + amount, 0, 100);
            rightBot = Mathf.Clamp(rightBot + amount, 0, 100);
        }
    }

    void UpdateDynamicLayout(Renderer[] quads)
    {
        if (quads == null || quads.Length < 4) return;
        
        float w = targetWidth;
        float h = targetHeight;

        if (divisionMode == 2)
        {
            quads[0].transform.localPosition = new Vector3(w / 4f, 0, 0);
            quads[0].transform.localScale = new Vector3(w / 2f, h, 1);
            quads[0].gameObject.SetActive(true);

            quads[2].transform.localPosition = new Vector3(-w / 4f, 0, 0);
            quads[2].transform.localScale = new Vector3(w / 2f, h, 1);
            quads[2].gameObject.SetActive(true);

            quads[1].gameObject.SetActive(false);
            quads[3].gameObject.SetActive(false);
        }
        else if (divisionMode == 3)
        {
            float stripW = w / 4f;

            quads[0].transform.localPosition = new Vector3(-w / 2f + stripW / 2f, 0, 0);
            quads[0].transform.localScale = new Vector3(stripW, h, 1);
            quads[0].gameObject.SetActive(true);

            quads[1].transform.localPosition = new Vector3(-w / 2f + stripW + stripW / 2f, 0, 0);
            quads[1].transform.localScale = new Vector3(stripW, h, 1);
            quads[1].gameObject.SetActive(true);

            quads[2].transform.localPosition = new Vector3(w / 2f - stripW - stripW / 2f, 0, 0);
            quads[2].transform.localScale = new Vector3(stripW, h, 1);
            quads[2].gameObject.SetActive(true);

            quads[3].transform.localPosition = new Vector3(w / 2f - stripW / 2f, 0, 0);
            quads[3].transform.localScale = new Vector3(stripW, h, 1);
            quads[3].gameObject.SetActive(true);
        }
        else
        {
            float offset = w / 4.0f;
            quads[0].transform.localPosition = new Vector3(offset, h / 4f, 0);
            quads[1].transform.localPosition = new Vector3(offset, -h / 4f, 0);
            quads[2].transform.localPosition = new Vector3(-offset, h / 4f, 0);
            quads[3].transform.localPosition = new Vector3(-offset, -h / 4f, 0);

            for (int i = 0; i < 4; i++)
            {
                quads[i].transform.localScale = new Vector3(w / 2f, h / 2f, 1);
                quads[i].gameObject.SetActive(true);
            }
        }
    }

    void Apply()
    {
        UpdateDynamicLayout(leftQuads);
        UpdateDynamicLayout(rightQuads);

        bool isLeftActive = (currentEyeTarget == 0 || currentEyeTarget == 2);
        bool isRightActive = (currentEyeTarget == 1 || currentEyeTarget == 2);

        if (isFlipMode && divisionMode == 2 && currentEyeTarget == 2)
        {
            isLeftActive = isLeftEyeShown;
            isRightActive = !isLeftEyeShown;
        }

        SetCameraBlackout(leftCamera, !isLeftActive);
        SetCameraBlackout(rightCamera, !isRightActive);

        if (divisionMode == 2)
        {
            Color lNasal = baseColors[currentColorIndex] * (leftNasal / 100.0f); lNasal.a = 1f;
            Color lTemp = baseColors[currentColorIndex] * (leftTemporal / 100.0f); lTemp.a = 1f;
            Color rNasal = baseColors[currentColorIndex] * (rightNasal / 100.0f); rNasal.a = 1f;
            Color rTemp = baseColors[currentColorIndex] * (rightTemporal / 100.0f); rTemp.a = 1f;

            if (currentEyeTarget == 2) { lTemp = lNasal; rTemp = rNasal; }

            if (isLeftActive) ApplyBiColor(leftQuads, lNasal, lTemp, true);
            if (isRightActive) ApplyBiColor(rightQuads, rNasal, rTemp, false);
        }
        else if (divisionMode == 3)
        {
            Color c1 = baseColors[currentColorIndex] * (leftTop / 100.0f); c1.a = 1f;
            Color c2 = baseColors[currentColorIndex] * (leftBot / 100.0f); c2.a = 1f;
            Color c3 = baseColors[currentColorIndex] * (rightTop / 100.0f); c3.a = 1f;
            Color c4 = baseColors[currentColorIndex] * (rightBot / 100.0f); c4.a = 1f;

            if (isLeftActive && leftQuads != null)
            {
                if (leftQuads[0] != null) leftQuads[0].material.color = c1;
                if (leftQuads[1] != null) leftQuads[1].material.color = c2;
                if (leftQuads[2] != null) leftQuads[2].material.color = c3;
                if (leftQuads[3] != null) leftQuads[3].material.color = c4;
            }
            if (isRightActive && rightQuads != null)
            {
                if (rightQuads[0] != null) rightQuads[0].material.color = c1;
                if (rightQuads[1] != null) rightQuads[1].material.color = c2;
                if (rightQuads[2] != null) rightQuads[2].material.color = c3;
                if (rightQuads[3] != null) rightQuads[3].material.color = c4;
            }
        }
        else
        {
            Color[] qColors = new Color[4];
            for (int i = 0; i < 4; i++)
            {
                qColors[i] = baseColors[currentColorIndex] * (quadBrightness[i] / 100.0f);
                qColors[i].a = 1f;
            }
            if (isLeftActive) ApplyQuadColor(leftQuads, qColors);
            if (isRightActive) ApplyQuadColor(rightQuads, qColors);
        }
        UpdateStatusText();
    }

    void ApplyBiColor(Renderer[] quads, Color nasalColor, Color temporalColor, bool isLeft)
    {
        if (quads == null || quads.Length < 4) return;
        bool nasalIsRight = isLeft;
        Color right = nasalIsRight ? nasalColor : temporalColor;
        Color left = nasalIsRight ? temporalColor : nasalColor;
        if (quads[0] != null) quads[0].material.color = right;
        if (quads[2] != null) quads[2].material.color = left;
    }

    void ApplyQuadColor(Renderer[] quads, Color[] c)
    {
        if (quads == null || quads.Length < 4) return;
        for (int i = 0; i < 4; i++)
            if (quads[i] != null && quads[i].gameObject.activeSelf) quads[i].material.color = c[i];
    }

    void UpdateStartScreenText()
    {
        lastStatus = "<b>[ 검사 대기 중 ]</b>\nA버튼/Enter: 2분면 | B버튼/Space: 4분면";
        if (statusText != null) statusText.text = lastStatus;
        SendStateToServer();
    }

    void UpdateStatusText()
    {
        if (currentState == AppState.ModeSelection) return;

        string modeStr;
        if (divisionMode == 3)
            modeStr = adjustMode == 0 ? "세로 전체" : verticalGroupNames[adjustMode - 1];
        else if (divisionMode == 2)
            modeStr = adjustMode == 0 ? (currentEyeTarget == 2 ? "양쪽 눈 통제" : "양쪽 제어") : 
                     (currentEyeTarget == 2 ? binocularModes[adjustMode] : (adjustMode == 1 ? "코쪽 제어" : "귀쪽 제어"));
        else
            modeStr = adjustMode == 0 ? "전체 조절" : $"{quadNames[adjustMode - 1]} 조절";

        string flipStatus = isFlipMode ? " | [FLIP]" : "";
        lastStatus = $"<b>{modeStr}</b> | {targetNames[currentEyeTarget]}{flipStatus}";
        if (statusText != null) statusText.text = lastStatus;
        SendStateToServer();
    }

    private async void SendStateToServer()
    {
        if (!isRegistered || websocket == null || websocket.State != WebSocketState.Open) return;
        if (isSending || (Time.time - lastSendTime) < sendInterval) return;
        isSending = true; lastSendTime = Time.time;
        try
        {
            VRStateData data = new VRStateData
            {
                divMode = this.divisionMode,
                colorIdx = this.currentColorIndex,
                leftNasal = this.leftNasal,
                leftTemporal = this.leftTemporal,
                rightNasal = this.rightNasal,
                rightTemporal = this.rightTemporal,
                q0 = quadBrightness[0],
                q1 = quadBrightness[1],
                q2 = quadBrightness[2],
                q3 = quadBrightness[3],
                leftTop = this.leftTop,
                leftBot = this.leftBot,
                rightTop = this.rightTop,
                rightBot = this.rightBot,
                uiText = this.lastStatus,
                currentEyeTarget = this.currentEyeTarget,
                isFlipMode = this.isFlipMode,
                isLeftEyeShown = this.isLeftEyeShown
            };
            string json = JsonUtility.ToJson(data);
            if (!string.IsNullOrEmpty(json))
            {
                byte[] buffer = Encoding.UTF8.GetBytes(json);
                await websocket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None);
            }
        }
        catch { }
        finally { isSending = false; }
    }

    private async void OnDestroy()
    {
        if (websocket != null && websocket.State == WebSocketState.Open)
            await websocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None);
    }
}