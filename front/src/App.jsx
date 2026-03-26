import React, { useState, useEffect, useRef } from 'react';

const App = () => {
  const [status, setStatus] = useState('서버 연결 중...');
  const [vrState, setVrState] = useState({
    divMode: 2, colorIdx: 0,
    leftNasal: 50, leftTemporal: 50, rightNasal: 50, rightTemporal: 50,
    q0: 50, q1: 50, q2: 50, q3: 50,
    leftTop: 50, leftBot: 50, rightTop: 50, rightBot: 50,
    uiText: "VR 기기의 신호를 기다리고 있습니다...",
    currentEyeTarget: 2,
    isFlipMode: false,
    isLeftEyeShown: true
  });

  const [logs, setLogs] = useState([]);
  const [inputVal, setInputVal] = useState({
    leftNasal: 50, leftTemporal: 50, rightNasal: 50, rightTemporal: 50,
    q0: 80, q1: 80, q2: 30, q3: 80,
    leftTop: 50, leftBot: 50, rightTop: 50, rightBot: 50,
    flipInterval: 1.0
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
          setSelectedDevice(prev => (prev !== 'ALL' && !devices.includes(prev)) ? 'ALL' : prev);
          return;
        }
        addLog('수신 ↓', textData, '#8b5cf6');
        if (textData.trim().startsWith('{')) {
          const data = JSON.parse(textData);
          setVrState(data);
          setInputVal(prev => ({
            ...prev,
            leftNasal: data.leftNasal ?? prev.leftNasal,
            leftTemporal: data.leftTemporal ?? prev.leftTemporal,
            rightNasal: data.rightNasal ?? prev.rightNasal,
            rightTemporal: data.rightTemporal ?? prev.rightTemporal,
            q0: data.q0 ?? prev.q0,
            q1: data.q1 ?? prev.q1,
            q2: data.q2 ?? prev.q2,
            q3: data.q3 ?? prev.q3,
            leftTop: data.leftTop ?? prev.leftTop,
            leftBot: data.leftBot ?? prev.leftBot,
            rightTop: data.rightTop ?? prev.rightTop,
            rightBot: data.rightBot ?? prev.rightBot,
          }));
        }
      } catch (e) {
        addLog('에러 ⚠️', `JSON 파싱 실패: ${e.message}`, '#ef4444');
      }
    };
    return () => ws.close();
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
    const keyMap = {
      L_NASAL: 'leftNasal', L_TEMP: 'leftTemporal',
      R_NASAL: 'rightNasal', R_TEMP: 'rightTemporal',
      L_ALL: 'leftNasal', R_ALL: 'rightNasal',
      L_TOP: 'leftTop', L_BOT: 'leftBot',
      R_TOP: 'rightTop', R_BOT: 'rightBot',
    };
    const stateKey = keyMap[target] || target.toLowerCase();
    setVrState(prev => ({ ...prev, [stateKey]: parseInt(val) }));
  };

  // 현재 검사 모드 판별
  // divMode: 2=단안/양안2분면, 3=세로4분면, 4=구형4분면
  const getActiveMode = () => {
    if (vrState.divMode === 3) return '세로4분';
    if (vrState.divMode === 4) return '4분면';
    if (vrState.currentEyeTarget === 0) return '좌안';
    if (vrState.currentEyeTarget === 1) return '우안';
    return '양안2분';
  };
  const activeMode = getActiveMode();

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

    const labelStyle = { position: 'absolute', color: 'white', fontWeight: 'bold', textShadow: '1px 1px 2px black', fontSize: '13px' };

    // 세로 4분면 (divMode=3): 4개의 세로 띠(Stripe)
    if (activeMode === '세로4분') {
      return (
        <div style={styles.screenContainer}>
          <div style={{ display: 'flex', width: '100%', height: '100%' }}>
            {[
              { label: '1번 띠', val: vrState.leftTop },
              { label: '2번 띠', val: vrState.leftBot },
              { label: '3번 띠', val: vrState.rightTop },
              { label: '4번 띠', val: vrState.rightBot }
            ].map((strip, idx) => (
              <div key={idx} style={{ flex: 1, position: 'relative', backgroundColor: getRGB(strip.val), borderRight: idx < 3 ? '4px solid #333' : 'none', display: 'flex', justifyContent: 'center', alignItems: 'center' }}>
                <ValueOverlay percent={strip.val} isSmall />
                <div style={{ ...labelStyle, top: 8, left: 8, color: '#10b981' }}>{strip.label}</div>
              </div>
            ))}
          </div>
          <div style={styles.cross}>+</div>
          <div style={styles.textOverlay} dangerouslySetInnerHTML={{ __html: vrState.uiText.replace(/\n/g, '<br/>') }}></div>
        </div>
      );
    }

    // 구형 4분면 (divMode=4)
    if (activeMode === '4분면') {
      return (
        <div style={styles.screenContainer}>
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
          <div style={styles.cross}>+</div>
          <div style={styles.textOverlay} dangerouslySetInnerHTML={{ __html: vrState.uiText.replace(/\n/g, '<br/>') }}></div>
        </div>
      );
    }

    // 좌안 / 우안 / 양안 2분면 (divMode=2)
    const leftOpacity = vrState.isFlipMode
      ? (vrState.isLeftEyeShown ? 1 : 0.05)
      : ((vrState.currentEyeTarget === 0 || vrState.currentEyeTarget === 2) ? 1 : 0.2);
    const rightOpacity = vrState.isFlipMode
      ? (!vrState.isLeftEyeShown ? 1 : 0.05)
      : ((vrState.currentEyeTarget === 1 || vrState.currentEyeTarget === 2) ? 1 : 0.2);

    return (
      <div style={styles.screenContainer}>
        <div style={{ display: 'flex', width: '100%', height: '100%' }}>
          {/* 왼쪽 눈 */}
          <div style={{ flex: 1, position: 'relative', borderRight: '8px solid #333', opacity: leftOpacity }}>
            {vrState.currentEyeTarget === 2 ? (
              <div style={{ ...styles.quad, left: 0, width: '100%', height: '100%', backgroundColor: getRGB(vrState.leftNasal) }}>
                <ValueOverlay percent={vrState.leftNasal} isSmall />
                <div style={{ ...labelStyle, top: 10, left: 10 }}>좌안 단색</div>
              </div>
            ) : (
              <>
                <div style={{ ...styles.quad, left: 0, width: '50%', height: '100%', backgroundColor: getRGB(vrState.leftTemporal) }}>
                  <ValueOverlay percent={vrState.leftTemporal} isSmall />
                  <div style={{ ...labelStyle, top: 10, left: 10 }}>좌안 귀쪽</div>
                </div>
                <div style={{ ...styles.quad, right: 0, width: '50%', height: '100%', backgroundColor: getRGB(vrState.leftNasal), borderLeft: '2px solid black' }}>
                  <ValueOverlay percent={vrState.leftNasal} isSmall />
                  <div style={{ ...labelStyle, top: 10, right: 10 }}>좌안 코쪽</div>
                </div>
              </>
            )}
          </div>
          <div style={{ width: '16px', backgroundColor: '#111', zIndex: 20 }}></div>
          {/* 오른쪽 눈 */}
          <div style={{ flex: 1, position: 'relative', borderLeft: '8px solid #333', opacity: rightOpacity }}>
            {vrState.currentEyeTarget === 2 ? (
              <div style={{ ...styles.quad, left: 0, width: '100%', height: '100%', backgroundColor: getRGB(vrState.rightNasal) }}>
                <ValueOverlay percent={vrState.rightNasal} isSmall />
                <div style={{ ...labelStyle, top: 10, left: 10 }}>우안 단색</div>
              </div>
            ) : (
              <>
                <div style={{ ...styles.quad, left: 0, width: '50%', height: '100%', backgroundColor: getRGB(vrState.rightNasal), borderRight: '2px solid black' }}>
                  <ValueOverlay percent={vrState.rightNasal} isSmall />
                  <div style={{ ...labelStyle, top: 10, left: 10 }}>우안 코쪽</div>
                </div>
                <div style={{ ...styles.quad, right: 0, width: '50%', height: '100%', backgroundColor: getRGB(vrState.rightTemporal) }}>
                  <ValueOverlay percent={vrState.rightTemporal} isSmall />
                  <div style={{ ...labelStyle, top: 10, right: 10 }}>우안 귀쪽</div>
                </div>
              </>
            )}
          </div>
        </div>
        <div style={styles.cross}>+</div>
        <div style={styles.textOverlay} dangerouslySetInnerHTML={{ __html: vrState.uiText.replace(/\n/g, '<br/>') }}></div>
      </div>
    );
  };

  const modeButtons = [
    { key: '좌안',   label: '좌안 단독',   cmd: () => sendCommand('EYE_LEFT'),                              bg: '#ffe4e6', activeBg: '#e11d48', tc: '#e11d48' },
    { key: '우안',   label: '우안 단독',   cmd: () => sendCommand('EYE_RIGHT'),                             bg: '#ffe4e6', activeBg: '#e11d48', tc: '#e11d48' },
    { key: '양안2분', label: '양안 2분면', cmd: () => { sendCommand('EYE_BOTH'); sendCommand('MODE_2'); },  bg: '#e0f2fe', activeBg: '#0284c7', tc: '#0284c7' },
    { key: '세로4분', label: '세로 4분면', cmd: () => sendCommand('MODE_4V'),                               bg: '#f0fdf4', activeBg: '#10b981', tc: '#10b981' },
  ];

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

          {/* 기기 선택 */}
          <div style={styles.card}>
            <h3 style={{ ...styles.cardTitle, marginBottom: '10px' }}>조작 기기 선택</h3>
            <select value={selectedDevice} onChange={(e) => setSelectedDevice(e.target.value)} style={styles.deviceSelect}>
              <option value="ALL">전체 기기 (동시 조작)</option>
              {connectedDevices.map(ip => <option key={ip} value={ip}>{ip}</option>)}
            </select>
            {connectedDevices.length === 0 && <div style={{ fontSize: '12px', color: '#94a3b8', marginTop: '5px' }}>접속된 VR 기기가 없습니다.</div>}
          </div>

          <div style={styles.card}>
            <h3 style={styles.cardTitle}>리모컨</h3>

            {/* 검사 모드 4종 */}
            <div style={{ marginBottom: '12px', padding: '10px', backgroundColor: '#f8fafc', borderRadius: '8px', border: '1px solid #e2e8f0' }}>
              <div style={{ fontWeight: 'bold', fontSize: '13px', color: '#334155', marginBottom: '8px' }}>검사 모드</div>
              <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: '6px' }}>
                {modeButtons.map(m => (
                  <button key={m.key} style={{ ...styles.btn, backgroundColor: activeMode === m.key ? m.activeBg : m.bg, color: activeMode === m.key ? 'white' : m.tc }} onClick={m.cmd}>
                    {m.label}
                  </button>
                ))}
              </div>
            </div>

            <button style={{ ...styles.btn, backgroundColor: '#e0f2fe', color: '#0284c7', width: '100%', marginBottom: '6px' }} onClick={() => sendCommand('CHANGE_COLOR')}>색상 변경 (Space)</button>
            <button style={{ ...styles.btn, backgroundColor: '#e0f2fe', color: '#0284c7', width: '100%', marginBottom: '12px' }} onClick={() => sendCommand('CHANGE_TARGET')}>타겟 영역 변경 (Enter)</button>

            {/* 일괄 밝기 */}
            <div style={{ display: 'flex', gap: '6px' }}>
              <button style={{ ...styles.btn, flex: 1, backgroundColor: '#3b82f6', color: 'white', fontSize: '16px' }} onClick={() => sendCommand('BRIGHT_UP')}>▲ 일괄 밝기 +2%</button>
              <button style={{ ...styles.btn, flex: 1, backgroundColor: '#3b82f6', color: 'white', fontSize: '16px' }} onClick={() => sendCommand('BRIGHT_DOWN')}>▼ 일괄 밝기 -2%</button>
            </div>

            {/* 플립 모드 */}
            <div style={styles.controlGroup}>
              <div style={styles.groupHeader}>플립 모드 (양안 2분면 전용)</div>
              <div style={{ display: 'flex', gap: '8px', marginBottom: '8px' }}>
                <button style={{ ...styles.btn, flex: 2, backgroundColor: vrState.isFlipMode ? '#ef4444' : '#10b981', color: 'white' }} onClick={() => sendCommand('TOGGLE_FLIP')}>
                  {vrState.isFlipMode ? '⏹ 플립 중지' : '▶ 플립 시작'}
                </button>
                <input type="number" step="0.1" min="0.1" value={inputVal.flipInterval} onChange={e => setInputVal({ ...inputVal, flipInterval: e.target.value })} style={{ ...styles.smallInput, width: '50px' }} />
                <small style={{ fontSize: '10px', alignSelf: 'center' }}>초</small>
                <button style={{ ...styles.setBtn, padding: '0 8px' }} onClick={() => sendCommand(`SET_FLIP_INTERVAL:${inputVal.flipInterval}`)}>Set</button>
              </div>

              {/* 플립 중일 때: 눈별 독립 밝기 */}
              {vrState.isFlipMode && (
                <div style={{ padding: '10px', backgroundColor: '#fef3c7', borderRadius: '8px', border: '1px solid #fde68a' }}>
                  <div style={{ fontSize: '12px', fontWeight: 'bold', color: '#92400e', marginBottom: '8px' }}>눈별 독립 밝기</div>
                  {[
                    { label: '좌안', color: '#e11d48', valKey: 'leftNasal', target: 'L_ALL', upCmd: 'BRIGHT_UP_L', downCmd: 'BRIGHT_DOWN_L' },
                    { label: '우안', color: '#0284c7', valKey: 'rightNasal', target: 'R_ALL', upCmd: 'BRIGHT_UP_R', downCmd: 'BRIGHT_DOWN_R' },
                  ].map(eye => (
                    <div key={eye.label} style={{ ...styles.inputRow, marginBottom: '6px' }}>
                      <span style={{ width: '32px', fontSize: '12px', fontWeight: 'bold', color: eye.color }}>{eye.label}</span>
                      <input type="number" value={inputVal[eye.valKey]} onChange={e => setInputVal({ ...inputVal, [eye.valKey]: e.target.value })} style={styles.smallInput} />
                      <button style={{ ...styles.setBtn, flex: 1 }} onClick={() => sendVal(eye.target, inputVal[eye.valKey])}>Set</button>
                      <button style={styles.arrowBtn} onClick={() => sendCommand(eye.upCmd)}>▲</button>
                      <button style={styles.arrowBtn} onClick={() => sendCommand(eye.downCmd)}>▼</button>
                    </div>
                  ))}
                </div>
              )}
            </div>

            {/* 독립 수치 제어 */}
            <div style={styles.controlGroup}>
              <div style={{ ...styles.groupHeader, display: 'flex', justifyContent: 'space-between' }}>
                <span>독립 수치 제어</span>
                <span style={{ fontSize: '12px', color: '#6366f1', fontWeight: 'bold' }}>{activeMode}</span>
              </div>

              {/* 세로 4분면 (독립 띠 제어) */}
              {activeMode === '세로4분' && (
                <div style={{ ...styles.eyePanel, marginBottom: '8px' }}>
                  <div style={{ ...styles.eyeTitle, color: '#10b981' }}>[ 세로 4개의 띠 독립 제어 ]</div>
                  {[
                    { label: '1번 띠', valKey: 'leftTop', target: 'L_TOP' },
                    { label: '2번 띠', valKey: 'leftBot', target: 'L_BOT' },
                    { label: '3번 띠', valKey: 'rightTop', target: 'R_TOP' },
                    { label: '4번 띠', valKey: 'rightBot', target: 'R_BOT' },
                  ].map(row => (
                    <div key={row.label} style={styles.inputRow}>
                      <span style={{ width: '40px', fontSize: '13px', fontWeight: 'bold' }}>{row.label}</span>
                      <input type="number" value={inputVal[row.valKey]} onChange={e => setInputVal({ ...inputVal, [row.valKey]: e.target.value })} style={styles.smallInput} />
                      <button style={{...styles.setBtn, backgroundColor: '#10b981'}} onClick={() => sendVal(row.target, inputVal[row.valKey])}>Set</button>
                    </div>
                  ))}
                </div>
              )}

              {/* 구형 4분면 */}
              {activeMode === '4분면' && (
                <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: '8px' }}>
                  {[{ label: '좌상(Q2)', key: 'q2', target: 'Q2' }, { label: '우상(Q0)', key: 'q0', target: 'Q0' }, { label: '좌하(Q3)', key: 'q3', target: 'Q3' }, { label: '우하(Q1)', key: 'q1', target: 'Q1' }].map(q => (
                    <div key={q.key} style={styles.qInputBox}>
                      <small>{q.label}</small>
                      <input type="number" value={inputVal[q.key]} onChange={e => setInputVal({ ...inputVal, [q.key]: e.target.value })} style={styles.smallInput} />
                      <button style={styles.qBtn} onClick={() => sendVal(q.target, inputVal[q.key])}>Set</button>
                    </div>
                  ))}
                </div>
              )}

              {/* 2분면 계열 (좌안/우안/양안2분) */}
              {(activeMode === '좌안' || activeMode === '우안' || activeMode === '양안2분') && (
                <div style={{ display: 'flex', flexDirection: 'column', gap: '10px' }}>
                  {vrState.currentEyeTarget === 2 ? (
                    <>
                      {[
                        { title: '[ 좌 안 단색 ]', titleColor: '#e11d48', valKey: 'leftNasal', target: 'L_ALL' },
                        { title: '[ 우 안 단색 ]', titleColor: '#0284c7', valKey: 'rightNasal', target: 'R_ALL' },
                      ].map(panel => (
                        <div key={panel.title} style={styles.eyePanel}>
                          <div style={{ ...styles.eyeTitle, color: panel.titleColor }}>{panel.title}</div>
                          <div style={styles.inputRow}>
                            <span style={{ width: '35px', fontSize: '12px' }}>밝기</span>
                            <input type="number" value={inputVal[panel.valKey]} onChange={e => setInputVal({ ...inputVal, [panel.valKey]: e.target.value })} style={styles.smallInput} />
                            <button style={styles.setBtn} onClick={() => sendVal(panel.target, inputVal[panel.valKey])}>Set</button>
                          </div>
                        </div>
                      ))}
                    </>
                  ) : (
                    <>
                      <div style={styles.eyePanel}>
                        <div style={styles.eyeTitle}>[ 좌 안 (분할) ]</div>
                        {[{ label: '코쪽', valKey: 'leftNasal', target: 'L_NASAL' }, { label: '귀쪽', valKey: 'leftTemporal', target: 'L_TEMP' }].map(r => (
                          <div key={r.label} style={styles.inputRow}>
                            <span style={{ width: '35px', fontSize: '12px' }}>{r.label}</span>
                            <input type="number" value={inputVal[r.valKey]} onChange={e => setInputVal({ ...inputVal, [r.valKey]: e.target.value })} style={styles.smallInput} />
                            <button style={styles.setBtn} onClick={() => sendVal(r.target, inputVal[r.valKey])}>Set</button>
                          </div>
                        ))}
                      </div>
                      <div style={styles.eyePanel}>
                        <div style={{ ...styles.eyeTitle, color: '#0284c7' }}>[ 우 안 (분할) ]</div>
                        {[{ label: '코쪽', valKey: 'rightNasal', target: 'R_NASAL' }, { label: '귀쪽', valKey: 'rightTemporal', target: 'R_TEMP' }].map(r => (
                          <div key={r.label} style={styles.inputRow}>
                            <span style={{ width: '35px', fontSize: '12px' }}>{r.label}</span>
                            <input type="number" value={inputVal[r.valKey]} onChange={e => setInputVal({ ...inputVal, [r.valKey]: e.target.value })} style={styles.smallInput} />
                            <button style={styles.setBtn} onClick={() => sendVal(r.target, inputVal[r.valKey])}>Set</button>
                          </div>
                        ))}
                      </div>
                    </>
                  )}
                </div>
              )}
            </div>
          </div>

          {/* 로그 */}
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
  leftColumn: { width: '320px', display: 'flex', flexDirection: 'column', gap: '20px', height: '100%', overflowY: 'auto' },
  rightColumn: { flex: 1, backgroundColor: 'black', borderRadius: '20px', position: 'relative', overflow: 'hidden', border: '6px solid #1e293b', boxShadow: '0 10px 25px rgba(0,0,0,0.2)' },
  card: { backgroundColor: 'white', borderRadius: '18px', padding: '20px', boxShadow: '0 4px 6px rgba(0,0,0,0.05)' },
  cardTitle: { fontSize: '17px', fontWeight: 'bold', marginBottom: '15px', color: '#334155', borderLeft: '5px solid #3b82f6', paddingLeft: '10px' },
  deviceSelect: { width: '100%', padding: '10px', borderRadius: '8px', border: '1px solid #cbd5e1', fontSize: '15px', fontWeight: 'bold', outline: 'none', backgroundColor: '#f8fafc', color: '#334155', cursor: 'pointer' },
  btn: { flex: 1, padding: '10px 8px', border: 'none', borderRadius: '10px', cursor: 'pointer', fontWeight: 'bold', fontSize: '13px', transition: '0.2s' },
  controlGroup: { marginTop: '16px', paddingTop: '14px', borderTop: '1px dashed #cbd5e1' },
  groupHeader: { fontSize: '13px', fontWeight: 'bold', color: '#64748b', marginBottom: '8px' },
  eyePanel: { padding: '8px', backgroundColor: '#f8fafc', border: '1px solid #e2e8f0', borderRadius: '8px', display: 'flex', flexDirection: 'column', gap: '5px' },
  eyeTitle: { fontSize: '13px', fontWeight: '900', color: '#e11d48', textAlign: 'center', marginBottom: '2px' },
  inputRow: { display: 'flex', gap: '5px', alignItems: 'center' },
  smallInput: { width: '45px', padding: '6px', borderRadius: '6px', border: '1px solid #ddd', textAlign: 'center' },
  setBtn: { flex: 1, backgroundColor: '#6366f1', color: 'white', border: 'none', borderRadius: '6px', cursor: 'pointer', fontWeight: 'bold', padding: '6px 4px' },
  arrowBtn: { padding: '6px 8px', backgroundColor: '#e2e8f0', border: 'none', borderRadius: '6px', cursor: 'pointer', fontWeight: 'bold', fontSize: '12px' },
  qInputBox: { display: 'flex', flexDirection: 'column', gap: '4px', backgroundColor: '#fff', padding: '5px', borderRadius: '8px', border: '1px solid #eee', alignItems: 'center' },
  qBtn: { width: '100%', backgroundColor: '#6366f1', color: 'white', border: 'none', borderRadius: '4px', fontSize: '12px', cursor: 'pointer', padding: '4px' },
  logBox: { flex: 1, overflowY: 'auto', backgroundColor: '#f8fafc', padding: '12px', borderRadius: '10px', border: '1px solid #e2e8f0', fontFamily: 'monospace' },
  screenContainer: { width: '100%', height: '100%', position: 'relative' },
  quad: { position: 'absolute', display: 'flex', justifyContent: 'center', alignItems: 'center', transition: 'background-color 0.15s' },
  valueOverlay: { color: 'white', textAlign: 'center', textShadow: '3px 3px 8px black', zIndex: 10 },
  cross: { position: 'absolute', top: '50%', left: '50%', transform: 'translate(-50%, -50%)', color: 'white', fontSize: '120px', opacity: 0.2, zIndex: 5, pointerEvents: 'none' },
  textOverlay: { position: 'absolute', bottom: '30px', width: '100%', textAlign: 'center', color: 'white', fontWeight: 'bold', fontSize: '24px', textShadow: '2px 2px 5px black', zIndex: 10 }
};

export default App;
