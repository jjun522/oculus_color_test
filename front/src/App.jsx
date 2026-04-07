import React, { useState, useEffect, useRef } from 'react';

const App = () => {
  const [status, setStatus] = useState('서버 연결 중...');
  const [activeTab, setActiveTab] = useState('control'); // 'control' | 'settings'
  const [vrState, setVrState] = useState({
    divMode: 2, colorIdx: 0,
    leftNasal: 50, leftTemporal: 50, rightNasal: 50, rightTemporal: 50,
    q0: 50, q1: 50, q2: 50, q3: 50,
    leftTop: 50, leftBot: 50, rightTop: 50, rightBot: 50,
    uiText: "VR 기기의 신호를 기다리고 있습니다...",
    currentEyeTarget: 2, isFlipMode: false, isLeftEyeShown: true,
    leftFlipAdj: 100,
    cfgScale: 1.8, cfgDistance: 5.0, cfgFlipInterval: 1.0,
    cfgColorOrder: "0,1,2,3"
  });

  const [logs, setLogs] = useState([]);
  const [inputVal, setInputVal] = useState({
    leftNasal: 50, leftTemporal: 50, rightNasal: 50, rightTemporal: 50,
    q0: 50, q1: 50, q2: 50, q3: 50,
    leftTop: 50, leftBot: 50, rightTop: 50, rightBot: 50,
    flipInterval: 1.0, leftFlipAdj: 100
  });
  const [cfgInput, setCfgInput] = useState({
    scale: 1.8, distance: 5.0, flipInterval: 1.0,
    defaultBright: 50, targetBright: 30, bgBright: 80,
    colorOrder: "0,1,2,3"
  });
  const [connectedDevices, setConnectedDevices] = useState([]);
  const [selectedDevice, setSelectedDevice] = useState('ALL');
  const wsRef = useRef(null);
  const logEndRef = useRef(null);

  const addLog = (type, msg, color) => {
    const time = new Date().toLocaleTimeString();
    setLogs((prev) => [...prev, { time, type, msg, color }].slice(-20));
  };

  useEffect(() => {
    let ws;
    let reconnectTimer;
    let isComponentMounted = true;

    const connect = () => {
      if (!isComponentMounted) return;
      
      // 이미 연결 시도 중이거나 열려있다면 중복 생성 방지
      if (wsRef.current && (wsRef.current.readyState === WebSocket.CONNECTING || wsRef.current.readyState === WebSocket.OPEN)) {
        return;
      }

      // 중계 서버 포트(12346)로 연결
      ws = new WebSocket(`ws://${window.location.hostname}:12346/ws`);
      wsRef.current = ws;

      ws.onopen = () => { 
        if (!isComponentMounted) return;
        setStatus('🟢 정상 연결됨'); 
        addLog('시스템', '서버 연결 성공', '#10b981'); 
        // 서버에 WEB 클라이언트임을 명시적으로 등록 (서버의 receive_text 대기 해소)
        // REG: 접두사를 피하여 서버가 VR 기기로 오인하지 않게 함
        ws.send('WEB_MASTER');
      };

      ws.onclose = (e) => {
        wsRef.current = null;
        if (!isComponentMounted) return;
        setStatus('🔴 연결 끊김 (재연결 중...)');
        addLog('시스템', `서버 연결 단절 - 3초 후 재연결 (${e.code})`, '#ef4444');
        reconnectTimer = setTimeout(connect, 3000);
      };

      ws.onerror = () => {
        if (ws) ws.close();
      };
      ws.onmessage = (event) => {
        try {
          const textData = event.data;
          if (typeof textData === 'string' && textData.startsWith('DEVICE_LIST:')) {
            const devicesStr = textData.substring(12);
            const devices = devicesStr ? devicesStr.split(',') : [];
            setConnectedDevices(devices);
            setSelectedDevice(prev => (prev !== 'ALL' && !devices.includes(prev)) ? 'ALL' : prev);
            return;
          }
          addLog('수신 ↓', textData, '#8b5cf6');
          if (textData.trim().startsWith('{')) {
            const data = JSON.parse(textData);
            setVrState(data);
            setInputVal(prev => ({
              ...prev,
              leftNasal: data.leftNasal ?? prev.leftNasal, leftTemporal: data.leftTemporal ?? prev.leftTemporal,
              rightNasal: data.rightNasal ?? prev.rightNasal, rightTemporal: data.rightTemporal ?? prev.rightTemporal,
              q0: data.q0 ?? prev.q0, q1: data.q1 ?? prev.q1, q2: data.q2 ?? prev.q2, q3: data.q3 ?? prev.q3,
              leftTop: data.leftTop ?? prev.leftTop, leftBot: data.leftBot ?? prev.leftBot,
              rightTop: data.rightTop ?? prev.rightTop, rightBot: data.rightBot ?? prev.rightBot,
              leftFlipAdj: data.leftFlipAdj ?? prev.leftFlipAdj,
            }));
            if (data.cfgScale !== undefined) {
              setCfgInput(prev => ({
                ...prev,
                scale: data.cfgScale ?? prev.scale, distance: data.cfgDistance ?? prev.distance,
                flipInterval: data.cfgFlipInterval ?? prev.flipInterval,
                defaultBright: data.cfgDefaultBright ?? prev.defaultBright,
                targetBright: data.cfgTargetBright ?? prev.targetBright,
                bgBright: data.cfgBgBright ?? prev.bgBright,
                colorOrder: data.cfgColorOrder ?? prev.colorOrder,
              }));
            }
          }
        } catch (e) { addLog('에러 ⚠️', `파싱 실패: ${e.message}`, '#ef4444'); }
      };
    };

    connect();
    return () => {
      clearTimeout(reconnectTimer);
      if (ws) ws.onclose = null; // 재연결 방지 후 정리
      if (ws) ws.close();
    };
  }, []);

  useEffect(() => { logEndRef.current?.scrollIntoView({ behavior: 'smooth' }); }, [logs]);

  const sendCommand = (cmd) => {
    if (wsRef.current?.readyState === WebSocket.OPEN) {
      const finalCmd = `TARGET:ALL:${cmd}`;
      wsRef.current.send(finalCmd);
      addLog('명령 ↑', finalCmd, '#3b82f6');
    }
  };

  const sendVal = (target, val) => {
    if (val < 0 || val > 100) return alert("0~100 사이만 가능!");
    sendCommand(`SET_VAL:${target}:${val}`);
  };

  const sendCfg = (key, val) => {
    sendCommand(`CFG:${key}:${val}`);
    addLog('설정 ⚙️', `CFG:${key}:${val}`, '#f59e0b');
  };

  const getActiveMode = () => {
    if (vrState.divMode === 3) return '세로4분';
    if (vrState.divMode === 4) return '4분면';
    if (vrState.currentEyeTarget === 0) return '좌안';
    if (vrState.currentEyeTarget === 1) return '우안';
    return '양안';
  };
  const activeMode = getActiveMode();

  const getBrightnessDiff = () => {
    if (activeMode === '세로4분' || activeMode === '4분면') return null;
    const lAvg = (vrState.leftNasal + vrState.leftTemporal) / 2;
    const rAvg = (vrState.rightNasal + vrState.rightTemporal) / 2;
    const diff = Math.abs(lAvg - rAvg).toFixed(1);
    if (diff == 0) return { text: "밝기 동일", color: "#10b981", diff };
    if (lAvg > rAvg) return { text: `좌안이 ${diff}% 더 밝음`, color: "#e11d48", diff };
    return { text: `우안이 ${diff}% 더 밝음`, color: "#0284c7", diff };
  };

  useEffect(() => {
    const handleKeyDown = (e) => {
      if (activeTab !== 'control') return;
      if (e.key === 'ArrowUp') { e.preventDefault(); sendCommand('BRIGHT_UP'); }
      if (e.key === 'ArrowDown') { e.preventDefault(); sendCommand('BRIGHT_DOWN'); }
      if (e.key === 'ArrowLeft') sendCommand('EYE_LEFT');
      if (e.key === 'ArrowRight') sendCommand('EYE_RIGHT');
      if (e.key === 'Shift') sendCommand('EYE_BOTH');
      if (e.key === ' ') { e.preventDefault(); sendCommand('CHANGE_COLOR'); }
      if (e.key === 'Enter') sendCommand('CHANGE_TARGET');
    };
    window.addEventListener('keydown', handleKeyDown);
    return () => window.removeEventListener('keydown', handleKeyDown);
  }, [activeTab]);

  const calculateLux = (p) => (87 * Math.pow(p / 100, 2.2) * Math.PI).toFixed(1);

  const ValueOverlay = ({ percent, isSmall }) => (
    <div style={styles.valueOverlay}>
      <div style={{ fontSize: isSmall ? '40px' : '72px', fontWeight: '900' }}>{percent}%</div>
      <div style={{ fontSize: isSmall ? '18px' : '26px', color: '#fbbf24', fontWeight: 'bold' }}>{calculateLux(percent)} Lux</div>
    </div>
  );

  // ============================================================
  // 미러 스크린 렌더링 (기존 그대로)
  // ============================================================
  const renderMirrorScreen = () => {
    const masterPalette = [
      { r: 255, g: 0, b: 0 },   // 0: Red
      { r: 0, g: 255, b: 0 },   // 1: Green
      { r: 0, g: 0, b: 255 },   // 2: Blue
      { r: 255, g: 255, b: 255 } // 3: White
    ];
    
    // 색상 순서 파싱 (공백 제거 및 숫자 변환 필터링)
    const orderStr = vrState.cfgColorOrder || "0,1,2,3";
    const order = orderStr.split(',').map(n => parseInt(n.trim())).filter(n => !isNaN(n));
    
    // 현재 Unity colorIdx가 가리키는 실제 마스터 팔레트 인덱스 추출
    const masterIdx = order[vrState.colorIdx] ?? order[0] ?? 0;
    const b = masterPalette[masterIdx] || masterPalette[0];

    const getRGB = (p) => {
      const gamma = Math.pow(p / 100, 2.2);
      return `rgb(${Math.round(b.r * gamma)},${Math.round(b.g * gamma)},${Math.round(b.b * gamma)})`;
    };

    const leftFlipFactor = vrState.leftFlipAdj / 100;
    const leftOpacity = vrState.isFlipMode ? (vrState.isLeftEyeShown ? leftFlipFactor : 0.05) : ((vrState.currentEyeTarget === 0 || vrState.currentEyeTarget === 2) ? 1 : 0.2);
    const rightOpacity = vrState.isFlipMode ? (!vrState.isLeftEyeShown ? 1 : 0.05) : ((vrState.currentEyeTarget === 1 || vrState.currentEyeTarget === 2) ? 1 : 0.2);

    const CommonOverlay = () => (
      <>
        <div style={styles.cross}>+</div>
        <div style={styles.textOverlay} dangerouslySetInnerHTML={{ __html: vrState.uiText?.replace(/\n/g, '<br/>') || '' }}></div>
      </>
    );

    if (activeMode === '세로4분') {
      const strips = [
        { label: '1번', val: vrState.leftTop }, { label: '2번', val: vrState.leftBot },
        { label: '3번', val: vrState.rightTop }, { label: '4번', val: vrState.rightBot }
      ];
      return (
        <div style={styles.screenContainer}>
          <div style={{ display: 'flex', width: '100%', height: '100%' }}>
            {/* 좌측 안구 */}
            <div style={{ flex: 1, display: 'flex', borderRight: '2px solid #444', opacity: leftOpacity }}>
              {strips.map((s, i) => (
                <div key={i} style={{ flex: 1, position: 'relative', backgroundColor: getRGB(s.val), borderRight: i < 3 ? '1px solid #111' : 'none' }}>
                  <ValueOverlay percent={s.val} isSmall />
                </div>
              ))}
            </div>
            {/* 우측 안구 */}
            <div style={{ flex: 1, display: 'flex', borderLeft: '2px solid #444', opacity: rightOpacity }}>
              {strips.map((s, i) => (
                <div key={i} style={{ flex: 1, position: 'relative', backgroundColor: getRGB(s.val), borderRight: i < 3 ? '1px solid #111' : 'none' }}>
                  <ValueOverlay percent={s.val} isSmall />
                </div>
              ))}
            </div>
          </div>
          <CommonOverlay />
        </div>
      );
    }

    if (activeMode === '4분면') {
      const CrossGrid = ({ opacity }) => (
        <div style={{ flex: 1, position: 'relative', opacity, display: 'grid', gridTemplateColumns: '1fr 1fr', gridTemplateRows: '1fr 1fr' }}>
          <div style={{ backgroundColor: getRGB(vrState.q2), borderRight: '1px solid #111', borderBottom: '1px solid #111' }}><ValueOverlay percent={vrState.q2} isSmall /></div>
          <div style={{ backgroundColor: getRGB(vrState.q0), borderBottom: '1px solid #111' }}><ValueOverlay percent={vrState.q0} isSmall /></div>
          <div style={{ backgroundColor: getRGB(vrState.q3), borderRight: '1px solid #111' }}><ValueOverlay percent={vrState.q3} isSmall /></div>
          <div style={{ backgroundColor: getRGB(vrState.q1) }}><ValueOverlay percent={vrState.q1} isSmall /></div>
        </div>
      );
      return (
        <div style={styles.screenContainer}>
          <div style={{ display: 'flex', width: '100%', height: '100%' }}>
            <div style={{ flex: 1, display: 'flex', borderRight: '2px solid #444' }}><CrossGrid opacity={leftOpacity} /></div>
            <div style={{ flex: 1, display: 'flex', borderLeft: '2px solid #444' }}><CrossGrid opacity={rightOpacity} /></div>
          </div>
          <CommonOverlay />
        </div>
      );
    }

    return (
      <div style={styles.screenContainer}>
        <div style={{ display: 'flex', width: '100%', height: '100%' }}>
          <div style={{ flex: 1, position: 'relative', borderRight: '2px solid #444', opacity: leftOpacity }}>
            {vrState.currentEyeTarget === 2 ? (
              <div style={{ width: '100%', height: '100%', backgroundColor: getRGB(vrState.leftNasal), display: 'flex', justifyContent: 'center', alignItems: 'center' }}>
                <ValueOverlay percent={vrState.leftNasal} isSmall />
                <div style={{ position: 'absolute', top: 10, left: 10, color: 'white', fontWeight: 'bold', fontSize: '12px' }}>좌안</div>
              </div>
            ) : (
              <div style={{ display: 'flex', width: '100%', height: '100%' }}>
                <div style={{ flex: 1, backgroundColor: getRGB(vrState.leftTemporal), position: 'relative' }}><ValueOverlay percent={vrState.leftTemporal} isSmall /></div>
                <div style={{ flex: 1, backgroundColor: getRGB(vrState.leftNasal), position: 'relative', borderLeft: '1px solid black' }}><ValueOverlay percent={vrState.leftNasal} isSmall /></div>
              </div>
            )}
          </div>
          <div style={{ flex: 1, position: 'relative', borderLeft: '2px solid #444', opacity: rightOpacity }}>
            {vrState.currentEyeTarget === 2 ? (
              <div style={{ width: '100%', height: '100%', backgroundColor: getRGB(vrState.rightNasal), display: 'flex', justifyContent: 'center', alignItems: 'center' }}>
                <ValueOverlay percent={vrState.rightNasal} isSmall />
                <div style={{ position: 'absolute', top: 10, left: 10, color: 'white', fontWeight: 'bold', fontSize: '12px' }}>우안</div>
              </div>
            ) : (
              <div style={{ display: 'flex', width: '100%', height: '100%' }}>
                <div style={{ flex: 1, backgroundColor: getRGB(vrState.rightNasal), position: 'relative' }}><ValueOverlay percent={vrState.rightNasal} isSmall /></div>
                <div style={{ flex: 1, backgroundColor: getRGB(vrState.rightTemporal), position: 'relative', borderLeft: '1px solid black' }}><ValueOverlay percent={vrState.rightTemporal} isSmall /></div>
              </div>
            )}
          </div>
        </div>
        <CommonOverlay />
      </div>
    );
  };

  const modeButtons = [
    { key: '좌안', label: '좌안 단독', cmd: () => sendCommand('EYE_LEFT'), bg: '#ffe4e6', activeBg: '#e11d48', tc: '#e11d48' },
    { key: '우안', label: '우안 단독', cmd: () => sendCommand('EYE_RIGHT'), bg: '#ffe4e6', activeBg: '#e11d48', tc: '#e11d48' },
    { key: '양안', label: '양안 모드', cmd: () => sendCommand('MODE_2'), bg: '#e0f2fe', activeBg: '#0284c7', tc: '#0284c7' },
    { key: '세로4분', label: '세로 4분면', cmd: () => sendCommand('MODE_4V'), bg: '#f0fdf4', activeBg: '#10b981', tc: '#10b981' },
    { key: '4분면', label: '십자 4분면', cmd: () => sendCommand('MODE_4'), bg: '#fef3c7', activeBg: '#f59e0b', tc: '#f59e0b' },
  ];

  // ============================================================
  // 설정 탭 렌더링
  // ============================================================
  const renderSettingsTab = () => {
    const CfgRow = ({ label, desc, stateKey, cfgKey, unit, step, min, max, isText }) => (
      <div style={{ padding: '12px', backgroundColor: '#f8fafc', borderRadius: '10px', border: '1px solid #e2e8f0', marginBottom: '10px' }}>
        <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: '6px' }}>
          <span style={{ fontWeight: 'bold', fontSize: '14px', color: '#1e293b' }}>{label}</span>
          <span style={{ fontSize: '11px', color: '#94a3b8' }}>{desc}</span>
        </div>
        <div style={{ display: 'flex', gap: '8px', alignItems: 'center' }}>
          <input
            type={isText ? "text" : "number"}
            step={step || 1} min={min} max={max}
            value={cfgInput[cfgKey]}
            onChange={e => setCfgInput({ ...cfgInput, [cfgKey]: isText ? e.target.value : parseFloat(e.target.value) || 0 })}
            style={{ flex: 1, padding: '10px', borderRadius: '8px', border: '1px solid #cbd5e1', fontSize: '16px', fontWeight: 'bold', textAlign: 'center' }}
          />
          {unit && <span style={{ fontSize: '13px', color: '#64748b', fontWeight: 'bold', minWidth: '30px' }}>{unit}</span>}
          <button
            onClick={() => sendCfg(cfgKey, cfgInput[cfgKey])}
            style={{ padding: '10px 20px', backgroundColor: '#6366f1', color: 'white', border: 'none', borderRadius: '8px', fontWeight: 'bold', fontSize: '14px', cursor: 'pointer' }}
          >적용</button>
        </div>
      </div>
    );

    return (
      <div style={{ display: 'flex', flexDirection: 'column', gap: '8px' }}>
        <div style={styles.card}>
          <h3 style={styles.cardTitle}>화면 설정</h3>
          <CfgRow label="Quad 크기" desc="화면 패널 크기" cfgKey="scale" unit="" step={0.1} min={0.5} max={5} />
          <CfgRow label="Quad 거리" desc="카메라~패널 거리" cfgKey="distance" unit="m" step={0.5} min={1} max={20} />
        </div>

        <div style={styles.card}>
          <h3 style={styles.cardTitle}>밝기 설정</h3>
          <CfgRow label="기본 밝기" desc="모드 전환 시 초기값" cfgKey="defaultBright" unit="%" step={5} min={0} max={100} />
          <CfgRow label="타겟 밝기" desc="조절 대상 영역" cfgKey="targetBright" unit="%" step={5} min={0} max={100} />
          <CfgRow label="배경 밝기" desc="비조절 영역" cfgKey="bgBright" unit="%" step={5} min={0} max={100} />
        </div>

        <div style={styles.card}>
          <h3 style={styles.cardTitle}>플립 설정</h3>
          <CfgRow label="플립 간격" desc="좌↔우 전환 속도" cfgKey="flipInterval" unit="초" step={0.1} min={0.1} max={10} />
        </div>

        <div style={styles.card}>
          <h3 style={styles.cardTitle}>색상 순서</h3>
          <div style={{ fontSize: '12px', color: '#64748b', marginBottom: '8px' }}>0=빨강, 1=초록, 2=파랑, 3=흰색 (쉼표 구분)</div>
          <CfgRow label="색상 순서" desc="A버튼 순환 순서" stateKey="colorOrder" cfgKey="COLOR_ORDER" isText />
          <div style={{ display: 'flex', gap: '6px', marginTop: '8px' }}>
            {[
              { label: '빨→초→파→흰', val: '0,1,2,3' },
              { label: '빨→파→초→흰', val: '0,2,1,3' },
              { label: '흰색만', val: '3' },
              { label: '빨강만', val: '0' },
            ].map(preset => (
              <button key={preset.val} onClick={() => { 
                setCfgInput({ ...cfgInput, colorOrder: preset.val }); 
                sendCfg('COLOR_ORDER', preset.val); 
              }}
                style={{ flex: 1, padding: '8px 4px', backgroundColor: '#e2e8f0', border: 'none', borderRadius: '6px', fontSize: '11px', fontWeight: 'bold', cursor: 'pointer' }}>
                {preset.label}
              </button>
            ))}
          </div>
        </div>
      </div>
    );
  };

  // ============================================================
  // 리모컨 탭 렌더링 (기존 그대로)
  // ============================================================
  const renderControlTab = () => (
    <div style={{ display: 'flex', flexDirection: 'column', gap: '12px' }}>
      <div style={styles.card}>
        <h3 style={{ ...styles.cardTitle, marginBottom: '10px' }}>조작 기기</h3>
        <select value={selectedDevice} onChange={(e) => setSelectedDevice(e.target.value)} style={styles.deviceSelect}>
          <option value="ALL">전체 기기</option>
          {connectedDevices.map(ip => <option key={ip} value={ip}>{ip}</option>)}
        </select>
        {connectedDevices.length === 0 && <div style={{ fontSize: '12px', color: '#94a3b8', marginTop: '5px' }}>VR 미연결</div>}
      </div>

      <div style={styles.card}>
        <h3 style={styles.cardTitle}>검사 모드</h3>
        <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: '6px', marginBottom: '12px' }}>
          {modeButtons.map(m => (
            <button key={m.key} style={{ ...styles.btn, backgroundColor: activeMode === m.key ? m.activeBg : m.bg, color: activeMode === m.key ? 'white' : m.tc }} onClick={m.cmd}>{m.label}</button>
          ))}
        </div>

        <button style={{ ...styles.btn, backgroundColor: '#e0f2fe', color: '#0284c7', width: '100%', marginBottom: '6px' }} onClick={() => sendCommand('CHANGE_COLOR')}>색상 변경</button>
        <button style={{ ...styles.btn, backgroundColor: '#e0f2fe', color: '#0284c7', width: '100%', marginBottom: '12px' }} onClick={() => sendCommand('CHANGE_TARGET')}>타겟 영역 변경</button>

        <div style={{ display: 'flex', gap: '6px' }}>
          <button style={{ ...styles.btn, flex: 1, backgroundColor: '#3b82f6', color: 'white', fontSize: '16px' }} onClick={() => sendCommand('BRIGHT_UP')}>▲ +5%</button>
          <button style={{ ...styles.btn, flex: 1, backgroundColor: '#3b82f6', color: 'white', fontSize: '16px' }} onClick={() => sendCommand('BRIGHT_DOWN')}>▼ -5%</button>
        </div>
      </div>

      <div style={styles.card}>
        <h3 style={styles.cardTitle}>플립 모드</h3>
        <div style={{ display: 'flex', gap: '8px', marginBottom: '8px' }}>
          <button style={{ ...styles.btn, flex: 2, backgroundColor: vrState.isFlipMode ? '#ef4444' : '#10b981', color: 'white' }} onClick={() => sendCommand('TOGGLE_FLIP')}>
            {vrState.isFlipMode ? '⏹ 중지' : '▶ 시작'}
          </button>
          <input type="number" step="0.1" min="0.1" value={inputVal.flipInterval} onChange={e => setInputVal({ ...inputVal, flipInterval: e.target.value })} style={{ ...styles.smallInput, width: '50px' }} />
          <small style={{ fontSize: '10px', alignSelf: 'center' }}>초</small>
          <button style={{ ...styles.setBtn, padding: '0 8px' }} onClick={() => sendCommand(`SET_FLIP_INTERVAL:${inputVal.flipInterval}`)}>Set</button>
        </div>

        {vrState.isFlipMode && (
          <div style={{ marginTop: '12px', padding: '10px', backgroundColor: '#fff', borderRadius: '8px', border: '1px solid #ddd' }}>
            <div style={{ display: 'flex', justifyContent: 'space-between', marginBottom: '5px' }}>
              <span style={{ fontSize: '12px', fontWeight: 'bold', color: '#e11d48' }}>좌안 보정 % (Flip 전용)</span>
              <span style={{ fontSize: '12px', fontWeight: 'bold' }}>{inputVal.leftFlipAdj}%</span>
            </div>
            <div style={{ display: 'flex', gap: '8px', alignItems: 'center' }}>
              <input type="range" min="0" max="100" value={inputVal.leftFlipAdj} 
                onChange={e => {
                  const val = parseInt(e.target.value);
                  setInputVal({ ...inputVal, leftFlipAdj: val });
                  sendCommand(`SET_LEFT_FLIP_ADJ:${val}`);
                }} 
                style={{ flex: 1 }} 
              />
              <button style={{ ...styles.setBtn, padding: '4px 8px', width: 'auto' }} onClick={() => sendCommand(`SET_LEFT_FLIP_ADJ:${inputVal.leftFlipAdj}`)}>저장</button>
            </div>
            <small style={{ fontSize: '10px', color: '#64748b' }}>* FLIP 모드에서 좌안 전용 강도 조절</small>
          </div>
        )}
      </div>

      {/* 독립 수치 제어 */}
      <div style={styles.card}>
        <div style={{ ...styles.cardTitle, display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
          <span>독립 수치 제어</span>
          <div style={{ display: 'flex', alignItems: 'center', gap: '8px' }}>
            {(() => {
              const bDiff = getBrightnessDiff();
              if(bDiff) return (
                <span style={{ fontSize: '11px', padding: '4px 8px', borderRadius: '4px', backgroundColor: bDiff.color + '20', color: bDiff.color, fontWeight: 'bold' }}>
                  ⚖️ {bDiff.text}
                </span>
              );
              return null;
            })()}
            <span style={{ fontSize: '12px', color: '#6366f1', fontWeight: 'bold', borderLeft: 'none', paddingLeft: 0 }}>{activeMode}</span>
          </div>
        </div>

        {activeMode === '세로4분' && (
          <div style={styles.eyePanel}>
            {[{ label: '1번 띠', valKey: 'leftTop', target: 'L_TOP' }, { label: '2번 띠', valKey: 'leftBot', target: 'L_BOT' },
            { label: '3번 띠', valKey: 'rightTop', target: 'R_TOP' }, { label: '4번 띠', valKey: 'rightBot', target: 'R_BOT' }
            ].map(row => (
              <div key={row.label} style={styles.inputRow}>
                <span style={{ width: '40px', fontSize: '13px', fontWeight: 'bold' }}>{row.label}</span>
                <input type="number" value={inputVal[row.valKey]} onChange={e => setInputVal({ ...inputVal, [row.valKey]: e.target.value })} style={styles.smallInput} />
                <button style={{ ...styles.setBtn, backgroundColor: '#10b981' }} onClick={() => sendVal(row.target, inputVal[row.valKey])}>Set</button>
              </div>
            ))}
          </div>
        )}

        {activeMode === '4분면' && (
          <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: '8px' }}>
            {[{ label: '좌상(Q2)', key: 'q2', target: 'Q2' }, { label: '우상(Q0)', key: 'q0', target: 'Q0' },
            { label: '좌하(Q3)', key: 'q3', target: 'Q3' }, { label: '우하(Q1)', key: 'q1', target: 'Q1' }].map(q => (
              <div key={q.key} style={styles.qInputBox}>
                <small>{q.label}</small>
                <input type="number" value={inputVal[q.key]} onChange={e => setInputVal({ ...inputVal, [q.key]: e.target.value })} style={styles.smallInput} />
                <button style={styles.qBtn} onClick={() => sendVal(q.target, inputVal[q.key])}>Set</button>
              </div>
            ))}
          </div>
        )}

        {(activeMode === '좌안' || activeMode === '우안' || activeMode === '양안') && (
          <div style={{ display: 'flex', flexDirection: 'column', gap: '10px' }}>
            {vrState.currentEyeTarget === 2 ? (
              [{ title: '[ 좌안 ]', tc: '#e11d48', vk: 'leftNasal', tg: 'L_ALL' }, { title: '[ 우안 ]', tc: '#0284c7', vk: 'rightNasal', tg: 'R_ALL' }].map(p => (
                <div key={p.title} style={styles.eyePanel}>
                  <div style={{ ...styles.eyeTitle, color: p.tc }}>{p.title}</div>
                  <div style={styles.inputRow}>
                    <span style={{ width: '35px', fontSize: '12px' }}>밝기</span>
                    <input type="number" value={inputVal[p.vk]} onChange={e => setInputVal({ ...inputVal, [p.vk]: e.target.value })} style={styles.smallInput} />
                    <button style={styles.setBtn} onClick={() => sendVal(p.tg, inputVal[p.vk])}>Set</button>
                  </div>
                </div>
              ))
            ) : (
              <>
                <div style={styles.eyePanel}>
                  <div style={styles.eyeTitle}>[ 좌안 (분할) ]</div>
                  {[{ l: '코쪽', vk: 'leftNasal', tg: 'L_NASAL' }, { l: '귀쪽', vk: 'leftTemporal', tg: 'L_TEMP' }].map(r => (
                    <div key={r.l} style={styles.inputRow}>
                      <span style={{ width: '35px', fontSize: '12px' }}>{r.l}</span>
                      <input type="number" value={inputVal[r.vk]} onChange={e => setInputVal({ ...inputVal, [r.vk]: e.target.value })} style={styles.smallInput} />
                      <button style={styles.setBtn} onClick={() => sendVal(r.tg, inputVal[r.vk])}>Set</button>
                    </div>
                  ))}
                </div>
                <div style={styles.eyePanel}>
                  <div style={{ ...styles.eyeTitle, color: '#0284c7' }}>[ 우안 (분할) ]</div>
                  {[{ l: '코쪽', vk: 'rightNasal', tg: 'R_NASAL' }, { l: '귀쪽', vk: 'rightTemporal', tg: 'R_TEMP' }].map(r => (
                    <div key={r.l} style={styles.inputRow}>
                      <span style={{ width: '35px', fontSize: '12px' }}>{r.l}</span>
                      <input type="number" value={inputVal[r.vk]} onChange={e => setInputVal({ ...inputVal, [r.vk]: e.target.value })} style={styles.smallInput} />
                      <button style={styles.setBtn} onClick={() => sendVal(r.tg, inputVal[r.vk])}>Set</button>
                    </div>
                  ))}
                </div>
              </>
            )}
          </div>
        )}
      </div>
    </div>
  );

  // ============================================================
  // 메인 렌더
  // ============================================================
  return (
    <div style={styles.wrapper}>
      <header style={styles.header}>
        <h1 style={styles.title}>VR 검사 실시간 관제 시스템</h1>
        <div style={{ display: 'flex', gap: '10px' }}>
          <div style={styles.statusBadge(status)}>{status}</div>
          <div style={styles.statusBadge(connectedDevices.length > 0 ? '🟢' : '🔴')}>
            {connectedDevices.length > 0 ? `🟢 VR: ${connectedDevices[0].substring(0, 8)}...` : '🔴 VR 미연결'}
          </div>
        </div>
      </header>

      <div style={styles.mainLayout}>
        <div style={styles.leftColumn}>
          {/* 탭 전환 */}
          <div style={{ display: 'flex', gap: '4px', marginBottom: '-8px' }}>
            <button onClick={() => setActiveTab('control')}
              style={{
                flex: 1, padding: '12px', border: 'none', borderRadius: '12px 12px 0 0', fontWeight: 'bold', fontSize: '14px', cursor: 'pointer',
                backgroundColor: activeTab === 'control' ? 'white' : '#e2e8f0', color: activeTab === 'control' ? '#3b82f6' : '#64748b'
              }}>
              🎮 리모컨
            </button>
            <button onClick={() => setActiveTab('settings')}
              style={{
                flex: 1, padding: '12px', border: 'none', borderRadius: '12px 12px 0 0', fontWeight: 'bold', fontSize: '14px', cursor: 'pointer',
                backgroundColor: activeTab === 'settings' ? 'white' : '#e2e8f0', color: activeTab === 'settings' ? '#f59e0b' : '#64748b'
              }}>
              ⚙️ 설정
            </button>
          </div>

          {/* 탭 내용 */}
          <div style={{ flex: 1, overflowY: 'auto' }}>
            {activeTab === 'control' ? renderControlTab() : renderSettingsTab()}
          </div>

          {/* 로그 (항상 하단에) */}
          <div style={{ ...styles.card, flex: '0 0 auto', maxHeight: '180px', display: 'flex', flexDirection: 'column' }}>
            <h3 style={styles.cardTitle}>실시간 로그</h3>
            <div style={styles.logBox}>
              {logs.map((l, i) => (
                <div key={i} style={{ fontSize: '11px', marginBottom: '4px', borderBottom: '1px solid #f1f5f9' }}>
                  <span style={{ color: l.color, fontWeight: 'bold' }}>{l.type}:</span> {l.msg}
                </div>
              ))}
              <div ref={logEndRef} />
            </div>
          </div>
        </div>

        <div style={styles.rightColumn}>{renderMirrorScreen()}</div>
      </div>
    </div>
  );
};

const styles = {
  wrapper: { margin: 0, backgroundColor: '#f1f5f9', height: '100vh', padding: '20px', boxSizing: 'border-box', display: 'flex', flexDirection: 'column', fontFamily: '"Pretendard", sans-serif' },
  header: { display: 'flex', justifyContent: 'space-between', marginBottom: '15px', alignItems: 'center' },
  title: { fontSize: '24px', fontWeight: '900', color: '#1e293b', margin: 0 },
  statusBadge: (s) => ({ padding: '8px 18px', borderRadius: '25px', fontWeight: 'bold', fontSize: '14px', backgroundColor: s.includes('연결') ? '#dcfce7' : '#fee2e2', color: s.includes('연결') ? '#166534' : '#991b1b' }),
  mainLayout: { display: 'flex', gap: '25px', flex: 1, minHeight: 0 },
  leftColumn: { width: '340px', display: 'flex', flexDirection: 'column', gap: '12px', height: '100%' },
  rightColumn: { flex: 1, backgroundColor: 'black', borderRadius: '20px', position: 'relative', overflow: 'hidden', border: '6px solid #1e293b', boxShadow: '0 10px 25px rgba(0,0,0,0.2)' },
  card: { backgroundColor: 'white', borderRadius: '18px', padding: '16px', boxShadow: '0 4px 6px rgba(0,0,0,0.05)' },
  cardTitle: { fontSize: '16px', fontWeight: 'bold', marginBottom: '12px', color: '#334155', borderLeft: '5px solid #3b82f6', paddingLeft: '10px' },
  deviceSelect: { width: '100%', padding: '10px', borderRadius: '8px', border: '1px solid #cbd5e1', fontSize: '14px', fontWeight: 'bold' },
  btn: { flex: 1, padding: '10px 8px', border: 'none', borderRadius: '10px', cursor: 'pointer', fontWeight: 'bold', fontSize: '13px', transition: '0.2s' },
  eyePanel: { padding: '8px', backgroundColor: '#f8fafc', border: '1px solid #e2e8f0', borderRadius: '8px', display: 'flex', flexDirection: 'column', gap: '5px' },
  eyeTitle: { fontSize: '13px', fontWeight: '900', color: '#e11d48', textAlign: 'center', marginBottom: '2px' },
  inputRow: { display: 'flex', gap: '5px', alignItems: 'center' },
  smallInput: { width: '45px', padding: '6px', borderRadius: '6px', border: '1px solid #ddd', textAlign: 'center' },
  setBtn: { flex: 1, backgroundColor: '#6366f1', color: 'white', border: 'none', borderRadius: '6px', cursor: 'pointer', fontWeight: 'bold', padding: '6px 4px' },
  arrowBtn: { padding: '6px 8px', backgroundColor: '#e2e8f0', border: 'none', borderRadius: '6px', cursor: 'pointer', fontWeight: 'bold', fontSize: '12px' },
  qInputBox: { display: 'flex', flexDirection: 'column', gap: '4px', backgroundColor: '#fff', padding: '5px', borderRadius: '8px', border: '1px solid #eee', alignItems: 'center' },
  qBtn: { width: '100%', backgroundColor: '#6366f1', color: 'white', border: 'none', borderRadius: '4px', fontSize: '12px', cursor: 'pointer', padding: '4px' },
  logBox: { flex: 1, overflowY: 'auto', backgroundColor: '#f8fafc', padding: '8px', borderRadius: '8px', border: '1px solid #e2e8f0', fontFamily: 'monospace', minHeight: '60px' },
  screenContainer: { width: '100%', height: '100%', position: 'relative' },
  quad: { position: 'absolute', display: 'flex', justifyContent: 'center', alignItems: 'center', transition: 'background-color 0.15s' },
  valueOverlay: { color: 'white', textAlign: 'center', textShadow: '3px 3px 8px black', zIndex: 10 },
  cross: { position: 'absolute', top: '50%', left: '50%', transform: 'translate(-50%,-50%)', color: 'white', fontSize: '120px', opacity: 0.2, zIndex: 5, pointerEvents: 'none' },
  textOverlay: { position: 'absolute', bottom: '30px', width: '100%', textAlign: 'center', color: 'white', fontWeight: 'bold', fontSize: '24px', textShadow: '2px 2px 5px black', zIndex: 10 }
};

export default App;