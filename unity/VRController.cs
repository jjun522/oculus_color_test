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
    public int nasal;
    public int temporal;
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

    [Header("UI 연결")]
    public Text statusText;
    public GameObject crosshair;

    private enum AppState { ModeSelection, Testing }
    private AppState currentState = AppState.ModeSelection;

    private int divisionMode = 2;
    private int nasalBrightness = 50;
    private int temporalBrightness = 50;
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
    private string lastStatus = ""; // 💡 서버로 보낼 텍스트 상태 저장용

    async void Start()
    {
        leftEyeGroup.SetActive(false);
        rightEyeGroup.SetActive(false);
        if (crosshair != null) crosshair.SetActive(false);
        
        lastStatus = "📡 서버 탐색 중...";
        UpdateStatusText(); // VR에는 안 보이지만 로그에는 남음
        
        await DiscoverServerIP(); // 💡 서버 자동으로 찾기
        await ConnectToServer();
    }

    private async Task DiscoverServerIP()
    {
        UdpClient udpClient = new UdpClient(50002);
        udpClient.Client.ReceiveTimeout = 5000; // 5초 타임아웃
        
        try
        {
            while (true)
            {
                UdpReceiveResult result = await udpClient.ReceiveAsync();
                string message = Encoding.UTF8.GetString(result.Buffer);
                if (message.StartsWith("EYE_SERVER:"))
                {
                    serverIP = message.Split(':')[1];
                    Debug.Log($"✅ 서버 발견: {serverIP}");
                    break;
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"❌ 서버 탐색 오류: {e.Message}");
        }
        finally
        {
            udpClient.Close();
        }
    }

    private async Task ConnectToServer()
    {
        websocket = new ClientWebSocket();
        Uri serverUri = new Uri($"ws://{serverIP}:8000/ws");
        try
        {
            await websocket.ConnectAsync(new Uri($"ws://{serverIP}:8001/ws"), CancellationToken.None);
            ReceiveMessages();
        }
        catch (Exception) { }
    }

    private async void ReceiveMessages()
    {
        byte[] buffer = new byte[1024];
        while (websocket.State == WebSocketState.Open)
        {
            var result = await websocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Text)
            {
                string message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                commandQueue.Enqueue(message);
            }
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
        if (currentState == AppState.ModeSelection)
        {
            if (Input.GetKeyDown("joystick button 15") || Input.GetKeyDown("joystick button 0") || Input.GetMouseButtonDown(0))
            {
                divisionMode = 2; StartTest();
            }
            else if (Input.GetKeyDown("joystick button 9") || Input.GetKeyDown(KeyCode.Space))
            {
                divisionMode = 4; StartTest();
            }
            return;
        }

        // 💡 밝기 조절 로직 (휠 + 조이스틱 + 방향키)
        float wheel = Input.GetAxis("Mouse ScrollWheel");
        if (wheel > 0.01f) ProcessCommand("BRIGHT_UP");
        else if (wheel < -0.01f) ProcessCommand("BRIGHT_DOWN");

        float vertical = Input.GetAxis("Vertical");
        if (vertical > 0.8f) ProcessCommand("BRIGHT_UP");
        else if (vertical < -0.8f) ProcessCommand("BRIGHT_DOWN");

        if (Input.GetKeyDown(KeyCode.UpArrow)) ProcessCommand("BRIGHT_UP");
        if (Input.GetKeyDown(KeyCode.DownArrow)) ProcessCommand("BRIGHT_DOWN");

        if (Input.GetKeyDown("joystick button 15") || Input.GetMouseButtonDown(0)) ProcessCommand("EYE_TARGET_TOGGLE");
        if (Input.GetKeyDown("joystick button 9") || Input.GetKeyDown(KeyCode.Space)) ProcessCommand("CHANGE_COLOR");
        if (Input.GetKeyDown("joystick button 1") || Input.GetKeyDown(KeyCode.Escape)) ProcessCommand("CHANGE_TARGET");
    }

    void StartTest()
    {
        currentState = AppState.Testing;
        if (crosshair != null) crosshair.SetActive(true);
        Apply();
    }

    void ProcessCommand(string cmd)
    {
        // 💡 직접 수치 설정 명령 처리 (확장 버전)
        if (cmd.StartsWith("SET_VAL:"))
        {
            string[] parts = cmd.Split(':'); // 예: [SET_VAL, Q0, 80]
            if (parts.Length == 3)
            {
                string target = parts[1];
                int value = Mathf.Clamp(int.Parse(parts[2]), 0, 100);
                
                // 2분면 타겟
                if (target == "NASAL") nasalBrightness = value;
                else if (target == "TEMPORAL") temporalBrightness = value;
                // 4분면 타겟 (Q0:우상, Q1:우하, Q2:좌상, Q3:좌하)
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
            if (adjustMode == 1) { temporalBrightness = 80; nasalBrightness = 30; }
            else if (adjustMode == 2) { nasalBrightness = 80; temporalBrightness = 30; }
            else { nasalBrightness = 50; temporalBrightness = 50; }
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
        int maxLevel = 100; // 흰색 제한(80) 해제: 모든 색상 최대 100% 가능
        if (divisionMode == 2)
        {
            if (adjustMode == 0 || adjustMode == 1) nasalBrightness = Mathf.Clamp(nasalBrightness + amount, 0, maxLevel);
            if (adjustMode == 0 || adjustMode == 2) temporalBrightness = Mathf.Clamp(temporalBrightness + amount, 0, maxLevel);
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

        if (leftCamera != null) leftCamera.cullingMask = isLeftActive ? -1 : 0;
        if (rightCamera != null) rightCamera.cullingMask = isRightActive ? -1 : 0;

        if (divisionMode == 2)
        {
            Color nColor = baseColors[currentColorIndex] * (nasalBrightness / 100.0f); nColor.a = 1f;
            Color tColor = baseColors[currentColorIndex] * (temporalBrightness / 100.0f); tColor.a = 1f;
            if (isLeftActive) ApplyBiColor(leftEyeGroup, nColor, tColor, true);
            if (isRightActive) ApplyBiColor(rightEyeGroup, nColor, tColor, false);
        }
        else
        {
            Color[] qColors = new Color[4];
            for (int i = 0; i < 4; i++)
            {
                qColors[i] = baseColors[currentColorIndex] * (quadBrightness[i] / 100.0f); qColors[i].a = 1f;
            }
            if (isLeftActive) ApplyQuadColor(leftEyeGroup, qColors);
            if (isRightActive) ApplyQuadColor(rightEyeGroup, qColors);
        }
        UpdateStatusText();
    }

    void ApplyBiColor(GameObject group, Color nColor, Color tColor, bool isLeft)
    {
        foreach (Renderer r in group.GetComponentsInChildren<Renderer>())
        {
            bool isRightSide = r.transform.localPosition.x > 0;
            bool isNasal = isLeft ? isRightSide : !isRightSide;
            r.material.color = isNasal ? nColor : tColor;
        }
    }

    void ApplyQuadColor(GameObject group, Color[] c)
    {
        foreach (Renderer r in group.GetComponentsInChildren<Renderer>())
        {
            bool isRight = r.transform.localPosition.x > 0; bool isTop = r.transform.localPosition.y > 0;
            if (isRight && isTop) r.material.color = c[0];
            else if (isRight && !isTop) r.material.color = c[1];
            else if (!isRight && isTop) r.material.color = c[2];
            else if (!isRight && !isTop) r.material.color = c[3];
        }
    }

    void UpdateStartScreenText()
    {
        lastStatus = "<b>[ 검사 대기 중 ]</b>\n트리거: 2분면 / 앱버튼: 4분면";
        if (statusText != null) statusText.text = ""; // VR 화면에서는 텍스트 제거
        SendStateToServer();
    }

    void UpdateStatusText()
    {
        if (currentState == AppState.ModeSelection) return;
        int targetBright = (divisionMode == 2) ? (adjustMode == 2 ? temporalBrightness : nasalBrightness) : quadBrightness[(adjustMode == 0 ? 0 : adjustMode - 1)];
        float currentPercent = targetBright / 100f;
        float currentLux = (MAX_NITS * Mathf.Pow(currentPercent, GAMMA)) * PI;

        string modeStr = (divisionMode == 2)
            ? (adjustMode == 0 ? "양쪽 조절" : (adjustMode == 1 ? "코쪽 테스트" : "귀쪽 테스트"))
            : (adjustMode == 0 ? "전체 조절" : $"{quadNames[adjustMode - 1]} 조절");

        lastStatus = $"<b>{modeStr}</b> | {targetNames[currentEyeTarget]}\n<color=#00FF00>밝기: {targetBright}% | {currentLux:F1} Lux</color>";
        if (statusText != null) statusText.text = ""; // VR 화면에서는 텍스트 제거
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
                nasal = nasalBrightness,
                temporal = temporalBrightness,
                q0 = quadBrightness[0],
                q1 = quadBrightness[1],
                q2 = quadBrightness[2],
                q3 = quadBrightness[3],
                uiText = lastStatus // 💡 서버(리액트)에는 텍스트 정보 전달
            };
            byte[] bytes = Encoding.UTF8.GetBytes(JsonUtility.ToJson(data));
            await websocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
        }
    }

    private async void OnDestroy()
    {
        if (websocket != null) await websocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None);
    }
}
