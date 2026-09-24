# 架構說明

這個專案分成三層，目的是讓大部分邏輯可以在不啟動完整 Subtitle Edit UI 的情況下獨立測試。

## 1. Core libraries

### AudioWorkflow.Core

負責可逆音訊流程：

- 音訊剪除與精確 sample 邊界
- 原始／修改後時間軸映射
- revision 與 checkpoint
- hash 與內容完整性驗證
- 音訊／字幕配對輸出與載入
- Adobe Audition 往返驗證

### SubtitleReview.Core

負責 AI 校閱安全協定：

- 批次 ID 與 Prompt 產生
- Markdown 回覆解析
- 缺漏、重複、衝突 ID 檢查
- 詞彙表與保護詞
- session fingerprint 與過期結果防護
- 套用前預覽與格式保護

## 2. Subtitle Edit integration

`integration/ReversibleAudioSync/` 與 `integration/SubtitleReview/` 把 Core 功能接到 Subtitle Edit 的波形、播放器、Undo/Redo、選單與操作視窗。

公開 repo 不維護一份完整 Subtitle Edit fork，而是使用 `integration/host-hooks.patch` 保存必要且有限的 host 修改。

## 3. Tests

測試包含：

- 可重現的音訊核心測試
- AI 校閱協定與解析測試
- 校閱 session 安全測試
- Subtitle Edit UI 整合測試

音訊 fixture 為合成測試音，不需要任何使用者錄音。

## 建置方式

`scripts/setup-upstream.ps1` 會取得指定的 Subtitle Edit 官方版本並套用 integration patch，之後測試與 build 都在這份乾淨 checkout 上執行。

這樣可以讓公開 repo 專注在新增的工作流，同時清楚保留與官方 Subtitle Edit 的版本關係。
