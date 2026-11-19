namespace EasyServiceManager.Cli

open System
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open System.Text
open System.Text.Json
open System.Threading
open EasyServiceManager.Models
open EasyServiceManager.Services

module Program =
    let version = "1.1.0"

    /// <summary>ヘルプメッセージを表示します。</summary>
    let showHelp (langCode: string) =
        let t key = Localization.translate langCode key
        printfn "%s" (Localization.format langCode "簡単サービス診断 CLI (esd) v{0}" [| box version |])
        printfn "%s" (t "ローカル設定の簡易確認ツール（読み取り専用）")
        printfn ""
        printfn "%s" (t "使用法:")
        printfn "%s" (t "HELP_BODY")
        printfn ""
        printfn "%s" (t "scan のオプション:")
        printfn "%s" (t "HELP_OPTIONS")
        printfn ""
        printfn "%s" (t "終了コード:")
        printfn "%s" (t "HELP_EXITCODES")

    /// <summary>CLI引数オプション。</summary>
    type CliOptions =
        { Command: string
          Format: string
          OutputPath: string option
          Lang: string option
          OnlyRunning: bool
          Category: string option
          SaveSnapshot: bool
          SnapshotSavePath: string option
          Diff: bool
          DiffFiles: string list
          Qr: bool
          QrPlain: bool
          QrIp: string option
          NoColor: bool
          Quiet: bool }

    let defaultOptions =
        { Command = "scan"
          Format = "text"
          OutputPath = None
          Lang = None
          OnlyRunning = false
          Category = None
          SaveSnapshot = false
          SnapshotSavePath = None
          Diff = false
          DiffFiles = []
          Qr = false
          QrPlain = false
          QrIp = None
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
                | "update-check" -> parse { opts with Command = "update-check" } (i + 1)
                | "update-defs" -> parse { opts with Command = "update-defs" } (i + 1)
                | "version" | "-v" | "--version" -> parse { opts with Command = "version" } (i + 1)
                | "help" | "-h" | "--help" -> parse { opts with Command = "help" } (i + 1)
                | "--format" when i + 1 < args.Length ->
                    parse { opts with Format = args.[i + 1].ToLowerInvariant() } (i + 2)
                | "--output" when i + 1 < args.Length ->
                    parse { opts with OutputPath = Some args.[i + 1] } (i + 2)
                | "--lang" when i + 1 < args.Length ->
                    parse { opts with Lang = Some args.[i + 1] } (i + 2)
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
                | "--qr" ->
                    parse { opts with Qr = true } (i + 1)
                | "--qr-plain" ->
                    parse { opts with QrPlain = true } (i + 1)
                | "--ip" when i + 1 < args.Length ->
                    parse { opts with QrIp = Some args.[i + 1] } (i + 2)
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

    /// <summary>診断結果から終了コードを判定します（言語非依存のRaw値で判定）。</summary>
    let determineExitCode (findings: SecurityFinding list) =
        let warnings = findings |> List.filter (fun f -> f.Level = FindingLevel.Warning)
        if warnings.IsEmpty then 0
        else
            let hasCritical =
                warnings
                |> List.exists (fun f ->
                    f.Deduction >= 25 ||
                    (f.RawTitle = "要注意ポート" && f.Deduction > 0) ||
                    (f.RawDetail.Contains("全インターフェースで待ち受けています") && f.Deduction >= 20))
            if hasCritical then 2 else 1

    let private listenersFromSnapshot (snap: ScanSnapshot) : ListenerInfo list =
        if isNull snap || isNull snap.Listeners then []
        else
            snap.Listeners
            |> Array.map (fun l ->
                { Port = l.Port; BindAddress = l.BindAddress; ProcessId = 0; ProcessName = l.ProcessName })
            |> Array.toList

    let private runQrTransfer (html: string) (langCode: string) (qrPlain: bool) (qrIpOpt: string option) =
        let timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss")
        let fileName = sprintf "EasyServiceReport_%s.html" timestamp
        let bytes = Encoding.UTF8.GetBytes(html)
        use server = new QrReportTransferService(bytes, fileName, "text/html; charset=utf-8", langCode)
        try
            server.Start(5070)
            let targetIp =
                match qrIpOpt with
                | Some ip when not (String.IsNullOrWhiteSpace ip) -> ip.Trim()
                | _ -> server.IpCandidates.Head.IpAddress
            let url = server.GetTransferUrl(targetIp)

            if Console.IsOutputRedirected then
                printfn "%s" url
            else
                printfn ""
                printfn "%s" (Localization.translate langCode "QRコード転送サーバーを起動しました。スマホでスキャンしてください:")
                printfn ""
                printfn "%s" (QrCodeCore.renderToTerminal url (not qrPlain))
                printfn ""
                printfn "%s" (Localization.format langCode "転送URL: {0}" [| box url |])
                printfn "%s" (Localization.translate langCode "待機中（有効期限3分・1回限り、Ctrl+C で終了）...")

            use doneEvent = new ManualResetEventSlim(false)
            let mutable outcomeMessage: string option = None

            server.DownloadCompleted.Add(fun () ->
                outcomeMessage <- Some(Localization.translate langCode "ダウンロードが完了しました。")
                doneEvent.Set())

            server.Expired.Add(fun () ->
                outcomeMessage <- Some(Localization.translate langCode "QRコードの有効期限（3分）が切れました。")
                doneEvent.Set())

            let cancelHandler = ConsoleCancelEventHandler(fun _ e ->
                e.Cancel <- true
                doneEvent.Set())
            Console.CancelKeyPress.AddHandler(cancelHandler)
            try
                doneEvent.Wait()
            finally
                Console.CancelKeyPress.RemoveHandler(cancelHandler)
                server.Stop()

            match outcomeMessage with
            | Some msg when not Console.IsOutputRedirected -> printfn "%s" msg
            | _ -> ()
            true
        with ex ->
            eprintfn "%s" (Localization.format langCode "QRコード転送を開始できませんでした: {0}" [| box ex.Message |])
            false

    [<EntryPoint>]
    let main argv =
        try
            try Console.OutputEncoding <- Encoding.UTF8 with _ -> ()
            try Console.InputEncoding <- Encoding.UTF8 with _ -> ()

            let options = parseArgs argv

            let langCodeResult =
                match options.Lang with
                | None -> Ok "ja-JP"
                | Some rawLang ->
                    match Localization.tryNormalizeCode rawLang with
                    | Some code -> Ok code
                    | None -> Error rawLang

            match langCodeResult with
            | Error invalidLang ->
                eprintfn "%s" (Localization.format "ja-JP" "エラー: 未対応の言語コード '{0}' です。" [| box invalidLang |])
                3
            | Ok langCode ->
                let t key = Localization.translate langCode key
                let fmt key args = Localization.format langCode key args

                match options.Command with
                | "version" ->
                    printfn "Easy Service Diagnostics (esd) v%s" version
                    printfn "Releases: %s" MasterDataRepository.ReleasesUrl
                    0
                | "help" ->
                    showHelp langCode
                    0
                | "update-check" ->
                    printfn "%s" (t "更新を確認しています...")
                    let fetched = MasterDataRepository.FetchAndImportAsync() |> Async.RunSynchronously
                    if not fetched then
                        eprintfn "%s" (t "更新を確認できませんでした（ネットワーク未接続または取得失敗）。")
                        3
                    else
                        let snapshot = MasterDataRepository.GetSnapshot()
                        match MasterDataRepository.TryGetApp(MasterDataRepository.AppId, snapshot) with
                        | Some app when VersionDiagnostics.isAppOutdated version app.Latest ->
                            printfn "%s" (fmt "新しいバージョン v{0} が利用可能です（現在: v{1}）。" [| box app.Latest; box version |])
                            printfn "%s" (fmt "ダウンロード先: {0}" [| box MasterDataRepository.ReleasesUrl |])
                            1
                        | _ ->
                            printfn "%s" (fmt "最新のバージョンです (v{0})。" [| box version |])
                            0
                | "update-defs" ->
                    printfn "%s" (t "定義データを更新しています...")
                    let fetched = MasterDataRepository.FetchAndImportAsync() |> Async.RunSynchronously
                    if not fetched then
                        eprintfn "%s" (t "定義データの取得に失敗しました。")
                        3
                    else
                        let snapshot = MasterDataRepository.GetSnapshot()
                        let defDate = MasterDataRepository.GetDefinitionDateText snapshot |> Option.defaultValue "N/A"
                        printfn "%s" (fmt "定義データを更新しました（定義データ: {0}時点）。" [| box defDate |])
                        0
                | "scan" | "diff" ->
                    let isLinux = RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                    let isRoot = checkIsRoot isLinux
                    let showDiffOnly = options.Command = "diff"
                    let isDiffEnabled = options.Diff || showDiffOnly

                    // 1. 設定ファイルの読み込み
                    let config = Config.loadConfig ()

                    // 2. サービス検出・リスナー取得・差分比較
                    let detectedServices, detectedListeners, hasPreviousScan, diffMessages =
                        match options.DiffFiles with
                        | [ fileA; fileB ] ->
                            match ServiceDiscovery.loadSnapshotFile fileA, ServiceDiscovery.loadSnapshotFile fileB with
                            | Some snapA, Some snapB ->
                                let svcs, hasPrev, diffs = ServiceDiscovery.compareTwoSnapshots snapA snapB
                                svcs, listenersFromSnapshot snapB, hasPrev, diffs
                            | _ ->
                                eprintfn "%s" (fmt "エラー: スナップショットファイルを読み込めませんでした ({0}, {1})" [| box fileA; box fileB |])
                                [||], [], false, []
                        | [ fileA ] ->
                            match ServiceDiscovery.loadSnapshotFile fileA with
                            | Some snapA ->
                                let svcs, listeners = ServiceDiscovery.detectWithListeners ()
                                let combined, hasPrev, diffs = ServiceDiscovery.compareSnapshots (Some snapA) svcs listeners
                                combined, listeners, hasPrev, diffs
                            | None ->
                                eprintfn "%s" (fmt "エラー: スナップショットファイルを読み込めませんでした ({0})" [| box fileA |])
                                [||], [], false, []
                        | _ ->
                            if isDiffEnabled || options.SaveSnapshot then
                                ServiceDiscovery.detectAndCompareWithListeners ()
                            else
                                let svcs, listeners = ServiceDiscovery.detectWithListeners ()
                                svcs, listeners, false, []

                    // --save-snapshot で指定パスがある場合はリスナー情報も含めて保存
                    match options.SnapshotSavePath with
                    | Some path when detectedServices.Length > 0 ->
                        try
                            let snap = ServiceDiscovery.buildSnapshot detectedServices detectedListeners
                            let dir = Path.GetDirectoryName(path)
                            if not (String.IsNullOrWhiteSpace dir) && not (Directory.Exists dir) then
                                Directory.CreateDirectory(dir) |> ignore
                            File.WriteAllText(path, JsonSerializer.Serialize(snap, JsonSerializerOptions(WriteIndented = true)))
                        with ex ->
                            eprintfn "%s" (fmt "警告: スナップショットの保存に失敗しました ({0}): {1}" [| box path; box ex.Message |])
                    | _ -> ()

                    // カスタムサービスの追加
                    let customEntries =
                        config.CustomServices
                        |> List.map (fun c ->
                            ServiceEntry(c.Name, c.DisplayName, c.Category, "起動中", c.Ports, "", c.Url, true, false, false))

                    let allServices =
                        [ yield! detectedServices
                          yield! customEntries ]
                    for s in allServices do
                        s.LanguageCode <- langCode

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

                    // 3. セキュリティ診断（scan/diff時は一切外部通信せずローカルSQLiteキャッシュのみ参照）
                    let masterSnapshot = MasterDataRepository.GetSnapshot()
                    let findings = SecurityAudit.inspectWithContext allServices (Some detectedListeners) (Some masterSnapshot)
                    for f in findings do
                        f.LanguageCode <- langCode

                    let scoreOpt = SecurityAudit.score findings
                    let scoreText, grade =
                        match scoreOpt with
                        | Some(v, g) -> sprintf "%d / 100" v, g
                        | None -> t "未評価", "—"
                    let scoreValue = scoreOpt |> Option.map fst
                    let localizedGrade = Formatters.localizeGrade langCode grade

                    // 4. 出力生成
                    let outputText =
                        match options.Format with
                        | "json" ->
                            Formatters.formatJson langCode isRoot isLinux scoreValue grade findings filteredServices detectedListeners diffMessages hasPreviousScan
                        | "markdown" ->
                            ReportExporter.buildMarkdownReport langCode filteredServices findings scoreText localizedGrade diffMessages hasPreviousScan
                        | "html" ->
                            ReportExporter.buildHtmlReport langCode filteredServices findings scoreText localizedGrade diffMessages hasPreviousScan
                        | _ ->
                            let useColor =
                                not options.NoColor
                                && not Console.IsOutputRedirected
                                && String.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"))
                            let theme = Formatters.ColorTheme(useColor)
                            let defDateOpt = MasterDataRepository.GetDefinitionDateText masterSnapshot
                            Formatters.formatText
                                theme
                                langCode
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
                                defDateOpt

                    // 5. 出力先（ファイルまたは標準出力）
                    match options.OutputPath with
                    | Some path ->
                        let dir = Path.GetDirectoryName(path)
                        if not (String.IsNullOrWhiteSpace dir) && not (Directory.Exists dir) then
                            Directory.CreateDirectory(dir) |> ignore
                        File.WriteAllText(path, outputText)
                        if not options.Quiet then
                            printfn "%s" (fmt "レポートを保存しました: {0}" [| box path |])
                    | None ->
                        printfn "%s" outputText

                    // 6. --qr 転送サーバー起動（指定時）
                    if options.Qr then
                        let htmlReport =
                            ReportExporter.buildHtmlReport langCode filteredServices findings scoreText localizedGrade diffMessages hasPreviousScan
                        let _ = runQrTransfer htmlReport langCode options.QrPlain options.QrIp
                        ()

                    // 7. 終了コードの決定
                    determineExitCode findings

                | unknownCmd ->
                    eprintfn "%s" (fmt "エラー: 不明なコマンド '{0}'" [| box unknownCmd |])
                    eprintfn "%s" (t "ヘルプを表示するには 'esd help' を実行してください。")
                    3
        with ex ->
            eprintfn "%s" (Localization.format "ja-JP" "エラーが発生しました: {0}" [| box ex.Message |])
            3
