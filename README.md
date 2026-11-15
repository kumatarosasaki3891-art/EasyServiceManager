# 簡単サービス診断

「このPCやサーバーで何が動いているか」を一覧で把握するための、サービス発見・構成可視化アプリです。主要サービスをカテゴリ別に表示し、基本的なローカルセキュリティ設定を確認します。

> サービスの起動・停止や脆弱性スキャンを行う管理ツールではありません。

## 主な機能

- OS上のサービスを検出し、カテゴリ・状態・待受ポートを表示
- サービス名・表示名・カテゴリで検索
- カテゴリ別およびお気に入り一覧で絞り込み
- サービスの詳細表示と、URLがあるサービスの既定ブラウザー起動
- カスタムサービスの登録
- Firewall、待受ポート、SSH、HTTPSポートの簡易診断
- Linux上のDocker・Podmanコンテナー設定の簡易確認
- 診断結果を目立たせた画面構成（診断結果を右上、サービス詳細を右下に表示）
- 日本語、英語、中国語（簡体字・繁体字）、韓国語、ベトナム語、ドイツ語、フランス語、スペイン語、イタリア語、ポルトガル語、ロシア語での表示

言語は診断結果パネル上部から切り替えられ、選択した言語は設定ファイルに保存されます。Windowsでは`%APPDATA%\EasyServiceManager\settings.json`、Linuxでは`~/.config/EasyServiceManager/settings.json`を使用します。

## サービス検出

### 対応OS

- Windows
- systemdを利用するLinux

ディストリビューション固有の設定を必要としない範囲で動作します。Linuxでは`systemctl`と`ss`を利用するため、これらのコマンドが必要です。

### 取得方法

| OS | サービス | 待受ポート |
| --- | --- | --- |
| Windows | Windows Service API（PowerShell経由） | TCP待受とプロセス情報（PowerShell経由） |
| Linux | `systemctl list-units --type=service --all` | `ss -lntp` |

Linuxではロード済みのsystemdサービスを一覧にします。インストール済みだがロードされていないunitファイルをすべて列挙するものではありません。待受ポートはプロセス名を使ってサービスに関連付けますが、Linuxの実行ファイルパスは現在取得していません。

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

サービス名の内部定義に基づいて分類します。未登録のサービスはOtherに表示されます。

### 状態表示

- 🟢 起動中
- 🔴 停止中
- ⚪ 不明

### URL起動

待受ポートを検出したWeb Server、Monitoring、AI / LLMカテゴリのサービスには、`localhost`を使ったURLを設定します。ポート443の場合はHTTPS、それ以外はHTTPとして扱います。これはポート番号から推測したURLであり、実際のWeb画面やTLS設定を検証するものではありません。

カスタムサービスはHTTPまたはHTTPSのURLを登録できます。URLが正しく設定されている場合、詳細画面から既定ブラウザーで開けます。

## セキュリティ診断

診断は現在のマシン上の設定情報を読み取るもので、設定変更や外部ホストへのスキャンは行いません。

### 確認項目

| 項目 | Linux | Windows |
| --- | --- | --- |
| Firewall | UFW / firewalld | Windows Firewallプロファイル |
| 要注意ポート | `ss`によるTCP待受 | WindowsのTCP待受 |
| SSH設定 | `sshd -T`の実効設定 | 未対応 |
| HTTPSポート | TCP/80・443の待受 | TCP/80・443の待受 |
| Docker / Podman | ローカルの稼働コンテナー、privileged設定、ポート公開 | 未対応 |

要注意ポートは3306、5432、6379、9200、5601、27017、5000です。これらが全インターフェースで待ち受けている場合に注意を表示します。待受が検出されても、外部から到達可能かどうかはFirewallやNATなどに依存します。

DockerはローカルのUnixソケット、Podmanはローカルモードで確認します。リモートDocker contextやリモートPodmanには接続しません。ランタイムへの接続や設定の取得ができない場合は「不明」と表示します。

### スコア

診断項目の警告に応じて100点から減点し、次の基準で評価します。

| 評価 | 点数 |
| --- | --- |
| A | 95～100 |
| B | 80～94 |
| C | 60～79 |
| D | 0～59 |

確認できなかった項目は問題なしとは扱わず、評価に「暫定」を付けます。診断は基本的な設定確認であり、脆弱性の有無や実際の外部到達性を保証しません。HTTPSポートの検出も、TLS設定や証明書の有効性を検証するものではありません。

## カスタムサービスとお気に入り

- カスタムサービスにはサービス名、カテゴリ、表示名、URLを登録できます。
- 検出済みサービスはお気に入りに登録し、「お気に入り」カテゴリで絞り込めます。
- カスタムサービスとお気に入りの状態はアプリの実行中のみ保持されます。アプリ終了後の保存には対応していません。

## レポート出力・QRコード転送

右下の「📤 レポート出力」ボタンから、診断結果とサービス一覧をエクスポートできます。

- **Markdown 保存 (`.md`)**
- **HTML 保存 (`.html`)**（検索フィルター付きの単一HTMLレポート）
- **📱 QRコード転送**: 同一Wi-Fi内のスマートフォンからローカルHTTPサーバー経由でHTMLレポートをダウンロードできます（有効期限3分・1対1セッション制限・ダウンロード完了後にQRコードを自動無効化）。

## 起動・ビルド方法

必要環境は.NET 10 SDKです。リポジトリのルートから実行します。

```powershell
dotnet run --project .\EasyServiceManager\EasyServiceManager.fsproj
```

ビルドのみ行う場合:

```powershell
dotnet build .\EasyServiceManager\EasyServiceManager.fsproj
```

### Microsoft Store / MSIX パッケージのビルド

Windows 10/11 SDK がインストールされた環境で、リポジトリルートから `build-msix.ps1` を実行します。

```powershell
# Microsoft Store 提出用 MSIX（未署名）
.\build-msix.ps1 -Mode Store

# ローカル検証用サイドロード MSIX（自己署名証明書付き）
.\build-msix.ps1 -Mode Sideload
```

## 対象外・未対応

- サービスの起動、停止、再起動などの管理操作
- 死活監視、通知、アラート、履歴保存
- SNMP、脆弱性スキャン
- Nessus、OpenVAS、Zabbix、Grafana、Prometheusの代替
- Windows上のSSH設定およびDocker / Podman診断
- カスタムサービスとお気に入りの永続化
