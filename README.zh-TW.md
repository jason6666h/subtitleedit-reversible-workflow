# Subtitle Edit 可逆音訊工作流

[English](README.md)

這是一套基於 **Subtitle Edit 5.2.0** 的原始碼整合擴充，主要補上兩種工作流程：

1. **可逆的音訊剪修**：剪掉或修整音訊時，同步維持字幕時間、編輯歷史與還原資料的一致性。
2. **強調安全檢查的 AI 字幕校閱**：大量字幕可分批送交 AI 校閱，並在套用前檢查缺漏、重複、衝突、過期 Session 與詞彙規則。

它特別適合訪談、講座、Podcast、口述歷史、長篇課程等「音訊內容會持續修改，但字幕時間又不能亂掉」的工作。

## 主要功能

### 可逆音訊剪修

- 直接從 Subtitle Edit 波形區選取並剪除音訊。
- 剪除後自動重算後續字幕時間。
- 音訊與字幕一起進入 Undo / Redo。
- 可切換原始時間軸與修改後時間軸進行比對。
- 可快速跳到上一個／下一個變更位置巡聽。
- 保留不可覆寫的 revision 與 checkpoint，方便驗證與還原。
- 支援送到 Adobe Audition 修音，接回前會先驗證檔案狀態。
- 音訊剪修快捷鍵可自行設定、清除或停用。

### AI 字幕校閱

- 長字幕可自動切成多個批次。
- 每列字幕都有固定 ID，避免 AI 回覆對錯行。
- 檢查缺少 ID、未知 ID、重複 ID 與互相衝突的建議。
- 所有修改先預覽，再由使用者決定是否套用。
- 支援詞彙表、保護詞、參考資料與先前校閱文本。
- Session 可保存與續接，並用 fingerprint 檢查是否已經過期。
- AI 回覆表格同時支援繁體中文與英文格式。

## 目前支援版本

目前以以下環境開發與測試：

- **Subtitle Edit 5.2.0**
- 官方基線 commit：`d8e3b8b41e856a896c541ce7e59b490a99c21196`
- Windows x64
- .NET 10

這不是 Subtitle Edit 官方外掛，也不是官方發行版。

目前採用 **source integration**，原因是現有 Plugin API 還無法完整取得波形選區、替換目前音訊、音訊／字幕共同 Undo/Redo 等能力。

## 語言支援

- GitHub 文件：繁體中文＋英文
- AI 校閱 Prompt／回覆格式：繁體中文＋英文
- 主要操作介面：目前繁體中文最完整
- 英文 UI：持續整理中

中英文共用同一套程式碼，不會維護兩份不同版本。

## 快速開始

需要：

- Git
- PowerShell 7+
- .NET 10 SDK
- Windows x64

```powershell
git clone https://github.com/jason6666h/subtitleedit-reversible-workflow.git
cd subtitleedit-reversible-workflow

pwsh ./scripts/setup-upstream.ps1
pwsh ./scripts/test.ps1
pwsh ./scripts/build.ps1
```

### 三個腳本分別做什麼

`setup-upstream.ps1`

- 下載官方 Subtitle Edit 原始碼
- 固定到目前支援的 5.2.0 基線
- 套用 `integration/host-hooks.patch`

`test.ps1`

- 測試可逆音訊核心
- 測試 AI 校閱核心與 Session
- 執行相關的 Subtitle Edit UI 整合測試

`build.ps1`

- 建置 Windows x64 版本
- 輸出到 `artifacts/SubtitleEdit-win-x64`

本 repo **不重新散布** FFmpeg、libmpv 或 Subtitle Edit 官方 binary。因此自行 build 完成後，如果要做成可直接使用的完整 portable 版本，仍需自行準備相容的 runtime 元件並遵守各自授權。

## 專案結構

```text
integration/
  ReversibleAudioSync/   音訊剪修與 Subtitle Edit 整合
  SubtitleReview/        AI 字幕校閱 UI／整合
  host-hooks.patch       套到 Subtitle Edit 的最小 host 修改

src/
  AudioWorkflow.Core/    音訊時間軸、revision、驗證、輸出、外部修音往返
  SubtitleReview.Core/   校閱協定、解析、詞彙表、Evidence、Session 安全

tests/
  ...                    核心與 Subtitle Edit 整合回歸測試

scripts/
  setup-upstream.ps1
  test.ps1
  build.ps1
```

## 為什麼不是一般 Plugin

這些功能需要直接取得 Subtitle Edit 的內部狀態，例如：

- 波形區間選取
- 替換目前播放／編輯中的音訊
- 剪除音訊後同步重算字幕時間
- 音訊與字幕共同 Undo / Redo
- 外部修音往返時的播放器與波形狀態

目前一般 Plugin API 還沒有完整提供這些能力。

因此這個專案把 host 修改控制在一小份明確的 patch 裡，真正的功能邏輯則盡量留在獨立程式碼中，降低後續跟進 Subtitle Edit 新版本的成本。

## 測試

公開版包含以下類型的 regression tests：

- 音訊時間軸與剪除
- 音訊驗證與輸出
- revision／checkpoint
- Adobe Audition 往返
- 音訊＋字幕 Undo / Redo
- AI 回覆解析
- 缺漏、重複、衝突校閱結果
- Session 還原與過期保護
- Subtitle Edit UI 整合

詳細內容請看 [docs/TESTING.zh-TW.md](docs/TESTING.zh-TW.md)。

## 隱私

這個 repo 只應包含程式碼、通用測試文字與合成測試音。

請不要提交真實使用者影音、私人字幕、AI 校閱 Session、revision 資料、API key、Token 或含有個人路徑的紀錄。

詳見 [PRIVACY.zh-TW.md](PRIVACY.zh-TW.md)。

## 歡迎協作

目前特別適合協作的方向：

- 英文 UI localization
- 更多 regression tests
- 繼續縮小 Subtitle Edit host patch
- 跟進後續穩定版 Subtitle Edit
- 改善工作流程，同時不影響原本 Subtitle Edit 的正常使用

詳見 [CONTRIBUTING.zh-TW.md](CONTRIBUTING.zh-TW.md)。

## 授權

本專案自行開發的程式碼以 MIT License 公開。

Subtitle Edit 與其他第三方元件仍依各自原本的授權，本專案不改變其授權條件。
