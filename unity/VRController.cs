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
}

public class VRController : MonoBehaviour
{
    [Header("서버 설정")]
    public string serverIP = "10.2.52.8";

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

    private enum AppState { ModeSelection, Testing }
    private AppState currentState = AppState.ModeSelection;

    private int divisionMode = 2;
    // 시야 고정된 독립 밝기 수치
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

    private ClientWebSocket websocket;
    private ConcurrentQueue<string> commandQueue = new ConcurrentQueue<string>();
    private string lastStatus = "";

    async void Start()
    {
        leftEyeGroup.SetActive(false);
        rightEyeGroup.SetActive(false);
        if (crosshair != null) crosshair.SetActive(false);
        
        lastStatus = "📡 서버 (172.20.10.14) 직항 연결 중...";
        if (statusText != null) statusText.text = lastStatus;
        
        // 💡 도커(Docker) 환경에서는 UDP 자동 탐색이 도커 내부 IP(172.20.0.x 등)를 반환하므로 완전히 차단합니다.
        // 유저님의 맥(Mac) IP 주소로 완벽하게 고정합니다.
        serverIP = "172.20.10.14";
        Debug.Log($"✅ 고정 IP 접속 시도: {serverIP}");

        await ConnectToServer();
    }

    private async Task DiscoverServerIP()
    {
        // 사용 안 함 (도커 환경 문제 방지)
    }

    private async Task ConnectToServer()
    {
        if (statusText != null) statusText.text = $"📡 연결 시도 중...\n(Direct 12346 Port)";
        bool connected = await TryConnect($"ws://{serverIP}:12346/ws");
        
        if (!connected)
        {
            if (statusText != null) statusText.text = $"⚠️ 직접 연결 실패.\n프록시(80 포트)로 우회 접속 시도 중...";
            Debug.Log($"⚠️ 12346 포트 연결 실패, 80 포트(/vr/ws)로 폴백 접속 시도");
            connected = await TryConnect($"ws://{serverIP}/vr/ws");
        }

        if (connected)
        {
            if (statusText != null) statusText.text = "✅ 연결 성공!\n키보드 C, 엔터, 스페이스바를 누르세요.";
            UpdateStartScreenText(); 
            SendStateToServer();     
            ReceiveMessages();
        }
        else
        {
             if (statusText != null) statusText.text = $"❌ 모든 연결 시도 실패.\n방화벽, 포트(80, 12346) 설정을 확인하세요.";
        }
    }

    private async Task<bool> TryConnect(string url)
    {
        websocket = new ClientWebSocket();
        try
        {
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3))) // 3초 타임아웃
            {
                await websocket.ConnectAsync(new Uri(url), cts.Token);
                return websocket.State == WebSocketState.Open;
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"❌ {url} 연결 실패: {e.Message}");
            return false;
        }
    }

    private async void ReceiveMessages()
    {
        byte[] buffer = new byte[1024];
        while (websocket != null && websocket.State == WebSocketState.Open)
        {
            try
            {
                var result = await websocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Text)
                {
                    string message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    commandQueue.Enqueue(message);
                }
            }
            catch { break; }
        }
    }

    void Update()
    {
        while (commandQueue.TryDequeue(out string command))
        {
            ProcessCommand(command);
        }

        HandlePhysicalInput();
    }

    void HandlePhysicalInput()
    {
        // 🎮 [ VR 조이스틱 & 마우스 클릭 제어 ]
        bool btnA = Input.GetKeyDown("joystick button 0"); 
        bool btnB = Input.GetKeyDown("joystick button 1"); 
        bool trigger = Input.GetKeyDown("joystick button 14") || Input.GetKeyDown("joystick button 15") || Input.GetMouseButtonDown(0);

        // ⌨️ [ PC 키보드 제어 (VR 없이 완벽 테스트용) ]
        bool keyStart2 = Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.C) || Input.GetKeyDown(KeyCode.LeftControl);
        bool keyStart4 = Input.GetKeyDown(KeyCode.Space);
        
        bool keyToggleEye = Input.GetKeyDown(KeyCode.LeftControl); // L/R/Both 눈 변경
        bool keyChangeColor = Input.GetKeyDown(KeyCode.Space);     // 색상 변경
        bool keyChangeTarget = Input.GetKeyDown(KeyCode.Escape);   // 제어 타겟(Nasal/Temporal 등) 변경
        
        bool keyBrightUp = Input.GetKeyDown(KeyCode.UpArrow);
        bool keyBrightDown = Input.GetKeyDown(KeyCode.DownArrow);

        // 1. 대기 화면 제어
        if (currentState == AppState.ModeSelection)
        {
            if (trigger || btnA || keyStart2)
            {
                divisionMode = 2; StartTest();
            }
            else if (btnB || Input.GetKeyDown("joystick button 9") || keyStart4)
            {
                divisionMode = 4; StartTest();
            }
            return;
        }

        // 2. 밝기 조절 (위/아래 방향키 및 마우스 휠)
        float wheel = Input.GetAxis("Mouse ScrollWheel");
        float vertical = Input.GetAxis("Vertical");

        if (keyBrightUp || wheel > 0.01f || vertical > 0.8f) ProcessCommand("BRIGHT_UP");
        else if (keyBrightDown || wheel < -0.01f || vertical < -0.8f) ProcessCommand("BRIGHT_DOWN");

        // 3. 모드 / 타겟 / 색상 제어
        if (trigger || keyToggleEye) ProcessCommand("EYE_TARGET_TOGGLE");
        if (Input.GetKeyDown("joystick button 2") || Input.GetKeyDown("joystick button 9") || keyChangeColor) ProcessCommand("CHANGE_COLOR");
        if (btnB || keyChangeTarget) ProcessCommand("CHANGE_TARGET");
    }

    void StartTest()
    {
        currentState = AppState.Testing;
        if (crosshair != null) crosshair.SetActive(false); 
        Apply();
    }

    void ProcessCommand(string cmd)
    {
        // 💡 웹 UI에서 원격으로 명령이 내려오면, 즉시 대기 화면을 끝내고 테스트 모드로 자동 진입합니다.
        if (currentState == AppState.ModeSelection)
        {
            StartTest();
        }

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

        switch (cmd)
        {
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
                for (int i = 0; i < 4; i++)
                {
                    if (i == targetIdx) quadBrightness[i] = 30;
                    else quadBrightness[i] = 80;
                }
            }
            else
            {
                for (int i = 0; i < 4; i++) quadBrightness[i] = 50;
            }
        }
    }

    void ChangeBrightness(int amount)
    {
        int maxLevel = 100;
        if (divisionMode == 2)
        {
            if (adjustMode == 0 || adjustMode == 1) { leftNasal = Mathf.Clamp(leftNasal + amount, 0, maxLevel); rightNasal = Mathf.Clamp(rightNasal + amount, 0, maxLevel); }
            if (adjustMode == 0 || adjustMode == 2) { leftTemporal = Mathf.Clamp(leftTemporal + amount, 0, maxLevel); rightTemporal = Mathf.Clamp(rightTemporal + amount, 0, maxLevel); }
        }
        else
        {
            if (adjustMode == 0)
            {
                for (int i = 0; i < 4; i++) quadBrightness[i] = Mathf.Clamp(quadBrightness[i] + amount, 0, maxLevel);
            }
            else
            {
                int idx = adjustMode - 1;
                quadBrightness[idx] = Mathf.Clamp(quadBrightness[idx] + amount, 0, maxLevel);
            }
        }
    }

    void Apply()
    {
        bool isLeftActive = (currentEyeTarget == 0 || currentEyeTarget == 2);
        bool isRightActive = (currentEyeTarget == 1 || currentEyeTarget == 2);
        leftEyeGroup.SetActive(isLeftActive);
        rightEyeGroup.SetActive(isRightActive);
        
        bool isBinocular = (currentEyeTarget == 2);

        if (leftCamera != null) leftCamera.cullingMask = isLeftActive ? -1 : 0;
        if (rightCamera != null) rightCamera.cullingMask = isRightActive ? -1 : 0;

        if (divisionMode == 2)
        {
            Color lNasalColor = baseColors[currentColorIndex] * (leftNasal / 100.0f); lNasalColor.a = 1f;
            Color lTempColor = baseColors[currentColorIndex] * (leftTemporal / 100.0f); lTempColor.a = 1f;
            Color rNasalColor = baseColors[currentColorIndex] * (rightNasal / 100.0f); rNasalColor.a = 1f;
            Color rTempColor = baseColors[currentColorIndex] * (rightTemporal / 100.0f); rTempColor.a = 1f;
            
            if (isLeftActive) ApplyBiColor(leftQuads, lNasalColor, lTempColor, true);
            if (isRightActive) ApplyBiColor(rightQuads, rNasalColor, rTempColor, false);
        }
        else
        {
            Color[] qColors = new Color[4];
            for (int i = 0; i < 4; i++)
            {
                qColors[i] = baseColors[currentColorIndex] * (quadBrightness[i] / 100.0f); qColors[i].a = 1f;
            }
            if (isLeftActive) ApplyQuadColor(leftQuads, qColors);
            if (isRightActive) ApplyQuadColor(rightQuads, qColors);
        }
        UpdateStatusText();
    }

    void ApplyBiColor(Renderer[] quads, Color nasalColor, Color temporalColor, bool isLeft)
    {
        // 0:우상, 1:우하, 2:좌상, 3:좌하
        // 왼쪽 눈 (isLeft=true): 오른쪽(0,1)이 코쪽(Nasal), 왼쪽(2,3)이 귀쪽(Temporal)
        // 오른쪽 눈 (isLeft=false): 오른쪽(0,1)이 귀쪽(Temporal), 왼쪽(2,3)이 코쪽(Nasal)
        
        if (quads == null || quads.Length < 4) return;

        bool isNasalRightSide = isLeft; 

        if (quads[0] != null) quads[0].material.color = isNasalRightSide ? nasalColor : temporalColor;
        if (quads[1] != null) quads[1].material.color = isNasalRightSide ? nasalColor : temporalColor;
        if (quads[2] != null) quads[2].material.color = isNasalRightSide ? temporalColor : nasalColor;
        if (quads[3] != null) quads[3].material.color = isNasalRightSide ? temporalColor : nasalColor;
    }

    void ApplyQuadColor(Renderer[] quads, Color[] c)
    {
        // c[0]=우상, c[1]=우하, c[2]=좌상, c[3]=좌하
        if (quads == null || quads.Length < 4) return;

        if (quads[0] != null) quads[0].material.color = c[0];
        if (quads[1] != null) quads[1].material.color = c[1];
        if (quads[2] != null) quads[2].material.color = c[2];
        if (quads[3] != null) quads[3].material.color = c[3];
    }

    void UpdateStartScreenText()
    {
        lastStatus = "<b>[ 검사 대기 중 ]</b>\n트리거: 2분면 / 앱버튼: 4분면";
        if (statusText != null) statusText.text = ""; 
        SendStateToServer();
    }

    void UpdateStatusText()
    {
        if (currentState == AppState.ModeSelection) return;
        
        int targetBright = (divisionMode == 2) ? leftNasal : quadBrightness[(adjustMode == 0 ? 0 : adjustMode - 1)];
        float currentPercent = targetBright / 100f;
        float currentLux = (MAX_NITS * Mathf.Pow(currentPercent, GAMMA)) * PI;

        string modeStr = (divisionMode == 2)
            ? (adjustMode == 0 ? "양쪽 제어" : (adjustMode == 1 ? "코쪽 제어" : "귀쪽 제어"))
            : (adjustMode == 0 ? "전체 조절" : $"{quadNames[adjustMode - 1]} 조절");

        lastStatus = $"<b>{modeStr}</b> | {targetNames[currentEyeTarget]}";
        if (statusText != null) statusText.text = ""; 
        SendStateToServer();
    }

    private async void SendStateToServer()
    {
        if (websocket != null && websocket.State == WebSocketState.Open)
        {
            VRStateData data = new VRStateData
            {
                divMode = divisionMode,
                colorIdx = currentColorIndex,
                leftNasal = leftNasal,
                leftTemporal = leftTemporal,
                rightNasal = rightNasal,
                rightTemporal = rightTemporal,
                q0 = quadBrightness[0],
                q1 = quadBrightness[1],
                q2 = quadBrightness[2],
                q3 = quadBrightness[3],
                uiText = string.IsNullOrEmpty(lastStatus) ? "VR 기기 연동 완료" : lastStatus
            };
            byte[] bytes = Encoding.UTF8.GetBytes(JsonUtility.ToJson(data));
            await websocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
        }
    }

    private async void OnDestroy()
    {
        if (websocket != null && websocket.State == WebSocketState.Open)
        {
            await websocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None);
        }
    }
}
