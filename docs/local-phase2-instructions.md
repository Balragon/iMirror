# 2차 작업 로컬(Windows) 지시서

> 상태: 이 지시서의 작업은 2026-10-27~28에 모두 끝났다. 결과는 `docs/code-review-2026-10-26.md` 6장 참고.

2026-10-26 전체 코드 리뷰(`docs/code-review-2026-10-26.md`) 2차 항목 중 **GPU·오디오 장치·실기 송신이 있어야 재현되는 것**을 Windows PC에서 처리하기 위한 지시서다. 로컬 Claude Code 세션에 이 파일을 그대로 읽히거나, 사람이 따라 해도 된다.

클라우드에서 이미 올린 PR과 이 지시서의 관계:

| PR | 내용 | 로컬에서 할 일 |
|---|---|---|
| #52~#59 | 1차(보안·크래시) 8건 | 병합 후 §2 스모크 1회 |
| #60 | 디코더 콜백 identity 검사, `FfmpegDecoder.Dispose` 중 재시작 금지 | §3-A 실기 확인 |
| #61 | 오디오 시작 backoff, 타이밍 포트 바인드 가드 | §3-B 로그 확인 |
| #62 | 셧다운 중 세션 종료 처리, `Window_Loaded` 가드, 다운로드 timeout | §3-C 확인 |
| (없음) | WASAPI 락 데드락 | **§4-A 로컬에서 구현** |
| (없음) | MF `IMFSample` 수명 | **§4-B 로컬에서 구현** |
| (없음) | MF 오버플로 GOP 정책 | **§4-C 로컬에서 구현** |

## 1. 준비

- Windows 10/11 x64, .NET 10 SDK, Gyan FFmpeg Essentials(`winget install Gyan.FFmpeg.Essentials`), 하드웨어 H.264 디코드가 되는 GPU, 같은 LAN의 iPhone 또는 Mac.
- `git clone https://github.com/Balragon/iMirror.git && cd iMirror`
- 모든 작업은 `main`에서 브랜치를 따서 하고, 항목마다 PR 하나. 브랜치 이름은 `local/<항목>` 형식.
- 빌드·테스트·실행:
  ```powershell
  dotnet test iMirror.sln -c Release
  dotnet build iMirror.sln -c Release
  .\bin\Release\net10.0-windows\iMirror.exe
  ```
- 로그: `%LOCALAPPDATA%\iMirror\Logs\iMirror.log`. 확인할 때마다 `Get-Content -Wait`로 열어 두면 편하다.
- PR 본문에는 Windows에서 본 **로그 발췌**와 (영상 항목은) `LatencyAcceptanceReport` 결과를 붙인다. CI는 빌드·단위 테스트만 본다.

## 2. 1차 PR 병합 후 스모크 (한 번)

#52~#59를 `main`에 병합한 뒤 `main`으로 다음을 한 번 돈다.

1. 앱 실행 → iPhone/Mac에서 미러링 → 영상·음성 나오는지 → 연결 해제 → UI 대기 상태 복귀.
2. 다른 PC에서(또는 WSL에서) 아래를 실행해도 미러링이 **끊기지 않아야** 한다.
   ```
   nc <iMirror PC IP> 7100        # 로그: "rejected: it is not the host that performed SETUP"
   printf 'POST /feedback RTSP/1.0\r\nContent-Length: 2000000000\r\n\r\n' | nc <IP> 7000
                                   # 로그: "exceeds 1,048,576 bytes; closing connection"
   ```
3. 설정에서 방화벽 규칙을 지운 뒤 앱의 "방화벽 허용" 버튼 → UAC 창 → 규칙 생성 확인(`Get-NetFirewallRule -DisplayName 'iMirror AirPlay Receiver'`).
4. 로그에 `Unhandled`/`Dispatcher exception`이 없어야 한다.

## 3. 클라우드 PR 실기 확인

### 3-A. #60 디코더 수명
- **재SETUP 중 좀비 ffmpeg**: 설정에서 비디오 엔진을 Software로 두고 미러링 시작 → 첫 영상이 나오기 전에(키프레임 대기 중) iPhone에서 미러링을 껐다 켠다. 이 과정을 3~4번 반복한 뒤 작업 관리자에서 `ffmpeg.exe`가 **0개**여야 한다. 로그에 "Decoder restarted" 뒤에 "Ignoring"이나 1초 멈춤 흔적이 없어야 한다.
- **GPU fault 1회 폴백**: GPU 엔진으로 미러링 중 `dxcap -forcetdr`(Windows SDK) 또는 그래픽 드라이버 재시작(Win+Ctrl+Shift+B)을 한다. 로그에 `HandleHighResolutionD3DFatal` 경로가 **한 번만** 찍히고("GPU decode faulted; restarting on FFmpeg software decoder." 1회), 영상이 FFmpeg로 이어져야 한다. "Ignoring fault from a superseded Media Foundation decoder"가 찍히면 identity 검사가 작동한 것이다.

### 3-B. #61 오디오 시작 backoff·타이밍 포트
- **backoff**: `tools\ffmpeg\bin\ffmpeg.exe`를 잠시 다른 이름으로 바꾸고 PATH에도 없게 한 뒤 미러링. 로그에 "Audio pipeline failed to start (attempt 1, next retry in 5s)", "(attempt 2, next retry in 10s)"… 가 **초당 수십 줄이 아니라 5·10·20초 간격**으로 찍혀야 한다. 영상은 계속 나와야 한다.
- **타이밍 포트**: 다른 프로그램으로 UDP 7102를 점유한다.
  ```powershell
  $u = New-Object System.Net.Sockets.UdpClient(7102)   # 열어 둔 채로
  ```
  미러링이 **정상 시작**되고 로그에 "AirPlay timing UDP port 7102 unavailable (AddressAlreadyInUse); continuing without timing requests."가 있어야 한다. 이전에는 SETUP에서 세션이 끝났다.

### 3-C. #62 호스트 수명
- **셧다운**: 미러링 중 트레이 메뉴로 종료. 로그에 "AirPlay mirror session ended during shutdown; skipping UI state reset."이 찍히고, "AirPlay probe dispose" 단계가 1초를 다 쓰지 않아야 한다(타임스탬프 차이로 확인).
- **Save & Restart**: 설정 → 저장·재시작을 5회 반복해도 매번 새 인스턴스가 뜨고 "exiting duplicate instance"가 없어야 한다.
- **다운로드 stall**: 설정의 업데이트 확인으로 다운로드를 시작한 직후 Wi-Fi를 끈다. 30초 안에 알림이 "Update could not be installed: The update download stalled…"로 바뀌고 Retry/Dismiss가 활성화되어야 한다.

## 4. 로컬에서 구현할 항목

세 항목 모두 **재현 → 수정 → 재현 안 됨 확인 → 단위 테스트 추가 → PR** 순서로 한다.

### 4-A. `WasapiAudioOutput` 락 안에서 `Stop()` 호출 → 데드락

**파일**: `MacMirrorReceiver.Audio/WasapiAudioOutput.cs`
`TryRestartOutputLocked`(약 200줄)와 `Dispose`(약 320줄)가 `_gate`를 쥔 채 `DisposeOutputLocked(stop: true)` → `output.Stop()`을 부른다. NAudio `WasapiOut.Stop()`은 재생 스레드를 `Join`한다. 재생 스레드는 장치 오류 시 `PlaybackStopped`를 **동기로** 올리고(출력 객체가 SynchronizationContext 없는 스레드풀에서 만들어지므로) `HandlePlaybackStopped` → `RequestOutputRestart` → `lock(_gate)`에서 기다린다. 양쪽이 서로를 기다린다. 이후 `MainWindow.StopAudioPipeline`(UI 스레드)이 `output.Dispose()`에서 멈춰 **비디오까지 얼어붙는다**.

**재현**
1. USB DAC 또는 블루투스 헤드셋을 기본 출력으로 두고 미러링(음성 나오는 상태).
2. 장치를 뽑는다(또는 블루투스 끔). 몇 번 반복.
3. 데드락이면: 음성이 멈추고 로그에 "WASAPI audio output restarted…"가 안 찍히며, 이후 연결 해제 시 UI가 멈춘다. 프로세스 덤프(`dotnet-dump collect`)에서 두 스레드가 `_gate`와 `playThread.Join`에 각각 서 있으면 확정.
   재현이 안 되면 `FakeWasapiOutputDevice`에 "Stop() 안에서 PlaybackStopped를 동기로 올리는" 동작을 넣어 단위 테스트로 먼저 재현한다(현재 테스트의 fake는 Stop이 즉시 반환한다).

**수정 방향**
- `_gate` 아래에서는 **참조 교체만** 한다. `DisposeOutputLocked`는 `_output`을 떼어내 반환하고, `Stop()`/`Dispose()`는 락 밖에서 호출한다(`TryRestartOutputLocked`, `Dispose` 둘 다).
- `HandlePlaybackStopped`는 블로킹하지 않는다: `Interlocked`로 `_restartRequested` 플래그와 사유만 기록하고 반환. 다음 `Submit`이 재시작을 수행한다(지금도 `Submit`이 `_restartRequested`를 본다).
- 이미 떼어낸 옛 장치에서 늦게 오는 `PlaybackStopped`는 `sender`가 현재 `_output`과 다르면 무시.
- 규칙 한 줄을 주석으로 남긴다: "`_gate`를 쥔 채 NAudio/COM 수명 메서드를 부르지 않는다."

**테스트**: `WasapiAudioOutputTests`에 (1) fake 장치의 `Stop()`이 동기로 `PlaybackStopped`를 올려도 `Submit` 경로가 끝나고 재시작이 1회인 테스트, (2) `Dispose` 중 같은 상황에서 반환되는 테스트. 기존 두 테스트는 그대로 통과해야 한다.

**완료 기준**: 장치 뽑기 5회 모두 음성이 2초 내 복구되고 UI 멈춤 없음. 로그에 "restarted after audio endpoint changed" 5회.

### 4-B. MF 출력 샘플을 텍스처만 남기고 해제 (plausible, 눈으로 확인 필요)

**파일**: `MacMirrorReceiver.Video/VorticeMediaFoundationD3D11Decoder.cs`(`DrainOutput` finally, 약 443~457줄; `TryCreateD3D11Frame`, 약 539~636줄), `MacMirrorReceiver.Video/VorticeD3D11VideoFrame.cs`
`TryCreateD3D11Frame`은 `ID3D11Texture2D` 참조 + 슬라이스 인덱스만 보관하고, `DrainOutput`의 finally가 `IMFSample`을 즉시 해제한다. MFT의 D3D11 샘플 할당자는 **샘플** refcount로 서피스를 재활용하므로, UI 스레드가 `_pendingD3DFrame`을 `VideoProcessorBlt`하기 전에 같은 슬라이스에 다음 프레임이 디코딩될 수 있다(찢김·미래 프레임, 오류 없음).

**재현(확인)**
1. GPU 엔진, 1440p60 또는 4K60 송신(Mac이 좋다). 화면에 빠르게 스크롤되는 내용을 띄운다.
2. UI 스레드에 부하를 준다: 진단 패널을 열어 두고 창 크기를 계속 바꾸거나, 설정 창을 열었다 닫는다.
3. 찢김/되감기 같은 프레임이 보이면 재현. `IMIRROR_DUMP_H264=1`로 덤프를 받아 두면 입력 스트림이 멀쩡한지 교차 확인 가능.
   눈에 안 띄더라도 수정은 안전하므로 진행한다(수정은 소유권을 명확히 하는 것뿐이다).

**수정 방향**
- `VorticeD3D11VideoFrame`에 `IntPtr Sample`(AddRef된 `IMFSample` 포인터)를 추가하고 `Dispose`에서 `Texture.Dispose()` 뒤 `Marshal.Release(Sample)`.
- `TryCreateD3D11Frame`에서 프레임을 만들 때 `Marshal.AddRef`(또는 `GetIUnknownForObject`)로 샘플 참조를 하나 더 잡아 프레임에 넘긴다. `DrainOutput`의 finally는 지금처럼 자기 참조를 해제하면 된다(프레임이 가진 참조가 남으므로 할당자가 재활용하지 않는다).
- `ReleasePendingD3DFrame`, 프레젠터의 drop-oldest 경로가 모두 `Dispose()`를 부르는지 확인(이미 그렇다).
- 주의: 샘플을 오래 들고 있으면 할당자 풀이 고갈되어 `MF_E_NOTACCEPTING`이 늘 수 있다. 단일 슬롯(`_pendingD3DFrame`) + 표시 직후 Dispose이므로 최대 2개만 잡히지만, 로그에서 "ProcessInput stall"이 늘지 않는지 본다.

**테스트**: GPU가 필요해 단위 테스트는 어렵다. `VorticeD3D11VideoFrame.Dispose`가 Sample 포인터를 정확히 한 번 Release하는지(이중 Dispose 포함)만 fake 포인터 카운터로 검증.

**완료 기준**: 2단계 부하에서 찢김 없음, 10분 세션 `LatencyAcceptanceReport`가 `corruption=pass`, `ProcessInput stall` 로그 횟수가 수정 전과 같거나 적음.

### 4-C. MF 경로가 오버플로 시 P-프레임을 개별 드롭

**파일**: `VorticeMediaFoundationD3D11Decoder.cs` `QueueH264`(약 228~290줄), `MacMirrorReceiver.Video/HighResolutionD3DInterfaces.cs`, `MacMirrorReceiver/MainWindow.cs`(`TryStartHighResolutionD3DPath`, `ProcessVideoPayload`)
큐가 차면(`MaxQueuedInputPackets=128` / 16 MiB) IDR이 아닌 패킷을 **하나씩** 버리고 큐는 그대로 둔다. `architecture.md`의 "do not randomly drop P-frames"와 반대이고, `FfmpegDecoder.QueueH264`(486~530줄)는 큐 전체를 flush하고 `InputQueueOverflowed`를 올려 게이트가 `RequireKeyframe()`을 한다. AirPlay는 IDR을 사실상 다시 보내지 않으므로 MF 경로는 다음 IDR까지 영구 매크로블록 깨짐. 드롭마다 `MainWindow.ProcessVideoPayload`가 "input unavailable" 로그도 쓴다(두 디코더의 `QueueH264` 반환 의미가 다름: MF는 드롭 시 false, FFmpeg는 true).

**재현**
- 4K60 송신을 느린 GPU로 받거나, 임시로 `MaxQueuedInputPackets`를 8로 낮춰 빌드한다. 30초쯤 지나면 로그에 "input unavailable; packet was not queued"가 초당 여러 줄 찍히고 화면이 깨진 채 회복되지 않는다.

**수정 방향**
- `IHighResolutionD3DDecoder`에 `event Action? InputQueueOverflowed;` 추가.
- MF `QueueH264`를 FFmpeg와 같은 정책으로: 오버플로 시 큐 전체 flush, 들어온 패킷이 IDR이면 그것부터 재개, 아니면 그것도 버리고 `InputQueueOverflowed` 발생. 반환값은 FFmpeg처럼 backpressure 드롭에서도 `true`(별도 카운터 `DroppedInputPackets`로 집계).
- `MainWindow.TryStartHighResolutionD3DPath`에서 구독: `decoder.InputQueueOverflowed += () => BeginInvoke(... if (!ReferenceEquals(_mediaFoundationD3DDecoder, decoder)) return; RequireH264Keyframe(); ...)` — #60에서 넣은 identity 검사 패턴을 그대로 따른다.
- 공통 로직(`ContainsIdrNal`, 큐/오버플로)은 두 디코더에 복붙되어 있다. 이번 PR에서는 정책만 맞추고, 공통화는 3차로 미룬다.

**테스트**: `VorticeMediaFoundationD3D11Decoder`는 생성에 D3D 장치가 필요하므로, 큐 정책을 작은 `H264InputQueue` 클래스로 빼서(두 디코더가 공유) 순수 단위 테스트를 둔다: (1) 비IDR 오버플로 → 전체 flush + 이벤트, (2) IDR 오버플로 → flush 후 IDR 적재, (3) 드롭 카운터. 빼기 어려우면 MF 디코더는 CI 없이 가고 FFmpeg 쪽 동작만 테스트로 고정한다.

**완료 기준**: 재현 조건에서 오버플로 뒤 "holding video until next keyframe" 1회 → 다음 IDR에서 깨끗하게 복구(아이폰은 재SETUP, Mac은 화면 전환으로 IDR 유도). "input unavailable" 스팸이 사라짐.

## 5. 보고

항목마다 PR을 만들고 본문에 다음을 적는다.
- 재현 절차와 수정 전 로그 발췌(2~5줄)
- 수정 후 같은 절차의 로그 발췌
- 영상 항목은 `LatencyAcceptanceReport` 요약 줄(`p95=`, `corruption=`)
- 단위 테스트 추가 내역

세 PR이 끝나면 2차는 완료다. 3차(테스트·구조)는 `docs/code-review-2026-10-26.md` 4절 13~16번을 본다.
