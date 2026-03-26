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
using UnityEngine.InputSystem.XR;
using UnityEngine.InputSystem.Controls;
using System.Linq;
using System.Collections.Generic;
using UnityEngine.XR;

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
}

public class VRController : MonoBehaviour
{
    [Header("서버 설정")]
    public string serverIP = "10.2.52.8";
    public int serverPort = 12346;

    [Header("카메라 및 화면 그룹")]
    public Camera leftCamera;
    public Camera rightCamera;
    public GameObject leftEyeGroup;
    public GameObject rightEyeGroup;

    [Header("분할 화면 렌더러 (Inspector 할당)")]
    [Tooltip("0: 우상단, 1: 우하단, 2: 좌상단, 3: 좌하단 순서로 넣어주세요.")]
    public Renderer[] leftQuads = new Renderer[4]; 
    public Renderer[] rightQuads = new Renderer[4];

    [Header("UI 연결")]
    public Text statusText;
    public GameObject crosshair;

    [Header("시각 고도화 설정")]
    public float targetScale = 1.8f;   // 사각형 크기
    public float targetDistance = 5.0f; // 카메라 앞 Z축 거리 (원하는 만큼 수정 가능)
    public int leftLayer = 30;      // 좌안 전용 (Unity Editor에서 미리 생성 권장)
    public int rightLayer = 31;     // 우안 전용 (Unity Editor에서 미리 생성 권장)

    private enum AppState { ModeSelection, Testing }
    private AppState currentState = AppState.ModeSelection;

    private int divisionMode = 2;
    private int leftNasal = 50;
    private int leftTemporal = 50;
    private int rightNasal = 50;
    private int rightTemporal = 50;
    private int[] quadBrightness = new int[] { 50, 50, 50, 50 };

    private int currentEyeTarget = 0;
    private int currentColorIndex = 0;
    private int adjustMode = 0;

    private Color[] baseColors = new Color[] { Color.red, Color.green, Color.blue, Color.white };
    private string[] targetNames = new string[] { "왼쪽 눈", "오른쪽 눈", "양쪽 눈" };
    private string[] quadNames = new string[] { "우상단", "우하단", "좌상단", "좌하단" };

    private const float MAX_NITS = 87f;
    private const float GAMMA = 2.2f;
    private const float PI = 3.14159f;
    
    [Header("플립 모드 설정")]
    private bool isFlipMode = false;
    private float flipInterval = 1.0f;
    private float nextFlipTime = 0f;
    private bool isLeftEyeShown = true;

    // Quest 컨트롤러 이전 프레임 상태 저장용
    private bool prevBtnA = false;
    private bool prevBtnB = false;
    private bool prevTrigger = false;
    private bool prevGrip = false;

    private ClientWebSocket websocket;
    private ConcurrentQueue<string> commandQueue = new ConcurrentQueue<string>();
    private string lastStatus = "";
    private bool isRegistered = false;
    private bool isSending = false; 
    private float lastSendTime = 0f;
    private const float sendInterval = 0.1f; 

    async void Start()
    {
        // 1. 초기 상태 설정
        leftEyeGroup.SetActive(false);
        rightEyeGroup.SetActive(false);
        if (crosshair != null) crosshair.SetActive(false);

        // 1-1. 하이라키 자동 정렬 및 Z축 거리 확보 (거리 조절 시 이 값이 쓰임)
        if (leftCamera != null) {
            leftEyeGroup.transform.parent = leftCamera.transform;
            leftEyeGroup.transform.localPosition = new Vector3(0, 0, targetDistance);
            leftEyeGroup.transform.localEulerAngles = Vector3.zero;
        }
        if (rightCamera != null) {
            rightEyeGroup.transform.parent = rightCamera.transform;
            rightEyeGroup.transform.localPosition = new Vector3(0, 0, targetDistance);
            rightEyeGroup.transform.localEulerAngles = Vector3.zero;
        }

        // 2. 양안 분리 레이어 설정 (코드에서 강제 할당)
        SetLayerRecursively(leftEyeGroup, leftLayer);
        SetLayerRecursively(rightEyeGroup, rightLayer);

        // 3. 카메라 컬링 마스크 및 스테레오 렌더링 타겟 강제 설정 (Quest Single Pass 버그 방지)
        int commonMask = (1 << 0) | (1 << 5); // Default + UI
        if (leftCamera != null) {
            leftCamera.cullingMask = commonMask | (1 << leftLayer);
            leftCamera.stereoTargetEye = StereoTargetEyeMask.Left;
            leftCamera.clearFlags = CameraClearFlags.SolidColor;
            leftCamera.backgroundColor = Color.black;
        }
        if (rightCamera != null) {
            rightCamera.cullingMask = commonMask | (1 << rightLayer);
            rightCamera.stereoTargetEye = StereoTargetEyeMask.Right;
            rightCamera.clearFlags = CameraClearFlags.SolidColor;
            rightCamera.backgroundColor = Color.black;
        }

        // 3-1. 유니티 에디터 2D 화면에서 두 카메라가 덮어쓰는 버그를 막기 위해 에디터 화면을 반반으로 나눔
        if (Application.isEditor)
        {
            if (leftCamera != null) leftCamera.rect = new Rect(0f, 0f, 0.5f, 1f);
            if (rightCamera != null) rightCamera.rect = new Rect(0.5f, 0f, 0.5f, 1f);
        }

        // 4. 사각형 크기 키우기
        ScaleAllQuads(leftQuads, targetScale);
        ScaleAllQuads(rightQuads, targetScale);
        
        lastStatus = "📡 서버 접속 시도 중 (유선/무선)...";
        if (statusText != null) statusText.text = lastStatus;
        
        // 유선(ADB/127.0.0.1) 연결을 최우선으로 시도하면서 백그라운드에서도 탐색 시작
        DiscoverServerIP(); 
        await ConnectToServer();

        // XR Plugin 관련 로깅 (경고 발생 시 안내용)
        Debug.Log("ℹ️ 'Unable to start Oculus XR Plugin' 경고는 고글이 PC와 Link(유선/무선)되지 않았거나 Unity 에디터 모드일 때 발생할 수 있습니다. 수치 전송에는 영향이 없습니다.");
    }

    private void SetLayerRecursively(GameObject obj, int newLayer)
    {
        if (null == obj) return;
        obj.layer = newLayer;
        foreach (Transform child in obj.transform)
        {
            SetLayerRecursively(child.gameObject, newLayer);
        }
    }

    private void ScaleAllQuads(Renderer[] quads, float scale)
    {
        foreach (var r in quads)
        {
            if (r != null) r.transform.localScale = new Vector3(scale, scale, 1f);
        }
    }

    private async Task DiscoverServerIP()
    {
        // 이미 연결되었으면 탐색 건너뜀 (선택 사항)
        if (websocket != null && websocket.State == WebSocketState.Open) return;

        Debug.Log("📡 UDP 브로드캐스트 대기 중 (Port: 50002)...");
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
                            Debug.Log($"✅ 서버 발견: {serverIP}:{serverPort}");
                        }
                    }
                }
                catch (Exception e) { Debug.LogError($"❌ UDP Error: {e.Message}"); }
            }
        }
    }

    private async Task ConnectToServer()
    {
        // 1. 유선(ADB) 최우선 시도
        bool connected = await TryConnect($"ws://127.0.0.1:{serverPort}/ws");
        
        // 2. 안되면 기존 serverIP 시도
        if (!connected) connected = await TryConnect($"ws://{serverIP}:{serverPort}/ws");
        
        // 3. 기타 경로 시도
        if (!connected && serverPort != 80) connected = await TryConnect($"ws://{serverIP}/vr/ws");

        if (connected)
        {
            Debug.Log($"✅ 서버 연결 성공! ({websocket.Options.Proxy})");
            try {
                string deviceId = SystemInfo.deviceUniqueIdentifier;
                byte[] regMsg = Encoding.UTF8.GetBytes($"REG:{deviceId}");
                await websocket.SendAsync(new ArraySegment<byte>(regMsg), WebSocketMessageType.Text, true, CancellationToken.None);
                isRegistered = true; 
                lastSendTime = Time.time;
                UpdateStartScreenText(); 
                SendStateToServer();     
                ReceiveMessages();
            } catch (Exception e) { Debug.LogError($"❌ 등록 프로세스 에러: {e.Message}"); }
        }
    }

    private async Task<bool> TryConnect(string url)
    {
        try {
            websocket = new ClientWebSocket();
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3))) {
                await websocket.ConnectAsync(new Uri(url), cts.Token);
                return websocket.State == WebSocketState.Open;
            }
        } catch { return false; }
    }

    private async void ReceiveMessages()
    {
        byte[] buffer = new byte[2048];
        while (websocket != null && websocket.State == WebSocketState.Open)
        {
            try {
                var result = await websocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Text) {
                    string message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    if (!string.IsNullOrEmpty(message)) commandQueue.Enqueue(message);
                }
                else if (result.MessageType == WebSocketMessageType.Close) break;
            } catch { break; }
        }
    }

    void Update()
    {
        while (commandQueue.TryDequeue(out string command)) ProcessCommand(command);

        // 플립 모드 타이머 (양안 모드일 때만 작동)
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

        // 1. 가동성이 가장 좋은 Legacy XR 장치 먼저 확인 (Quest 한정 100% 인식률)
        List<UnityEngine.XR.InputDevice> xrDevices = new List<UnityEngine.XR.InputDevice>();
        UnityEngine.XR.InputDevices.GetDevicesWithCharacteristics(UnityEngine.XR.InputDeviceCharacteristics.Right | UnityEngine.XR.InputDeviceCharacteristics.Controller, xrDevices);
        
        if (xrDevices.Count > 0)
        {
            var device = xrDevices[0];
            device.TryGetFeatureValue(UnityEngine.XR.CommonUsages.primaryButton, out currA); // A
            device.TryGetFeatureValue(UnityEngine.XR.CommonUsages.secondaryButton, out currB); // B
            device.TryGetFeatureValue(UnityEngine.XR.CommonUsages.triggerButton, out currTrigger); // Trigger
            device.TryGetFeatureValue(UnityEngine.XR.CommonUsages.gripButton, out currGrip); // Grip
            device.TryGetFeatureValue(UnityEngine.XR.CommonUsages.primary2DAxis, out stick); // Stick
            
            if (Time.frameCount % 120 == 0) Debug.Log($"🔍 [Legacy XR] 기기명: {device.name} 인식됨.");
        }
        else
        {
            // 2. Fallback: Input System
            if (Time.frameCount % 120 == 0) Debug.LogWarning("🔍 [Search Fail] 오른쪽 컨트롤러를 찾을 수 없습니다.");
        }

        // 프레임 단위 상태 계산 (wasPressedThisFrame 역할 보정)
        bool btnA = currA && !prevBtnA;
        bool btnB = currB && !prevBtnB;
        bool trigger = currTrigger && !prevTrigger;
        bool grip = currGrip && !prevGrip;

        prevBtnA = currA;
        prevBtnB = currB;
        prevTrigger = currTrigger;
        prevGrip = currGrip;

        // 4. 로컬 키보드 입동 제어 (기존 유지)
        if (keyboard == null) keyboard = InputSystem.GetDevice<Keyboard>();
        bool keyStart2 = keyboard != null && (keyboard.enterKey.wasPressedThisFrame || keyboard.cKey.wasPressedThisFrame || keyboard.leftCtrlKey.wasPressedThisFrame);
        bool keyStart4 = keyboard != null && keyboard.spaceKey.wasPressedThisFrame;

        // 5. 초기 상태 (모드 선택)
        if (currentState == AppState.ModeSelection)
        {
            if (btnA || keyStart2) { Debug.Log("🕹️ [Input] 2분면 선택됨"); divisionMode = 2; StartTest(); }
            else if (btnB || keyStart4) { Debug.Log("🕹️ [Input] 4분면 선택됨"); divisionMode = 4; StartTest(); }
            return;
        }

        // 6. 검사 중 (명령 처리)
        if (btnA) { Debug.Log("🕹️ [Quest] A 버튼 - 색상 변경"); ProcessCommand("CHANGE_COLOR"); }
        if (btnB) { Debug.Log("🕹️ [Quest] B 버튼 - 타겟 변경"); ProcessCommand("CHANGE_TARGET"); }
        if (trigger) { Debug.Log("🕹️ [Quest] 트리거 - 플립 토글"); ProcessCommand("TOGGLE_FLIP"); }
        if (grip) { Debug.Log("🕹️ [Quest] 그립 - 눈 변경"); ProcessCommand("EYE_TARGET_TOGGLE"); }

        // 조이스틱 상하/좌우
        if (stick.y > 0.5f && Time.frameCount % 10 == 0) ProcessCommand("BRIGHT_UP");
        else if (stick.y < -0.5f && Time.frameCount % 10 == 0) ProcessCommand("BRIGHT_DOWN");
        
        if (stick.x < -0.7f && Time.frameCount % 20 == 0) ProcessCommand("EYE_LEFT");
        else if (stick.x > 0.7f && Time.frameCount % 20 == 0) ProcessCommand("EYE_RIGHT");

        // 키보드 입동 제어
        if (keyboard != null) {
            if (keyboard.upArrowKey.wasPressedThisFrame) ProcessCommand("BRIGHT_UP");
            if (keyboard.downArrowKey.wasPressedThisFrame) ProcessCommand("BRIGHT_DOWN");
            if (keyboard.leftCtrlKey.wasPressedThisFrame) ProcessCommand("EYE_TARGET_TOGGLE");
            if (keyboard.spaceKey.wasPressedThisFrame) ProcessCommand("CHANGE_COLOR");
            if (keyboard.escapeKey.wasPressedThisFrame) ProcessCommand("CHANGE_TARGET");
        }

        // 마우스 휠
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
        
        // 원격 모드 변경 명령 수신
        if (cmd == "MODE_2") { divisionMode = 2; StartTest(); return; }
        if (cmd == "MODE_4") { divisionMode = 4; StartTest(); return; }

        if (currentState == AppState.ModeSelection) StartTest();

        if (cmd.StartsWith("SET_VAL:"))
        {
            string[] parts = cmd.Split(':'); 
            if (parts.Length == 3)
            {
                string target = parts[1];
                int value = Mathf.Clamp(int.Parse(parts[2]), 0, 100);
                if (target == "L_NASAL") leftNasal = value;
                else if (target == "L_TEMP") leftTemporal = value;
                else if (target == "R_NASAL") rightNasal = value;
                else if (target == "R_TEMP") rightTemporal = value;
                else if (target == "Q0") quadBrightness[0] = value;
                else if (target == "Q1") quadBrightness[1] = value;
                else if (target == "Q2") quadBrightness[2] = value;
                else if (target == "Q3") quadBrightness[3] = value;
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
        }
        Apply();
    }

    void SetAdjustMode(int newMode)
    {
        adjustMode = newMode;
        if (divisionMode == 2)
        {
            if (adjustMode == 1) { leftTemporal = 80; rightTemporal = 80; leftNasal = 30; rightNasal = 30; }
            else if (adjustMode == 2) { leftNasal = 80; rightNasal = 80; leftTemporal = 30; rightTemporal = 30; }
            else { leftNasal = 50; rightNasal = 50; leftTemporal = 50; rightTemporal = 50; }
        }
        else
        {
            if (adjustMode != 0)
            {
                int targetIdx = adjustMode - 1;
                for (int i = 0; i < 4; i++) {
                    if (i == targetIdx) quadBrightness[i] = 30;
                    else quadBrightness[i] = 80;
                }
            } else { for (int i = 0; i < 4; i++) quadBrightness[i] = 50; }
        }
    }

    void ChangeBrightness(int amount)
    {
        bool affectLeft = (currentEyeTarget == 0 || currentEyeTarget == 2);
        bool affectRight = (currentEyeTarget == 1 || currentEyeTarget == 2);
        if (divisionMode == 2) {
            if (adjustMode == 0 || adjustMode == 1) {
                if (affectLeft) leftNasal = Mathf.Clamp(leftNasal + amount, 0, 100);
                if (affectRight) rightNasal = Mathf.Clamp(rightNasal + amount, 0, 100);
            }
            if (adjustMode == 0 || adjustMode == 2) {
                if (affectLeft) leftTemporal = Mathf.Clamp(leftTemporal + amount, 0, 100);
                if (affectRight) rightTemporal = Mathf.Clamp(rightTemporal + amount, 0, 100);
            }
        } else {
            if (adjustMode == 0) for (int i = 0; i < 4; i++) quadBrightness[i] = Mathf.Clamp(quadBrightness[i] + amount, 0, 100);
            else { int idx = adjustMode - 1; quadBrightness[idx] = Mathf.Clamp(quadBrightness[idx] + amount, 0, 100); }
        }
    }

    void Apply()
    {
        bool isLeftActive = (currentEyeTarget == 0 || currentEyeTarget == 2);
        bool isRightActive = (currentEyeTarget == 1 || currentEyeTarget == 2);
        
        // 플립 모드 적용 (양안일 때만)
        if (isFlipMode && divisionMode == 2 && currentEyeTarget == 2)
        {
            isLeftActive = isLeftEyeShown;
            isRightActive = !isLeftEyeShown;
        }

        leftEyeGroup.SetActive(isLeftActive);
        rightEyeGroup.SetActive(isRightActive);
        
        if (divisionMode == 2)
        {
            Color lNasalColor = baseColors[currentColorIndex] * (leftNasal / 100.0f); lNasalColor.a = 1f;
            Color lTempColor = baseColors[currentColorIndex] * (leftTemporal / 100.0f); lTempColor.a = 1f;
            Color rNasalColor = baseColors[currentColorIndex] * (rightNasal / 100.0f); rNasalColor.a = 1f;
            Color rTempColor = baseColors[currentColorIndex] * (rightTemporal / 100.0f); rTempColor.a = 1f;
            
            // 양안 검사 모드일 경우: 눈을 코/귀로 쪼개지 않고, 좌안은 좌안 전체, 우안은 우안 전체 단색으로 렌더링
            if (currentEyeTarget == 2)
            {
                lTempColor = lNasalColor;
                rTempColor = rNasalColor;
            }

            if (isLeftActive) ApplyBiColor(leftQuads, lNasalColor, lTempColor, true);
            if (isRightActive) ApplyBiColor(rightQuads, rNasalColor, rTempColor, false);
        }
        else
        {
            Color[] qColors = new Color[4];
            for (int i = 0; i < 4; i++) {
                qColors[i] = baseColors[currentColorIndex] * (quadBrightness[i] / 100.0f); qColors[i].a = 1f;
            }
            if (isLeftActive) ApplyQuadColor(leftQuads, qColors);
            if (isRightActive) ApplyQuadColor(rightQuads, qColors);
        }
        UpdateStatusText();
    }

    void ApplyBiColor(Renderer[] quads, Color nasalColor, Color temporalColor, bool isLeft)
    {
        if (quads == null || quads.Length < 4) return;
        bool isNasalRightSide = isLeft; 
        if (quads[0] != null) quads[0].material.color = isNasalRightSide ? nasalColor : temporalColor;
        if (quads[1] != null) quads[1].material.color = isNasalRightSide ? nasalColor : temporalColor;
        if (quads[2] != null) quads[2].material.color = isNasalRightSide ? temporalColor : nasalColor;
        if (quads[3] != null) quads[3].material.color = isNasalRightSide ? temporalColor : nasalColor;
    }

    void ApplyQuadColor(Renderer[] quads, Color[] c)
    {
        if (quads == null || quads.Length < 4) return;
        if (quads[0] != null) quads[0].material.color = c[0];
        if (quads[1] != null) quads[1].material.color = c[1];
        if (quads[2] != null) quads[2].material.color = c[2];
        if (quads[3] != null) quads[3].material.color = c[3];
    }

    void UpdateStartScreenText()
    {
        lastStatus = "<b>[ 검사 대기 중 ]</b>\n트리거: 2분면 / 앱버튼: 4분면";
        if (statusText != null) statusText.text = lastStatus; 
        SendStateToServer();
    }

    void UpdateStatusText()
    {
        if (currentState == AppState.ModeSelection) return;
        int targetBright = (divisionMode == 2) ? leftNasal : quadBrightness[(adjustMode == 0 ? 0 : adjustMode - 1)];
        string modeStr = (divisionMode == 2)
            ? (adjustMode == 0 ? "양쪽 제어" : (adjustMode == 1 ? "코쪽 제어" : "귀쪽 제어"))
            : (adjustMode == 0 ? "전체 조절" : $"{quadNames[adjustMode - 1]} 조절");
        
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
        try {
            VRStateData data = new VRStateData {
                divMode = divisionMode, colorIdx = currentColorIndex,
                leftNasal = leftNasal, leftTemporal = leftTemporal,
                rightNasal = rightNasal, rightTemporal = rightTemporal,
                q0 = quadBrightness[0], q1 = quadBrightness[1], q2 = quadBrightness[2], q3 = quadBrightness[3],
                uiText = lastStatus, currentEyeTarget = currentEyeTarget,
                isFlipMode = isFlipMode, isLeftEyeShown = isLeftEyeShown
            };
            string json = JsonUtility.ToJson(data);
            if (!string.IsNullOrEmpty(json)) {
                byte[] buffer = Encoding.UTF8.GetBytes(json);
                await websocket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None);
            }
        } catch {} finally { isSending = false; }
    }

    private async void OnDestroy()
    {
        if (websocket != null && websocket.State == WebSocketState.Open) {
            await websocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None);
        }
    }
}
