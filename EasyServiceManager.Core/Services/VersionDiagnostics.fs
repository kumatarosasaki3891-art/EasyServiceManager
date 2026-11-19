namespace EasyServiceManager.Services

open System
open System.ComponentModel
open System.Diagnostics
open System.IO
open System.Text.RegularExpressions
open EasyServiceManager.Models

/// <summary>数値比較用に分解されたバージョン情報。</summary>
type ParsedVersion = {
    Raw: string
    Segments: int list
}

/// <summary>ホスト上で受動検出されたランタイムバージョン情報。</summary>
type DetectedHostRuntime = {
    RuntimeId: string
    DisplayName: string
    Version: ParsedVersion
    RawVersion: string
    DetectionSource: string
    Exposure: ExposureLevel
}

/// <summary>マスターデータを使用してホスト稼働サービスのバージョン古さ・緊急 advisory を診断するモジュール。</summary>
module VersionDiagnostics =

    let private dateTagRegex = Regex("""^\d{4}-\d{2}-\d{2}(?:$|[-_.])""")
    let private epochPrefixRegex = Regex("""^\d+:""")
    let private versionTagRegex = Regex("""^[vV]?(\d+(?:\.\d+)*)(?:$|[-+_~].*)""")

    /// <summary>バージョン文字列をドット区切りの数値セグメントに解析します（日付タグや 0.0.0.0 は除外）。</summary>
    let tryParseVersion (raw: string) : ParsedVersion option =
        if String.IsNullOrWhiteSpace(raw) then None
        else
            let trimmed = epochPrefixRegex.Replace(raw.Trim(), "")
            if dateTagRegex.IsMatch(trimmed) then None
            else
                let m = versionTagRegex.Match(trimmed)
                if not m.Success then None
                else
                    let numericPart = m.Groups.[1].Value
                    let parts = numericPart.Split('.')
                    let parsed =
                        parts
                        |> Array.choose (fun p ->
                            match Int32.TryParse(p) with
                            | true, n when n >= 0 -> Some n
                            | _ -> None)
                        |> Array.toList
                    if parsed.Length <> parts.Length || List.isEmpty parsed then None
                    elif parsed.[0] >= 1000000 then None
                    elif parsed |> List.forall (fun s -> s = 0) then None
                    else Some { Raw = numericPart; Segments = parsed }

    /// <summary>2つのバージョンを数値セグメント単位で比較します（a < b なら負、等しければ 0、a > b なら正）。</summary>
    let compareVersions (a: ParsedVersion) (b: ParsedVersion) : int =
        let maxLen = max a.Segments.Length b.Segments.Length
        let pad (segs: int list) =
            if segs.Length >= maxLen then segs
            else segs @ List.replicate (maxLen - segs.Length) 0
        let rec loop xs ys =
            match xs, ys with
            | [], [] -> 0
            | x :: xTail, y :: yTail ->
                let c = compare x y
                if c <> 0 then c else loop xTail yTail
            | _ -> 0
        loop (pad a.Segments) (pad b.Segments)

    /// <summary>バージョンが半開区間 [from, toExclusive) に入るかを判定します。</summary>
    let isInRange (version: ParsedVersion) (range: AffectedRange) : bool =
        match tryParseVersion range.From, tryParseVersion range.ToExclusive with
        | Some fromVer, Some toVer ->
            compareVersions version fromVer >= 0 && compareVersions version toVer < 0
        | _ -> false

    /// <summary>指定基準日においてブランチがサポート終了（EOL 超過）かどうかを判定します（EOL 当日は対象外）。</summary>
    let isBranchEol (referenceDate: DateOnly) (branch: RuntimeBranchRecord) : bool =
        match branch.Eol with
        | Some eolText ->
            match DateOnly.TryParse(eolText) with
            | true, eolDate -> referenceDate > eolDate
            | _ -> not branch.Support
        | None -> not branch.Support

    let private startsWithSegments (prefix: int list) (target: int list) : bool =
        if target.Length < prefix.Length then false
        else
            List.zip prefix (List.take prefix.Length target)
            |> List.forall (fun (a, b) -> a = b)

    let private tryFindBranch (version: ParsedVersion) (branches: RuntimeBranchRecord list) : (RuntimeBranchRecord * ParsedVersion) option =
        branches
        |> List.choose (fun b ->
            tryParseVersion b.Branch
            |> Option.bind (fun bv ->
                if startsWithSegments bv.Segments version.Segments then Some(b, bv)
                else None))
        |> List.sortByDescending (fun (_, bv) -> bv.Segments.Length)
        |> List.tryHead

    let private getSegmentAt (index: int) (segs: int list) =
        if index < segs.Length then segs.[index] else 0

    /// <summary>アプリの現在バージョンがマスターデータの最新バージョンより古いかを判定します。</summary>
    let isAppOutdated (currentVersion: string) (latestVersion: string) : bool =
        match tryParseVersion currentVersion, tryParseVersion latestVersion with
        | Some currentVer, Some latestVer -> compareVersions latestVer currentVer > 0
        | _ -> false

    let private runtimeDisplayNameForId = function
        | "nginx" -> "nginx"
        | "php" -> "PHP"
        | "mysql" -> "MySQL"
        | "mariadb" -> "MariaDB"
        | "postgres" -> "PostgreSQL"
        | "redis" -> "Redis"
        | "httpd" -> "Apache httpd"
        | other -> other

    /// <summary>コマンドライン文字列から実行ファイルの実パスを安全に抽出します。</summary>
    let extractExecutablePath (commandLine: string) : string option =
        if String.IsNullOrWhiteSpace commandLine then None
        else
            let trimmed = commandLine.Trim()
            let candidate =
                if trimmed.StartsWith("\"", StringComparison.Ordinal) then
                    let closing = trimmed.IndexOf('"', 1)
                    if closing > 1 then trimmed.Substring(1, closing - 1) else trimmed.Trim('"')
                else
                    let exeMatch = Regex.Match(trimmed, "^(.*?\\.exe)(?=\\s|$)", RegexOptions.IgnoreCase)
                    if exeMatch.Success then exeMatch.Groups.[1].Value
                    else
                        trimmed.Split([| ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries)
                        |> Array.tryHead
                        |> Option.defaultValue ""
            if String.IsNullOrWhiteSpace candidate then None else Some candidate

    let private mapExeNameToRuntimeId (fileNameLower: string) (productHint: string) =
        match fileNameLower with
        | "httpd.exe" | "apache.exe" | "apache2.exe" -> Some "httpd"
        | "nginx.exe" -> Some "nginx"
        | "mysqld.exe" ->
            if productHint.Contains("mariadb", StringComparison.OrdinalIgnoreCase) then Some "mariadb"
            else Some "mysql"
        | "mariadbd.exe" -> Some "mariadb"
        | "postgres.exe" -> Some "postgres"
        | "redis-server.exe" -> Some "redis"
        | "php.exe" | "php-cgi.exe" -> Some "php"
        | _ -> None

    let private readWindowsFileVersion (exePath: string) =
        try
            if not (File.Exists exePath) then None
            else
                let info = FileVersionInfo.GetVersionInfo(exePath)
                let productHint = sprintf "%s %s" (Option.ofObj info.ProductName |> Option.defaultValue "") (Option.ofObj info.FileDescription |> Option.defaultValue "")
                let verOpt =
                    [ info.ProductVersion; info.FileVersion ]
                    |> List.tryPick (fun v ->
                        if String.IsNullOrWhiteSpace v then None
                        else tryParseVersion v)
                verOpt |> Option.map (fun v -> v, productHint)
        with _ -> None

    let private runPassiveCommand (executable: string) (arguments: string list) : string option =
        try
            let inFlatpak = File.Exists("/.flatpak-info")
            let effectiveExe = if inFlatpak then "flatpak-spawn" else executable
            let effectiveArgs = if inFlatpak then "--host" :: executable :: arguments else arguments
            let psi = ProcessStartInfo(effectiveExe)
            psi.UseShellExecute <- false
            psi.RedirectStandardOutput <- true
            psi.RedirectStandardError <- true
            psi.CreateNoWindow <- true
            if inFlatpak then psi.WorkingDirectory <- "/"
            for a in effectiveArgs do psi.ArgumentList.Add(a)
            use p = new Process(StartInfo = psi)
            if not (p.Start()) then None
            else
                let outTask = p.StandardOutput.ReadToEndAsync()
                let _errTask = p.StandardError.ReadToEndAsync()
                if not (p.WaitForExit(4000)) then
                    try p.Kill(true) with _ -> ()
                    None
                elif p.ExitCode = 0 then
                    Some(outTask.GetAwaiter().GetResult().Trim())
                else None
        with
        | :? Win32Exception
        | _ -> None

    let private servicePorts (service: ServiceEntry) =
        if String.IsNullOrWhiteSpace service.Ports then []
        else
            service.Ports.Split([| ',' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.choose (fun p ->
                let mutable v = 0
                if Int32.TryParse(p.Trim(), &v) then Some v else None)
            |> Array.toList

    let private maxExposureForService (service: ServiceEntry) (listenersByPort: Map<int, ListenerInfo>) =
        let byServicePorts =
            servicePorts service
            |> List.choose (fun p -> Map.tryFind p listenersByPort)
            |> List.map (fun l -> Exposure.classify l.BindAddress)
        if not (List.isEmpty byServicePorts) then
            byServicePorts |> List.maxBy Exposure.rank
        elif not (String.IsNullOrWhiteSpace service.BindAddress) then
            Exposure.classify service.BindAddress
        else
            ExposureLevel.Local

    let private detectWindowsHostRuntimes (services: ServiceEntry seq) (listeners: ListenerInfo list) : DetectedHostRuntime list =
        let listenersByPort =
            listeners
            |> List.groupBy (fun l -> l.Port)
            |> List.map (fun (p, grp) -> p, grp |> List.maxBy (fun l -> Exposure.rank (Exposure.classify l.BindAddress)))
            |> Map.ofList

        let results = ResizeArray<DetectedHostRuntime>()
        let candidatePaths = ResizeArray<string * ExposureLevel>()

        for s in services do
            if s.Status.StartsWith("起動中", StringComparison.Ordinal) && not (String.IsNullOrWhiteSpace s.Executable) then
                match extractExecutablePath s.Executable with
                | Some exePath ->
                    let exp = maxExposureForService s listenersByPort
                    candidatePaths.Add(exePath, exp)
                | None -> ()

        // リスナーPIDからも実行パスを補完（XAMPP コントロールパネル起動の httpd/mysqld 等）
        for l in listeners do
            if l.ProcessId > 4 then
                try
                    use proc = Process.GetProcessById(l.ProcessId)
                    let exePath = proc.MainModule.FileName
                    if not (String.IsNullOrWhiteSpace exePath) then
                        candidatePaths.Add(exePath, Exposure.classify l.BindAddress)
                with _ -> ()

        let xamppRoots = ResizeArray<string * ExposureLevel>()
        for (exePath, exp) in candidatePaths do
            let fileName = Path.GetFileName(exePath).ToLowerInvariant()
            match readWindowsFileVersion exePath with
            | Some(ver, hint) ->
                match mapExeNameToRuntimeId fileName hint with
                | Some rtId ->
                    results.Add({
                        RuntimeId = rtId
                        DisplayName = runtimeDisplayNameForId rtId
                        Version = ver
                        RawVersion = ver.Raw
                        DetectionSource = sprintf "FileVersionInfo (%s)" (Path.GetFileName exePath)
                        Exposure = exp
                    })
                    // XAMPP 等（<root>\apache\bin\httpd.exe または <root>\mysql\bin\mysqld.exe）の配下 PHP を補助検出
                    if rtId = "httpd" || rtId = "nginx" then
                        try
                            let binDir = Path.GetDirectoryName(exePath)
                            let parent1 = if isNull binDir then null else Path.GetDirectoryName(binDir)
                            let parent2 = if isNull parent1 then null else Path.GetDirectoryName(parent1)
                            for rootDir in [ parent1; parent2 ] do
                                if not (isNull rootDir) then
                                    xamppRoots.Add(rootDir, exp)
                        with _ -> ()
                | None -> ()
            | None -> ()

        for (rootDir, exp) in xamppRoots do
            for phpExeName in [ "php.exe"; "php-cgi.exe" ] do
                let phpPath = Path.Combine(rootDir, "php", phpExeName)
                match readWindowsFileVersion phpPath with
                | Some(ver, _) ->
                    results.Add({
                        RuntimeId = "php"
                        DisplayName = runtimeDisplayNameForId "php"
                        Version = ver
                        RawVersion = ver.Raw
                        DetectionSource = sprintf "FileVersionInfo (%s)" phpExeName
                        Exposure = exp
                    })
                | None -> ()

        Seq.toList results

    let private linuxNameToRuntimeAndPackages (nameLower: string) =
        if nameLower.Contains("nginx") then Some("nginx", [ "nginx"; "nginx-core"; "nginx-full"; "nginx-light" ])
        elif nameLower.Contains("apache2") || nameLower = "httpd" || nameLower.StartsWith("httpd.") then Some("httpd", [ "apache2"; "apache2-bin"; "httpd" ])
        elif nameLower.Contains("mariadb") then Some("mariadb", [ "mariadb-server"; "mariadb-server-core" ])
        elif nameLower.Contains("mysql") then Some("mysql", [ "mysql-server"; "mysql-community-server"; "mariadb-server" ])
        elif nameLower.Contains("postgres") then Some("postgres", [ "postgresql"; "postgresql-server" ])
        elif nameLower.Contains("redis") then Some("redis", [ "redis-server"; "redis" ])
        elif nameLower.Contains("php") then Some("php", [ "php-fpm"; "php-cli"; "php" ])
        else None

    let private queryLinuxPackageVersionByPath (binaryPath: string) : (string * string) option =
        // 1. dpkg-query -S <path>
        match runPassiveCommand "dpkg-query" [ "-S"; binaryPath ] with
        | Some pkgLine ->
            let pkgName = pkgLine.Split(':').[0].Trim()
            if String.IsNullOrWhiteSpace pkgName then None
            else
                match runPassiveCommand "dpkg-query" [ "-W"; "-f=${Version}\n"; pkgName ] with
                | Some verOut -> Some(verOut, sprintf "dpkg (%s)" pkgName)
                | None -> None
        | None ->
            // 2. rpm -qf <path>
            match runPassiveCommand "rpm" [ "-qf"; "--qf"; "%{NAME}|%{VERSION}\n"; binaryPath ] with
            | Some rpmOut ->
                let parts = rpmOut.Split('|', 2)
                if parts.Length = 2 then Some(parts.[1].Trim(), sprintf "rpm (%s)" (parts.[0].Trim()))
                else None
            | None -> None

    let private queryLinuxPackageVersionByName (pkgCandidates: string list) : (string * string) option =
        pkgCandidates
        |> List.tryPick (fun pkg ->
            match runPassiveCommand "dpkg-query" [ "-W"; "-f=${Status}|${Version}\n"; pkg ] with
            | Some out when out.Contains("install ok installed", StringComparison.OrdinalIgnoreCase) ->
                let parts = out.Split('|', 2)
                if parts.Length = 2 then Some(parts.[1].Trim(), sprintf "dpkg (%s)" pkg) else None
            | _ ->
                match runPassiveCommand "rpm" [ "-q"; "--qf"; "%{VERSION}\n"; pkg ] with
                | Some verOut when not (String.IsNullOrWhiteSpace verOut) ->
                    Some(verOut.Trim(), sprintf "rpm (%s)" pkg)
                | _ -> None)

    let private detectLinuxHostRuntimes (services: ServiceEntry seq) (listeners: ListenerInfo list) : DetectedHostRuntime list =
        let listenersByPort =
            listeners
            |> List.groupBy (fun l -> l.Port)
            |> List.map (fun (p, grp) -> p, grp |> List.maxBy (fun l -> Exposure.rank (Exposure.classify l.BindAddress)))
            |> Map.ofList

        let targets = System.Collections.Generic.Dictionary<string, ExposureLevel * int * string list>()

        let registerTarget (rtId: string) (exp: ExposureLevel) (pid: int) (pkgCandidates: string list) =
            match targets.TryGetValue(rtId) with
            | true, (curExp, curPid, curPkgs) ->
                let bestExp = if Exposure.rank exp > Exposure.rank curExp then exp else curExp
                let bestPid = if curPid > 0 then curPid else pid
                targets.[rtId] <- (bestExp, bestPid, curPkgs)
            | false, _ ->
                targets.[rtId] <- (exp, pid, pkgCandidates)

        for s in services do
            if s.Status.StartsWith("起動中", StringComparison.Ordinal) then
                match linuxNameToRuntimeAndPackages (s.Name.ToLowerInvariant()) with
                | Some(rtId, pkgs) ->
                    let exp = maxExposureForService s listenersByPort
                    let repPid =
                        servicePorts s
                        |> List.tryPick (fun p ->
                            Map.tryFind p listenersByPort
                            |> Option.bind (fun l -> if l.ProcessId > 0 then Some l.ProcessId else None))
                        |> Option.defaultValue 0
                    registerTarget rtId exp repPid pkgs
                | None -> ()

        for l in listeners do
            if not (String.IsNullOrWhiteSpace l.ProcessName) then
                match linuxNameToRuntimeAndPackages (l.ProcessName.ToLowerInvariant()) with
                | Some(rtId, pkgs) ->
                    registerTarget rtId (Exposure.classify l.BindAddress) l.ProcessId pkgs
                | None -> ()

        let results = ResizeArray<DetectedHostRuntime>()
        for kv in targets do
            let rtId = kv.Key
            let exp, pid, pkgCandidates = kv.Value
            let binPathOpt =
                if pid > 0 then
                    try
                        let link = File.ResolveLinkTarget(sprintf "/proc/%d/exe" pid, true)
                        if not (isNull link) && not (String.IsNullOrWhiteSpace link.FullName) then Some link.FullName
                        else None
                    with _ -> None
                else None

            let pkgVerOpt =
                match binPathOpt with
                | Some binPath ->
                    queryLinuxPackageVersionByPath binPath
                    |> Option.orElseWith (fun () -> queryLinuxPackageVersionByName pkgCandidates)
                | None ->
                    queryLinuxPackageVersionByName pkgCandidates

            match pkgVerOpt with
            | Some(rawVer, source) ->
                match tryParseVersion rawVer with
                | Some parsed ->
                    results.Add({
                        RuntimeId = rtId
                        DisplayName = runtimeDisplayNameForId rtId
                        Version = parsed
                        RawVersion = parsed.Raw
                        DetectionSource = source
                        Exposure = exp
                    })
                | None -> ()
            | None -> ()

        Seq.toList results

    /// <summary>OS上の稼働サービスとリスナーからランタイム版本を受動検出します（バイナリ実行は一切行いません）。</summary>
    let detectHostRuntimes (services: ServiceEntry seq) (listeners: ListenerInfo list) : DetectedHostRuntime list =
        try
            if OperatingSystem.IsWindows() then detectWindowsHostRuntimes services listeners
            elif OperatingSystem.IsLinux() then detectLinuxHostRuntimes services listeners
            else []
        with _ -> []

    /// <summary>検出されたホストランタイム群とマスターデータを照合し、露出レベルを加味した診断結果を返します。</summary>
    let diagnoseHostRuntimes
        (detected: DetectedHostRuntime list)
        (referenceDate: DateOnly)
        (snapshot: MasterDataSnapshot)
        : SecurityFinding list =
        let defDate = MasterDataRepository.GetDefinitionDateText snapshot |> Option.defaultValue "N/A"
        let deduplicated =
            detected
            |> List.groupBy (fun r -> r.RuntimeId.ToLowerInvariant(), r.RawVersion)
            |> List.map (fun (_, grp) -> grp |> List.maxBy (fun r -> Exposure.rank r.Exposure))

        deduplicated
        |> List.collect (fun runtime ->
            match snapshot.Runtimes |> List.tryFind (fun r -> String.Equals(r.Id, runtime.RuntimeId, StringComparison.OrdinalIgnoreCase)) with
            | None -> []
            | Some runtimeDef ->
                // 1. 緊急 advisory 照合
                let matchingAdvisories =
                    runtimeDef.Advisories
                    |> List.filter (fun adv -> adv.AffectedRanges |> List.exists (isInRange runtime.Version))

                if not (List.isEmpty matchingAdvisories) then
                    let adv = matchingAdvisories.Head
                    let title =
                        match adv.Requires with
                        | Some _ -> sprintf "ランタイム緊急アドバイザリ（条件付き）: %s" runtime.DisplayName
                        | None -> sprintf "ランタイム緊急アドバイザリ: %s" runtime.DisplayName
                    let statusLine =
                        match runtime.Exposure, adv.Requires with
                        | ExposureLevel.Local, Some req when String.Equals(req, "http2", StringComparison.OrdinalIgnoreCase) ->
                            "重大な脆弱性の対象バージョンです（HTTP/2 有効時は危険）。現在はローカル限定のため外部到達不可です。"
                        | ExposureLevel.Local, Some _ ->
                            "重大な脆弱性の対象バージョンです（成立条件に該当時は危険）。現在はローカル限定のため外部到達不可です。"
                        | ExposureLevel.Local, None ->
                            "重大な脆弱性の対象バージョンですが、現在はローカル限定のため外部到達不可です。"
                        | _, Some req when String.Equals(req, "http2", StringComparison.OrdinalIgnoreCase) ->
                            "対象バージョンです。HTTP/2 を有効にしている場合は危険です。"
                        | _, Some _ ->
                            "対象バージョンです。成立条件に該当する場合は危険です。"
                        | _, None ->
                            "重大な脆弱性の対象バージョンです。修正版への更新を推奨します。"

                    let advIdsText =
                        if List.isEmpty adv.RelatedIds then adv.Id
                        else sprintf "%s (%s)" adv.Id (String.concat ", " adv.RelatedIds)
                    let fixedText =
                        if List.isEmpty adv.FixedIn then "-"
                        else String.concat ", " adv.FixedIn
                    let detail =
                        String.concat "\n" [
                            statusLine
                            sprintf "検出バージョン: %s / 検出経路: %s / 定義データ: %s時点" runtime.RawVersion runtime.DetectionSource defDate
                            sprintf "対象アドバイザリ: %s / 修正版: %s" advIdsText fixedText
                        ]
                    let level, deduction =
                        match runtime.Exposure with
                        | ExposureLevel.Local -> FindingLevel.Information, 0
                        | ExposureLevel.Lan -> FindingLevel.Warning, 25
                        | ExposureLevel.Public -> FindingLevel.Warning, 25
                    [ SecurityFinding(title, detail, level, deduction) ]
                else
                    // 2. EOL ブランチ / 古さ照合
                    match tryFindBranch runtime.Version runtimeDef.Branches with
                    | None -> []
                    | Some(branch, branchVer) ->
                        if isBranchEol referenceDate branch then
                            let title = sprintf "サポート終了 (EOL) ランタイム: %s" runtime.DisplayName
                            let eolLabel = branch.Eol |> Option.defaultValue "EOL"
                            let statusLine =
                                match runtime.Exposure with
                                | ExposureLevel.Local ->
                                    sprintf "使用中のブランチ %s はサポート期限（%s）を終了しています（ローカル限定）。" branch.Branch eolLabel
                                | ExposureLevel.Lan | ExposureLevel.Public ->
                                    sprintf "使用中のブランチ %s はサポート期限（%s）を終了しています。保守中のブランチへの更新を推奨します。" branch.Branch eolLabel
                            let detail =
                                String.concat "\n" [
                                    statusLine
                                    sprintf "検出バージョン: %s / 検出経路: %s / 定義データ: %s時点" runtime.RawVersion runtime.DetectionSource defDate
                                    sprintf "最新バージョン: %s" branch.Latest
                                ]
                            let level, deduction =
                                match runtime.Exposure with
                                | ExposureLevel.Local -> FindingLevel.Information, 0
                                | ExposureLevel.Lan -> FindingLevel.Warning, 10
                                | ExposureLevel.Public -> FindingLevel.Warning, 15
                            [ SecurityFinding(title, detail, level, deduction) ]
                        else
                            let patchLagOpt =
                                if runtime.Version.Segments.Length > branchVer.Segments.Length then
                                    match tryParseVersion branch.Latest with
                                    | Some latestVer when compareVersions latestVer runtime.Version > 0 ->
                                        let idx = branchVer.Segments.Length
                                        let diff = getSegmentAt idx latestVer.Segments - getSegmentAt idx runtime.Version.Segments
                                        if diff >= 3 then Some latestVer else None
                                    | _ -> None
                                else None

                            let lagInfoOpt =
                                match patchLagOpt with
                                | Some latestVer ->
                                    let statusLine = sprintf "使用中のブランチ %s に新しいパッチ版（最新: %s）が公開されています。" branch.Branch latestVer.Raw
                                    Some(statusLine, latestVer.Raw)
                                | None ->
                                    let supportedBranches =
                                        runtimeDef.Branches
                                        |> List.filter (fun b -> not (isBranchEol referenceDate b))
                                        |> List.choose (fun b -> tryParseVersion b.Branch |> Option.map (fun bv -> b, bv))
                                        |> List.sortWith (fun (_, a) (_, b) -> compareVersions b a)
                                    let branchIndex =
                                        supportedBranches
                                        |> List.tryFindIndex (fun (b, _) -> b.Branch = branch.Branch)
                                        |> Option.defaultValue 0
                                    if branchIndex >= 2 && not (List.isEmpty supportedBranches) then
                                        let latestBranch, _ = supportedBranches.Head
                                        let statusLine = sprintf "より新しい安定版ブランチ（%s / 最新: %s）が公開されています。" latestBranch.Branch latestBranch.Latest
                                        Some(statusLine, latestBranch.Latest)
                                    else None

                            match lagInfoOpt with
                            | None -> []
                            | Some(statusLine, latestDisplay) ->
                                match runtime.Exposure with
                                | ExposureLevel.Local -> [] // L0 の古さ・パッチ遅れは沈黙
                                | ExposureLevel.Lan ->
                                    let title = sprintf "ランタイムのバージョン遅れ: %s" runtime.DisplayName
                                    let detail =
                                        String.concat "\n" [
                                            statusLine
                                            sprintf "検出バージョン: %s / 検出経路: %s / 定義データ: %s時点" runtime.RawVersion runtime.DetectionSource defDate
                                            sprintf "最新バージョン: %s" latestDisplay
                                        ]
                                    [ SecurityFinding(title, detail, FindingLevel.Information, 0) ]
                                | ExposureLevel.Public ->
                                    let title = sprintf "ランタイムのバージョン遅れ: %s" runtime.DisplayName
                                    let detail =
                                        String.concat "\n" [
                                            statusLine
                                            sprintf "検出バージョン: %s / 検出経路: %s / 定義データ: %s時点" runtime.RawVersion runtime.DetectionSource defDate
                                            sprintf "最新バージョン: %s" latestDisplay
                                        ]
                                    [ SecurityFinding(title, detail, FindingLevel.Warning, 10) ])
