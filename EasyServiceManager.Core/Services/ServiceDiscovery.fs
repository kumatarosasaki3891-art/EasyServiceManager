namespace EasyServiceManager.Services

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open System.Text.Json
open System.Text.RegularExpressions
open EasyServiceManager.Models

/// <summary>検出された待ち受けリスナー情報。</summary>
type ListenerInfo =
    { Port: int
      BindAddress: string
      ProcessId: int
      ProcessName: string }

/// <summary>スナップショット保存用のサービス項目。</summary>
[<AllowNullLiteral>]
type ServiceSnapshotItem() =
    member val Name = "" with get, set
    member val DisplayName = "" with get, set
    member val Status = "" with get, set
    member val Ports = "" with get, set

/// <summary>スナップショット保存用のリスナー項目。</summary>
[<AllowNullLiteral>]
type ListenerSnapshotItem() =
    member val Port = 0 with get, set
    member val BindAddress = "" with get, set
    member val ProcessName = "" with get, set

/// <summary>前回スキャン結果を保持するスナップショットモデル。</summary>
[<AllowNullLiteral>]
type ScanSnapshot() =
    member val Timestamp = "" with get, set
    member val Services: ServiceSnapshotItem array = [||] with get, set
    member val Listeners: ListenerSnapshotItem array = [||] with get, set

/// <summary>OS上のサービスと待ち受けポートを自動検出し、カテゴリ分類を行うモジュール。</summary>
module ServiceDiscovery =
    let private categoryDefinitions =
        [ "SSH",
          [ "sshd"; "ssh"; "openssh"; "ssh-agent" ],
          [ "openssh"; "ssh server"; "ssh agent" ]

          "Database",
          [ "mssql"; "sqlagent"; "mysql"; "mysqld"; "mariadb"; "postgresql"; "postgres"; "redis"; "mongod"; "mongodb"; "elasticsearch"; "couchdb"; "influxdb"; "rabbitmq"; "oracle" ],
          [ "sql server"; "sqlexpress"; "mysql"; "postgresql"; "postgres"; "mongodb"; "redis"; "oracle service"; "mariadb" ]

          "File Share",
          [ "lanmanserver"; "lanmanworkstation"; "smbd"; "samba"; "nmbd"; "nfs-server"; "nfs-kernel-server"; "rpcbind"; "winbind"; "vsftpd"; "pure-ftpd"; "proftpd"; "ftpsvc" ],
          [ "lanmanserver"; "file share"; "samba"; "nfs"; "ftp server"; "server (lanmanserver)" ]

          "Remote Access",
          [ "termservice"; "winrm"; "rdp"; "vnc"; "anydesk"; "teamviewer"; "remotedesktop"; "remoteregistry"; "xrdp" ],
          [ "remote desktop"; "remote access"; "windows remote management"; "anydesk"; "teamviewer"; "vnc server"; "vncserver" ]

          "Web Server",
          [ "nginx"; "apache"; "apache2"; "httpd"; "caddy"; "lighttpd"; "iis"; "iisadmin"; "w3svc"; "was"; "traefik"; "jenkins"; "gitea"; "nextcloud"; "portainer"; "webmin" ],
          [ "world wide web publishing"; "iis worker"; "apache http"; "nginx" ]

          "Container",
          [ "docker"; "dockerd"; "docker.service"; "podman"; "containerd"; "cri-o"; "nerdctl"; "com.docker.service"; "wslservice"; "lxssmanager" ],
          [ "docker desktop"; "docker engine"; "container runtime"; "podman service" ]

          "Virtualization",
          [ "libvirtd"; "virtqemud"; "qemu"; "kvm"; "virtualbox"; "vboxservice"; "vbox"; "vmware"; "kubelet"; "k3s"; "microk8s"; "vmms"; "vmcompute" ],
          [ "hyper-v"; "virtual machine"; "virtualbox"; "vmware" ]

          "Monitoring",
          [ "zabbix-agent"; "zabbix-agent2"; "prometheus"; "grafana"; "node-exporter"; "telegraf"; "nagios"; "icinga2"; "sysmon"; "perfmon" ],
          [ "zabbix"; "prometheus"; "grafana"; "performance counter"; "system monitor" ]

          "Mail",
          [ "postfix"; "dovecot"; "exim"; "sendmail"; "spamassassin"; "smtpsvc" ],
          [ "simple mail transfer"; "smtp service"; "dovecot"; "postfix" ]

          "AI / LLM",
          [ "open-webui"; "ollama"; "anythingllm"; "langflow"; "flowise"; "vllm"; "text-generation-webui" ],
          [ "ollama"; "localai"; "vllm" ]

          "Cloud Agent",
          [ "amazon-ssm-agent"; "ssm-agent"; "azure-agent"; "waagent"; "google-cloud-agent"; "google-guest-agent"; "tailscaled"; "cloudflared" ],
          [ "ssm agent"; "guest agent"; "tailscale"; "cloudflared" ]

          "Security",
          [ "crowdstrike"; "falcon-sensor"; "wazuh-agent"; "defender"; "clamav"; "clamd"; "mdatp"; "windefend"; "wscsvc"; "sense"; "securityhealthservice" ],
          [ "windows defender"; "microsoft defender"; "security center"; "antivirus" ] ]

    /// <summary>サービス名を比較用に小文字・拡張子なしへ正規化します。</summary>
    let private normalize (name: string) =
        name.Trim().ToLowerInvariant().Replace(".service", "")

    /// <summary>サービス名、表示名、プロセス名からカテゴリを総合判定します。</summary>
    let categoryForDetailed (name: string) (displayName: string) (processName: string) =
        let normName = normalize name
        let normDisplay = if String.IsNullOrWhiteSpace displayName then "" else displayName.Trim().ToLowerInvariant()
        let normProcess = if String.IsNullOrWhiteSpace processName then "" else processName.Trim().ToLowerInvariant()

        // 1. サービス名またはプロセス名の完全一致 / 前方一致（例: mssql$sqlexpress -> Database, lanmanserver -> File Share）
        let exactOrPrefix =
            categoryDefinitions
            |> List.tryPick (fun (cat, prefixes, _) ->
                let matchesName = prefixes |> List.exists (fun prefix -> normName = prefix || normName.StartsWith(prefix, StringComparison.Ordinal))
                let matchesProcess = not (String.IsNullOrEmpty normProcess) && prefixes |> List.exists (fun prefix -> normProcess = prefix || normProcess.StartsWith(prefix, StringComparison.Ordinal))
                if matchesName || matchesProcess then Some cat else None)

        match exactOrPrefix with
        | Some cat -> cat
        | None ->
            // 2. 表示名・サービス名・プロセス名の部分一致判定
            let substringMatch =
                categoryDefinitions
                |> List.tryPick (fun (cat, _, substrings) ->
                    let matched =
                        substrings
                        |> List.exists (fun sub ->
                            normDisplay.Contains(sub, StringComparison.Ordinal)
                            || normName.Contains(sub, StringComparison.Ordinal)
                            || normProcess.Contains(sub, StringComparison.Ordinal))
                    if matched then Some cat else None)
            substringMatch |> Option.defaultValue "Other"

    /// <summary>サービス名からカテゴリを判定します。</summary>
    let categoryFor (name: string) =
        categoryForDetailed name "" ""

    /// <summary>外部コマンドを実行して標準出力文字列を取得します。</summary>
    let private runCommand executable (arguments: string list) =
        let info = ProcessStartInfo(executable)
        info.UseShellExecute <- false
        info.RedirectStandardOutput <- true
        info.RedirectStandardError <- true
        info.CreateNoWindow <- true
        for argument in arguments do info.ArgumentList.Add(argument)
        use childProcess = new Process(StartInfo = info)
        if not (childProcess.Start()) then
            failwithf "コマンドを開始できませんでした: %s" executable
        let outputTask = childProcess.StandardOutput.ReadToEndAsync()
        let errorTask = childProcess.StandardError.ReadToEndAsync()
        if not (childProcess.WaitForExit(10000)) then
            childProcess.Kill(true)
            failwithf "コマンドがタイムアウトしました: %s" executable
        let output = outputTask.GetAwaiter().GetResult()
        let error = errorTask.GetAwaiter().GetResult()
        if childProcess.ExitCode <> 0 then
            failwithf "%s の実行に失敗しました: %s" executable (error.Trim())
        output

    /// <summary>Linux の ss コマンド出力からプロセスごとの待ち受けポートを解析します。</summary>
    let private parseListeningPorts (output: string) =
        let ports = Dictionary<string, ResizeArray<int>>(StringComparer.OrdinalIgnoreCase)
        let pattern = Regex("users:\\(\\(\"([^\"]+)\"")
        for line in output.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries) do
            let portMatch = Regex.Match(line, ":(\\d+)\\s")
            let processMatch = pattern.Match(line)
            if portMatch.Success && processMatch.Success then
                let mutable port = 0
                if Int32.TryParse(portMatch.Groups.[1].Value, &port) then
                    let processName = processMatch.Groups.[1].Value
                    if not (ports.ContainsKey(processName)) then ports.[processName] <- ResizeArray()
                    if not (ports.[processName].Contains(port)) then ports.[processName].Add(port)
        ports

    /// <summary>サービス名または実行ファイル名から待ち受けポート一覧を検索して整形します。</summary>
    let private portsFor (ports: Dictionary<string, ResizeArray<int>>) (serviceName: string) (executable: string) =
        let candidates =
            [ yield serviceName
              yield serviceName.Replace(".service", "")
              if not (String.IsNullOrWhiteSpace executable) then
                  yield System.IO.Path.GetFileNameWithoutExtension(executable) ]
        candidates
        |> Seq.tryPick (fun candidate ->
            match ports.TryGetValue(candidate) with
            | true, values -> Some values
            | _ ->
                ports
                |> Seq.tryPick (fun pair ->
                    if String.Equals(pair.Key, candidate, StringComparison.OrdinalIgnoreCase) then Some pair.Value else None))
        |> Option.map (fun values -> values |> Seq.sort |> Seq.map string |> String.concat ", ")
        |> Option.defaultValue ""

    /// <summary>引数付きパス文字列から実行ファイル名（拡張子なし）を安全に抽出します。</summary>
    let private extractProcessName (commandLine: string) =
        if String.IsNullOrWhiteSpace commandLine then ""
        else
            let trimmed = commandLine.Trim()
            let exePath =
                if trimmed.StartsWith("\"", StringComparison.Ordinal) then
                    let closing = trimmed.IndexOf('"', 1)
                    if closing > 1 then trimmed.Substring(1, closing - 1) else trimmed.Trim('"')
                else
                    let exeMatch = Regex.Match(trimmed, "^(.*?\\.exe)(?=\\s|$)", RegexOptions.IgnoreCase)
                    if exeMatch.Success then exeMatch.Groups.[1].Value
                    else trimmed.Split([| ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries) |> Array.tryHead |> Option.defaultValue ""
            try IO.Path.GetFileNameWithoutExtension(exePath)
            with _ -> ""

    /// <summary>先頭ポート番号からローカル確認用URLを構築します。</summary>
    let private webUrl (ports: string) =
        match ports.Split([| ", " |], StringSplitOptions.RemoveEmptyEntries) |> Array.tryHead with
        | Some port ->
            let scheme = if port = "443" then "https" else "http"
            sprintf "%s://localhost:%s" scheme port
        | None -> ""

    /// <summary>アドレス一覧を 0.0.0.0 / 127.0.0.1 等へ正規化します。</summary>
    let private normalizeBindAddress (addresses: string seq) =
        let list = addresses |> Seq.map (fun a -> a.Trim()) |> Seq.toList
        let hasWildcard =
            list |> List.exists (fun a -> a = "0.0.0.0" || a = "::" || a = "*" || a = "[::]" || a = "[::0]")
        if hasWildcard then "0.0.0.0"
        else
            let allLoopback =
                not list.IsEmpty
                && list |> List.forall (fun a -> a = "127.0.0.1" || a = "::1" || a = "[::1]" || a.StartsWith("127.", StringComparison.Ordinal))
            if allLoopback then "127.0.0.1"
            else
                list
                |> List.tryFind (fun a -> not (a.Contains(":")))
                |> Option.orElse (List.tryHead list)
                |> Option.defaultValue "0.0.0.0"

    /// <summary>Linux の ss コマンド出力からリスナー一覧を解析します。</summary>
    let private parseLinuxListeners (output: string) : ListenerInfo list =
        let raw = ResizeArray<string * int * string>()
        let procPattern = Regex("users:\\(\\(\"([^\"]+)\"")
        for line in output.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries) do
            let columns = Regex.Split(line.Trim(), "\\s+")
            if columns.Length >= 4 then
                let localAddress = columns.[3]
                let portMatch = Regex.Match(localAddress, ":(\\d+)$")
                if portMatch.Success then
                    let mutable port = 0
                    if Int32.TryParse(portMatch.Groups.[1].Value, &port) then
                        let host = localAddress.Substring(0, localAddress.Length - portMatch.Value.Length)
                        let procMatch = procPattern.Match(line)
                        let procName = if procMatch.Success then procMatch.Groups.[1].Value else ""
                        raw.Add((host, port, procName))
        raw
        |> Seq.groupBy (fun (_, port, _) -> port)
        |> Seq.map (fun (port, group) ->
            let addrs = group |> Seq.map (fun (addr, _, _) -> addr)
            let procName = group |> Seq.tryPick (fun (_, _, p) -> if String.IsNullOrWhiteSpace p then None else Some p) |> Option.defaultValue ""
            { Port = port; BindAddress = normalizeBindAddress addrs; ProcessId = 0; ProcessName = procName })
        |> Seq.toList

    /// <summary>Linux (systemd / ss) 環境のサービス一覧とリスナー一覧を検出します。</summary>
    let private detectLinuxWithListeners () =
        let units = runCommand "systemctl" [ "list-units"; "--type=service"; "--all"; "--no-legend"; "--no-pager" ]
        let listenersOutput = runCommand "ss" [ "-lntp" ]
        let ports = parseListeningPorts listenersOutput
        let listenerInfos = parseLinuxListeners listenersOutput
        let unitPattern = Regex("^\\s*(\\S+\\.service)\\s+\\S+\\s+(\\S+)\\s+(\\S+)\\s*(.*)$")
        let services =
            units.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.choose (fun line ->
                let matched = unitPattern.Match(line)
                if not matched.Success then None
                else
                    let unit = matched.Groups.[1].Value
                    let name = unit.Substring(0, unit.Length - ".service".Length)
                    let active = matched.Groups.[2].Value
                    let sub = matched.Groups.[3].Value
                    let description = matched.Groups.[4].Value.Trim()
                    let state = if active = "active" then "起動中" else if active = "inactive" then "停止中" else "不明"
                    let processName = name
                    let category = categoryForDetailed name description processName
                    let servicePorts = portsFor ports name processName
                    let url = if category = "Web Server" || category = "Monitoring" || category = "AI / LLM" then webUrl servicePorts else ""
                    Some(ServiceEntry(name, (if String.IsNullOrWhiteSpace description then name else description), category, state + (if sub = "failed" then " (失敗)" else ""), servicePorts, "", url, false, false, false)))
        services, listenerInfos

    /// <summary>Windows 環境のサービスおよびPIDごとの待ち受けポート・リスナー一覧を検出します。</summary>
    let private detectWindowsWithListeners () =
        let script =
            "Get-CimInstance Win32_Service | ForEach-Object { '{0}|{1}|{2}|{3}|{4}' -f $_.Name,$_.DisplayName,$_.State,$_.PathName,$_.ProcessId }"
        let serviceLines = runCommand "powershell.exe" [ "-NoProfile"; "-NonInteractive"; "-Command"; script ]
        let portsOutput =
            runCommand "powershell.exe" [ "-NoProfile"; "-NonInteractive"; "-Command"; "Get-NetTCPConnection -State Listen | ForEach-Object { '{0}|{1}|{2}' -f $_.LocalAddress,$_.LocalPort,$_.OwningProcess }" ]
        let processNames = Dictionary<int, string>()
        processNames.[4] <- "System"
        let processes = runCommand "powershell.exe" [ "-NoProfile"; "-NonInteractive"; "-Command"; "Get-Process | ForEach-Object { '{0}|{1}' -f $_.Id,$_.ProcessName }" ]
        for line in processes.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries) do
            let parts = line.Split('|', 2)
            let mutable id = 0
            if parts.Length = 2 && Int32.TryParse(parts.[0], &id) then processNames.[id] <- parts.[1]
        let portsByPid = Dictionary<int, ResizeArray<int>>()
        let ports = Dictionary<string, ResizeArray<int>>(StringComparer.OrdinalIgnoreCase)
        let rawListeners = ResizeArray<string * int * int>()
        let currentPid = Environment.ProcessId
        for line in portsOutput.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries) do
            let parts = line.Split('|')
            let mutable port = 0
            let mutable processId = 0
            if parts.Length = 3 && Int32.TryParse(parts.[1], &port) && Int32.TryParse(parts.[2], &processId) then
                if processId <> currentPid then
                    let localAddr = parts.[0].Trim()
                    rawListeners.Add((localAddr, port, processId))
                    if processId > 0 then
                        if not (portsByPid.ContainsKey(processId)) then portsByPid.[processId] <- ResizeArray()
                        if not (portsByPid.[processId].Contains(port)) then portsByPid.[processId].Add(port)
                    match processNames.TryGetValue(processId) with
                    | true, processName ->
                        if not (ports.ContainsKey(processName)) then ports.[processName] <- ResizeArray()
                        if not (ports.[processName].Contains(port)) then ports.[processName].Add(port)
                    | _ -> ()
        let listenerInfos =
            rawListeners
            |> Seq.groupBy (fun (_, port, _) -> port)
            |> Seq.map (fun (port, group) ->
                let addrs = group |> Seq.map (fun (addr, _, _) -> addr)
                let repPid = group |> Seq.map (fun (_, _, pid) -> pid) |> Seq.tryHead |> Option.defaultValue 0
                let procName =
                    match processNames.TryGetValue(repPid) with
                    | true, pName -> pName
                    | _ -> ""
                { Port = port; BindAddress = normalizeBindAddress addrs; ProcessId = repPid; ProcessName = procName })
            |> Seq.toList
        let services =
            serviceLines.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.choose (fun line ->
                let parts = line.Split([| '|' |], 5)
                if parts.Length < 4 then None
                else
                    let name = parts.[0].Trim()
                    let displayName = parts.[1].Trim()
                    let state = if parts.[2].Trim() = "Running" then "起動中" else "停止中"
                    let executable = parts.[3].Trim().Trim('"')
                    let mutable pid = 0
                    if parts.Length >= 5 then Int32.TryParse(parts.[4].Trim(), &pid) |> ignore
                    let processName = extractProcessName parts.[3]
                    let category = categoryForDetailed name displayName processName
                    let isSharedProcess = String.Equals(processName, "svchost", StringComparison.OrdinalIgnoreCase)
                    // 強化4: LanmanServer (SMB) のポート補完（System/PID 4 が 445 をリスン）
                    let isLanmanServer = String.Equals(name, "LanmanServer", StringComparison.OrdinalIgnoreCase)
                    let hasSystemSmb =
                        isLanmanServer && state = "起動中" && portsByPid.ContainsKey(4)
                        && (portsByPid.[4].Contains(445) || portsByPid.[4].Contains(139))
                    let servicePorts =
                        if pid > 0 && portsByPid.ContainsKey(pid) then
                            portsByPid.[pid] |> Seq.sort |> Seq.map string |> String.concat ", "
                        elif hasSystemSmb then
                            portsByPid.[4]
                            |> Seq.filter (fun p -> p = 445 || p = 139)
                            |> Seq.sort
                            |> Seq.map string
                            |> String.concat ", "
                        elif not isSharedProcess && state = "起動中" then
                            portsFor ports name processName
                        else
                            ""
                    let effectiveShared = isSharedProcess || hasSystemSmb
                    let url = if category = "Web Server" || category = "Monitoring" || category = "AI / LLM" then webUrl servicePorts else ""
                    Some(ServiceEntry(name, (if String.IsNullOrWhiteSpace displayName then name else displayName), category, state, servicePorts, executable, url, false, false, effectiveShared)))
        services, listenerInfos

    let mutable private lastSnapshot: ScanSnapshot option = None

    /// <summary>スナップショットファイルの保存先パスを返します。</summary>
    let snapshotPath () =
        let directory =
            if OperatingSystem.IsWindows() then
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EasyServiceManager")
            else
                let dataHome =
                    match Environment.GetEnvironmentVariable("XDG_DATA_HOME") with
                    | v when not (String.IsNullOrWhiteSpace v) -> v
                    | _ -> Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share")
                Path.Combine(dataHome, "easy-service-diagnosis", "snapshots")
        Path.Combine(directory, "snapshot.json")

    /// <summary>指定されたファイルパスからスナップショットを読み込みます。</summary>
    let loadSnapshotFile (path: string) : ScanSnapshot option =
        if not (File.Exists(path)) then None
        else
            try
                let snap = JsonSerializer.Deserialize<ScanSnapshot>(File.ReadAllText(path))
                if isNull snap then None else Some snap
            with _ -> None

    /// <summary>前回スキャンのスナップショットを取得します。</summary>
    let loadPreviousSnapshot () =
        match lastSnapshot with
        | Some snap -> Some snap
        | None -> loadSnapshotFile (snapshotPath ())

    /// <summary>現在のスキャン結果をスナップショットとして保存します。</summary>
    let buildSnapshot (services: ServiceEntry array) (listeners: ListenerInfo list) =
        let snap = ScanSnapshot()
        snap.Timestamp <- DateTime.UtcNow.ToString("o")
        snap.Services <-
            services
            |> Array.map (fun s ->
                let item = ServiceSnapshotItem()
                item.Name <- s.Name
                item.DisplayName <- s.DisplayName
                item.Status <- s.Status
                item.Ports <- s.Ports
                item)
        snap.Listeners <-
            listeners
            |> List.map (fun l ->
                let item = ListenerSnapshotItem()
                item.Port <- l.Port
                item.BindAddress <- l.BindAddress
                item.ProcessName <- l.ProcessName
                item)
            |> List.toArray
        snap

    let private saveCurrentSnapshot (services: ServiceEntry array) (listeners: ListenerInfo list) =
        let snap = buildSnapshot services listeners
        lastSnapshot <- Some snap
        try
            let path = snapshotPath ()
            Directory.CreateDirectory(Path.GetDirectoryName(path)) |> ignore
            File.WriteAllText(path, JsonSerializer.Serialize(snap, JsonSerializerOptions(WriteIndented = true)))
        with _ -> ()

    /// <summary>Windows ユーザー別サービス等のセッション接尾辞 (_146dea72 等) を正規化します。</summary>
    let private normalizeServiceName (name: string) =
        if String.IsNullOrEmpty name then ""
        else
            let m = Regex.Match(name, "^([a-zA-Z0-9]+)_[0-9a-fA-F]{4,12}$")
            if m.Success then m.Groups.[1].Value
            else name

    /// <summary>前回スナップショットと現在の検出結果を比較し、差分フラグと差分メッセージを付与します。</summary>
    let compareSnapshots (prevOpt: ScanSnapshot option) (services: ServiceEntry array) (listeners: ListenerInfo list) =
        match prevOpt with
        | None -> services, false, []
        | Some prev ->
            let diffMessages = ResizeArray<string>()
            let extraEntries = ResizeArray<ServiceEntry>()

            let prevServicesMap = Dictionary<string, ServiceSnapshotItem>(StringComparer.OrdinalIgnoreCase)
            let prevNormalizedMap = Dictionary<string, ServiceSnapshotItem>(StringComparer.OrdinalIgnoreCase)
            if not (isNull prev.Services) then
                for item in prev.Services do
                    if not (isNull item) && not (String.IsNullOrWhiteSpace item.Name) then
                        prevServicesMap.[item.Name] <- item
                        let norm = normalizeServiceName item.Name
                        if not (prevNormalizedMap.ContainsKey(norm)) then
                            prevNormalizedMap.[norm] <- item

            for s in services do
                let prevItemOpt =
                    match prevServicesMap.TryGetValue(s.Name) with
                    | true, item -> Some item
                    | _ ->
                        let norm = normalizeServiceName s.Name
                        match prevNormalizedMap.TryGetValue(norm) with
                        | true, item -> Some item
                        | _ -> None

                match prevItemOpt with
                | None ->
                    s.IsDiff <- true
                    s.DiffNote <- "前回から変化: 新規サービス"
                    diffMessages.Add(sprintf "新規サービス: %s" s.Name)
                | Some prevItem ->
                    let wasRunning = not (isNull prevItem.Status) && prevItem.Status.StartsWith("起動中", StringComparison.Ordinal)
                    let isRunning = s.Status.StartsWith("起動中", StringComparison.Ordinal)
                    if not wasRunning && isRunning then
                        s.IsDiff <- true
                        s.DiffNote <- "前回から変化: 停止中 → 起動中"
                        diffMessages.Add(sprintf "サービス起動: %s" s.Name)

            let prevListenersMap = Dictionary<int, ListenerSnapshotItem>()
            if not (isNull prev.Listeners) then
                for item in prev.Listeners do
                    if not (isNull item) && item.Port > 0 then
                        prevListenersMap.[item.Port] <- item

            let hasPort (service: ServiceEntry) (port: int) =
                if String.IsNullOrWhiteSpace service.Ports then false
                else
                    let portStr = string port
                    service.Ports.Split([| ',' |], StringSplitOptions.RemoveEmptyEntries)
                    |> Array.exists (fun p -> p.Trim() = portStr)

            for l in listeners do
                let procLabel = if String.IsNullOrWhiteSpace l.ProcessName then "unknown" else l.ProcessName
                let changeOpt =
                    match prevListenersMap.TryGetValue(l.Port) with
                    | false, _ ->
                        let msg = sprintf "新規待ち受け: %s:%d (%s)" l.BindAddress l.Port procLabel
                        let note = sprintf "前回から変化: 新規待ち受け (%s:%d)" l.BindAddress l.Port
                        Some(msg, note)
                    | true, prevItem when not (String.Equals(prevItem.BindAddress, l.BindAddress, StringComparison.OrdinalIgnoreCase)) ->
                        let msg = sprintf "バインド変化: ポート %d (%s) %s → %s" l.Port procLabel prevItem.BindAddress l.BindAddress
                        let note = sprintf "前回から変化: バインド変化 (%s → %s :%d)" prevItem.BindAddress l.BindAddress l.Port
                        Some(msg, note)
                    | _ -> None

                match changeOpt with
                | None -> ()
                | Some(msg, note) ->
                    diffMessages.Add(msg)
                    let matchedServices = services |> Array.filter (fun s -> hasPort s l.Port)
                    if matchedServices.Length > 0 then
                        for s in matchedServices do
                            s.IsDiff <- true
                            s.DiffNote <- note
                    else
                        let procName = if String.IsNullOrWhiteSpace l.ProcessName then "listener" else l.ProcessName
                        let cat = categoryForDetailed procName "" procName
                        let url = if cat = "Web Server" || cat = "Monitoring" || cat = "AI / LLM" then webUrl (string l.Port) else ""
                        let entry =
                            ServiceEntry(
                                sprintf "%s (Port %d)" procName l.Port,
                                sprintf "待ち受けリスナー (%s:%d)" l.BindAddress l.Port,
                                cat,
                                "起動中",
                                string l.Port,
                                "",
                                url,
                                false,
                                false,
                                false)
                        entry.IsDiff <- true
                        entry.DiffNote <- note
                        extraEntries.Add(entry)

            let combinedServices =
                if extraEntries.Count = 0 then services
                else Array.append (extraEntries.ToArray()) services
            combinedServices, true, Seq.toList diffMessages

    /// <summary>実行中のOSに応じてサービス一覧を検出します。</summary>
    let detect () =
        let services, _ =
            if RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then detectWindowsWithListeners()
            elif RuntimeInformation.IsOSPlatform(OSPlatform.Linux) then detectLinuxWithListeners()
            else failwith "このOSでのサービス自動検出はまだ対応していません。"
        services

    /// <summary>2つのスナップショットオブジェクトを比較し、差分情報を返します。</summary>
    let compareTwoSnapshots (prev: ScanSnapshot) (curr: ScanSnapshot) =
        let services =
            if isNull curr.Services then [||]
            else
                curr.Services
                |> Array.map (fun s ->
                    let cat = categoryFor s.Name
                    ServiceEntry(s.Name, s.DisplayName, cat, s.Status, s.Ports, "", "", false, false, false))
        let listeners =
            if isNull curr.Listeners then []
            else
                curr.Listeners
                |> Array.map (fun l ->
                    { Port = l.Port; BindAddress = l.BindAddress; ProcessId = 0; ProcessName = l.ProcessName })
                |> Array.toList
        compareSnapshots (Some prev) services listeners

    /// <summary>サービス一覧を検出し、前回スキャンとの差分を比較してスナップショットを更新します。</summary>
    let detectAndCompare () =
        let services, listeners =
            if RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then detectWindowsWithListeners()
            elif RuntimeInformation.IsOSPlatform(OSPlatform.Linux) then detectLinuxWithListeners()
            else failwith "このOSでのサービス自動検出はまだ対応していません。"
        let prevOpt = loadPreviousSnapshot ()
        saveCurrentSnapshot services listeners
        compareSnapshots prevOpt services listeners
