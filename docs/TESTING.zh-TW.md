# 測試

這個 repo 同時測試本專案自己的核心邏輯，以及和 Subtitle Edit 接在一起之後的整合行為。

## 目前主要測試範圍

- **可逆音訊核心**：時間軸轉換、剪除、驗證、輸出、revision 與還原
- **AI 字幕校閱核心**：Prompt、回覆解析、詞彙表、缺漏／重複／衝突 ID、套用前預覽
- **Review Session 安全**：保存、續接與過期 Session fingerprint
- **Subtitle Edit 整合**：波形區間選取、音訊工作流與 AI 校閱 UI

## 執行測試

```powershell
pwsh ./scripts/setup-upstream.ps1
pwsh ./scripts/test.ps1
```

`setup-upstream.ps1` 會先建立乾淨的 Subtitle Edit 5.2.0 checkout，再套用 integration patch，因此 UI 測試不是依賴開發者電腦上原本的工作樹。

## 驗證建置

```powershell
pwsh ./scripts/build.ps1
```

建置腳本會先執行測試，通過後才 publish Windows x64 整合版本。

## 測試素材

公開音訊 fixture 為合成測試音；文字 fixture 只使用通用範例。
