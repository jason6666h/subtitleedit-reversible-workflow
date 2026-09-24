# 參與開發

歡迎一起改善這個專案。

核心原則是：**增加音訊／字幕工作能力，但不要讓原本 Subtitle Edit 的正常操作變得更難預測或更不安全。**

## 很適合協作的方向

- 英文 UI localization
- regression tests
- 跟進新的 Subtitle Edit 穩定版
- 繼續縮小 host hook 數量
- revision／checkpoint 安全性
- 音訊巡聽與剪修操作體驗
- AI 校閱的驗證與工作流程

## Pull Request 前請先確認

1. 測試資料必須是合成或通用內容。
2. 不要加入私人影音、真實字幕專案、登入憑證或特定電腦路徑。
3. Subtitle Edit host 的修改盡量維持最小。
4. 可重複使用的功能邏輯應放在本專案自己的 core／integration 目錄，不要無限制擴大 host patch。
5. 執行：

```powershell
pwsh ./scripts/test.ps1
```

6. 如果修改到建置流程，再執行：

```powershell
pwsh ./scripts/build.ps1
```

## UI 修改原則

調整 UI 時：

- 不影響這套工作流之外的 Subtitle Edit 正常使用
- 常用操作不要增加不必要的點擊
- 破壞性操作要清楚、可辨識
- 快捷鍵要能自訂或取消
- 優先使用一般使用者看得懂的名稱，不使用內部工程術語

## 語言

目前繁體中文 UI 最完整，歡迎協助英文 localization。

修改公開文件時，也請盡量同步維護繁中與英文版本。
