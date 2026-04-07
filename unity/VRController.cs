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

// ============================================================
// 서버 ↔ VR 상태 데이터
// 이 구조체가 JSON으로 변환되어 웹 관제 패널에 전송됨
// 웹의 React 코드가 이 필드명을 그대로 사용하므로 이름 변경 금지
// ============================================================
[System.Serializable]
public class VRStateData
{
    public int divMode;           // 분할 모드: 2=2분할, 3=세로4분할, 4=십자4분할
    public int colorIdx;          // 현재 색상 인덱스 (activeColors 배열의 인덱스)
    public int leftNasal;         // 왼쪽 눈 코쪽 밝기 (0~100%)
    public int leftTemporal;      // 왼쪽 눈 귀쪽 밝기
    public int rightNasal;        // 오른쪽 눈 코쪽 밝기
    public int rightTemporal;     // 오른쪽 눈 귀쪽 밝기
    public int q0, q1, q2, q3;   // 십자 4분할 각 영역 밝기 (우상, 우하, 좌상, 좌하)
    public int leftTop, leftBot;  // 세로 4분할: 1번 띠, 2번 띠
    public int rightTop, rightBot;// 세로 4분할: 3번 띠, 4번 띠
    public string uiText;         // VR 내 UI 텍스트
    public int currentEyeTarget;  // 어느 눈에 표시? 0=좌, 1=우, 2=양
    public bool isFlipMode;       // 플립 모드 ON/OFF
    public bool isLeftEyeShown;   // 플립 중 현재 왼쪽이 보이는 차례?
    public float leftFlipAdj;     // 좌안 플립 보정 계수 (0~1)
    // --- 설정값 (웹 설정 탭에서 동기화용) ---
    public float cfgScale;        // Quad 크기
    public float cfgDistance;     // Quad 거리
    public float cfgFlipInterval; // 플립 간격
    public int cfgDefaultBright;  // 기본 밝기
    public int cfgTargetBright;   // 타겟 밝기
    public int cfgBgBright;       // 배경 밝기
    public string cfgColorOrder;  // 색상 순서 (예: "0,1,2,3")
}

public class SimpleEyeTest : MonoBehaviour
{
    // ============================================================
    // Inspector에서 설정하는 항목들
    // ============================================================

    [Header("서버 설정")]
    public string serverIP = "10.2.52.8";  // 웹 관제 서버 IP (UDP 자동탐색으로 덮어씌워질 수 있음)
    public int serverPort = 12346;          // 웹 관제 서버 포트

    [Header("OVRCameraRig > LeftEyeAnchor / RightEyeAnchor")]
    public Camera leftCamera;   // OVRCameraRig > TrackingSpace > LeftEyeAnchor 의 Camera
    public Camera rightCamera;  // OVRCameraRig > TrackingSpace > RightEyeAnchor 의 Camera

    [Header("UI")]
    public Text statusText;      // 상태 표시 텍스트 (VR 내 Canvas)
    public GameObject crosshair; // 가운데 십자 표시 오브젝트

    [Header("레이어 (Tags and Layers에서 30, 31번 추가 필요)")]
    public int leftLayer = 30;   // 왼쪽 눈 전용 레이어 번호
    public int rightLayer = 31;  // 오른쪽 눈 전용 레이어 번호
    // → leftCamera는 30번 레이어만, rightCamera는 31번 레이어만 렌더링
    // → 이걸로 왼쪽 눈/오른쪽 눈에 다른 화면을 보여줌

    // ============================================================
    // 설정값 (웹 설정 탭에서 CFG: 명령으로 실시간 변경 가능)
    // ============================================================
    private float quadScale = 1.8f;      // Quad(화면 패널)의 크기
    private float quadDistance = 5.0f;   // 카메라에서 Quad까지 거리 (미터)
    private float flipInterval = 1.0f;   // 플립 모드에서 좌↔우 전환 간격 (초)
    private int defaultBright = 50;      // 모드 전환 시 초기 밝기 (%)
    private int targetBright = 30;       // 조절 대상 영역의 시작 밝기 (어두운 쪽)
    private int bgBright = 80;           // 비조절 영역의 밝기 (밝은 쪽)
    private int[] colorOrder = { 0, 1, 2, 3 };  // 색상 순환 순서 (0=빨,1=초,2=파,3=흰)

    // ============================================================
    // 모드별 Quad 그룹
    //
    // 핵심 개념: 각 모드마다 별도의 Quad 세트를 미리 만들어두고,
    // 모드 전환 시 해당 세트만 켜고 나머지는 꺼버림
    // → 이전 모드의 색상이 남아서 겹쳐 보이는 문제를 원천 차단
    //
    // Quad = 카메라 앞에 놓는 단색 사각형. 색상을 바꿔서 밝기 검사
    // ============================================================

    // --- divMode=2, currentEyeTarget=2 → 양안 모드 (눈당 Quad 1개, 전체 단색) ---
    private GameObject grpL_Whole, grpR_Whole;     // 양안용 Quad 그룹 (왼/오)
    private Renderer[] rendL_Whole, rendR_Whole;   // [0] = 단색 Quad 1개

    // --- divMode=2, currentEyeTarget=0or1 → 단안 2분할 (눈당 Quad 2개) ---
    private GameObject grpL_Split, grpR_Split;     // 2분할용 Quad 그룹
    private Renderer[] rendL_Split, rendR_Split;   // [0]=코쪽, [1]=귀쪽

    // --- divMode=3 → 세로 4분할 (눈당 Quad 4개, 수직 띠) ---
    private GameObject grpL_Vert, grpR_Vert;       // 세로4분할용 Quad 그룹
    private Renderer[] rendL_Vert, rendR_Vert;     // [0]~[3] = 1~4번 띠 (왼→오)

    // --- divMode=4 → 십자 4분할 (눈당 Quad 4개, 2x2 격자) ---
    private GameObject grpL_Cross, grpR_Cross;     // 십자4분할용 Quad 그룹
    private Renderer[] rendL_Cross, rendR_Cross;   // [0]=우상, [1]=우하, [2]=좌상, [3]=좌하

    // ============================================================
    // 밝기 변수 (웹 프로토콜 호환 - 이름 변경 금지)
    // ============================================================
    private int leftNasal = 50, leftTemporal = 50;    // divMode=2: 왼쪽 눈 코쪽/귀쪽
    private int rightNasal = 50, rightTemporal = 50;   // divMode=2: 오른쪽 눈 코쪽/귀쪽
    private int leftTop = 50, leftBot = 50;            // divMode=3: 1번 띠, 2번 띠
    private int rightTop = 50, rightBot = 50;          // divMode=3: 3번 띠, 4번 띠
    private int[] quadBright = { 50, 50, 50, 50 };     // divMode=4: 우상/우하/좌상/좌하

    // ============================================================
    // 앱 상태
    // ============================================================
    private enum AppState { ModeSelection, Testing }
    private AppState appState = AppState.ModeSelection;  // 시작 시 모드 선택 화면

    private int divisionMode = 2;         // 현재 분할 모드 (웹의 divMode와 동일)
    private int currentEyeTarget = 2;     // 0=좌안만, 1=우안만, 2=양안
    private int currentColorIndex = 0;    // activeColors 배열에서 현재 인덱스
    private int adjustMode = 0;           // 밝기 조절 대상 (0=전체, 1~=개별 영역)

    // --- 색상 관련 ---
    private Color[] masterColors = { Color.red, Color.green, Color.blue, Color.white };
    private Color[] activeColors;  // colorOrder에 따라 재배열된 색상 배열

    // --- UI 텍스트용 ---
    private string[] targetNames = { "왼쪽 눈", "오른쪽 눈", "양쪽 눈" };
    private string[] quadNames = { "우상단", "우하단", "좌상단", "좌하단" };
    private string[] vertNames = { "1-3번 띠", "2-4번 띠", "1-4번 띠", "2-3번 띠" };

    // --- 플립 모드 (양안 교대 깜빡임) ---
    private bool isFlipMode = false;       // 플립 모드 ON?
    private float nextFlipTime = 0f;       // 다음 전환 시각 (Time.time 기준)
    private bool isLeftEyeShown = true;    // 현재 왼쪽이 보이는 차례?
    private float leftFlipAdj = 1.0f;      // 좌안 플립 보정 (0.0~1.0)
    private int leftMask, rightMask;       // 카메라 레이어 마스크

    // --- 컨트롤러 버튼 이전 프레임 상태 (새로 눌림 감지용) ---
    private bool prevA, prevB, prevT, prevG;

    // --- WebSocket 네트워크 ---
    private ClientWebSocket ws;                                        // WebSocket 연결 객체
    private ConcurrentQueue<string> cmdQueue = new ConcurrentQueue<string>();  // 서버에서 받은 명령 큐 (스레드 안전)
    private string lastStatus = "";    // 마지막 UI 텍스트 (서버 전송용)
    private bool registered, sending;  // 서버 등록 완료? / 현재 전송 중?
    private float lastSendTime;        // 마지막 상태 전송 시각



    // ============================================================
    // Start: 앱 시작 시 초기화
    // ============================================================
    async void Start()
    {
        // 레이어 마스크 초기화 (leftLayer=30, rightLayer=31)
        leftMask = 1 << leftLayer;
        rightMask = 1 << rightLayer;

        if (crosshair != null) crosshair.SetActive(false);

        // ---- OVRCameraRig의 CenterEyeAnchor 카메라 끄기 ----
        // OVRCameraRig는 기본적으로 CenterEyeAnchor가 양쪽 눈에 렌더링함
        // 우리는 Left/Right를 독립 제어해야 하므로 Center를 꺼야 함
        var center = GameObject.Find("CenterEyeAnchor");
        if (center != null)
        {
            var cam = center.GetComponent<Camera>();
            if (cam != null) cam.enabled = false;
        }

        // 색상 순서 초기화 (colorOrder 배열 → activeColors 배열)
        RebuildColors();

        // 카메라 초기 설정 (배경 검정, 원래 설정 백업)
        InitCamera(leftCamera);
        InitCamera(rightCamera);

        // 모드별 Quad 세트 전부 생성
        CreateAllQuads();

        // 모든 Quad 그룹 숨기기
        HideAllGroups();

        // 밝기를 기본값으로 초기화
        ResetBrightness();

        // UI 초기화
        lastStatus = "서버 접속 시도 중...";
        if (statusText != null) statusText.text = lastStatus;

        // 서버 자동 탐색 및 연결 루프 시작 (백그라운드)
        DiscoverAndConnectLoop();
    }

    async void DiscoverAndConnectLoop()
    {
        while (true)
        {
            // 이미 연결되어 있다면 대기
            if (ws != null && ws.State == WebSocketState.Open)
            {
                await Task.Delay(5000);
                continue;
            }

            // 연결 시도
            UpdateStatusInVR("서버 연결 시도 중...");
            
            // 1. 즉시 로컬/기존 IP 시도
            await ConnectServer();

            // 2. 안 되면 탐색 후 다시 시도
            if (ws == null || ws.State != WebSocketState.Open)
            {
                UpdateStatusInVR("서버 탐색 중 (UDP)...");
                await DiscoverServer();
                await ConnectServer();
            }

            // 실패 시 3초 후 재시도
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

    /// <summary>
    /// 카메라 초기 설정
    /// - 원래 cullingMask와 clearFlags를 백업 (나중에 복원용)
    /// - 배경을 검은색으로 설정
    /// </summary>
    void InitCamera(Camera cam)
    {
        if (cam == null) return;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = Color.black;
        cam.cullingMask = 0; // 시작 시 아무것도 안 보임 (Apply()에서 켜줌)
        cam.enabled = true;
    }

    /// <summary>
    /// colorOrder 배열을 기반으로 activeColors 배열을 재구성
    /// 예: colorOrder = {2,0,1,3} → activeColors = {파랑, 빨강, 초록, 흰색}
    /// </summary>
    void RebuildColors()
    {
        activeColors = new Color[colorOrder.Length];
        for (int i = 0; i < colorOrder.Length; i++)
            activeColors[i] = masterColors[Mathf.Clamp(colorOrder[i], 0, 3)];
    }

    /// <summary>
    /// 모든 밝기 변수를 defaultBright 값으로 초기화
    /// 모드 전환 시 호출됨
    /// </summary>
    void ResetBrightness()
    {
        int d = defaultBright;
        leftNasal = d; leftTemporal = d; rightNasal = d; rightTemporal = d;
        leftTop = d; leftBot = d; rightTop = d; rightBot = d;
        for (int i = 0; i < 4; i++) quadBright[i] = d;
    }


    // ============================================================
    // Quad 생성: 모드별로 분리된 Quad 세트를 코드에서 자동 생성
    // ============================================================

    /// <summary>
    /// 4가지 모드에 대해 총 8개의 Quad 그룹을 생성 (왼/오 × 4모드)
    /// </summary>
    void CreateAllQuads()
    {
        // 양안 단색 (divMode=2, eye=2): 눈당 Quad 1개
        grpL_Whole = MakeGroup("L_Whole", leftCamera, leftLayer, 1, out rendL_Whole);
        grpR_Whole = MakeGroup("R_Whole", rightCamera, rightLayer, 1, out rendR_Whole);

        // 단안 2분할 (divMode=2, eye=0or1): 눈당 Quad 2개
        grpL_Split = MakeGroup("L_Split", leftCamera, leftLayer, 2, out rendL_Split);
        grpR_Split = MakeGroup("R_Split", rightCamera, rightLayer, 2, out rendR_Split);

        // 세로 4분할 (divMode=3): 눈당 Quad 4개
        grpL_Vert = MakeGroup("L_Vert", leftCamera, leftLayer, 4, out rendL_Vert);
        grpR_Vert = MakeGroup("R_Vert", rightCamera, rightLayer, 4, out rendR_Vert);

        // 십자 4분할 (divMode=4): 눈당 Quad 4개
        grpL_Cross = MakeGroup("L_Cross", leftCamera, leftLayer, 4, out rendL_Cross);
        grpR_Cross = MakeGroup("R_Cross", rightCamera, rightLayer, 4, out rendR_Cross);
    }

    /// <summary>
    /// Quad 그룹 1개를 생성하는 공통 함수
    /// - 빈 GameObject를 만들고 카메라의 자식으로 붙임
    /// - 그 안에 count개의 Quad를 생성
    /// - 각 Quad에 Unlit/Color 머티리얼 적용 (조명 영향 안 받는 단색)
    /// </summary>
    /// <param name="name">그룹 이름 (디버그용)</param>
    /// <param name="parent">부모 카메라 (LeftEyeAnchor 또는 RightEyeAnchor)</param>
    /// <param name="layer">레이어 번호 (30=왼쪽, 31=오른쪽)</param>
    /// <param name="count">Quad 개수</param>
    /// <param name="rends">생성된 Renderer 배열 (out)</param>
    /// <returns>생성된 그룹 GameObject</returns>
    GameObject MakeGroup(string name, Camera parent, int layer, int count, out Renderer[] rends)
    {
        rends = new Renderer[count];
        var grp = new GameObject(name);

        // 카메라의 자식으로 붙이고, 카메라 앞 quadDistance 거리에 배치
        if (parent != null)
        {
            grp.transform.parent = parent.transform;
            grp.transform.localPosition = new Vector3(0, 0, quadDistance);
            grp.transform.localEulerAngles = Vector3.zero;
        }
        grp.layer = layer;

        // Quad들 생성
        for (int i = 0; i < count; i++)
        {
            var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
            q.name = $"Q{i}";
            q.layer = layer;  // 해당 눈 전용 레이어에 배치
            q.transform.SetParent(grp.transform);
            q.transform.localRotation = Quaternion.identity;

            // Unlit/Color 셰이더 적용 (빛 영향 안 받는 순수 단색)
            var r = q.GetComponent<Renderer>();
            var sh = Shader.Find("Unlit/Color") ?? Shader.Find("Sprites/Default");
            r.material = new Material(sh);
            r.material.color = Color.black;  // 시작은 검은색

            // 충돌체 불필요 → 삭제 (성능)
            Destroy(q.GetComponent<Collider>());
            rends[i] = r;
        }

        // 그룹과 모든 자식의 레이어를 통일
        SetLayerAll(grp, layer);

        // 시작 시 비활성 (모드 전환 시 필요한 것만 켬)
        grp.SetActive(false);
        return grp;
    }

    /// <summary>
    /// 모든 Quad 그룹의 거리를 quadDistance로 업데이트
    /// 웹에서 CFG:DISTANCE 변경 시 호출
    /// </summary>
    void UpdateDistances()
    {
        var all = new[] { grpL_Whole, grpR_Whole, grpL_Split, grpR_Split,
                          grpL_Vert, grpR_Vert, grpL_Cross, grpR_Cross };
        foreach (var g in all)
            if (g != null) g.transform.localPosition = new Vector3(0, 0, quadDistance);
    }


    // ============================================================
    // Quad 그룹 활성화/비활성화
    // 모드 전환 시: 해당 모드의 Quad 세트만 켜고 나머지 전부 끔
    // → 이전 모드의 색상이 남아서 겹쳐 보이는 문제 원천 차단
    // ============================================================

    /// <summary>모든 Quad 그룹 숨기기</summary>
    void HideAllGroups()
    {
        var all = new[] { grpL_Whole, grpR_Whole, grpL_Split, grpR_Split,
                          grpL_Vert, grpR_Vert, grpL_Cross, grpR_Cross };
        foreach (var g in all)
            if (g != null) g.SetActive(false);
    }

    /// <summary>
    /// 현재 모드에 맞는 Quad 그룹만 활성화
    /// divMode=2일 때 양안(eye=2)이면 Whole, 단안이면 Split 사용
    /// </summary>
    void ShowActiveGroups()
    {
        HideAllGroups();

        if (divisionMode == 2)
        {
            if (currentEyeTarget == 2) // 양안 모드: 단색 Quad 1개씩
            {
                if (grpL_Whole) grpL_Whole.SetActive(true);
                if (grpR_Whole) grpR_Whole.SetActive(true);
            }
            else // 단안 모드: 코쪽/귀쪽 Quad 2개씩
            {
                if (grpL_Split) grpL_Split.SetActive(true);
                if (grpR_Split) grpR_Split.SetActive(true);
            }
        }
        else if (divisionMode == 3) // 세로 4분할
        {
            if (grpL_Vert) grpL_Vert.SetActive(true);
            if (grpR_Vert) grpR_Vert.SetActive(true);
        }
        else if (divisionMode == 4) // 십자 4분할
        {
            if (grpL_Cross) grpL_Cross.SetActive(true);
            if (grpR_Cross) grpR_Cross.SetActive(true);
        }
    }


    // ============================================================
    // 레이아웃: Quad의 위치와 크기를 모드에 맞게 배치
    // ============================================================

    /// <summary>모든 모드의 Quad 레이아웃을 한번에 갱신</summary>
    void LayoutAll()
    {
        float s = quadScale;
        LayoutSingle(rendL_Whole, s);
        LayoutSingle(rendR_Whole, s);
        Layout2Split(rendL_Split, s, true);   // isLeft=true → 코쪽이 오른쪽(+x)
        Layout2Split(rendR_Split, s, false);  // isLeft=false → 코쪽이 왼쪽(-x)
        LayoutVert4(rendL_Vert, s);
        LayoutVert4(rendR_Vert, s);
        LayoutCross4(rendL_Cross, s);
        LayoutCross4(rendR_Cross, s);
    }

    /// <summary>양안 단색: Quad 1개를 화면 전체 크기로</summary>
    void LayoutSingle(Renderer[] q, float s)
    {
        if (q == null || q.Length < 1) return;
        q[0].transform.localPosition = Vector3.zero;
        q[0].transform.localScale = new Vector3(2 * s, 2 * s, 1);
    }

    /// <summary>
    /// 단안 2분할: Quad 2개를 좌우로 배치
    /// [0]=코쪽, [1]=귀쪽
    /// 왼쪽 눈(isLeft=true): 코=오른쪽(+x), 귀=왼쪽(-x)
    /// 오른쪽 눈(isLeft=false): 코=왼쪽(-x), 귀=오른쪽(+x)
    /// </summary>
    void Layout2Split(Renderer[] q, float s, bool isLeft)
    {
        if (q == null || q.Length < 2) return;
        float nasalX = isLeft ? s / 2f : -s / 2f;    // 코쪽 위치
        float tempX = isLeft ? -s / 2f : s / 2f;     // 귀쪽 위치

        q[0].transform.localPosition = new Vector3(nasalX, 0, 0);
        q[0].transform.localScale = new Vector3(s, 2 * s, 1);
        q[1].transform.localPosition = new Vector3(tempX, 0, 0);
        q[1].transform.localScale = new Vector3(s, 2 * s, 1);
    }

    /// <summary>세로 4분할: 4개의 수직 띠를 왼→오 순서로 배치</summary>
    void LayoutVert4(Renderer[] q, float s)
    {
        if (q == null || q.Length < 4) return;
        float w = (2 * s) / 4f;  // 각 띠의 너비 = 전체÷4
        for (int i = 0; i < 4; i++)
        {
            q[i].transform.localPosition = new Vector3(-s + w / 2f + w * i, 0, 0);
            q[i].transform.localScale = new Vector3(w, 2 * s, 1);
        }
    }

    /// <summary>십자 4분할: 2×2 격자 배치 (우상/우하/좌상/좌하)</summary>
    void LayoutCross4(Renderer[] q, float s)
    {
        if (q == null || q.Length < 4) return;
        float o = s / 2f;  // 중심에서 각 Quad 중심까지 거리
        q[0].transform.localPosition = new Vector3(o, o, 0);    // 우상
        q[1].transform.localPosition = new Vector3(o, -o, 0);   // 우하
        q[2].transform.localPosition = new Vector3(-o, o, 0);   // 좌상
        q[3].transform.localPosition = new Vector3(-o, -o, 0);  // 좌하
        for (int i = 0; i < 4; i++)
            q[i].transform.localScale = new Vector3(s, s, 1);
    }


    // ============================================================
    // 카메라 블랙아웃: 한쪽 눈을 완전히 끄는 핵심 함수
    //
    // black=true  → cullingMask=0 (아무것도 안 그림) + 배경 검정
    //              = 해당 렌즈가 완전히 검은색
    // black=false → 원래 설정 복원 → Quad가 다시 보임
    // ============================================================
    /// <summary>
    /// 카메라 블랙아웃: 한쪽 눈을 완전히 끄는 핵심 함수
    /// </summary>
    /// <param name="cam">카메라 객체</param>
    /// <param name="black">true면 화면 차단, false면 레이어 마스크 복구</param>
    /// <param name="mask">차단 해제 시 적용할 레이어 마스크 (LeftEye/RightEye)</param>
    void Blackout(Camera cam, bool black, int mask)
    {
        if (cam == null) return;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = Color.black;
        // 숨겨야 할 경우 0 (아무것도 안 보임), 아닐 경우 전용 마스크 할당
        cam.cullingMask = black ? 0 : mask;
    }


    // ============================================================
    // ★ Apply: 모든 변경사항을 실제 화면에 반영하는 핵심 함수
    //
    // 호출 시점: 모든 명령 처리 후, 플립 전환 후, 모드 전환 후
    // 순서: Quad 그룹 전환 → 레이아웃 → 카메라 블랙아웃 → 색상 적용
    // ============================================================
    void Apply()
    {
        // 1) 현재 모드에 맞는 Quad 그룹만 활성화
        ShowActiveGroups();

        // 2) 모든 Quad의 위치/크기 재계산
        LayoutAll();

        // 3) 어느 눈이 활성인지 결정
        bool leftOn = (currentEyeTarget == 0 || currentEyeTarget == 2);
        bool rightOn = (currentEyeTarget == 1 || currentEyeTarget == 2);

        // 플립 모드: 양안(eye=2) + divMode=2 일 때만 교대
        if (isFlipMode && divisionMode == 2 && currentEyeTarget == 2)
        {
            leftOn = isLeftEyeShown;
            rightOn = !isLeftEyeShown;
        }

        // 4) 비활성 눈은 카메라 블랙아웃 (렌즈 전체 검정)
        // 좌안 카메라는 LeftEye 레이어(30)만, 우안 카메라는 RightEye 레이어(31)만 봅니다.
        Blackout(leftCamera, !leftOn, leftMask);
        Blackout(rightCamera, !rightOn, rightMask);

        // 5) 현재 색상 가져오기
        Color bc = (activeColors != null && currentColorIndex < activeColors.Length)
            ? activeColors[currentColorIndex] : Color.white;

        // 6) 모드별로 Quad에 색상 적용
        if (divisionMode == 2)
        {
            if (currentEyeTarget == 2) // 양안 단색
            {
                // leftNasal을 "왼쪽 눈 전체 밝기"로 사용 (양안 모드에서는 코/귀 구분 없음)
                SetColor(rendL_Whole, 0, leftNasal, bc, leftOn);
                SetColor(rendR_Whole, 0, rightNasal, bc, rightOn);
            }
            else // 단안 2분할
            {
                SetColor(rendL_Split, 0, leftNasal, bc, leftOn);      // 왼눈 코쪽
                SetColor(rendL_Split, 1, leftTemporal, bc, leftOn);   // 왼눈 귀쪽
                SetColor(rendR_Split, 0, rightNasal, bc, rightOn);    // 오른눈 코쪽
                SetColor(rendR_Split, 1, rightTemporal, bc, rightOn); // 오른눈 귀쪽
            }
        }
        else if (divisionMode == 3) // 세로 4분할
        {
            int[] v = { leftTop, leftBot, rightTop, rightBot };
            for (int i = 0; i < 4; i++)
            {
                // 양쪽 눈에 같은 패턴 표시 (세로 띠는 양안 공통)
                SetColor(rendL_Vert, i, v[i], bc, leftOn);
                SetColor(rendR_Vert, i, v[i], bc, rightOn);
            }
        }
        else if (divisionMode == 4) // 십자 4분할
        {
            for (int i = 0; i < 4; i++)
            {
                SetColor(rendL_Cross, i, quadBright[i], bc, leftOn);
                SetColor(rendR_Cross, i, quadBright[i], bc, rightOn);
            }
        }

        // 7) UI 텍스트 갱신 + 서버에 상태 전송
        UpdateStatusText();
    }

    /// <summary>
    /// Quad 1개의 색상을 설정하는 유틸 함수
    /// bright(0~100)를 baseColor에 곱해서 실제 색상으로 변환
    /// </summary>
    void SetColor(Renderer[] q, int idx, int bright, Color bc, bool on)
    {
        if (q == null || idx >= q.Length || q[idx] == null) return;
        if (!on) { q[idx].material.color = Color.black; return; }

        // 1. 감마 2.2 보정 (사람의 눈은 비선형적으로 밝기를 느낌)
        float intensity = Mathf.Pow(bright / 100f, 2.2f);

        // 2. 좌안 플립 보정 적용 (플립 모드 중이고 이 렌더러가 좌안 소속인 경우)
        // 레이어 30이 좌안 레이어임
        if (isFlipMode && q[idx].gameObject.layer == leftLayer) {
            intensity *= leftFlipAdj;
        }

        Color c = bc * intensity;
        c.a = 1f;
        q[idx].material.color = c;
    }


    // ============================================================
    // 모드 전환
    // ============================================================

    /// <summary>
    /// 분할 모드를 전환하고 초기화
    /// </summary>
    /// <param name="newDiv">새 분할 모드 (2/3/4)</param>
    /// <param name="newEye">눈 대상 변경 (-1이면 유지)</param>
    void SwitchMode(int newDiv, int newEye = -1)
    {
        divisionMode = newDiv;
        if (newEye >= 0) currentEyeTarget = newEye;
        adjustMode = 0;       // 조절 대상 초기화
        ResetBrightness();    // 밝기 초기화
        Apply();              // 화면 갱신
    }

    // Apply() 중복 정의 제거됨 (489번 라인의 정의를 사용)


    // ============================================================
    // 조절 대상 변경 (CHANGE_TARGET 명령)
    //
    // 원리: "타겟 영역=어둡게(targetBright), 나머지=밝게(bgBright)"로 세팅
    // → 검사자가 어두운 쪽 밝기를 올려서 밝은 쪽과 같아지는 점을 찾음
    // ============================================================
    void SetAdjustMode(int m)
    {
        adjustMode = m;
        int t = targetBright, b = bgBright, d = defaultBright;

        if (divisionMode == 2)
        {
            if (currentEyeTarget == 2) // 양안 모드: 왼눈 전체 vs 오른눈 전체
            {
                if (m == 1) { leftNasal = t; leftTemporal = t; rightNasal = b; rightTemporal = b; }       // 왼눈이 타겟
                else if (m == 2) { leftNasal = b; leftTemporal = b; rightNasal = t; rightTemporal = t; }  // 오른눈이 타겟
                else { leftNasal = d; leftTemporal = d; rightNasal = d; rightTemporal = d; }              // 전체 균일
            }
            else // 단안 모드: 코쪽 vs 귀쪽
            {
                if (m == 1) { leftNasal = t; rightNasal = t; leftTemporal = b; rightTemporal = b; }       // 코쪽이 타겟
                else if (m == 2) { leftTemporal = t; rightTemporal = t; leftNasal = b; rightNasal = b; }  // 귀쪽이 타겟
                else { leftNasal = d; rightNasal = d; leftTemporal = d; rightTemporal = d; }
            }
        }
        else if (divisionMode == 3) // 세로 4분할: 띠 조합이 타겟
        {
            if (m != 0)
            {
                leftTop = (m == 1 || m == 3) ? t : b;   // adjustMode에 따라 특정 띠 조합이 타겟
                leftBot = (m == 2 || m == 4) ? t : b;
                rightTop = (m == 1 || m == 4) ? t : b;
                rightBot = (m == 2 || m == 3) ? t : b;
            }
            else { leftTop = d; leftBot = d; rightTop = d; rightBot = d; }
        }
        else if (divisionMode == 4) // 십자 4분할: 1개 사분면이 타겟
        {
            if (m != 0)
            {
                int ti = m - 1;  // 타겟 인덱스
                for (int i = 0; i < 4; i++) quadBright[i] = (i == ti) ? t : b;
            }
            else { for (int i = 0; i < 4; i++) quadBright[i] = d; }
        }
    }


    // ============================================================
    // 밝기 증감 (BRIGHT_UP / BRIGHT_DOWN)
    // ============================================================
    void ChangeBrightness(int amt)
    {
        bool aL = (currentEyeTarget == 0 || currentEyeTarget == 2);  // 왼쪽 눈 영향?
        bool aR = (currentEyeTarget == 1 || currentEyeTarget == 2);  // 오른쪽 눈 영향?

        if (divisionMode == 2)
        {
            if (currentEyeTarget == 2) // 양안 모드: 눈 단위로 조절 (코/귀 동기화)
            {
                if (adjustMode == 0 || adjustMode == 1)
                {
                    leftNasal = Cl(leftNasal + amt);
                    leftTemporal = leftNasal;  // 양안 모드에서는 코=귀 (눈 전체가 단색)
                }
                if (adjustMode == 0 || adjustMode == 2)
                {
                    rightNasal = Cl(rightNasal + amt);
                    rightTemporal = rightNasal;
                }
            }
            else // 단안 모드: 코쪽/귀쪽 독립 조절
            {
                if (adjustMode == 0 || adjustMode == 1) // 코쪽
                {
                    if (aL) leftNasal = Cl(leftNasal + amt);
                    if (aR) rightNasal = Cl(rightNasal + amt);
                }
                if (adjustMode == 0 || adjustMode == 2) // 귀쪽
                {
                    if (aL) leftTemporal = Cl(leftTemporal + amt);
                    if (aR) rightTemporal = Cl(rightTemporal + amt);
                }
            }
        }
        else if (divisionMode == 3) // 세로 4분할
        {
            if (adjustMode == 0) // 전체
            {
                leftTop = Cl(leftTop + amt); leftBot = Cl(leftBot + amt);
                rightTop = Cl(rightTop + amt); rightBot = Cl(rightBot + amt);
            }
            else // 조합별
            {
                if (adjustMode == 1 || adjustMode == 3) leftTop = Cl(leftTop + amt);
                if (adjustMode == 2 || adjustMode == 4) leftBot = Cl(leftBot + amt);
                if (adjustMode == 1 || adjustMode == 4) rightTop = Cl(rightTop + amt);
                if (adjustMode == 2 || adjustMode == 3) rightBot = Cl(rightBot + amt);
            }
        }
        else if (divisionMode == 4) // 십자 4분할
        {
            if (adjustMode == 0) // 전체
            {
                for (int i = 0; i < 4; i++) quadBright[i] = Cl(quadBright[i] + amt);
            }
            else // 개별 사분면
            {
                quadBright[adjustMode - 1] = Cl(quadBright[adjustMode - 1] + amt);
            }
        }
    }

    /// <summary>0~100 범위로 클램프하는 유틸</summary>
    int Cl(int v) => Mathf.Clamp(v, 0, 100);


    // ============================================================
    // Update: 매 프레임 실행
    // ============================================================
    void Update()
    {
        // 1) 큐에 쌓인 서버 명령들 처리
        while (cmdQueue.TryDequeue(out string cmd))
            ProcessCommand(cmd);

        // 2) 플립 모드: 일정 간격으로 좌↔우 전환
        if (isFlipMode && divisionMode == 2 && currentEyeTarget == 2 && Time.time >= nextFlipTime)
        {
            isLeftEyeShown = !isLeftEyeShown;
            nextFlipTime = Time.time + flipInterval;
            Apply();
        }

        // 3) 물리적 입력 (VR 컨트롤러 + 키보드)
        HandleInput();
    }


    // ============================================================
    // 입력 처리
    // ============================================================
    void HandleInput()
    {
        var kb = Keyboard.current;

        // --- Quest 오른손 컨트롤러 버튼 읽기 ---
        bool cA = false, cB = false, cT = false, cG = false;
        Vector2 st = Vector2.zero;

        var devs = new List<UnityEngine.XR.InputDevice>();
        UnityEngine.XR.InputDevices.GetDevicesWithCharacteristics(
            UnityEngine.XR.InputDeviceCharacteristics.Right | UnityEngine.XR.InputDeviceCharacteristics.Controller, devs);

        if (devs.Count > 0)
        {
            var d = devs[0];
            d.TryGetFeatureValue(UnityEngine.XR.CommonUsages.primaryButton, out cA);      // A 버튼
            d.TryGetFeatureValue(UnityEngine.XR.CommonUsages.secondaryButton, out cB);     // B 버튼
            d.TryGetFeatureValue(UnityEngine.XR.CommonUsages.triggerButton, out cT);        // 검지 트리거
            d.TryGetFeatureValue(UnityEngine.XR.CommonUsages.gripButton, out cG);           // 옆면 그립
            d.TryGetFeatureValue(UnityEngine.XR.CommonUsages.primary2DAxis, out st);        // 조이스틱
        }

        // "이번 프레임에 새로 눌렸는지" 판정
        bool bA = cA && !prevA, bB = cB && !prevB, bT = cT && !prevT, bG = cG && !prevG;
        prevA = cA; prevB = cB; prevT = cT; prevG = cG;

        if (kb == null) kb = InputSystem.GetDevice<Keyboard>();

        // --- 모드 선택 화면 ---
        if (appState == AppState.ModeSelection)
        {
            bool kA = kb != null && (kb.enterKey.wasPressedThisFrame || kb.cKey.wasPressedThisFrame || kb.leftCtrlKey.wasPressedThisFrame);
            bool kB = kb != null && kb.spaceKey.wasPressedThisFrame;
            if (bA || kA) { SwitchMode(2, 2); StartTest(); }      // A = 양안 모드로 시작
            else if (bB || kB) { SwitchMode(3, 2); StartTest(); }  // B = 세로4분할로 시작
            return;
        }

        // --- 검사 중 입력 매핑 ---
        if (bA) ProcessCommand("CHANGE_COLOR");        // A = 색상 순환
        if (bB) ProcessCommand("CHANGE_TARGET");       // B = 조절 대상 순환
        if (bT) ProcessCommand("TOGGLE_FLIP");         // 트리거 = 플립 토글
        if (bG) ProcessCommand("EYE_TARGET_TOGGLE");   // 그립 = 좌→우→양 순환

        // 조이스틱 위/아래 = 밝기 +/-
        if (st.y > 0.5f && Time.frameCount % 10 == 0) ProcessCommand("BRIGHT_UP");
        else if (st.y < -0.5f && Time.frameCount % 10 == 0) ProcessCommand("BRIGHT_DOWN");

        // 조이스틱 좌/우 = 눈 전환
        if (st.x < -0.7f && Time.frameCount % 20 == 0) ProcessCommand("EYE_LEFT");
        else if (st.x > 0.7f && Time.frameCount % 20 == 0) ProcessCommand("EYE_RIGHT");

        // 키보드 화살표 (에디터 테스트용)
        if (kb != null)
        {
            if (kb.upArrowKey.wasPressedThisFrame) ProcessCommand("BRIGHT_UP");
            if (kb.downArrowKey.wasPressedThisFrame) ProcessCommand("BRIGHT_DOWN");
        }
    }

    /// <summary>검사 시작 (모드 선택 → 검사 화면 전환)</summary>
    void StartTest()
    {
        appState = AppState.Testing;
        if (statusText != null) statusText.gameObject.SetActive(false);
        if (crosshair != null) crosshair.SetActive(true);
        Apply();
    }


    // ============================================================
    // 명령 처리
    // 서버에서 받은 명령 or 컨트롤러 입력을 여기서 통합 처리
    // ============================================================
    void ProcessCommand(string cmd)
    {
        if (string.IsNullOrEmpty(cmd)) return;
        cmd = cmd.Trim(); // 공백/줄바꿈 제거

        // 무시할 메시지 (기기 목록, JSON 상태)
        if (cmd.StartsWith("DEVICE_LIST:") || cmd.StartsWith("{")) return;

        // --- 모드 전환 명령 (웹 호환) ---
        if (cmd == "MODE_2") { SwitchMode(2, 2); StartTest(); return; }   // 2분할 (양안 강제)
        if (cmd == "MODE_4V") { SwitchMode(3, 2); StartTest(); return; } // 세로4분할 (양안 강제)
        if (cmd == "MODE_4") { SwitchMode(4, 2); StartTest(); return; }   // 십자4분할 (양안 강제)

        // 아직 모드 선택 화면이면 자동으로 검사 시작
        if (appState == AppState.ModeSelection) { SwitchMode(2, 2); StartTest(); }

        // --- 설정 변경 명령 (웹 설정 탭) ---
        if (cmd.StartsWith("CFG:")) { ApplyCfg(cmd); return; }

        // --- SET_LEFT_FLIP_ADJ: 좌안 플립 보정 세기 ---
        if (cmd.StartsWith("SET_LEFT_FLIP_ADJ:"))
        {
            var p = cmd.Split(':');
            if (p.Length == 2 && float.TryParse(p[1], out float v))
            {
                leftFlipAdj = Mathf.Clamp01(v / 100f);
            }
            Apply(); return;
        }

        // --- SET_VAL: 개별 밝기 직접 지정 (웹 패널에서 입력) ---
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
                    case "L_ALL": leftNasal = leftTemporal = v; break;   // 왼눈 전체
                    case "R_ALL": rightNasal = rightTemporal = v; break; // 오른눈 전체
                    case "L_TOP": leftTop = v; break;    // 세로 1번 띠
                    case "L_BOT": leftBot = v; break;    // 세로 2번 띠
                    case "R_TOP": rightTop = v; break;   // 세로 3번 띠
                    case "R_BOT": rightBot = v; break;   // 세로 4번 띠
                    case "Q0": quadBright[0] = v; break; // 십자 우상
                    case "Q1": quadBright[1] = v; break; // 십자 우하
                    case "Q2": quadBright[2] = v; break; // 십자 좌상
                    case "Q3": quadBright[3] = v; break; // 십자 좌하
                }
            }
            Apply(); return;
        }

        // --- 플립 간격 변경 ---
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

        // --- 일반 명령 ---
        switch (cmd)
        {
            case "TOGGLE_FLIP":        // 플립 모드 ON/OFF
                isFlipMode = !isFlipMode;
                nextFlipTime = Time.time + flipInterval;
                isLeftEyeShown = true;
                break;
            case "EYE_LEFT":           // 왼쪽 눈만
                currentEyeTarget = 0; break;
            case "EYE_RIGHT":          // 오른쪽 눈만
                currentEyeTarget = 1; break;
            case "EYE_BOTH":           // 양쪽 눈
                currentEyeTarget = 2; break;
            case "EYE_TARGET_TOGGLE":  // 좌→우→양 순환
                currentEyeTarget = (currentEyeTarget + 1) % 3; break;
            case "CHANGE_COLOR":       // 색상 순환 (빨→초→파→흰...)
                currentColorIndex = (currentColorIndex + 1) % (activeColors?.Length ?? 4);
                break;
            case "CHANGE_TARGET":      // 조절 대상 순환
                int max = (divisionMode == 2) ? 3 : 5;
                SetAdjustMode((adjustMode + 1) % max);
                break;
            case "BRIGHT_UP":          // 밝기 +5%
                ChangeBrightness(5); break;
            case "BRIGHT_DOWN":        // 밝기 -5%
                ChangeBrightness(-5); break;
            case "BRIGHT_UP_L":        // 왼눈만 +5%
                leftNasal = Cl(leftNasal + 5); leftTemporal = Cl(leftTemporal + 5); break;
            case "BRIGHT_DOWN_L":      // 왼눈만 -5%
                leftNasal = Cl(leftNasal - 5); leftTemporal = Cl(leftTemporal - 5); break;
            case "BRIGHT_UP_R":        // 오른눈만 +5%
                rightNasal = Cl(rightNasal + 5); rightTemporal = Cl(rightTemporal + 5); break;
            case "BRIGHT_DOWN_R":      // 오른눈만 -5%
                rightNasal = Cl(rightNasal - 5); rightTemporal = Cl(rightTemporal - 5); break;
        }
        Apply();
    }


    // ============================================================
    // CFG 설정 변경 처리
    // 웹 설정 탭에서 "적용" 버튼 누르면 "CFG:KEY:VALUE" 형태로 전송됨
    // ============================================================
    void ApplyCfg(string cmd)
    {
        var p = cmd.Split(':');
        if (p.Length < 3) return;

        switch (p[1])
        {
            case "SCALE":
            case "scale":          // Quad 크기 변경
                if (float.TryParse(p[2], out float sc))
                { quadScale = Mathf.Max(0.5f, sc); LayoutAll(); }
                break;
            case "DISTANCE":
            case "distance":       // Quad 거리 변경
                if (float.TryParse(p[2], out float di))
                { quadDistance = Mathf.Max(1f, di); UpdateDistances(); }
                break;
            case "FLIP_INTERVAL":
            case "flipInterval":   // 플립 간격 변경
                if (float.TryParse(p[2], out float fi))
                    flipInterval = Mathf.Max(0.1f, fi);
                break;
            case "DEFAULT_BRIGHT":
            case "defaultBright":  // 기본 밝기 변경
                if (int.TryParse(p[2], out int db))
                    defaultBright = Mathf.Clamp(db, 0, 100);
                break;
            case "TARGET_BRIGHT":
            case "targetBright":   // 타겟 밝기 변경
                if (int.TryParse(p[2], out int tb))
                    targetBright = Mathf.Clamp(tb, 0, 100);
                break;
            case "BG_BRIGHT":
            case "bgBright":       // 배경 밝기 변경
                if (int.TryParse(p[2], out int bb))
                    bgBright = Mathf.Clamp(bb, 0, 100);
                break;
            case "COLOR_ORDER":
            case "colorOrder":     // 색상 순서 변경 (예: "2,0,1,3")
                var parts = p[2].Split(',');
                int[] order = new int[parts.Length];
                for (int i = 0; i < parts.Length; i++)
                    int.TryParse(parts[i].Trim(), out order[i]);
                colorOrder = order;
                RebuildColors();       // activeColors 재구성
                currentColorIndex = 0; // 첫 번째 색상으로 리셋
                break;
        }
        Apply();
        SendState();  // 변경된 설정값을 웹에 전송
    }


    // ============================================================
    // UI 텍스트 갱신
    // ============================================================
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
        if (divisionMode == 3)
            modeStr = adjustMode == 0 ? "세로 전체" : vertNames[adjustMode - 1];
        else if (divisionMode == 4)
            modeStr = adjustMode == 0 ? "전체 조절" : $"{quadNames[adjustMode - 1]} 조절";
        else // divisionMode == 2
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


    // ============================================================
    // 네트워크: 서버 자동 탐색 (UDP 브로드캐스트 수신)
    // 서버가 "EYE_SERVER:IP:PORT" 형태로 주기적으로 브로드캐스트
    // ============================================================
    async Task DiscoverServer()
    {
        if (ws != null && ws.State == WebSocketState.Open) return;
        using (var udp = new UdpClient())
        {
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, 50002)); // 50002 포트에서 대기
            // 원래 10초 타임아웃이었으나 유선 연결 시 무선 UDP를 찾느라 지연되는 현상을 막기 위해 2초로 단축
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

    /// <summary>
    /// WebSocket 서버 연결
    /// 로컬(127.0.0.1) → 원격(serverIP) 순서로 시도
    /// </summary>
    async Task ConnectServer()
    {
        bool ok = await TryWs($"ws://127.0.0.1:{serverPort}/ws");
        if (!ok) ok = await TryWs($"ws://{serverIP}:{serverPort}/ws");
        if (!ok && serverPort != 80) ok = await TryWs($"ws://{serverIP}/vr/ws");

        if (ok)
        {
            try
            {
                // 서버에 기기 등록 (고유 ID 전송)
                await ws.SendAsync(Seg($"REG:{SystemInfo.deviceUniqueIdentifier}"),
                    WebSocketMessageType.Text, true, CancellationToken.None);
                registered = true;
                lastSendTime = Time.time;
                UpdateStartScreenText();
                SendState();
                RecvLoop(); // 수신 루프 시작
            }
            catch { }
        }
    }

    /// <summary>WebSocket 연결 시도 (3초 타임아웃)</summary>
    async Task<bool> TryWs(string url)
    {
        try
        {
            ws = new ClientWebSocket();
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1.5))) // 빠른 재시도를 위해 3초 -> 1.5초 단축
            {
                await ws.ConnectAsync(new Uri(url), cts.Token);
                return ws.State == WebSocketState.Open;
            }
        }
        catch { return false; }
    }

    /// <summary>서버로부터 명령 수신 루프 (백그라운드)</summary>
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
                        cmdQueue.Enqueue(msg); // 메인 스레드에서 처리하도록 큐에 넣기
                }
                else if (r.MessageType == WebSocketMessageType.Close) break;
            }
            catch { break; }
        }
    }

    /// <summary>
    /// 현재 상태를 JSON으로 서버에 전송
    /// 웹 관제 패널이 이 데이터를 받아서 미러 화면 + 설정 동기화
    /// 최소 0.1초 간격으로 전송 (과도한 전송 방지)
    /// </summary>
    async void SendState()
    {
        if (!registered || ws == null || ws.State != WebSocketState.Open) return;
        if (sending || (Time.time - lastSendTime) < 0.1f) return;
        sending = true; lastSendTime = Time.time;

        try
        {
            var data = new VRStateData
            {
                // 기존 웹 호환 필드
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
                leftTop = leftTop,
                leftBot = leftBot,
                rightTop = rightTop,
                rightBot = rightBot,
                uiText = lastStatus,
                currentEyeTarget = currentEyeTarget,
                isFlipMode = isFlipMode,
                isLeftEyeShown = isLeftEyeShown,
                leftFlipAdj = leftFlipAdj * 100f, // 0~100으로 변환해서 전송
                // 설정값 (웹 설정 탭 동기화용)
                cfgScale = quadScale,
                cfgDistance = quadDistance,
                cfgFlipInterval = flipInterval,
                cfgDefaultBright = defaultBright,
                cfgTargetBright = targetBright,
                cfgBgBright = bgBright,
                cfgColorOrder = string.Join(",", colorOrder)
            };

            string json = JsonUtility.ToJson(data);
            if (!string.IsNullOrEmpty(json))
                await ws.SendAsync(Seg(json), WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch { }
        finally { sending = false; }
    }

    /// <summary>string → byte[] ArraySegment 변환 유틸</summary>
    ArraySegment<byte> Seg(string s) => new ArraySegment<byte>(Encoding.UTF8.GetBytes(s));

    /// <summary>앱 종료 시 WebSocket 정리</summary>
    async void OnDestroy()
    {
        if (ws != null && ws.State == WebSocketState.Open)
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None);
    }

    // 중복 정의된 Blackout(), SwitchModeQuads(), SetGroupActive() 제거됨

    /// <summary>오브젝트와 모든 자식의 레이어를 재귀적으로 변경</summary>
    void SetLayerAll(GameObject obj, int layer)
    {
        if (obj == null) return;
        obj.layer = layer;
        foreach (Transform c in obj.transform)
            SetLayerAll(c.gameObject, layer);
    }
}