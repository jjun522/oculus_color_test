from fastapi import FastAPI, WebSocket, WebSocketDisconnect
from fastapi.middleware.cors import CORSMiddleware
import uvicorn
import socket
import threading
import time
import sys
import codecs

# 윈도우 콘솔에서 이모지/한글 출력이 깨지는 현상 방지
if sys.stdout.encoding.lower() != 'utf-8':
    sys.stdout = codecs.getwriter('utf-8')(sys.stdout.buffer, 'strict')

app = FastAPI()
# 전담 연결 변수
vr_client = None
web_client = None
vr_id = None # 현재 연결된 VR의 ID

app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)

async def update_web_status():
    """웹 관리자에게 현재 VR 기기 상태를 보고"""
    if web_client:
        try:
            # VR이 있으면 ID를, 없으면 빈 값을 보냄
            status_msg = f"DEVICE_LIST:{vr_id if vr_client else ''}"
            await web_client.send_text(status_msg)
        except: pass

def get_local_ip():
    try:
        s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        s.connect(("8.8.8.8", 80))
        ip = s.getsockname()[0]
        s.close()
        return ip
    except:
        return "127.0.0.1"

PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 12346

def udp_broadcast_thread():
    ip = get_local_ip()
    print(f"📡 UDP 탐색 서버 시작 (IP: {ip}, Port: 50002)")
    udp_socket = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    udp_socket.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
    while True:
        try:
            msg = f"EYE_SERVER:{ip}:{PORT}"
            udp_socket.sendto(msg.encode(), ("<broadcast>", 50002))
            udp_socket.sendto(f"EYE_SERVER:127.0.0.1:{PORT}".encode(), ("127.0.0.1", 50002))
            time.sleep(2)
        except:
            time.sleep(5)

threading.Thread(target=udp_broadcast_thread, daemon=True).start()

@app.websocket("/ws")
async def websocket_endpoint(websocket: WebSocket):
    global vr_client, web_client, vr_id
    client_ip = websocket.client.host
    role = "PENDING"
    
    try:
        await websocket.accept()

        # 1. 첫 메시지로 VR인지 WEB인지 판별 (VR은 REG:로 시작)
        first_msg = await websocket.receive_text()

        if first_msg.startswith("REG:"):
            # VR 기기 연결 - 기존 VR만 끊고, 웹은 유지
            if vr_client:
                try: await vr_client.close()
                except: pass
            vr_client = websocket
            vr_id = first_msg.split(":", 1)[1]
            role = "VR"
            print(f"👑 [VR 등록] {vr_id}")
            await update_web_status()
        else:
            # 웹 관리자 연결 - 기존 웹만 끊고, VR은 유지
            if web_client:
                try: await web_client.close()
                except: pass
            web_client = websocket
            role = "WEB"
            print(f"🩺 [WEB 연결] {client_ip}")
            await update_web_status()
            # 첫 메시지가 명령이면 VR로 전달
            if vr_client and first_msg:
                cmd = first_msg
                if first_msg.startswith("TARGET:"):
                    parts = first_msg.split(":", 2)
                    if len(parts) == 3: cmd = parts[2]
                await vr_client.send_text(cmd)

        # 2. 이후 메시지 중계
        while True:
            data = await websocket.receive_text()
            if not data: continue

            if role == "VR":
                if web_client: await web_client.send_text(data)
            elif role == "WEB":
                if vr_client:
                    cmd = data
                    if data.startswith("TARGET:"):
                        parts = data.split(":", 2)
                        if len(parts) == 3: cmd = parts[2]
                    await vr_client.send_text(cmd)

    except WebSocketDisconnect:
        pass
    except Exception as e:
        print(f"🔥 [오류] {role} ({client_ip}): {e}")
    finally:
        if role == "VR" and vr_client == websocket:
            vr_client = None
            vr_id = None
            print(f"🔚 [VR 해제] {client_ip}")
        if role == "WEB" and web_client == websocket:
            web_client = None
            print(f"🔚 [WEB 해제] {client_ip}")
        
        # 누군가 떠나면 남은 사람(특히 웹)에게 전파
        await update_web_status()


if __name__ == "__main__":
    print(f"🚀 [1:1 전담] 서버 가동 중... (Port: {PORT})")
    uvicorn.run(app, host="0.0.0.0", port=PORT)
