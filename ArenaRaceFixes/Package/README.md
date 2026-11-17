# ArenaRaceFixes

2026 年 11 月 5 日のゲームの更新で動かなくなった、REPO_JP さんのアリーナレース用 MOD を直す MOD です。
元の MOD はそのまま入れておき、この MOD を一緒に入れます。**ホストだけ**が入れれば効きます。

| 直す MOD | 更新後の症状 | この MOD で |
|---|---|---|
| ArenaRaceMultiplayerSpawnFix 4.0.2 | 並べ直しが効かない。全員の PC で `RPC method 'SpawnRPC' ... has wrong parameters` のエラーが出る(MOD のログには「Applied」と出るので気づきにくい) | 並べ直しを今のゲームの形で送る |
| ArenaRaceCosmeticBoxTrap 4.0.0 | コスメボックスに触れても爆発しない。`Failure: TryExplodeFromCollider` の警告(MissingMethodException)がログに出続ける | 触れたら爆発する |

- 元の MOD が入っていなければ、その分は何もしません。
- 元の MOD の並べ方(ゲームの開始位置から 5 m 上・10 m 後ろ)は更新前のゲーム用で、今のゲームではスタートの部屋の外になってしまいます。そのため既定では並べ直さず、ゲームの開始位置のままにします(スタートのブーストは効きます)。
- 元の MOD の設定(並べる人数、スタートのブースト、ボックスの出る確率・大きさなど)は、そのまま使われます。
- ArenaRaceSpeedBoost 4.0.0 は今のゲームでもそのまま動くので、直していません。

## 設定(`BepInEx\config\shunm.ArenaRaceFixes.cfg`)
| 項目 | 既定 | 内容 |
|---|---|---|
| ArenaRaceMultiplayerSpawnFix を直す | ON | |
| レースの開始位置 | Game | Game = ゲームの開始位置のまま(おすすめ)、Spread = ゲームの開始位置で、重なった人だけ横にずらす、Original = 元の MOD の並べ方 |
| ArenaRaceCosmeticBoxTrap を直す | ON | |

## ログ
- 起動時に「〜を今のゲームに合わせて直しました」と出れば、修正が効いています。
- 「〜の版が想定と違うので直しません」と出たら、元の MOD が更新されています。元の MOD だけで動くか確かめて、動くならこの MOD は外してください。
- ArenaRaceMultiplayerSpawnFix の起動時の `PlayerAvatar.SpawnRPC method was not found` の警告は元の MOD が出すもので、この MOD を入れれば問題ありません。

## 動作確認
- [ ] 2 人以上でアリーナレース: 並べ直しの「SpawnRPC ... wrong parameters」のエラーが出ない・格子に並ぶ
- [ ] コスメボックスにカートで触れて爆発する

## 更新履歴
- 0.2.0: 並べ直すと開始位置がスタートの部屋の外になるので、既定ではゲームの開始位置のままにする(設定「レースの開始位置」)
- 0.1.0: 最初の版
