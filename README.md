# AutoAudioAdjust

YouTube Music을 들을 때 곡마다 다른 체감 음량을 줄이기 위해 만든 Windows 앱입니다. 선택한 앱의 오디오 세션 음량을 감지해 목표 레벨에 맞춰 부드럽게 조절합니다.

## 사용법

1. [Releases](https://github.com/tjdgns0618/AutoAudioAdjust/releases)에서 `AutoAudioAdjust.App.exe`를 내려받습니다.
2. Windows에서 YouTube Music을 재생하고 `AutoAudioAdjust.App.exe`를 실행합니다.
3. 앱 목록에서 Chrome, Edge 또는 YouTube Music PWA 세션을 선택합니다.
4. 목표 음량과 최소·최대 범위를 설정하고 **자동 보정 시작**을 누릅니다.

Windows x64용 포터블 실행 파일이며 .NET 런타임을 따로 설치할 필요가 없습니다. 앱이 실행 중일 때만 보정하며, Chrome 또는 Edge가 여러 탭의 소리를 하나의 세션으로 묶으면 브라우저 전체 세션의 음량이 조절될 수 있습니다. YouTube Music PWA나 음악 전용 브라우저 창을 권장합니다.

## 동작 방식

Windows Audio Session API를 사용해 선택한 앱의 재생 레벨을 읽고 앱별 볼륨을 조정합니다. 순간적인 소리 변화는 평활화하고, 갑자기 커진 소리는 빠르게 낮추며, 작은 소리는 천천히 높입니다. 무음 구간에는 보정하지 않고 설정한 볼륨 범위를 지킵니다. 오디오를 녹음하거나 서버로 전송하지 않습니다.

## 소스에서 빌드

Windows와 .NET 9 SDK가 필요합니다.

```powershell
dotnet build AutoAudioAdjust.sln -c Release
dotnet run --project AutoAudioAdjust.App
```

포터블 배포본을 만들려면 프로젝트 루트에서 다음을 실행합니다.

```powershell
./build-release.ps1
```

이 프로젝트는 Windows 전용 WPF 앱이며, YouTube Music 자체의 음원 데이터는 분석하거나 변경하지 않습니다.
