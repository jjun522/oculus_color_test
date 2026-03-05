from fastapi import FastAPI, WebSocket, WebSocketDisconnect
from fastapi.middleware.cors import CORSMiddleware
import uvicorn
import socket
import threading
import time

app = FastAPI()
connected_clients = []

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

def udp_broadcast_thread():
    ip = get_local_ip()
    print(f"📡 UDP 탐색 서버 시작 (IP: {ip}, 포트: 50002)")
    udp_socket = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    udp_socket.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
    
    while True:
        try:
            # 💡 기존 유니티 앱이 IP를 직접 읽을 수 있도록 주소를 포함해서 보냅니다.
            message = f"EYE_SERVER:{ip}"
            udp_socket.sendto(message.encode(), ("<broadcast>", 50002))
            time.sleep(2)
        except Exception as e:
            print(f"UDP Error: {e}")
            time.sleep(5)

# 서버 시작 시 UDP 스레드 실행
thread = threading.Thread(target=udp_broadcast_thread, daemon=True)
thread.start()

@app.websocket("/ws")
async def websocket_endpoint(websocket: WebSocket):
    await websocket.accept()
    connected_clients.append(websocket)
    print(f"✅ 새 기기 연결됨!")
    try:
        while True:
            data = await websocket.receive_text()
            for client in connected_clients:
                if client != websocket:
                    await client.send_text(data)
    except WebSocketDisconnect:
        connected_clients.remove(websocket)
        print(f"❌ 기기 연결 끊어짐")

if __name__ == "__main__":
    print(f"🚀 서버 가동 중... (Web: 12346, WebSocket: 8001)")
    uvicorn.run(app, host="0.0.0.0", port=8000)