# Chilled Leves

[ChilledLeves](https://github.com/LeontopodiumNivale14/ChilledLeves) 的台服移植版。理符任務自動領取與交納插件。

## 功能

- **理符瀏覽**：依職業、地點、完成狀態篩選理符，可加入最愛，並查看所需道具與報酬
- **工作清單**：把要做的理符與次數排進清單，自動前往 NPC 領取、交納（生產職與漁師的交納型理符，道具要先備好）；清單可儲存、以 JSON 匯入／匯出，也能把所需道具匯出成 Artisan 清單或 Teamcraft 連結
- **優先理符刷取**：採集職（採掘師、園藝師、漁師）依優先順序反覆領取、交納理符，可設定跑到指定等級或該職業理符全部完成為止
- 可一次從 NPC 領取多個理符、重複最後一個理符、加長領取間隔
- **理符受理限額提醒**：限額累積到指定數量時以音效、聊天訊息或提醒視窗通知，可用白名單／黑名單指定角色
- 執行期間會暫時讓開 YesAlready、TextAdvance 與 PandorasBox「自動選擇交納道具」，結束時還原

## 指令

- `/chilledleves`（別名 `/leveitalone`）：開關主視窗
- `/chilledleves s`：開啟工作清單視窗
- `/chilledleves add <理符ID> <次數>`：把理符加入工作清單
- `/chilledleves clear`：清空工作清單
- `/chilledleves start`／`stop`：開始／停止交納流程

## 台服版的修改

- 介面繁體中文化
- 不再永久關掉 vnavmesh 的「移動時對齊鏡頭」，改成排程結束時還原（vnavmesh 不在時也不會誤記）
- 修正確認框按下後又被連按第二次會讓遊戲關閉、過場動畫期間守衛解不開的問題
- 修正 `/chilledleves add` 少打參數會崩潰、`/chilledleves start` 啟動後不做事
- 清單裡有已做完或材料／等級不足的理符時，不再讓自動執行中途停下
- 沒裝 PandorasBox 時不再每幀擲例外
- 插件庫安裝提示改指台服 feed，缺少 vnavmesh 時不會導向國際服
- 多處降低記憶體用量

## 需要搭配的插件

- 必要：vnavmesh（移動）
- 選用：YesAlready、TextAdvance、PandorasBox（裝了會在執行期間自動暫停衝突的功能）

## 安裝

在 Dalamud 設定的「自訂插件庫」加入
`https://raw.githubusercontent.com/ffxiv-tc-port/DalamudPluginsTC/main/repo.json`
並啟用，再從插件列表安裝。

原作者：[LeontopodiumNivale14](https://github.com/LeontopodiumNivale14)
