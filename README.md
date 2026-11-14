# REPO-Mods

[R.E.P.O.](https://store.steampowered.com/app/3241660/REPO/) 用の BepInEx 5 プラグイン(MOD)集です。
どれも**ホストだけが入れれば、MOD を入れていない参加者にも効く**ように作っています(LoadProgress を除く)。

> **English:** A collection of BepInEx 5 plugins for R.E.P.O. Most of them work when only the host installs them.
> - **FloorRoles** – pick a role (Carrier / Fighter / Special) in the truck before each level for stat rewards and role traits; per-player shop budgets and shop duels.
> - **MonsterTweaks** – heavy behaviour buffs for 27 monsters, plus per-monster stun immunity and move speed for all 31.
> - **DroneTweaks** – brings removed drones and orbs back to the shop and gives them new effects (revive, invincibility, valuable repair, item conversion).
> - **AIMonsters** – an Azure OpenAI model directs Robe, Loom and Cleanup Crew once they spot a player; break a big monster orb while holding it to summon a chatty ally Robe.
> - **LoadProgress** – shows every player's loading progress in multiplayer.
>
> Documentation is in Japanese. MIT licensed. Not on Thunderstore.

> **開発中です。** ゲーム内でまだ確かめていない機能があります。おかしな動きを見つけたら [Issues](https://github.com/26G2116/R.E.P.O.-Mods/issues) で教えてください。

## MOD の一覧
| MOD | 版 | 内容 | 入れる人 |
|---|---|---|---|
| [**FloorRoles**](FloorRoles/Package/README.md) | 0.3.6 | レベルごとに役職を選ぶ。ショップの所持金を個人ごとに分ける | ホストだけ |
| [**MonsterTweaks**](MonsterTweaks/Package/README.md) | 0.10.5 | モンスター 27 体を個別に強化する。全モンスター共通の「スタン無効」「移動速度倍率」 | ホストだけ |
| [**DroneTweaks**](DroneTweaks/Package/README.md) | 0.5.3 | 削除されたドローン・オーブを店に戻し、新しい効果をつける | ホストだけ |
| [**AIMonsters**](AIMonsters/Package/README.md) | 0.6.0 | Azure OpenAI の AI にローブ・ルーム・クリーンアップクルーを動かさせる。話せる味方のローブを呼べる | ホストだけ |
| [**LoadProgress**](LoadProgress/Package/README.md) | 0.1.0 | マルチプレイのロード中に、全員のロードの進み具合を 10 段階で表示する | 見たい人 |
| [Translation](Translation/README.md) | — | ほかの作者の MOD の英語を日本語にする翻訳表(XUnity.AutoTranslator 用) | 使いたい人 |

MOD の名前をクリックすると、それぞれの説明書を開けます。

### FloorRoles(階層毎選択型役職)
- ショップのあとのトラックで、ロールオーブをインベントリのどのスロットに入れるかで役職(運搬・戦闘・特殊)を選ぶ
- 出発時のダイスで、役職ごとのステータス(Stamina、Health、Extra Jump など)が 1〜3 個もらえる
- 次のレベルでは役職の特性が使える
  - 運搬「引っ越し用運搬ロボ」: 貴重品の衝撃を 1/2 で無効にする。貴重品を金袋に変える
  - 戦闘「避雷針」: 武器を掲げてチャージすると、次の攻撃が 2 回当たる
  - 特殊「ギャンブラー」: チャットの `/r` のダイスで、回復・お金・自爆が起きる
- ショップでは、チームのお金を人数分の金袋に分け、各自の所持金で買い物をする。同じ商品を取り合うと決闘になる

### MonsterTweaks
- エルサ・APEX プレデター・ローブ・クリーンアップクルー・リーパーなど 27 体の行動を強化する(敵ごとに ON / OFF・数値を設定できる)
- できるだけゲーム本来の効果(卵の爆発、ロールの杖、無重力の杖、本物の敵の分身など)を使うので、MOD なしの参加者の画面でも自然に見える

**注意**: 強化はかなり強めです。ホストが入れると、参加者は MOD を入れていなくても強化された敵と戦うことになります。
知らない人と遊ぶ部屋で使うときは、先に伝えてください。

### DroneTweaks
- ゲームから削除されたヒーラードローンと 5 種類のオーブを、店に並ぶ候補に戻す(アイテムごとに ON / OFF)
- ヒーラードローンで倒れた人を復活させる。不滅オーブの範囲内のプレイヤーは無敵になる
- フェザーオーブで貴重品の価値を回復する。マグネットオーブで貴重品をアイテムに変える
- ドローン・オーブの店の値段を変えられる

## 導入方法(遊ぶ人向け)
Releases はまだありません。zip は下の「ビルド」で作ります。

1. MOD マネージャー([Gale](https://github.com/Kesomannen/gale) または r2modman)で R.E.P.O. のプロファイルを作る
2. MOD マネージャーの「ローカル MOD のインポート」で、MOD の zip(`shunm-<MOD名>-<版>.zip`)を取り込む
3. そのプロファイルでゲームを起動する

- 設定ファイルは `BepInEx\config\shunm.<MOD名>.cfg` です。MOD マネージャーの設定画面からも変えられます。
- [REPOConfig](https://thunderstore.io/c/repo/p/REPOModding/REPOConfig/) を入れると、ゲーム中に設定を変えられます。

## ビルド(開発する人向け)
必要なもの: .NET SDK(`dotnet build` が使えること)、R.E.P.O. 本体、BepInEx を入れた Gale のプロファイル。

1. 各 `.csproj` の `GameDir`(ゲームのフォルダ)と `ProfileDir`(Gale のプロファイル)を自分の環境に合わせる
   (`dotnet build -p:GameDir=...` のように指定してもよい)
2. **Release** でビルドする → `dist\shunm-<MOD名>-<版>.zip` ができる。Gale の「ローカル MOD のインポート」で取り込む
3. 開発中は **Debug** でビルドする。Gale が取り込んだ MOD フォルダの DLL が自動で上書きされる
   - Gale で OFF にしているときや、まだ取り込んでいないときはコピーしない
   - ゲームを起動したままだと DLL のコピーに失敗するので、ビルド前にゲームを閉じる
4. ログ: `%APPDATA%\com.kesomannen.gale\repo\profiles\<プロファイル名>\BepInEx\LogOutput.log`

ゲームの DLL(Assembly-CSharp など)はゲームのフォルダから参照するだけで、このリポジトリには含めていません。
非公開のメンバーには BepInEx.AssemblyPublicizer でアクセスしています。

### Windows の「スマート アプリ コントロール」でビルドが止まるとき
`PublicizeTask` が「アプリケーション制御ポリシーによってこのファイルがブロックされました」で失敗する場合は、
一度ふつうにビルドできたあとなら、次のように前回作った公開化済みの DLL を使ってビルドできます。

```
dotnet build -c Release -p:UseCachedPublicized=true
```

ゲームがアップデートされたあとは古い DLL のままになるので、この方法は使わないでください。

### フォルダ構成
| フォルダ | 内容 |
|---|---|
| `FloorRoles/` | 役職を選ぶ MOD |
| `MonsterTweaks/` | モンスターを強化する MOD(`Enemies/` に敵ごとの処理。開発の記録: [Docs/monster_tweaks.md](Docs/monster_tweaks.md)) |
| `DroneTweaks/` | ドローン・オーブを変える MOD |
| `AIMonsters/` | Azure OpenAI の AI に敵を動かさせる MOD(仕様: [Docs/ai_monsters.md](Docs/ai_monsters.md)) |
| `LoadProgress/` | ロードの進み具合を表示する MOD(仕組み: [Docs/loading_flow.md](Docs/loading_flow.md)) |
| `RepoModTemplate/` | 新しい MOD を作るときのひな形(起動ログ・設定・Harmony パッチのサンプル) |
| `Translation/` | 日本語化の翻訳表 |
| `Tools/` | MOD の設定項目や英語の文を抜き出すスクリプト、上のビルド用の設定 |

各 MOD の `Package/` には、zip に入れる説明書(README.md)とアイコンがあります。

### 新しい MOD を作るとき
`RepoModTemplate` をコピーして、次の名前を変えます。
- `.csproj` のファイル名と `AssemblyName` / `RootNamespace`
- `Plugin.cs` の `PluginGuid` / `PluginName` / namespace

## ライセンス
[MIT License](LICENSE) です。自由に使用・改変・再配布できます(著作権表示とライセンス文は残してください)。

- R.E.P.O. は semiwork の作品です。この MOD 集は非公式のファンメイドで、semiwork とは関係ありません。
  ゲームのファイル(DLL・画像・音・モデル)は含んでいません。
- `Translation/` の翻訳表の原文(英語)は、それぞれの MOD の作者のものです。
