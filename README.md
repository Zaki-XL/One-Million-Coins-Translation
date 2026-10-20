[English](README_EN.md) | 日本語

# One Million Coins 日本語翻訳システム (Doorstop方式)

Steamゲーム「One Million Coins」のゲーム本体ファイルを一切改変しない、Doorstop方式による多言語翻訳システムです。

## 特徴
- **本体非改変**: ゲーム本体の exe や dll、assets を1バイトも書き換えません。
- **先行インジェクション**: Windows の DLL プロキシ機構（Doorstop `winhttp.dll`）により、ゲームロード時に最優先で割り込み、フォントとテキストを日本語に動的置換します。
- **多言語対応**: フォント（`fonts/`）と翻訳テキスト（`locales/`）が分離されており、JSON ファイルの追加・編集で多言語に対応可能です。
- **ワンクリック導入・削除**: `install.bat` と `uninstall.bat` でいつでも導入・完全原状復帰が可能です。

## 使い方

### 導入（インストール）
1. `bin/Release/` 内の全ファイルを、ゲームインストールフォルダにコピーします。（または `install.bat` を実行します）
2. ゲームを通常通り Steam から起動します。自動的に日本語化されて開始します。

### 削除（アンインストール）
1. `uninstall.bat` を実行するか、ゲームフォルダから `winhttp.dll`, `doorstop_config.ini`, `Translation` フォルダを削除するだけで、完全に元の状態に戻ります。

## ディレクトリ構成
```text
ONE MILLION COINS/
├── README.md
├── README_EN.md
├── data/
│   └── Translation/            # 言語辞書・フォント定義データ
├── src/
│   └── TranslationLoader.cs    # C# ローダーソースコード
```
