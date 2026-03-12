
import socket

def listen_udp():
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    sock.bind(("", 50002))
    print("Listening for UDP broadcast on port 50002...")
    while True:
        data, addr = sock.recvfrom(1024)
        print(f"Received message: {data.decode()} from {addr}")
        break 

if __name__ == "__main__":
    try:
        listen_udp()
    except Exception as e:
        print(f"Error: {e}")
