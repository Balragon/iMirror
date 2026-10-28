# iMirror 전체 코드 리뷰 (2026-10-26)

- 대상: `main` 기준 HEAD `cc8bb76` (`claude/stoic-bardeen-h0jxcj` 동일), `tools/`·`ThirdParty/` 제외 전체 소스 약 25,000줄
- 관점: 정확성·버그, 구조·유지보수성, 성능, 보안
- 방법: 영역별(네트워킹·비디오·오디오·앱 호스트·테스트/CI) 정독 후 상위 항목을 교차 검증. Linux 환경이라 빌드·실행은 하지 못했고, 모든 findings는 코드 읽기 기준이다. 실행 시점 동작(NAudio 내부, MF 할당자 등)에 의존하는 항목은 **plausible**로 표시했다.
- 줄 번호는 위 커밋 기준.

## 1. 요약

전반적으로 인터롭(COM/P-Invoke), 업데이터 무결성 검사, 키 자료 취급, 입력 큐 상한 등 어려운 부분은 신중하게 만들어져 있다. 반면 **네트워크 입력 파싱 경계**와 **수명(lifetime)·락 순서**에서 치명적인 구멍이 몇 개 있고, 바로 그 영역이 테스트가 전혀 없는 곳이다.

즉시 고쳐야 할 것(모두 confirmed):

| # | 심각도 | 위치 | 내용 |
|---|---|---|---|
| N1 | **Critical** | `AirPlayBinaryPlist.cs:124-198` | 순환 참조 bplist → 무한 재귀 → `StackOverflowException`으로 프로세스 즉사. LAN의 아무 호스트나 SETUP 한 번으로 가능 |
| N2 | High | `AirPlayProbeService.cs:1479-1495` | `Content-Length` 상한 없이 `new byte[n]` 할당 + 바디 대기 무한. 연결 몇 개로 OOM |
| N3 | High | `AirPlayProbeService.cs:581`, `AirPlayMirrorDecryptor.cs:51-84` | 복호화 실패 패킷마다 약 4,000개 후보 AES 전수 시도. 한 번 desync 나면 영상 정지 |
| N4 | High | `AirPlayProbeService.cs:500-503, 524` | 헤더는 8 MiB까지 허용하는데 버퍼는 1 MiB에서 잘라 큰 IDR을 영원히 조립 못 함 |
| V1 | High (plausible) | `VorticeMediaFoundationD3D11Decoder.cs:443-457` | 텍스처만 들고 `IMFSample`을 즉시 해제 → 할당자가 슬라이스를 재활용해 표시 전 프레임이 덮어써질 수 있음 |
| V2 | High | `FfmpegDecoder.cs:797-835, 952-986` | `Dispose` 중 read task가 `TryFallbackDecoder`로 새 ffmpeg.exe를 띄우고, 그 프로세스는 Kill되지 않음 + UI 1초 정지 |
| A1 | High (plausible) | `WasapiAudioOutput.cs:209, 321, 232` | `_gate` 잡은 채 `WasapiOut.Stop()`(=Join) 호출, 재생 스레드는 같은 락 대기 → 데드락. 이후 UI 스레드까지 멈춤 |
| A2 | High | `MainWindow.cs:1038-1041, 1200-1230` | 오디오 파이프라인 시작 실패 시 매 프레임(~100회/초) 재시도 + 스택트레이스 로그 |
| H1 | High (plausible) | `WindowsFirewallRuleInstaller.cs:81-100` | 사용자 쓰기 가능한 `%TEMP%` 스크립트를 UAC 상승 PowerShell로 실행, `powershell.exe`를 상대 이름으로 해석 |
| H2 | High (plausible) | `UpdateController.cs:213-217`, `iMirror.iss:35`, `App.cs:56-59` | 앱 뮤텍스를 쥔 채 `/SILENT /SUPPRESSMSGBOXES` 설치기 실행 → Inno `AppMutex` 검사에서 조용히 취소될 수 있음. 앱은 이미 종료 |
| C1 | Medium | `release.yml:79,118,154` | `${{ github.ref_name }}`을 pwsh 스크립트에 직접 보간. 태그 이름으로 명령 주입 가능 |

## 2. 영역별 findings

심각도: critical > high > medium > low. 괄호는 confidence.

### 2.1 네트워킹·프로토콜 (`MacMirrorReceiver.Networking`, `.Protocol`)

위협 모델: `pw=false`로 광고하고 실질적인 인증이 없으므로 "LAN의 아무 호스트"가 TCP 7000/5000/7100, UDP 5353에 도달하면 공격자다.

1. **[critical, confirmed] bplist 순환 참조 → 스택 오버플로.** `AirPlayBinaryPlist.cs:148`에서 `cache[index] = value`가 자식들을 재귀적으로 읽은 **뒤**에 기록된다. 객체 0이 자기 자신을 참조하는 배열이면 무한 재귀. `.NET`은 `StackOverflowException`을 잡을 수 없어 WPF 프로세스 전체가 죽는다. `objectCount > 4096` 검사는 2객체 사이클을 막지 못한다. 호출 경로: `AirPlayProbeService.cs:1502`(`ObserveSetupRequest`), `AirPlaySetupContext.cs:33`. **수정:** 하강 전에 cache에 sentinel을 넣거나 방문 집합/깊이 제한을 두고 사이클을 거부.
2. **[high, confirmed] Content-Length 무제한.** `AirPlayProbeService.cs:1479-1495`에서 `int.TryParse` 후 그대로 `new byte[contentLength]`. `client.ReceiveTimeout`(375줄)은 `NetworkStream.ReadAsync`에 효력이 없고 idle timeout·동시 연결 상한이 없다. `Content-Length: 2000000000`을 보내고 쉬는 연결 20개면 OOM. **수정:** 상한(1 MiB면 충분), `CancellationTokenSource.CancelAfter`로 연결별 read timeout, 동시 연결 수 제한.
3. **[high, confirmed logic / plausible trigger] 복호화 후보 전수 탐색이 실패 패킷마다 반복.** `TrySelectMirrorDecryptor`(768-851줄)가 `_mirrorDecryptor`가 이미 선택된 뒤에도 `TryNormalizeClearH264Payload` 실패 시마다 실행된다. 후보 약 800개 × 오프셋 변형 64개 = 약 4,000번 `DecryptOnce`, 매번 `Aes` 새로 생성, 수신 스레드에서 동기 실행. NAL 12(filler) 같은 whitelist 밖 패킷 하나로 유발 가능. **수정:** 세션/streamConnectionID당 한 번만 sweep, `_mirrorDecryptor` 설정 후에는 재실행 금지.
4. **[high, confirmed] 페이로드 상한과 버퍼 상한 불일치.** 524줄은 8 MiB까지 허용, 500-503줄은 `pending`이 1 MiB를 넘으면 마지막 128 KiB만 남긴다. 1 MiB 넘는 IDR(고비트레이트 1440p)은 영원히 조립 불가, 이후 `EmitMirrorPackets`가 바이트 단위 `RemoveAt(0)` + `Take(128).ToArray()`로 O(n²) 재동기화하며 가짜 헤더에 걸릴 확률이 높다(위치당 약 1/512). 재SETUP 전까지 복구 불가. **수정:** 버퍼 상한 ≥ 128 + 8 MiB, 재동기화는 다음 그럴듯한 헤더를 스캔.
5. **[medium, confirmed] 예외 필터가 좁아 조용히 끊김.** 420줄은 `IOException|SocketException|OperationCanceledException|ObjectDisposedException`만 잡는다. bplist 파싱의 `IndexOutOfRange`/`Overflow`(`offsetSize=0`, `checked((int)offsetTableOffset)` 66줄), `AirPlaySetupContext.ReadInt`의 `checked` 오버플로(157줄), `VideoPayloadReceived` 구독자 예외 등은 fire-and-forget `Task.Run`(346줄)으로 빠져나가 로그 없이 연결이 사라지고 `_activeDataClient`가 정리되지 않는다. **수정:** `HandleProbeClientAsync`에 catch-all + 로그, plist 파싱은 `TryRead`.
6. **[medium, confirmed] 7100 포트 세션 탈취.** 436-456줄: 아무 TCP 연결이나 `_activeDataGeneration`을 올리고 기존 송신자 소켓을 `Close()`한다. `nc <receiver> 7100` 한 줄로 미러링 중단. **수정:** 마지막 SETUP을 수행한 주소만 허용하거나 최소한 유효한 128바이트 헤더를 받은 뒤 supersede.
7. **[medium, confirmed] mDNS 과응답.** `IsProbeName`(2361-2366줄)이 `.local`로 끝나는 모든 이름에 true, 매 응답마다 `NetworkInterface.GetAllNetworkInterfaces()`(느린 Win32 호출)를 수신 스레드에서 실행. Chromecast/Apple 기기 많은 LAN에서 증폭 + 수신 루프 정체. **수정:** `_airplay._tcp.local`/`_raop._tcp.local`/자기 호스트명만 응답, RFC 6762 §6 rate-limit, 인터페이스 목록 캐시.
8. **[medium, confirmed] 비ASCII 표시 이름 깨짐.** `AirPlayBinaryPlist.cs:227-230`이 모든 문자열을 `0x50`+ASCII로 쓴다. "거실 TV"는 mDNS에는 맞게 나가지만 `GET /info`에는 `?? TV`. **수정:** 비ASCII면 `0x60` + BE UTF-16.
9. **[medium, confirmed] 핫패스 복사.** `List<byte>.Add` 바이트별 수신(483-486줄), `Take/Skip/ToArray` 요소별 복사, `TryConvertAvccPayloadAtOffset`(1077-1101줄)의 바이트별 `Add`, 복호화에서 2-3회 전체 복사. 프레임당 약 6회 전체 패스 + LOH 할당. 헤더는 바이트당 `await`(1439-1442줄). **수정:** `ArrayPool` 링버퍼 또는 `System.IO.Pipelines`, Span 기반 AVCC→AnnexB 단일 출력, in-place CTR XOR.
10. **[medium, confirmed] 오디오 복호화 패킷당 `CreateDecryptor` + `Key` 복사.** `AirPlayAudioDecryptor.cs:51-68`. **수정:** `Aes.DecryptCbc` one-shot API.
11. **[low, confirmed] pair-verify 결과 미강제.** `AirPlayPairingContext.cs:129-157`은 `verified`를 로그만 하고 세션을 유지한다. `pw=false` 레거시로서 일관되지만, 로그 문구가 강제를 암시한다. 둘째 호스트의 `pair-verify` POST가 활성 세션의 shared secret을 교체(119줄). 암호 사용 자체는 문제 없음(BouncyCastle X25519/Ed25519, `SecureRandom`).
12. **[low, confirmed] UDP 오디오 주입.** `AirPlayAudioReceiver.cs:219-278`: 소스 주소·SSRC·RTP version 검사 없음, `Start(IPAddress? remoteAddress)`의 인자가 사용되지 않음(71줄). CC/X/P 비트 무시. 쓰레기가 AAC-ELD 디코더(libavcodec)에 들어간다.
13. **[low] 리소스.** `AirPlayMirrorDecryptor.cs:23-28` `Aes` 미해제, `AirPlayAudioReceiver._aesKey/_eiv` 쓰기만 함, `AirPlayTimingClient`는 NTP 요청 40개를 보내고 응답을 읽지 않음(동기화 미구현).
14. **[low, confirmed] 7102 바인드 실패가 SETUP을 죽임.** `AirPlayTimingClient.cs:36` 고정 포트 바인드에 try/catch 없음 → `AirPlaySetupContext.cs:43-46` → 420줄 필터에서 `SocketException`으로 잡혀 SETUP 응답 없이 세션 종료. 다른 프로그램이 UDP 7102를 쥐고 있으면 모든 미러 연결 실패.
15. **[low, confirmed] FairPlay S-box를 런타임에 `.c/.h` 소스에서 정규식으로 파싱**(`AirPlayPlayFair.cs:647-697`). 파일 없거나 수정되면 복호화 전체가 조용히 꺼지고 `Lazy`가 실패를 캐시. 포팅 자체는 `omg_hax.c` 등과 대조해 일치. **수정:** 빌드 시 `static readonly` 테이블 생성 또는 임베디드 리소스.

검증 후 제외(방어 존재): `TryBuildSpsPpsPayload` 경계, AVCC NAL 길이 검사(1085줄), 치수 클램프(1028줄), `MirrorClient` 페이로드 상한·`checked`, CSeq 반사, mDNS 이름 포인터 루프 가드, 복호화기 use-after-dispose(모두 `_mirrorKeyGate` 아래), `DecryptAesKey` 범위.

### 2.2 비디오 파이프라인 (`MacMirrorReceiver.Video`, MainWindow 구동부)

1. **[high, plausible] MF 출력 샘플을 텍스처만 남기고 즉시 해제.** `VorticeMediaFoundationD3D11Decoder.cs:443-457`. MFT의 D3D11 샘플 할당자는 *샘플* refcount로 서피스를 재활용한다. `_pendingD3DFrame`이 UI 스레드에서 `VideoProcessorBlt`되기 전에 같은 슬라이스가 다음 프레임으로 덮일 수 있다(찢김/미래 프레임, 오류 없음). **수정:** `VorticeD3D11VideoFrame`이 `IMFSample`을 보유하고 `Dispose`에서 해제.
2. **[high, confirmed] `FfmpegDecoder.Dispose` 중 대체 ffmpeg.exe 생성·방치.** `Dispose`(952-986줄)가 Kill → read task는 `EndOfStream` → `catch` → `TryFallbackDecoder`(797-835줄). 이 메서드는 `_cts` 취소를 확인하지 않고, 첫 프레임 전이면 새 프로세스를 띄우고 `DecoderRestarted`를 올린다. 핸들러(`MainWindow.cs:2460-2468`)는 `Dispatcher.Invoke`인데 UI 스레드는 `_readTask.Wait(1s)` 중 → 1초 타임아웃 후 `_process?.Dispose()`만 호출(Kill 없음) → ffmpeg.exe 잔류. 키프레임 대기 중 재SETUP마다 재현. **수정:** `TryFallbackDecoder` 진입 시 취소 확인, `Dispose`에서 `_processGate` 잡고 join 후 최종 `_process` Kill, `DecoderRestarted`는 `BeginInvoke`.
3. **[medium, confirmed] 디코더 콜백에 세대 검사 없음.** `MainWindow.cs:2536-2557`의 `Faulted`/`StatusChanged`/`FrameDecoded`는 어떤 디코더가 올렸는지 비교하지 않고 현재 필드에 작동한다(`_client.StatusChanged`는 `ReferenceEquals` 검사함, 1353줄). 디코더 A의 12초 타임아웃 fault와 새 stream config가 겹치면 새 GPU 디코더 B를 폐기하고 세션 내 GPU를 끈다. 같은 device-lost에서 `PresentD3DFrame`(3501-3509줄)이 동기로 `HandleHighResolutionD3DFatal`을 부르고 큐된 핸들러가 또 부르면 FFmpeg 재시작이 **두 번**. **수정:** 람다에 `decoder` 캡처 후 `ReferenceEquals(_mediaFoundationD3DDecoder, decoder)` 아니면 return.
4. **[medium, confirmed] MF 경로가 P-프레임을 개별 드롭.** `VorticeMediaFoundationD3D11Decoder.cs:244-258`. 큐가 차면 IDR 아닌 패킷을 하나씩 버리고 키프레임 재요청도 없다. `architecture.md`의 "do not randomly drop P-frames"와 반대이고 `FfmpegDecoder`는 GOP 단위 flush + `InputQueueOverflowed`로 `RequireKeyframe()`을 한다. AirPlay는 IDR을 사실상 다시 보내지 않으므로 4K60을 55fps로 겨우 디코딩하는 GPU에서는 영구 매크로블록 깨짐. 드롭마다 AppLog도 씀(`MainWindow.cs:1505-1509`, 두 디코더의 `QueueH264` 반환 의미가 달라 생기는 스팸).
5. **[low/medium, confirmed] CWD 조상 디렉터리에서 ffmpeg.exe 탐색.** `FfmpegDecoder.cs:855-911` `FindFfmpegInAncestorTools(Environment.CurrentDirectory)` + WinGet 패키지 폴더 재귀 스캔. 공유 폴더나 "시작 위치"가 조작된 바로가기로 실행되면 심어둔 바이너리가 복호화된 화면을 받는다. **수정:** `AppContext.BaseDirectory` + PATH만, 나머지는 Debug 전용.
6. **[low, confirmed] MF 출력 프레임의 `ReceivedTick`이 현재 입력 패킷 시각.** 422, 630줄. `MF_E_NOTACCEPTING` 뒤 여러 프레임이 최신 패킷 시각으로 찍혀 200ms stale-drop(`MainWindow.cs:3485`)과 지연 창이 백로그를 과소 보고.
7. **[low]** `_inputSignal.Release()` after Dispose 레이스(230, 278-281, 1157줄; FFmpeg도 동일), `OnRenderSizeChanged`가 DIP로 `MoveWindow`(`SwapChainVideoPresenter.cs:299-312`, 중복이라 삭제 가능), 프레임마다 `GetBuffer`/`Create*View` COM 객체 생성(122-144, 156-167줄), 게이트의 패킷당 `string.Join` 문자열 생성(`H264AnnexBStreamGate.cs:167, 171`), `VideoFrame.Buffer.Length ≠ 프레임 크기`(ArrayPool), non-`ProvidesSamples` 분기 샘플 누수(372-376줄, 실제론 도달 어려움).
8. **[구조]** `VorticeD3D11VideoProcessorD3DImagePresenter`는 제품 코드에서 미사용(tools만 참조), `_disposed` 가드도 없음 → tools로 이동 또는 삭제. `HIGH_RESOLUTION_D3D`는 항상 정의되어 `#else` 분기가 컴파일된 적이 없고 `HighResolutionPipelineProbe.cs:344-398`이 인터롭을 중복 선언 → 상수 폐기. `IHighResolutionD3DFrame`이 텍스처를 노출하지 않아 모든 소비자가 다운캐스트.

검증된 긍정: `MediaFoundationNative.cs`의 vtable 순서·구조체 레이아웃·`[PreserveSig]`·대칭 Release가 정확. `H264AnnexBStreamGate` NAL 스캔은 임의 입력에 안전하고 테스트됨. 치수 [2, 8192] 클램프 + `checked` 프레임 크기. `ID3D11Multithread` 보호, 단일 슬롯 drop-oldest + CAS, 큐 개수·바이트 이중 상한, `LatencyWindow` 락 건전.

### 2.3 오디오 파이프라인 (`MacMirrorReceiver.Audio`, `FfmpegAudioDecoder`, `AirPlayAudioReceiver`)

전제 정정: 오디오 디코더는 네이티브 인터롭이 아니라 `ffmpeg.exe`를 띄워 fMP4를 stdin으로 넣고 s16le를 stdout에서 읽는다. `unsafe` 코드 없음.

1. **[high, plausible] `_gate` ↔ NAudio 재생 스레드 데드락.** `WasapiAudioOutput.cs:209`(`TryRestartOutputLocked`), `:321`(`Dispose`)이 `_gate` 아래서 `output.Stop()`(= `playThread.Join()`). 출력 객체는 SynchronizationContext 없는 스레드풀에서 만들어지므로 `PlaybackStopped`가 재생 스레드에서 동기 발생 → `HandlePlaybackStopped` → `RequestOutputRestart` → `lock(_gate)`(232줄). USB DAC 뽑힘 시 양쪽이 영원히 대기 → 이후 `StopAudioPipeline`(UI 스레드)이 `output.Dispose()`에서 멈춰 **오디오 장애가 비디오 세션을 얼린다**(정책 위반). **수정:** 락 아래서 NAudio/COM 수명 메서드를 호출하지 말 것(참조만 교체하고 밖에서 Stop), `HandlePlaybackStopped`는 non-blocking 플래그.
2. **[high, confirmed] 시작 실패 핫루프.** `MainWindow.cs:1038-1041`이 `_audioDecoder == null`이면 매 오디오 프레임마다 `StartAudioPipelineLocked`. RDP/헤드리스(기본 렌더 장치 없음)나 ffmpeg 없음이면 세션 내내 약 100회/초 `MMDeviceEnumerator`+`WasapiOut` 생성 + 프로세스 spawn 시도 + 전체 스택트레이스 로그, `_audioGate`도 그만큼 점유. **수정:** 실패 backoff 또는 세션 내 비활성 플래그.
3. **[medium, confirmed] 타이밍 포트 바인드 실패가 SETUP을 죽임.** 2.1-14와 동일(`AirPlayTimingClient.cs:36`, `AirPlaySetupContext.cs:43-46`). 타이밍은 best-effort이므로 try/catch + 로그.
4. **[medium, confirmed] 입력 오버플로 flush가 ffmpeg 안의 프레임 타이밍을 버림.** `FfmpegAudioDecoder.cs:142-151`에서 *미기록* 큐 오버플로 시 `_writtenTiming`을 clear. 이미 stdin에 쓴 프레임의 PCM이 나중 패킷의 타이밍을 1:1로 가져가 영구 어긋남 → `EstimateLatencyMilliseconds`(`WasapiAudioOutput.cs:275`) 과소 보고 → sync-drop이 안 걸려 지연이 조용히 증가. **수정:** 프로세스 재시작 때만 clear.
5. **[medium, confirmed] RTP seq/timestamp 미사용.** `FfmpegAudioDecoder.cs:268-270`은 도착 순서대로 쓰고 `_nextDecodeTime`을 패킷당 `spf`씩 증가. `SourceTimestampNanos`/`RtpTimestamp`는 아무도 읽지 않음. 재정렬 패킷은 순서 틀리게 디코딩(AAC-ELD는 overlap-add라 잡음), 손실은 타임라인 압축으로 오디오가 앞서감. 재전송 요청 없음. 중복 제거(130-140줄)만 있음. 4096 창 vs 65536 공간이라 wraparound는 안전.
6. **[medium, confirmed logic] 싱크 목표 포화.** `MainWindow.cs:94-96`이 120..220ms로 클램프, `WasapiAudioOutput.cs:17/154`가 260ms 이상 버퍼를 드롭. FFmpeg/WPF 폴백에서 비디오 지연이 250-400ms면 오디오를 더 늦출 방법이 없어 영구적으로 앞선다. env 오프셋도 음수 불가(`ReceiverSettings.cs:300`, 60으로 클램프).
7. **[medium, confirmed] 비인증 RTP 주입.** 2.1-12 참조.
8. **[low]** `StopAudioPipelineLocked`가 `_audioGate` 아래서 `decoder.Dispose()`(1초 대기) → read task가 `HandleAudioPcmFrame`에서 같은 락 대기 중이면 UI 1초 정지(`MainWindow.cs:1247, 1275`). `ArrayPool` 이중 반환 잠재(`FfmpegAudioDecoder.cs:333-350` + `MainWindow.cs:1263`). ffmpeg 오디오 프로세스 사망이 세션 내 영구(비디오 디코더엔 재시작 있음). `IMIRROR_AUDIO_SYNC_OFFSET_MS` 파싱 실패·범위 초과가 무로그. 생성자에서 올리는 `StatusChanged`는 구독 전이라 미전달(`WasapiAudioOutput.cs:76`). `_buffer.ClearBuffer()`(140-146줄) 도달 불가.

검증된 긍정: 오디오 실패 격리가 대체로 실제로 작동(위 락 두 건과 SETUP 경로 제외). 재시작 500ms rate-limit + 가짜 장치 테스트. fMP4/ESDS/ELD-ASC 구성 정확(AOT 39 escape, `tfhd` default-base-is-moof, `trun` data-offset 재계산). RAOP "whole blocks CBC, tail clear" + 패킷당 IV 리셋 정확.

### 2.4 앱 호스트 (`MacMirrorReceiver/*`, 업데이터, 설정, 설치기)

1. **[high, plausible] 방화벽 스크립트: %TEMP% + 상대 이름 `powershell.exe`.** `WindowsFirewallRuleInstaller.cs:81-100`. 스크립트는 UAC 동의 **후** 상승 프로세스가 읽으므로 동일 사용자 권한의 비상승 코드가 동의 창 동안 GUID 이름 파일을 교체할 수 있다(디렉터리 열거 가능). `UseShellExecute=true`에서 bare `powershell.exe`는 CWD(`{localappdata}\Programs\iMirror`, iss:20,48)를 PATH보다 먼저 본다. **수정:** `-EncodedCommand`로 파일 없이 전달, `Environment.SystemDirectory` 절대 경로. 인수 인용·이스케이프 자체는 올바르고 테스트됨.
2. **[high, plausible] 업데이트 핸드오프가 `AppMutex`에서 취소될 수 있음.** `UpdateController.cs:213-217`은 설치기를 띄운 **다음** `ShutdownApplicationAsync`를 기다린다. `iMirror.iss:35` `AppMutex=Local\iMirror.App`, `UpdateLauncher.cs:9` `/SILENT /SUPPRESSMSGBOXES`. Inno는 `InitializeSetup`에서 뮤텍스를 검사하고 `/SUPPRESSMSGBOXES`면 "실행 중" 대화상자가 Cancel로 기본 처리되어 Setup이 종료한다. 뮤텍스는 `App.OnExit`(`App.cs:56-59`)에서야 해제되는데 그 앞에 ffmpeg kill·2초 대기·probe 1초·mDNS 2초가 있다. 결과: 앱은 꺼지고 업데이트는 안 됨, 로그는 "installer started". `CloseApplications=yes`는 그 뒤 단계(Restart Manager)라 무관. `v04-updater-design.md` 테스트 항목 6에 해당하지만 실행 기록 없음. **수정:** `_launchInstaller` 전에 뮤텍스 해제(또는 `AppMutex` 제거하고 `CloseApplications`에 의존), 혹은 `App.OnExit`에서 설치기 실행.
3. **[medium, confirmed] 다운로드 stall timeout·크기 상한 없음, 진행 중 Dismiss 불가.** `UpdateService.cs:110-118`(`ResponseHeadersRead`라 100초 기본 타임아웃은 헤더만), `CopyWithProgressAsync`(354-378줄) read별 데드라인·`expectedBytes` 초과 중단 없음. `MainWindow.cs:2132-2138`, `SettingsWindow.xaml.cs:392-394`가 버튼 둘 다 비활성. Wi-Fi 끊기면 "43%"에서 영원히.
4. **[medium, confirmed] 셧다운 중 probe `WaitAll` 1초를 항상 소진.** `ShutdownApplicationAsync`(`MainWindow.cs:731-757`)가 UI 스레드에서 `_airPlayProbe.Dispose()` → `Task.WaitAll(1s)`(`AirPlayProbeService.cs:2566-2572`). 네트워크 태스크는 종료하며 `MirrorSessionEnded` → `Dispatcher.Invoke(Send)`(870-882줄)를 부르는데 UI가 막혀 있어 매번 타임아웃. "Save & Restart"(2111-2121줄, 새 프로세스를 **먼저** 띄움)에서 ffmpeg 2초 + probe 1초 + mDNS 2초가 #50의 5초 `PreviousInstanceExitTimeout`을 넘길 수 있다. **수정:** 세션 이벤트는 `BeginInvoke`, 또는 교체 프로세스는 `App.OnExit`에서.
5. **[medium, plausible] `Window_Loaded`가 try/catch 없는 `async void`.** `MainWindow.cs:397-407`, `App.cs:62-65`는 로그만 하고 `Handled`를 안 세움. `NetworkInterface.GetAllNetworkInterfaces()`(`StartupDiagnostics.cs:150`)가 던지면 시작 즉시 종료. 다른 `async void` 핸들러 14개는 모두 내부에서 잡는다.
6. **[medium, confirmed] UI 스레드 동기 DNS.** `MainWindow.cs:2064` `Dns.GetHostAddresses` → `GetHostAddressesAsync`.
7. **[medium, confirmed] AppLog 무회전 + 줄마다 open/append/close.** `AppLog.cs:18-36`, 전역 락. present 경로(`LogRenderLatencyThrottled`, 1초마다)와 제어 요청마다 기록. 시간당 약 2 MB, 무한 증가, AV 스캔 중인 디스크면 수신·디코더·UI 스레드가 락에서 대기. **수정:** 크기 기반 회전(5 MB × 3) + 백그라운드 writer.
8. **[low]** SHA256SUMS URL은 HTTPS 강제 없음(`UpdateService.cs:332-345`, 다만 api.github.com TLS 경유라 심층 방어 수준). 설정 파일 비원자적 쓰기(`ReceiverSettings.cs:176`, 크래시 시 기본값으로 조용히 복귀). `DisconnectAsync`가 `await` 사이에 AirPlay 세션 시작 핸들러와 재진입(3174-3216줄). 메인 창과 설정 창에서 같은 다운로드 동시 시작 가능(`File.Delete` on `FileShare.None` → 가짜 "Retry"). 디코더 재시작마다 `settings.json` 2회 재읽기(2488-2495줄). `_shutdownCts`, `_audioRtpWatchdogCts`, `_disconnectGate` 미해제.
9. **[low, confirmed] 죽은 코드 약 250줄.** `SyntheticCursorOverlayEnabled=false`(109줄)로 커서 오버레이 전체(1657-1679, 2829-2859, 2905-3048줄 + XAML 70-80줄) 도달 불가. `RenderMode.Responsive/Native4K`는 생성되지 않는 상태. `WindowLifecycleState.ShouldHideOnClose/ConsumeFirstHideNotification` 미호출(`Window_Closing`은 항상 종료) → `WindowLifecycleStateTests`는 앱에 없는 동작을 테스트.

검증된 긍정: 다운로드 소스 상수 고정, setup URL HTTPS 강제, .NET이 HTTPS→HTTP 리다이렉트 거부, SHA-256 fail-closed + 크기 검사 선행 + 부분 파일 삭제, 파일명 sanitize, 설치기 인수 상수, semver 비교(빌드 메타데이터 포함). verify→execute TOCTOU는 `PrivilegesRequired=lowest` + 사용자별 설치라 권한 경계를 넘지 않음. 로그에 키 자료 없음(길이·마커만). #51 셧다운 취소와 #49 컨트롤러 분리는 건전하고 23개 테스트.

### 2.5 테스트·CI·릴리스·위생

1. **[medium, confirmed] 태그 이름 명령 주입.** `release.yml:79, 118, 154` `"${{ github.ref_name }}"`을 pwsh에 직접 보간. `v1.0.0$(…)`는 유효한 태그이면서 `v*.*.*`에 매치 → 태그 push 권한 = `contents: write` 잡에서 코드 실행. **수정:** `env:`로 넘기고 `$env:TAG`.
2. **[medium] 액션 미고정.** `actions/*@v4`, `softprops/action-gh-release@v2`(third-party)가 SHA가 아닌 floating major. 업데이터가 신뢰하는 SHA256SUMS가 같은 잡에서 생성되므로 빌드 단계 오염을 막지 못함. `choco install innosetup`, `dotnet tool install CycloneDX`도 미고정(FFmpeg는 URL+SHA-256 고정, 잘 되어 있음). `ci.yml`, `sbom.yml`에 최상위 `permissions:` 없음.
3. **[medium, confirmed] 테스트가 실제 네트워크·실제 설정 파일에 의존.** `AirPlayProbeServiceTests.cs:23-38`은 0.0.0.0:7000/5000/7100-7102 바인드 + 멀티캐스트 가입 + 실제 mDNS 송출. 포트 점유(iTunes/Bonjour)나 멀티캐스트 불가 NIC에서 실패. `AirPlayProbeService.cs:59-66` 정적 초기화가 `%LOCALAPPDATA%\iMirror\Config`의 개발자 실제 설정을 읽고, 실제 로그 파일에 쓴다.
4. **[medium, confirmed] `.gitignore`가 덤프 확장자를 안 막음.** `IMIRROR_DUMP_AUDIO`/`IMIRROR_DUMP_H264`는 임의 경로를 받고, 오디오 덤프 `.meta.json`에 `aesKeyHex`, `sharedSecretHex` 등이 들어간다(`AirPlayAudioReceiver.cs:395-431`). `.gitignore`는 `*.h264 *.bgra *.log Diagnostics/`만. README:125/SECURITY:76의 "git에서 무시된다"는 기본 경로에서만 참.
5. **[medium] 라이선스.** 벤더링된 playfair 소스에 저작권 헤더·출처 URL·커밋이 없고 THIRD_PARTY_NOTICES도 밝히지 않음(GPLv3 §4/§5; `#define printf(...) (void)0;`는 로컬 수정으로 보이는데 표시 없음). 배포 패키지에 iMirror 자체 `LICENSE`/`NOTICE`가 복사되지 않음(`csproj:55-65`, `publish-win-x64.ps1:245-262`). FFmpeg 처리(별도 프로세스, essentials 빌드 단언, LICENSE 동봉, 소스 제공 고지)는 올바름.
6. **[medium] 문서 부패.** `docs/release.md:12-13`은 "설치기·자동 업데이트 없음", ".NET 8 SDK"; `CONTRIBUTING.md:44,57,64`는 net8·잘못된 테스트 프로젝트 경로; `AssemblyInfo.cs:13` 주석 net8; README:27은 playfair를 "검증용 참고"라 하지만 실제로는 런타임 필수. `IMIRROR_GPU_BINDING`(docs) 읽는 코드 없음.
7. **[low]** README 미문서 env var: `IMIRROR_AUDIO_ENABLED`, `IMIRROR_AUDIO_SYNC_OFFSET_MS`, `IMIRROR_D3D_COLOR_SPACE`, `IMIRROR_FORCE_SOFTWARE`, `IMIRROR_PREFER_HARDWARE`, `IMIRROR_HR_V2_PROBE`. csproj 기본 `Version` 0.2.0 vs 릴리스 0.7.5 → 비태그 빌드는 항상 "업데이트 있음". `UpdateServiceTests` 임시 디렉터리 미삭제. `WindowsFirewallRuleInstallerTests`는 생성기 문자열 미러링이라 의미 오류를 못 잡음.
8. **빈 catch 51개 / 로그만 17개**(production 121개 중). 대부분 teardown이지만 `FfmpegDecoder.cs:325/340/466/753`은 프로세스 시작·재시작 경로라 소프트웨어 폴백이 왜 실패했는지 숨긴다. `AppLog.cs:32`는 로거 자체가 삼킴.

**커버리지 판단: 테스트 없는 집합이 정확히 위험한 집합이다.** SECURITY.md:56-58이 "네트워크 입력 파싱"과 "FairPlay/암호"를 주 공격면으로 명시하지만 `AirPlayBinaryPlist`, `AirPlayPairingContext`, `AirPlayFairPlayContext`, `AirPlayPlayFair`, `AirPlayMirrorDecryptor`, `AirPlayAudioDecryptor`, `AirPlayAudioReceiver`, `MdnsBrowser`, `Protocol/*`, `LatencyWindow`, `FfmpegAudioDecoder`는 테스트 0. 잘 테스트된 곳(업데이터, H.264 게이트, 설정 정규화)은 상대적으로 순수·저위험이다. GPU 프레젠터는 CI에서 불가능하지만 위 목록은 바이트 배열 위의 순수 함수라 픽스처 테스트가 가능하다.

CI 긍정: 테스트가 실제로 돌고(`ci.yml:33`, sln 포함), publish 스크립트 스모크로 .NET 9+ WPF 어셈블리 누락 회귀를 잡음, 태그 기반 버전 주입 정확, NuGet 전부 고정 버전, 큰 바이너리 없음(최대 483 KB 테이블), 의존성 라이선스 전부 GPLv3 호환.

## 3. 구조 평가

- **`AirPlayProbeService`(2,599줄)**: mDNS 응답기 / RTSP 연결·요청 코덱 / 메서드 라우터 / 미러 세션·키 상태기계 / 데이터 스트림 프레이머+H.264 정규화기 다섯 책임. (5)와 (1) 대부분은 static이거나 이벤트 싱크만 필요해 무동작변경으로 먼저 뺄 수 있다. 죽은 코드: `EmitAnnexBPayloads`, `EmitAvccPayloads`, `CollectionsMarshalAsSpan`(span이 아니고 할당함), `BuildInfoPlist`/`BuildSetupPlist`/`PlistString`/`EscapeXml`(XML plist 경로), `AirPlayFairPlayContext.HasKeyMessage`. `CopyOf`가 5곳, plist 정수 리더가 3곳(오버플로 의미 다름 → 2.1-5). `MirrorClient.cs`에 디컴파일러 흔적(`_ = 1;`, `num`, `array2`).
- **`MainWindow.cs`(3,786줄)**: 아홉 책임이 섞여 있고, 그 비용이 "클라이언트 콜백은 identity 검사, 디코더 콜백은 안 함"(2.2-3)으로 직접 드러난다. 저위험 추출 순서: `PendingVideoBuffer`(1611-1655줄, 순수) → `LegacyConnectionManager` → `VideoEngineHost`(디코더 identity 캡처로 2.2-3 해결) → `FramePresenter` → `AudioSessionHost`(2.3-1/2/8 해결) → `FullscreenChrome` → `ReceiverStatusModel`(스냅샷 테스트) → `TrayIconHost`/`ShutdownCoordinator`(뮤텍스를 설치기 실행 전에 해제, 2.4-2/4 해결). 그 전에 2.4-9 죽은 코드 250줄 삭제.
- **두 FFmpeg 디코더**: 입력 큐+세마포어+오버플로 정책+메트릭+덤프+`ContainsIdrNal`+`Dispose` 순서가 약 60% 복붙이며 의미가 미묘하게 다르다(2.2-4, 2.2-6). `PipedFfmpegProcess` 베이스 + `H264InputQueue`로 약 400줄 제거 가능하고 비디오의 재시작 로직을 오디오가 재사용할 수 있다.
- **A/V 싱크는 타임스탬프가 아니라 지연 매칭**이다. RTP timestamp, `SourceTimestampNanos`, NTP 타이밍 클라이언트가 전부 연결만 되고 버려진다. 삭제하거나 설계를 끝내거나 둘 중 하나.
- **복호화 후보 brute force**는 연구 하네스가 제품에 남은 모양이다. 오디오 주석은 실기기에서 파생식을 확인했다고 하니 비디오도 같다면 확정 파생식만 남기고 sweep은 진단 플래그 뒤로.
- **예외 정책 불일치**: probe는 좁게 필터, `MdnsBrowser`는 전부 무로그 삼킴, 오디오 수신기는 패킷마다 로그. 루프별 "log-and-continue / log-and-close" 한 가지 규칙이면 silent-drop 부류가 보이게 된다.
- **락 규율 한 줄**: `_gate`/`_audioGate`/`_processGate`를 쥔 채 NAudio·COM·프로세스 수명 메서드를 부르지 않는다. 2.3-1, 2.3-2, 2.3-8, 2.2-2가 모두 여기서 나온다.

## 4. 권장 순서

**1차(보안·크래시, 각각 작은 PR):**
1. bplist 사이클 가드 + 깊이 제한, `TryRead`로 감싸기 (2.1-1, 2.1-5)
2. `Content-Length` 상한 + 연결별 read timeout + 동시 연결 상한 (2.1-2)
3. 복호화 sweep 세션당 1회 (2.1-3), 버퍼 상한 ≥ 8 MiB (2.1-4)
4. 7100 데이터 연결을 SETUP 주소로 제한 (2.1-6), RTP 소스 주소·SSRC 검사 (2.1-12)
5. 방화벽 스크립트 `-EncodedCommand` + 절대 경로 PowerShell (2.4-1)
6. 설치기 실행 전 뮤텍스 해제 (2.4-2); 실제 Windows에서 v04 테스트 항목 6 수행
7. `release.yml` 태그를 `env:`로, 액션 SHA 고정, `permissions:` 추가 (2.5-1/2)
8. `.gitignore`에 `Dumps/ *.rtp *.control *.aacframes *.meta.json` (2.5-4)

**2차(수명·락):**
9. 디코더 콜백 identity 검사 (2.2-3), `TryFallbackDecoder` 취소 확인 + `Dispose` 최종 Kill (2.2-2)
10. `WasapiAudioOutput` 락 밖에서 Stop (2.3-1), 오디오 시작 실패 backoff (2.3-2), 타이밍 포트 try/catch (2.3-3)
11. MF 프레임이 `IMFSample` 보유 (2.2-1), MF 오버플로를 GOP 단위 + 키프레임 재요청으로 (2.2-4)
12. 세션 이벤트 `BeginInvoke`, `Window_Loaded` try/catch, 다운로드 idle timeout (2.4-3/4/5)

**3차(테스트·구조):**
13. 픽스처 테스트: `AirPlayBinaryPlist`(사이클·잘림·오버플로 포함), `MirrorHeader`, 두 복호화기 known-vector, `LatencyWindow`, fMP4 빌더, `HandleDataPacket`
14. `AirPlayProbeServiceTests`를 포트 0·설정 주입으로 격리
15. 죽은 코드 삭제 → `PendingVideoBuffer`/`VideoEngineHost`/`AudioSessionHost` 추출 → 두 FFmpeg 디코더 공통화
16. AppLog 회전, 문서 동기화(release.md, CONTRIBUTING, README env var), playfair 출처 기록, LICENSE/NOTICE 패키징

## 5. 검토 한계

- 빌드·테스트 실행 불가(Linux, dotnet 없음). 모든 줄 번호와 경로는 읽기로 확인했지만 실행 시 동작에 의존하는 항목(2.2-1, 2.3-1, 2.4-1, 2.4-2, 2.4-5)은 Windows에서 재현이 필요하다.
- `ThirdParty/playfair/*.c`는 포팅 일치 여부만 대조했고 자체 리뷰는 하지 않았다.
- `tools/*` 프로브 프로그램은 범위에서 제외했다.

## 6. 진행 결과 (2026-10-28 기준)

리뷰 로드맵 1~16번은 모두 처리했고 v0.8.0(2026-10-27), v0.8.1(2026-10-28)로 배포했다. 각 PR 본문과 댓글에 실기 확인 결과가 있다.

| 단계 | 주요 PR |
|---|---|
| 1차 보안·크래시 (N1~N4, H1, H2, C1, 덤프 gitignore) | #52~#59 |
| 2차 수명 주기·오디오·MF (V1, V2, A1, A2, 세션 이벤트 등) | #60~#63, #66, #67, #74, #76~#78 |
| 3차 테스트·구조 (픽스처 테스트, 테스트 격리, 죽은 코드, AppLog 회전, 문서·라이선스) | #68~#73, #84, #93, #94 |
| 리뷰 후속 (세션 소유권, 방화벽 감지·차단 규칙, mDNS 응답 범위, playfair 임베드, NTP 제거, 오디오 타이밍) | #79~#83, #85~#88 |
| 구조 분리: `MainWindow` (3,786 → 약 2,500줄) | #89~#92 |
| 구조 분리: 두 FFmpeg 디코더 공통화 | #97 |
| 구조 분리: `AirPlayProbeService` (2,823 → 1,076줄) | #101, #108 |

**하지 않기로 한 것**

- 2.1-9 수신 핫패스 복사 제거: 데이터가 압축된 H.264라 프레임당 1~6 KB이고, 측정한 수신→표시 지연이 p50 1~2 ms라 얻을 것이 없다. 버퍼 재사용은 프레임 손상 위험만 늘린다.
- MF 디코더 입력 큐를 `PipeInputQueue`로 통합: 주 경로(GPU)를 건드리는 정리일 뿐 사용자에게 보이는 차이가 없다. GPU 디코더를 다른 이유로 고칠 때 함께 할 수 있다.
- 오디오 싱크 슬라이더 하한 60 → 120 (#100, 닫음): GPU 엔진에서는 120 미만이 같은 목표로 수렴하지만, 소프트웨어 엔진은 영상 지연이 14~53 ms라 낮은 값도 효과가 있다. 60~220을 유지한다.

**남은 것**

- #3 장시간 A/V 싱크: 20분·11분 연속 세션은 지연 관문을 통과했지만 2시간 소크는 하지 않았다.
- 재연결 직후 지연이 높게 굳거나 잠깐 끊기는 현상은 송신 측 원인으로 확인했다. Mac의 '화면 미러링' 메뉴(실시간 미리보기)를 열어 두면 Mac이 영상·오디오를 불규칙하게 보낸다. 수신 측은 고치지 않기로 했다.
