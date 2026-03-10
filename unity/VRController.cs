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
    
    // 가상 격벽 참조
    private GameObject leftSeptum;
    private GameObject rightSeptum;

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
    private string lastStatus = ""; // 💡 서버로 보낼 텍스트 상태 저장용

    async void Start()
    {
        leftEyeGroup.SetActive(false);
        rightEyeGroup.SetActive(false);
        if (crosshair != null) crosshair.SetActive(false);
        
        CreateSeptum(); // 가상 격벽 생성

        if (statusText != null) statusText.text = "📡 서버 탐색 중 (5초)...";
        lastStatus = "📡 서버 탐색 중 (5초)...";
        // UpdateStatusText(); 처음에 이것 때문에 "Waiting"으로 덮어씌워질 수 있으므로 주석 처리
        
        // 💡 5초 동안 로컬 서버를 찾아보고, 못 찾으면 공인 IP로 직접 접속을 시도합니다.
        await Task.WhenAny(DiscoverServerIP(), Task.Delay(5000));
        
        if (string.IsNullOrEmpty(serverIP) || serverIP == "127.0.0.1")
        {
            Debug.Log("⚠️ 로컬 서버를 찾지 못했습니다. 기존 공인 IP로 접속 시도...");
            if (statusText != null) statusText.text = "⚠️ 자동 탐색 실패.\n기본(수동설정) IP로 접속 시도 중...";
        }
        else
        {
             if (statusText != null) statusText.text = $"✅ 서버 발견: {serverIP}\n연결 중...";
        }

        await ConnectToServer();
    }

    private void CreateSeptum()
    {
        // 왼쪽 눈 오른쪽 끝 가림막
        leftSeptum = GameObject.CreatePrimitive(PrimitiveType.Cube);
        leftSeptum.transform.parent = leftEyeGroup.transform.parent;
        leftSeptum.transform.localPosition = new Vector3(0.5f, 0, 1.0f); // 중심에서 약간 앞/오른쪽 (더 가깝게)
        leftSeptum.transform.localScale = new Vector3(0.1f, 20f, 20f); // 얇고 매우 넓은 판
        Material unlitBlack = new Material(Shader.Find("Unlit/Color"));
        unlitBlack.color = Color.black;
        leftSeptum.GetComponent<Renderer>().material = unlitBlack; // 조명 무시 완벽한 검은색
        Destroy(leftSeptum.GetComponent<Collider>());
        leftSeptum.layer = LayerMask.NameToLayer("Default"); // UI 렌더링 무시 문제 해결

        // 오른쪽 눈 왼쪽 끝 가림막
        rightSeptum = GameObject.CreatePrimitive(PrimitiveType.Cube);
        rightSeptum.transform.parent = rightEyeGroup.transform.parent;
        rightSeptum.transform.localPosition = new Vector3(-0.5f, 0, 1.0f);
        rightSeptum.transform.localScale = new Vector3(0.1f, 20f, 20f);
        rightSeptum.GetComponent<Renderer>().material = unlitBlack;
        Destroy(rightSeptum.GetComponent<Collider>());
        rightSeptum.layer = LayerMask.NameToLayer("Default");
        
        // 처음엔 끔
        leftSeptum.SetActive(false);
        rightSeptum.SetActive(false);
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
                    // 💡 도커 안에서 보내는 IP가 아닌, 실제 패킷이 날아온 '서버 PC'의 IP를 사용합니다.
                    serverIP = result.RemoteEndPoint.Address.ToString();
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
        Uri serverUri = new Uri($"ws://{serverIP}:12346/ws");
        try
        {
            // 💡 모든 통신이 12346 포트의 /ws 경로로 통합되었습니다.
            await websocket.ConnectAsync(serverUri, CancellationToken.None);
            if (statusText != null) statusText.text = "✅ 연결 성공!\n컨트롤러 버튼을 눌러 검사 모드를 선택하세요.";
            UpdateStartScreenText(); // 웹 서버에 대기 상태 전송
            ReceiveMessages();
        }
        catch (Exception e)
        { 
             if (statusText != null) statusText.text = $"❌ 연결 실패: {e.Message}\n앱을 재시작하거나 네트워크를 확인하세요.";
        }
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
        // 환자 몰입을 위해 십자선 제거 및 상태 텍스트 블라인드
        if (crosshair != null) crosshair.SetActive(false); 
        Apply();
    }

    void ProcessCommand(string cmd)
    {
        // 💡 직접 수치 설정 명령 처리 (확장 버전)
        if (cmd.StartsWith("SET_VAL:"))
        {
            string[] parts = cmd.Split(':'); // 예: [SET_VAL, L_NASAL, 80]
            if (parts.Length == 3)
            {
                string target = parts[1];
                int value = Mathf.Clamp(int.Parse(parts[2]), 0, 100);
                
                // 2분면 독립 타겟
                if (target == "L_NASAL") leftNasal = value;
                else if (target == "L_TEMP") leftTemporal = value;
                else if (target == "R_NASAL") rightNasal = value;
                else if (target == "R_TEMP") rightTemporal = value;
                
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
        int maxLevel = 100; // 흰색 제한(80) 해제: 모든 색상 최대 100% 가능
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
        
        // 양안 모드일 때만 격벽 활성화
        bool isBinocular = (currentEyeTarget == 2);
        if(leftSeptum != null) leftSeptum.SetActive(isBinocular);
        if(rightSeptum != null) rightSeptum.SetActive(isBinocular);

        if (leftCamera != null) leftCamera.cullingMask = isLeftActive ? -1 : 0;
        if (rightCamera != null) rightCamera.cullingMask = isRightActive ? -1 : 0;

        if (divisionMode == 2)
        {
            Color lNasalColor = baseColors[currentColorIndex] * (leftNasal / 100.0f); lNasalColor.a = 1f;
            Color lTempColor = baseColors[currentColorIndex] * (leftTemporal / 100.0f); lTempColor.a = 1f;
            Color rNasalColor = baseColors[currentColorIndex] * (rightNasal / 100.0f); rNasalColor.a = 1f;
            Color rTempColor = baseColors[currentColorIndex] * (rightTemporal / 100.0f); rTempColor.a = 1f;
            
            if (isLeftActive) ApplyBiColor(leftEyeGroup, lNasalColor, lTempColor, true);
            if (isRightActive) ApplyBiColor(rightEyeGroup, rNasalColor, rTempColor, false);
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
        
        // 대표 텍스트 표기용 (웹 UI에서 상세히 보게 되므로 간단히)
        int targetBright = (divisionMode == 2) ? leftNasal : quadBrightness[(adjustMode == 0 ? 0 : adjustMode - 1)];
        float currentPercent = targetBright / 100f;
        float currentLux = (MAX_NITS * Mathf.Pow(currentPercent, GAMMA)) * PI;

        string modeStr = (divisionMode == 2)
            ? (adjustMode == 0 ? "양쪽 제어" : (adjustMode == 1 ? "코쪽 제어" : "귀쪽 제어"))
            : (adjustMode == 0 ? "전체 조절" : $"{quadNames[adjustMode - 1]} 조절");

        lastStatus = $"<b>{modeStr}</b> | {targetNames[currentEyeTarget]}";
        if (statusText != null) statusText.text = ""; // VR 화면에서는 텍스트 제거 완벽화
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
                uiText = lastStatus // 💡 서버(리액트)에는 텍스트 정보 전달
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
