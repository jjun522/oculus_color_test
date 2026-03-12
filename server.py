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
connected_devices = {} # { "ip_address": websocket }

app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)

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
    print(f"📡 UDP 탐색 서버 시작 (탐지된 LAN IP: {ip}, 포트: 50002)")
    udp_socket = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    udp_socket.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
    
    while True:
        try:
            # 💡 형식을 EYE_SERVER:IP:PORT 로 변경하여 클라이언트가 포트도 알 수 있게 함
            message = f"EYE_SERVER:{ip}:{PORT}"
            udp_socket.sendto(message.encode(), ("<broadcast>", 50002))
            
            udp_socket.sendto(f"EYE_SERVER:127.0.0.1:{PORT}".encode(), ("127.0.0.1", 50002))
            
            time.sleep(2)
        except Exception as e:
            print(f"UDP Error: {e}")
            time.sleep(5)

# 서버 시작 시 UDP 스레드 실행
thread = threading.Thread(target=udp_broadcast_thread, daemon=True)
thread.start()

async def broadcast_device_list():
    """웹 관제 화면에 현재 접속 중인 기기(IP) 목록을 전송"""
    devices = list(connected_devices.keys())
    message = f"DEVICE_LIST:{','.join(devices)}"
    for ip, ws in list(connected_devices.items()):
        try:
            await ws.send_text(message)
        except:
            pass

@app.websocket("/ws")
async def websocket_endpoint(websocket: WebSocket):
    print(f"📡 새 웹소켓 연결 시도 중... (IP: {websocket.client.host})")
    await websocket.accept()
    client_ip = websocket.client.host
    
    device_id = client_ip
    counter = 1
    while device_id in connected_devices:
        device_id = f"{client_ip}_{counter}"
        counter += 1

    connected_devices[device_id] = websocket
    print(f"✅ 새 기기 연결됨: {device_id}")
    
    # 접속할 때마다 기기 목록 갱신해서 프론트엔드에 알리기
    await broadcast_device_list()

    try:
        while True:
            data = await websocket.receive_text()
            
            # 메시지 라우팅 로직: TARGET:device_id:명령어 형태면 해당 기기에만 전송
            if data.startswith("TARGET:"):
                parts = data.split(":", 2)
                if len(parts) == 3:
                    target_id = parts[1]
                    command = parts[2]
                    if target_id in connected_devices:
                        await connected_devices[target_id].send_text(command)
                    elif target_id == "ALL":
                        # 모두에게 전송
                         for ip, client in list(connected_devices.items()):
                            if client != websocket:
                                await client.send_text(command)
                continue

            # 기본 옛날 통신 방식 (TARGET 미지정 시 모두에게 브로드캐스트)
            for ip, client in list(connected_devices.items()):
                if client != websocket:
                    await client.send_text(data)

    except WebSocketDisconnect:
        if device_id in connected_devices:
            del connected_devices[device_id]
        print(f"❌ 기기 연결 끊어짐: {device_id}")
        await broadcast_device_list()

if __name__ == "__main__":
    print(f"🚀 서버 가동 중... (Port: {PORT}, Backend/WS: {PORT})")
    uvicorn.run(app, host="0.0.0.0", port=PORT)
