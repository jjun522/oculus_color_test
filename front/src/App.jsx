import React, { useState, useEffect, useRef } from 'react';

const App = () => {
  const [status, setStatus] = useState('서버 연결 중...');
  const [vrState, setVrState] = useState({
    divMode: 2, colorIdx: 0,
    leftNasal: 50, leftTemporal: 50, rightNasal: 50, rightTemporal: 50,
    q0: 50, q1: 50, q2: 50, q3: 50,
    uiText: "VR 기기의 신호를 기다리고 있습니다..."
  });

  const [logs, setLogs] = useState([]);
  const [inputVal, setInputVal] = useState({
    leftNasal: 50, leftTemporal: 50, rightNasal: 50, rightTemporal: 50,
    q0: 80, q1: 80, q2: 30, q3: 80
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
    // 💡 모든 통신은 12346 포트 하나로 통합되었습니다. (Nginx 리버스 프록시 적용)
    // 주소창에 특정 경로(/vr 등)가 붙어 있을 경우 이를 자동으로 감지해서 붙여줍니다.
    const wsPath = window.location.pathname.replace(/\/$/, '');
    const ws = new WebSocket(`ws://${window.location.host}${wsPath}/ws`);
    wsRef.current = ws;
    ws.onopen = () => { setStatus('🟢 정상 연결됨'); addLog('시스템', '서버 연결 성공', '#10b981'); };
    ws.onclose = () => { setStatus('🔴 연결 끊김'); addLog('시스템', '서버 연결 단절', '#ef4444'); };
    ws.onmessage = (event) => {
      try {
        const textData = event.data;
        if (typeof textData === 'string' && textData.startsWith('DEVICE_LIST:')) {
          const devicesStr = textData.substring(12);
          const devices = devicesStr ? devicesStr.split(',') : [];
          setConnectedDevices(devices);

          // 만약 선택된 기기가 ALL이 아니고, 새 목록에 없다면 ALL로 초기화
          setSelectedDevice(prev => (prev !== 'ALL' && !devices.includes(prev)) ? 'ALL' : prev);
          return;
        }

        // 💡 수신된 JSON 데이터 로그 출력
        addLog('수신 ↓', textData, '#8b5cf6');

        const data = JSON.parse(textData);
        setVrState(data); // 여기서 divMode를 받아 화면을 결정함
      } catch (e) {
        addLog('에러 ⚠️', `JSON 파싱 실패: ${e.message}`, '#ef4444');
      }
    };
    return () => ws.close();
  }, []);

  useEffect(() => { logEndRef.current?.scrollIntoView({ behavior: 'smooth' }); }, [logs]);

  const sendCommand = (cmd) => {
    if (wsRef.current?.readyState === WebSocket.OPEN) {
      // 💡 프록시 환경에서 IP가 뭉개지므로, 특정 기기 타겟팅을 풀고 무조건 브로드캐스트 전송
      const finalCmd = `TARGET:ALL:${cmd}`;
      wsRef.current.send(finalCmd);
      addLog('명령 ↑', finalCmd, '#3b82f6');
    }
  };

  const sendVal = (target, val) => {
    if (val < 0 || val > 100) return alert("0~100 사이만 가능!");
    sendCommand(`SET_VAL:${target}:${val}`);

    // 💡 즉시 UI 반영 (Optimistic Update)
    // 낙관적 업데이트를 위해 서버 데이터 구조와 동일하게 매핑
    let stateKey = target.toLowerCase();
    if (target === 'L_NASAL') stateKey = 'leftNasal';
    if (target === 'L_TEMP') stateKey = 'leftTemporal';
    if (target === 'R_NASAL') stateKey = 'rightNasal';
    if (target === 'R_TEMP') stateKey = 'rightTemporal';

    setVrState(prev => ({
      ...prev,
      [stateKey]: parseInt(val)
    }));
  };

  // 키보드 단축키
  useEffect(() => {
    const handleKeyDown = (e) => {
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
  }, []);

  const calculateLux = (p) => (87 * Math.pow(p / 100, 2.2) * Math.PI).toFixed(1);

  // HUD 수치 표시 컴포넌트
  const ValueOverlay = ({ percent, isSmall }) => (
    <div style={styles.valueOverlay}>
      <div style={{ fontSize: isSmall ? '40px' : '72px', fontWeight: '900' }}>{percent}%</div>
      <div style={{ fontSize: isSmall ? '18px' : '26px', color: '#fbbf24', fontWeight: 'bold' }}>{calculateLux(percent)} Lux</div>
    </div>
  );

  const renderMirrorScreen = () => {
    const colors = [{ r: 255, g: 0, b: 0 }, { r: 0, g: 255, b: 0 }, { r: 0, g: 0, b: 255 }, { r: 255, g: 255, b: 255 }];
    const b = colors[vrState.colorIdx];
    const getRGB = (p) => `rgb(${(b.r * p) / 100},${(b.g * p) / 100},${(b.b * p) / 100})`;

    return (
      <div style={styles.screenContainer}>
        {/* 💡 divMode가 2일 때: 양안 독립 2분면 레이아웃 */}
        {vrState.divMode === 2 ? (
          <div style={{ display: 'flex', width: '100%', height: '100%' }}>
            {/* 왼쪽 눈 화면 */}
            <div style={{ flex: 1, position: 'relative', borderRight: '8px solid #333' }}>
              <div style={{ ...styles.quad, left: 0, width: '50%', height: '100%', backgroundColor: getRGB(vrState.leftTemporal) }}>
                <ValueOverlay percent={vrState.leftTemporal} isSmall />
                <div style={{ position: 'absolute', top: 10, left: 10, color: 'white', fontWeight: 'bold', textShadow: '1px 1px 2px black' }}>좌안 귀쪽</div>
              </div>
              <div style={{ ...styles.quad, right: 0, width: '50%', height: '100%', backgroundColor: getRGB(vrState.leftNasal), borderLeft: '2px solid black' }}>
                <ValueOverlay percent={vrState.leftNasal} isSmall />
                <div style={{ position: 'absolute', top: 10, right: 10, color: 'white', fontWeight: 'bold', textShadow: '1px 1px 2px black' }}>좌안 코쪽</div>
              </div>
            </div>

            {/* 가상 격벽 시뮬레이션 선 */}
            <div style={{ width: '16px', backgroundColor: '#111', zIndex: 20 }}></div>

            {/* 오른쪽 눈 화면 */}
            <div style={{ flex: 1, position: 'relative', borderLeft: '8px solid #333' }}>
              <div style={{ ...styles.quad, left: 0, width: '50%', height: '100%', backgroundColor: getRGB(vrState.rightNasal), borderRight: '2px solid black' }}>
                <ValueOverlay percent={vrState.rightNasal} isSmall />
                <div style={{ position: 'absolute', top: 10, left: 10, color: 'white', fontWeight: 'bold', textShadow: '1px 1px 2px black' }}>우안 코쪽</div>
              </div>
              <div style={{ ...styles.quad, right: 0, width: '50%', height: '100%', backgroundColor: getRGB(vrState.rightTemporal) }}>
                <ValueOverlay percent={vrState.rightTemporal} isSmall />
                <div style={{ position: 'absolute', top: 10, right: 10, color: 'white', fontWeight: 'bold', textShadow: '1px 1px 2px black' }}>우안 귀쪽</div>
              </div>
            </div>
          </div>
        ) : (
          /* 💡 divMode가 4일 때: 4분면 레이아웃 */
          <>
            <div style={{ ...styles.quad, left: 0, top: 0, width: '50%', height: '50%', backgroundColor: getRGB(vrState.q2), borderRight: '2px solid black', borderBottom: '2px solid black' }}>
              <ValueOverlay percent={vrState.q2} isSmall />
            </div>
            <div style={{ ...styles.quad, right: 0, top: 0, width: '50%', height: '50%', backgroundColor: getRGB(vrState.q0), borderBottom: '2px solid black' }}>
              <ValueOverlay percent={vrState.q0} isSmall />
            </div>
            <div style={{ ...styles.quad, left: 0, bottom: 0, width: '50%', height: '50%', backgroundColor: getRGB(vrState.q3), borderRight: '2px solid black' }}>
              <ValueOverlay percent={vrState.q3} isSmall />
            </div>
            <div style={{ ...styles.quad, right: 0, bottom: 0, width: '50%', height: '50%', backgroundColor: getRGB(vrState.q1) }}>
              <ValueOverlay percent={vrState.q1} isSmall />
            </div>
          </>
        )}
        <div style={styles.cross}>+</div>
        <div style={styles.textOverlay} dangerouslySetInnerHTML={{ __html: vrState.uiText.replace(/\n/g, '<br/>') }}></div>
      </div>
    );
  };

  return (
    <div style={styles.wrapper}>
      <header style={styles.header}>
        <h1 style={styles.title}>VR 검사 실시간 관제 시스템</h1>
        <div style={styles.statusBadge(status)}>{status}</div>
      </header>
      <div style={styles.mainLayout}>
        <div style={styles.leftColumn}>
          <div style={styles.card}>
            <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: '15px' }}>
              <h3 style={{ ...styles.cardTitle, marginBottom: 0 }}>조작 기기 선택</h3>
            </div>
            <select
              value={selectedDevice}
              onChange={(e) => setSelectedDevice(e.target.value)}
              style={styles.deviceSelect}
            >
              <option value="ALL">전체 기기 (동시 조작)</option>
              {connectedDevices.map(ip => (
                <option key={ip} value={ip}>{ip}</option>
              ))}
            </select>
            {connectedDevices.length === 0 && <div style={{ fontSize: '12px', color: '#94a3b8', marginTop: '5px' }}>접속된 VR 기기가 없습니다.</div>}
          </div>

          <div style={styles.card}>
            <h3 style={styles.cardTitle}>리모컨 (단축키 연동)</h3>
            <div style={styles.btnRow}>
              <button style={{ ...styles.btn, backgroundColor: '#ffe4e6', color: '#e11d48' }} onClick={() => sendCommand('EYE_LEFT')}>좌안</button>
              <button style={{ ...styles.btn, backgroundColor: '#ffe4e6', color: '#e11d48' }} onClick={() => sendCommand('EYE_RIGHT')}>우안</button>
              <button style={{ ...styles.btn, backgroundColor: '#ffe4e6', color: '#e11d48' }} onClick={() => sendCommand('EYE_BOTH')}>양안</button>
            </div>
            <button style={{ ...styles.btn, backgroundColor: '#e0f2fe', color: '#0284c7', width: '100%', marginTop: '10px' }} onClick={() => sendCommand('CHANGE_COLOR')}>색상 변경 (Space)</button>
            <button style={{ ...styles.btn, backgroundColor: '#e0f2fe', color: '#0284c7', width: '100%', marginTop: '10px' }} onClick={() => sendCommand('CHANGE_TARGET')}>타겟 영역 변경 (Enter)</button>
            <div style={{ marginTop: '15px' }}>
              <button style={{ ...styles.btn, backgroundColor: '#3b82f6', color: 'white', width: '100%', marginBottom: '6px', fontSize: '18px' }} onClick={() => sendCommand('BRIGHT_UP')}>▲ 일괄 밝기 +2% (↑)</button>
              <button style={{ ...styles.btn, backgroundColor: '#3b82f6', color: 'white', width: '100%', fontSize: '18px' }} onClick={() => sendCommand('BRIGHT_DOWN')}>▼ 일괄 밝기 -2% (↓)</button>
            </div>
            {/* 💡 [핵심] 수치 직접 입력 섹션 */}
            <div style={styles.controlGroup}>
              <div style={{ ...styles.groupHeader, display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
                <span>🎯 독립 수치 제어</span>
                <span style={{ fontSize: '12px', color: '#6366f1', fontWeight: 'bold' }}>{vrState.divMode === 2 ? '2분면 모드' : '4분면 모드'}</span>
              </div>

              {vrState.divMode === 2 ? (
                /* 2분면용 독립 양안 입력창 */
                <div style={{ display: 'flex', flexDirection: 'column', gap: '12px' }}>
                  {/* 좌안 패널 */}
                  <div style={styles.eyePanel}>
                    <div style={styles.eyeTitle}>[ 좌 안 ]</div>
                    <div style={styles.inputRow}>
                      <span style={{ width: '35px', fontSize: '12px' }}>코쪽</span>
                      <input type="number" value={inputVal.leftNasal} onChange={e => setInputVal({ ...inputVal, leftNasal: e.target.value })} style={styles.smallInput} />
                      <button style={styles.setBtn} onClick={() => sendVal('L_NASAL', inputVal.leftNasal)}>Set</button>
                    </div>
                    <div style={styles.inputRow}>
                      <span style={{ width: '35px', fontSize: '12px' }}>귀쪽</span>
                      <input type="number" value={inputVal.leftTemporal} onChange={e => setInputVal({ ...inputVal, leftTemporal: e.target.value })} style={styles.smallInput} />
                      <button style={styles.setBtn} onClick={() => sendVal('L_TEMP', inputVal.leftTemporal)}>Set</button>
                    </div>
                  </div>

                  {/* 우안 패널 */}
                  <div style={styles.eyePanel}>
                    <div style={{ ...styles.eyeTitle, color: '#0284c7' }}>[ 우 안 ]</div>
                    <div style={styles.inputRow}>
                      <span style={{ width: '35px', fontSize: '12px' }}>코쪽</span>
                      <input type="number" value={inputVal.rightNasal} onChange={e => setInputVal({ ...inputVal, rightNasal: e.target.value })} style={styles.smallInput} />
                      <button style={styles.setBtn} onClick={() => sendVal('R_NASAL', inputVal.rightNasal)}>Set</button>
                    </div>
                    <div style={styles.inputRow}>
                      <span style={{ width: '35px', fontSize: '12px' }}>귀쪽</span>
                      <input type="number" value={inputVal.rightTemporal} onChange={e => setInputVal({ ...inputVal, rightTemporal: e.target.value })} style={styles.smallInput} />
                      <button style={styles.setBtn} onClick={() => sendVal('R_TEMP', inputVal.rightTemporal)}>Set</button>
                    </div>
                  </div>
                </div>
              ) : (
                /* 4분면용 입력창 (2x2 그리드 배치) */
                <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: '8px' }}>
                  <div style={styles.qInputBox}>
                    <small>좌상(Q2)</small>
                    <input type="number" value={inputVal.q2} onChange={e => setInputVal({ ...inputVal, q2: e.target.value })} style={styles.smallInput} />
                    <button style={styles.qBtn} onClick={() => sendVal('Q2', inputVal.q2)}>Set</button>
                  </div>
                  <div style={styles.qInputBox}>
                    <small>우상(Q0)</small>
                    <input type="number" value={inputVal.q0} onChange={e => setInputVal({ ...inputVal, q0: e.target.value })} style={styles.smallInput} />
                    <button style={styles.qBtn} onClick={() => sendVal('Q0', inputVal.q0)}>Set</button>
                  </div>
                  <div style={styles.qInputBox}>
                    <small>좌하(Q3)</small>
                    <input type="number" value={inputVal.q3} onChange={e => setInputVal({ ...inputVal, q3: e.target.value })} style={styles.smallInput} />
                    <button style={styles.qBtn} onClick={() => sendVal('Q3', inputVal.q3)}>Set</button>
                  </div>
                  <div style={styles.qInputBox}>
                    <small>우하(Q1)</small>
                    <input type="number" value={inputVal.q1} onChange={e => setInputVal({ ...inputVal, q1: e.target.value })} style={styles.smallInput} />
                    <button style={styles.qBtn} onClick={() => sendVal('Q1', inputVal.q1)}>Set</button>
                  </div>
                </div>
              )}
            </div>
          </div>
          <div style={{ ...styles.card, flex: 1, display: 'flex', flexDirection: 'column', minHeight: 0 }}>
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
  leftColumn: { width: '320px', display: 'flex', flexDirection: 'column', gap: '20px', height: '100%' },
  rightColumn: { flex: 1, backgroundColor: 'black', borderRadius: '20px', position: 'relative', overflow: 'hidden', border: '6px solid #1e293b', boxShadow: '0 10px 25px rgba(0,0,0,0.2)' },
  card: { backgroundColor: 'white', borderRadius: '18px', padding: '20px', boxShadow: '0 4px 6px rgba(0,0,0,0.05)' },
  cardTitle: { fontSize: '17px', fontWeight: 'bold', marginBottom: '15px', color: '#334155', borderLeft: '5px solid #3b82f6', paddingLeft: '10px' },
  deviceSelect: { width: '100%', padding: '10px', borderRadius: '8px', border: '1px solid #cbd5e1', fontSize: '15px', fontWeight: 'bold', outline: 'none', backgroundColor: '#f8fafc', color: '#334155', cursor: 'pointer' },
  btnRow: { display: 'flex', gap: '6px' },
  btn: { flex: 1, padding: '12px 10px', border: 'none', borderRadius: '10px', cursor: 'pointer', fontWeight: 'bold', fontSize: '14px', transition: '0.2s' },
  controlGroup: { marginTop: '20px', paddingTop: '15px', borderTop: '1px dashed #cbd5e1' },
  groupHeader: { fontSize: '14px', fontWeight: 'bold', color: '#64748b', marginBottom: '10px' },
  eyePanel: { padding: '8px', backgroundColor: '#f8fafc', border: '1px solid #e2e8f0', borderRadius: '8px', display: 'flex', flexDirection: 'column', gap: '5px' },
  eyeTitle: { fontSize: '13px', fontWeight: '900', color: '#e11d48', textAlign: 'center', marginBottom: '4px' },
  inputRow: { display: 'flex', gap: '5px', alignItems: 'center' },
  smallInput: { width: '45px', padding: '6px', borderRadius: '6px', border: '1px solid #ddd', textAlign: 'center' },
  setBtn: { flex: 1, backgroundColor: '#6366f1', color: 'white', border: 'none', borderRadius: '6px', cursor: 'pointer', fontWeight: 'bold' },
  qInputBox: { display: 'flex', flexDirection: 'column', gap: '4px', backgroundColor: '#fff', padding: '5px', borderRadius: '8px', border: '1px solid #eee', alignItems: 'center' },
  qBtn: { width: '100%', backgroundColor: '#6366f1', color: 'white', border: 'none', borderRadius: '4px', fontSize: '12px', cursor: 'pointer' },
  logBox: { flex: 1, overflowY: 'auto', backgroundColor: '#f8fafc', padding: '12px', borderRadius: '10px', border: '1px solid #e2e8f0', fontFamily: 'monospace' },
  screenContainer: { width: '100%', height: '100%', position: 'relative' },
  quad: { position: 'absolute', display: 'flex', justifyContent: 'center', alignItems: 'center', transition: 'background-color 0.15s' },
  valueOverlay: { color: 'white', textAlign: 'center', textShadow: '3px 3px 8px black', zIndex: 10 },
  cross: { position: 'absolute', top: '50%', left: '50%', transform: 'translate(-50%, -50%)', color: 'white', fontSize: '120px', opacity: 0.2, zIndex: 5, pointerEvents: 'none' },
  textOverlay: { position: 'absolute', bottom: '30px', width: '100%', textAlign: 'center', color: 'white', fontWeight: 'bold', fontSize: '24px', textShadow: '2px 2px 5px black', zIndex: 10 }
};

export default App;