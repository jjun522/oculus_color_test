from fastapi import FastAPI, WebSocket, WebSocketDisconnect
from fastapi.middleware.cors import CORSMiddleware
import uvicorn
import socket
import threading
import time

app = FastAPI()

# 현재 접속 중인 기기들을 기억하는 리스트
connected_clients = []

# CORS 설정
app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)

# --- 💡 UDP 브로드캐스트 (서버 자동 찾기 기능) ---
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
    print(f"📡 UDP 브로드캐스트 시작: {ip} (포트: 50002)")
    udp_socket = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    udp_socket.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
    
    while True:
        message = f"EYE_SERVER:{ip}"
        udp_socket.sendto(message.encode(), ("<broadcast>", 50002))
        time.sleep(2)

# 서버 시작 시 UDP 스레드 실행
thread = threading.Thread(target=udp_broadcast_thread, daemon=True)
thread.start()

@app.websocket("/ws")
async def websocket_endpoint(websocket: WebSocket):
    await websocket.accept()
    connected_clients.append(websocket)
    print(f"✅ 새 기기 연결됨! (현재 접속 중인 기기 수: {len(connected_clients)}대)")
    
    try:
        while True:
            data = await websocket.receive_text()
            print(f"📩 수신된 명령: {data}")
            for client in connected_clients:
                if client != websocket:
                    await client.send_text(data)
                    
    except WebSocketDisconnect:
        connected_clients.remove(websocket)
        print(f"❌ 기기 연결 끊어짐")

if __name__ == "__main__":
    local_ip = get_local_ip()
    print(f"🚀 서버 IP: {local_ip} (포트: 8000)")
    uvicorn.run(app, host="0.0.0.0", port=8000)