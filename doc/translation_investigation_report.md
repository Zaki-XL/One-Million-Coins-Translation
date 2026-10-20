# One Million Coins 翻訳技術調査・実装報告書

## 1. 調査概要
- **対象ゲーム**: One Million Coins (Steam版)
- **対象ディレクトリ**: `C:\Program Files (x86)\Steam\steamapps\common\One Million Coins`
- **目的**: ゲーム本体を一切改変しない「先行インジェクション（Doorstop方式）」による多言語翻訳システムの設計・実装

---

## 2. システム・アーキテクチャ解析結果

### 2.1 ゲームエンジンおよび実行基盤
- **エンジン**: Unity 6000.6.0f1 (Unity 6 / 64-bit)
- **スクリプティングバックエンド**: **Mono** (`MonoBleedingEdge\EmbedRuntime\mono-2.0-bdwgc.dll`)
- **主要アセンブリ**: `One Million Coins_Data\Managed\Assembly-CSharp.dll` (サイズ: 約986KB)
- **評価**: IL2CPPではなくMono版であるため、C#のクラス・メソッド・プロパティがそのまま維持されており、逆コンパイル・解析・フック・パッチが非常に容易な構造です。

### 2.2 テキストデータの所在
ゲーム内テキストは外部ファイルではなく、**C#ソースコード内に直接ハードコード**されています。
- **主要ネームスペース**: `GoldDigger`
- **アイテム定義**: `GoldDigger.ItemCatalog.ForFloor` (アイテム33種)
- **アップグレード定義**: `GoldDigger.UpgradeCatalog.CreateDefaults` (アップグレード7種)
- **UIテキスト**: `GoldDigger.VaultUI`, `GoldDigger.MainMenuUI`, `GoldDigger.ShopTerminal` 等

### 2.3 UIフレームワークとフォント仕様
- **UI方式**: Unity IMGUI (`OnGUI`, `GUIStyle`, `UnityEngine.Font`)
- **フォントロード処理**: `VaultUI.Ensure()` 内で欧文フォント（Rajdhani, Bangers）がロードされる。
- **解決策**: Unity標準の `Font.CreateDynamicFontFromOSFont` により、OS標準日本語フォント（Meiryo / Yu Gothic等）を動的生成して差し替え可能。

---

## 3. 実装した翻訳システム（Doorstop方式）

ゲーム本体のファイルを1バイトも書き換えることなく、ゲーム起動時に先行割り込みを行うシステムを構築・ビルドしました。

### 3.1 ディレクトリ構成
```text
One Million Coins/
├── winhttp.dll                     # Doorstop x64 プロキシDLL (ゲーム起動時にOSが先行ロード)
├── doorstop_config.ini             # Doorstop設定ファイル
└── Translation/
    ├── TranslationLoader.dll       # ゲーム起動直後に実行されるマネージドDLL
    ├── 0Harmony.dll                # メソッドフックライブラリ
    ├── fonts/
    │   └── font_config.json        # フォント設定（OSフォント候補 Meiryo/Yu Gothic 等）
    └── locales/
        ├── config.json             # 言語設定 ("language": "ja")
        ├── en.json                 # 英語原文辞書 (抽出済み全33アイテム+7アップグレード)
        └── ja.json                 # 日本語辞書 (翻訳済み全アイテム・アップグレード・UI)
```

### 3.2 処理シーケンス
1. ゲーム起動時、OSが `winhttp.dll` を先行ロード。
2. Monoランタイム初期化直後に `Doorstop.Entrypoint.Start()` 経由で `TranslationLoader.dll` が実行される。
3. `Translation/locales/config.json` を参照し、指定された言語辞書（`ja.json`）をロード。
4. Harmony により以下のメソッドにフックを注入：
   - `GoldDigger.VaultUI.Ensure`: 日本語フォント（Meiryo等）を `_regular`, `_bold`, `_display` に差し替え。
   - `GoldDigger.ItemCatalog.ForFloor`: アイテム名および説明文を辞書引きして日本語に差し替え。
   - `GoldDigger.UpgradeCatalog.CreateDefaults`: アップグレード説明文を辞書引きして日本語に差し替え。
5. 通常のゲームロードが再開され、完全な日本語UI・テキストでゲームが開始される。

---

## 4. 成果物一覧
- **ビルド成果物**: `bin/Release/`
  - `winhttp.dll`, `doorstop_config.ini`
  - `Translation/TranslationLoader.dll`, `Translation/0Harmony.dll`
  - `Translation/locales/` (en.json, ja.json, config.json)
  - `Translation/fonts/font_config.json`
  - `install.bat`, `uninstall.bat` (ワンクリック導入・削除スクリプト)
- **ソースコード**: `src/TranslationLoader.cs`
