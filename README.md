# OpenVisionLab Machine Studio

OpenVisionLab Machine Studio는 실제 장비를 연결하기 전에 머신 레이아웃과
자동 동작을 구성하고, 같은 동작을 데스크톱에서 반복해서 확인하기 위한
Windows 프로그램입니다.

가상 축과 I/O를 배치하고 시퀀스를 작성한 뒤 실행·일시정지·단계 실행·초기화와
드라이런으로 상태와 결과를 확인할 수 있습니다.

## 현재 코드 기준

- 버전: `v0.2.0-dev.23`
- 머신 레이아웃, 장치 상태, 센서, 실린더, 컨베이어, 워크피스 모델을 구성할 수 있습니다.
- 자동 시퀀스를 작성하고 5 ms 고정 간격 시뮬레이션으로 실행할 수 있습니다.
- 실행 중 일시정지, 단계 실행, 초기화, 오류 주입, 상태·이벤트 확인을 지원합니다.
- 시뮬레이션 준비 확인, 연결된 단계 미리보기, 레시피 드라이런, 실행 경계 재생을 지원합니다.
- `.ovmachine` 프로젝트와 프로젝트에 연결된 실행 근거를 저장하고 다시 열 수 있습니다.
- 내장 예제 레시피를 수정해 장비 연결, 인터록, I/O, 축 동작, 분기 흐름을 확인할 수 있습니다.
- 화면 언어를 한국어와 영어로 전환할 수 있습니다.

## 버전

현재 버전: `v0.2.0-dev.23`

이 프로젝트는 명시적 버전으로 관리됩니다.

### 최근 버전 기록

#### `v0.2.0-dev.23` (2026-09-22)

- 외부 Result/HeightMap 연동의 런타임 상태, 진단, 운영자 설정과 명령 흐름을 확장했습니다.
- 프로젝트·시뮬레이션 실행 상태의 저장, 복구, 검증 경계를 제품 source와 focused tests에 반영했습니다.

#### `v0.2.0-dev.22` (2026-09-22)

- 외부 검사 결과 교환의 대기·검증·적용 상태를 Machine Studio 런타임과 화면에 연결했습니다.
- MMI 운영 화면에서 외부 검사 설정, 이미지 상태, 결과 적용 흐름을 확인할 수 있도록 했습니다.

#### `v0.2.0-dev.21` (2026-09-18)

- 공개 저장소 문서를 제품 사용과 기여 흐름에 맞게 정리했습니다.

#### `v0.2.0-dev.20` (2026-09-10)

- 공개 저장소의 폴더 구조를 정리했습니다.

#### `v0.2.0-dev.19` (2026-09-09)

- 리팩터링 변경을 공개 저장소에 반영했습니다.

## 기본 실행 흐름

1. 프로그램을 실행합니다.
2. **Start from sample**을 선택합니다.
3. **Connections**에서 장비 연결과 시퀀스 사용처를 확인합니다.
4. **Check simulation readiness**로 실행 전에 구성을 확인합니다.
5. **Dry run recipe**로 타임라인과 최종 상태를 확인합니다.
6. 타임라인 항목에서 **View state on layout**을 선택해 해당 시점의 레이아웃 상태를 봅니다.
7. 작업 화면에서 **Simulation ON**을 선택한 뒤 **Pause**, **Step**, **Reset**으로 실행을 살펴봅니다.
8. 내장 예제를 수정할 때는 **Save As**로 별도 프로젝트를 만듭니다.

프로젝트를 열거나 저장된 실행 근거를 확인하는 동작은 시뮬레이션을 자동으로
시작하거나 프로젝트를 자동 저장하지 않습니다.

## 내장 예제 레시피

내장 갤러리에는 다음 예제가 포함되어 있습니다.

- FOUP 로드 포트
- 카세트 매핑
- 웨이퍼 프리얼라이너
- OCR 검사 인계
- 로드락 진입
- 스핀 코트 이송
- 디벨로퍼 트랙 이송
- 드라이 에치 이송
- CMP 이송
- 계측 분류

각 예제는 장비 소유 관계, 인터록, I/O, 축 동작, 분기와 제어 흐름을
확인하기 위한 시작점입니다. 예제 레시피의 구성과 확인 범위는
[레시피 안내](samples/SemiconductorRecipes/README.md)에 정리되어 있습니다.

## 2D·3D 연동 진행

Machine Studio와 외부 2D·3D 프로그램 사이에서 이미지와 검사 결과를
주고받는 흐름을 연결하고 있습니다. 현재 **3D Exchange** 화면에서 교환
폴더와 피어 정보를 설정하고, 명시적인 Ping·Push·Pull과 결과 새로고침으로
핸드오프와 결과 교환을 확인하는 경로를 다듬고 있습니다.

프로젝트의 `.ovmachine` 파일과 실행 근거는 저장한 프로젝트 옆에 두며,
화면 언어 설정은 `%LOCALAPPDATA%\OpenVisionLab\MachineStudio\CONFIG`에
저장합니다. 기본 흐름은 로컬에서 동작하며 계정이나 클라우드 연결을
필요로 하지 않습니다.

## 소스에서 실행

필요 환경:

- Windows 10 이상
- .NET 8 SDK

저장소 루트에서 실행합니다.

```powershell
dotnet restore OpenVisionLab.MachineStudio.sln
dotnet build OpenVisionLab.MachineStudio.sln -c Release --no-restore
dotnet test OpenVisionLab.MachineStudio.sln -c Release --no-build --no-restore
dotnet run --project src/OpenVisionLab.MachineStudio/OpenVisionLab.MachineStudio.csproj
```

## 구조

시뮬레이션 스레드가 런타임 상태를 소유하고, UI 명령은 명령 큐를 통해
전달됩니다. UI는 불변 스냅샷과 순서가 보장된 이벤트를 읽습니다.
시뮬레이션 계산, 시퀀스, 장치 상태, 화면 표현은 각각의 모듈에서 관리합니다.

- [구조와 모듈](docs/ARCHITECTURE.md)
- [시뮬레이션 시간 모델](docs/SIMULATION_TIME_MODEL.md)
- [비전 연동 경계](docs/VISION_INTEGRATION.md)
- [외부 자산과 벤더 연동 정책](docs/VENDOR_INTEGRATION_AND_ASSET_POLICY.md)

## 참여와 문의

변경을 제안하기 전에 [CONTRIBUTING.md](CONTRIBUTING.md)를 확인해 주세요.
재현 가능한 문제는 이슈 템플릿을 사용하고, 보안 문제는
[SECURITY.md](SECURITY.md)의 안내를 따라 주세요.

- [지원 안내](SUPPORT.md)
- [커뮤니티 행동 규칙](CODE_OF_CONDUCT.md)

## 라이선스

Machine Studio는 [MIT License](LICENSE)로 제공됩니다. 의존성 고지는
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)에, 내장 시각 자산의
출처와 해시는 [ASSET-PROVENANCE.json](ASSET-PROVENANCE.json)에 기록되어
있습니다.
