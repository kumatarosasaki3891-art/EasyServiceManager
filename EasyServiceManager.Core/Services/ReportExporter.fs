namespace EasyServiceManager.Services

open System
open System.Net
open System.Runtime.InteropServices
open System.Text
open EasyServiceManager.Models

/// <summary>サービス一覧およびセキュリティ診断結果を Markdown / HTML レポートへ出力するモジュール。</summary>
module ReportExporter =
    /// <summary>Markdown テーブルセル向けに改行やパイプ文字をエスケープします。</summary>
    let private escapeMarkdown (value: string) =
        if String.IsNullOrEmpty value then ""
        else value.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ")

    /// <summary>HTML 出力向けに特殊文字をエスケープします。</summary>
    let private escapeHtml (value: string) =
        if String.IsNullOrEmpty value then ""
        else WebUtility.HtmlEncode(value)

    /// <summary>サービスのサマリー件数（総数・起動中・停止中・お気に入り）を集計します。</summary>
    let private summarizeServices (services: ServiceEntry list) =
        let total = services.Length
        let running = services |> List.filter (fun s -> s.Status.StartsWith("起動中", StringComparison.Ordinal)) |> List.length
        let stopped = services |> List.filter (fun s -> s.Status.StartsWith("停止中", StringComparison.Ordinal)) |> List.length
        let favorites = services |> List.filter (fun s -> s.IsFavorite) |> List.length
        total, running, stopped, favorites

    /// <summary>診断結果とサービス一覧を含む Markdown レポート文字列を生成します。</summary>
    let buildMarkdownReport
        (languageCode: string)
        (services: ServiceEntry seq)
        (findings: SecurityFinding seq)
        (scoreText: string)
        (gradeLabel: string)
        (diffMessages: string seq)
        (hasPreviousScan: bool)
        =
        let t key = Localization.translate languageCode key
        let serviceList =
            services
            |> Seq.sortBy (fun s -> (not s.IsDiff, not s.IsFavorite, not (s.Status.StartsWith("起動中", StringComparison.Ordinal)), s.Name))
            |> Seq.toList
        let findingList = findings |> Seq.toList
        let diffList = diffMessages |> Seq.toList
        let total, running, stopped, favorites = summarizeServices serviceList
        let warningCount = findingList |> List.filter (fun f -> f.Level = FindingLevel.Warning) |> List.length
        let nowText = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")

        let sb = StringBuilder()
        sb.AppendLine(sprintf "# %s - %s" (t "簡単サービス診断") (t "レポート出力")) |> ignore
        sb.AppendLine() |> ignore
        sb.AppendLine(sprintf "- **Generated:** %s" nowText) |> ignore
        sb.AppendLine(sprintf "- **Host:** %s" Environment.MachineName) |> ignore
        sb.AppendLine(sprintf "- **OS:** %s" RuntimeInformation.OSDescription) |> ignore
        sb.AppendLine() |> ignore

        sb.AppendLine(sprintf "## %s" (t "診断結果")) |> ignore
        sb.AppendLine() |> ignore
        sb.AppendLine(sprintf "> %s" (t "ローカル設定の簡易確認です。脆弱性スキャンではありません。")) |> ignore
        sb.AppendLine() |> ignore
        sb.AppendLine(sprintf "- **Score:** %s (%s)" scoreText gradeLabel) |> ignore
        sb.AppendLine(sprintf "- **Warnings:** %s" (Localization.format languageCode "{0} 件の警告" [| box warningCount |])) |> ignore
        sb.AppendLine() |> ignore
        sb.AppendLine(sprintf "| | %s | %s |" (t "診断項目") (t "状態")) |> ignore
        sb.AppendLine("| --- | --- | --- |") |> ignore
        for f in findingList do
            sb.AppendLine(sprintf "| %s | %s | %s |" f.Icon (escapeMarkdown f.Title) (escapeMarkdown f.Detail)) |> ignore
        sb.AppendLine() |> ignore

        if not diffList.IsEmpty || hasPreviousScan then
            sb.AppendLine(sprintf "## %s" (t "前回スキャンとの差分")) |> ignore
            sb.AppendLine() |> ignore
            if not diffList.IsEmpty then
                for msg in diffList do
                    sb.AppendLine(sprintf "- ⚠ %s" (escapeMarkdown (Localization.translateText languageCode msg))) |> ignore
            else
                sb.AppendLine(sprintf "- %s" (t "前回から変化なし")) |> ignore
            sb.AppendLine() |> ignore

        sb.AppendLine(sprintf "## %s" (t "サービス一覧")) |> ignore
        sb.AppendLine() |> ignore
        sb.AppendLine(sprintf "- **Total:** %d / **%s:** %d / **%s:** %d / **%s:** %d"
            total (t "起動中") running (t "停止中") stopped (t "お気に入り") favorites) |> ignore
        sb.AppendLine() |> ignore
        sb.AppendLine(sprintf "| %s | %s | %s | %s | %s | URL |"
            (t "状態") (t "サービス名") (t "表示名") (t "カテゴリ") (t "ポート")) |> ignore
        sb.AppendLine("| --- | --- | --- | --- | --- | --- |") |> ignore
        for s in serviceList do
            let favMark = if s.IsFavorite then " ★" else ""
            let diffMark = if s.IsDiff then sprintf " (%s)" (escapeMarkdown s.DiffNoteLabel) else ""
            let portText =
                if String.IsNullOrWhiteSpace s.Ports then "-"
                elif s.IsSharedProcess then sprintf "%s (%s)" s.PortLabel (t "システム共有プロセス")
                else s.PortLabel
            let urlText = if String.IsNullOrWhiteSpace s.Url then "-" else s.Url
            sb.AppendLine(
                sprintf "| %s %s | %s%s%s | %s | %s | %s | %s |"
                    s.StatusIcon
                    (escapeMarkdown s.StatusLabel)
                    (escapeMarkdown s.Name)
                    favMark
                    diffMark
                    (escapeMarkdown s.DisplayName)
                    (escapeMarkdown s.CategoryLabel)
                    (escapeMarkdown portText)
                    (escapeMarkdown urlText)) |> ignore

        sb.ToString()

    /// <summary>スマホやブラウザで閲覧しやすいスタンドアロン HTML レポート文字列を生成します。</summary>
    let buildHtmlReport
        (languageCode: string)
        (services: ServiceEntry seq)
        (findings: SecurityFinding seq)
        (scoreText: string)
        (gradeLabel: string)
        (diffMessages: string seq)
        (hasPreviousScan: bool)
        =
        let t key = Localization.translate languageCode key
        let serviceList =
            services
            |> Seq.sortBy (fun s -> (not s.IsDiff, not s.IsFavorite, not (s.Status.StartsWith("起動中", StringComparison.Ordinal)), s.Name))
            |> Seq.toList
        let findingList = findings |> Seq.toList
        let diffList = diffMessages |> Seq.toList
        let total, running, stopped, favorites = summarizeServices serviceList
        let warningCount = findingList |> List.filter (fun f -> f.Level = FindingLevel.Warning) |> List.length
        let nowText = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")

        let diffHtml =
            if not diffList.IsEmpty then
                let items =
                    diffList
                    |> List.map (fun msg -> sprintf "<li>%s</li>" (escapeHtml (Localization.translateText languageCode msg)))
                    |> String.concat ""
                sprintf """<div class="diff-box"><div class="diff-title">%s</div><ul>%s</ul></div>"""
                    (escapeHtml (Localization.format languageCode "⚠ 前回スキャンからの変化 ({0}件)" [| box diffList.Length |]))
                    items
            elif hasPreviousScan then
                sprintf """<div class="note">✓ %s</div>""" (escapeHtml (t "前回から変化なし"))
            else
                ""

        let findingsHtml = StringBuilder()
        for f in findingList do
            findingsHtml.AppendLine(
                sprintf """<div class="finding" style="border-left: 4px solid %s;"><div class="finding-icon">%s</div><div><div class="finding-title">%s</div><div class="finding-detail">%s</div></div></div>"""
                    (escapeHtml f.Color)
                    (escapeHtml f.Icon)
                    (escapeHtml f.Title)
                    (escapeHtml f.Detail)) |> ignore

        let rowsHtml = StringBuilder()
        for s in serviceList do
            let favBadge = if s.IsFavorite then "<span class=\"badge fav\">★</span>" else ""
            let isRunning = s.Status.StartsWith("起動中", StringComparison.Ordinal)
            let statusKey = if isRunning then "running" else "stopped"
            let diffKey = if s.IsDiff then "true" else "false"
            let rowClass = if s.IsDiff then "diff-row" else ""
            let diffBadge =
                if s.IsDiff then sprintf "<div class=\"diff-badge\">%s</div>" (escapeHtml s.DiffNoteLabel)
                else ""
            let portHtml =
                if String.IsNullOrWhiteSpace s.Ports then "<span class=\"muted\">-</span>"
                elif s.IsSharedProcess then
                    sprintf "<div>%s</div><div class=\"shared-note\">%s</div>"
                        (escapeHtml s.PortLabel)
                        (escapeHtml (t "システム共有プロセス"))
                else escapeHtml s.PortLabel
            let urlHtml =
                if String.IsNullOrWhiteSpace s.Url then "<span class=\"muted\">-</span>"
                else sprintf "<a href=\"%s\" target=\"_blank\" rel=\"noopener\">%s</a>" (escapeHtml s.Url) (escapeHtml s.Url)
            rowsHtml.AppendLine(
                sprintf """<tr class="%s" data-status="%s" data-category="%s" data-diff="%s"><td>%s %s</td><td><strong>%s</strong> %s<div class="sub">%s</div>%s</td><td>%s</td><td>%s</td><td>%s</td></tr>"""
                    rowClass
                    statusKey
                    (escapeHtml s.Category)
                    diffKey
                    (escapeHtml s.StatusIcon)
                    (escapeHtml s.StatusLabel)
                    (escapeHtml s.Name)
                    favBadge
                    (escapeHtml s.DisplayName)
                    diffBadge
                    (escapeHtml s.CategoryLabel)
                    portHtml
                    urlHtml) |> ignore

        let categories =
            serviceList
            |> Seq.map (fun s -> s.Category, s.CategoryLabel)
            |> Seq.distinctBy fst
            |> Seq.sortBy snd
            |> Seq.toList

        let categoryOptionsHtml = StringBuilder()
        categoryOptionsHtml.AppendLine(sprintf """<option value="">%s</option>""" (escapeHtml (t "すべてのカテゴリ"))) |> ignore
        for (catKey, catLabel) in categories do
            categoryOptionsHtml.AppendLine(sprintf """<option value="%s">%s</option>""" (escapeHtml catKey) (escapeHtml catLabel)) |> ignore

        let scoreBg = if warningCount > 0 then "#FFF0E8" else "#E8F4EE"
        let scoreFg = if warningCount > 0 then "#9C4512" else "#1D6B4B"

        sprintf """<!DOCTYPE html>
<html lang="%s">
<head>
<meta charset="utf-8" />
<meta name="viewport" content="width=device-width, initial-scale=1.0" />
<title>%s - %s</title>
<style>
  * { box-sizing: border-box; }
  body { margin: 0; font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, "Hiragino Sans", "Noto Sans JP", sans-serif; background: #F4F6F8; color: #18212B; line-height: 1.5; }
  .wrap { max-width: 1080px; margin: 0 auto; padding: 20px 16px 40px; }
  .header { background: #172331; color: #FFFFFF; border-radius: 14px; padding: 20px 22px; margin-bottom: 18px; }
  .header h1 { margin: 0 0 6px; font-size: 21px; }
  .meta { color: #B4C2D0; font-size: 12px; }
  .stats { display: grid; grid-template-columns: repeat(auto-fit, minmax(140px, 1fr)); gap: 10px; margin-bottom: 18px; }
  .stat { background: #FFFFFF; border: 1px solid #DDE4EA; border-radius: 10px; padding: 12px 14px; }
  .stat-label { font-size: 11px; color: #687887; }
  .stat-val { font-size: 22px; font-weight: bold; color: #172331; }
  .card { background: #FFFFFF; border: 1px solid #DDE4EA; border-radius: 12px; padding: 18px; margin-bottom: 18px; }
  .card h2 { margin: 0 0 8px; font-size: 18px; }
  .note { font-size: 12px; color: #687887; margin-bottom: 12px; }
  .score-box { background: %s; border-radius: 10px; padding: 14px 16px; display: flex; flex-wrap: wrap; align-items: center; gap: 14px; margin-bottom: 14px; }
  .grade { font-size: 28px; font-weight: bold; color: #172331; }
  .score { font-size: 15px; font-weight: 600; color: #4A5A6B; }
  .warn { font-size: 13px; font-weight: bold; color: %s; }
  .diff-box { background: #FEFCE8; border: 1px solid #FACC15; border-left: 4px solid #EAB308; border-radius: 8px; padding: 10px 12px; margin-bottom: 12px; color: #854D0E; font-size: 12px; }
  .diff-title { font-weight: bold; margin-bottom: 4px; }
  .diff-box ul { margin: 0; padding-left: 18px; }
  .diff-row { background: #FEFCE8; }
  .diff-badge { font-size: 11px; color: #854D0E; background: #FEF08A; border: 1px solid #FACC15; display: inline-block; padding: 1px 6px; border-radius: 4px; margin-top: 3px; font-weight: 600; }
  .finding { display: flex; gap: 10px; background: #FAFBFC; border: 1px solid #E3E8ED; border-radius: 8px; padding: 10px 12px; margin-bottom: 8px; }
  .finding-icon { font-size: 16px; }
  .finding-title { font-size: 13px; font-weight: bold; }
  .finding-detail { font-size: 12px; color: #5C6B7A; white-space: pre-line; }
  .filter-bar { display: flex; flex-direction: column; gap: 10px; margin-bottom: 14px; }
  .search { width: 100%%; padding: 10px 12px; border: 1px solid #CFD8E0; border-radius: 8px; font-size: 14px; }
  .filter-controls { display: flex; flex-wrap: wrap; align-items: center; justify-content: space-between; gap: 10px; }
  .filter-checks { display: flex; flex-wrap: wrap; align-items: center; gap: 12px; }
  .filter-check { display: inline-flex; align-items: center; gap: 6px; font-size: 13px; font-weight: 500; cursor: pointer; user-select: none; color: #374754; }
  .filter-check input { width: 16px; height: 16px; cursor: pointer; accent-color: #217A66; }
  .filter-select { padding: 8px 12px; border: 1px solid #CFD8E0; border-radius: 8px; font-size: 13px; background: #FFFFFF; color: #18212B; cursor: pointer; min-width: 140px; }
  .table-wrap { overflow-x: auto; }
  table { width: 100%%; border-collapse: collapse; font-size: 13px; }
  th, td { text-align: left; padding: 9px 10px; border-bottom: 1px solid #E8EDF2; vertical-align: top; }
  th { background: #F5F7F9; color: #495969; font-size: 12px; position: sticky; top: 0; }
  .sub { font-size: 11px; color: #6C7C8C; }
  .shared-note { font-size: 11px; color: #8A6413; background: #FFF6DD; display: inline-block; padding: 1px 6px; border-radius: 4px; margin-top: 2px; }
  .badge.fav { color: #D89B22; }
  .muted { color: #9AA7B4; }
  a { color: #217A66; text-decoration: none; }
</style>
</head>
<body>
<div class="wrap">
  <div class="header">
    <h1>%s - %s</h1>
    <div class="meta">%s | %s | %s</div>
  </div>
  <div class="stats">
    <div class="stat"><div class="stat-label">%s</div><div class="stat-val">%d</div></div>
    <div class="stat"><div class="stat-label">%s</div><div class="stat-val">%d</div></div>
    <div class="stat"><div class="stat-label">%s</div><div class="stat-val">%d</div></div>
    <div class="stat"><div class="stat-label">%s</div><div class="stat-val">%d</div></div>
  </div>
  <div class="card">
    <h2>%s</h2>
    <div class="note">%s</div>
    <div class="score-box">
      <div class="grade">%s</div>
      <div class="score">%s</div>
      <div class="warn">%s</div>
    </div>
    %s
    %s
  </div>
  <div class="card">
    <h2>%s</h2>
    <div class="filter-bar">
      <input id="filterBox" class="search" type="search" placeholder="%s" oninput="filterRows()" />
      <div class="filter-controls">
        <div class="filter-checks">
          <label class="filter-check">
            <input type="checkbox" id="runningOnly" onchange="filterRows()" />
            <span>%s</span>
          </label>
          <label class="filter-check">
            <input type="checkbox" id="diffOnly" onchange="filterRows()" />
            <span>%s</span>
          </label>
        </div>
        <select id="categoryFilter" class="filter-select" onchange="filterRows()">
          %s
        </select>
      </div>
    </div>
    <div class="table-wrap">
      <table id="svcTable">
        <thead><tr><th>%s</th><th>%s</th><th>%s</th><th>%s</th><th>URL</th></tr></thead>
        <tbody>%s</tbody>
      </table>
    </div>
  </div>
</div>
<script>
function filterRows() {
  var q = (document.getElementById('filterBox').value || '').toLowerCase();
  var runningOnly = document.getElementById('runningOnly').checked;
  var diffOnly = document.getElementById('diffOnly').checked;
  var cat = document.getElementById('categoryFilter').value;
  var rows = document.querySelectorAll('#svcTable tbody tr');
  for (var i = 0; i < rows.length; i++) {
    var row = rows[i];
    var status = row.getAttribute('data-status') || '';
    var category = row.getAttribute('data-category') || '';
    var isDiff = row.getAttribute('data-diff') === 'true';
    var txt = row.textContent.toLowerCase();
    var matchQ = !q || txt.indexOf(q) >= 0;
    var matchRunning = !runningOnly || status === 'running';
    var matchDiff = !diffOnly || isDiff;
    var matchCat = !cat || category === cat;
    row.style.display = (matchQ && matchRunning && matchDiff && matchCat) ? '' : 'none';
  }
}
</script>
</body>
</html>"""
            (escapeHtml languageCode)
            (escapeHtml (t "簡単サービス診断"))
            (escapeHtml (t "レポート出力"))
            scoreBg
            scoreFg
            (escapeHtml (t "簡単サービス診断"))
            (escapeHtml (t "レポート出力"))
            (escapeHtml nowText)
            (escapeHtml Environment.MachineName)
            (escapeHtml RuntimeInformation.OSDescription)
            (escapeHtml (t "すべて"))
            total
            (escapeHtml (t "起動中"))
            running
            (escapeHtml (t "停止中"))
            stopped
            (escapeHtml (t "お気に入り"))
            favorites
            (escapeHtml (t "診断結果"))
            (escapeHtml (t "ローカル設定の簡易確認です。脆弱性スキャンではありません。"))
            (escapeHtml gradeLabel)
            (escapeHtml scoreText)
            (escapeHtml (Localization.format languageCode "{0} 件の警告" [| box warningCount |]))
            diffHtml
            (findingsHtml.ToString())
            (escapeHtml (t "サービス一覧"))
            (escapeHtml (t "サービス名・表示名で検索"))
            (escapeHtml (t "起動中のみ表示"))
            (escapeHtml (t "差分ありのみ表示"))
            (categoryOptionsHtml.ToString())
            (escapeHtml (t "状態"))
            (escapeHtml (t "サービス名"))
            (escapeHtml (t "カテゴリ"))
            (escapeHtml (t "ポート"))
            (rowsHtml.ToString())
