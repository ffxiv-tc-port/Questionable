# Questionable

[Questionable](https://github.com/PunishXIV/Questionable) 的台服移植版。自動跑任務助手：導航到任務目標、處理對話與互動，自動完成任務。

## 功能

- 依序自動執行任務：移動、對話選項、互動、戰鬥觸發等
- 支援主線／支線任務、部分友好部族任務、採集類任務
- 採集任務可自動移動至採集點並執行採集
- 釣魚類任務可自動生成釣組並完成釣魚（需搭配 AutoHook）
- 任務戰鬥（單人副本）可交給 BossMod 自動完成
- 任務優先順序視窗，可調整多個可做任務的執行順序
- 任務驗證／除錯視窗，方便回報路徑資料問題

## 相依插件

**必要**：
- vnavmesh — 區域內導航移動
- Lifestream — 城內乙太之晶快速移動
- TextAdvance — 自動接取／完成任務、跳過對話與過場動畫

**戰鬥模組（擇一）**：BossMod（台服插件庫的 BossMod Reborn 也可以）、Rotation Solver Reborn

**選配**：[CBT](https://github.com/Jaksuhn/Automaton)（自動完成狙擊任務）、
[Pandora's Box](https://github.com/PunishXIV/PandorasBox)（自動完成 ATM）、
Artisan（涉及製作的任務）、AutoDuty（需要組隊完成的副本）、AutoHook（釣魚任務）、
YesAlready（跑任務期間自動暫停它）、TataruPraise（需要人工處理時出聲提醒）

除了 CBT、Pandora's Box 與 Rotation Solver Reborn，以上都在台服插件庫。

## 指令

| 指令 | 功能 |
|---|---|
| `/qst` | 開啟任務視窗 |
| `/qst help`／`/qst help-all` | 列出常用／全部指令 |
| `/qst config` | 開啟設定視窗 |
| `/qst start` | 開始執行任務 |
| `/qst stop` | 停止執行 |
| `/qst reload` | 重新載入所有任務資料 |
| `/qst next <任務編號>` | 指定下一個要做的任務（不帶編號＝清除） |
| `/qst which` | 顯示以目前選取目標開頭的任務 |
| `/qst zone` | 顯示目前所在地圖可執行的任務（僅限有路徑資料、可見且未接取的任務） |
| `/qst journal` | 開啟任務進度視窗 |
| `/qst priority` | 開啟任務優先順序視窗 |

## 台服版的修改

- **自動接續**（主視窗勾選，預設關）：沒有任務可做時不停下、持續等下一個任務（最多 90 秒），尋路失敗會自動重試目前步驟。
- **暫停主線任務**（進階分頁，預設關）：不再自動接取下一個主線任務，其他任務照常。
- **任務獎勵**：可自動開啟任務獎勵寶箱（預設關）；接任務時自動使用背包裡還沒學會的表情教材、秘傳書與傳承錄。
- **優先任務清單**可一鍵補上整串還沒做的前置任務。
- **任務戰鬥失敗時停下**：交給 BossMod 打的任務戰鬥失敗時不自動重試，停下並提醒（可關）。
- **卡住處理**：卡住時自動重新整理任務步驟、乙太之光共鳴卡住時小幅走位、飛行中找不到路先降落再重試，用完重試額度會停下並說明原因。
- **跨插件整合**：釣魚任務接上 AutoHook；交給 AutoDuty 跑副本時只用暫時設定，不改掉 AutoDuty 的模式與輪數；跑任務期間以租約暫停 YesAlready；需要人工處理時請 TataruPraise 出聲；AutoRetainer 換角前會請它停止。
- **台服相容性修正**：確認框（含帶物品名稱的問句、付交通費的渡船與飛空艇）自動回答、副本區選單自動選「隨意（自動選擇）」、台服還沒開放的後續任務照常交完目前任務再結束、台服沒有的任務路線收成一行統計；相依插件的安裝來源改指台服插件庫。
- 多處防閃退修正（確認框連按、跨插件 IPC 改在主執行緒執行）與多處降低記憶體用量。
- 介面繁體中文化。

## 安裝

在 Dalamud 設定的「自訂插件庫」加入
`https://raw.githubusercontent.com/ffxiv-tc-port/DalamudPluginsTC/main/repo.json` 並啟用，
再從插件列表安裝。

原作者：[Liza](https://github.com/carvelli)，上游現由 alydev、erdelf、Limiana、Censored、
ClockwiseStarr、MrGuffels、WigglyMuffin、v3rso 等人維護。
