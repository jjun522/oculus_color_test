# Eye Color VR Project - 시신경 병증 테스트 (Optic Neuropathy Test)

## 1. 프로젝트 목적
본 프로젝트는 **시신경 병증(Optic Neuropathy)** 상태를 비교 및 테스트하기 위한 VR 기반 검사 솔루션입니다. 

## 2. 개발 환경 및 디바이스
- **Target VR Device**: **Oculus 3S (오큘러스 3S)**
- **Front-End (Web)**: React.js + Vite (언어: JavaScript / Node.js)
- **Front-End (VR)**: Unity (언어: C#)
- **Back-End (Server)**: Python (FastAPI 프레임워크)

## 3. 실행 환경 및 명령어 지침 (Run Commands)

### 3.1 Back-End (파이썬 데이터 중계 서버)
서버는 VR 화면 정보와 웹 브라우저 관리자 간의 통신을 1:1로 중계하는 핵심 역할을 합니다.
- **Conda 환경 이름**: `eye_color`
- **실행 방법**: 최상위 폴더(`eye_color`)에서 터미널을 열고 다음 명령어를 실행합니다.
```bash
# 1. 콘다 환경 활성화
conda activate eye_color

# 2. 파이썬 웹소켓 서버 실행 (기본 포트: 12346)
python server.py 12346
```

### 3.2 Front-End (웹 관리자 화면)
관리자가 환자의 눈 화면 분할 상태를 확인하고 제어하는 웹페이지입니다.
- **위치**: `front` 폴더 내부
- **실행 방법**: 
```bash
# 1. 프론트엔드 폴더로 이동
cd front

# 2. 필수 노드 모듈 설치 (프로젝트를 처음 받았을 때만 실행)
npm install

# 3. 로컬 테스트 서버 띄우기
npm run dev
```

### 4. 개발자 필수 주의사항
- 프론트엔드/백엔드 수정 후 반드시 `npm run dev`와 `python server.py`를 모두 켜 둔 채로 연동 테스트를 진행하세요.
- 오큘러스 3S 기반이기 때문에 VR에서의 모든 로직 변경은 좌안/우안의 독립적 픽셀 제어(분할 모드)가 깨지지 않도록 유의해야 합니다. 
