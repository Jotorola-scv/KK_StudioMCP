# Maker MCP（本體 Maker 支援）

[English summary](#english-summary)

這是 [tomTom1010-IEE/KK_StudioMCP](https://github.com/tomTom1010-IEE/KK_StudioMCP) 的改編分支，讓同一顆 bridge
DLL 也能在 **Koikatsu Sunshine 本體的角色 Maker** 裡運作。用途是讓 AI agent 直接查看並修正角色的材質、shader、
貼圖與服裝穿脫問題，而不必先把角色搬進 Studio。

- **一顆 DLL，兩種模式**：在 `CharaStudio.exe` 裡維持原本的 StudioMCP；在 `KoikatsuSunshine.exe` 裡自動切換成 Maker 模式。
- **可與 Studio 同時開**：Maker 用獨立埠（預設 8848）與獨立連線檔 `UserData/StudioMCP/maker-connection.json`。
- **修改都走 Material Editor**：材質變更由 ME 保存，在 Maker 存卡時一起寫進角色卡。

## 功能一覽

### 查看角色

| 工具 | 功能 |
|---|---|
| `studio_status` | 連線、模式（`maker`）、寫入權限、ME／Screencap 是否可用、截圖模式 |
| `maker_snapshot` | 角色名稱、目前服裝套數、服裝 9 格／頭髮 4 格／飾品的清單 ID、名稱、穿脫狀態與網格數、鏡頭，以及寫入用的 `sceneSession` |

### 材質（透過 Material Editor，存卡時保存）

| 工具 | 功能 |
|---|---|
| `materials_list` | 列出所有網格、材質槽與 shader，並分類為 Character／Hair／Clothing／Accessory 與部位格號 |
| `material_inspect` | 讀取材質的所有 shader 屬性與目前數值 |
| `material_set` | 修改顏色（RGBA）或數值屬性 |
| `material_set_shader` | 更換為 ME 已註冊的 shader |
| `material_import_texture` | 匯入 PNG／JPEG 作為貼圖，貼圖會存進卡片 |
| `material_texture_transform` | 調整貼圖平鋪（scale）與偏移（offset） |
| `material_set_render_queue` | 修改渲染順序（處理透明穿插、深度問題），`-1` 還原 |
| `material_reset` | 移除某屬性的 ME 修改，回到原始值 |

Maker 只有一個角色，所以材質工具的 `objectId` 一律填 `0`。ME 會把修改套用到同一部位中所有同名材質，
回傳的 `matchingMaterials` 會列出一起受影響的網格。

### 服裝穿脫

| 工具 | 功能 |
|---|---|
| `maker_set_clothes_state` | 切換部位狀態：0 穿著、1 半脫、2 半脫 2、3 脫掉。部位可用 `0`–`8` 或 `top`、`bottom`、`bra`、`shorts`、`gloves`、`pantyhose`、`socks`、`shoes_inner`、`shoes_outer` |

這是暫時狀態，用來檢查半脫穿模，**不會存進卡片**。

### 鏡頭、截圖、批次

| 工具 | 功能 |
|---|---|
| `camera_get` / `camera_set` | 讀取／設定 Maker 鏡頭的注視點、旋轉、距離與 FOV |
| `capture` | 透過 Screencap 渲染截圖（不含 Maker UI），可選 `None`／`blackout`／`rgAlpha` |
| `batch_apply` | 一次執行 1–32 個 `me.set` 或 `camera.set`，先全部驗證，失敗會自動回滾 |
| `batch_undo` | 復原最近一次批次 |
| `render_get` / `render_diagnostics` | 唯讀的環境光、燈光、反射探針、相機深度資訊 |

### 建議流程

1. `maker_snapshot` 取得 `sceneSession` 與部位清單
2. `materials_list` → `material_inspect` 找出問題材質
3. `capture` 截修改前的圖
4. `batch_apply` 試改，或用 `maker_set_clothes_state` 檢查半脫
5. 再 `capture` 比較，不滿意就 `batch_undo`，滿意就在 Maker 存卡

## 限制

- 燈光、場景物件、存檔點、天空盒、色調映射等 Studio 專用功能，在 Maker 會回傳「Studio-only」錯誤。
- 不會更換服裝品項、改 Maker 內建的顏色／花紋滑桿或新增飾品；只修改材質層。
- 載入別的角色或服裝後 `sceneSession` 會更新、網格 ID 失效，需重新 `maker_snapshot` 與 `materials_list`。
- 剛進 Maker 時可能回傳 `Maker character is still loading`，稍後重試即可。
- 目前只在 KKS 實測；KK 的程式路徑已加上，但尚未驗證。

## 安裝

1. 建置 bridge 並放進 `BepInEx/plugins/StudioMCP/`（取代原本的 `KKS_StudioMCP.dll`，另外兩顆依賴 DLL 不變）：

   ```powershell
   dotnet build src/StudioBridge/StudioBridge.csproj -c Release -p:Game=KKS -p:GameRoot='<KKS 安裝路徑>'
   ```

2. 建置 host：

   ```powershell
   dotnet publish src/StudioMcp.Host -c Release -o publish/Host
   ```

3. 在 `BepInEx/config/com.tomtom.studiomcp.cfg` 開啟 `Server.AllowWrites = true`（預設唯讀）。Maker 埠可在 `[Maker] Port` 修改。
4. 在 MCP client 新增一個伺服器，`STUDIOMCP_CONNECTION` 指向 Maker 的連線檔，例如 Claude Code 的 `~/.claude.json`：

   ```json
   "kksmaker": {
     "type": "stdio",
     "command": "C:\\Program Files\\dotnet\\dotnet.exe",
     "args": ["<repo>/publish/Host/StudioMcp.Host.dll"],
     "env": { "STUDIOMCP_CONNECTION": "<KKS 安裝路徑>/UserData/StudioMCP/maker-connection.json" }
   }
   ```

   Studio 用的伺服器維持指向 `connection.json`。連線檔含 token，請勿分享。

## 相對上游的修改

- **新增 Maker 模式**：`Plugin.cs` 依行程名稱切換模式，主程式使用獨立埠與連線檔，並在 Maker 載入／重載角色／離開時更新 session；新增 `MakerCommands.cs`（snapshot、服裝狀態、Maker 鏡頭）。
- **材質解析通用化**：`MaterialCommands.cs` 在 Maker 中改以 Maker 角色為目標；`BatchCommands.cs` 在 Maker 中使用 Maker 鏡頭的復原。
- **修正材質被複製的問題（Studio 也受益）**：原本讀取 `renderer.materials`，每次呼叫都會複製所有材質（名稱累加 `(Instance)`，也會讓遊戲持有的原材質參照失效）；改為 `sharedMaterials`，與 Material Editor 一致。
- **Host 錯誤訊息**：bridge 的錯誤改以 `McpException` 回傳，agent 能看到實際原因，而不是只有「An error occurred invoking」。
- **Host 工具**：新增 `maker_snapshot`、`maker_set_clothes_state`，並在相關工具說明中註明 Maker 用法。

---

## English summary

This fork lets the same StudioMCP bridge DLL run inside the **Koikatsu Sunshine main-game character maker**, so an
agent can inspect and fix a character's materials, shaders, textures and clothing states without moving it into Studio.

- The bridge picks its mode from the process name. In the main game it listens on its own port (default 8848, config
  `[Maker] Port`) and writes `UserData/StudioMCP/maker-connection.json`, so Studio and the maker can run together.
- Works in the maker: `studio_status`, new `maker_snapshot` and `maker_set_clothes_state`, all `material_*` tools
  (use `objectId: 0`), `camera_get`/`camera_set`, `capture`, `batch_apply`/`batch_undo` (`me.set`, `camera.set`),
  `render_get`, `render_diagnostics`. Studio-only tools return an explicit error.
- Material edits go through Material Editor and are saved with the card. Clothing states are runtime only.
- Also fixes an upstream issue where listing materials read `renderer.materials` and cloned every material on each
  call; the bridge now reads `sharedMaterials`, as Material Editor does. The host now forwards bridge error text.
- Tested live on KKS only.

Licensed under AGPL-3.0, like upstream. Modified 2026-10-29.
