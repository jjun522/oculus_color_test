FROM python:3.9-slim

ENV PYTHONUNBUFFERED=1
WORKDIR /app

COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

COPY . .

# UDP 브로드캐스트와 웹소켓을 위해 포트 오픈
EXPOSE 12346
EXPOSE 50002/udp

CMD ["python", "server.py"]
