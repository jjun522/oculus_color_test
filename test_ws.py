
import asyncio
import websockets

async def test_connection():
    uri = "ws://10.2.52.8:12346/ws"
    try:
        async with websockets.connect(uri) as websocket:
            print("Successfully connected to the server!")
            await websocket.send("ping")
            response = await websocket.recv()
            print(f"Received: {response}")
    except Exception as e:
        print(f"Connection failed: {e}")

if __name__ == "__main__":
    asyncio.run(test_connection())
