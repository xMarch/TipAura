using System.Globalization;

// English source strings are stable resource keys. New UI text is written in English first.
internal static class Localization
{
    private static bool _english;
    internal static void Configure(string? language) => _english = string.Equals(language, "en", StringComparison.OrdinalIgnoreCase);
    internal static bool English => _english;
    internal static string T(string key) => _english ? key : Chinese.TryGetValue(key, out string? value) ? value : key;
    internal static string F(string key, params object?[] arguments) =>
        string.Format(CultureInfo.InvariantCulture, T(key), arguments);

    // Worker and settings messages stay in English for the log; localize known prefixes at the UI
    // boundary while leaving paths and exception details intact.
    internal static string Status(string source)
    {
        if (_english) return source;
        if (Chinese.TryGetValue(source, out var exact)) return exact;
        foreach (var (prefix, translated) in StatusPrefixes)
            if (source.StartsWith(prefix, StringComparison.Ordinal)) return translated + source[prefix.Length..];
        return source;
    }

    // Every Chinese string the UI can show, so the font atlas can include its glyphs.
    internal static IEnumerable<string> AllChineseText() =>
        Chinese.Values.Concat(StatusPrefixes.Select(prefix => prefix.Chinese));

    internal static readonly (string English, string Chinese)[] StatusPrefixes =
    [
        ("Could not load settings: ", "無法載入設定："),
        ("Could not load window hooks: ", "無法載入視窗掛勾："),
        ("Could not write log: ", "無法寫入記錄檔："),
        ("Could not close log: ", "無法關閉記錄檔："),
    ];

    private static readonly Dictionary<string, string> Chinese = new(StringComparer.Ordinal)
    {
        ["Minimize"] = "最小化", ["Close"] = "關閉",
        ["Encounter"] = "戰鬥", ["Timers"] = "倒數編輯", ["Settings"] = "設定",
        ["Dismiss"] = "關閉訊息", ["(none)"] = "（無）",
        ["Encounter file"] = "戰鬥設定檔", ["Encounter files"] = "戰鬥設定檔", ["All files"] = "所有檔案",
        ["Refresh"] = "重新整理", ["Reload"] = "重新載入", ["Open..."] = "開啟…",
        ["No .yaml files in {0}"] = "{0} 中沒有 .yaml 檔案",
        ["{0} - {1} timer(s)"] = "{0} － {1} 條倒數",
        ["Sounds: {0} ready"] = "音效：{0} 個就緒",
        ["Sounds: {0} ready, {1} preparing"] = "音效：{0} 個就緒，{1} 個準備中",
        ["Sounds: {0} ready, {1} preparing, {2} failed"] = "音效：{0} 個就緒，{1} 個準備中，{2} 個失敗",
        ["{0} error(s), {1} warning(s)"] = "{0} 個錯誤，{1} 個警告",
        ["Running timers"] = "進行中的倒數", ["Reset encounter"] = "重設戰鬥", ["Test:"] = "測試：",
        ["Start the {0} timers"] = "啟動{0}的倒數",
        ["No running timers. Press a key's hotkey to start."] = "目前沒有倒數。按下按鍵的快捷鍵即可開始。",
        ["Key {0} ({1})"] = "按鍵{0} ({1})", ["Key {0}"] = "按鍵{0}",
        ["Key"] = "按鍵", ["Name"] = "名稱", ["Remaining"] = "剩餘", ["Reset"] = "重設",
        ["Hotkeys"] = "快捷鍵",
        ["Global hotkeys are unavailable: {0}"] = "無法使用全域快捷鍵：{0}",
        ["Discard unsaved timer changes?"] = "要捨棄尚未儲存的倒數變更嗎？", ["Discard"] = "捨棄", ["Cancel"] = "取消",
        ["Load an encounter file first."] = "請先載入戰鬥設定檔。", ["New encounter"] = "新戰鬥",
        ["Save YAML"] = "儲存 YAML", ["Save as..."] = "另存新檔…", ["Saved {0}."] = "已儲存 {0}。",
        ["Could not save {0}: {1}"] = "無法儲存 {0}：{1}",
        ["Upgraded {0}; the original is kept as {1}."] = "已升級 {0}，原檔保留為 {1}。",
        ["This file uses the older format version {0}."] = "此檔使用舊格式版本 {0}。",
        ["This file does not declare its format version."] = "此檔未標記格式版本。",
        ["Upgrade"] = "升級",
        ["Pack entry '{0}' has an invalid name."] = "整合包項目「{0}」的名稱無效。",
        ["Pack entry '{0}' is encrypted."] = "整合包項目「{0}」已加密。",
        ["Pack entry '{0}' uses compression method {1}; use Zstandard, Deflate or store."] =
            "整合包項目「{0}」使用壓縮方法 {1}；請使用 Zstandard、Deflate 或不壓縮。",
        ["Pack entry '{0}' is larger than {1} MB."] = "整合包項目「{0}」超過 {1} MB。",
        ["Pack entry '{0}' appears twice."] = "整合包項目「{0}」重複出現。",
        ["The pack has more than {0} entries."] = "整合包的項目超過 {0} 個。",
        ["{0} is not in the pack."] = "整合包中沒有 {0}。",
        ["Could not read pack entry '{0}': {1}"] = "無法讀取整合包項目「{0}」：{1}",
        ["Pack entry '{0}' is truncated."] = "整合包項目「{0}」不完整。",
        ["Pack entry '{0}' is longer than its declared size."] = "整合包項目「{0}」比標示的大小還長。",
        ["Pack entry '{0}' is damaged."] = "整合包項目「{0}」已損毀。",
        ["Not a zip file."] = "不是 zip 檔案。", ["Split zip files are not supported."] = "不支援分割的 zip 檔案。",
        ["A pack entry name is not valid UTF-8."] = "整合包項目名稱不是有效的 UTF-8。",
        ["The pack has no {0}."] = "整合包中沒有 {0}。", ["Could not read pack: {0}"] = "無法讀取整合包：{0}",
        ["{0} must declare id: {1}, the pack's file name."] = "{0} 必須宣告 id: {1}（整合包的檔名）。",
        ["The pack's id '{0}' does not match its file name '{1}'."] = "整合包的 id「{0}」與檔名「{1}」不符。",
        ["Timer '{0}': '{1}' must be a path inside the pack."] = "倒數「{0}」：「{1}」必須是整合包內的路徑。",
        ["Timer '{0}': {1} is not in the pack."] = "倒數「{0}」：整合包中沒有 {1}。",
        ["Pack entry '{0}' is not allowed; a pack holds only {1}, sounds in sfx/ and images in icons/."] =
            "不允許整合包項目「{0}」；整合包只能包含 {1}、sfx/ 中的音效與 icons/ 中的圖片。",
        ["Timer '{0}': '{1}' must be directly in the pack's {2}/ folder."] = "倒數「{0}」：「{1}」必須直接放在整合包的 {2}/ 資料夾中。",
        ["'{0}' has an unsupported file type; supported: {1}."] = "「{0}」的檔案類型不支援；支援：{1}。",
        ["Save pack"] = "儲存整合包",
        ["Rewrite the zip with index.yaml and the files the timers use; unused files are removed."] =
            "重新寫出 zip，內含 index.yaml 及倒數用到的檔案；未使用的檔案會被移除。",
        ["Saved pack {0} with {1} asset file(s)."] = "已儲存整合包 {0}，含 {1} 個素材檔。",
        ["{0} unused file(s) removed."] = "已移除 {0} 個未使用的檔案。",
        ["Extract to data"] = "解壓至 data",
        ["Write {0}.yaml into data/, and the sounds and icons it uses into data/sfx and data/icons with the prefix {0}_. Existing files with other contents are asked about one by one."] =
            "將 {0}.yaml 寫入 data/，用到的音效與圖示以 {0}_ 為檔名前綴寫入 data/sfx 與 data/icons。已存在且內容不同的檔案會逐一詢問。",
        ["Could not extract {0}: {1}"] = "無法解壓 {0}：{1}",
        ["Extracted {0}: {1} file(s) written, {2} kept."] = "已解壓 {0}：寫入 {1} 個檔案，保留 {2} 個既有檔案。",
        ["Extracted {1} file(s); {0} was kept, so the pack stays open."] = "已解壓 {1} 個檔案；保留了既有的 {0}，因此仍開啟整合包。",
        ["File already exists"] = "檔案已存在",
        ["data/{0} already exists with different contents."] = "data/{0} 已存在且內容不同。",
        ["Conflict {0} of {1}"] = "第 {0} 個衝突，共 {1} 個",
        ["Overwrite"] = "覆蓋", ["Keep existing"] = "忽略", ["Overwrite all"] = "全部覆蓋", ["Keep all"] = "全部忽略",
        ["Cancel extraction"] = "取消解壓",
        ["Pack files..."] = "整合包資源…", ["Show the sounds and icons in the pack."] = "檢視整合包內的音效與圖示。",
        ["Pack files"] = "整合包資源", ["Icons"] = "圖示",
        ["Pick a sound for the timer."] = "為倒數選擇音效。", ["Pick an icon for the timer."] = "為倒數選擇圖示。",
        ["No files in this folder."] = "此資料夾中沒有檔案。",
        ["(added, not saved yet)"] = "（新加入，尚未儲存）",
        ["No timer uses this file; saving the pack removes it."] = "沒有倒數使用此檔案；儲存整合包時會移除。",
        ["Add file..."] = "加入檔案…",
        ["Copy a file into the pack as {0}/<file name> and use it. It is written into the zip when the pack is saved."] =
            "將檔案以 {0}/<檔名> 加入整合包並使用，儲存整合包時寫入 zip。",
        ["Could not add {0} to the pack: {1}"] = "無法將 {0} 加入整合包：{1}",
        ["Export pack..."] = "匯出整合包…", ["Encounter packs"] = "整合包",
        ["Set an id first; the pack is named <id>.zip."] = "請先設定 id；整合包會命名為 <id>.zip。",
        ["Write <id>.zip with index.yaml and the sounds and icons it uses, by default into data/pack."] =
            "寫出 <id>.zip，內含 index.yaml 及用到的音效與圖示，預設存到 data/pack。",
        ["The id '{0}' cannot be used as a file name."] = "id「{0}」不能作為檔名。",
        ["The pack must be named {0}."] = "整合包必須命名為 {0}。",
        ["Missing files: {0}. Nothing was exported."] = "找不到檔案：{0}。未匯出任何檔案。",
        ["{0} is larger than {1} MB."] = "{0} 超過 {1} MB。",
        ["The pack would be larger than 4 GB."] = "整合包會超過 4 GB。",
        ["Exported pack {0} with {1} asset file(s)."] = "已匯出整合包 {0}，含 {1} 個素材檔。",
        ["Could not export {0}: {1}"] = "無法匯出 {0}：{1}",
        ["Id (optional)"] = "Id（選填）",
        ["A pack's index.yaml must declare the zip's file name as its id."] = "整合包的 index.yaml 必須以 zip 檔名作為 id。",
        ["A pack only uses files inside its zip: pick one, or add a file to the pack."] = "整合包只使用 zip 內的檔案：請從中選擇，或將檔案加入整合包。",
        ["Copies the file to {0}.v{1}.bak first (an unused name), then saves it in format version {2}. Comments are kept only in the copy."] =
            "先將原檔複製為 {0}.v{1}.bak（若已存在則另取名稱），再以格式版本 {2} 存回。註解只保留在複本中。",
        ["Discard changes"] = "捨棄更改",
        ["Reload the file and drop unsaved edits. Ctrl+Z brings them back."] = "重新載入檔案並捨棄未儲存的編輯，可按 Ctrl+Z 復原。",
        ["Ctrl+S: save. Ctrl+Z / Ctrl+Y: undo / redo (up to 50 steps, cleared when another file is loaded)."] =
            "Ctrl+S：儲存。Ctrl+Z / Ctrl+Y：復原 / 重做（最多 50 步，載入其他檔案時清空）。",
        ["Pause hotkeys while typing in TipAura"] = "在 TipAura 中輸入文字時停用快捷鍵",
        ["While a TipAura text field has keyboard focus, hotkeys are ignored, so typing does not start timers. They work again once another window, such as the game, is focused."] =
            "TipAura 的文字欄位取得鍵盤焦點時會忽略快捷鍵，輸入文字不會啟動倒數；切換到其他視窗（例如遊戲）後立即恢復。",
        ["Start modifier ({0})"] = "啟動倒數組合鍵 ({0})", ["Reset modifier ({0})"] = "重設倒數組合鍵 ({0})",
        ["None"] = "無", ["Left"] = "左", ["Right"] = "右", ["Either"] = "任意",
        ["Hold these together with a key. Click a selected option again to clear it; a modifier with nothing selected must not be held."] =
            "與按鍵一起按住。再次點擊已選取的選項可取消；沒有選取任何選項的組合鍵不能按著。",
        ["The modifiers must differ."] = "組合鍵不可重複",
        ["Reset encounter key"] = "重設戰鬥鍵",
        ["Press a key... (Esc to cancel)"] = "請按下按鍵…（Esc 取消）",
        ["Click, then press the key to bind. Key bindings are stored on this computer only; encounter files keep using keys 1-9."] =
            "點擊後按下要綁定的按鍵。按鍵設定只存在這台電腦；戰鬥設定檔仍使用按鍵 1~9。",
        ["{0} cannot be bound."] = "{0} 無法綁定。", ["{0} is already bound to {1}."] = "{0} 已綁定到{1}。",
        ["Restore default hotkeys"] = "還原預設快捷鍵",
        ["Unchecked timers are disabled for this session: their keys do not start them. The file does not change, and reloading enables them again."] =
            "取消勾選的倒數在本次執行中停用：按鍵不會啟動它們。不會修改檔案，重新載入後恢復啟用。",
        ["Edits apply immediately and reset running timers. Saving rewrites the file without its comments."] =
            "編輯會立即套用並重設進行中的倒數。儲存會重寫檔案，原有註解不會保留。",
        ["Encounter name"] = "戰鬥名稱", ["Add timer"] = "新增倒數", ["Select a timer to edit it."] = "選擇要編輯的倒數。",
        ["Id"] = "ID", ["Duration (s)"] = "倒數秒數", ["Warn before (s)"] = "提前預告（秒）",
        ["Seconds before the end to show the message. Sound offsets count from this point. 0 = at the end."] =
            "在倒數結束前幾秒顯示字幕，音效的偏移也以此為基準；0 表示結束時。",
        ["Repeat"] = "重複", ["Restart automatically until reset"] = "結束後自動重新計時，直到重設",
        ["Simultaneous limit"] = "同時存在上限", ["At the limit"] = "達到上限時",
        ["Replace oldest"] = "取代最舊的", ["Ignore"] = "忽略新觸發",
        ["Replace oldest: drop the oldest copy. Ignore: keep the running copies. Reset: clear them and start one."] =
            "取代最舊的：移除最早的一條。忽略新觸發：保留進行中的倒數。重設：清空後重新開始一條。",
        ["Windows TTS"] = "Windows TTS", ["Sound file"] = "音效檔",
        ["Sounds"] = "音效", ["Offsets are seconds from the warn-before point; sounds at the same offset play together."] =
            "偏移以提前預告的時間點為基準（秒）；偏移相同的音效會同時播放。",
        ["{name} = timer name, {sec} = seconds left when this sound plays"] = "{name} = 倒數名稱，{sec} = 此音效播放時的剩餘秒數",
        ["Plays {0} s after the start (negative offsets play before the warn-before point)."] =
            "於倒數開始後 {0} 秒播放（負的偏移會在提前預告時間點之前播放）。",
        ["Preview"] = "試聽", ["Remove"] = "移除", ["Add sound"] = "新增音效",
        ["Preview all"] = "依時間軸試聽", ["Plays every sound with its offset."] = "依各音效的偏移依序播放全部音效。",
        ["Keys"] = "按鍵", ["Any selected key starts this timer; the reset modifier with that key resets it."] =
            "按下任一選取的按鍵都會啟動此倒數；重設倒數組合鍵 + 該鍵會重設它。",
        ["Provider"] = "提供者", ["Optional"] = "選填", ["Offset"] = "偏移",
        ["Move up (file order only)"] = "上移（僅限檔案順序）", ["Move down (file order only)"] = "下移（僅限檔案順序）",
        ["Note"] = "註解",
        ["File order"] = "檔案順序", ["By key"] = "依鍵位", ["By time"] = "依時間",
        ["Sort the list. The order in the file does not change."] = "排序清單，不會改變檔案內的順序。",
        ["Browse..."] = "瀏覽…", ["Sound files"] = "音效檔", ["Images"] = "圖片",
        ["Lucide..."] = "Lucide…", ["Pick a built-in Lucide icon. It is drawn in the timer's color."] = "選擇內建的 Lucide 圖示，會以倒數的顏色繪製。",
        ["Search icons"] = "搜尋圖示", ["All categories"] = "所有分類", ["No matching icons."] = "沒有符合的圖示。", ["{0} icons"] = "{0} 個圖示",
        ["Accessibility"] = "無障礙", ["Animals"] = "動物", ["Buildings"] = "建築", ["Emoji"] = "表情符號",
        ["Food & beverage"] = "食物與飲料", ["Nature"] = "自然", ["Navigation & Places"] = "導航與地點", ["People"] = "人物",
        ["Science"] = "科學", ["Seasons"] = "季節", ["Shapes"] = "形狀", ["Sustainability"] = "永續",
        ["Tools"] = "工具", ["Transportation"] = "交通運輸", ["Travel"] = "旅行", ["Weather"] = "天氣",
        ["Could not play sound: {0}"] = "無法播放音效：{0}",
        ["Message"] = "字幕提示", ["Icon"] = "圖示", ["Color"] = "顏色",
        ["Duplicate"] = "複製", ["Delete timer"] = "刪除倒數",
        ["Audio"] = "音訊", ["Volume"] = "音量", ["Simultaneous sounds"] = "同時播放上限",
        ["When more sounds play at once, the oldest one stops."] = "超過上限時，最早開始的音效會被停止。",
        ["TTS voice"] = "TTS 語音", ["System default"] = "系統預設", ["TTS speed"] = "TTS 語速",
        ["No Chinese voice installed: Settings > Time & Language > Speech > Add voices."] =
            "未安裝中文語音：請到「設定 > 時間與語言 > 語音 > 新增語音」安裝。",
        ["Windows TTS is unavailable: {0}"] = "無法使用 Windows TTS：{0}",
        ["Audio error: {0}"] = "音訊錯誤：{0}",
        ["Floating windows"] = "懸浮視窗", ["Timeline bars"] = "計時條", ["Center alerts"] = "中央字幕",
        ["Enabled"] = "啟用", ["Locked (no preview or editing)"] = "鎖定（關閉預覽與編輯）", ["Topmost"] = "置頂", ["Reset position"] = "重設位置",
        ["Locked windows are click-through and show no placeholder. Unlock to preview the window, drag it, or resize it from its edges."] =
            "鎖定時滑鼠穿透且不顯示預覽。解除鎖定後可預覽視窗、拖曳位置，或拖曳邊緣調整大小。",
        ["Monochrome colors"] = "單色顯示",
        ["Draw every bar in the colors below instead of the timers' colors from the encounter file. The dark and light themes each have their own colors."] =
            "所有計時條改用下方顏色，不使用戰鬥設定檔中各倒數的顏色。深色與淺色主題各有一組顏色。",
        ["Colors (dark theme)"] = "顏色（深色主題）", ["Colors (light theme)"] = "顏色（淺色主題）",
        ["Text"] = "文字", ["Text shadow"] = "文字陰影", ["Bar"] = "計時條", ["Track"] = "底色", ["Flash"] = "閃爍",
        ["Restore default colors"] = "恢復預設顏色",
        ["Max sound length"] = "單一音效最長播放",
        ["Each sound file or TTS phrase stops after this time, previews included."] = "每個音效檔或 TTS 語音播放超過此時間即截斷，預覽也一樣。",
        ["Text size"] = "文字大小", ["Background"] = "背景不透明度", ["Bars shown"] = "顯示條數",
        ["Alert duration"] = "字幕停留時間", ["Test alert"] = "測試字幕",
        ["Timeline (drag to move)"] = "計時條（可拖曳）", ["Alerts (drag to move)"] = "中央字幕（可拖曳）",
        ["Floating window failed: {0}"] = "懸浮視窗發生錯誤：{0}",
        ["Discord capture (timeline)"] = "Discord 擷取（時間軸）",
        ["For Discord streaming: choose the TipAura main window in Discord, and the stream shows the timeline bars on black instead of the main window. Nothing changes on your screen. Requires the timeline window to be shown."] =
            "Discord 直播用：在 Discord 選擇 TipAura 主視窗，直播畫面會是黑底的倒數時間軸，而不是主視窗。你的螢幕畫面不會改變。需要顯示時間軸視窗。",
        ["Discord capture failed: {0}"] = "Discord 擷取發生錯誤：{0}",
        ["Anchor"] = "錨點",
        ["The point of the hooked window (or of the primary screen without a hook) that the floating window keeps its distance from."] =
            "懸浮視窗以掛勾視窗（未使用掛勾時為主螢幕）的哪個位置為基準保持距離。",
        ["Top left"] = "左上", ["Top center"] = "正上", ["Top right"] = "右上",
        ["Middle left"] = "左中", ["Center"] = "正中", ["Middle right"] = "右中",
        ["Bottom left"] = "左下", ["Bottom center"] = "正下", ["Bottom right"] = "右下",
        ["Changing the anchor keeps the floating window in place."] = "切換錨點時保留懸浮視窗位置。",
        ["Window hook"] = "視窗掛勾", ["Window hooks"] = "視窗掛勾",
        ["Add window hooks on the Settings tab."] = "請在「設定」分頁新增視窗掛勾。",
        ["(unnamed)"] = "（未命名）",
        ["Floating windows follow the primary screen."] = "懸浮視窗以主螢幕為準。",
        ["Window not found; floating windows are hidden."] = "找不到視窗，懸浮視窗已隱藏。",
        ["Window found; floating windows follow it."] = "已找到視窗，懸浮視窗跟隨該視窗。",
        ["The hooked encounter file was not found: {0}"] = "找不到掛勾記憶的戰鬥設定檔：{0}",
        ["No window hooks. A hook anchors the floating windows to a game window and remembers its encounter file."] =
            "尚無視窗掛勾。掛勾會讓懸浮視窗以遊戲視窗為錨點，並記憶該視窗使用的戰鬥設定檔。",
        ["Match by"] = "比對方式", ["Process name / title"] = "程序名稱／標題",
        ["Process name"] = "程序名稱", ["Window title"] = "視窗標題",
        ["Process name: the executable name, with or without .exe. Window title: any part of the title. Case is ignored."] =
            "程序名稱：執行檔名稱，可含或不含 .exe。視窗標題：標題中的任一段文字。不分大小寫。",
        ["Loaded when this hook is selected on the Encounter tab. Loading another file while the hook is selected replaces it."] =
            "在「戰鬥」分頁選擇此掛勾時載入。選擇此掛勾期間載入其他檔案會取代記憶的檔案。",
        ["Add hook"] = "新增掛勾", ["Add from running window..."] = "從執行中的視窗新增…",
        ["No windows found."] = "找不到視窗。",
        ["Select the active hook on the Encounter tab."] = "在「戰鬥」分頁選擇使用中的掛勾。",
        ["Disable hotkeys unless the hooked window is focused"] = "掛勾視窗不在前景時停用所有快捷鍵",
        ["Hotkeys only work while the window of the selected window hook, or TipAura itself, is in the foreground. With no hook selected, they only work in TipAura."] =
            "只有在所選掛勾的視窗或 TipAura 本身位於前景時，快捷鍵才有效。未選擇掛勾時只在 TipAura 中有效。",
        ["Hotkeys only work while {0} or TipAura is focused."] = "只有在 {0} 或 TipAura 位於前景時快捷鍵才有效。",
        ["No window hook is selected: hotkeys only work while TipAura is focused."] = "未選擇視窗掛勾：快捷鍵只在 TipAura 位於前景時有效。",
        ["Interface"] = "介面", ["Font size"] = "字型大小", ["Theme"] = "主題", ["Dark"] = "深色", ["Light"] = "淺色",
        ["Language"] = "語言", ["Restart TipAura to change the language."] = "重新啟動 TipAura 以切換語言。",
        ["Open data folder"] = "開啟 data 資料夾", ["Open log folder"] = "開啟記錄檔資料夾", ["About"] = "關於",
        ["Could not open folder: {0}"] = "無法開啟資料夾：{0}",
        ["About TipAura"] = "關於 TipAura", ["Overview"] = "概覽", ["License"] = "授權", ["Third-party licenses"] = "第三方授權",
        ["Version {0}"] = "版本 {0}", ["Disclaimer"] = "免責聲明",
        ["Click to open in the browser"] = "點擊以在瀏覽器開啟", ["Could not open link: {0}"] = "無法開啟連結：{0}",
        ["TipAura is released under the MIT License. Bundled components keep their own licenses."] =
            "TipAura 以 MIT 授權釋出，隨附元件依其各自的授權條款。",
        ["TipAura is an unofficial tool. It is not developed, endorsed or supported by any game developer or publisher. Game names and trademarks belong to their respective owners."] =
            "TipAura 為非官方工具，未經任何遊戲開發商或營運商開發、授權或支援。遊戲名稱與商標屬於其各自的權利人。",
        ["TipAura only listens to the keyboard to start its own timers; it does not modify the game, read its memory or send input to it. Users are responsible for determining whether its use complies with the game's terms of service and bear any resulting risk."] =
            "TipAura 僅監聽鍵盤以啟動自身的倒數，不修改遊戲、不讀取遊戲記憶體，亦不向遊戲發送任何操作輸入。使用者應自行確認使用方式是否符合遊戲服務條款，並自負相關風險。",
        ["Timer alerts depend on the encounter files and the user's key presses and may be late, early or missing. Use them for reference only."] =
            "倒數提示取決於戰鬥設定檔與使用者的按鍵操作，可能延遲、提前或遺漏，僅供參考。",
        ["Versions modified, redistributed or further developed by others are their own responsibility; the original author is not liable for any disputes or damages arising from them."] =
            "經他人修改、重新散布或二次開發的版本由其自行負責，原作者不對因此產生的任何爭議或損害負責。",
        ["TipAura is provided \"as is\", without warranty of any kind. The author shall not be liable for any damages arising from the use or inability to use this software."] =
            "TipAura 按「現狀」提供，不提供任何明示或暗示之擔保。對於因使用或無法使用本軟體所生之任何損害，作者概不負責。",
        ["Could not read file: {0}"] = "無法讀取檔案：{0}",
        ["YAML syntax error: {0}"] = "YAML 語法錯誤：{0}",
        ["The file must be a mapping with name and timers."] = "檔案必須是含有 name 與 timers 的對應表（mapping）。",
        ["No timers are defined."] = "沒有定義任何倒數。",
        ["timers must be a list."] = "timers 必須是清單。",
        ["Each timer must be a mapping."] = "每條倒數必須是對應表（mapping）。",
        ["Duplicate timer id '{0}'; the timer is skipped."] = "倒數 ID「{0}」重複，已略過此倒數。",
        ["Unknown field '{0}' is ignored."] = "未知欄位「{0}」已忽略。",
        ["{0} must be text; the value is ignored."] = "{0} 必須是文字，已忽略此值。",
        ["{0} must be a number."] = "{0} 必須是數字。",
        ["version {0} is newer than this TipAura supports ({1}); unknown fields are ignored."] = "version {0} 比此版 TipAura 支援的版本（{1}）新，未知欄位會被忽略。",
        ["version must be an integer from 1; the format is detected from the content."] = "version 必須是大於或等於 1 的整數，改依內容判斷格式。",
        ["Timer '{0}': sound sets both tts and sfx; using sfx."] = "倒數「{0}」：sound 同時設定了 tts 與 sfx，改用 sfx。",
        ["{0} must be a number; using the default."] = "{0} 必須是數字，改用預設值。",
        ["{0} must be true or false; using false."] = "{0} 必須是 true 或 false，改用 false。",
        ["A timer has no id; the timer is skipped."] = "有倒數缺少 id，已略過此倒數。",
        ["Timer '{0}': key must be an integer from 1 to 9, or a list of them."] = "倒數「{0}」：key 必須是 1 到 9 的整數，或由這些整數組成的清單。",
        ["Timer '{0}': key {1} is listed twice."] = "倒數「{0}」：key {1} 重複列出。",
        ["Timer '{0}': note is longer than {1} characters; the rest is cut off."] = "倒數「{0}」：note 超過 {1} 字，超出部分已截斷。",
        ["Timer '{0}': duration must be greater than 0 and at most 86400 seconds."] = "倒數「{0}」：duration 必須大於 0 且不超過 86400 秒。",
        ["Timer '{0}': max_instances must be an integer from 1 to 20; using 1."] = "倒數「{0}」：max_instances 必須是 1 到 20 的整數，改用 1。",
        ["Timer '{0}': on_limit must be replace_oldest, ignore or reset; using replace_oldest."] =
            "倒數「{0}」：on_limit 必須是 replace_oldest、ignore 或 reset，改用 replace_oldest。",
        ["Timer '{0}': warn_before must be 0 or more seconds; using 0."] = "倒數「{0}」：warn_before 必須大於或等於 0 秒，改用 0。",
        ["Timer '{0}': warn_before is longer than duration; the alert fires when the timer starts."] =
            "倒數「{0}」：warn_before 比 duration 長，提示會在倒數開始時立即觸發。",
        ["Timer '{0}': color must be #RRGGBB or #RRGGBBAA; using the default."] = "倒數「{0}」：color 必須是 #RRGGBB 或 #RRGGBBAA，改用預設顏色。",
        ["Timer '{0}': each sound must be a mapping with tts or sfx; it is ignored."] = "倒數「{0}」：每個音效必須是含 tts 或 sfx 的對應表，已忽略。",
        ["Timer '{0}': offset must be from -86400 to 86400 seconds; using 0."] = "倒數「{0}」：offset 必須介於 -86400 到 86400 秒，改用 0。",
        ["Timer '{0}': more than {1} sounds; the rest are ignored."] = "倒數「{0}」：音效超過 {1} 個，其餘已忽略。",
        ["Timer '{0}': a sound offset falls outside the countdown; it plays at the start or end."] =
            "倒數「{0}」：音效偏移超出倒數範圍，將於倒數開始或結束時播放。",
        ["Timer '{0}': invalid path '{1}'."] = "倒數「{0}」：無效的路徑「{1}」。",
        ["Timer '{0}': unsupported file type '{1}'; supported: {2}."] = "倒數「{0}」：不支援的檔案類型「{1}」；支援：{2}。",
        ["Timer '{0}': unknown Lucide icon '{1}'."] = "倒數「{0}」：未知的 Lucide 圖示「{1}」。",
        ["Timer '{0}': file not found: {1}"] = "倒數「{0}」：找不到檔案：{1}",
    };
}
