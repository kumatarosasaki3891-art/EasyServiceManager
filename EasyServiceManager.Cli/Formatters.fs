namespace EasyServiceManager.Cli

open System
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

    /// <summary>評価グレード文字列（暫定表記を含む）を指定言語へ整形します。</summary>
    let localizeGrade (langCode: string) (grade: string) =
        if String.IsNullOrWhiteSpace grade then "—"
        elif grade.EndsWith("（暫定）", StringComparison.Ordinal) then
            let letter = grade.Substring(0, grade.Length - "（暫定）".Length)
            sprintf "%s (%s)" letter (Localization.translate langCode "暫定")
        else grade

    /// <summary>テキスト形式で診断結果を整形します。</summary>
    let formatText
        (color: ColorTheme)
        (langCode: string)
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
        (definitionDateOpt: string option)
        : string =
        let t key = Localization.translate langCode key
        let fmt key args = Localization.format langCode key args
        let tt text = Localization.translateText langCode text
        let sb = StringBuilder()

        // 1. 免責
        sb.AppendLine(color.Gray(t "※ 本診断はローカル設定の簡易確認です。脆弱性スキャンではありません。")) |> ignore
        match definitionDateOpt with
        | Some defDate ->
            sb.AppendLine(color.Gray(fmt "定義データ: {0}時点" [| box defDate |])) |> ignore
        | None -> ()
        sb.AppendLine() |> ignore

        // 2. 権限注記 (Linux で非root実行時)
        if isLinux && not isRoot then
            sb.AppendLine(color.Gray(t "注: 一部のプロセス名が取得できていないため、帰属は推定を含みます（sudo で実行すると精度が向上します）")) |> ignore
            sb.AppendLine() |> ignore

        // 3. 総合スコアと評価
        let warnings = findings |> List.filter (fun f -> f.Level = FindingLevel.Warning)
        let scoreStr =
            match scoreValue with
            | Some v -> sprintf "%d / 100" v
            | None -> t "未評価"

        let localizedGrade = localizeGrade langCode grade
        let gradeColored =
            if grade.StartsWith("A") then color.Green(localizedGrade)
            elif grade.StartsWith("B") then color.Cyan(localizedGrade)
            elif grade.StartsWith("C") then color.Yellow(localizedGrade)
            else color.Red(localizedGrade)

        let warningText = fmt "{0} 件の警告" [| box warnings.Length |]
        let warningStr =
            if warnings.Length = 0 then color.Green(warningText)
            else color.Yellow(warningText)

        sb.AppendLine(sprintf "🛡️  %s" (fmt "総合スコア: {0} (グレード: {1})  [{2}]" [| box (color.Bold scoreStr); box gradeColored; box warningStr |])) |> ignore
        sb.AppendLine(String.replicate 60 "─") |> ignore

        // 4. 前回との差分
        if hasPreviousScan then
            sb.AppendLine() |> ignore
            if diffMessages.Length > 0 then
                sb.AppendLine(color.Yellow(color.Bold((fmt "⚠ 前回スキャンからの変化 ({0}件)" [| box diffMessages.Length |]) + ":"))) |> ignore
                for msg in diffMessages do
                    sb.AppendLine(color.Yellow(sprintf "  • %s" (tt msg))) |> ignore
            else
                sb.AppendLine(color.Gray(sprintf "• %s" (t "前回から変化なし"))) |> ignore
            sb.AppendLine(String.replicate 60 "─") |> ignore

        if not showDiffOnly then
            // 5. 診断項目一覧
            sb.AppendLine() |> ignore
            sb.AppendLine(color.Bold(t "■ 診断項目:")) |> ignore
            for f in findings do
                let icon, line =
                    match f.Level with
                    | FindingLevel.Good ->
                        "✅", color.Green(sprintf "%s: %s" f.Title f.Detail)
                    | FindingLevel.Warning ->
                        let tag = if f.Deduction >= 25 then color.Red(t "[危険]") else color.Yellow(t "[警告]")
                        let pts = fmt "-{0}点" [| box f.Deduction |]
                        "⚠️", sprintf "%s %s: %s (%s)" tag (color.Bold f.Title) f.Detail pts
                    | FindingLevel.Unknown ->
                        "❓", color.Gray(sprintf "%s: %s" f.Title f.Detail)
                    | FindingLevel.Information ->
                        "ℹ️", color.Gray(sprintf "%s: %s" f.Title f.Detail)
                sb.AppendLine(sprintf "  %s %s" icon line) |> ignore

            // 6. サービス一覧 (--quiet のときは省略)
            if not quiet then
                let serviceList = services |> Seq.toList
                sb.AppendLine() |> ignore
                sb.AppendLine(color.Bold(fmt "■ サービス一覧 ({0} 件):" [| box serviceList.Length |])) |> ignore
                sb.AppendLine(color.Gray(sprintf "  %-12s %-28s %-16s %s" (t "状態") (t "サービス名") (t "カテゴリ") (t "ポート"))) |> ignore
                sb.AppendLine(color.Gray("  " + String.replicate 68 "-")) |> ignore
                for s in serviceList do
                    let statusIcon =
                        if s.Status.StartsWith("起動中", StringComparison.Ordinal) then
                            color.Green("● " + s.StatusLabel)
                        else
                            color.Gray("○ " + s.StatusLabel)
                    let diffBadge = if s.IsDiff then color.Yellow(" " + t "[差分]") else ""
                    let portsStr = if String.IsNullOrWhiteSpace s.Ports then "-" else s.Ports
                    sb.AppendLine(sprintf "  %-20s %-28s %-16s %s%s" statusIcon s.Name s.CategoryLabel portsStr diffBadge) |> ignore

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
          [<JsonPropertyName("listeners")>]
          Listeners: obj list
          [<JsonPropertyName("services")>]
          Services: obj list }

    /// <summary>JSON形式で診断結果を整形します。</summary>
    let formatJson
        (langCode: string)
        (isRoot: bool)
        (isLinux: bool)
        (scoreValue: int option)
        (grade: string)
        (findings: SecurityFinding list)
        (services: ServiceEntry seq)
        (listeners: ListenerInfo list)
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

        let jsonListeners =
            listeners
            |> List.map (fun l ->
                box {| port = l.Port
                       bindAddress = l.BindAddress
                       exposureLevel = Exposure.toCode (Exposure.classify l.BindAddress)
                       processId = l.ProcessId
                       processName = l.ProcessName |})

        let jsonServices =
            services
            |> Seq.map (fun s ->
                let localizedDiffNote =
                    if String.IsNullOrWhiteSpace s.DiffNote then ""
                    else Localization.translateText langCode s.DiffNote
                box {| name = s.Name
                       displayName = s.DisplayName
                       category = s.Category
                       status = s.StatusLabel
                       ports = s.Ports
                       bindAddress = s.BindAddress
                       exposureLevel = s.ExposureCode
                       executable = s.Executable
                       url = s.Url
                       isCustom = s.IsCustom
                       isDiff = s.IsDiff
                       diffNote = localizedDiffNote |})
            |> Seq.toList

        let jsonDiff =
            box {| hasPrevious = hasPreviousScan
                   changes = diffMessages |> List.map (Localization.translateText langCode) |}

        let output: JsonOutput =
            { Disclaimer = Localization.translate langCode "ローカル設定の簡易確認です。脆弱性スキャンではありません。"
              Timestamp = DateTimeOffset.Now.ToString("o")
              Os = if isLinux then "Linux" else "Windows"
              IsRoot = isRoot
              Score = scoreValue
              Grade = localizeGrade langCode grade
              WarningsCount = warnings.Length
              Findings = jsonFindings
              Diff = jsonDiff
              Listeners = jsonListeners
              Services = jsonServices }

        let options = JsonSerializerOptions(WriteIndented = true)
        JsonSerializer.Serialize(output, options)
