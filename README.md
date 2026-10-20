[English](README_EN.md) | 日本語

# One Million Coins 多言語翻訳システム (Doorstop方式)

Steamゲーム「One Million Coins」のゲーム本体ファイルを一切改変しない、Doorstop方式による多言語翻訳システムです。  
日本語（デフォルト）、簡体字中国語、韓国語、フランス語に対応しています。

---

## 特徴
- **本体非改変**: ゲーム本体の exe や dll、assets を1バイトも書き換えません。
- **先行インジェクション**: Windows の DLL プロキシ機構（Doorstop `winhttp.dll`）により、ゲーム起動時に最優先で割り込み、フォントとテキストを動的置換します。
- **YAMLネイティブ辞書**: 人間が読みやすく編集しやすい YAML 形式の辞書を採用。文字サイズ縮小（scale）や翻訳の個別ON/OFF（enabled）が柔軟に設定可能です。
- **アトラス保護・ゼロアロケーション最適化**: Unity IMGUI 特有のフォントテクスチャアトラス破損を事前ウォームアップで防止し、描画負荷（GC Alloc）を極小化しています。
- **多言語対応**: 言語設定（`config.json`）の切り替えにより、各言語の辞書とOS標準フォント（Yu Gothic UI, Microsoft YaHei, Malgun Gothic, Segoe UI 等）が自動適用されます。
- **ワンクリック導入・削除**: ゲームフォルダへの配置・削除だけで、いつでも安全に導入・完全原状復帰が可能です。

---

## 使い方

### 導入（インストール）
1. `bin/Release/` 内の全ファイル（`winhttp.dll`, `doorstop_config.ini`, `Translation/`）を、ゲームインストールフォルダ（`One Million Coins.exe` がある場所）にコピーします。
2. ゲームを通常通り Steam から起動します。自動的に翻訳が適用されて開始します。

### 言語の切り替え
`Translation/locales/config.json` 内の `"language"` 設定を変更することで、言語を切り替えることができます。
```json
{
  "language": "ja"
}
```
- `"ja"`: 日本語
- `"zh"`: 简体中文 (簡体字中国語)
- `"ko"`: 한국어 (韓国語)
- `"fr"`: Français (フランス語)

### 削除（アンインストール）
ゲームフォルダから `winhttp.dll`, `doorstop_config.ini`, `Translation` フォルダを削除するだけで、完全に元の状態に戻ります。

---

## 翻訳辞書（YAML）の仕様とカスタマイズ方法

辞書ファイルは `Translation/locales/<言語コード>.yaml`（例: `ja.yaml`）に配置されています。  
テキストエディタ等で編集し、ゲームを再起動するだけで即座に変更が反映されます（DLLの再ビルドは不要です）。

### 1. セクション構成
辞書は以下の 6 つのセクションで構成されています。
- **`meta`**: 言語コードと表示名（例: `language: "ja"`, `display_name: "日本語"`）
- **`branches`**: スキルツリーの全 7 ブランチ名・ブランチ説明文（`0` 〜 `6`）
- **`upgrades`**: 7 種類の基本ステータスアップグレード名・説明文
- **`items`**: 全 18 種類のアイテム（装備品・ツール）名・説明文
- **`ui`**: メインメニュー、設定、操作設定、ランキング、動的スタッツ等の全UIテキスト
- **`skills`**: 全 238 スキルの名称・詳細説明文

---

### 2. 指定可能なプロパティ

各エントリには、以下のプロパティを自由に設定できます。

| プロパティ | 型 | 初期値 | 説明 |
|---|:---:|:---:|---|
| **`text`** | string | - | 翻訳後の表示テキスト（UIエントリで使用）。 |
| **`name`** | string | - | アイテム名、スキル名、ブランチ名などの表示名称。 |
| **`description`** | string | - | 説明文テキスト。改行は `\n` または複数行構文（`\|`）で記述可能。 |
| **`enabled`** | bool | `true` | `false` にすると**そのエントリの翻訳のみが無効化**され、ゲーム本来の英語テキストで表示されます。 |
| **`scale`** | float | `1.0` | 文字サイズの縮小率（`0.01` 〜 `1.0`）。ボタン枠からのはみ出しを防止するために使用します。 |
| **`name_scale`** | float | `scale`値 | スキル名のみの文字サイズ縮小率。 |
| **`desc_scale`** | float | `scale`値 | 説明文のみの文字サイズ縮小率。 |

> [!TIP]
> **ボタン枠からのはみ出し防止 (`scale`)**:  
> `scale: 0.8` と指定すると、Unity の RichText（`<size=80%>...</size>`）が自動付与され、フォントサイズが 80% に縮小されます。文字数の多いボタンやタイトル画面のボタン調整に最適です。

---

### 3. 記述スタイルの使い分け

用途に応じて「1行短縮形式」と「ネスト形式」を自由に使い分けることができます。

#### A. 1行短縮形式（scale や enabled の変更が不要な場合）
```yaml
ui:
  "START": "スタート"
  "OPTIONS": "設定"
  "BUY": "購入"
```

#### B. ネスト形式（scale や enabled を指定したい場合）
キーの下に **4スペースでインデント** して属性を記述します。
```yaml
ui:
  # ボタン枠に収めるため 80% に縮小
  "Credits":
    text: "クレジット"
    enabled: true
    scale: 0.8

  # 特定の項目だけ英語のまま残したい場合
  "DEBUG_MENU":
    text: "デバッグメニュー"
    enabled: false

skills:
  1325:
    name: "高速ポンプ"
    description: "ポンプの間隔 -3秒。"
    scale: 0.95
```

---

## バージョンアップ時の挙動について
Steam でゲーム本体のアップデート（パッチやバージョンアップ）が配信された場合の互換性やメンテナンス手順については、別紙 **[VERSION_UPDATE_GUIDE.md](VERSION_UPDATE_GUIDE.md)** をご確認ください。

---

## ディレクトリ構成
```text
ONE MILLION COINS/
├── README.md                   # 本ドキュメント (日本語)
├── README_EN.md                # 英語版ドキュメント
├── VERSION_UPDATE_GUIDE.md     # バージョンアップ対応・互換性ガイド
├── data/
│   └── Translation/
│       ├── locales/
│       │   ├── config.json     # 言語設定ファイル ("language": "ja" 等)
│       │   ├── ja.yaml         # 日本語辞書
│       │   ├── zh.yaml         # 簡体字中国語辞書
│       │   ├── ko.yaml         # 韓国語辞書
│       │   └── fr.yaml         # フランス語辞書
│       └── fonts/              # カスタムフォント配置用フォルダ
├── doc/
│   └── reviewer_report.md      # 品質・セキュリティ監査報告書
└── src/
    └── TranslationLoader.cs    # C# ローダーソースコード
```
