# TipAura

TipAura 是一個 Windows 戰鬥計時提示工具：按下快捷鍵（預設為數字鍵盤）啟動倒數，時間到（或提前預告時）以 **Windows TTS 語音** 或 **預錄音效** 提示，並在遊戲畫面上顯示類似 WoW RaidAbilityTimeline 的 **計時條** 與 BigWigs 風格的 **中央大字字幕**。每場戰鬥的倒數內容寫在 `data/` 資料夾（含子資料夾）中的 YAML 檔，也可以連同音效與圖示打包成 `data/pack/` 中的整合包。

## Requirements

- Windows 10 19H1 (build 18362) or later, x64.
- Direct3D 11
- .NET 10 SDK（自行建置時；也可用 .NET 11 SDK 建置，見 [Develop](#develop)）
- (Native AOT builds) MSVC Build Tools and Windows SDK.
- 中文 TTS 需要安裝中文語音：**設定 → 時間與語言 → 語音 → 新增語音**（例如「中文（台灣）」）。

## Quick start

1. 啟動 `TipAura.exe`。程式會自動載入 `data/`（含子資料夾）中的第一個 YAML（附有 `example.yaml` 範例）。
2. 在 **戰鬥** 分頁選擇戰鬥設定檔；有錯誤或警告時會列出行號與原因。
3. 進入遊戲後按 **按鍵1～9**（預設 Numpad1～9）啟動對應的倒數。
4. 計時條與中央字幕預設為鎖定（滑鼠穿透、不顯示預覽）。到 **設定 → 懸浮視窗** 的 **計時條**／**中央字幕** 取消「鎖定（關閉預覽與編輯）」即可預覽、拖曳位置、拖曳邊緣調整大小，調整完再鎖定。
5. （選用）到 **設定 → 視窗掛勾** 新增遊戲視窗，再到 **戰鬥** 分頁選擇它：懸浮視窗會跟著遊戲視窗移動，並記住這個視窗使用的戰鬥設定檔。

### 快捷鍵

戰鬥設定檔只記錄 **按鍵1～9**；每個按鍵實際對應哪個鍵盤按鍵，在 **設定 → 快捷鍵** 設定，只存在這台電腦。預設如下：

| 按鍵 | 功能 |
| --- | --- |
| 按鍵1～9（Numpad1～9） | 啟動綁定該鍵的所有倒數（一條倒數可綁多個鍵，按任一個都會啟動） |
| Alt + 按鍵1～9 | 重設（清除）綁定該鍵的倒數 |
| Alt + 重設戰鬥鍵（Numpad0） | 重設戰鬥（清空所有倒數） |

在 **設定 → 快捷鍵** 可以：

- 按 **綁定按鈕** 後按下任一按鍵，重新綁定按鍵1～9 與重設戰鬥鍵（Esc 取消；不能綁定 Ctrl、Alt、Shift、Win、Esc、NumLock，也不能兩個按鍵綁同一個鍵）。
- 設定 **啟動倒數組合鍵**（預設為空）與 **重設倒數組合鍵**（預設 Alt）：標題後的括號顯示目前的組合；Ctrl、Alt、Shift 各有「左／右／任意」選項，再點一次已選的選項可取消。必須完全符合（沒有選取的鍵不能按著）。組合鍵為空表示單按按鍵，兩者不可重複（因此只有其中一個可以為空）。例如啟動倒數組合鍵設為 Ctrl、重設倒數組合鍵留空時，單按按鍵1～9 會重設倒數、單按重設戰鬥鍵會重設戰鬥。
- **還原預設快捷鍵**。

介面上以 `按鍵1 (Num1)` 的形式顯示按鍵與目前對應的快捷鍵。

綁定數字鍵盤時，NumLock 開或關都可以使用。快捷鍵為全域監聽，遊戲在前景時也有效；若遊戲以系統管理員身分執行，TipAura 也需要以系統管理員身分執行才收得到按鍵。在 TipAura 的文字欄位輸入時快捷鍵預設會暫停，避免輸入數字時誤觸倒數（可在 **設定 → 快捷鍵** 關閉）。勾選 **設定 → 快捷鍵 → 掛勾視窗不在前景時停用所有快捷鍵** 後，只有在所選掛勾的視窗或 TipAura 位於前景時快捷鍵才有效，在其他程式中按快捷鍵不會啟動倒數。

### 視窗掛勾與錨點

- **設定 → 視窗掛勾**：新增掛勾，以 **程序名稱**（執行檔名稱，可省略 `.exe`）或 **視窗標題**（標題中的任一段文字）比對遊戲視窗，不分大小寫；也可以從「從執行中的視窗新增…」直接挑選。每個掛勾可指定一個戰鬥設定檔。掛勾清單另外存在 exe 旁的 `tipaura.windows.json`。
- **戰鬥 → 視窗掛勾**：快速切換使用中的掛勾。切換時會載入該掛勾記住的戰鬥設定檔；掛勾使用中載入或另存其他檔案，掛勾就改記那個檔案。不會因為切換前景視窗而自動切換掛勾。
- 使用掛勾時，懸浮視窗以遊戲視窗的內容區域為基準定位，遊戲視窗移動或縮放時跟著移動；找不到遊戲視窗或視窗最小化時，計時條與中央字幕會隱藏。未選擇掛勾時以主螢幕為基準。
- **設定 → 懸浮視窗 → 錨點**：與 yaaft 相同的九宮格，決定懸浮視窗以基準區域的哪個位置保持距離（切換錨點時視窗位置不變）。預設中央字幕為正上方、計時條為右上方；**重設位置** 會一併恢復預設錨點。

### 主視窗

- **戰鬥**：選擇視窗掛勾；選擇／重新載入／開啟 YAML 或整合包（清單包含 `data/` 子資料夾中的 YAML 與 `data/pack/` 中的整合包）、檢視驗證結果、進行中的倒數（可單獨重設）、重設戰鬥、以按鈕測試各鍵位。
- **倒數編輯**：新增、複製、刪除與編輯倒數（按鍵、提供者、音效時間軸、註解等），試聽音效，**儲存 YAML**（Ctrl+S）、另存新檔或匯出整合包（開啟整合包時為 **儲存整合包** 與 **解壓至 data**）；**捨棄更改** 會重新載入檔案內容。Ctrl+Z／Ctrl+Y 可復原／重做編輯（最多 50 步，捨棄更改也能復原；載入其他檔案時清空）。清單可依檔案順序、鍵位或時間排序（只影響顯示）；在「檔案順序」下可用排序選單右側的上／下按鈕調整倒數在檔案中的順序。清單每列前的勾選框可暫時停用該倒數：停用後按鍵與測試按鈕都不會啟動它，進行中的也會移除；不會修改檔案，重新載入檔案後恢復啟用。滑鼠移到倒數上會顯示註解；註解欄會隨內容增高，右下角顯示字數。編輯會立即套用（並清除進行中的倒數）；儲存時會重寫檔案，原檔註解不會保留。
- **設定**：音量、最大音效同時播放數（預設 10，超過時停止最早的音效）、單一音效最長播放時間（預設 10 秒，超過即截斷，TTS 與試聽也一樣）、TTS 語音與語速、視窗掛勾、懸浮視窗（計時條與中央字幕各自的啟用、鎖定、置頂、錨點、文字大小、背景）、計時條單色顯示、字幕停留時間、Discord 擷取、輸入文字時暫停快捷鍵、掛勾視窗不在前景時停用快捷鍵、快捷鍵按鍵與組合鍵、介面字型大小、主題與語言。

### 計時條外觀

- 每條計時條左側固定保留圖示欄位，沒有圖示的倒數也會對齊。
- **設定 → 懸浮視窗 → 計時條 → 單色顯示**：所有計時條改用自訂的文字、文字陰影、計時條、底色與閃爍（警示後）顏色，不使用 YAML 的 `color`；中央字幕仍使用 YAML 顏色。深色與淺色主題各有一組顏色，互不共用，切換主題時跟著切換。細節見 [TECHNICAL.md](TECHNICAL.md#floating-windows)。

### Discord 直播（實驗性）

勾選 **設定 → 懸浮視窗 → Discord 擷取（時間軸）** 後，在 Discord 直播選擇 **TipAura 主視窗**，直播畫面會是黑底的倒數時間軸（不含中央字幕），你螢幕上的主視窗不會改變。時間軸視窗需保持顯示。開始直播後的前幾秒 Discord 可能仍顯示主視窗，等它的遊戲擷取（hook）接手後才切換成時間軸。若一直顯示主視窗，請在 Discord **設定 → 已註冊的遊戲** 加入 TipAura。開啟期間主視窗與「關於」視窗改由 DirectComposition 顯示，外觀不變。實作細節見 [TECHNICAL.md](TECHNICAL.md#discord-capture)。

## 戰鬥設定檔（YAML）

```yaml
version: 4                   # 檔案格式版本（儲存時自動寫入）
id: example                  # 識別碼（選填；整合包必填，且須與 zip 檔名相同）
name: 範例首領
timers:
  - id: breath              # 唯一識別碼（必填）
    name: 龍息               # 計時條上的名稱
    key: 1                   # 按鍵1～9（必填，同一鍵可綁多條；多鍵寫成 [1, 3]）
    duration: 30             # 倒數秒數（必填）
    warn_before: 5           # 提前預告秒數；0 = 倒數結束時提示
    repeat: false            # true = 結束後自動重新計時，直到重設
    max_instances: 1         # 這條倒數同時存在的上限
    on_limit: replace_oldest # 達上限時：replace_oldest / ignore / reset
    sound:
      tts: "{name} {sec} 秒"  # Windows TTS 文字；或改用 sfx: sfx/breath.ogg
    icon: icons/fire.png     # 圖示；也可用內建圖示，例如 lucide:flame
    message: 躲到首領背後！   # 中央字幕（省略時顯示 name）
    color: "#E05A3A"         # 計時條／字幕顏色
    provider: 坦克組          # 提供者（選填，顯示在清單上）
    note: 第二階段後改為 25 秒 # 註解（選填，最多 1024 字，只顯示在主視窗）
```

一條倒數也可以有多個音效（例如提示音 + TTS），寫成清單並以 `offset` 調整各自的時間：

```yaml
    sound:
      - sfx: sfx/chime.wav     # offset 省略 = 0，與字幕同時
      - tts: "{sec} 秒後轉階段"
        offset: 1              # 相對於 warn_before 時間點的秒數；負值提前、正值延後
```

偏移相同的音效會同時播放；`{sec}` 會換成該音效播放時的剩餘秒數。

- 音效與圖示路徑相對於 YAML 所在資料夾。音效支援 WAV、OGG (Vorbis)、MP3、M4A/AAC、WMA；圖示支援 PNG、JPG、BMP、GIF。
- 圖示也可以使用內建的 [Lucide](https://lucide.dev/icons/categories) 圖示，寫成 `lucide:<名稱>`（例如 `lucide:flame`、`lucide:snowflake`），不需要圖檔，整合包也能使用。收錄無障礙、動物、建築、表情符號、食物與飲料、自然、導航與地點、人物、科學、季節、形狀、永續、工具、交通運輸、旅行、天氣分類，共 605 個。Lucide 圖示以該倒數的 `color` 繪製。在 **倒數編輯** 分頁按圖示欄旁的 **Lucide…**，即可搜尋名稱、依分類瀏覽並點選。細節見 [TECHNICAL.md](TECHNICAL.md#lucide-icons)。
- 載入舊版 TipAura 的設定檔時，**戰鬥** 分頁會顯示格式提示與 **升級** 按鈕：升級前會把原檔（含註解）備份成 `<檔名>.v1.bak`，再以目前格式存回。舊檔不升級也能照常使用。細節見 [TECHNICAL.md](TECHNICAL.md#format-versions-and-upgrade)。
- 完整欄位規格與驗證規則見 [TECHNICAL.md](TECHNICAL.md#encounter-yaml)。

### 整合包（data/pack）

整合包把一場戰鬥的 YAML、音效與圖示放在同一個 `.zip`，方便分享：

- 放在 `data/pack/`，檔名必須等於 YAML 中的 `id`（例如 `id: dragon` → `data/pack/dragon.zip`）。
- 包內的戰鬥設定檔必須命名為 `index.yaml` 並放在 zip 根目錄；音效與圖示路徑相對於 zip 根目錄（例如 `sfx/breath.ogg`），只能使用包內的檔案。
- 壓縮方式使用 Zstandard（zstd，例如 7-Zip ZS 的 ZIP 格式選 Zstandard）；不壓縮或 Deflate 也可讀取。
- 包內路徑固定：除了 `index.yaml`，音效只能直接放在 `sfx/`、圖示只能直接放在 `icons/`（不可有子資料夾或其他檔案），否則整個整合包無法載入。
- 可以直接編輯整合包：**儲存整合包**（Ctrl+S）會重新寫出 zip，只保留倒數用到的音效與圖示（未使用的檔案會被移除），儲存後會重新載入（清空復原紀錄）。
- 音效或圖示的 **瀏覽…** 會開啟整合包資源視窗：可試聽音效、預覽圖示並點選使用，或用 **加入檔案…** 把電腦上的檔案加入整合包（儲存整合包時才寫入 zip）。**整合包資源…** 按鈕可單純瀏覽包內的音效與圖示，沒有倒數使用的檔案會以灰色顯示。
- **解壓至 data**（取代另存新檔）：把整合包（含未儲存的編輯）解壓到 `data/`：YAML 存成 `data/<整合包名稱>.yaml`，用到的音效與圖示存到 `data/sfx/`、`data/icons/`，檔名加上 `<整合包名稱>_` 前綴。若已有內容不同的同名檔案，會逐一詢問 **覆蓋** 或 **忽略**，也可選 **全部覆蓋**、**全部忽略** 或 **取消解壓**。完成後會開啟解壓出的 YAML（若保留了原有的 YAML 則仍開啟整合包）。
- **匯出整合包…**（倒數編輯分頁）：把目前的戰鬥連同用到的音效與圖示寫成 `<id>.zip`（Zstandard 壓縮），預設存到 `data/pack/`。需要先填 **Id**，檔名必須與 id 相同。音效一律放到包內的 `sfx/`、圖示放到 `icons/`；有檔案找不到或類型不符時不會寫出任何東西，未使用的檔案不會寫入。編輯中的檔案不受影響；若覆蓋目前載入的整合包，會重新載入（清空復原紀錄）。

規格與限制見 [TECHNICAL.md](TECHNICAL.md#encounter-packs)。

## Develop

由專案目錄執行:

```powershell
dotnet run -c Release
```

或是編譯至 AoT 執行檔:
```
dotnet publish -c Release -r win-x64 -p:PublishAot=true -o artifacts/aot
```

預設以 .NET 10 建置；加上 `-p:TipAuraNet11=true` 改以 .NET 11 建置（整合包改用 .NET 內建的 Zstandard，不需 SharpCompress）。

> [!WARNING]
> 本專案絕大多數的程式碼由 Agentic AI Coding Tool 生成， 二次開發請優先考慮 GPT6.1-Sol 或 Opus 5.5 等同級 Model。

## License

TipAura 以 [MIT 授權](LICENSE) 釋出。`data/` 中隨附的範例設定檔、音效與圖示以 [CC0 1.0](data/LICENSE.txt) 釋出（屬公眾領域，可自由使用）。第三方元件授權見 **關於 → 第三方授權**，發布的 zip 也附在 `licenses/`。

## Disclaimer

- TipAura is an unofficial tool. It is not developed, endorsed or supported by any game developer or publisher. Game names and trademarks belong to their respective owners.
- TipAura only listens to the keyboard to start its own timers; it does not modify the game, read its memory or send input to it. Users are responsible for determining whether its use complies with the game's terms of service and bear any resulting risk.
- Timer alerts depend on the encounter files and the user's key presses and may be late, early or missing. Use them for reference only.
- Versions modified, redistributed or further developed by others are their own responsibility; the original author is not liable for any disputes or damages arising from them.
- TipAura is provided "as is", without warranty of any kind. The author shall not be liable for any damages arising from the use or inability to use this software.

These statements supplement, and do not change, the warranty disclaimer and limitation of liability in the [MIT License](LICENSE).

### 免責聲明

- TipAura 為非官方工具，未經任何遊戲開發商或營運商開發、授權或支援。遊戲名稱與商標屬於其各自的權利人。
- TipAura 僅監聽鍵盤以啟動自身的倒數，不修改遊戲、不讀取遊戲記憶體，亦不向遊戲發送任何操作輸入。使用者應自行確認使用方式是否符合遊戲服務條款，並自負相關風險。
- 倒數提示取決於戰鬥設定檔與使用者的按鍵操作，可能延遲、提前或遺漏，僅供參考。
- 經他人修改、重新散布或二次開發的版本由其自行負責，原作者不對因此產生的任何爭議或損害負責。
- TipAura 按「現狀」提供，不提供任何明示或暗示之擔保。對於因使用或無法使用本軟體所生之任何損害，作者概不負責。

以上聲明為 [MIT 授權](LICENSE) 中擔保免責與責任限制條款之補充，並不變更其內容。

## Agentic Coder

For implementation details, precise behavior rules, validation commands and build/release workflows, see [TECHNICAL.md](TECHNICAL.md).
