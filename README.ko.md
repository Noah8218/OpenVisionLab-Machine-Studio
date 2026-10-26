# OpenVisionLab Machine Studio

**가상 장비를 구성하고 제어 Sequence를 작성해, 실제 하드웨어 시운전 전에 장비 동작을 확인합니다.**

제어·머신비전 엔지니어를 위한 Windows 데스크톱 앱입니다.
컨베이어·축·센서·실린더·가상 카메라를 배치하고 신호와 Sequence를 정의한 뒤,
같은 프로젝트에서 동작·인터록·획득·검사 결과를 확인합니다.

[![CI](https://github.com/Noah8218/OpenVisionLab-Machine-Studio/actions/workflows/fault-scenario-headless-smoke.yml/badge.svg)](https://github.com/Noah8218/OpenVisionLab-Machine-Studio/actions/workflows/fault-scenario-headless-smoke.yml)
![Windows](https://img.shields.io/badge/platform-Windows%2010%2F11-0078D4) ![.NET](https://img.shields.io/badge/.NET-8-512BD4) [![License](https://img.shields.io/badge/license-MIT-green)](LICENSE)

[English](README.md) | 한국어

![장비 장면과 런타임 진단을 함께 보여 주는 Simulation](docs/images/simulation.png)

[Equipment setup](#equipment-setup) · [Process and actions](#process-and-actions) · [Capture and inspection](#capture-and-inspection) · [Simulation](#simulation) · [Experiments and data](#experiments-and-data) · [Result analysis](#result-analysis)

## 3단계로 시작하기

1. `https://github.com/Noah8218/OpenVisionLab-Machine-Studio.git`를 clone하고 저장소 폴더로 이동합니다.
2. [Machine Studio를 빌드하고 실행합니다](#소스에서-빌드).
3. 시작 화면에서 **Automatic Transfer Cell**을 열고 편집 전에 복사본을 저장합니다. [예제 작업 순서](docs/USER_GUIDE.md#start-with-an-example)에 따라 장치·Sequence 대상·획득 설정을 확인한 뒤 Simulation을 실행합니다.

**Apply**는 작업 중인 프로젝트의 해당 설정이나 Step을 갱신합니다.
**Save**는 프로젝트 파일을 저장합니다. Run 시작은 별도 동작입니다.
프로젝트 열기, 장치 선택, 결과 탐색만으로 장비가 실행되지는 않습니다.

## 지원 작업

- 장비·장치·DI/DO 연결을 구성합니다.
- 대상·대기 조건·적용한 Step 설정을 명시해 Sequence를 작성합니다.
- 테스트 영상을 획득하고 시뮬레이션·고장·시나리오 검증·기록을 확인합니다.
- 클라우드 계정 없이 기본 로컬 시뮬레이션을 실행합니다.

## Equipment setup

![장비 계층·장면·설정 패널](docs/images/equipment-setup.png)

Station과 Unit을 만들고 장치를 추가해 위치와 연결을 설정합니다.
장비 트리와 장면은 선택을 공유합니다.
상단·사선 뷰는 같은 배치를 보여 주며, 사선 뷰는 CAD나 접촉 물리 모델이 아닌 장비 개략도입니다.

## Process and actions

![Sequence 개요와 Step 설정](docs/images/process-and-actions.png)

Sequence의 Step·분기·연결을 확인하고 각 동작의 대상을 선택합니다.
Step 초안은 Apply해야 하며, 티칭 위치를 보는 것만으로 축이 움직이지는 않습니다.
지원 Step 종류와 선형 Sequence 편집 제한은 [사용자 안내](docs/USER_GUIDE.md#process-and-actions)를 확인합니다.

## Capture and inspection

![획득 영상과 카메라 설정](docs/images/capture-and-inspection.png)

가상 카메라를 선택하고 입력을 설정한 뒤 명시적으로 영상을 획득합니다.
준비 정보는 선택한 Recipe와 현재 획득에 사용된 Recipe·이미지 ID·결과를 구분합니다.
[사용자 안내](docs/USER_GUIDE.md#capture-and-inspection)는 지원 입력과 외부 검사 연결 조건, 정해진 규칙으로 제어 흐름을 테스트하는 내장 간이 판정을 설명합니다.

## Simulation

![장비 장면과 런타임 진단](docs/images/simulation.png)

작성한 장비를 시작·일시정지·재개·Step 실행·초기화하고 런타임 상태를 확인합니다.
고정 5 ms Tick은 시뮬레이션 시간이며 하드 실시간 스케줄링 보장이 아닙니다.
장비를 편집하려면 Run을 종료해야 하며, 일시정지는 현재 Run을 유지하고 수정한 정의를 적용하지 않습니다.

## Experiments and data

![카메라 변화 설정과 적용 설정 요약](docs/images/experiments-and-data.png)

카메라 변화 파라미터를 편집하고 획득 설정을 명시적으로 Apply합니다.
시나리오와 반복 횟수를 선택해 검증을 실행하고 상태를 확인합니다.
정확한 런타임 대상을 골라 고장을 활성화·해제하며, 고장은 현재 세션에만 속하고 Recipe 설정에 저장되지 않습니다.

## Result analysis

![획득 기록과 비교 컨트롤](docs/images/result-analysis.png)

저장된 Run을 열거나 같은 Recipe의 두 기록을 비교하고 비교할 획득 항목을 직접 선택합니다.
기록의 카메라에서 현재 설정을 연 뒤 돌아올 수 있으며, 누락된 Step 식별자를 이름이나 순서로 추정하지 않습니다.
기록 열기나 설정 복원은 Run·영상 획득·검사 결과 적용을 실행하지 않습니다.

작업 화면의 세부 동작, 샘플 프로젝트, 고장 종류와 화면 이미지 조건은 [영문 사용자 안내](docs/USER_GUIDE.md)를 확인합니다.

## OpenVisionLab 연동

연결 설정을 통해 호환되는 외부 검사 애플리케이션을 사용할 수 있습니다.
[2D Workbench](https://github.com/Noah8218/OpenVisionLab),
[3D Studio](https://github.com/Noah8218/OpenVisionLab-3D-Studio),
[Labeling Studio](https://github.com/Noah8218/OpenVisionLab-Labeling-Studio)의 직접 작업 흐름 연동은 개발 중입니다.

## 소스에서 빌드

Windows 10 이상, .NET 8 SDK와 Git이 필요합니다.
Visual Studio에서는 `OpenVisionLab.MachineStudio.sln`을 열고
시작 프로젝트를 `OpenVisionLab.MachineStudio`로 지정합니다.

저장소 루트에서 실행합니다.

```powershell
dotnet build OpenVisionLab.MachineStudio.sln -c Release
dotnet run --project src/OpenVisionLab.MachineStudio/OpenVisionLab.MachineStudio.csproj -c Release --no-build
```

고정된 연동 패키지는 로컬 NuGet 피드에 포함됩니다.
버전과 라이선스는 [의존성 안내](third_party/OpenVisionLabIntegrationContracts/README.md)를 확인합니다.
소스 빌드 성공이 임의의 설치된 검사 애플리케이션과의 호환성을 뜻하지는 않습니다.

## 문서

[사용자 안내(영문)](docs/USER_GUIDE.md) · [아키텍처](docs/ARCHITECTURE.md) · [반도체 장비 예제](samples/SemiconductorRecipes/README.md)

현재 버전: `v0.2.0-dev.81` · [변경 이력](CHANGELOG.md)

## 개발 상태

하드웨어 없이 시뮬레이션하는 개발 중인 앱입니다.
생산 장비 제어나 안전 기능에 사용하도록 검증되지 않았습니다.

장비 구성·Sequence 편집·가상 획득·시뮬레이션·시나리오 검사·Run 분석을 제공합니다.
전체 데스크톱 조작·테마와 배율 조합·재시작 동작·운영 성능은 검증 중입니다.
평가할 때는 중요한 프로젝트의 복사본을 사용합니다.

실제 PLC·로봇·카메라 제어, 안전 제어, MES/클라우드 운영, CAD 작성,
보정된 물리 시뮬레이션과 운영용 AI 검사는 현재 지원 범위 밖입니다.
높이 데이터는 수치형 테스트 데이터이며, 보정된 3D 측정 시스템의 증거가 아닙니다.
이미지 세트 생성·검토·보관은 완성된 작업 흐름으로 안내하지 않습니다.

## OpenVisionLab 프로젝트

| 프로젝트 | 용도 |
| --- | --- |
| [2D Workbench](https://github.com/Noah8218/OpenVisionLab) | 규칙 기반 2D 검사 Recipe |
| [3D Studio](https://github.com/Noah8218/OpenVisionLab-3D-Studio) | 3D 티칭, Thickness와 Warpage |
| [Labeling Studio](https://github.com/Noah8218/OpenVisionLab-Labeling-Studio) | 라벨링·학습·모델 비교 |
| [Machine Studio](https://github.com/Noah8218/OpenVisionLab-Machine-Studio) | 하드웨어 준비 전 머신 시뮬레이션 |
| [Vision SDK](https://github.com/Noah8218/OpenVisionLab-Vision-SDK) | 공통 C# 검사 라이브러리 |

## 기여와 라이선스

소스 소유권과 빌드 안내는 [아키텍처](docs/ARCHITECTURE.md)를,
변경 지침은 [Contributing](CONTRIBUTING.md)을 확인합니다.
UI 문제를 보고할 때 버전·재현 단계·레이아웃과 배율을 적고, 공유할 프로젝트에서 고객 데이터와 자격 증명을 제거합니다.

[지원](SUPPORT.md) · [보안 제보](SECURITY.md) · [행동 강령](CODE_OF_CONDUCT.md)

[MIT License](LICENSE)를 적용합니다. 서드파티 고지는 [NOTICE](NOTICE),
[서드파티 안내](THIRD-PARTY-NOTICES.md), [자산 출처](ASSET-PROVENANCE.json)를 확인합니다.
특정 공급업체에 종속되지 않으며 장비 제조사와 제휴하지 않습니다.
