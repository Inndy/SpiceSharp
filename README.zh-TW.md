# SpiceSharp

[English](README.md) | 繁體中文

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
![100% vibe-coded](https://img.shields.io/badge/100%25-vibe--coded-8b5cf6)

**讓 Proxmox 主控台，融入你的 Windows 桌面。**

SpiceSharp 是原生 Windows SPICE 用戶端。其中的 **SpiceViewer** 可直接開啟 Proxmox 的 `.vv` 連線檔，
在一般 Windows 視窗中操作虛擬機桌面。專案採用 C#、.NET 10 Native AOT、Win32 介面與 Direct3D 11 畫面呈現。

## 為什麼要做這個專案

打開虛擬機主控台，是為了把事情做完。它應該迅速連線、融入平常使用的桌面，讓快捷鍵送到正在操作的虛擬機，
也應該在畫面不動時停止消耗不必要的資源。

SpiceSharp 想探索：以 Windows 使用體驗為出發點，SPICE 用戶端還能做到什麼？我們重視的是：

- **原生桌面體驗。** 支援個別螢幕 DPI、深色標題列、視窗貼齊、全螢幕，以及 Alt+Tab、Windows 鍵等系統快捷鍵擷取。
- **效能與省電優先。** 畫面有變化才呈現，熱路徑重用緩衝區，音訊採事件驅動。在這之後，才依序追求低記憶體用量與小體積。
- **能獨立使用的檢視器。** 可直接開啟 `.vv`，也能透過獨立的 Proxmox 啟動器操作。每個 SPICE 主控台都有自己的檢視器程序，
  啟動器不介入畫面繪製。
- **可檢視的設計取捨。** 協定、傳輸、codec 與介面分層，設計決策、審查發現與驗證紀錄隨程式碼公開。
  LGPL codec 保留在使用者可替換的 DLL 中。

這些是工程優先順序，並非所有環境都能達到的效能保證。
[工程原則](docs/design/principles.md)與[驗證紀錄](docs/verification/README.md)記載了取捨與實際測量結果。

## 目前能做什麼

目前是早期 **0.1.0** 專案，以 Windows 可攜式 zip 發布。仍有待打磨之處，協定支援也尚未完整。
[設計規格](docs/design/spec.md)同時包含現行要求與未來工作，並非全部都已實作。

| 項目 | 支援內容 |
| --- | --- |
| 連線 | SPICE `.vv`、Proxmox spiceproxy 通道、CA 與憑證主體驗證 |
| 桌面操作 | 畫面、游標、鍵盤滑鼠、全螢幕、適應視窗／1:1 縮放、記住視窗狀態 |
| 客體整合 | 搭配相容的 SPICE guest agent，支援剪貼簿與隨視窗調整解析度 |
| 多媒體 | Opus／PCM 播放、MJPEG、軟體 H.264；裝置與 Windows codec 支援時可硬體解碼 H.264／VP9／HEVC |
| Proxmox 工具 | 獨立 WinUI 3 啟動器、終端機橋接、串接使用者自選 VNC 檢視器的橋接工具 |
| 診斷 | 日誌、可選狀態資訊疊圖、錄製流量重播測試、記憶體配置回歸測試 |

目前範圍不包含 USB 重新導向、智慧卡、麥克風輸入、檔案傳輸與共用資料夾。檢視器僅支援 Windows。
macOS 可參考 [SpiceSee](https://github.com/aaronpollock/SpiceSee)，它也是本專案重要的架構與 codec 參考來源。

## 開始使用

系統需求：**Windows 11**，搭配支援 **x86-64-v3** 的 **x64 CPU**（包含 AVX2、FMA、BMI1/2），或 **ARM64**。
Windows 10 22H2 為盡力支援。無法使用硬體 GPU 的環境（例如 RDP 工作階段）可使用 WARP 軟體繪圖，
但仍須符合 x64 CPU 要求。發行套件不需另外安裝 .NET 執行環境。

1. 若[發行頁面](https://github.com/Inndy/SpiceSharp/releases)已有套件，下載並解壓縮對應的
   `SpiceViewer-<version>-win-x64.zip` 或 `-win-arm64.zip`；也可以依下方說明自行建置。
2. 在 Proxmox 將虛擬機設為 SPICE 顯示，選擇 **Console → SPICE** 下載 `.vv` 連線檔。
3. 連線票證有效時間很短，下載後請立即開啟：

   ```powershell
   .\SpiceViewer.exe .\console.vv
   ```

可執行 `SpiceViewer.exe --register`，將目前這份程式設為此 Windows 帳戶的 `.vv` 開啟程式；
`--unregister` 可取消關聯。zip 也包含 `PveLauncher\PveLauncher.exe` 與 `PveTerm.exe`。
詳見[終端機文件](src/PveTerm/README.md)與[套件使用指南](tools/package/README.txt)。
設定與日誌通常存於執行檔旁的 `data\`，請讓解壓縮後的目錄保持可寫入。

| 預設快捷鍵 | 功能 |
| --- | --- |
| Ctrl+Alt+R | 將鍵盤與滑鼠控制權交回 Windows |
| Shift+F11 | 切換全螢幕 |
| Ctrl+Alt+Shift+F12 | 顯示／隱藏狀態資訊疊圖 |

`.vv` 可覆寫釋放控制權與全螢幕快捷鍵。在標題列按右鍵，可開啟包含傳送 Ctrl+Alt+Del、縮放與輸入擷取設定的視窗選單。

**請將 `.vv` 視為憑證。** 它可能包含仍有效的連線票證，請勿附在 issue 或提交至版本庫。
票證過期時，回到 Proxmox 重新下載；憑證不符時程式會拒絕連線，沒有略過驗證的按鈕。

## 公開揭露：100% vibe-coded

**這個專案是 100% vibe-coded。** 專案自身的程式碼由 AI 在人類指導下撰寫，主要由 Claude 實作、
Codex 審查並撰寫回歸測試。引入的第三方程式碼保留原作者與授權；目標、產品方向與架構決策則由人類負責。

我們仍需要證據確認成果：上游協定參考、去除敏感資料的流量重播、經核准的 golden images、記憶體配置測試、
獨立審查與真實客體驗證。[Agent 指南](AGENTS.md)記錄工作流程，[審查紀錄](docs/reviews/README.md)保留發現與實作者回應。
AI 審查與測試通過，無法證明每個協定邊界或惡意輸入都安全。歡迎回報問題，也歡迎人工審查。

## 建置與測試

使用 `global.json` 指定的 .NET 10 SDK。Windows Native AOT 發布另需 Visual Studio Build Tools 的 C++ 工具、
Windows SDK，以及 PowerShell 7。目前 codec 建置採用 MSYS2 CLANG64，需 Clang/LLD、CMake、Ninja、NASM、
GNU make 與下載工具。完整需求及 Linux 交叉編譯方式見 [native/README.md](native/README.md)。
相依套件以版本或原始碼壓縮檔雜湊固定。

在 MSYS2 CLANG64 shell 中，從專案根目錄建置 codec DLL：

```sh
bash native/build.sh x64
python native/verify.py x64
```

接著在 Windows 執行：

```powershell
dotnet build SpiceViewer.sln -c Release
dotnet test tests/Spice.Wire.Tests -c Release
pwsh tools/package.ps1
```

zip 與 SHA-256 檔案位於 `artifacts/`；同一份可執行內容也會放到 `out/win-x64/`，並保留其中的 `data\`。
ARM64 請先依 native 文件交叉編譯 DLL，再安裝對應的 MSVC ARM64 建置工具，執行
`pwsh tools/package.ps1 -Rid win-arm64`。

不開視窗的測試專案位於 `tests/`，請逐一執行：`Spice.Native.Tests`、`Spice.Wire.Tests`、`Spice.Replay.Tests`、
`Spice.Video.Tests`、`Spice.Diagnostics.Tests`、`Pve.Api.Tests`、`Pve.Term.Tests`。
Native、replay 與 video 測試需要 codec DLL。**`dotnet test SpiceViewer.sln` 也會執行開啟視窗的 viewer 測試**，
請在專用桌面工作階段執行；硬體解碼測試的涵蓋範圍取決於執行環境。

## GitLab CI

[`.gitlab-ci.yml`](.gitlab-ci.yml) 會建置 x64 codec、驗證原生 DLL、建置 solution、依序執行七個不開視窗的測試專案，
再產出 Native AOT 可攜式 zip。失敗時仍保留測試結果；成功時可從 job artifacts 下載 zip 與校驗碼。

需先備妥具有指定工具與 tags 的 Windows x64 runner，詳見 [CI 設定](docs/ci.md)。
只把 YAML 放進 GitHub 不會啟動 GitLab pipeline。GUI 測試、真實客體與 ARM64 執行驗證仍須另外進行。

## 參與貢獻

歡迎 issue 與 pull request。回報問題時請附上建置版本、Windows 版本、CPU 架構、客體 OS、重現步驟與去識別化的日誌片段。
請移除票證、token、位址、主機名稱與帳號資訊，勿在公開 issue 張貼憑證或私人客體錄影／流量。

先閱讀[文件索引](docs/README.md)、[規格](docs/design/spec.md)、[原則](docs/design/principles.md)與相關
[決策](docs/decisions/README.md)。變更請維持明確範圍，修正行為時附上回歸測試。
新增 replay fixture 前須用 `tools/spicerec/scrub.py` 清除敏感資料；golden image 變更須經維護者核准。

| 路徑 | 用途 |
| --- | --- |
| `src/VvFile`、`src/Spice.Wire`、`src/Spice.Transport` | 連線檔解析、協定與傳輸 |
| `src/Spice.Canvas`、`src/Spice.Native`、`src/Spice.Media`、`src/Spice.Session` | 繪圖、codec、多媒體與連線工作階段 |
| `src/SpiceViewer` | Win32 檢視器、畫面呈現與輸入 |
| `src/Pve.Api`、`src/PveLauncher`、`src/Pve.Term`、`src/PveTerm` | Proxmox API、啟動器與主控台橋接 |
| `native/`、`tests/`、`tools/`、`docs/` | 原生建置、回歸測試、工具與專案紀錄 |

## 授權與致謝

SpiceSharp 自身程式碼採 [MIT 授權](LICENSE)。SPICE QUIC／LZ／GLZ 與 FFmpeg 的 LGPL-only H.264 解碼器，
集中於可替換的 `spicecodec.dll`；其他隨附元件各有自己的授權。可選的 WinUI 啟動器也會散布適用各自條款的 Microsoft 執行元件。
[licenses/README.md](licenses/README.md) 列出元件對應授權，[native/VENDORED.md](native/VENDORED.md) 記錄上游來源與修改。
發行套件附上授權聲明、原生元件重建原始碼與腳本；固定版本的外部來源以雜湊驗證後下載。

感謝 [SpiceSee](https://github.com/aaronpollock/SpiceSee)、SPICE、QEMU、libjpeg-turbo、Opus、LZ4 與 FFmpeg。
這些專案的實作與參考資料，讓這個用戶端得以實現。
