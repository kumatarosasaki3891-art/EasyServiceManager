namespace EasyServiceManager.Services

open System
open System.Diagnostics
open System.IO
open System.Text.RegularExpressions
open System.ComponentModel
open System.Runtime.InteropServices
open EasyServiceManager.Models

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

    /// <summary>Linux の UFW / firewalld の有効状態を確認します。</summary>
    let private inspectLinuxFirewall () =
        let ufwResult = runCommand "ufw" [ "status" ]
        match ufwResult with
        | Some result when result.Output.Contains("Status: active", StringComparison.OrdinalIgnoreCase) ->
            good "Firewall (UFW)" "UFWは有効です。"
        | _ ->
            match runCommand "firewall-cmd" [ "--state" ] with
            | Some result when result.ExitCode = 0 && result.Output.Trim() = "running" ->
                good "Firewall (firewalld)" "firewalldは稼働中です。"
            | _ ->
                match runCommand "systemctl" [ "is-active"; "firewalld" ] with
                | Some result when result.Output.Trim() = "active" -> good "Firewall (firewalld)" "firewalldは稼働中です。"
                | Some result when result.Output.Trim() = "inactive" ->
                    warning "Firewall" "UFW/firewalldはいずれも有効ではありません。" 25
                | _ ->
                    match ufwResult with
                    | Some result when result.Output.Contains("Status: inactive", StringComparison.OrdinalIgnoreCase) ->
                        warning "Firewall" "UFWは無効です。firewalldの状態も確認できませんでした。" 25
                    | _ -> unknown "Firewall" "UFW/firewalldを確認できませんでした。未導入または権限不足の可能性があります。"

    /// <summary>ss の出力から待ち受けアドレスと全インターフェース待ち受け判定を抽出します。</summary>
    let private parseListeningAddresses (output: string) =
        output.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
        |> Seq.choose (fun line ->
            let columns = Regex.Split(line.Trim(), "\\s+")
            if columns.Length < 4 then None
            else
                let localAddress = columns.[3]
                let portMatch = Regex.Match(localAddress, ":(\\d+)$")
                if not portMatch.Success then None
                else
                    let mutable port = 0
                    if not (Int32.TryParse(portMatch.Groups.[1].Value, &port)) then None
                    else
                        let host = localAddress.Substring(0, localAddress.Length - portMatch.Value.Length)
                        let isWildcard =
                            host = "*"
                            || host = "0.0.0.0"
                            || host = "::"
                            || host = "[::]"
                            || host = "[::0]"
                        Some(port, host, isWildcard))
        |> Seq.toList

    /// <summary>Linux の要注意公開ポートを検査します。</summary>
    let private inspectLinuxListeners (result: CommandResult option) =
        match result with
        | None -> unknown "公開ポート" "ssを実行できないため、待受ポートを確認できません。"
        | Some result when result.ExitCode <> 0 ->
            unknown "公開ポート" "待受ポート一覧を取得できませんでした。"
        | Some result ->
            let listeners = parseListeningAddresses result.Output
            let riskyPorts = set [ 445; 3306; 5432; 6379; 9200; 5601; 27017; 5000 ]
            let exposed =
                listeners
                |> List.filter (fun (port, _, wildcard) -> wildcard && Set.contains port riskyPorts)
                |> List.map (fun (port, _, _) -> port)
                |> List.distinct
                |> List.sort
            if exposed.IsEmpty then
                good "要注意ポート" "要注意ポートの全インターフェース待受は検出されませんでした。"
            else
                let ports = exposed |> List.map string |> String.concat ", "
                warning "要注意ポート" (sprintf "ポート %s が全インターフェースで待ち受けています。外部から到達可能かはFirewallやNATにも依存します。" ports) 25

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
    let private inspectHttps (listeners: (int * string * bool) list) =
        let hasHttps = listeners |> List.exists (fun (port, _, _) -> port = 443)
        let hasHttp = listeners |> List.exists (fun (port, _, _) -> port = 80)
        if hasHttps then good "HTTPSポート" "TCP/443の待受を検出しました。TLS設定や証明書の有効性は確認していません。"
        elif hasHttp then warning "HTTPSポート" "TCP/80の待受があり、TCP/443は検出されませんでした。" 10
        else unknown "HTTPSポート" "TCP/80/443の待受を検出できませんでした。"

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

    /// <summary>Linux 環境向けの簡易セキュリティ診断を実行します。</summary>
    let private inspectLinux () =
        let firewall = inspectLinuxFirewall ()
        let listenerResult = runCommand "ss" [ "-lntH" ]
        let listeners =
            match listenerResult with
            | Some result when result.ExitCode = 0 -> parseListeningAddresses result.Output
            | _ -> []
        [ yield firewall
          yield inspectLinuxListeners listenerResult
          yield! inspectSsh ()
          yield inspectHttps listeners
          yield! inspectContainers () ]

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

    /// <summary>Windows 環境向けの簡易セキュリティ診断を実行します。</summary>
    let private inspectWindows () =
        let firewallScript =
            "Get-NetFirewallProfile | ForEach-Object { '{0}|{1}' -f $_.Name,$_.Enabled }"
        let firewall =
            match runCommand "powershell.exe" [ "-NoProfile"; "-NonInteractive"; "-Command"; firewallScript ] with
            | Some result when result.ExitCode = 0 ->
                let profiles = result.Output.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
                if profiles.Length < 3 then unknown "Windows Firewall" "Firewallプロファイルをすべて確認できませんでした。"
                elif profiles |> Array.exists (fun line -> line.EndsWith("|False", StringComparison.OrdinalIgnoreCase)) then
                    warning "Windows Firewall" "無効なファイアウォールプロファイルがあります。" 25
                else good "Windows Firewall" "各ネットワークプロファイルでFirewallが有効です。"
            | _ -> unknown "Windows Firewall" "Firewall状態を確認できませんでした。"
        let portScript =
            "Get-NetTCPConnection -State Listen | ForEach-Object { '{0}|{1}|{2}' -f $_.LocalAddress,$_.LocalPort,$_.OwningProcess }"
        let portFindings =
            match runCommand "powershell.exe" [ "-NoProfile"; "-NonInteractive"; "-Command"; portScript ] with
            | Some result when result.ExitCode = 0 ->
                let riskyPorts = set [ 3306; 5432; 6379; 9200; 5601; 27017; 5000 ]
                let entries =
                    result.Output.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
                    |> Array.choose (fun line ->
                        let parts = line.Split('|')
                        let mutable port = 0
                        let mutable pid = 0
                        if parts.Length >= 2 && Int32.TryParse(parts.[1], &port) then
                            if parts.Length >= 3 then Int32.TryParse(parts.[2], &pid) |> ignore
                            Some(parts.[0], port, pid)
                        else None)
                let exposed =
                    entries
                    |> Array.choose (fun (address, port, _) ->
                        let wildcard = address = "0.0.0.0" || address = "::"
                        if wildcard && Set.contains port riskyPorts then Some port else None)
                    |> Array.distinct
                    |> Array.sort
                let publicPortFinding =
                    if exposed.Length = 0 then good "要注意ポート" "要注意ポートの全インターフェース待受は検出されませんでした。"
                    else warning "要注意ポート" (sprintf "ポート %s が全インターフェースで待ち受けています。外部から到達可能かはFirewallやNATにも依存します。" (exposed |> Array.map string |> String.concat ", ")) 25

                // 強化4: System (PID 4) 等が掴む未帰属リスナー（SMB 445等）の検査
                let hasWildcardSmb =
                    entries
                    |> Array.exists (fun (address, port, pid) ->
                        (address = "0.0.0.0" || address = "::") && port = 445 && pid = 4)
                let unassignedFinding =
                    if hasWildcardSmb then
                        warning "帰属できない待ち受け" "SMB (ポート 445) が全インターフェースで待ち受けています（システム共有プロセス PID 4）。外部到達性はFirewallやNATにも依存します。" 15
                    else
                        good "帰属できない待ち受け" "要注意なシステム未帰属リスナーは検出されませんでした。"

                let hasHttps = entries |> Array.exists (fun (_, port, _) -> port = 443)
                let hasHttp = entries |> Array.exists (fun (_, port, _) -> port = 80)
                let httpsFinding =
                    if hasHttps then good "HTTPSポート" "TCP/443の待受を検出しました。TLS設定や証明書の有効性は確認していません。"
                    elif hasHttp then warning "HTTPSポート" "TCP/80の待受があり、TCP/443は検出されませんでした。" 10
                    else unknown "HTTPSポート" "TCP/80/443の待受を検出できませんでした。"
                [ publicPortFinding; unassignedFinding; httpsFinding ]
            | _ -> [ unknown "要注意ポート" "Windowsの待受ポートを確認できませんでした。"
                     unknown "HTTPSポート" "WindowsのHTTPS待受ポートを確認できませんでした。" ]
        [ firewall
          yield! portFindings
          inspectWindowsSsh ()
          inspectWindowsContainers () ]

    /// <summary>現在のOSに応じたセキュリティ簡易診断を実行して項目一覧を返します。</summary>
    let inspect () =
        if RuntimeInformation.IsOSPlatform(OSPlatform.Linux) then inspectLinux ()
        elif RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then inspectWindows ()
        else [ unknown "セキュリティ診断" "このOSでの診断は未対応です。" ]

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
