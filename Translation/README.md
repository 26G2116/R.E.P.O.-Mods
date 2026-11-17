# MOD の日本語化(XUnity.AutoTranslator 用)

「Repo MODs」「レース等」プロファイルの MOD の英語(と一部の中国語)を、XUnity.AutoTranslator で日本語に置き換えるための翻訳表。
ゲーム本体は公式の日本語(ゲームの設定で言語を日本語にする)、SharePermissions と DarkSpider-AdminMenu は MOD 自身の日本語を使う。

## 入れ方
1. Gale で `RTLC-XUnity_AutoTranslator` を入れる
2. 次の 2 つをプロファイルにコピーする
   (プロファイルの場所: Gale の「プロファイル」→ フォルダを開く。既定は
   `%APPDATA%\com.kesomannen.gale\repo\profiles\<プロファイル名>`)

   | このフォルダのファイル | コピー先 |
   |---|---|
   | `AutoTranslatorConfig.ini` | `BepInEx\config\`(すでにある場合は下の「設定」の項目だけ書き換える) |
   | `ja\Text\REPO_Mods_ja.txt` | `BepInEx\Translation\ja\Text\` |
3. ゲームの言語を日本語にして起動する

**注意**: XUnity を入れただけ(設定ファイルが既定のまま)だと、`Endpoint=GoogleTranslateV2`・`Language=en`・`FromLanguage=ja` になっています。
この状態では、画面の文(プレイヤー名やチャットを含む)が Google に送られ、さらに**日本語を英語に**訳してしまいます(自作の MOD の日本語も英語になる)。
必ず下の「設定」のとおりに書き換えてください。

## 設定(AutoTranslatorConfig.ini)
- `Endpoint=` を空にして、機械翻訳を使わない(プレイヤー名やチャットを外部に送らない)
- `Language=ja` / `FromLanguage=en`
- `EnableIMGUI=True`(LateJoinNow の部屋情報表示に必要)

## ゲーム中の操作(XUnity の機能)
- `Alt + T`: 翻訳の ON / OFF(元の英語を確認したいとき)
- `Alt + R`: 翻訳表の読み込み直し(ゲームを起動したまま翻訳表を直したとき)

## 翻訳表の書き方
- `英語=日本語`(左は画面に出る文と完全に一致させる。`=` を含む場合は `\=`)
- 数字や名前が入る文は正規表現: `r:"^Banned (.+)\.$"=$1 を BAN しました。`
- 設定画面(REPOConfig)の項目名は、設定のキーの大文字の区切りにスペースを入れた形で出る
  (例: `SpawnUpgradeItems` → `Spawn Upgrade Items`)
- 訳を作るときは `Tools\extract_binds.py`(設定項目)と `Tools\extract_literals.py`(画面の文の候補)で
  逆コンパイルした MOD から英語を抜き出した

## 対象の MOD
| MOD | 訳したもの |
|---|---|
| REPOConfig・MenuLib | 設定画面の部品 |
| BagExpander・Shop Items Spawn in Level・All Shop Items In Level・MentalistAttackModifier・RoboUnion・MorePlayerList(+ PingAddon)・Hunter Enhancements・RespawnNoiseControl・ReviveHeadInTruckOrExtractionPoint・SharedUpgradesPlus・REPOLib | 設定の項目名・画面の文 |
| REPO_JP さんのアリーナレース用 MOD(ArenaRaceMultiplayerSpawnFix・ArenaRaceCosmeticBoxTrap・ArenaRaceSpeedBoost)・ClownBeamTrackingEnhancements | 設定の項目名 |
| LateJoinNow | 設定の項目名・部屋情報の表示 |
| RepoAdminMenu(1.0.17) | 設定の項目名・メニュー・メッセージ |
| MoreUIPanel | 設定の項目名・移動モードの表示(中国語) |
| LateJoinSimple・DarkSpider-AdminMenu(今は無効) | 設定の項目名 |

## 訳していないもの
- MOD 名(REPOConfig の MOD 一覧)… 元の名前のほうが探しやすいため
- キーの名前(F6 など)、ping の表示(`[H] 45ms`)
- アイテム名など、MOD のアセット(バンドル)の中にある文 … 遊んでみて英語が残っていたら追加する
- ステータス名(Health・Stamina など)… 英語名のほうが分かりやすいため
- RepoAdminMenu のアイテム・貴重品・敵・マップの名前 … ゲーム内部の英語名がそのまま並ぶため
- DarkSpider-AdminMenu のメニューの中、SharePermissions … MOD 自身が日本語に対応している
- 文字のない MOD(UnlimitedOrbs・JapaneseTextInput・TF2Minigun)、自作の MOD(FloorRoles・ArenaGrandPrix など。最初から日本語)
- ArenaRaceCosmeticBoxTrap の「大きさの決め方」の値(Wide / CompactRarity)… 「Wide」が RepoAdminMenu の「横長」と同じ文で、別々に訳せないため
- 設定の説明文(REPOConfig で項目を選んだときに出る文)… 長くて MOD の更新で変わりやすいため

## メモ
- RepoAdminMenu は 1.0.17 から、MOD のフォルダの `Languages\<言語>.json` で翻訳できるようになった。
  ただし MOD のフォルダに置くと、MOD の更新で消える。そのため、この翻訳表(XUnity)で訳している
- MOD が更新されたら、逆コンパイルし直して `Tools\extract_binds.py` と `Tools\extract_literals.py` で前の版と比べ、増えた文を足す
