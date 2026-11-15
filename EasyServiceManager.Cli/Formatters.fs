namespace EasyServiceManager.Cli

open System
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Serialization
open EasyServiceManager.Models
open EasyServiceManager.Services

module Formatters =
    /// <summary>ANSIエスケープカラーをサポートするヘルパー。</summary>
    type ColorTheme(enabled: bool) =
        member _.Red(text: string) = if enabled then "\u001b[31m" + text + "\u001b[0m" else text
        member _.Yellow(text: string) = if enabled then "\u001b[33m" + text + "\u001b[0m" else text
        member _.Green(text: string) = if enabled then "\u001b[32m" + text + "\u001b[0m" else text
        member _.Cyan(text: string) = if enabled then "\u001b[36m" + text + "\u001b[0m" else text
        member _.Bold(text: string) = if enabled then "\u001b[1m" + text + "\u001b[0m" else text
        member _.Gray(text: string) = if enabled then "\u001b[90m" + text + "\u001b[0m" else text

    /// <summary>テキスト形式で診断結果を整形します。</summary>
    let formatText
        (color: ColorTheme)
        (isRoot: bool)
        (isLinux: bool)
        (scoreValue: int option)
        (grade: string)
        (findings: SecurityFinding list)
        (services: ServiceEntry seq)
        (diffMessages: string list)
        (hasPreviousScan: bool)
        (showDiffOnly: bool)
        (quiet: bool)
        : string =
        let sb = StringBuilder()

        // 1. 免責
        sb.AppendLine(color.Gray("※ 本診断はローカル設定の簡易確認です。脆弱性スキャンではありません。")) |> ignore
        sb.AppendLine() |> ignore

        // 2. 権限注記 (Linux で非root実行時)
        if isLinux && not isRoot then
            sb.AppendLine(color.Gray("注: 一部のプロセス名が取得できていないため、帰属は推定を含みます（sudo で実行すると精度が向上します）")) |> ignore
            sb.AppendLine() |> ignore

        // 3. 総合スコアと評価
        let warnings = findings |> List.filter (fun f -> f.Level = FindingLevel.Warning)
        let scoreStr =
            match scoreValue with
            | Some v -> sprintf "%d / 100" v
            | None -> "未評価"

        let gradeColored =
            if grade.StartsWith("A") then color.Green(grade)
            elif grade.StartsWith("B") then color.Cyan(grade)
            elif grade.StartsWith("C") then color.Yellow(grade)
            else color.Red(grade)

        let warningStr =
            if warnings.Length = 0 then color.Green("0 件の警告")
            else color.Yellow(sprintf "%d 件の警告" warnings.Length)

        sb.AppendLine(sprintf "🛡️  総合スコア: %s (グレード: %s)  [%s]" (color.Bold scoreStr) gradeColored warningStr) |> ignore
        sb.AppendLine(String.replicate 60 "─") |> ignore

        // 4. 前回との差分
        if hasPreviousScan then
            sb.AppendLine() |> ignore
            if diffMessages.Length > 0 then
                sb.AppendLine(color.Yellow(color.Bold(sprintf "⚠ 前回スキャンからの変化 (%d件):" diffMessages.Length))) |> ignore
                for msg in diffMessages do
                    sb.AppendLine(color.Yellow(sprintf "  • %s" msg)) |> ignore
            else
                sb.AppendLine(color.Gray("• 前回から変化なし")) |> ignore
            sb.AppendLine(String.replicate 60 "─") |> ignore

        if not showDiffOnly then
            // 5. 診断項目一覧
            sb.AppendLine() |> ignore
            sb.AppendLine(color.Bold("■ 診断項目:")) |> ignore
            for f in findings do
                let icon, line =
                    match f.Level with
                    | FindingLevel.Good ->
                        "✅", color.Green(sprintf "%s: %s" f.Title f.Detail)
                    | FindingLevel.Warning ->
                        let tag = if f.Deduction >= 25 then color.Red("[危険]") else color.Yellow("[警告]")
                        "⚠️", sprintf "%s %s: %s (-%d点)" tag (color.Bold f.Title) f.Detail f.Deduction
                    | FindingLevel.Unknown ->
                        "❓", color.Gray(sprintf "%s: %s" f.Title f.Detail)
                    | FindingLevel.Information ->
                        "ℹ️", color.Gray(sprintf "%s: %s" f.Title f.Detail)
                sb.AppendLine(sprintf "  %s %s" icon line) |> ignore

            // 6. サービス一覧 (--quiet のときは省略)
            if not quiet then
                let serviceList = services |> Seq.toList
                sb.AppendLine() |> ignore
                sb.AppendLine(color.Bold(sprintf "■ サービス一覧 (%d 件):" serviceList.Length)) |> ignore
                sb.AppendLine(color.Gray(sprintf "  %-10s %-28s %-16s %s" "状態" "サービス名" "カテゴリ" "ポート")) |> ignore
                sb.AppendLine(color.Gray("  " + String.replicate 68 "-")) |> ignore
                for s in serviceList do
                    let statusIcon = if s.Status = "起動中" then color.Green("● 起動中") else color.Gray("○ 停止中")
                    let diffBadge = if s.IsDiff then color.Yellow(" [差分]") else ""
                    let portsStr = if String.IsNullOrWhiteSpace s.Ports then "-" else s.Ports
                    sb.AppendLine(sprintf "  %-18s %-28s %-16s %s%s" statusIcon s.Name s.Category portsStr diffBadge) |> ignore

        sb.ToString().TrimEnd()

    /// <summary>JSON形式の出力モデル。</summary>
    type JsonOutput =
        { [<JsonPropertyName("disclaimer")>]
          Disclaimer: string
          [<JsonPropertyName("timestamp")>]
          Timestamp: string
          [<JsonPropertyName("os")>]
          Os: string
          [<JsonPropertyName("isRoot")>]
          IsRoot: bool
          [<JsonPropertyName("score")>]
          Score: int option
          [<JsonPropertyName("grade")>]
          Grade: string
          [<JsonPropertyName("warningsCount")>]
          WarningsCount: int
          [<JsonPropertyName("findings")>]
          Findings: obj list
          [<JsonPropertyName("diff")>]
          Diff: obj
          [<JsonPropertyName("services")>]
          Services: obj list }

    /// <summary>JSON形式で診断結果を整形します。</summary>
    let formatJson
        (isRoot: bool)
        (isLinux: bool)
        (scoreValue: int option)
        (grade: string)
        (findings: SecurityFinding list)
        (services: ServiceEntry seq)
        (diffMessages: string list)
        (hasPreviousScan: bool)
        : string =
        let warnings = findings |> List.filter (fun f -> f.Level = FindingLevel.Warning)
        let jsonFindings =
            findings
            |> List.map (fun f ->
                box {| title = f.Title
                       detail = f.Detail
                       level = string f.Level
                       deduction = f.Deduction |})

        let jsonServices =
            services
            |> Seq.map (fun s ->
                box {| name = s.Name
                       displayName = s.DisplayName
                       category = s.Category
                       status = s.Status
                       ports = s.Ports
                       executable = s.Executable
                       url = s.Url
                       isCustom = s.IsCustom
                       isDiff = s.IsDiff
                       diffNote = s.DiffNote |})
            |> Seq.toList

        let jsonDiff =
            box {| hasPrevious = hasPreviousScan
                   changes = diffMessages |}

        let output: JsonOutput =
            { Disclaimer = "ローカル設定の簡易確認です。脆弱性スキャンではありません。"
              Timestamp = DateTimeOffset.Now.ToString("o")
              Os = if isLinux then "Linux" else "Windows"
              IsRoot = isRoot
              Score = scoreValue
              Grade = grade
              WarningsCount = warnings.Length
              Findings = jsonFindings
              Diff = jsonDiff
              Services = jsonServices }

        let options = JsonSerializerOptions(WriteIndented = true)
        JsonSerializer.Serialize(output, options)
