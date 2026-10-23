# EcatMaster

Oriental Motor **AZD-CED** (EtherCAT) 드라이브 최대 12축을 모션 보드 없이 **N100 미니PC + SOEM** 으로
직접 제어하는 골격 프로젝트. 외부 시스템은 **REST API(OpenAPI)** 로 동작/정지 명령을 내리고,
같은 프로세스가 **시리얼 포트**도 함께 다룬다.

```
┌──────────────┐   HTTP/JSON    ┌──────────────────────────────────────────┐   EtherCAT   ┌─────────┐
│ 외부 시스템   │ ─────────────► │ EcatMaster.Api  (ASP.NET Core, .NET 9)│ ───────────► │ AZD-CED │ ×12
│ (MES/HMI 등) │ ◄───────────── │  ├ IEtherCatMaster  → SoemMaster         │ ◄─────────── │         │
└──────────────┘   /swagger     │  │      (실시간 스레드, 2 ms 사이클)        │  libsoemshim └─────────┘
                                │  ├ SerialService   → /dev/ttyUSB0 …      │
┌──────────────┐   RS-232/485   │  └ SerialCommandBridge (시리얼→모션 명령) │
│ PLC/장비      │ ◄────────────► │                                          │
└──────────────┘                └──────────────────────────────────────────┘
```

## 구성

| 경로 | 내용 |
|---|---|
| `src/EcatMaster.Core` | 하드웨어 독립 라이브러리. CiA402 상태기계(`Axis`), AZD PDO 정의, SOEM P/Invoke, 시뮬레이터, 시리얼 |
| `src/EcatMaster.Api` | REST API + OpenAPI 문서(`/swagger`). 마스터/시리얼을 호스팅 |
| `native/soemshim` | SOEM 1.4.0 을 감싼 얇은 C 라이브러리(`libsoemshim.so`). 미니PC에서 빌드 |
| `deploy/` | systemd 유닛, 미니PC 초기 설정 스크립트 |
| `EcatMaster/` | WPF 테스트 GUI(Windows). SOEM 을 Windows PC 에서 직접 돌려 축 상태를 보고 서보 온/이동/정지를 확인 |

### 설계 요점

- **PP(Profile Position) 모드**: 궤적 생성은 드라이브가 한다. 마스터는 목표/속도/가감속을 PDO 로 보내고
  set‑point 핸드셰이크(bit4 ↔ bit12)만 관리하므로 2 ms 사이클에 수십 µs 지터가 있어도 문제없다.
- **PDO 를 우리가 고정**: PreOp 단계(SOEM PO2SO 훅)에서 1600h/1A00h 를 `AzdPdoMap` 순서로 다시 써서
  C# 구조체(`AzdRxPdo` 23 byte / `AzdTxPdo` 17 byte)와 레이아웃을 일치시킨다.
- **스레드 분리**: 실시간 스레드(SCHED_FIFO, 고정 코어) 하나만 EtherCAT 프레임과 `Axis.Tick()` 을 돈다.
  API 스레드는 `AxisCommand` 레코드를 `Interlocked.Exchange` 로 넘길 뿐 락을 잡지 않는다.
- **시뮬레이션 모드**: `Master:Mode = "Simulated"` 면 네이티브 라이브러리 없이 가상 드라이브로 동작한다.
  Windows 개발 PC 에서 API 와 시퀀스를 그대로 개발·시험할 수 있다.

## 빠른 시작 (Windows, 시뮬레이션)

```bash
dotnet run --project src/EcatMaster.Api --launch-profile "EcatMaster.Api (Simulated)"
```

브라우저에서 <http://localhost:5000/swagger>. 예:

```bash
curl -X POST http://localhost:5000/api/axes/1/enable
curl -X POST http://localhost:5000/api/axes/1/move-absolute -H "content-type: application/json" -d "{\"position\":20000,\"velocity\":10000}"
curl http://localhost:5000/api/axes/1
curl -X POST http://localhost:5000/api/system/stop -H "content-type: application/json" -d "{\"quick\":true}"
```

## Windows PC 에서 실기 확인 (WPF 테스트 GUI)

미니PC 없이 개발 PC 의 이더넷 포트에 드라이브를 직접 물려 SOEM 동작을 확인할 때 쓴다.

0. **자동 연결**: GUI 를 열면 Npcap 이 보여 주는 NIC(루프백·WAN 미니포트·Bluetooth 제외)를 차례로 열어 `ec_config_init` 브로드캐스트로 EtherCAT 슬레이브를 찾는다(포트당 수십~수백 ms).
   슬레이브가 있는 NIC 가 1개면 SOEM 모드로 바로 연결, 여러 개면 "EtherCAT NIC 선택" 창에서 고른다(슬레이브 수와 이름 표시), 없으면 연결 안 된 상태로 둔다.
   상단 "시작 시 자동 연결" 체크로 끌 수 있고(settings.json `AutoConnect`), 자동화 테스트는 환경 변수 `ECATMASTER_NO_AUTOCONNECT=1` 로 건너뛴다.
1. **Npcap 설치**: <https://npcap.com> 에서 무료판 설치. 옵션 중 **"Install Npcap in WinPcap API-compatible Mode"** 체크를 권장한다.
   체크하지 않아도 앱이 `System32\Npcap` 을 DLL 검색 경로에 자동 추가하므로 동작한다.
   **"Restrict Npcap driver's access to Administrators only"** 는 체크하지 말 것 (체크하면 GUI 를 관리자 권한으로 실행해야 함).
   무료판 조건: 조직 내 5대까지, 제품에 동봉·재배포 금지 → 개발·검증용으로만 사용하고 현장은 Linux 로 배포한다.
   (SOEM 은 `wpcap.dll` 을 통해 raw 프레임을 보낸다. 미설치 시 GUI 가 시뮬레이션 모드로만 동작한다.)
2. **네이티브 DLL 빌드** (VS2022 C++ 워크로드 필요, 최초 1회):
   ```powershell
   powershell -ExecutionPolicy Bypass -File native\soemshim\build-win.ps1
   ```
   → `native/soemshim/build-win/soemshim.dll`. WPF 프로젝트가 빌드 시 출력 폴더로 복사한다.
3. Visual Studio 에서 `EcatMaster` (WPF) 프로젝트를 시작 프로젝트로 실행.
4. 상단에서 **SOEM (실제)** 선택 → **NIC** 콤보에서 드라이브가 연결된 어댑터 선택 → **연결**.
   슬레이브 표에 AZD-CED 가 `OP` 로 뜨고 하단 상태바의 WKC 가 `기대값/기대값` 이면 정상.
5. 축을 선택하고 **서보 온** → 목표/속도 입력 → **절대 이동**. 상태 워드 비트와 위치가 실시간으로 갱신된다.
   **조그 버튼은 누르는 동안만 움직이고 떼는 순간 감속 정지(Halt)** 한다. 버튼 밖에서 떼거나 창이 비활성화되어도 정지한다.
   문제가 있으면 로그 패널에 SDO 오류(PDO 재매핑 실패 등)나 AL 상태 코드가 찍힌다.

하단 상태바의 **사이클 LED** 는 실시간 루프 생존 표시다: 녹색 = 최근 5주기 안에 사이클 수행, 빨강 = 정지. 옆 숫자는 최근 1초의 실측 사이클 수/초와 평균 주기로,
2 ms 설정이면 `500/s (2.00 ms)` 근처여야 정상이다 (누적 횟수는 툴팁, API 는 `cycleCount`, `cycleAlive`, `cyclesPerSecond`, `actualCycleTimeUs`).

Windows 는 실시간 OS 가 아니라 사이클 2 ms 에서 수십~수백 µs 지터가 생길 수 있다. 하단의 **최대 지터** 가 사이클의 절반을 넘으면
코어를 고정하거나 사이클을 늘린다. 실제 양산 운용은 Linux 미니PC 로 한다.

**Windows 에서 지터를 줄이는 설정**

- EtherCAT 전용 NIC 의 어댑터 속성에서 Npcap 을 제외한 모든 항목(IPv4, IPv6, LLDP, 클라이언트 등) 체크 해제 → Windows 가 그 포트로 다른 프레임을 보내지 않는다.
- 어댑터 고급 설정에서 **Energy Efficient Ethernet**, **Green Ethernet**, **Interrupt Moderation** 을 끈다.
- 전원 옵션을 "고성능" 으로, USB 이더넷 어댑터는 EtherCAT 용으로 쓰지 않는다.
- GUI 상단 "코어" 에 실시간 스레드를 고정할 코어 번호를 넣는다 (예: 2). 시작 직후 500 사이클은 지터 통계에서 제외되며, 하단 **통계 리셋** 으로 누적값을 지울 수 있다.

**첫 연결 때 로그로 확인하는 것**

- `Found N slaves` 와 각 슬레이브의 `vendor=0x2BE` → 벤더 ID 가 다르면 `Master:VendorId` 수정.
- `Slave n: remapping PDOs` 뒤에 SDO 오류가 없고 `IOmap uses ...` 가 축 수 × 40 byte 근처 → PDO 재매핑 성공.
- `PDO size mismatch` 경고가 나오면 바로 아래에 드라이브의 실제 1600h/1A00h 매핑이 덤프된다. 그 순서에 맞춰 `AzdPdo.cs` 를 수정하거나 재매핑을 끈다.
- `All slaves OPERATIONAL, expected WKC=…` → 통신 정상. 이후 우측 **SDO 읽기/쓰기** 패널로 6091h(기어비), 6098h(원점 방식) 등 드라이브 설정을 확인한다.

## IO 모듈과 인터록

EK1100 커플러 뒤의 EL1889(16 DI)·EL2889(16 DO) 같은 디지털 터미널을 축과 같은 사이클로 다룬다.

**슬레이브 자동 분류** (연결 시 로그로 확인)

| 조건 | 분류 |
|---|---|
| 벤더 ID = `Master:VendorId` (Oriental 0x2BE) 이고 PDO 크기가 맞음 | 축. 축 번호는 등록 순서 1..N (슬레이브 위치와 무관) |
| 비트 PDO 가 있는 그 외 슬레이브 (Beckhoff 0x2 등) | IO 모듈 `IO1`, `IO2`… (최대 64 bit/방향) |
| PDO 가 없는 슬레이브 (EK1100 등) | 무시 |
| `Master:IoModules[{Slave, Kind:"Ignore"}]` | 강제 무시 (아날로그 터미널 등) |

`Master:IoModules` 로 모듈 이름과 비트 이름을 붙일 수 있다. 예시가 `appsettings.json` 에 있다 (슬레이브 13 = DI1, 14 = DO1).

**인터록 규칙** (`Master:Interlocks`, GUI 는 `%LOCALAPPDATA%\EcatMaster\devices.json` — 프로젝트 이름이 DirectControl 이던 시절의 `%LOCALAPPDATA%\DirectControl` 폴더가 있으면 첫 실행 때 `AppPaths` 가 자동으로 복사해 온다)

```json
{ "Name": "EStop", "Module": "DI1", "Bit": 0, "ActiveLow": true,
  "Action": "QuickStop", "Axes": [], "BlockMotion": true,
  "OutputModule": "DO1", "OutputBit": 2, "Message": "비상정지 눌림" }
```

- `ActiveLow: true` 는 입력이 0 일 때 활성 (NC 접점 비상정지·도어 스위치). 배선이 안 된 입력은 0 이므로 **활성 상태로 시작**한다.
- 활성 상승 에지에서 `Action`(Halt / QuickStop / Disable / None) 을 대상 축(`Axes` 비우면 전체)에 적용한다.
- `BlockMotion` 이면 활성 중 서보 온·이동·조그·원점복귀 명령을 거부한다. REST 는 409, GUI 는 축 표 "인터록" 열과 마지막 오류에 사유가 뜬다. 정지·서보 오프·알람 리셋은 항상 허용.
- 모듈이 오프라인(WKC 오류)이면 안전 측(활성)으로 본다.
- **참조 위치 표기**: GUI 인터록 탭은 "소스 (슬레이브/축)" = `DI1 (slave 13)` / `축 Z(3) (slave 7)`, "조건" = `DI1.IN0 (EStop) = 0` / `축 X(1) 이동 중` / `축 Z(3) 위치 1000~2000 um 안 (±20)`, "현재값" = 입력 1/0 · 이동 중/정지 · 현재 위치, "표시 출력 (슬레이브)" = `DO1.OUT2 · InterlockLamp (slave 14)`, "대상 축" = `전체 (12축)` 또는 `X(1), Y(2)` 로 어느 슬레이브·축의 무엇을 보는지 그대로 보여 준다. 연결 로그·트리거 로그·상태바(`비상정지 눌림 [DI1.IN0 (EStop) = 0]`)도 같은 문구(`InterlockText`)를 쓴다. 시뮬레이션 모듈은 slave 1001, 1002… 로 표시된다.

**축 조건 인터록** (`Source` = `AxisMoving` | `AxisInRange` | `AxisOutOfRange`; 없으면 `Input`)

```json
{ "Name": "ZvsXMoving", "Source": "AxisMoving", "SourceAxis": "X", "Mode": "BlockWhileActive",
  "Action": "Halt", "Axes": [3], "BlockMotion": true, "Message": "X 이동 중 — Z 대기" },
{ "Name": "XNeedsZSafe", "Source": "AxisInRange", "SourceAxis": "Z", "Min": 1000, "Max": 2000, "Unit": "um", "Hysteresis": 20,
  "Mode": "AllowOnlyWhileActive", "Action": "Halt", "Axes": [1], "BlockMotion": true, "Message": "Z 안전 범위 밖 — X 금지" }
```

- 규칙 하나 = 조건 하나. 여러 규칙은 자연히 AND 로 겹친다(하나라도 활성이면 그 축은 차단).
- `SourceAxis` 는 축 번호 또는 별칭. **소스 축이 대상 축에 들어가면(Axes 비움 = 전체 포함) 자기 참조로 거부**되어 규칙이 비활성(오류 표시)된다.
- `Mode`: `BlockWhileActive`(조건 참 → 정지·차단, 기본) / `AllowOnlyWhileActive`(조건 참일 때만 허용, 거짓이면 정지·차단. 연결 직후 조건이 안 맞으면 바로 차단 상태로 시작).
- `AxisMoving`: 소스 축이 이동/조그/원점복귀 명령 상태(IsMotionActive). 실기 드라이브는 Halt 뒤에도 잠시 감속하므로 `VelocityThreshold`(단위/s)를 주면 |실속도| 로도 판정한다.
- `AxisInRange/OutOfRange`: `Min < Max`, 단위 `um`/`step`(비우면 소스 축의 단위), 매 사이클 소스 축 위치를 사용자 단위로 변환해 비교(리드·기어 변경 자동 반영). `Hysteresis` 는 차단 **해제**에만 적용: 진입은 정확한 경계, 해제는 경계에서 h 만큼 벗어나야 한다(2h < 폭).
- `Action` 을 생략하면 기본은 입력 조건 QuickStop, 축 조건 **Halt**. 소스 축이 오프라인이면 두 모드 모두 안전 측(차단). 소스 축 Fault 는 위치가 유효하므로 조건을 그대로 평가한다.
- 평가는 실시간 스레드에서 축 Tick 직전에 이루어져 위치는 이번 사이클 값, 이동 여부는 직전 사이클 값이다.

**GUI 규칙 편집기**: 인터록 탭 "추가… / 편집… / 삭제"(행 더블클릭 = 편집, 연결 중에만). 대화상자에서 조건 종류·IO 모듈(슬레이브)·비트(별칭)·소스 축·범위/단위/히스테리시스·방식·동작·대상 축 체크리스트·차단·표시 출력·메시지·사용을 고르면 실시간으로 검증(`ValidateInterlock`)과 미리보기 문장이 뜬다. 저장하면 마스터의 규칙 배열이 원자적으로 교체되어 **즉시 적용**되고(같은 이름은 트리거 통계 이월, 제거된 규칙의 램프는 OFF) devices.json 에 저장된다. 삭제와 안전 수준을 낮추는 편집(차단 해제·사용 끔·정지 동작→None)은 확인 창을 거친다.
- 평가는 실시간 스레드에서 매 사이클 이루어져 REST 를 거치지 않고 1~2 사이클 안에 반응한다.

**상태 출력** (`Master:StatusOutputs`): `AnyMoving`, `AnyFault`, `AllServoOn`, `AnyServoOn`, `AnyInterlock`, `BusOk`, `Heartbeat`(1 Hz) 를 DO 비트로 내보낸다. 램프·부저·PLC 신호용.

**별칭(alias)**: 축·IO 모듈·IO 비트에 사용자 이름을 붙이고, API 경로와 규칙에서 번호 대신 쓸 수 있다.

```json
"Axes": [ { "Id": 1, "Name": "X", "LeadUm": 10000 }, { "Id": 3, "Name": "Z", "LeadUm": 5000 } ],
"IoModules": [ { "Slave": 13, "Name": "DI1", "InputAliases": { "0": "EStop", "1": "Door" } } ],
"Interlocks": [ { "Name": "EStop", "Module": "DI1", "Input": "EStop", "ActiveLow": true, "Action": "QuickStop" } ],
"StatusOutputs": [ { "Module": "DO1", "Output": "RunLamp", "Source": "AnyMoving" } ]
```

- `/api/axes/X/move-absolute`, `/api/io/DO1/outputs/RunLamp` 처럼 번호와 별칭을 모두 받는다 (대소문자 무시). 숫자만으로 된 별칭은 번호와 혼동되므로 피한다.
- 런타임 변경: `PUT /api/axes/{axis}/alias`, `PUT /api/io/{name}/alias`, `PUT /api/io/{name}/inputs/{bit}/alias` `{ "name": "..." }`. 영구 반영은 설정 파일에 한다.
- GUI: 선택 축 패널의 "별칭 저장", IO 탭에서 비트 오른쪽 클릭 → "별칭 변경…". 둘 다 `devices.json` 에 저장된다.

**GUI 의 IO 탭 (스테이션 그룹)**: 하위 탭 하나 = IO 그룹 하나. 그룹은 보통 입력 모듈 + 출력 모듈 한 쌍이며 입력을 왼쪽, 출력을 오른쪽 카드로 나란히 보인다.
- 묶는 규칙: ① `IoModules[].Group` 이 있으면 같은 Group 이름끼리 (예: `"Group": "Station1"`). ② 없으면 이름 끝 숫자가 같은 **입력전용 1개 + 출력전용 1개**를 자동으로 묶어 "DI1/DO1" 탭으로. ③ 혼합형(입출력이 한 슬레이브)이나 짝이 없는 모듈은 단독 탭.
- 탭 앞의 점은 그룹의 모든 모듈이 온라인이면 녹색, 하나라도 빠지면 빨강. 각 카드에는 모듈별 "오프라인" 배지가 따로 뜬다.
- "창으로 열기"는 그룹을 **모달리스 창**(`IoGroupWindow`, 메인 창 소유)으로 띄워 다른 탭이나 축 제어를 보면서 IO 를 감시·조작할 수 있다. 같은 그룹을 다시 열면 기존 창이 앞으로 오고, 해제하면 모두 자동으로 닫힌다. 탭과 창은 같은 뷰모델을 공유한다.
- **별칭 편집…** (탭과 창 모두): 그룹의 모든 비트를 표(모듈/방향/비트/현재 별칭/새 별칭)에서 한 번에 고친다(`IoAliasEditorWindow`). 저장하면 즉시 반영 + `devices.json` 저장. 비우면 기본 이름(IN4/OUT4)으로 돌아가며 이때 파일의 alias 항목은 지워진다. 비트 오른쪽 클릭 → "별칭 변경…" 도 그대로 쓸 수 있다.

**GUI 스타일**: `EcatMaster/Themes/WinUI3.xaml` 이 Fluent(WinUI 3) 느낌의 스타일(둥근 모서리, 강조색 `#0067C0`, 카드형 GroupBox, 밑줄형 TabItem)을
Button·TextBox·ComboBox·CheckBox·RadioButton·TabControl·DataGrid·ListBox·ScrollBar 에 암시적으로 적용한다. 성능을 위해 Storyboard/VisualState 애니메이션은 쓰지 않고
트리거만 사용한다. TabControl 템플릿의 `PART_SelectedContentHost` 이름은 UI 자동화(UIA)가 탭 내용을 노출하는 데 필요하므로 유지할 것.
- 연결 시 중복 별칭과 숫자 별칭은 경고 로그로 알려 준다.

**API**: `GET /api/io`, `PUT /api/io/{name}/outputs/{bit}` `{value}`, `PUT /api/io/{name}/outputs` `{value, mask}`, `GET /api/interlocks`.
시뮬레이션에서는 `PUT /api/io/{name}/inputs/{bit}` `{value}` 로 입력을 강제해 인터록을 시험할 수 있다 (GUI 는 IO 탭에서 입력 버튼 클릭).

## API 요약

| 메서드 | 경로 | 설명 |
|---|---|---|
| GET | `/api/system/status` | 마스터/슬레이브 상태, WKC, 지터 |
| GET | `/api/system/health` | 200 정상 / 503 EtherCAT 비정상 |
| POST | `/api/system/enable` `/disable` `/fault-reset` | 전체 축 |
| POST | `/api/system/stop` `{quick}` | 전체 감속 정지(false) / 급정지+서보오프(true) |
| GET | `/api/axes`, `/api/axes/{id}` | 축 상태 |
| POST | `/api/axes/{id}/enable` `/disable` `/fault-reset` | 서보 온/오프, 알람 리셋 |
| POST | `/api/axes/{id}/move-absolute` `/move-relative` | `{position, velocity?, acceleration?, deceleration?}` |
| POST | `/api/axes/{id}/jog` | `{velocity}` PV 모드, 부호가 방향 |
| POST | `/api/axes/{id}/stop` `/quick-stop` `/home` | 감속 정지 / 급정지 / 원점복귀 |
| GET | `/api/serial/devices`, `/api/serial` | 장치 목록, 설정된 포트 상태 |
| POST | `/api/serial/{name}/open` `/close` `/write` | `write`: `{text}` 또는 `{hex}` |
| GET | `/api/serial/{name}/read?max=` | 수신 라인 꺼내기 |

명령 POST 는 모두 **202 Accepted** 를 즉시 반환하고 다음 EtherCAT 사이클에 적용된다.
결과는 `/api/axes/{id}` 의 `activity`, `targetReached`, `lastError` 로 확인한다.

## 미니PC 배포 (Linux)

1. Ubuntu 24.04 또는 Debian 12 설치 후
   ```bash
   sudo deploy/setup-minipc.sh enp2s0      # EtherCAT 전용 NIC 이름
   sudo reboot                             # RT 커널 + isolcpus 적용
   uname -a                                # PREEMPT_RT 확인
   ```
2. 네이티브 라이브러리 빌드
   ```bash
   native/soemshim/build.sh                # → native/soemshim/build/libsoemshim.so
   ```
3. 게시
   ```bash
   dotnet publish src/EcatMaster.Api -c Release -r linux-x64 --self-contained false -o /opt/ecatmaster
   ```
   `csproj` 가 `libsoemshim.so` 를 출력 폴더에 함께 복사한다.
4. `/opt/ecatmaster/appsettings.json` 에서 `Master:Mode=Soem`, `Master:Interface`, `Master:CpuCore` 확인
5. 서비스 등록
   ```bash
   sudo cp deploy/ecatmaster.service /etc/systemd/system/
   sudo systemctl daemon-reload && sudo systemctl enable --now ecatmaster
   journalctl -u ecatmaster -f
   ```

기동 로그에 `All slaves OPERATIONAL, expected WKC=…` 가 나오면 정상이다.

## 설정 (`appsettings.json` → `Master`)

| 키 | 기본 | 설명 |
|---|---|---|
| `Mode` | `Soem` | `Soem` / `Simulated` |
| `Interface` | `enp2s0` | EtherCAT 전용 NIC (`ip -br link`) |
| `CycleTimeUs` | 2000 | 사이클. 안정 확인 후 1000 으로 낮출 수 있음 |
| `RealtimePriority` | 80 | SCHED_FIFO 우선순위 |
| `CpuCore` | 3 | 실시간 스레드 고정 코어 (`isolcpus` 와 일치) |
| `AxisCount` | 12 | 슬레이브 1..N 이 축 1..N |
| `VendorId` | 702 (0x2BE) | 이 벤더 ID 슬레이브에만 PDO 재매핑 |
| `Defaults.Velocity/Acceleration/Deceleration` | 5000 / 1000 / 1000 | 명령에 값이 없을 때 사용 |

## 왕복 테스트 (A ↔ B, N회)

검증·내구 시험용으로 한 축을 A 지점과 B 지점 사이에서 지정 속도로 N회(0 = 무한) 왕복시킨다.
GUI 는 선택 축 패널의 **왕복 테스트** 그룹("현재→A/B" 로 현재 위치를 지점으로 지정), REST 는 아래와 같다.

```
POST   /api/axes/X/cycle-test   { "pointA": 0, "pointB": 50000, "velocity": 20000, "cycles": 1000, "dwellMs": 200 }
GET    /api/axes/X/cycle-test   → 완료 횟수, 진행 단계, 이동 시간 min/max, 최대 위치 오차, 중단 사유
DELETE /api/axes/X/cycle-test   → 중지 (진행 중 이동은 Halt)
GET    /api/cycle-tests         → 모든 축
```

- 서보 온 상태에서만 시작된다. 실행 중 서보 오프·알람·인터록·통신 끊김·위치 오차(기본 허용 5 step)·이동 타임아웃(기본 60 s)이 나면 즉시 `Failed` 로 끝나고 사유를 남긴다.
- 통계(이동 시간 최소/최대, 최대 위치 오차)는 검증계획 3단계(장기 안정성)의 판정 자료로 쓴다.
- 여러 축을 동시에 돌릴 수 있다 (축마다 독립 실행).

## 연속 운전 (시간 기반 내구 시험 · 에러 기록)

연속 운전 시 안정성을 평가하려고 한 축을 **분/시간 단위**로 돌리면서 그동안 생긴 이상을 모두 파일로 남긴다.
GUI 는 선택 축 패널의 **연속 운전** 그룹, REST 는 아래와 같다.

```
POST   /api/axes/X/endurance-test   { "mode": "Rotate", "velocity": 66667, "durationMinutes": 120 }
POST   /api/axes/X/endurance-test   { "mode": "Reciprocate", "pointA": 0, "pointB": 50000, "velocity": 20000, "durationMinutes": 60, "dwellMs": 200 }
POST   /api/axes/X/endurance-test   { "mode": "Rotate", "velocity": 41667, "durationMinutes": 0 }      ← 0 = 무한 (중지할 때까지)
GET    /api/axes/X/endurance-test   → 경과, RPM 최소/평균/최대, 오류·경고 수, 최근 사건 200개, 기록 파일 경로
DELETE /api/axes/X/endurance-test   → 중지 (감속 정지 후 요약 기록)
GET    /api/endurance-tests         → 모든 축
```

- **방식**: `Rotate` 는 속도(PV) 모드로 한 방향 연속 회전(속도 부호 = 방향, GUI 는 "역방향" 체크), `Reciprocate` 는 A↔B 왕복을 시간이 끝날 때까지 반복(GUI 는 위 왕복 테스트의 A/B·정지 시간 사용).
- **기록** (`%LOCALAPPDATA%\EcatMaster\endurance\`, Linux 는 `~/.local/share/EcatMaster/endurance/`, GUI "기록 폴더" 버튼):
  - `…_events.csv` — 사건: 드라이브 알람(603Fh 코드), 드라이브 경고(6041h bit7), 서보 꺼짐, 축 오프라인, 인터록 차단, 축 오류, 추종 오차, 속도 편차(지령 대비 `speedTolerancePercent`% 초과가 1 s 이상), 회전 멈춤, 실시간 사이클 정지, 슬레이브 OP 이탈·AL 상태, WKC 오류·사이클 누락(1초 단위로 묶음). 발생·해제 시각과 그 순간의 위치/속도/RPM/상태 워드를 함께 남긴다.
  - `…_samples.csv` — `sampleIntervalSeconds`(기본 1 s) 마다 위치·속도·RPM·상태·WKC·지터 스냅샷.
  - `…_summary.txt` — 결과, RPM 통계, 누적 회전수, 종류별 발생 횟수, 버스 통계.
  CSV 는 UTF-8 BOM 이라 Excel 에서 바로 열린다.
- **오류 처리**: 기본은 첫 오류에서 감속 정지하고 `Failed` 로 끝낸다. `autoRecover: true`(GUI "오류 시 자동 복구")면 알람 리셋 → 서보 온 → 운전 재개를 `maxRecoveries` 회까지 시도하고, 그 과정도 사건으로 남긴다.
- 서보 온·인터록 해제 상태에서만 시작된다(아니면 REST 409). 시작할 때 마스터의 WKC 오류·사이클 누락·최대 지터 통계를 초기화해 시험 구간만 평가한다(`resetBusStatistics: false` 로 끔).
- GUI 는 시험 중 Windows 절전을 막는다(화면 꺼짐은 허용). 해제(연결 끊기)하면 진행 중 시험을 중지하고 요약까지 기록한 뒤 끊는다.

## 드라이브 프로파일 (AZD / VPH-HD)

벤더별 차이는 `IDriveProfile` 로 분리되어 있고, 슬레이브의 벤더 ID 로 자동 선택된다 (`Axes[].Profile` 로 강제 지정 가능).
한 버스에 두 종류를 섞어 쓸 수 있다.

| 항목 | Oriental AZD (0x2BE) | Nikki Denso VPH-HD / NCR-HD (0x816) |
|---|---|---|
| PDO | 1600h/1A00h 재매핑 (Rx 7, Tx 6 엔트리) | 1600h/1A00h 재매핑 (Rx 5: 6040/6060/607A/6081/60FF, Tx 6). 6083h/6084h 는 매핑 불가 → **SDO 로 전송**(이동 직전, 정지 시 반영) |
| 동기 | 프리런 | **DC SYNC0 필수** → 마스터가 DC 기준 클록에 위상 동기 (`DcSyncOffsetNs`, `DcSync0ShiftNs`) |
| 조그 | PV 모드 | pv 미지원 → **PP 상대 이동**(2억 step)으로 조그, 떼면 Halt |
| PP set-point | Bit4 + Bit5(Change set immediately) | **Bit5 는 0 고정** (1 이면 603Fh = FFF3h 경고 + set-point 거부). 진행 중 이동 위에 새 set-point 를 걸 땐 Halt 로 세워 Target reached 후에 건다 (`SupportsChangeSetImmediately=false`) |
| 분해능 | 10,000 × B / A (6091h 전자기어 A/B) | 엔코더 분해능(`Axes[].PulsesPerRev`, 예: 20 bit = 1048576) × 분모 / 분자 (6091h, P162/P163) |
| 기어비 47F1h | 있음 | 없음 |
| 유지보수 | 40C6h Configuration, 40C9h NV 저장 | 없음 (드라이브 도구로 저장) |
| 단위 | step, Hz, step/s² | 지령단위(cmd), cmd/s, cmd/s² |
| 원점복귀 방식 | 매뉴얼 참조 | 8, 12, 20, 22, 33, 34, 35 |
| P 파라미터 SDO | 4xxxh 고유 | `3000h + 번호` (P162 = 30A2h) |

VPH-HD 는 매뉴얼 K_TJ-42620 Ver.1.5 기준. **Windows 에서 시험할 때** DC 동기 드라이브는 SYNC0 이전에 프레임이 도착해야 하므로
지터가 크면 동기 오류(603Fh = FFE3h 계열)가 날 수 있다. 그때는 `Master:DcSyncOffsetNs` 를 사이클의 1/4(2 ms → 500000) 로 키우고
사이클을 4 ms 로 늘려 본다. 양산은 Linux RT 에서 검증한다.

## 단위와 드라이브 설정

AZ 시리즈 EtherCAT 의 원단위는 **위치 step, 속도 Hz(step/s), 가감속 step/s²** 이다 (매뉴얼 HM-60375).
분해능은 `6091h` 전자기어로 정해지고(`분해능 = 10,000 × B / A` step/rev, 공장값 A=B=1), 기어드 모터는 `47F1h` 기어비(×0.01)가 붙는다.
드라이브에 "리드(µm/rev)" 는 없으므로 그 값만 소프트웨어 설정으로 준다.

```
step/µm = 분해능[step/rev] × 기어비 / 리드[µm/rev]
예) 10,000 step/rev, 기어비 1, 볼스크류 리드 10 mm(10,000 µm) → 1 step = 1 µm
```

**동작 방식 (셋업이 끝난 장비를 그대로 쓰는 원칙)**

- 연결 시 축마다 드라이브에서 `6091h` 전자기어, `47F1h` 기어비, `6081h/6083h/6084h` 속도·가감속을 SDO 로 읽어 그대로 사용한다.
  605Ah/605Dh 옵션 코드도 덮어쓰지 않는다 (`Master:WriteOptionCodes=false`).
- REST 와 GUI 는 `Master:Units:Mode` (기본 `um`) 단위로 값을 받고 보여 준다. 요청에 `unit: "step"` 을 주면 그 호출만 step 으로 해석한다.
- 수정은 `PUT /api/axes/{id}/drive-settings` 또는 GUI "드라이브 설정" 패널에서 한다. 바뀐 항목만 SDO 로 쓰고,
  전자기어/기어비는 갱신 시점 C 이므로 **Configuration(40C6h)** 을 실행하거나 **NV 저장(40C9h)** 후 제어전원을 다시 넣어야 반영된다.
- 속도/가감속 기본값은 우리가 PDO 로 매 사이클 보내는 값이므로 GUI 에서 바꾸면 즉시 다음 명령부터 쓰이고, NV 저장하면 다음 연결 때도 같은 값이 읽힌다.

| 설정 키 | 기본 | 설명 |
|---|---|---|
| `Master:Units:Mode` | `um` | API/GUI 단위 (`um` 또는 `step`) |
| `Master:Units:DefaultLeadUm` | 10000 | 출력축 1회전 이동량 [µm] |
| `Master:Units:AxisLeadUm` | `[]` | 축별 리드 (인덱스 0 = 1축), 0 이면 기본값 |
| `Master:Defaults:UseDriveValues` | true | 연결 시 드라이브 속도·가감속을 기본값으로 읽음 |
| `Master:WriteOptionCodes` | false | true 면 605Ah=2, 605Dh=1 로 덮어씀 |

## 코드로 축 제어하기 (샘플)

`samples/EcatMaster.Samples` 는 GUI 없이 Core 만으로 특정 모터에 명령을 내리는 콘솔 예제다.

```
dotnet run --project samples/EcatMaster.Samples                 # 시뮬레이션 (하드웨어 불필요)
dotnet run --project samples/EcatMaster.Samples -- --soem I350  # 설명에 "I350" 이 들어간 NIC 로 실기 연결
dotnet run --project samples/EcatMaster.Samples -- --axis Z     # 대상 축 (번호 또는 별칭, 기본 X)
```

흐름: `MasterOptions` → `SoemMaster`/`SimulatedMaster` → `TryStartAsync` → `GetAxis("X")` → `Submit(...)` → 상태 폴링. `AxisControl.cs` 는
`EnableAsync`, `MoveAsync`(절대/상대, 완료 대기, 거부·알람을 예외로), `JogForAsync`(지정 시간 조그 후 Halt), `HomeAsync`, `Halt`, `QuickStop`, `Describe` 를 제공하며
다른 솔루션에 그대로 복사해 쓸 수 있다. `Submit` 은 축당 최신 명령 1개만 유지하는 실시간 우편함이고, 거부 사유는 `Axis.LastError` 에 남는다(인터록 차단은 `MotionBlocked/BlockReason`).

## 다른 솔루션으로 이식

복사할 것은 세 덩어리다. Api 프로젝트는 필요 없다.

| 덩어리 | 경로 | 비고 |
|---|---|---|
| 제어 라이브러리 | `src/EcatMaster.Core` | OS 중립. 참조만 추가 (`ProjectReference`). 패키지: Microsoft.Extensions.Options/Logging.Abstractions/Hosting.Abstractions, System.IO.Ports |
| 네이티브 심 | `native/soemshim` (`build-win/soemshim.dll` 또는 `build-win.ps1` 로 재빌드) | csproj 에 `<None Include="…\soemshim.dll" CopyToOutputDirectory="PreserveNewest" Link="soemshim.dll"/>` + `<PlatformTarget>x64</PlatformTarget>` |
| WPF GUI | `EcatMaster/{Views, ViewModels, Infrastructure, Themes}` | 폴더 통째로. 네임스페이스 `EcatMaster.Views / .ViewModels / .Infrastructure` |

GUI 폴더 구성:
- `Views/` — 모든 창: `MainWindow`, `IoGroupWindow`(모달리스 IO), `IoAliasEditorWindow`, `InterlockEditorWindow`, `NicPickerWindow`, `PromptDialog`(코드 전용 한 줄 입력).
- `Themes/WinUI3.xaml` — 스타일·브러시. `Themes/Converters.xaml` — 전역 컨버터(`BoolToVis`, `Inverse`, `NullToVis`, `Join`, `LocalTime`, `UsToMs`). `Themes/DataTemplates.xaml` — 공용 DataTemplate(`IoBitTemplate`, `IoModuleContentTemplate`, `IoGroupContentTemplate`, `IoGroupTabHeaderTemplate`).
- 창들은 자체 리소스에 컨버터/템플릿을 다시 정의하지 않고 App 수준 리소스를 `StaticResource` 로 쓴다. 따라서 이식할 때 `App.xaml` 에 세 사전을 **이 순서로** 병합해야 한다(스타일 → 컨버터 → 템플릿):

```xml
<Application.Resources>
  <ResourceDictionary>
    <ResourceDictionary.MergedDictionaries>
      <ResourceDictionary Source="Themes/WinUI3.xaml"/>
      <ResourceDictionary Source="Themes/Converters.xaml"/>
      <ResourceDictionary Source="Themes/DataTemplates.xaml"/>
    </ResourceDictionary.MergedDictionaries>
  </ResourceDictionary>
</Application.Resources>
```

**MVVM 규약** (CommunityToolkit.Mvvm 8.4 기반)
- 뷰모델은 `CommunityToolkit.Mvvm.ComponentModel.ObservableObject` 를 상속하고 커맨드는 `RelayCommand`/`AsyncRelayCommand`/`RelayCommand<T>` 를 쓴다. 자체 베이스 클래스는 없다.
  툴킷 커맨드는 CanExecute 를 자동 재평가하지 않으므로 `MainViewModel.RefreshCommandStates()` 가 연결/선택/바쁨 상태 변화와 100 ms 갱신마다 모든 `IRelayCommand` 에 `NotifyCanExecuteChanged` 를 보낸다.
- 뷰모델은 창을 직접 만들지 않는다. 대화상자·모달리스 창은 `ViewModels/IDialogService` (Prompt, Confirm, EditInterlock, EditIoAliases, PickNic, ShowIoGroup, CloseIoGroupsExcept) 로만 열고, WPF 구현은 `Views/WpfDialogService`, 테스트용은 `NullDialogService`.
  `MainViewModel(IDialogService)` 생성자에 주입하므로 기존 프로젝트의 다이얼로그 서비스로 어댑터를 만들어 넘기면 된다.
- 코드 비하인드는 `MainWindow.xaml.cs` 의 뷰모델 생성과 `OnClosing`(마스터 정리)만 남았다. 조그 누름/뗌(`bh:Jog.StartCommand/StopCommand`), DataGrid 체크박스 한 번 클릭·행 더블클릭(`bh:DataGridBehaviors.SingleClickCheckBox/DoubleClickCommand`), 로그 자동 스크롤(`bh:ListBoxBehaviors.AutoScrollToEnd`), Loaded 자동 연결(`bh:WindowBehaviors.LoadedCommand`)은 `Infrastructure/Behaviors.cs` 의 attached property 로 XAML 에서 연결한다 (`xmlns:bh="clr-namespace:EcatMaster.Infrastructure.Behaviors"`). 추가 패키지(Microsoft.Xaml.Behaviors) 없음.
- 기존 앱에 끼워 넣을 때는 `MainWindow` 를 통째로 쓰지 않고 `MainViewModel` + 필요한 창만 가져다 자기 화면에 붙일 수 있다. 데이터 파일은 `%LOCALAPPDATA%\EcatMaster\{settings,devices}.json` (`UserSettings`, `DeviceConfigFile`) 이며 경로 상수만 바꾸면 된다.
- 자동 연결(`MainViewModel.AutoConnectAsync`)은 창 `Loaded` 에서 부른다. 자동화 테스트에서는 `ECATMASTER_NO_AUTOCONNECT=1`.

## 다음 작업 후보

- 실기 연결 후 `GET /api/system/status` 의 `maxJitterUs`, `wkcErrors` 확인 → 사이클 1 ms 로 조정
- 드라이브별 원점복귀 방식(6098h), 소프트리밋, 토크 제한 등 초기화 SDO 를 `AzdDriveConfigurator` 에 추가
- `SerialCommandBridge.Handle` 을 실제 상대 장비 프로토콜로 교체
- 기존 WPF 앱을 이 API 의 클라이언트로 전환(HttpClient + OpenAPI 생성 클라이언트)
