namespace EasyServiceManager.Cli

open System
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open System.Text.Json
open EasyServiceManager.Models
open EasyServiceManager.Services

module Program =
    let version = "1.0.0"

    /// <summary>ヘルプメッセージを表示します。</summary>
    let showHelp () =
        printfn "簡単サービス診断 CLI (esd) v%s" version
        printfn "ローカル設定の簡易確認ツール（読み取り専用）"
        printfn ""
        printfn "使用法:"
        printfn "  esd                      その場で診断して要約を表示（scan と同等）"
        printfn "  esd scan [options]       診断を実行して結果を出力"
        printfn "  esd diff [options] [files] 前回スナップショットとの差分のみを表示"
        printfn "                           (例: esd diff / esd diff prev.json / esd diff a.json b.json)"
        printfn "  esd version              バージョン情報を表示"
        printfn "  esd help                 このヘルプを表示"
        printfn ""
        printfn "scan のオプション:"
        printfn "  --format <fmt>           出力形式を指定 (text | json | markdown | html) [既定: text]"
        printfn "  --output <path>          ファイルへ出力 (未指定時は標準出力)"
        printfn "  --only-running           起動中サービスのみを一覧に含める"
        printfn "  --category <name>        指定カテゴリで絞り込み (Web Server, Database 等)"
        printfn "  --save-snapshot [path]   今回の結果をスナップショットとして保存する"
        printfn "  --diff                   診断結果と同時に前回スキャンとの差分も表示する"
        printfn "  --no-color               ANSI色付けを無効化 (パイプ時は自動で無効化)"
        printfn "  --quiet                  要約（スコアと警告一覧）のみを表示"
        printfn ""
        printfn "終了コード:"
        printfn "  0: 警告なし"
        printfn "  1: 警告あり (要注意レベル)"
        printfn "  2: 危険レベルあり (DBポート全公開や重大な設定不備)"
        printfn "  3: 診断自体の実行エラー"

    /// <summary>CLI引数オプション。</summary>
    type CliOptions =
        { Command: string
          Format: string
          OutputPath: string option
          OnlyRunning: bool
          Category: string option
          SaveSnapshot: bool
          SnapshotSavePath: string option
          Diff: bool
          DiffFiles: string list
          NoColor: bool
          Quiet: bool }

    let defaultOptions =
        { Command = "scan"
          Format = "text"
          OutputPath = None
          OnlyRunning = false
          Category = None
          SaveSnapshot = false
          SnapshotSavePath = None
          Diff = false
          DiffFiles = []
          NoColor = false
          Quiet = false }

    /// <summary>コマンドライン引数を解析します。</summary>
    let parseArgs (args: string array) =
        let rec parse (opts: CliOptions) (i: int) =
            if i >= args.Length then opts
            else
                match args.[i] with
                | "scan" -> parse { opts with Command = "scan" } (i + 1)
                | "diff" -> parse { opts with Command = "diff"; Diff = true } (i + 1)
                | "version" | "-v" | "--version" -> parse { opts with Command = "version" } (i + 1)
                | "help" | "-h" | "--help" -> parse { opts with Command = "help" } (i + 1)
                | "--format" when i + 1 < args.Length ->
                    parse { opts with Format = args.[i + 1].ToLowerInvariant() } (i + 2)
                | "--output" when i + 1 < args.Length ->
                    parse { opts with OutputPath = Some args.[i + 1] } (i + 2)
                | "--only-running" ->
                    parse { opts with OnlyRunning = true } (i + 1)
                | "--category" when i + 1 < args.Length ->
                    parse { opts with Category = Some args.[i + 1] } (i + 2)
                | "--save-snapshot" ->
                    if i + 1 < args.Length && not (args.[i + 1].StartsWith("-")) then
                        parse { opts with SaveSnapshot = true; SnapshotSavePath = Some args.[i + 1] } (i + 2)
                    else
                        parse { opts with SaveSnapshot = true } (i + 1)
                | "--diff" ->
                    parse { opts with Diff = true } (i + 1)
                | "--no-color" ->
                    parse { opts with NoColor = true } (i + 1)
                | "--quiet" ->
                    parse { opts with Quiet = true } (i + 1)
                | extra ->
                    if opts.Command = "diff" && not (extra.StartsWith("-")) then
                        parse { opts with DiffFiles = opts.DiffFiles @ [ extra ] } (i + 1)
                    elif i = 0 && not (extra.StartsWith("-")) then
                        parse { opts with Command = extra } (i + 1)
                    else
                        parse opts (i + 1)
        parse defaultOptions 0

    /// <summary>現在の実行環境が root かどうかを判定します。</summary>
    let checkIsRoot (isLinux: bool) =
        if isLinux then
            try
                let psi = ProcessStartInfo("id", "-u")
                psi.RedirectStandardOutput <- true
                psi.UseShellExecute <- false
                use p = Process.Start(psi)
                let out = p.StandardOutput.ReadToEnd().Trim()
                p.WaitForExit()
                out = "0"
            with _ -> Environment.UserName = "root"
        else false

    /// <summary>診断結果から終了コードを判定します。</summary>
    let determineExitCode (findings: SecurityFinding list) =
        let warnings = findings |> List.filter (fun f -> f.Level = FindingLevel.Warning)
        if warnings.IsEmpty then 0
        else
            // 危険レベル（終了コード 2）:
            // 減点25以上の重大警告、または「要注意ポート」での全公開、またはDBポート公開等
            let hasCritical =
                warnings
                |> List.exists (fun f ->
                    f.Deduction >= 25 ||
                    (f.Title = "要注意ポート" && f.Deduction > 0) ||
                    (f.Detail.Contains("全インターフェースで待ち受けています") && f.Deduction >= 20))
            if hasCritical then 2 else 1

    [<EntryPoint>]
    let main argv =
        try
            let options = parseArgs argv

            match options.Command with
            | "version" ->
                printfn "Easy Service Diagnostics (esd) v%s" version
                0
            | "help" ->
                showHelp ()
                0
            | "scan" | "diff" ->
                let isLinux = RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                let isRoot = checkIsRoot isLinux
                let showDiffOnly = options.Command = "diff"
                let isDiffEnabled = options.Diff || showDiffOnly

                // 1. 設定ファイルの読み込み
                let config = Config.loadConfig ()

                // 2. サービス検出・差分比較
                let detectedServices, hasPreviousScan, diffMessages =
                    match options.DiffFiles with
                    | [ fileA; fileB ] ->
                        // 2つのスナップショットファイル同士の比較
                        match ServiceDiscovery.loadSnapshotFile fileA, ServiceDiscovery.loadSnapshotFile fileB with
                        | Some snapA, Some snapB ->
                            ServiceDiscovery.compareTwoSnapshots snapA snapB
                        | _ ->
                            eprintfn "エラー: スナップショットファイルを読み込めませんでした (%s, %s)" fileA fileB
                            [||], false, []
                    | [ fileA ] ->
                        // 指定スナップショットファイルと現在状態の比較
                        match ServiceDiscovery.loadSnapshotFile fileA with
                        | Some snapA ->
                            let svcs = ServiceDiscovery.detect ()
                            // 空リスナーとして比較
                            ServiceDiscovery.compareSnapshots (Some snapA) svcs []
                        | None ->
                            eprintfn "エラー: スナップショットファイルを読み込めませんでした (%s)" fileA
                            [||], false, []
                    | _ ->
                        if isDiffEnabled || options.SaveSnapshot then
                            ServiceDiscovery.detectAndCompare ()
                        else
                            let svcs = ServiceDiscovery.detect ()
                            svcs, false, []

                // --save-snapshot で指定パスがある場合は保存
                match options.SnapshotSavePath with
                | Some path when detectedServices.Length > 0 ->
                    try
                        let snap = ServiceDiscovery.buildSnapshot detectedServices []
                        let dir = Path.GetDirectoryName(path)
                        if not (String.IsNullOrWhiteSpace dir) && not (Directory.Exists dir) then
                            Directory.CreateDirectory(dir) |> ignore
                        File.WriteAllText(path, JsonSerializer.Serialize(snap, JsonSerializerOptions(WriteIndented = true)))
                    with ex ->
                        eprintfn "警告: スナップショットの保存に失敗しました (%s): %s" path ex.Message
                | _ -> ()

                // カスタムサービスの追加
                let customEntries =
                    config.CustomServices
                    |> List.map (fun c ->
                        ServiceEntry(c.Name, c.DisplayName, c.Category, "起動中", c.Ports, "", c.Url, true, false, false))

                let allServices =
                    [ yield! detectedServices
                      yield! customEntries ]

                // 絞り込み（--only-running, --category）
                let filteredServices =
                    allServices
                    |> Seq.filter (fun s ->
                        let matchesRunning = if options.OnlyRunning then s.Status = "起動中" else true
                        let matchesCategory =
                            match options.Category with
                            | Some cat -> String.Equals(s.Category, cat, StringComparison.OrdinalIgnoreCase)
                            | None -> true
                        matchesRunning && matchesCategory)
                    |> Seq.toList

                // 3. セキュリティ診断
                let findings = SecurityAudit.inspect ()
                let scoreText, grade =
                    match SecurityAudit.score findings with
                    | Some(v, g) -> sprintf "%d / 100" v, g
                    | None -> "未評価", "—"

                let scoreValue =
                    match SecurityAudit.score findings with
                    | Some(v, _) -> Some v
                    | None -> None

                // 4. 出力生成
                let outputText =
                    match options.Format with
                    | "json" ->
                        Formatters.formatJson isRoot isLinux scoreValue grade findings filteredServices diffMessages hasPreviousScan
                    | "markdown" ->
                        ReportExporter.buildMarkdownReport "ja" filteredServices findings scoreText grade diffMessages hasPreviousScan
                    | "html" ->
                        ReportExporter.buildHtmlReport "ja" filteredServices findings scoreText grade diffMessages hasPreviousScan
                    | _ -> // "text"
                        let useColor =
                            not options.NoColor
                            && not Console.IsOutputRedirected
                            && String.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"))
                        let theme = Formatters.ColorTheme(useColor)
                        Formatters.formatText
                            theme
                            isRoot
                            isLinux
                            scoreValue
                            grade
                            findings
                            filteredServices
                            diffMessages
                            hasPreviousScan
                            showDiffOnly
                            options.Quiet

                // 5. 出力先（ファイルまたは標準出力）
                match options.OutputPath with
                | Some path ->
                    let dir = Path.GetDirectoryName(path)
                    if not (String.IsNullOrWhiteSpace dir) && not (Directory.Exists dir) then
                        Directory.CreateDirectory(dir) |> ignore
                    File.WriteAllText(path, outputText)
                    if not options.Quiet then
                        printfn "レポートを保存しました: %s" path
                | None ->
                    printfn "%s" outputText

                // 6. 終了コードの決定
                determineExitCode findings

            | unknownCmd ->
                eprintfn "エラー: 不明なコマンド '%s'" unknownCmd
                eprintfn "ヘルプを表示するには 'esd help' を実行してください。"
                3
        with ex ->
            eprintfn "エラーが発生しました: %s" ex.Message
            3
