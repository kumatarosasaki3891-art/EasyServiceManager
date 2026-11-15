namespace EasyServiceManager.Services

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Net
open System.Net.NetworkInformation
open System.Net.Sockets
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks

/// <summary>検出されたローカル IPv4 アダプタ候補情報。</summary>
type LocalIpCandidate =
    { IpAddress: string
      AdapterName: string
      Score: int }

/// <summary>
/// スマホで QR コードを読み取り、ローカル Wi-Fi 内で 1対1 セッションによりレポートをダウンロードさせる軽量 HTTP サーバー。
/// 有効期限 3 分・1回ダウンロード完了で自動無効化されます。
/// </summary>
type QrReportTransferService(reportBytes: byte array, fileName: string, contentType: string, languageCode: string) =
    let syncRoot = obj ()
    let qrToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant()
    let sessionToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant()

    let mutable listener: TcpListener option = None
    let mutable cts: CancellationTokenSource option = None
    let mutable port = 0
    let mutable ipCandidates: LocalIpCandidate list = []
    let mutable claimedIp: string option = None
    let mutable isInvalidated = false
    let mutable isDisposed = false

    let clientConnectedEvent = Event<unit>()
    let downloadCompletedEvent = Event<unit>()
    let expiredEvent = Event<unit>()

    /// <summary>最初のスマホが QR URL を開いて 1:1 セッションが確立されたときに発火します。</summary>
    [<CLIEvent>]
    member _.ClientConnected = clientConnectedEvent.Publish

    /// <summary>レポートのダウンロードが完了して QR コードを無効化したときに発火します。</summary>
    [<CLIEvent>]
    member _.DownloadCompleted = downloadCompletedEvent.Publish

    /// <summary>3 分の制限時間を超過して QR コードを無効化したときに発火します。</summary>
    [<CLIEvent>]
    member _.Expired = expiredEvent.Publish

    /// <summary>バインドされたローカル TCP ポート番号。</summary>
    member _.Port = port

    /// <summary>優先度順に並べられたローカル IPv4 候補一覧。</summary>
    member _.IpCandidates = ipCandidates

    /// <summary>QR コードがすでに使用済み・期限切れ・停止で無効化されているかどうか。</summary>
    member _.IsInvalidated = lock syncRoot (fun () -> isInvalidated)

    /// <summary>指定された IP アドレスに対するワンタイム QR 転送 URL を生成します。</summary>
    member _.GetTransferUrl(ipAddress: string) =
        sprintf "http://%s:%d/?t=%s" ipAddress port qrToken

    /// <summary>仮想アダプタを後順位にし、アクティブな物理 Wi-Fi / 有線 LAN のプライベート IPv4 を最優先にして抽出します。</summary>
    static member DetectLocalIpCandidates() : LocalIpCandidate list =
        let list = ResizeArray<LocalIpCandidate>()
        let virtualKeywords =
            [| "WSL"; "Hyper-V"; "vEthernet"; "Virtual"; "VMware"; "VirtualBox"
               "Docker"; "Loopback"; "VPN"; "Tailscale"; "ZeroTier"; "Hamachi"
               "TAP"; "TUN"; "Bluetooth"; "NPcap"; "Container"; "Host-Only"
               "WslHostPkt"; "WireGuard"; "Default Switch"; "vethernet" |]
        try
            for ni in NetworkInterface.GetAllNetworkInterfaces() do
                if ni.OperationalStatus = OperationalStatus.Up
                   && ni.NetworkInterfaceType <> NetworkInterfaceType.Loopback
                   && ni.NetworkInterfaceType <> NetworkInterfaceType.Tunnel then
                    let nameAndDesc = sprintf "%s %s" ni.Name ni.Description
                    let isVirtual =
                        virtualKeywords
                        |> Array.exists (fun k -> nameAndDesc.Contains(k, StringComparison.OrdinalIgnoreCase))
                    let ipProps = ni.GetIPProperties()
                    let hasGateway =
                        ipProps.GatewayAddresses
                        |> Seq.exists (fun g ->
                            g.Address.AddressFamily = AddressFamily.InterNetwork
                            && not (IPAddress.IsLoopback(g.Address))
                            && not (g.Address.Equals(IPAddress.Any)))
                    for ua in ipProps.UnicastAddresses do
                        if ua.Address.AddressFamily = AddressFamily.InterNetwork
                           && not (IPAddress.IsLoopback(ua.Address)) then
                            let ipStr = ua.Address.ToString()
                            if not (ipStr.StartsWith("169.254.", StringComparison.Ordinal)) then
                                let mutable score = 0
                                if isVirtual then
                                    score <- -1000
                                    if hasGateway then score <- score + 50
                                    if ipStr.StartsWith("192.168.", StringComparison.Ordinal) then score <- score + 20
                                else
                                    // 物理アダプターを最優先グループとする
                                    score <- 1000
                                    if hasGateway then score <- score + 500
                                    if ni.NetworkInterfaceType = NetworkInterfaceType.Wireless80211 then score <- score + 300
                                    elif ni.NetworkInterfaceType = NetworkInterfaceType.Ethernet then score <- score + 200
                                    if ipStr.StartsWith("192.168.", StringComparison.Ordinal) then score <- score + 150
                                    elif ipStr.StartsWith("10.", StringComparison.Ordinal) then score <- score + 100
                                    elif ipStr.StartsWith("172.", StringComparison.Ordinal) then score <- score + 50
                                list.Add({ IpAddress = ipStr; AdapterName = ni.Name; Score = score })
        with ex ->
            Debug.WriteLine(sprintf "[QrReportTransfer] DetectLocalIpCandidates error: %s" ex.Message)

        list
        |> Seq.sortByDescending (fun c -> c.Score)
        |> Seq.distinctBy (fun c -> c.IpAddress)
        |> Seq.toList

    /// <summary>HTTP レスポンスをストリームへ書き込みます。</summary>
    static member private WriteHttpResponseAsync
        (stream: NetworkStream, status: string, respContentType: string, body: byte array, extraHeaders: string list, token: CancellationToken) =
        task {
            let sb = StringBuilder()
            sb.Append(sprintf "HTTP/1.1 %s\r\n" status) |> ignore
            sb.Append(sprintf "Content-Type: %s\r\n" respContentType) |> ignore
            sb.Append(sprintf "Content-Length: %d\r\n" body.Length) |> ignore
            sb.Append("Cache-Control: no-store, no-cache\r\n") |> ignore
            for h in extraHeaders do
                sb.Append(h + "\r\n") |> ignore
            sb.Append("Connection: close\r\n\r\n") |> ignore
            let headerBytes = Encoding.ASCII.GetBytes(sb.ToString())
            do! stream.WriteAsync(headerBytes.AsMemory(), token)
            do! stream.WriteAsync(body.AsMemory(), token)
            do! stream.FlushAsync(token)
        }

    /// <summary>クエリ文字列をキーと値のマップへ解析します。</summary>
    static member private ParseQuery(path: string) =
        let dict = Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        let qIdx = path.IndexOf('?')
        if qIdx >= 0 && qIdx < path.Length - 1 then
            let query = path.Substring(qIdx + 1)
            for pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries) do
                let kv = pair.Split('=', 2)
                if kv.Length = 2 then
                    dict.[Uri.UnescapeDataString(kv.[0])] <- Uri.UnescapeDataString(kv.[1])
        dict

    /// <summary>Cookie ヘッダーから指定名の値を抽出します。</summary>
    static member private TryGetCookie(headers: string array, cookieName: string) =
        headers
        |> Array.tryPick (fun line ->
            if line.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase) then
                let raw = line.Substring("Cookie:".Length)
                raw.Split(';', StringSplitOptions.RemoveEmptyEntries)
                |> Array.tryPick (fun part ->
                    let kv = part.Trim().Split('=', 2)
                    if kv.Length = 2 && kv.[0].Trim().Equals(cookieName, StringComparison.OrdinalIgnoreCase) then
                        Some(kv.[1].Trim())
                    else None)
            else None)

    /// <summary>スマホのブラウザに表示する 1:1 ダウンロードページ HTML を生成します。</summary>
    member private _.BuildLandingHtml() =
        let appTitle = WebUtility.HtmlEncode(Localization.translate languageCode "簡単サービス診断")
        let sizeKb = max 1 (int (Math.Ceiling(float reportBytes.Length / 1024.0)))
        let escapedFile = WebUtility.HtmlEncode(fileName)
        let downloadHref = sprintf "/download?s=%s" (Uri.EscapeDataString(sessionToken))
        let isJa = languageCode.StartsWith("ja", StringComparison.OrdinalIgnoreCase)
        let badgeConn = if isJa then "🟢 PCと1対1で接続中（ローカルWi-Fi）" else "🟢 1-on-1 Direct Local Wi-Fi Session"
        let heading = if isJa then "📄 診断レポートを受信" else "📄 Receive Diagnostic Report"
        let desc =
            if isJa then "下のボタンを押すとレポートをダウンロードします。<br/>ダウンロード完了後、このQRコードは自動的に無効化されます。"
            else "Tap below to download the report.<br/>This QR code will be invalidated immediately after download."
        let btnLabel = if isJa then "📥 レポートをダウンロード" else "📥 Download Report"
        let doneMsg =
            if isJa then "✅ ダウンロードを開始しました！このセッションは無効化されました。画面を閉じて構いません。"
            else "✅ Download started! This session has been invalidated. You may close this page."
        let warningNotice = WebUtility.HtmlEncode(Localization.translate languageCode "【ブラウザによっては「安全にダウンロードできません」と表示されますが、ご自身のPCから直接受け取るファイルです。そのまま「保存」を押してください】")

        sprintf """<!DOCTYPE html>
<html lang="%s">
<head>
<meta charset="utf-8" />
<meta name="viewport" content="width=device-width, initial-scale=1.0, maximum-scale=1.0, user-scalable=no" />
<title>%s</title>
<style>
  * { box-sizing: border-box; margin: 0; padding: 0; }
  body { font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, "Hiragino Sans", "Noto Sans JP", sans-serif; background: #F4F6F8; color: #18212B; min-height: 100vh; display: flex; align-items: center; justify-content: center; padding: 20px; }
  .card { background: #FFFFFF; border: 1px solid #DDE4EA; border-radius: 18px; padding: 26px 22px; width: 100%%; max-width: 420px; text-align: center; box-shadow: 0 8px 24px rgba(23, 35, 49, 0.08); }
  .status { display: inline-block; background: #E8F4EE; border: 1px solid #A7DEC4; color: #1D6B4B; font-size: 12px; font-weight: bold; padding: 5px 14px; border-radius: 999px; margin-bottom: 12px; }
  h1 { font-size: 20px; color: #172331; margin-bottom: 8px; }
  .desc { font-size: 13px; color: #5B6B7A; line-height: 1.5; margin-bottom: 16px; }
  .file-box { background: #F5F7F9; border: 1px solid #DDE4EA; border-radius: 10px; padding: 10px 12px; font-size: 12px; color: #314454; margin-bottom: 18px; word-break: break-all; }
  .btn { display: block; width: 100%%; background: #217A66; color: #FFFFFF; font-size: 16px; font-weight: bold; padding: 16px; border-radius: 12px; text-decoration: none; box-shadow: 0 4px 12px rgba(33, 122, 102, 0.28); }
  .btn:active { background: #185E4E; }
  #doneBox { display: none; margin-top: 16px; background: #E8F5E9; border: 1px solid #A5D6A7; color: #1B5E20; border-radius: 10px; padding: 12px; font-size: 13px; font-weight: bold; line-height: 1.45; }
  .warning-note { margin-top: 16px; background: #FFFBEB; border: 1px solid #FDE68A; border-radius: 10px; padding: 12px 14px; font-size: 12px; color: #92400E; line-height: 1.5; text-align: left; word-break: break-word; }
</style>
</head>
<body>
  <div class="card">
    <div class="status">%s</div>
    <h1>%s</h1>
    <p class="desc">%s</p>
    <div class="file-box"><strong>%s</strong> (%d KB)</div>
    <a id="dlBtn" class="btn" href="%s" onclick="onDownloadClick()">%s</a>
    <div id="doneBox">%s</div>
    <div class="warning-note">%s</div>
  </div>
<script>
function onDownloadClick() {
  setTimeout(function() {
    var btn = document.getElementById('dlBtn');
    if (btn) btn.style.display = 'none';
    var box = document.getElementById('doneBox');
    if (box) box.style.display = 'block';
  }, 400);
}
</script>
</body>
</html>"""
            (WebUtility.HtmlEncode(languageCode))
            appTitle
            badgeConn
            heading
            desc
            escapedFile
            sizeKb
            downloadHref
            btnLabel
            doneMsg
            warningNotice

    /// <summary>エラーまたはセッション無効化を伝える簡易 HTML を生成します。</summary>
    static member private BuildErrorHtml(title: string, message: string) =
        sprintf """<!DOCTYPE html><html><head><meta charset="utf-8" /><meta name="viewport" content="width=device-width,initial-scale=1" /><title>%s</title><style>body{font-family:sans-serif;background:#F4F6F8;color:#18212B;display:flex;align-items:center;justify-content:center;min-height:100vh;padding:20px;margin:0}.box{background:#fff;border:1px solid #DDE4EA;border-radius:14px;padding:24px;max-width:400px;text-align:center}h2{margin:0 0 10px;font-size:18px;color:#B42318}p{margin:0;font-size:13px;color:#516273;line-height:1.5}</style></head><body><div class="box"><h2>%s</h2><p>%s</p></div></body></html>"""
            (WebUtility.HtmlEncode(title))
            (WebUtility.HtmlEncode(title))
            (WebUtility.HtmlEncode(message))

    /// <summary>1つの TCP クライアント接続の HTTP リクエストを処理し、1:1 セッション検証とダウンロード配信を行います。</summary>
    member this.HandleClientAsync(client: TcpClient, token: CancellationToken) : Task =
        task {
            use _ = client
            client.ReceiveTimeout <- 15000
            client.SendTimeout <- 30000
            try
                let remoteIp =
                    match client.Client.RemoteEndPoint with
                    | :? IPEndPoint as ep -> ep.Address.ToString()
                    | _ -> "unknown"

                use stream = client.GetStream()
                let headerBuffer = ResizeArray<byte>(2048)
                let singleByte = Array.zeroCreate<byte> 1
                let mutable matchState = 0
                let mutable eof = false

                while headerBuffer.Count < 32768 && matchState < 4 && not eof do
                    let! read = stream.ReadAsync(singleByte.AsMemory(0, 1), token)
                    if read = 0 then
                        eof <- true
                    else
                        let b = singleByte.[0]
                        headerBuffer.Add(b)
                        if (matchState = 0 || matchState = 2) && b = byte '\r' then matchState <- matchState + 1
                        elif (matchState = 1 || matchState = 3) && b = byte '\n' then matchState <- matchState + 1
                        elif b = byte '\r' then matchState <- 1
                        else matchState <- 0

                if not eof && headerBuffer.Count > 0 then
                    let headerText = Encoding.ASCII.GetString(headerBuffer.ToArray())
                    let lines = headerText.Split([| "\r\n" |], StringSplitOptions.RemoveEmptyEntries)
                    if lines.Length > 0 then
                        let parts = lines.[0].Split(' ')
                        if parts.Length >= 2 then
                            let httpMethod = parts.[0].ToUpperInvariant()
                            let rawPath = parts.[1]
                            let pathOnly =
                                let idx = rawPath.IndexOf('?')
                                if idx >= 0 then rawPath.Substring(0, idx) else rawPath
                            let query = QrReportTransferService.ParseQuery(rawPath)
                            let cookieSid = QrReportTransferService.TryGetCookie(lines, "esm_session")

                            if httpMethod = "GET" && pathOnly = "/favicon.ico" then
                                do! QrReportTransferService.WriteHttpResponseAsync(stream, "404 Not Found", "text/plain", Encoding.ASCII.GetBytes("Not Found"), [], token)
                            elif httpMethod = "GET" && pathOnly = "/" then
                                let providedToken =
                                    match query.TryGetValue("t") with
                                    | true, v -> v
                                    | _ -> ""
                                let decision =
                                    lock syncRoot (fun () ->
                                        if isInvalidated then Choice1Of3()
                                        elif not (String.Equals(providedToken, qrToken, StringComparison.Ordinal)) then Choice2Of3("Invalid QR token")
                                        else
                                            match claimedIp with
                                            | None ->
                                                claimedIp <- Some remoteIp
                                                Choice3Of3(true)
                                            | Some _ ->
                                                match cookieSid with
                                                | Some sid when String.Equals(sid, sessionToken, StringComparison.Ordinal) -> Choice3Of3(false)
                                                | _ -> Choice2Of3("Locked to another device"))
                                match decision with
                                | Choice1Of3() ->
                                    let body = Encoding.UTF8.GetBytes(QrReportTransferService.BuildErrorHtml("QR Invalidated", "このQRコードは期限切れまたは使用済みのため無効になりました。"))
                                    do! QrReportTransferService.WriteHttpResponseAsync(stream, "410 Gone", "text/html; charset=utf-8", body, [], token)
                                | Choice2Of3(reason) ->
                                    let msg =
                                        if reason = "Locked to another device" then "このQRコードは別の端末と1対1で接続中のためアクセスできません。"
                                        else "無効なアクセスです。PC画面のQRコードを再度スキャンしてください。"
                                    let body = Encoding.UTF8.GetBytes(QrReportTransferService.BuildErrorHtml("Access Denied", msg))
                                    do! QrReportTransferService.WriteHttpResponseAsync(stream, "403 Forbidden", "text/html; charset=utf-8", body, [], token)
                                | Choice3Of3(firstClaim) ->
                                    if firstClaim then
                                        clientConnectedEvent.Trigger()
                                    let html = Encoding.UTF8.GetBytes(this.BuildLandingHtml())
                                    let cookieHeader = sprintf "Set-Cookie: esm_session=%s; Path=/; HttpOnly; SameSite=Strict" sessionToken
                                    do! QrReportTransferService.WriteHttpResponseAsync(stream, "200 OK", "text/html; charset=utf-8", html, [ cookieHeader ], token)

                            elif httpMethod = "GET" && pathOnly = "/download" then
                                let sidParam =
                                    match query.TryGetValue("s") with
                                    | true, v -> v
                                    | _ -> ""
                                let allowed =
                                    lock syncRoot (fun () ->
                                        if isInvalidated then Error "Gone"
                                        elif not (String.Equals(sidParam, sessionToken, StringComparison.Ordinal)) then Error "Forbidden"
                                        else
                                            let sessionOk =
                                                match cookieSid with
                                                | Some c -> String.Equals(c, sessionToken, StringComparison.Ordinal)
                                                | None -> claimedIp = Some remoteIp
                                            if not sessionOk then Error "Forbidden"
                                            else
                                                isInvalidated <- true
                                                Ok())
                                match allowed with
                                | Error "Gone" ->
                                    let body = Encoding.UTF8.GetBytes(QrReportTransferService.BuildErrorHtml("QR Invalidated", "このQRコードはすでに使用済みまたは期限切れです。"))
                                    do! QrReportTransferService.WriteHttpResponseAsync(stream, "410 Gone", "text/html; charset=utf-8", body, [], token)
                                | Error _ ->
                                    let body = Encoding.UTF8.GetBytes(QrReportTransferService.BuildErrorHtml("Access Denied", "1対1セッションの認証に失敗しました。"))
                                    do! QrReportTransferService.WriteHttpResponseAsync(stream, "403 Forbidden", "text/html; charset=utf-8", body, [], token)
                                | Ok() ->
                                    let disp = sprintf "Content-Disposition: attachment; filename=\"%s\"" fileName
                                    do! QrReportTransferService.WriteHttpResponseAsync(stream, "200 OK", contentType, reportBytes, [ disp ], token)
                                    try client.Client.Shutdown(SocketShutdown.Send) with _ -> ()
                                    do! Task.Delay(300, CancellationToken.None)
                                    this.Stop()
                                    downloadCompletedEvent.Trigger()
                            else
                                do! QrReportTransferService.WriteHttpResponseAsync(stream, "404 Not Found", "text/plain", Encoding.ASCII.GetBytes("Not Found"), [], token)
                try client.Client.Shutdown(SocketShutdown.Send) with _ -> ()
            with
            | :? OperationCanceledException -> ()
            | ex -> Debug.WriteLine(sprintf "[QrReportTransfer] Client handler error: %s" ex.Message)
        }

    /// <summary>ブラウザで ERR_UNSAFE_PORT として拒否される危険ポート（SIP 5060/5061 等）かどうか判定します。</summary>
    static member private IsBrowserRestrictedPort(p: int) =
        p = 5060 || p = 5061 || p = 6000 || p = 6566 || (p >= 6665 && p <= 6669) || p = 10080

    /// <summary>ローカル HTTP サーバーと 3 分間の有効期限タイマーを起動します。</summary>
    member this.Start(?preferredPort: int) =
        let startPort = defaultArg preferredPort 5070
        let candidates = QrReportTransferService.DetectLocalIpCandidates()
        if candidates.IsEmpty then
            failwith (Localization.translate languageCode "利用可能なローカルWi-Fi/LANのIPv4アドレスが見つかりませんでした。")
        ipCandidates <- candidates

        let mutable boundListener: TcpListener option = None
        let mutable boundPort = 0
        for p in startPort .. startPort + 15 do
            if boundListener.IsNone && not (QrReportTransferService.IsBrowserRestrictedPort p) then
                try
                    let candidate = new TcpListener(IPAddress.Any, p)
                    candidate.Start()
                    boundListener <- Some candidate
                    boundPort <- p
                with _ -> ()

        if boundListener.IsNone then
            for _ in 1 .. 5 do
                if boundListener.IsNone then
                    let fallback = new TcpListener(IPAddress.Any, 0)
                    fallback.Start()
                    let p = (fallback.LocalEndpoint :?> IPEndPoint).Port
                    if QrReportTransferService.IsBrowserRestrictedPort p then
                        fallback.Stop()
                    else
                        boundListener <- Some fallback
                        boundPort <- p

        listener <- boundListener
        port <- boundPort
        let newCts = new CancellationTokenSource()
        cts <- Some newCts

        // 接続待ち受けループ
        Task.Run(fun () ->
            task {
                let token = newCts.Token
                let mutable running = true
                while running && not token.IsCancellationRequested do
                    match listener with
                    | None -> running <- false
                    | Some l ->
                        try
                            let! client = l.AcceptTcpClientAsync(token).AsTask()
                            Task.Run(fun () -> this.HandleClientAsync(client, token)) |> ignore
                        with
                        | :? OperationCanceledException
                        | :? ObjectDisposedException -> running <- false
                        | ex -> Debug.WriteLine(sprintf "[QrReportTransfer] Accept error: %s" ex.Message)
            } :> Task) |> ignore

        // 3分の有効期限監視タスク
        Task.Run(fun () ->
            task {
                try
                    do! Task.Delay(TimeSpan.FromMinutes(3.0), newCts.Token)
                    let shouldExpire =
                        lock syncRoot (fun () ->
                            if not isInvalidated then
                                isInvalidated <- true
                                true
                            else false)
                    if shouldExpire then
                        this.Stop()
                        expiredEvent.Trigger()
                with :? OperationCanceledException -> ()
            } :> Task) |> ignore

    /// <summary>サーバーを停止してセッションを無効化します。</summary>
    member _.Stop() =
        lock syncRoot (fun () -> isInvalidated <- true)
        match cts with
        | Some c ->
            try c.Cancel() with _ -> ()
        | None -> ()
        match listener with
        | Some l ->
            try l.Stop() with _ -> ()
        | None -> ()

    interface IDisposable with
        member this.Dispose() =
            if not isDisposed then
                isDisposed <- true
                this.Stop()
                match cts with
                | Some c -> try c.Dispose() with _ -> ()
                | None -> ()
