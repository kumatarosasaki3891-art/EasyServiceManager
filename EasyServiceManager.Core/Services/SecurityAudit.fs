namespace EasyServiceManager.Services

open System
open System.Collections.Generic
open System.ComponentModel
open System.Diagnostics
open System.IO
open System.Net
open System.Net.NetworkInformation
open System.Net.Security
open System.Net.Sockets
open System.Runtime.InteropServices
open System.Security.Cryptography.X509Certificates
open System.Text.RegularExpressions
open System.Threading
open EasyServiceManager.Models

/// <summary>ファイアウォールの有効状態。</summary>
type FirewallState =
    | Enabled
    | Disabled
    | Unknown

/// <summary>ローカル環境のセキュリティ設定を簡易診断してスコアを算出するモジュール。</summary>
module SecurityAudit =
    type private CommandResult = { Output: string; ExitCode: int }

    /// <summary>外部コマンドを実行し、標準出力と終了コードを返します。</summary>
    let private runCommand executable (arguments: string list) =
        // Flatpak 内では systemctl 等が存在しないため、ホスト側で実行する
        let inFlatpak = File.Exists("/.flatpak-info")
        let effectiveExecutable = if inFlatpak then "flatpak-spawn" else executable
        let effectiveArguments =
            if inFlatpak then "--host" :: executable :: arguments else arguments
        let info = ProcessStartInfo(effectiveExecutable)
        info.UseShellExecute <- false
        info.RedirectStandardOutput <- true
        info.RedirectStandardError <- true
        info.CreateNoWindow <- true
        if inFlatpak then info.WorkingDirectory <- "/"
        for argument in effectiveArguments do info.ArgumentList.Add(argument)
        try
            use childProcess = new Process(StartInfo = info)
            if not (childProcess.Start()) then None
            else
                let outputTask = childProcess.StandardOutput.ReadToEndAsync()
                let errorTask = childProcess.StandardError.ReadToEndAsync()
                if not (childProcess.WaitForExit(10000)) then
                    childProcess.Kill(true)
                    Some { Output = ""; ExitCode = -1 }
                else
                    let output = outputTask.GetAwaiter().GetResult()
                    let _ = errorTask.GetAwaiter().GetResult()
                    Some { Output = output; ExitCode = childProcess.ExitCode }
        with :? Win32Exception ->
            None

    /// <summary>診断結果項目を生成します。</summary>
    let private add title detail level deduction =
        SecurityFinding(title, detail, level, deduction)

    let private unknown title detail = add title detail FindingLevel.Unknown 0
    let private good title detail = add title detail FindingLevel.Good 0
    let private warning title detail deduction = add title detail FindingLevel.Warning deduction
    let private info title detail = add title detail FindingLevel.Information 0

    let private dbPorts = set [ 3306; 5432; 6379; 27017; 9200 ]
    let private linuxAdminPorts = set [ 5601; 5000; 445 ]
    let private windowsAdminPorts = set [ 5601; 5000 ]

    /// <summary>Linux の UFW / firewalld の有効状態を確認します。</summary>
    let private inspectLinuxFirewall () : SecurityFinding * FirewallState =
        let ufwResult = runCommand "ufw" [ "status" ]
        match ufwResult with
        | Some result when result.Output.Contains("Status: active", StringComparison.OrdinalIgnoreCase) ->
            good "Firewall (UFW)" "UFWは有効です。", FirewallState.Enabled
        | _ ->
            match runCommand "firewall-cmd" [ "--state" ] with
            | Some result when result.ExitCode = 0 && result.Output.Trim() = "running" ->
                good "Firewall (firewalld)" "firewalldは稼働中です。", FirewallState.Enabled
            | _ ->
                match runCommand "systemctl" [ "is-active"; "firewalld" ] with
                | Some result when result.Output.Trim() = "active" ->
                    good "Firewall (firewalld)" "firewalldは稼働中です。", FirewallState.Enabled
                | Some result when result.Output.Trim() = "inactive" ->
                    warning "Firewall" "UFW/firewalldはいずれも有効ではありません。" 25, FirewallState.Disabled
                | _ ->
                    match ufwResult with
                    | Some result when result.Output.Contains("Status: inactive", StringComparison.OrdinalIgnoreCase) ->
                        warning "Firewall" "UFWは無効です。firewalldの状態も確認できませんでした。" 25, FirewallState.Disabled
                    | _ ->
                        unknown "Firewall" "UFW/firewalldを確認できませんでした。未導入または権限不足の可能性があります。", FirewallState.Unknown

    /// <summary>Windows の Firewall プロファイル有効状態を確認します。</summary>
    let private inspectWindowsFirewall () : SecurityFinding * FirewallState =
        let firewallScript =
            "Get-NetFirewallProfile | ForEach-Object { '{0}|{1}' -f $_.Name,$_.Enabled }"
        match runCommand "powershell.exe" [ "-NoProfile"; "-NonInteractive"; "-Command"; firewallScript ] with
        | Some result when result.ExitCode = 0 ->
            let profiles = result.Output.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
            if profiles.Length < 3 then
                unknown "Windows Firewall" "Firewallプロファイルをすべて確認できませんでした。", FirewallState.Unknown
            elif profiles |> Array.exists (fun line -> line.EndsWith("|False", StringComparison.OrdinalIgnoreCase)) then
                warning "Windows Firewall" "無効なファイアウォールプロファイルがあります。" 25, FirewallState.Disabled
            else
                good "Windows Firewall" "各ネットワークプロファイルでFirewallが有効です。", FirewallState.Enabled
        | _ ->
            unknown "Windows Firewall" "Firewall状態を確認できませんでした。", FirewallState.Unknown

    /// <summary>共有されたリスナー情報と FW 状態から、ポート露出（L0/L1/L2 × 区分）を評価します。</summary>
    let inspectPortExposure (isWindows: bool) (listeners: ListenerInfo list) (fwState: FirewallState) : SecurityFinding list =
        let adminPorts = if isWindows then windowsAdminPorts else linuxAdminPorts
        let effectiveByPort =
            listeners
            |> List.filter (fun l -> l.Port > 0)
            |> List.groupBy (fun l -> l.Port)
            |> List.map (fun (port, grp) ->
                let maxExp = grp |> List.map (fun l -> Exposure.classify l.BindAddress) |> List.maxBy Exposure.rank
                port, maxExp)

        let collect bucketPred targetExp =
            effectiveByPort
            |> List.filter (fun (p, exp) -> exp = targetExp && bucketPred p)
            |> List.map fst
            |> List.distinct
            |> List.sort

        let isDb p = Set.contains p dbPorts
        let isAdmin p = Set.contains p adminPorts
        let isGeneral p = not (isDb p) && not (isAdmin p) && not (isWindows && p = 445)

        let dbL2 = collect isDb ExposureLevel.Public
        let dbL1 = collect isDb ExposureLevel.Lan
        let adminL2 = collect isAdmin ExposureLevel.Public
        let adminL1 = collect isAdmin ExposureLevel.Lan
        let generalExposed =
            effectiveByPort
            |> List.filter (fun (p, exp) -> isGeneral p && (exp = ExposureLevel.Lan || exp = ExposureLevel.Public))
            |> List.map fst
            |> List.distinct
            |> List.sort

        let fwNoteForL2 =
            match fwState with
            | FirewallState.Disabled -> "Firewallも無効のため、外部から到達できる可能性が高いです。"
            | FirewallState.Enabled -> "Firewallが有効なため、到達性は制限される場合があります。"
            | FirewallState.Unknown -> "外部から到達可能かはFirewallやNATにも依存します。"

        let dbAuthNote (ports: int list) =
            let hasRedis = ports |> List.contains 6379
            let hasMongo = ports |> List.contains 27017
            if hasRedis && hasMongo then
                Some "認証設定（Redis: requirepass / protected-mode、MongoDB: security.authorization）の確認を推奨します。"
            elif hasRedis then
                Some "認証設定（Redis: requirepass / protected-mode）の確認を推奨します。"
            elif hasMongo then
                Some "認証設定（MongoDB: security.authorization）の確認を推奨します。"
            else None

        let joinPorts (ports: int list) = ports |> List.map string |> String.concat ", "

        let findings = ResizeArray<SecurityFinding>()

        if not dbL2.IsEmpty then
            let lines =
                [ yield sprintf "ポート %s が全インターフェースで待ち受けています。" (joinPorts dbL2)
                  yield fwNoteForL2
                  match dbAuthNote dbL2 with Some n -> yield n | None -> () ]
            findings.Add(warning "要注意ポート（DB・全公開）" (String.concat "\n" lines) 25)

        if not dbL1.IsEmpty then
            let lines =
                [ yield sprintf "ポート %s がLAN（特定アドレス）で待ち受けています。" (joinPorts dbL1)
                  yield "同一LAN内から到達可能な状態です。"
                  match dbAuthNote dbL1 with Some n -> yield n | None -> () ]
            findings.Add(warning "要注意ポート（DB・LAN公開）" (String.concat "\n" lines) 10)

        if not adminL2.IsEmpty then
            let lines =
                [ sprintf "ポート %s が全インターフェースで待ち受けています。" (joinPorts adminL2)
                  fwNoteForL2 ]
            findings.Add(warning "要注意ポート（管理系・全公開）" (String.concat "\n" lines) 25)

        if not adminL1.IsEmpty then
            let lines =
                [ sprintf "ポート %s がLAN（特定アドレス）で待ち受けています。" (joinPorts adminL1)
                  "同一LAN内から到達可能な状態です。" ]
            findings.Add(warning "要注意ポート（管理系・LAN公開）" (String.concat "\n" lines) 10)

        if findings.Count = 0 then
            findings.Add(good "要注意ポート" "要注意ポートのLAN・全インターフェース待受は検出されませんでした。")

        if not generalExposed.IsEmpty then
            let displayed =
                if generalExposed.Length <= 10 then joinPorts generalExposed
                else sprintf "%s, ... (+%d)" (joinPorts (generalExposed |> List.truncate 10)) (generalExposed.Length - 10)
            findings.Add(info "一般公開ポート" (sprintf "一般ポートが外部・LAN向けに待ち受けています: %s" displayed))

        Seq.toList findings

    /// <summary>Windows 環境のシステム共有プロセス (PID 4) による SMB 445 全公開を検査します。</summary>
    let private inspectWindowsUnassignedSmb (listeners: ListenerInfo list) =
        let hasWildcardSmb =
            listeners
            |> List.exists (fun l ->
                l.Port = 445 && l.ProcessId = 4 && Exposure.classify l.BindAddress = ExposureLevel.Public)
        if hasWildcardSmb then
            warning "帰属できない待ち受け" "SMB (ポート 445) が全インターフェースで待ち受けています（システム共有プロセス PID 4）。外部到達性はFirewallやNATにも依存します。" 15
        else
            good "帰属できない待ち受け" "要注意なシステム未帰属リスナーは検出されませんでした。"

    /// <summary>Redis 設定ファイル内容とリスナー状態から認証設定を評価します（パスワード値自体は読み取りません）。</summary>
    let evaluateRedisConfig (confText: string) (hasNonLoopbackListener: bool) : SecurityFinding =
        let mutable hasRequirepass = false
        let mutable protectedModeYes = true // Redis の既定は yes
        let mutable confHasNonLoopback = false
        let mutable hasBindDirective = false

        let isLoopbackAddr (addr: string) =
            let a = addr.Trim().ToLowerInvariant()
            a = "127.0.0.1" || a = "::1" || a = "localhost" || a.StartsWith("127.", StringComparison.Ordinal)

        for rawLine in confText.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries) do
            let line = rawLine.Trim()
            if not (String.IsNullOrWhiteSpace line) && not (line.StartsWith("#", StringComparison.Ordinal)) then
                let tokens = line.Split([| ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries)
                if tokens.Length >= 2 then
                    let key = tokens.[0].ToLowerInvariant()
                    let val1 = tokens.[1].Trim('"', '\'').ToLowerInvariant()
                    if key = "requirepass" && not (String.IsNullOrWhiteSpace val1) then
                        hasRequirepass <- true
                    elif key = "protected-mode" then
                        protectedModeYes <- (val1 <> "no" && val1 <> "false" && val1 <> "0")
                    elif key = "bind" then
                        hasBindDirective <- true
                        let addrs = tokens.[1..] |> Array.filter (fun t -> not (t.StartsWith("#", StringComparison.Ordinal)))
                        if addrs |> Array.exists (fun a -> not (isLoopbackAddr a)) then
                            confHasNonLoopback <- true

        let nonLoopback = hasNonLoopbackListener || (if hasBindDirective then confHasNonLoopback else false)
        if hasRequirepass then
            good "Redis認証設定" "Redis はパスワード認証（requirepass）が設定されています。"
        elif not protectedModeYes && nonLoopback then
            warning "Redis認証設定" "Redis に requirepass が未設定かつ protected-mode no で非ループバック待受が有効です（認証なし公開の危険）。" 25
        else
            warning "Redis認証設定" "Redis に requirepass が未設定です（protected-mode yes で保護中）。" 10

    /// <summary>MongoDB 設定ファイル内容とリスナー状態から認証設定を評価します。</summary>
    let evaluateMongoConfig (confText: string) (hasNonLoopbackListener: bool) : SecurityFinding =
        let mutable currentSection = ""
        let mutable authEnabled = false
        let mutable confHasNonLoopback = false

        let isLoopbackAddr (addr: string) =
            let a = addr.Trim().ToLowerInvariant()
            a = "127.0.0.1" || a = "::1" || a = "localhost" || a.StartsWith("127.", StringComparison.Ordinal)

        for rawLine in confText.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries) do
            let withoutComment =
                let hashIdx = rawLine.IndexOf('#')
                if hashIdx >= 0 then rawLine.Substring(0, hashIdx) else rawLine
            if not (String.IsNullOrWhiteSpace withoutComment) then
                let indent = withoutComment.Length - withoutComment.TrimStart().Length
                let trimmed = withoutComment.Trim()
                if indent = 0 && trimmed.EndsWith(":", StringComparison.Ordinal) then
                    currentSection <- trimmed.TrimEnd(':').ToLowerInvariant()
                else
                    let colonIdx = trimmed.IndexOf(':')
                    if colonIdx > 0 then
                        let key = trimmed.Substring(0, colonIdx).Trim().ToLowerInvariant()
                        let value = trimmed.Substring(colonIdx + 1).Trim().Trim('"', '\'')
                        if currentSection = "security" && key = "authorization" then
                            authEnabled <- value.Equals("enabled", StringComparison.OrdinalIgnoreCase)
                        elif currentSection = "net" && key = "bindip" then
                            let ips = value.Split([| ','; ' ' |], StringSplitOptions.RemoveEmptyEntries)
                            if ips |> Array.exists (fun ip -> not (isLoopbackAddr ip)) then
                                confHasNonLoopback <- true

        let nonLoopback = hasNonLoopbackListener || confHasNonLoopback
        if authEnabled then
            good "MongoDB認証設定" "MongoDB は認証（security.authorization: enabled）が有効です。"
        elif nonLoopback then
            warning "MongoDB認証設定" "MongoDB の認証（security.authorization: enabled）が無効のまま非ループバック待受が有効です。" 25
        else
            info "MongoDB認証設定" "MongoDB の認証（security.authorization: enabled）は未設定ですが、ローカル（ループバック）限定です。"

    let private inspectLinuxDbConfigs (listeners: ListenerInfo list) : SecurityFinding list =
        let tryReadFirstReadable (paths: string list) =
            paths
            |> List.tryPick (fun p ->
                try
                    if File.Exists(p) then Some(File.ReadAllText(p)) else None
                with _ -> None)

        let hasNonLoopbackOnPort port =
            listeners
            |> List.exists (fun l ->
                l.Port = port &&
                let exp = Exposure.classify l.BindAddress
                exp = ExposureLevel.Lan || exp = ExposureLevel.Public)

        [ match tryReadFirstReadable [ "/etc/redis/redis.conf"; "/etc/redis.conf"; "/usr/local/etc/redis/redis.conf" ] with
          | Some text -> yield evaluateRedisConfig text (hasNonLoopbackOnPort 6379)
          | None -> ()
          match tryReadFirstReadable [ "/etc/mongod.conf"; "/etc/mongodb.conf" ] with
          | Some text -> yield evaluateMongoConfig text (hasNonLoopbackOnPort 27017)
          | None -> () ]

    /// <summary>sshd -T から実効 SSH 設定を取得します。</summary>
    let private effectiveSshSettings () =
        match runCommand "sshd" [ "-T" ] with
        | Some result when result.ExitCode = 0 ->
            let values =
                result.Output.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
                |> Seq.choose (fun line ->
                    let parts = line.Trim().Split([| ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries)
                    if parts.Length >= 2 then Some(parts.[0].ToLowerInvariant(), parts.[1].ToLowerInvariant())
                    else None)
                |> Map.ofSeq
            Some values
        | _ -> None

    /// <summary>SSH の root ログインとパスワード認証の設定を検査します。</summary>
    let private inspectSsh () =
        match effectiveSshSettings () with
        | None -> [ unknown "SSH rootログイン" "sshdの実効設定を取得できませんでした。"
                    unknown "SSHパスワード認証" "sshdの実効設定を取得できませんでした。" ]
        | Some settings ->
            let get key = Map.tryFind key settings
            let rootFinding =
                match get "permitrootlogin" with
                | Some "no" -> good "SSH rootログイン" "PermitRootLogin no が有効です。"
                | Some value -> warning "SSH rootログイン" (sprintf "PermitRootLogin %s です。" value) 15
                | None -> unknown "SSH rootログイン" "PermitRootLoginを確認できませんでした。"
            let passwordFinding =
                match get "passwordauthentication" with
                | Some "no" -> good "SSHパスワード認証" "PasswordAuthentication no が有効です。"
                | Some value -> warning "SSHパスワード認証" (sprintf "PasswordAuthentication %s です。" value) 10
                | None -> unknown "SSHパスワード認証" "PasswordAuthenticationを確認できませんでした。"
            [ rootFinding; passwordFinding ]

    /// <summary>HTTP (80) および HTTPS (443) ポートの待ち受け状況を確認します。</summary>
    let private inspectHttps (listeners: ListenerInfo list) =
        let hasHttps = listeners |> List.exists (fun l -> l.Port = 443)
        let hasHttp = listeners |> List.exists (fun l -> l.Port = 80)
        if hasHttps then good "HTTPSポート" "TCP/443の待受を検出しました。"
        elif hasHttp then warning "HTTPSポート" "TCP/80の待受があり、TCP/443は検出されませんでした。" 10
        else unknown "HTTPSポート" "TCP/80/443の待受を検出できませんでした。"

    /// <summary>証明書の有効期限日時からセキュリティ診断結果を判定します。</summary>
    let evaluateCertificateExpiry (port: int) (notAfterUtc: DateTime) (nowUtc: DateTime) : SecurityFinding =
        let title = sprintf "証明書の有効期限（ポート %d）" port
        let expiryStr = notAfterUtc.ToString("yyyy-MM-dd")
        if notAfterUtc <= nowUtc then
            let daysAgo = max 0 (int (Math.Floor((nowUtc - notAfterUtc).TotalDays)))
            warning title (sprintf "証明書が期限切れです（有効期限: %s、%d日前に失効）。" expiryStr daysAgo) 25
        else
            let daysLeft = int (Math.Floor((notAfterUtc - nowUtc).TotalDays))
            if daysLeft <= 14 then
                warning title (sprintf "証明書の有効期限が近づいています（有効期限: %s、残り %d日）。" expiryStr daysLeft) 15
            elif daysLeft <= 30 then
                warning title (sprintf "証明書の有効期限が近づいています（有効期限: %s、残り %d日）。" expiryStr daysLeft) 10
            else
                good title (sprintf "証明書は有効です（有効期限: %s、残り %d日）。" expiryStr daysLeft)

    /// <summary>開かれた接続ストリーム上で TLS ハンドシェイクを行い、提示証明書の有効期限 (UTC) を取得します。</summary>
    let probeCertificateFromStream (stream: Stream) (sniHost: string) (timeoutMs: int) : DateTime option =
        try
            use cts = new CancellationTokenSource(timeoutMs)
            let mutable capturedNotAfter: DateTime option = None
            let callback =
                RemoteCertificateValidationCallback(fun _ cert _ _ ->
                    if not (isNull cert) then
                        try
                            use x509 = new X509Certificate2(cert)
                            capturedNotAfter <- Some(x509.NotAfter.ToUniversalTime())
                        with _ -> ()
                    true)
            use ssl = new SslStream(stream, false, callback)
            let targetHost = if String.IsNullOrWhiteSpace sniHost then "localhost" else sniHost
            let authOpts =
                SslClientAuthenticationOptions(
                    TargetHost = targetHost,
                    RemoteCertificateValidationCallback = callback)
            ssl.AuthenticateAsClientAsync(authOpts, cts.Token).GetAwaiter().GetResult()
            match capturedNotAfter with
            | Some dt -> Some dt
            | None ->
                if not (isNull ssl.RemoteCertificate) then
                    use x509 = new X509Certificate2(ssl.RemoteCertificate)
                    Some(x509.NotAfter.ToUniversalTime())
                else None
        with _ -> None

    /// <summary>自マシンループバック（127.0.0.1 / ::1）の指定ポートへ TLS 接続し、提示証明書の有効期限 (UTC) を取得します。</summary>
    let probeLoopbackCertificate (port: int) (sniHost: string) (timeoutMs: int) : DateTime option =
        let tryEndpoint (addr: IPAddress) =
            try
                use cts = new CancellationTokenSource(timeoutMs)
                use client = new TcpClient(addr.AddressFamily)
                client.ConnectAsync(addr, port, cts.Token).AsTask().GetAwaiter().GetResult()
                probeCertificateFromStream (client.GetStream()) sniHost timeoutMs
            with _ -> None

        tryEndpoint IPAddress.Loopback
        |> Option.orElseWith (fun () -> tryEndpoint IPAddress.IPv6Loopback)

    /// <summary>443 リスナーおよび https:// サービス URL の TLS 証明書有効期限を検査します。</summary>
    let private inspectCertificates (services: ServiceEntry seq) (listeners: ListenerInfo list) : SecurityFinding list =
        let targets = Dictionary<int, string>()

        for l in listeners do
            if l.Port = 443 && not (targets.ContainsKey(443)) then
                targets.[443] <- "localhost"

        for s in services do
            if not (String.IsNullOrWhiteSpace s.Url) then
                match Uri.TryCreate(s.Url.Trim(), UriKind.Absolute) with
                | true, uri when String.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ->
                    let port = if uri.Port > 0 then uri.Port else 443
                    let host = if String.IsNullOrWhiteSpace uri.Host then "localhost" else uri.Host
                    match targets.TryGetValue(port) with
                    | true, existing when existing = "localhost" && host <> "localhost" -> targets.[port] <- host
                    | false, _ -> targets.[port] <- host
                    | _ -> ()
                | _ -> ()

        let nowUtc = DateTime.UtcNow
        targets
        |> Seq.sortBy (fun kv -> kv.Key)
        |> Seq.map (fun kv ->
            match probeLoopbackCertificate kv.Key kv.Value 3000 with
            | Some notAfterUtc -> evaluateCertificateExpiry kv.Key notAfterUtc nowUtc
            | None ->
                unknown
                    (sprintf "証明書の有効期限（ポート %d）" kv.Key)
                    "TLS証明書を取得できませんでした（非TLSポートまたは接続拒否の可能性があります）。")
        |> Seq.toList

    type private DnsResolutionOutcome =
        | Resolved of IPAddress array
        | Failed
        | TimedOut

    let private resolveHostWithTimeout (host: string) (timeoutMs: int) : DnsResolutionOutcome =
        try
            use cts = new CancellationTokenSource(timeoutMs)
            let addrs = Dns.GetHostAddressesAsync(host, cts.Token).GetAwaiter().GetResult()
            if isNull addrs || addrs.Length = 0 then Failed else Resolved addrs
        with
        | :? OperationCanceledException -> TimedOut
        | :? AggregateException as agg ->
            if agg.InnerExceptions |> Seq.exists (fun e -> e :? OperationCanceledException) then TimedOut
            else Failed
        | _ -> Failed

    let private getLocalInterfaceIps () : Set<string> =
        let ips = ResizeArray<string>()
        ips.Add("127.0.0.1")
        ips.Add("::1")
        try
            for ni in NetworkInterface.GetAllNetworkInterfaces() do
                if ni.OperationalStatus = OperationalStatus.Up then
                    for ua in ni.GetIPProperties().UnicastAddresses do
                        ips.Add(ua.Address.ToString())
        with _ -> ()
        Set.ofSeq ips

    /// <summary>自マシンの名前解決と DNS サーバー設定を確認します（外部名テストや逆引きは行いません）。</summary>
    let inspectDns (services: ServiceEntry seq) : SecurityFinding list =
        let findings = ResizeArray<SecurityFinding>()

        // 1. localhost
        match resolveHostWithTimeout "localhost" 2000 with
        | Resolved addrs when addrs |> Array.exists IPAddress.IsLoopback ->
            findings.Add(good "DNS (localhost)" "localhost はループバックアドレスに解決できます。")
        | Resolved _ | Failed ->
            findings.Add(warning "DNS (localhost)" "localhost がループバックアドレス（127.0.0.1 / ::1）に解決できません。" 15)
        | TimedOut ->
            findings.Add(unknown "DNS (localhost)" "localhost の名前解決を確認できませんでした（タイムアウトまたはエラー）。")

        // 2. 自ホスト名
        try
            let hostName = Dns.GetHostName()
            if String.IsNullOrWhiteSpace hostName then
                findings.Add(unknown "DNS (自ホスト名)" "自ホスト名の名前解決を確認できませんでした（タイムアウトまたはエラー）。")
            else
                match resolveHostWithTimeout hostName 2000 with
                | Resolved addrs ->
                    let localIps = getLocalInterfaceIps ()
                    let matchesLocal =
                        addrs
                        |> Array.exists (fun a -> IPAddress.IsLoopback(a) || Set.contains (a.ToString()) localIps)
                    if matchesLocal then
                        findings.Add(good "DNS (自ホスト名)" (sprintf "自ホスト名（%s）を自マシンのアドレスに解決できます。" hostName))
                    else
                        findings.Add(warning "DNS (自ホスト名)" (sprintf "自ホスト名（%s）を自マシンのアドレスに解決できません。" hostName) 10)
                | Failed ->
                    findings.Add(warning "DNS (自ホスト名)" (sprintf "自ホスト名（%s）を自マシンのアドレスに解決できません。" hostName) 10)
                | TimedOut ->
                    findings.Add(unknown "DNS (自ホスト名)" "自ホスト名の名前解決を確認できませんでした（タイムアウトまたはエラー）。")
        with _ ->
            findings.Add(unknown "DNS (自ホスト名)" "自ホスト名の名前解決を確認できませんでした（タイムアウトまたはエラー）。")

        // 3. サービス URL のホスト名（localhost と IP リテラルを除く）
        let urlHosts =
            services
            |> Seq.choose (fun s ->
                if String.IsNullOrWhiteSpace s.Url then None
                else
                    match Uri.TryCreate(s.Url.Trim(), UriKind.Absolute) with
                    | true, uri when not (String.IsNullOrWhiteSpace uri.Host) ->
                        let h = uri.Host.Trim()
                        if String.Equals(h, "localhost", StringComparison.OrdinalIgnoreCase) then None
                        else
                            let mutable ip = Unchecked.defaultof<IPAddress>
                            if IPAddress.TryParse(h, &ip) then None
                            else Some h
                    | _ -> None)
            |> Seq.distinctBy (fun h -> h.ToLowerInvariant())
            |> Seq.toList

        if not urlHosts.IsEmpty then
            let okHosts = ResizeArray<string>()
            let failedHosts = ResizeArray<string>()
            let timedOutHosts = ResizeArray<string>()
            for h in urlHosts do
                match resolveHostWithTimeout h 2000 with
                | Resolved _ -> okHosts.Add(h)
                | Failed -> failedHosts.Add(h)
                | TimedOut -> timedOutHosts.Add(h)

            if failedHosts.Count > 0 then
                findings.Add(warning "DNS (サービスURL)" (sprintf "以下のサービスURLホスト名を解決できません: %s" (String.concat ", " failedHosts)) 10)
            elif timedOutHosts.Count > 0 then
                findings.Add(unknown "DNS (サービスURL)" "サービスURLホスト名の解決確認中にタイムアウトまたはエラーが発生しました。")
            else
                findings.Add(good "DNS (サービスURL)" (sprintf "検出サービス・カスタムサービスURLのホスト名（%s）は解決できます。" (String.concat ", " okHosts)))

        // 4. DNS サーバーの設定有無
        let dnsConfigCountOpt =
            try
                if RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && File.Exists("/etc/resolv.conf") then
                    try
                        let lines = File.ReadAllLines("/etc/resolv.conf")
                        let count =
                            lines
                            |> Array.filter (fun l ->
                                let t = l.Trim()
                                t.StartsWith("nameserver", StringComparison.OrdinalIgnoreCase) &&
                                t.Split([| ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries).Length >= 2)
                            |> Array.length
                        Some count
                    with _ -> None
                else
                    None
                |> Option.orElseWith (fun () ->
                    let mutable count = 0
                    for ni in NetworkInterface.GetAllNetworkInterfaces() do
                        if ni.OperationalStatus = OperationalStatus.Up && ni.NetworkInterfaceType <> NetworkInterfaceType.Loopback then
                            count <- count + ni.GetIPProperties().DnsAddresses.Count
                    Some count)
            with _ -> None

        match dnsConfigCountOpt with
        | Some count when count > 0 ->
            findings.Add(good "DNSサーバー設定" (sprintf "DNSサーバーが設定されています（%d 件）。" count))
        | Some _ ->
            findings.Add(warning "DNSサーバー設定" "有効なDNSサーバー設定が検出されませんでした。" 10)
        | None ->
            findings.Add(unknown "DNSサーバー設定" "DNSサーバー設定を取得できませんでした。")

        Seq.toList findings

    /// <summary>Docker / Podman コンテナーの特権モードと公開ポートを検査します。</summary>
    let private inspectContainerRuntime title executable prefix =
        let containerIds =
            match runCommand executable (prefix @ [ "ps"; "--format"; "{{.ID}}" ]) with
            | Some result when result.ExitCode = 0 ->
                Some(Ok(result.Output.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)))
            | Some _ -> Some(Error())
            | None -> None
        match containerIds with
        | None -> None
        | Some(Error()) -> Some [ unknown (title + "稼働") "ローカルランタイムに接続できません。起動状態やアクセス権を確認してください。" ]
        | Some(Ok ids) when ids.Length = 0 ->
            Some [ good (title + "稼働") "稼働中のコンテナーはありません。" ]
        | Some(Ok ids) ->
            let format =
                "{{.Name}}|{{.HostConfig.Privileged}}{{range $port, $bindings := .HostConfig.PortBindings}}{{range $bindings}}|{{.HostIp}}:{{.HostPort}}{{end}}{{end}}"
            match runCommand executable (prefix @ [ "inspect"; "--format"; format ] @ (ids |> Array.toList)) with
            | Some result when result.ExitCode = 0 ->
                let entries = result.Output.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
                let privileged =
                    entries
                    |> Array.choose (fun line ->
                        let columns = line.Split('|')
                        if columns.Length >= 2 && columns.[1].Equals("true", StringComparison.OrdinalIgnoreCase) then
                            Some columns.[0]
                        else None)
                let publicBindings =
                    entries
                    |> Array.collect (fun line ->
                        let columns = line.Split('|')
                        if columns.Length < 3 then [||]
                        else
                            columns.[2..]
                            |> Array.choose (fun binding ->
                                let separator = binding.LastIndexOf(':')
                                if separator < 0 then None
                                else
                                    let host = binding.Substring(0, separator).Trim('[', ']')
                                    let port = binding.Substring(separator + 1)
                                    let isWildcard =
                                        String.IsNullOrEmpty host
                                        || host = "0.0.0.0"
                                        || host = "::"
                                    if isWildcard then Some(port) else None))
                    |> Array.distinct
                let privilegeFinding =
                    if privileged.Length = 0 then
                        good (title + "特権コンテナー") "privilegedコンテナーは検出されませんでした。"
                    else
                        warning (title + "特権コンテナー")
                            (sprintf "privilegedコンテナーがあります: %s" (String.concat ", " privileged)) 20
                let bindingFinding =
                    if publicBindings.Length = 0 then
                        good (title + "ポート公開") "全インターフェースへのポート公開は検出されませんでした。"
                    else
                        warning (title + "ポート公開")
                            (sprintf "全インターフェースに公開されたホストポート: %s。外部到達性はFirewallやNATにも依存します。" (String.concat ", " publicBindings)) 15
                Some [ privilegeFinding; bindingFinding ]
            | _ -> Some [ unknown (title + "診断") "コンテナー設定を取得できませんでした。権限またはランタイム状態を確認してください。" ]

    /// <summary>ローカルのコンテナーランタイム（Docker / Podman）を診断します。</summary>
    let private inspectContainers () =
        let runtimeDirectory = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR")
        let dockerSocket =
            if not (String.IsNullOrWhiteSpace runtimeDirectory)
               && File.Exists(Path.Combine(runtimeDirectory, "docker.sock")) then
                "unix://" + Path.Combine(runtimeDirectory, "docker.sock")
            else "unix:///var/run/docker.sock"
        let docker = inspectContainerRuntime "Docker" "docker" [ "--host"; dockerSocket ]
        let podman = inspectContainerRuntime "Podman" "podman" [ "--remote=false" ]
        match docker, podman with
        | None, None -> [ add "Docker/Podman" "ローカルCLIを確認できませんでした。" FindingLevel.Information 0 ]
        | _ ->
            [ yield! docker |> Option.defaultValue []
              yield! podman |> Option.defaultValue [] ]

    /// <summary>Windows 環境の SSH (OpenSSH) 稼働状態を簡易検査します。</summary>
    let private inspectWindowsSsh () =
        let sshScript = "Get-Service sshd -ErrorAction SilentlyContinue | ForEach-Object { $_.Status }"
        match runCommand "powershell.exe" [ "-NoProfile"; "-NonInteractive"; "-Command"; sshScript ] with
        | Some result when result.ExitCode = 0 ->
            let status = result.Output.Trim()
            if String.IsNullOrWhiteSpace status || status.Equals("Stopped", StringComparison.OrdinalIgnoreCase) then
                good "SSH設定" "OpenSSHサーバーは未稼働（安全）"
            elif status.Equals("Running", StringComparison.OrdinalIgnoreCase) then
                unknown "SSH設定" "WindowsのOpenSSHサーバー設定診断は未対応です。"
            else
                good "SSH設定" "OpenSSHサーバーは未稼働（安全）"
        | _ -> good "SSH設定" "OpenSSHサーバーは未稼働（安全）"

    /// <summary>Windows 環境の Docker / Podman コンテナー公開ポートを簡易検査します。</summary>
    let private inspectWindowsContainers () =
        let checkRuntime (exe: string) =
            match runCommand exe [ "ps"; "--format"; "{{.Ports}}" ] with
            | Some result when result.ExitCode = 0 -> Some result.Output
            | _ -> None

        let outputOpt =
            match checkRuntime "docker" with
            | Some out -> Some out
            | None -> checkRuntime "podman"

        match outputOpt with
        | None ->
            good "コンテナー設定" "稼働中のコンテナーはありません。"
        | Some output ->
            let lines = output.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
            if lines.Length = 0 then
                good "コンテナー設定" "稼働中のコンテナーはありません。"
            else
                let regex = Regex("(?:0\\.0\\.0\\.0|:::|\\*):(\\d+)->")
                let publicPorts =
                    lines
                    |> Array.collect (fun line ->
                        regex.Matches(line)
                        |> Seq.cast<Match>
                        |> Seq.map (fun m -> m.Groups.[1].Value)
                        |> Seq.toArray)
                    |> Array.distinct
                    |> Array.sort
                if publicPorts.Length = 0 then
                    good "コンテナー設定" "全インターフェースへのポート公開は検出されませんでした。"
                else
                    warning "コンテナー設定"
                        (sprintf "全インターフェースに公開されたコンテナーポート: %s。外部到達性はFirewallやNATにも依存します。" (String.concat ", " publicPorts)) 15

    /// <summary>検出済みサービス一覧・リスナー情報・マスターデータを受け取り、リスナー情報を共用してセキュリティ簡易診断を実行します。</summary>
    let inspectWithContext
        (services: ServiceEntry seq)
        (listenersOpt: ListenerInfo list option)
        (masterSnapshotOpt: MasterDataSnapshot option)
        : SecurityFinding list =
        let serviceList = services |> Seq.toList
        let listeners =
            match listenersOpt with
            | Some l -> l
            | None ->
                try
                    let _, l = ServiceDiscovery.detectWithListeners ()
                    l
                with _ -> []
        let snapshot =
            match masterSnapshotOpt with
            | Some s -> s
            | None -> MasterDataRepository.GetSnapshot()

        let baseFindings =
            if RuntimeInformation.IsOSPlatform(OSPlatform.Linux) then
                let firewall, fwState = inspectLinuxFirewall ()
                [ yield firewall
                  yield! inspectPortExposure false listeners fwState
                  yield! inspectLinuxDbConfigs listeners
                  yield! inspectSsh ()
                  yield inspectHttps listeners
                  yield! inspectCertificates serviceList listeners
                  yield! inspectDns serviceList
                  yield! inspectContainers () ]
            elif RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then
                let firewall, fwState = inspectWindowsFirewall ()
                [ yield firewall
                  yield! inspectPortExposure true listeners fwState
                  yield inspectWindowsUnassignedSmb listeners
                  yield inspectHttps listeners
                  yield! inspectCertificates serviceList listeners
                  yield! inspectDns serviceList
                  yield inspectWindowsSsh ()
                  yield inspectWindowsContainers () ]
            else
                [ unknown "セキュリティ診断" "このOSでの診断は未対応です。" ]

        let detectedRuntimes = VersionDiagnostics.detectHostRuntimes serviceList listeners
        let versionFindings =
            VersionDiagnostics.diagnoseHostRuntimes
                detectedRuntimes
                (DateOnly.FromDateTime(DateTime.Now))
                snapshot

        baseFindings @ versionFindings

    /// <summary>現在のOSに応じたセキュリティ簡易診断を実行して項目一覧を返します。</summary>
    let inspect () =
        let services, listeners =
            try ServiceDiscovery.detectWithListeners ()
            with _ -> [||], []
        let snapshot = MasterDataRepository.GetSnapshot()
        inspectWithContext services (Some listeners) (Some snapshot)

    /// <summary>診断項目一覧から総合スコア（0〜100）と評価グレード（A〜D）を算出します。</summary>
    let score (findings: SecurityFinding list) =
        let evaluated = findings |> List.filter (fun item -> item.Level = FindingLevel.Good || item.Level = FindingLevel.Warning)
        if evaluated.IsEmpty then None
        else
            let totalDeduction = evaluated |> List.sumBy (fun item -> item.Deduction)
            let value = max 0 (100 - totalDeduction)
            let baseGrade = if value >= 95 then "A" elif value >= 80 then "B" elif value >= 60 then "C" else "D"
            let grade =
                if findings |> List.exists (fun item -> item.Level = FindingLevel.Unknown) then baseGrade + "（暫定）"
                else baseGrade
            Some(value, grade)
