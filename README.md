# 簡単サービス診断（Easy Service Diagnostics）

[![Microsoft Store](https://img.shields.io/badge/Microsoft%20Store-Windows%2010%2F11-10b981)](https://apps.microsoft.com/detail/9mxt54r13d17?hl=ja&gl=JP&ocid=pdpshare)
[![Linux / CLI](https://img.shields.io/badge/Linux-GUI%20%26%20CLI%20(esd)-0284c7)](#ダウンロード--download)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)

[English](README_EN.md) | 日本語

「このPCやサーバーで何が動いているか」を1画面で把握するための、ローカル完結型のサービス発見・簡易セキュリティ診断アプリです。  
主要サービスをカテゴリ別に可視化し、Firewallや待受ポート・バインドアドレスの基本的なローカルセキュリティ設定を安全に点検します。

> A local-first, read-only service discovery and port exposure diagnostic tool for Windows and Linux (GUI & headless CLI `esd`)—100% offline with Markdown/HTML export and local Wi-Fi QR transfer.

> サービスの起動・停止や外部への脆弱性スキャンを行う管理ツールではありません。

---

## 📥 ダウンロード / Download

- **🪟 Windows 10 / 11版 (GUI)**: [Microsoft Store から無料で入手](https://apps.microsoft.com/detail/9mxt54r13d17?hl=ja&gl=JP&ocid=pdpshare)
- **🐧 Linux版 (Flatpakバンドル / CLI `esd`)**: GitHub Releases にて順次公開（下記「起動・ビルド方法」からソースビルドも可能です）

---

## 📸 画面イメージ / Screenshots

| メイン画面（カテゴリ・サービス一覧・診断スコア） | レポート出力 ＆ ローカルWi-Fi QRコード転送 | 転送後はQRコード無効 |
| :---: | :---: | :---: |
| <img width="440" alt="easy_service_check_top" src="https://github.com/user-attachments/assets/2c1d0121-ff74-40ac-9235-f5c692fd3491" /> | <img width="440" alt="easy_service_check_qr_transfer" src="https://github.com/user-attachments/assets/e7a3575a-7f31-467b-9586-23245f6e0b3d" /> | <img width="440" alt="easy_service_check_qr_transfer2" src="https://github.com/user-attachments/assets/cf435059-f94e-418f-8172-b718cdc5f1f4" /> |

| スマートフォン受信画面 | HTMLレポート表示結果 |
| :---: | :---: |
| <img width="230" alt="easy_service_receive" src="https://github.com/user-attachments/assets/8eeda4b5-22ea-4845-be58-35b264025c46" /> | <img width="230" alt="easy_service_receive_2" src="https://github.com/user-attachments/assets/4f8e1d45-490d-44fc-9412-b332ccbc9012" /> |

---

## 主な機能

- OS上のサービスを検出し、カテゴリ・状態・待受ポートを一覧表示
- サービス名・表示名・カテゴリでの高速検索
- 12カテゴリ別およびお気に入り一覧での絞り込み
- サービスの詳細表示と、URLがあるサービスの既定ブラウザー起動
- カスタムサービスの登録
- Firewall、要注意ポート、SSH、HTTPSポートの簡易診断（100点満点スコア）
- Linux上のDocker・Podmanコンテナー設定の簡易確認
- **レポート出力 ＆ スマホへのQRコード転送**（Markdown / HTML保存、同一Wi-Fi内ワンタイムQR転送）
- **CLI版（`esd`）**: GUI版と同一の診断エンジンを搭載したヘッドレスサーバー・cron監視用コマンド
- **12言語対応**: 日本語、英語、中国語（簡体字・繁体字）、韓国語、ベトナム語、ドイツ語、フランス語、スペイン語、イタリア語、ポルトガル語、ロシア語

言語は診断結果パネル上部から切り替えられ、選択した言語は設定ファイルに保存されます。Windowsでは `%APPDATA%\EasyServiceManager\settings.json`、Linuxでは `~/.config/EasyServiceManager/settings.json` を使用します。

---

## サービス検出

### 対応OS

- Windows 10 / 11
- systemd を利用する Linux

ディストリビューション固有の設定を必要としない範囲で動作します。Linuxでは `systemctl` と `ss` を利用するため、これらのコマンドが必要です。

### 取得方法

| OS | サービス | 待受ポート |
| --- | --- | --- |
| Windows | Windows Service API（PowerShell経由） | TCP待受とプロセス情報（PowerShell経由） |
| Linux | `systemctl list-units --type=service --all` | `ss -lntp` |

Linuxではロード済みの systemd サービスを一覧にします。インストール済みだがロードされていない unit ファイルをすべて列挙するものではありません。待受ポートはプロセス名を使ってサービスに関連付けますが、Linuxの実行ファイルパスは現在取得していません。

### カテゴリ

- Web Server
- Container
- Database
- SSH
- Monitoring
- Mail
- File Share
- Virtualization
- AI / LLM
- Cloud Agent
- Security
- Other

サービス名の内部定義に基づいて分類します。未登録のサービスは Other に表示されます。

### 状態表示

- 🟢 起動中
- 🔴 停止中
- ⚪ 不明

### URL起動

待受ポートを検出した Web Server、Monitoring、AI / LLM カテゴリのサービスには、`localhost` を使ったURLを設定します。ポート443の場合は HTTPS、それ以外は HTTP として扱います。これはポート番号から推測したURLであり、実際のWeb画面やTLS設定を検証するものではありません。

カスタムサービスは HTTP または HTTPS のURLを登録できます。URLが正しく設定されている場合、詳細画面から既定ブラウザーで開けます。

---

## セキュリティ診断

診断は現在のマシン上の設定情報を読み取るもので、設定変更や外部ホストへのスキャンは行いません。

### 確認項目

| 項目 | Linux | Windows |
| --- | --- | --- |
| Firewall | UFW / firewalld | Windows Firewallプロファイル |
| 要注意ポート | `ss` によるTCP待受 | WindowsのTCP待受 |
| SSH設定 | `sshd -T` の実効設定 | 未対応 |
| HTTPSポート | TCP/80・443の待受 | TCP/80・443の待受 |
| Docker / Podman | ローカルの稼働コンテナー、privileged設定、ポート公開 | 未対応 |

要注意ポートは `3306`、`5432`、`6379`、`9200`、`5601`、`27017`、`5000` です。これらが全インターフェース（`0.0.0.0` / `::`）で待ち受けている場合に注意を表示します。待受が検出されても、外部から到達可能かどうかは Firewall や NAT などに依存します。

Docker はローカルの Unix ソケット、Podman はローカルモードで確認します。リモート Docker context やリモート Podman には接続しません。ランタイムへの接続や設定の取得ができない場合は「不明」と表示します。

### スコア

診断項目の警告に応じて100点から減点し、次の基準で評価します。

| 評価 | 点数 |
| --- | --- |
| A | 95～100 |
| B | 80～94 |
| C | 60～79 |
| D | 0～59 |

確認できなかった項目は問題なしとは扱わず、評価に「暫定」を付けます。診断は基本的な設定確認であり、脆弱性の有無や実際の外部到達性を保証しません。HTTPSポートの検出も、TLS設定や証明書の有効性を検証するものではありません。

---

## 💻 CLI版（`esd`）— サーバー・cron監視対応

画面のない（ヘッドレス）Linuxサーバーでも、GUI版と同一の診断エンジン・同一のスコア基準で簡易診断を実行できる単一バイナリCLI（`esd`）を備えています。

```bash
# その場で即時診断
esd

# 診断を実行し、次回比較用のスナップショットを保存
esd scan --save-snapshot

# 前回スキャンからの変化（新規ポート公開・バインド変化など）を確認
esd diff

# 起動中サービスのみ絞り込み、HTMLレポートとして保存
esd scan --only-running --format html --output report.html

# cron定期監視用：要約のみ出力し、終了コード（0〜3）でアラート連携
esd scan --quiet --format text
```

### 終了コード（cron・外部監視連携用）

| 終了コード | 意味 |
| --- | --- |
| `0` | 警告なし（安全） |
| `1` | 注意レベルの警告あり |
| `2` | 危険レベルの警告あり（DBポート全開放など） |
| `3` | 診断実行自体に失敗 |

### 主なオプション

| オプション | 説明 |
| --- | --- |
| `--format text\|json\|markdown\|html` | 出力フォーマット（デフォルト: `text`） |
| `--output <path>` | ファイル出力（省略時は標準出力） |
| `--only-running` | 起動中サービスのみに絞り込み |
| `--category <name>` | 指定カテゴリのみ表示（GUI版と同一の名称） |
| `--save-snapshot [path]` | 今回の診断結果をスナップショットとして保存 |
| `--diff` | 保存時に前回スナップショットとの差分も表示 |
| `--no-color` | カラー表示を無効化（パイプ時は自動で無効） |
| `--quiet` | スコアと警告（要約）のみ出力 |

```bash
esd version              # バージョン表示
esd help                 # ヘルプ表示
esd diff                 # 前回スナップショットとの差分のみ表示
```

スナップショット保存先: `~/.local/share/easy-service-diagnosis/snapshots/snapshot.json`
