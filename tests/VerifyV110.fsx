#r "../EasyServiceManager.Cli/bin/Release/net10.0/EasyServiceManager.Core.dll"
#r "../EasyServiceManager.Cli/bin/Release/net10.0/esd.dll"
#r "../EasyServiceManager.Cli/bin/Release/net10.0/Microsoft.Data.Sqlite.dll"
#r "../EasyServiceManager.Cli/bin/Release/net10.0/SQLitePCLRaw.core.dll"
#r "../EasyServiceManager.Cli/bin/Release/net10.0/SQLitePCLRaw.batteries_v2.dll"
#r "../EasyServiceManager.Cli/bin/Release/net10.0/SQLitePCLRaw.provider.e_sqlite3.dll"
#r "../EasyServiceManager.Cli/bin/Release/net10.0/CommunityToolkit.Mvvm.dll"

open System
open System.Collections.Concurrent
open System.IO
open System.Net
open System.Net.Security
open System.Net.Sockets
open System.Runtime.InteropServices
open System.Security.Cryptography
open System.Security.Cryptography.X509Certificates
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open EasyServiceManager.Models
open EasyServiceManager.Services
open EasyServiceManager.Cli

let private ensureFsiSqliteNative () =
    let rid =
        if RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then
            if RuntimeInformation.ProcessArchitecture = Architecture.Arm64 then "win-arm64", "e_sqlite3.dll"
            else "win-x64", "e_sqlite3.dll"
        else
            "linux-x64", "libe_sqlite3.so"
    let nativePath = Path.Combine(__SOURCE_DIRECTORY__, "..", "EasyServiceManager.Cli", "bin", "Release", "net10.0", "runtimes", fst rid, "native", snd rid)
    if File.Exists(nativePath) then
        try NativeLibrary.Load(nativePath) |> ignore with _ -> ()
    SQLitePCL.Batteries_V2.Init()

do ensureFsiSqliteNative ()

type DuplexPipeStream(incoming: BlockingCollection<byte array>, outgoing: BlockingCollection<byte array>) =
    inherit Stream()
    let mutable currentBuf: byte array = [||]
    let mutable currentPos = 0

    override _.CanRead = true
    override _.CanSeek = false
    override _.CanWrite = true
    override _.Length = 0L
    override _.Position with get() = 0L and set _ = ()
    override _.Flush() = ()
    override _.Seek(_, _) = raise (NotSupportedException())
    override _.SetLength(_) = raise (NotSupportedException())

    override _.Read(buffer: byte array, offset: int, count: int) =
        if count = 0 then 0
        else
            if currentPos >= currentBuf.Length then
                let mutable chunk = [||]
                if incoming.TryTake(&chunk, Timeout.Infinite) then
                    currentBuf <- chunk
                    currentPos <- 0
                else
                    currentBuf <- [||]
                    currentPos <- 0
            if currentPos >= currentBuf.Length then 0
            else
                let n = min count (currentBuf.Length - currentPos)
                Array.Copy(currentBuf, currentPos, buffer, offset, n)
                currentPos <- currentPos + n
                n

    override _.ReadAsync(buffer: byte array, offset: int, count: int, ct: CancellationToken) =
        Task.Run<int>(fun () ->
            if count = 0 then 0
            else
                if currentPos >= currentBuf.Length then
                    let mutable chunk = [||]
                    if incoming.TryTake(&chunk, Timeout.Infinite, ct) then
                        currentBuf <- chunk
                        currentPos <- 0
                    else
                        currentBuf <- [||]
                        currentPos <- 0
                if currentPos >= currentBuf.Length then 0
                else
                    let n = min count (currentBuf.Length - currentPos)
                    Array.Copy(currentBuf, currentPos, buffer, offset, n)
                    currentPos <- currentPos + n
                    n)

    override _.Write(buffer: byte array, offset: int, count: int) =
        if count > 0 then
            let copy = Array.zeroCreate<byte> count
            Array.Copy(buffer, offset, copy, 0, count)
            outgoing.Add(copy)

    override this.WriteAsync(buffer: byte array, offset: int, count: int, ct: CancellationToken) =
        this.Write(buffer, offset, count)
        Task.CompletedTask

    override _.Dispose(disposing: bool) =
        if disposing then
            try outgoing.CompleteAdding() with _ -> ()
        base.Dispose(disposing)

let mutable checks = 0
let check name condition =
    if not condition then
        failwithf "FAILED: %s" name
    checks <- checks + 1
    printfn "PASS: %s" name

let tempDbPath = Path.Combine(Path.GetTempPath(), sprintf "esm_v110_test_%s.db" (Guid.NewGuid().ToString("N")))
MasterDataRepository.CustomDatabasePath <- Some tempDbPath

try
    // 1. 翻訳辞書 12言語配列長・日本語残存検査
    let locSource = File.ReadAllText(Path.Combine(__SOURCE_DIRECTORY__, "..", "EasyServiceManager.Core", "Services", "Localization.fs"))
    let keyMatches = Regex.Matches(locSource, """^\s*"([^"]+)",\s*\[\|""", RegexOptions.Multiline)
    check "Localization keys found in source" (keyMatches.Count >= 100)

    let kanaRegex = Regex("[\u3040-\u309F\u30A1-\u30FA\u30FC-\u30FF]")
    let nonJaLangs =
        Localization.languages
        |> Array.map fst
        |> Array.filter (fun c -> c <> "ja-JP")

    for m in keyMatches do
        let key = m.Groups.[1].Value
        for lang in nonJaLangs do
            let translated = Localization.translate lang key
            if String.IsNullOrWhiteSpace(translated) then
                failwithf "Empty translation for key '%s' in '%s'" key lang
            if kanaRegex.IsMatch(translated) then
                failwithf "Japanese kana leaked in key '%s' for '%s': %s" key lang translated
    check "All Localization keys translated without kana leaks across 11 non-ja languages" true

    // 2. 言語コード正規化（短縮形・大文字小文字・アンダースコア）
    check "tryNormalizeCode ja" (Localization.tryNormalizeCode "ja" = Some "ja-JP")
    check "tryNormalizeCode en" (Localization.tryNormalizeCode "en" = Some "en-US")
    check "tryNormalizeCode EN_us" (Localization.tryNormalizeCode "EN_us" = Some "en-US")
    check "tryNormalizeCode zh" (Localization.tryNormalizeCode "zh" = Some "zh-CN")
    check "tryNormalizeCode zh-TW" (Localization.tryNormalizeCode "zh-TW" = Some "zh-TW")
    check "tryNormalizeCode zh_tw" (Localization.tryNormalizeCode "zh_tw" = Some "zh-TW")
    check "tryNormalizeCode ko" (Localization.tryNormalizeCode "ko" = Some "ko-KR")
    check "tryNormalizeCode vi" (Localization.tryNormalizeCode "vi" = Some "vi-VN")
    check "tryNormalizeCode de" (Localization.tryNormalizeCode "de" = Some "de-DE")
    check "tryNormalizeCode fr" (Localization.tryNormalizeCode "fr" = Some "fr-FR")
    check "tryNormalizeCode es" (Localization.tryNormalizeCode "es" = Some "es-ES")
    check "tryNormalizeCode it" (Localization.tryNormalizeCode "it" = Some "it-IT")
    check "tryNormalizeCode pt" (Localization.tryNormalizeCode "pt" = Some "pt-BR")
    check "tryNormalizeCode ru" (Localization.tryNormalizeCode "ru" = Some "ru-RU")
    check "tryNormalizeCode invalid" (Localization.tryNormalizeCode "xx-YY" = None)

    // 3. 証明書期限判定境界値 + 実ループバック TLS ハンドシェイク検証
    let nowUtc = DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc)
    let fExpired = SecurityAudit.evaluateCertificateExpiry 443 (nowUtc.AddDays(-1.0)) nowUtc
    check "Cert expired -> Warning 25" (fExpired.Level = FindingLevel.Warning && fExpired.Deduction = 25)
    let fExactNow = SecurityAudit.evaluateCertificateExpiry 443 nowUtc nowUtc
    check "Cert expires now -> Warning 25" (fExactNow.Level = FindingLevel.Warning && fExactNow.Deduction = 25)
    let f14 = SecurityAudit.evaluateCertificateExpiry 443 (nowUtc.AddDays(14.0)) nowUtc
    check "Cert 14 days -> Warning 15" (f14.Level = FindingLevel.Warning && f14.Deduction = 15)
    let f15 = SecurityAudit.evaluateCertificateExpiry 443 (nowUtc.AddDays(15.0)) nowUtc
    check "Cert 15 days -> Warning 10" (f15.Level = FindingLevel.Warning && f15.Deduction = 10)
    let f30 = SecurityAudit.evaluateCertificateExpiry 443 (nowUtc.AddDays(30.0)) nowUtc
    check "Cert 30 days -> Warning 10" (f30.Level = FindingLevel.Warning && f30.Deduction = 10)
    let f31 = SecurityAudit.evaluateCertificateExpiry 443 (nowUtc.AddDays(31.0)) nowUtc
    check "Cert 31 days -> Good 0" (f31.Level = FindingLevel.Good && f31.Deduction = 0)

    fExpired.LanguageCode <- "en-US"
    check "Cert title translated" (fExpired.Title.Contains("Certificate expiry (port 443)"))
    check "Cert detail translated" (fExpired.Detail.Contains("expired"))

    // オンメモリ自己署名証明書による実 SslStream ハンドシェイク検証
    use ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256)
    let req = CertificateRequest("CN=localhost", ecdsa, HashAlgorithmName.SHA256)
    use rawCert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1.0), DateTimeOffset.UtcNow.AddDays(20.0))
    use cert = X509CertificateLoader.LoadPkcs12(rawCert.Export(X509ContentType.Pfx), null)
    use c2s = new BlockingCollection<byte array>()
    use s2c = new BlockingCollection<byte array>()
    use clientStream = new DuplexPipeStream(s2c, c2s)
    use serverStream = new DuplexPipeStream(c2s, s2c)
    let serverTask =
        Task.Run(Func<Task>(fun () ->
            task {
                try
                    use ssl = new SslStream(serverStream, false)
                    do! ssl.AuthenticateAsServerAsync(cert, false, false)
                with ex -> eprintfn "TLS test server error: %s" ex.Message
            } :> Task))
    let probedOpt = SecurityAudit.probeCertificateFromStream clientStream "localhost" 3000
    serverTask.Wait(2000) |> ignore
    check "Live in-memory TLS probe succeeds with self-signed cert" probedOpt.IsSome
    let liveCertFinding = SecurityAudit.evaluateCertificateExpiry 443 probedOpt.Value DateTime.UtcNow
    check "Live TLS cert 20d -> Warning 10" (liveCertFinding.Level = FindingLevel.Warning && liveCertFinding.Deduction = 10)

    let closedProbe = SecurityAudit.probeLoopbackCertificate 1 "localhost" 300
    check "Closed port TLS probe returns None" closedProbe.IsNone

    // 4. DNS チェック
    let badUrlSvc = ServiceEntry("bad-svc", "Bad Service", "Web Server", "起動中", "8443", "", "https://nonexistent.invalid:8443", true, false, false)
    let dnsFindings = SecurityAudit.inspectDns [ badUrlSvc ]
    let dnsLocal = dnsFindings |> List.find (fun f -> f.RawTitle = "DNS (localhost)")
    check "DNS localhost is Good" (dnsLocal.Level = FindingLevel.Good)
    let dnsUrl = dnsFindings |> List.find (fun f -> f.RawTitle = "DNS (サービスURL)")
    check "DNS unresolvable service URL is Warning 10" (dnsUrl.Level = FindingLevel.Warning && dnsUrl.Deduction = 10)
    dnsUrl.LanguageCode <- "en-US"
    check "DNS unresolvable translated without kana" (not (kanaRegex.IsMatch(dnsUrl.Detail)))

    // 5. 露出レベル分類 + FW 連携 + DB 認証設定
    check "Classify 0.0.0.0 -> Public" (Exposure.classify "0.0.0.0" = ExposureLevel.Public)
    check "Classify :: -> Public" (Exposure.classify "::" = ExposureLevel.Public)
    check "Classify 127.0.0.1 -> Local" (Exposure.classify "127.0.0.1" = ExposureLevel.Local)
    check "Classify ::1 -> Local" (Exposure.classify "::1" = ExposureLevel.Local)
    check "Classify 192.168.1.50 -> Lan" (Exposure.classify "192.168.1.50" = ExposureLevel.Lan)

    let listenersMix =
        [ { Port = 6379; BindAddress = "0.0.0.0"; ProcessId = 100; ProcessName = "redis-server" }
          { Port = 3306; BindAddress = "192.168.1.10"; ProcessId = 101; ProcessName = "mysqld" }
          { Port = 5000; BindAddress = "192.168.1.10"; ProcessId = 102; ProcessName = "kestrel" }
          { Port = 8080; BindAddress = "0.0.0.0"; ProcessId = 103; ProcessName = "nginx" } ]
    let expFindings = SecurityAudit.inspectPortExposure false listenersMix FirewallState.Disabled
    let dbL2 = expFindings |> List.find (fun f -> f.RawTitle = "要注意ポート（DB・全公開）")
    check "DB L2 deduction stays 25 even when FW disabled (no double deduction)" (dbL2.Deduction = 25)
    check "DB L2 detail includes FW disabled note" (dbL2.RawDetail.Contains("Firewallも無効"))
    check "DB L2 detail includes Redis auth note" (dbL2.RawDetail.Contains("requirepass"))
    dbL2.LanguageCode <- "en-US"
    check "DB L2 translated completely to English" (not (kanaRegex.IsMatch(dbL2.Title)) && not (kanaRegex.IsMatch(dbL2.Detail)))

    let dbL1 = expFindings |> List.find (fun f -> f.RawTitle = "要注意ポート（DB・LAN公開）")
    check "DB L1 deduction is 10" (dbL1.Level = FindingLevel.Warning && dbL1.Deduction = 10)
    let adminL1 = expFindings |> List.find (fun f -> f.RawTitle = "要注意ポート（管理系・LAN公開）")
    check "Admin L1 deduction is 10" (adminL1.Level = FindingLevel.Warning && adminL1.Deduction = 10)
    let genInfo = expFindings |> List.find (fun f -> f.RawTitle = "一般公開ポート")
    check "General port is Information 0" (genInfo.Level = FindingLevel.Information && genInfo.Deduction = 0)

    let redisBad = SecurityAudit.evaluateRedisConfig "bind 0.0.0.0\nprotected-mode no\n# requirepass foo" true
    check "Redis no pass + protected-mode no + non-loopback -> Warning 25" (redisBad.Level = FindingLevel.Warning && redisBad.Deduction = 25)
    let redisProtected = SecurityAudit.evaluateRedisConfig "bind 0.0.0.0\nprotected-mode yes" true
    check "Redis no pass + protected-mode yes -> Warning 10" (redisProtected.Level = FindingLevel.Warning && redisProtected.Deduction = 10)
    let redisGood = SecurityAudit.evaluateRedisConfig "requirepass SuperSecretPassword123\nprotected-mode no" true
    check "Redis with requirepass -> Good 0 and hides password" (redisGood.Level = FindingLevel.Good && not (redisGood.RawDetail.Contains("SuperSecretPassword123")))

    let mongoBad = SecurityAudit.evaluateMongoConfig "net:\n  bindIp: 0.0.0.0\nsecurity:\n  authorization: disabled" true
    check "MongoDB unauthenticated non-loopback -> Warning 25" (mongoBad.Level = FindingLevel.Warning && mongoBad.Deduction = 25)
    let mongoLocal = SecurityAudit.evaluateMongoConfig "net:\n  bindIp: 127.0.0.1" false
    check "MongoDB unauthenticated local -> Information 0" (mongoLocal.Level = FindingLevel.Information && mongoLocal.Deduction = 0)
    let mongoGood = SecurityAudit.evaluateMongoConfig "net:\n  bindIp: 0.0.0.0\nsecurity:\n  authorization: enabled" true
    check "MongoDB enabled -> Good 0" (mongoGood.Level = FindingLevel.Good)

    // 6. MasterDataRepository & VersionDiagnostics
    MasterDataRepository.Initialize()
    check "SQLite DB file created on disk" (File.Exists(tempDbPath))
    let snapshot = MasterDataRepository.GetSnapshot()
    check "Snapshot has httpd runtime" (snapshot.Runtimes |> List.exists (fun r -> r.Id = "httpd"))
    check "Snapshot has nginx advisory" (snapshot.Runtimes |> List.exists (fun r -> r.Id = "nginx" && not r.Advisories.IsEmpty))
    check "Snapshot has simple-service-diagnosis app" (MasterDataRepository.TryGetApp(MasterDataRepository.AppId, snapshot).IsSome)

    let refDate = DateOnly(2026, 11, 20)
    let makeRt id name ver src exp : DetectedHostRuntime =
        { RuntimeId = id; DisplayName = name; Version = (VersionDiagnostics.tryParseVersion ver).Value; RawVersion = ver; DetectionSource = src; Exposure = exp }

    let advL0 = VersionDiagnostics.diagnoseHostRuntimes [ makeRt "nginx" "nginx" "1.30.6" "dpkg (nginx)" ExposureLevel.Local ] refDate snapshot
    check "nginx 1.30.6 L0 -> Info 0" (advL0.Length = 1 && advL0.Head.Level = FindingLevel.Information && advL0.Head.Deduction = 0)
    let advL1 = VersionDiagnostics.diagnoseHostRuntimes [ makeRt "nginx" "nginx" "1.30.6" "dpkg (nginx)" ExposureLevel.Lan ] refDate snapshot
    check "nginx 1.30.6 L1 -> Warning 25" (advL1.Length = 1 && advL1.Head.Level = FindingLevel.Warning && advL1.Head.Deduction = 25)
    advL1.Head.LanguageCode <- "en-US"
    check "nginx advisory translated without kana" (not (kanaRegex.IsMatch(advL1.Head.Title)) && not (kanaRegex.IsMatch(advL1.Head.Detail)))

    let advFixed = VersionDiagnostics.diagnoseHostRuntimes [ makeRt "nginx" "nginx" "1.30.7" "dpkg (nginx)" ExposureLevel.Public ] refDate snapshot
    check "nginx 1.30.7 L2 -> silent" advFixed.IsEmpty

    let eolL0 = VersionDiagnostics.diagnoseHostRuntimes [ makeRt "httpd" "Apache httpd" "2.2.34" "FileVersionInfo (httpd.exe)" ExposureLevel.Local ] refDate snapshot
    check "httpd 2.2.34 EOL L0 -> Info 0" (eolL0.Length = 1 && eolL0.Head.Level = FindingLevel.Information && eolL0.Head.Deduction = 0)
    let eolL1 = VersionDiagnostics.diagnoseHostRuntimes [ makeRt "httpd" "Apache httpd" "2.2.34" "FileVersionInfo (httpd.exe)" ExposureLevel.Lan ] refDate snapshot
    check "httpd 2.2.34 EOL L1 -> Warning 10" (eolL1.Length = 1 && eolL1.Head.Level = FindingLevel.Warning && eolL1.Head.Deduction = 10)
    let eolL2 = VersionDiagnostics.diagnoseHostRuntimes [ makeRt "httpd" "Apache httpd" "2.2.34" "FileVersionInfo (httpd.exe)" ExposureLevel.Public ] refDate snapshot
    check "httpd 2.2.34 EOL L2 -> Warning 15" (eolL2.Length = 1 && eolL2.Head.Level = FindingLevel.Warning && eolL2.Head.Deduction = 15)

    let lagL0 = VersionDiagnostics.diagnoseHostRuntimes [ makeRt "httpd" "Apache httpd" "2.4.58" "FileVersionInfo (httpd.exe)" ExposureLevel.Local ] refDate snapshot
    check "httpd 2.4.58 patch lag L0 -> silent" lagL0.IsEmpty
    let lagL1 = VersionDiagnostics.diagnoseHostRuntimes [ makeRt "httpd" "Apache httpd" "2.4.58" "FileVersionInfo (httpd.exe)" ExposureLevel.Lan ] refDate snapshot
    check "httpd 2.4.58 patch lag L1 -> Info 0" (lagL1.Length = 1 && lagL1.Head.Level = FindingLevel.Information && lagL1.Head.Deduction = 0)
    let lagL2 = VersionDiagnostics.diagnoseHostRuntimes [ makeRt "httpd" "Apache httpd" "2.4.58" "FileVersionInfo (httpd.exe)" ExposureLevel.Public ] refDate snapshot
    check "httpd 2.4.58 patch lag L2 -> Warning 10" (lagL2.Length = 1 && lagL2.Head.Level = FindingLevel.Warning && lagL2.Head.Deduction = 10)
    lagL2.Head.LanguageCode <- "en-US"
    check "httpd lag translated without kana" (not (kanaRegex.IsMatch(lagL2.Head.Title)) && not (kanaRegex.IsMatch(lagL2.Head.Detail)))

    // FetchAndImportAsync モック & ShouldAutoFetch & isAppOutdated
    let nowOffset = DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero)
    check "ShouldAutoFetch initially true" (MasterDataRepository.ShouldAutoFetch(nowOffset))
    let mockFetcher (url: string) = async {
        if url.EndsWith("apps.json") then
            return Ok """{"schemaVersion":1,"updatedAt":"2026-11-20T00:00:00Z","apps":[{"id":"simple-service-diagnosis","displayName":"簡単サービス診断","latest":"1.2.0","storeUrl":"https://example.com"}]}"""
        else
            return Error "skip"
    }
    let updatedOk = MasterDataRepository.FetchAndImportAsync(customFetcher = mockFetcher, nowUtc = nowOffset) |> Async.RunSynchronously
    check "FetchAndImportAsync mock updates apps" updatedOk
    // runtimes fetched failed in mock -> ShouldAutoFetch still checks runtimes fetchedAt, so mock both:
    let mockFetcherBoth (url: string) = async {
        if url.EndsWith("apps.json") then
            return Ok """{"schemaVersion":1,"updatedAt":"2026-11-20T00:00:00Z","apps":[{"id":"simple-service-diagnosis","displayName":"簡単サービス診断","latest":"1.2.0","storeUrl":"https://example.com"}]}"""
        else
            return Ok """{"schemaVersion":1,"updatedAt":"2026-11-20T00:00:00Z","runtimes":[]}"""
    }
    let _ = MasterDataRepository.FetchAndImportAsync(customFetcher = mockFetcherBoth, nowUtc = nowOffset) |> Async.RunSynchronously
    check "ShouldAutoFetch false within 24h" (not (MasterDataRepository.ShouldAutoFetch(nowOffset.AddHours(12.0))))
    check "ShouldAutoFetch true after 24h" (MasterDataRepository.ShouldAutoFetch(nowOffset.AddHours(24.1)))
    check "isAppOutdated 1.1.0 vs 1.2.0" (VersionDiagnostics.isAppOutdated "1.1.0" "1.2.0")
    check "isAppOutdated 1.1.0 vs 1.1.0 false" (not (VersionDiagnostics.isAppOutdated "1.1.0" "1.1.0"))

    // 7. QR コード生成＆端末半ブロック描画
    let qrUrl = "http://192.168.1.10:5070/?t=0123456789abcdef"
    let qrAnsi = QrCodeCore.renderToTerminal qrUrl true
    check "QR ANSI terminal rendering contains ANSI colors and half blocks" (qrAnsi.Contains("\u001b[30;47m") && qrAnsi.Contains("\u001b[0m") && (qrAnsi.Contains("█") || qrAnsi.Contains("▀") || qrAnsi.Contains("▄")))
    let qrPlain = QrCodeCore.renderToTerminal qrUrl false
    check "QR plain terminal rendering has no ANSI codes" (not (qrPlain.Contains("\u001b[")) && qrPlain.Split('\n').Length >= 15)

    // 8. CLI 終了コード判定（翻訳後も不変）
    check "Exit code 0 when no warnings" (Program.determineExitCode [ f31 ] = 0)
    check "Exit code 1 for L1 warning (-10)" (Program.determineExitCode [ dbL1 ] = 1)
    check "Exit code 2 for expired cert (-25)" (Program.determineExitCode [ fExpired ] = 2)
    check "Exit code 2 for DB L2 (-25)" (Program.determineExitCode [ dbL2 ] = 2)
    check "Exit code 2 for advisory L1 (-25)" (Program.determineExitCode advL1 = 2)

    // 9. Formatters (text / json / markdown / html) 11非日本語言語の完全翻訳検査（showDiffOnly=false フル出力）
    let fullFindings = [ fExpired; f14; f31; dnsLocal; dnsUrl; dbL2; dbL1; adminL1; genInfo; redisBad; mongoBad; advL0.Head; advL1.Head; eolL2.Head; lagL2.Head ]
    let fullServices =
        [ ServiceEntry("nginx", "nginx", "Web Server", "起動中", "80, 443", "", "https://localhost:443", false, true, false)
          ServiceEntry("mysqld", "MySQL", "Database", "停止中", "3306", "", "", false, false, false) ]
    fullServices.[0].BindAddress <- "0.0.0.0"
    fullServices.[0].ExposureCode <- "L2"
    fullServices.[0].IsDiff <- true
    fullServices.[0].DiffNote <- "前回から変化: 新規待ち受け (0.0.0.0:443)"
    let themeOff = Formatters.ColorTheme(false)
    let diffsSample = [ "新規サービス: nginx"; "新規待ち受け: 0.0.0.0:443 (nginx)" ]
    for lang in nonJaLangs do
        for f in fullFindings do f.LanguageCode <- lang
        for s in fullServices do s.LanguageCode <- lang
        let scoreOpt = SecurityAudit.score fullFindings
        let scoreVal = scoreOpt |> Option.map fst
        let grade = scoreOpt |> Option.map snd |> Option.defaultValue "—"
        let textOut = Formatters.formatText themeOff lang true true scoreVal grade fullFindings fullServices diffsSample true false false (Some "2026-11-19")
        if kanaRegex.IsMatch(textOut) then
            failwithf "Japanese kana leaked in full text output for '%s':\n%s" lang textOut
        let jsonOut = Formatters.formatJson lang true true scoreVal grade fullFindings fullServices listenersMix diffsSample true
        if kanaRegex.IsMatch(jsonOut) then
            failwithf "Japanese kana leaked in full json output for '%s':\n%s" lang jsonOut
        let scoreText = match scoreVal with Some v -> sprintf "%d / 100" v | None -> Localization.translate lang "未評価"
        let mdOut = ReportExporter.buildMarkdownReport lang fullServices fullFindings scoreText (Formatters.localizeGrade lang grade) diffsSample true
        if kanaRegex.IsMatch(mdOut) then
            failwithf "Japanese kana leaked in full markdown output for '%s':\n%s" lang mdOut
        let htmlOut = ReportExporter.buildHtmlReport lang fullServices fullFindings scoreText (Formatters.localizeGrade lang grade) diffsSample true
        if kanaRegex.IsMatch(htmlOut) then
            failwithf "Japanese kana leaked in full html output for '%s'" lang
    check "Full text/json/markdown/html outputs contain zero kana leaks across all 11 non-ja languages" true

    // 10. SecurityFinding 要約表示・減点バッジプロパティ検証
    let multiLineFinding =
        SecurityFinding(
            "要注意ポート（DB・全公開）",
            "ポート 6379 が全インターフェースで待ち受けています。\nFirewallも無効のため、外部から到達できる可能性が高いです。\n認証設定（Redis: requirepass / protected-mode）の確認を推奨します。",
            FindingLevel.Warning,
            25)
    check "SummaryDetail returns first line only (JA)" (multiLineFinding.SummaryDetail = "ポート 6379 が全インターフェースで待ち受けています。" && not (multiLineFinding.SummaryDetail.Contains("\n")))
    check "HasDeduction true and DeductionLabel JA" (multiLineFinding.HasDeduction && multiLineFinding.DeductionLabel = "-25点")
    multiLineFinding.LanguageCode <- "en-US"
    check "SummaryDetail returns first line only (EN)" (multiLineFinding.SummaryDetail = "Port(s) 6379 listening on all interfaces." && not (multiLineFinding.SummaryDetail.Contains("\n")))
    check "DeductionLabel translated EN" (multiLineFinding.DeductionLabel = "-25 pts")
    check "Good finding HasDeduction false" (not f31.HasDeduction && f31.DeductionLabel = "")

    printfn "\nALL %d VERIFICATION CHECKS PASSED!" checks
finally
    MasterDataRepository.CustomDatabasePath <- None
    try if File.Exists(tempDbPath) then File.Delete(tempDbPath) with _ -> ()
