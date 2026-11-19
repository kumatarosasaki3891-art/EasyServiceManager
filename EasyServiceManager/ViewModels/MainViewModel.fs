namespace EasyServiceManager.ViewModels

open System
open System.Collections.ObjectModel
open System.Diagnostics
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open System.Windows.Input
open Avalonia.Media.Imaging
open Avalonia.Threading
open CommunityToolkit.Mvvm.Input
open EasyServiceManager.Models
open EasyServiceManager.Services

/// <summary>言語選択ドロップダウン用の項目モデル。</summary>
[<AllowNullLiteral>]
type LanguageOption(key: string, label: string) =
    member _.Key = key
    member _.Label = label

/// <summary>左ペインのカテゴリ選択項目モデル。</summary>
[<AllowNullLiteral>]
type CategoryOption(name: string, icon: string) =
    inherit CommunityToolkit.Mvvm.ComponentModel.ObservableObject()

    let mutable languageCode = "ja-JP"
    let key = if name = "すべて" then "All" elif name = "お気に入り" then "Favorites" else name
    let localizationKey = if key = "All" then "すべて" elif key = "Favorites" then "お気に入り" else key

    member _.Key = key
    member _.Name = name
    member _.Icon = icon
    /// <summary>現在の言語に翻訳されたカテゴリ名。</summary>
    member _.Label = Localization.translate languageCode localizationKey

    /// <summary>表示言語コード。</summary>
    member this.LanguageCode
        with get () = languageCode
        and set value =
            if this.SetProperty(&languageCode, value) then this.OnPropertyChanged("Label")

/// <summary>カスタムサービスの登録プリセットモデル。</summary>
[<AllowNullLiteral>]
type CustomPreset(label: string, name: string, displayName: string, category: string, url: string) =
    member _.Label = label
    member _.Name = name
    member _.DisplayName = displayName
    member _.Category = category
    member _.Url = url

/// <summary>サービス一覧・簡易診断・カスタム登録・レポート出力およびQR転送を統括するメイン ViewModel。</summary>
type MainViewModel() as this =
    inherit ViewModelBase()

    let categoryNames =
        [ "Web Server"; "Container"; "Database"; "SSH"; "Monitoring"; "Mail"; "File Share"
          "Virtualization"; "AI / LLM"; "Cloud Agent"; "Security"; "Remote Access"; "Other" ]

    do MasterDataRepository.Initialize()

    let currentAppVersion =
        let ver = typeof<MainViewModel>.Assembly.GetName().Version
        if isNull ver then "1.1.0"
        else sprintf "%d.%d.%d" ver.Major ver.Minor ver.Build

    let mutable masterDataSnapshot = MasterDataRepository.GetSnapshot()
    let mutable lastListeners: ListenerInfo list = []

    let mutable languageCode = AppSettings.loadLanguage ()
    let languageOptions = Localization.languages |> Array.map (fun (key, label) -> LanguageOption(key, label))
    let mutable selectedLanguage = languageOptions |> Array.find (fun option -> option.Key = languageCode)

    let categoryOptions =
        ObservableCollection<CategoryOption>(
            [ CategoryOption("すべて", "◉")
              CategoryOption("お気に入り", "★")
              CategoryOption("Web Server", "🌐")
              CategoryOption("Container", "🐳")
              CategoryOption("Database", "▤")
              CategoryOption("SSH", "🔒")
              CategoryOption("Monitoring", "◷")
              CategoryOption("Mail", "✉")
              CategoryOption("File Share", "▣")
              CategoryOption("Virtualization", "◇")
              CategoryOption("AI / LLM", "✦")
              CategoryOption("Cloud Agent", "☁")
              CategoryOption("Security", "⛨")
              CategoryOption("Remote Access", "📡")
              CategoryOption("Other", "⋯") ])
    let customCategories =
        ObservableCollection<CategoryOption>(categoryOptions |> Seq.filter (fun option -> categoryNames |> List.contains option.Name))
    do categoryOptions |> Seq.iter (fun option -> option.LanguageCode <- languageCode)

    let allServices = ObservableCollection<ServiceEntry>()
    let visibleServices = ObservableCollection<ServiceEntry>()
    let securityFindings = ObservableCollection<SecurityFinding>()
    let scanDiffMessages = ObservableCollection<string>()
    let mutable rawDiffMessages: string list = []
    let mutable hasPreviousScan = false
    let mutable selectedService: ServiceEntry = null
    let mutable selectedCategory = categoryOptions.[0]
    let mutable searchText = ""
    let mutable statusMessage = "サービスを検出しています..."
    let mutable errorMessage = ""
    let mutable isAddingCustom = false
    let mutable customName = ""
    let mutable customDisplayName = ""
    let mutable customCategory = "Web Server"
    let mutable customUrl = ""
    let mutable securityScoreText = "未評価"
    let mutable securityGrade = "—"
    let mutable isScanning = false

    // レポート出力・QR転送モーダル用の状態
    let mutable isExportModalOpen = false
    let mutable isQrSectionVisible = false
    let mutable isQrActive = false
    let mutable isQrInvalidated = false
    let mutable qrImage: WriteableBitmap = null
    let mutable qrUrlText = ""
    let mutable qrStatusText = ""
    let mutable qrStatusBg = "#F3FAF8"
    let mutable qrStatusFg = "#00695C"
    let mutable selectedIpIndex = 0
    let mutable hasMultipleIps = false
    let mutable switchIpButtonText = ""
    let mutable exportMessage = ""
    let mutable exportMessageColor = "#1D6B4B"
    let mutable activeQrServer: QrReportTransferService option = None
    let mutable qrCountdownCts: CancellationTokenSource option = None
    let mutable isQrClientConnected = false
    let mutable saveFileHandler: Func<string, string, string, Task<string>> = null
    let mutable isInfoDialogOpen = false
    let mutable isUpdatingMasterData = false
    let mutable masterDataUpdateStatus = ""

    let localize key = Localization.translate languageCode key

    /// <summary>文字列が http:// または https:// の有効な絶対URLか判定します。</summary>
    let isHttpUrl (url: string) =
        match Uri.TryCreate(url, UriKind.Absolute) with
        | true, uri -> uri.Scheme = Uri.UriSchemeHttp || uri.Scheme = Uri.UriSchemeHttps
        | _ -> false

    /// <summary>選択中のカテゴリと検索クエリでサービス表示一覧と件数表記を再構築します。</summary>
    let refreshVisible () =
        visibleServices.Clear()
        let query = searchText.Trim()
        allServices
        |> Seq.filter (fun service ->
            let categoryMatches =
                selectedCategory.Key = "All"
                || (selectedCategory.Key = "Favorites" && service.IsFavorite)
                || service.Category = selectedCategory.Key
            let queryMatches =
                String.IsNullOrEmpty query
                || service.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || service.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || service.Category.Contains(query, StringComparison.OrdinalIgnoreCase)
                || service.CategoryLabel.Contains(query, StringComparison.OrdinalIgnoreCase)
            categoryMatches && queryMatches)
        |> Seq.sortBy (fun service -> (not service.IsDiff, service.Name))
        |> Seq.iter visibleServices.Add
        if not (isNull selectedService) && not (visibleServices.Contains(selectedService)) then
            this.SelectedService <- null
        if allServices.Count > 0 then
            let hasQuery = not (String.IsNullOrEmpty query)
            let isAllCategory = isNull selectedCategory || selectedCategory.Key = "All"
            if isAllCategory && not hasQuery then
                statusMessage <- sprintf "%d 件のサービスを検出しました" allServices.Count
            elif not isAllCategory && not hasQuery then
                statusMessage <- Localization.format languageCode "{0} のサービス {1} 件（全 {2} 件中）" [| box selectedCategory.Label; box visibleServices.Count; box allServices.Count |]
            elif isAllCategory && hasQuery then
                statusMessage <- Localization.format languageCode "検索結果 {0} 件（全 {1} 件中）" [| box visibleServices.Count; box allServices.Count |]
            else
                statusMessage <- Localization.format languageCode "{0} の検索結果 {1} 件（全 {2} 件中）" [| box selectedCategory.Label; box visibleServices.Count; box allServices.Count |]
            this.NotifyChanged("StatusMessage")
        this.NotifyChanged("ServiceCountBadgeText")

    /// <summary>現在の言語に合わせて差分メッセージ一覧を再構築します。</summary>
    let rebuildScanDiffs () =
        scanDiffMessages.Clear()
        for msg in rawDiffMessages do
            scanDiffMessages.Add("• " + Localization.translateText languageCode msg)
        this.NotifyChanged("ScanDiffMessages")
        this.NotifyChanged("HasScanDiffs")
        this.NotifyChanged("ShowNoDiffMessage")
        this.NotifyChanged("ScanDiffTitle")
        this.NotifyChanged("NoDiffMessageLabel")

    /// <summary>稼働中の QR 転送サーバーとカウントダウンタイマーを停止・破棄します。</summary>
    let stopQrSession () =
        match qrCountdownCts with
        | Some c ->
            try c.Cancel() with _ -> ()
            try c.Dispose() with _ -> ()
            qrCountdownCts <- None
        | None -> ()
        match activeQrServer with
        | Some srv ->
            try (srv :> IDisposable).Dispose() with _ -> ()
            activeQrServer <- None
        | None -> ()

    /// <summary>現在選択されている IP インデックスに応じて QR コード画像と URL 表示を更新します。</summary>
    let updateQrDisplay () =
        match activeQrServer with
        | Some srv when not srv.IpCandidates.IsEmpty ->
            let count = srv.IpCandidates.Length
            let candidate = srv.IpCandidates.[selectedIpIndex % count]
            let url = srv.GetTransferUrl(candidate.IpAddress)
            this.QrUrlText <-
                if String.IsNullOrWhiteSpace candidate.AdapterName then url
                else sprintf "%s (%s)" url candidate.AdapterName
            this.QrImage <- QrCodeGenerator.generateBitmap url 8 4
            this.HasMultipleIps <- count > 1
            this.SwitchIpButtonText <-
                if count > 1 then Localization.format languageCode "🌐 別のIPに切替 ({0}/{1})" [| box ((selectedIpIndex % count) + 1); box count |]
                else ""
        | _ -> ()

    let customPresets =
        [| CustomPreset("Vite (5173)", "vite-dev", "Vite 開発サーバー", "Web Server", "http://localhost:5173")
           CustomPreset("Kestrel (5000)", "kestrel-app", "ASP.NET Core Kestrel", "Web Server", "http://localhost:5000")
           CustomPreset("Ollama (11434)", "ollama", "Ollama ローカルLLM", "AI / LLM", "http://localhost:11434")
           CustomPreset("Node (3000)", "node-app", "Node.js 汎用アプリ", "Web Server", "http://localhost:3000")
           CustomPreset("Django (8000)", "django-app", "Django 開発サーバー", "Web Server", "http://localhost:8000")
           CustomPreset("MySQL (3306)", "mysql-local", "MySQL Database", "Database", "http://localhost:3306")
           CustomPreset("PostgreSQL (5432)", "postgres-local", "PostgreSQL Database", "Database", "http://localhost:5432") |]

    let refreshCommand = RelayCommand(Action(fun () -> this.Refresh()))
    let toggleFavoriteCommand = RelayCommand(Action(fun () -> this.ToggleFavorite()))
    let addCustomCommand = RelayCommand(Action(fun () -> this.AddCustomService()))
    let cancelCustomCommand = RelayCommand(Action(fun () -> this.IsAddingCustom <- false))
    let toggleCustomFormCommand = RelayCommand(Action(fun () -> this.IsAddingCustom <- not this.IsAddingCustom))
    let openBrowserCommand = RelayCommand(Action(fun () -> this.OpenSelectedService()))
    let openExportModalCommand = RelayCommand(Action(fun () -> this.OpenExportModal()))
    let closeExportModalCommand = RelayCommand(Action(fun () -> this.CloseExportModal()))
    let saveMarkdownCommand = RelayCommand(Action(fun () -> this.SaveReportAsync("markdown") |> ignore))
    let saveHtmlCommand = RelayCommand(Action(fun () -> this.SaveReportAsync("html") |> ignore))
    let startQrTransferCommand = RelayCommand(Action(fun () -> this.StartQrTransfer()))
    let switchQrIpCommand = RelayCommand(Action(fun () -> this.SwitchQrIp()))
    let applyPresetCommand = RelayCommand<CustomPreset>(Action<CustomPreset>(fun p -> this.ApplyPreset(p)))
    let openInfoDialogCommand = RelayCommand(Action(fun () -> this.OpenInfoDialog()))
    let closeInfoDialogCommand = RelayCommand(Action(fun () -> this.IsInfoDialogOpen <- false))
    let updateMasterDataCommand = RelayCommand(Action(fun () -> this.UpdateMasterDataAsync(silent = false) |> Async.Start))
    let openWebsiteCommand = RelayCommand(Action(fun () -> this.OpenExternalUrl(MasterDataRepository.WebsiteUrl)))
    let openInstagramCommand = RelayCommand(Action(fun () -> this.OpenExternalUrl(MasterDataRepository.InstagramUrl)))
    let openReleasesCommand = RelayCommand(Action(fun () -> this.OpenExternalUrl(MasterDataRepository.ReleasesUrl)))
    let openLatestVersionCommand = RelayCommand(Action(fun () -> this.OpenLatestVersionPage()))

    do
        this.Refresh()
        if MasterDataRepository.ShouldAutoFetch(DateTimeOffset.UtcNow) then
            this.UpdateMasterDataAsync(silent = true) |> Async.Start

    member private this.NotifyChanged(propertyName: string) = this.OnPropertyChanged(propertyName)

    member _.CategoryOptions = categoryOptions
    member _.CustomCategories = customCategories
    member _.LanguageOptions = languageOptions
    member _.Services = visibleServices
    member _.AllServices = allServices
    member _.SecurityFindings = securityFindings
    member _.SecurityScoreText = if securityScoreText = "未評価" then localize securityScoreText else securityScoreText
    member _.SecurityGrade = securityGrade
    member _.IsScanning = isScanning
    member _.CanRefresh = not isScanning

    member this.Language
        with get () = selectedLanguage
        and set (value: LanguageOption) =
            if not (isNull value) && languageCode <> value.Key then
                languageCode <- Localization.normalizeCode value.Key
                this.SetProperty(&selectedLanguage, value) |> ignore
                categoryOptions |> Seq.iter (fun option -> option.LanguageCode <- languageCode)
                allServices |> Seq.iter (fun service -> service.LanguageCode <- languageCode)
                securityFindings |> Seq.iter (fun finding -> finding.LanguageCode <- languageCode)
                for propertyName in
                    [ "AppTitle"; "AppSubtitle"; "ServiceListLabel"; "LanguageLabel"; "CategoryHeading"; "StatusMessage"
                      "ErrorMessage"; "SearchPlaceholder"; "RefreshLabel"; "SecurityTitle"; "SecuritySummary"
                      "WarningCountLabel"; "SecurityGrade"; "SecurityGradeLabel"; "SecurityGradeLetter"; "IsProvisional"; "ProvisionalBadgeText"; "SecurityScoreText"; "SecurityRefreshLabel"
                      "DiagnosticItemsLabel"; "SecurityCardColor"; "DetailTitle"; "NoSelectedServiceText"
                      "ServiceNameLabel"; "DisplayNameLabel"; "ServiceCategoryLabel"; "StateLabel"; "PortLabel"
                      "ExecutableLabel"; "UrlLabel"; "FavoriteButtonText"; "BrowserButtonLabel"; "CustomFormTitle"
                      "CustomFormDescription"; "CustomNameLabel"; "CustomDisplayNameLabel"; "CustomCategoryLabel"
                      "CustomUrlLabel"; "CustomNamePlaceholder"; "CustomDisplayNamePlaceholder"
                      "CustomCategoryPlaceholder"; "CustomUrlPlaceholder"; "AddCustomLabel"; "CancelLabel"
                      "ToggleCustomFormLabel"; "ExportReportButtonLabel"; "ExportModalTitle"
                      "ExportModalDescription"; "SaveMarkdownButtonLabel"; "SaveHtmlButtonLabel"
                      "StartQrTransferButtonLabel"; "CloseModalButtonLabel"; "QrGuideLabel"
                      "QrSubGuideLabel"; "QrSecurityNoteLabel"; "QrInvalidatedLabel"; "RegenerateQrButtonLabel"
                      "PresetHeadingLabel"; "ContainerIntegrationGuideLabel"; "ExposureSectionLabel"
                      "DefinitionDateText"; "InfoDialogTitle"; "CurrentVersionHeader"; "LatestVersionHeader"
                      "LatestAppVersionText"; "AppUpdateAvailableText"; "ViewLatestVersionLabel"
                      "UpdateMasterDataButtonLabel"; "MasterDataUpdateStatus"; "WebsiteButtonLabel"
                      "ReleasesButtonLabel"; "PrivacyNoticeText" ] do
                    this.OnPropertyChanged(propertyName)
                updateQrDisplay()
                refreshVisible()
                rebuildScanDiffs()
                match AppSettings.saveLanguage languageCode with
                | Ok () -> ()
                | Error message -> this.ErrorMessage <- sprintf "設定を保存できませんでした: %s" message

    member _.AppTitle = localize "簡単サービス診断"
    member _.AppSubtitle = localize "このマシンで何が動いている？"
    member _.ServiceListLabel = localize "サービス一覧"
    member _.LanguageLabel = localize "言語"
    member _.CategoryHeading = localize "カテゴリ"
    member _.SearchPlaceholder = localize "サービス名・表示名で検索"
    member _.RefreshLabel = localize "再スキャン"
    member _.SecurityTitle = localize "診断結果"
    member _.SecuritySummary = localize "ローカル設定の簡易確認です。脆弱性スキャンではありません。"
    member _.WarningCountLabel = Localization.format languageCode "{0} 件の警告" [| box (securityFindings |> Seq.filter (fun finding -> finding.Level = FindingLevel.Warning) |> Seq.length) |]
    member _.SecurityRefreshLabel = localize "スキャン結果を更新"
    member _.DiagnosticItemsLabel = localize "診断項目"
    member _.DetailTitle = localize "サービス詳細"
    member _.NoSelectedServiceText = localize "サービスを選択すると詳細が表示されます。"
    member _.ServiceNameLabel = localize "サービス名"
    member _.DisplayNameLabel = localize "表示名"
    member _.ServiceCategoryLabel = localize "カテゴリ"
    member _.StateLabel = localize "状態"
    member _.PortLabel = localize "ポート"
    member _.ExecutableLabel = localize "実行ファイル"
    member _.UrlLabel = "URL"
    member _.BrowserButtonLabel = localize "↗  ブラウザで開く"
    member _.CustomFormTitle = localize "カスタムサービス"
    member _.CustomFormDescription = localize "自動検出されないサービスを登録します。"
    member _.CustomNameLabel = localize "サービス名"
    member _.CustomDisplayNameLabel = localize "表示名"
    member _.CustomCategoryLabel = localize "カテゴリ"
    member _.CustomUrlLabel = "URL"
    member _.CustomNamePlaceholder = localize "例: myapp"
    member _.CustomDisplayNamePlaceholder = localize "例: 社内API"
    member _.CustomCategoryPlaceholder = localize "カテゴリを検索"
    member _.CustomUrlPlaceholder = localize "http://localhost:8088"
    member _.AddCustomLabel = localize "登録する"
    member _.CancelLabel = localize "キャンセル"
    member _.ToggleCustomFormLabel = localize "カスタムサービスを登録"
    member _.ExportReportButtonLabel = localize "📤 レポート出力"
    member _.ExportModalTitle = localize "レポート出力"
    member _.ExportModalDescription = localize "診断結果とサービス一覧を保存またはスマホへ転送します。"
    member _.SaveMarkdownButtonLabel = localize "📝 Markdown 保存 (.md)"
    member _.SaveHtmlButtonLabel = localize "🌐 HTML 保存 (.html)"
    member _.StartQrTransferButtonLabel = localize "📱 QRコード転送"
    member _.CloseModalButtonLabel = localize "閉じる"
    member _.QrGuideLabel = localize "スマホでQRコードを読み取ってください"
    member _.QrSubGuideLabel = localize "同一Wi-Fi内のスマホから1対1セッションでレポートをダウンロードできます（有効期限3分・1回限り）。"
    member _.QrSecurityNoteLabel = localize "🔒 インターネットを経由せず、ローカルWi-Fi内で直接1対1転送します。ダウンロード完了後または3分経過でQRコードは自動無効化されます。"
    member _.QrInvalidatedLabel = localize "QRコード無効"
    member _.RegenerateQrButtonLabel = localize "QRコードを再発行"
    member _.PresetHeadingLabel = localize "プリセットから選択"
    member _.ContainerIntegrationGuideLabel = localize "🐧 簡単コンテナ管理で詳細を確認（近日公開）"
    member _.ExposureSectionLabel = localize "露出レベル"
    member _.InfoDialogTitle = localize "アプリ情報"
    member _.CurrentVersionHeader = localize "現在のバージョン"
    member _.LatestVersionHeader = localize "最新バージョン"
    member _.CurrentAppVersionText = currentAppVersion
    member _.LatestAppVersionText =
        match MasterDataRepository.TryGetApp(MasterDataRepository.AppId, masterDataSnapshot) with
        | Some app -> app.Latest
        | None -> localize "未確認"
    member _.IsAppOutdated =
        match MasterDataRepository.TryGetApp(MasterDataRepository.AppId, masterDataSnapshot) with
        | Some app -> VersionDiagnostics.isAppOutdated currentAppVersion app.Latest
        | None -> false
    member _.AppUpdateAvailableText = localize "新しいバージョンがあります"
    member _.HasStoreUpdateLink =
        match MasterDataRepository.TryGetApp(MasterDataRepository.AppId, masterDataSnapshot) with
        | Some app -> app.StoreUrl.IsSome
        | None -> true
    member _.ViewLatestVersionLabel = localize "最新版を見る"
    member _.DefinitionDateText =
        match MasterDataRepository.GetDefinitionDateText masterDataSnapshot with
        | Some d -> Localization.format languageCode "定義データ: {0}時点" [| box d |]
        | None -> localize "定義データ: 未取得"
    member _.UpdateMasterDataButtonLabel =
        if isUpdatingMasterData then localize "更新中..." else localize "定義データを更新"
    member _.CanUpdateMasterData = not isUpdatingMasterData
    member _.WebsiteButtonLabel = localize "紹介サイト"
    member _.WebsiteUrlText = MasterDataRepository.WebsiteUrl
    member _.InstagramButtonLabel = "Instagram"
    member _.InstagramUrlText = MasterDataRepository.InstagramUrl
    member _.ReleasesButtonLabel = localize "リリースページ (GitHub Releases)"
    member _.ReleasesUrlText = MasterDataRepository.ReleasesUrl
    member _.PrivacyNoticeText = localize "診断処理は端末内で完結します。バージョン定義データの取得時のみ GitHub へ通信し、ユーザーのデータや診断結果を送信することはありません。"
    member _.CustomPresets = customPresets
    member _.ApplyPresetCommand: ICommand = applyPresetCommand
    member _.SecurityCardColor = if securityFindings |> Seq.exists (fun finding -> finding.Level = FindingLevel.Warning) then "#FFF0E8" else "#E8F4EE"
    member _.ServiceCountBadgeText =
        let hasQuery = not (String.IsNullOrWhiteSpace searchText)
        let isAllCategory = isNull selectedCategory || selectedCategory.Key = "All"
        if isAllCategory && not hasQuery then string visibleServices.Count
        else sprintf "%d / %d" visibleServices.Count allServices.Count
    member _.ScanDiffMessages = scanDiffMessages
    member _.HasScanDiffs = scanDiffMessages.Count > 0
    member _.ShowNoDiffMessage = hasPreviousScan && scanDiffMessages.Count = 0
    member _.ScanDiffTitle = Localization.format languageCode "⚠ 前回スキャンからの変化 ({0}件)" [| box scanDiffMessages.Count |]
    member _.NoDiffMessageLabel = localize "前回から変化なし"

    member this.ApplyPreset(preset: CustomPreset) =
        if not (isNull preset) then
            this.CustomName <- preset.Name
            this.CustomDisplayName <- preset.DisplayName
            this.CustomCategory <- preset.Category
            this.CustomUrl <- preset.Url
            this.ErrorMessage <- ""

    member this.IsContainerServiceSelected =
        not (isNull selectedService) && (selectedService.Category = "Container" || selectedService.Name.Contains("docker", StringComparison.OrdinalIgnoreCase) || selectedService.Name.Contains("podman", StringComparison.OrdinalIgnoreCase))

    member this.SelectedService
        with get () = selectedService
        and set value =
            if not (Object.ReferenceEquals(selectedService, value)) then
                selectedService <- value
                this.OnPropertyChanged()
                this.OnPropertyChanged("FavoriteButtonText")
                this.OnPropertyChanged("CanOpenSelectedService")
                this.OnPropertyChanged("HasSelectedService")
                this.OnPropertyChanged("HasNoSelectedService")
                this.OnPropertyChanged("IsContainerServiceSelected")

    member this.SelectedCategory
        with get () = selectedCategory
        and set value =
            if not (isNull value) && not (Object.ReferenceEquals(selectedCategory, value)) then
                selectedCategory <- value
                this.OnPropertyChanged()
                refreshVisible()

    member this.SearchText
        with get () = searchText
        and set value =
            if searchText <> value then
                searchText <- value
                this.OnPropertyChanged()
                refreshVisible()

    member this.StatusMessage
        with get () = Localization.translateText languageCode statusMessage
        and set value = if this.SetProperty(&statusMessage, value) then this.OnPropertyChanged("StatusMessage")

    member this.ErrorMessage
        with get () = Localization.translateText languageCode errorMessage
        and set value = if this.SetProperty(&errorMessage, value) then this.OnPropertyChanged("ErrorMessage")

    member this.IsAddingCustom
        with get () = isAddingCustom
        and set value =
            if this.SetProperty(&isAddingCustom, value) then
                this.OnPropertyChanged("HasNotAddingCustom")

    member this.CustomName
        with get () = customName
        and set value = this.SetProperty(&customName, value) |> ignore

    member this.CustomDisplayName
        with get () = customDisplayName
        and set value = this.SetProperty(&customDisplayName, value) |> ignore

    member this.CustomCategory
        with get () = customCategory
        and set value = this.SetProperty(&customCategory, value) |> ignore

    member this.CustomUrl
        with get () = customUrl
        and set value = this.SetProperty(&customUrl, value) |> ignore

    member this.FavoriteButtonText =
        if isNull selectedService then localize "☆ お気に入りに追加"
        elif selectedService.IsFavorite then localize "★ お気に入り解除"
        else localize "☆ お気に入りに追加"

    member this.SecurityGradeLabel =
        if securityGrade.EndsWith("（暫定）", StringComparison.Ordinal) then
            securityGrade.Substring(0, securityGrade.Length - "（暫定）".Length) + " (" + localize "暫定" + ")"
        else securityGrade

    member this.SecurityGradeLetter =
        if securityGrade.EndsWith("（暫定）", StringComparison.Ordinal) then
            securityGrade.Substring(0, securityGrade.Length - "（暫定）".Length)
        else securityGrade

    member this.IsProvisional =
        securityGrade.EndsWith("（暫定）", StringComparison.Ordinal)

    member this.ProvisionalBadgeText =
        localize "暫定"

    member this.CanOpenSelectedService =
        not (isNull selectedService) && isHttpUrl selectedService.Url
    member this.HasSelectedService = not (isNull selectedService)
    member this.HasNoSelectedService = isNull selectedService
    member this.HasNotAddingCustom = not isAddingCustom

    /// <summary>View 側からファイル保存ダイアログを呼び出すためのハンドラー。</summary>
    member _.SaveFileHandler
        with get () = saveFileHandler
        and set value = saveFileHandler <- value

    member this.IsExportModalOpen
        with get () = isExportModalOpen
        and set value = this.SetProperty(&isExportModalOpen, value) |> ignore

    member this.IsQrSectionVisible
        with get () = isQrSectionVisible
        and set value = this.SetProperty(&isQrSectionVisible, value) |> ignore

    member this.IsQrActive
        with get () = isQrActive
        and set value = this.SetProperty(&isQrActive, value) |> ignore

    member this.IsQrInvalidated
        with get () = isQrInvalidated
        and set value = this.SetProperty(&isQrInvalidated, value) |> ignore

    member this.QrImage
        with get () = qrImage
        and set value = this.SetProperty(&qrImage, value) |> ignore

    member this.QrUrlText
        with get () = qrUrlText
        and set value = this.SetProperty(&qrUrlText, value) |> ignore

    member this.QrStatusText
        with get () = qrStatusText
        and set value = this.SetProperty(&qrStatusText, value) |> ignore

    member this.QrStatusBg
        with get () = qrStatusBg
        and set value = this.SetProperty(&qrStatusBg, value) |> ignore

    member this.QrStatusFg
        with get () = qrStatusFg
        and set value = this.SetProperty(&qrStatusFg, value) |> ignore

    member this.HasMultipleIps
        with get () = hasMultipleIps
        and set value = this.SetProperty(&hasMultipleIps, value) |> ignore

    member this.SwitchIpButtonText
        with get () = switchIpButtonText
        and set value = this.SetProperty(&switchIpButtonText, value) |> ignore

    member this.ExportMessage
        with get () = exportMessage
        and set value =
            if this.SetProperty(&exportMessage, value) then
                this.OnPropertyChanged("HasExportMessage")

    member this.HasExportMessage = not (String.IsNullOrWhiteSpace exportMessage)

    member this.ExportMessageColor
        with get () = exportMessageColor
        and set value = this.SetProperty(&exportMessageColor, value) |> ignore

    member _.RefreshCommand: ICommand = refreshCommand
    member _.ToggleFavoriteCommand: ICommand = toggleFavoriteCommand
    member _.AddCustomCommand: ICommand = addCustomCommand
    member _.CancelCustomCommand: ICommand = cancelCustomCommand
    member _.ToggleCustomFormCommand: ICommand = toggleCustomFormCommand
    member _.OpenBrowserCommand: ICommand = openBrowserCommand
    member _.OpenExportModalCommand: ICommand = openExportModalCommand
    member _.CloseExportModalCommand: ICommand = closeExportModalCommand
    member _.SaveMarkdownCommand: ICommand = saveMarkdownCommand
    member _.SaveHtmlCommand: ICommand = saveHtmlCommand
    member _.StartQrTransferCommand: ICommand = startQrTransferCommand
    member _.SwitchQrIpCommand: ICommand = switchQrIpCommand
    member _.OpenInfoDialogCommand: ICommand = openInfoDialogCommand
    member _.CloseInfoDialogCommand: ICommand = closeInfoDialogCommand
    member _.UpdateMasterDataCommand: ICommand = updateMasterDataCommand
    member _.OpenWebsiteCommand: ICommand = openWebsiteCommand
    member _.OpenInstagramCommand: ICommand = openInstagramCommand
    member _.OpenReleasesCommand: ICommand = openReleasesCommand
    member _.OpenLatestVersionCommand: ICommand = openLatestVersionCommand

    member this.IsInfoDialogOpen
        with get () = isInfoDialogOpen
        and set value = this.SetProperty(&isInfoDialogOpen, value) |> ignore

    member this.IsUpdatingMasterData
        with get () = isUpdatingMasterData
        and set value =
            if this.SetProperty(&isUpdatingMasterData, value) then
                this.OnPropertyChanged("CanUpdateMasterData")
                this.OnPropertyChanged("UpdateMasterDataButtonLabel")

    member this.MasterDataUpdateStatus
        with get () = if String.IsNullOrWhiteSpace masterDataUpdateStatus then "" else localize masterDataUpdateStatus
        and set value =
            if this.SetProperty(&masterDataUpdateStatus, value) then
                this.OnPropertyChanged("HasMasterDataUpdateStatus")

    member _.HasMasterDataUpdateStatus = not (String.IsNullOrWhiteSpace masterDataUpdateStatus)

    member private this.NotifyMasterDataProperties() =
        this.NotifyChanged("DefinitionDateText")
        this.NotifyChanged("LatestAppVersionText")
        this.NotifyChanged("IsAppOutdated")
        this.NotifyChanged("HasStoreUpdateLink")
        this.NotifyChanged("AppUpdateAvailableText")

    member private this.OpenExternalUrl(url: string) =
        if isHttpUrl url then
            try
                Process.Start(ProcessStartInfo(url, UseShellExecute = true)) |> ignore
            with ex ->
                this.ErrorMessage <- sprintf "ブラウザを起動できませんでした: %s" ex.Message

    member this.OpenLatestVersionPage() =
        let url =
            match MasterDataRepository.TryGetApp(MasterDataRepository.AppId, masterDataSnapshot) with
            | Some app -> app.StoreUrl |> Option.defaultValue MasterDataRepository.ReleasesUrl
            | None -> MasterDataRepository.ReleasesUrl
        this.OpenExternalUrl(url)

    member this.OpenInfoDialog() =
        this.MasterDataUpdateStatus <- ""
        masterDataSnapshot <- MasterDataRepository.GetSnapshot()
        this.NotifyMasterDataProperties()
        this.IsInfoDialogOpen <- true

    /// <summary>キャッシュ済みサービス・リスナーで診断のみ再評価します。</summary>
    member private this.ReInspectWithCurrentContext() =
        if not isScanning && allServices.Count > 0 then
            let svcSnapshot = allServices |> Seq.toList
            let listenerSnapshot = lastListeners
            let mdSnapshot = masterDataSnapshot
            Task.Run(fun () ->
                try
                    let findings = SecurityAudit.inspectWithContext svcSnapshot (Some listenerSnapshot) (Some mdSnapshot)
                    let scoreOpt = SecurityAudit.score findings
                    Dispatcher.UIThread.Post(Action(fun () ->
                        securityFindings.Clear()
                        findings
                        |> List.iter (fun finding ->
                            finding.LanguageCode <- languageCode
                            securityFindings.Add(finding))
                        match scoreOpt with
                        | Some(score, grade) ->
                            securityScoreText <- sprintf "%d / 100" score
                            securityGrade <- grade
                        | None ->
                            securityScoreText <- "未評価"
                            securityGrade <- "—"
                        this.NotifyChanged("SecurityFindings")
                        this.NotifyChanged("SecurityScoreText")
                        this.NotifyChanged("SecurityGrade")
                        this.NotifyChanged("SecurityGradeLabel")
                        this.NotifyChanged("SecurityGradeLetter")
                        this.NotifyChanged("IsProvisional")
                        this.NotifyChanged("ProvisionalBadgeText")
                        this.NotifyChanged("WarningCountLabel")
                        this.NotifyChanged("SecurityCardColor")
                    ))
                with _ -> ()
            ) |> ignore

    /// <summary>定義データを GitHub から取得してローカル SQLite キャッシュを更新します。</summary>
    member this.UpdateMasterDataAsync(?silent: bool) =
        let isSilent = defaultArg silent false
        async {
            if not isUpdatingMasterData then
                Dispatcher.UIThread.Post(Action(fun () ->
                    this.IsUpdatingMasterData <- true
                    if not isSilent then this.MasterDataUpdateStatus <- ""))
                try
                    let! updated = MasterDataRepository.FetchAndImportAsync()
                    let snapshot = MasterDataRepository.GetSnapshot()
                    Dispatcher.UIThread.Post(Action(fun () ->
                        masterDataSnapshot <- snapshot
                        if not isSilent then
                            this.MasterDataUpdateStatus <-
                                if updated then "定義データを更新しました"
                                else "定義データの取得に失敗しました（キャッシュを使用します）"
                        this.NotifyMasterDataProperties()
                        if updated then this.ReInspectWithCurrentContext()))
                finally
                    Dispatcher.UIThread.Post(Action(fun () -> this.IsUpdatingMasterData <- false))
        }

    /// <summary>サービス検出とセキュリティ簡易診断をバックグラウンドで非同期実行して表示を更新します。</summary>
    member this.Refresh() =
        if not isScanning then
            isScanning <- true
            this.NotifyChanged("IsScanning")
            this.NotifyChanged("CanRefresh")
            this.ErrorMessage <- ""
            this.StatusMessage <- "サービスを検出しています..."
            let customSnapshot = allServices |> Seq.filter (fun service -> service.IsCustom) |> Seq.toList
            let mdSnapshot = masterDataSnapshot
            Task.Run(fun () ->
                let detectedResult =
                    try
                        let detected, listeners, hasPrev, diffs = ServiceDiscovery.detectAndCompareWithListeners ()
                        Ok(detected, listeners, hasPrev, diffs)
                    with ex ->
                        Error ex.Message

                let findingsResult =
                    try
                        let svcsForAudit, listenersOpt =
                            match detectedResult with
                            | Ok(detected, listeners, _, _) ->
                                ([ yield! detected; yield! customSnapshot ] :> ServiceEntry seq), Some listeners
                            | Error _ ->
                                (customSnapshot :> ServiceEntry seq), Some []
                        let findings = SecurityAudit.inspectWithContext svcsForAudit listenersOpt (Some mdSnapshot)
                        let scoreOpt = SecurityAudit.score findings
                        Ok(findings, scoreOpt)
                    with ex ->
                        Error ex.Message

                Dispatcher.UIThread.Post(Action(fun () ->
                    try
                        match detectedResult with
                        | Ok(detected, listeners, hasPrev, diffs) ->
                            lastListeners <- listeners
                            hasPreviousScan <- hasPrev
                            rawDiffMessages <- diffs
                            let favorites = allServices |> Seq.filter (fun service -> service.IsFavorite) |> Seq.map (fun service -> service.Name) |> Set.ofSeq
                            let custom = allServices |> Seq.filter (fun service -> service.IsCustom) |> Seq.toList
                            allServices.Clear()
                            detected
                            |> Seq.iter (fun service ->
                                service.IsFavorite <- Set.contains service.Name favorites
                                service.LanguageCode <- languageCode
                                allServices.Add(service))
                            custom |> List.iter allServices.Add
                            refreshVisible()
                            rebuildScanDiffs()
                        | Error msg ->
                            this.StatusMessage <- "サービスを検出できませんでした"
                            this.ErrorMessage <- msg

                        match findingsResult with
                        | Ok(findings, scoreOpt) ->
                            securityFindings.Clear()
                            findings
                            |> List.iter (fun finding ->
                                finding.LanguageCode <- languageCode
                                securityFindings.Add(finding))
                            match scoreOpt with
                            | Some(score, grade) ->
                                securityScoreText <- sprintf "%d / 100" score
                                securityGrade <- grade
                            | None ->
                                securityScoreText <- "未評価"
                                securityGrade <- "—"
                        | Error msg ->
                            securityFindings.Clear()
                            let finding = SecurityFinding("診断エラー", msg, FindingLevel.Unknown, 0)
                            finding.LanguageCode <- languageCode
                            securityFindings.Add(finding)
                            securityScoreText <- "未評価"
                            securityGrade <- "—"

                        this.NotifyChanged("SecurityFindings")
                        this.NotifyChanged("SecurityScoreText")
                        this.NotifyChanged("SecurityGrade")
                        this.NotifyChanged("SecurityGradeLabel")
                        this.NotifyChanged("SecurityGradeLetter")
                        this.NotifyChanged("IsProvisional")
                        this.NotifyChanged("ProvisionalBadgeText")
                        this.NotifyChanged("WarningCountLabel")
                        this.NotifyChanged("SecurityCardColor")
                    finally
                        isScanning <- false
                        this.NotifyChanged("IsScanning")
                        this.NotifyChanged("CanRefresh")
                ))
            ) |> ignore

    /// <summary>選択中サービスのお気に入り登録状態を切り替えます。</summary>
    member this.ToggleFavorite() =
        if not (isNull selectedService) then
            selectedService.IsFavorite <- not selectedService.IsFavorite
            this.OnPropertyChanged("FavoriteButtonText")
            refreshVisible()

    /// <summary>入力フォームの内容を検証してカスタムサービスを追加します。</summary>
    member this.AddCustomService() =
        let name = customName.Trim()
        let url = customUrl.Trim()
        if String.IsNullOrWhiteSpace name then
            this.ErrorMessage <- "サービス名を入力してください。"
        elif categoryNames |> List.contains customCategory |> not then
            this.ErrorMessage <- "カテゴリを選択してください。"
        elif not (String.IsNullOrWhiteSpace url) && not (isHttpUrl url) then
            this.ErrorMessage <- "URLには http:// または https:// のURLを入力してください。"
        elif allServices |> Seq.exists (fun service -> String.Equals(service.Name, name, StringComparison.OrdinalIgnoreCase)) then
            this.ErrorMessage <- "同じ名前のサービスがすでに登録されています。"
        else
            let ports =
                if String.IsNullOrWhiteSpace url then ""
                else
                    let uri = Uri(url)
                    if uri.IsDefaultPort then "" else string uri.Port
            let displayName = if String.IsNullOrWhiteSpace customDisplayName then name else customDisplayName.Trim()
            let entry = ServiceEntry(name, displayName, customCategory, "不明", ports, "", url, true, false, false)
            entry.LanguageCode <- languageCode
            allServices.Add(entry)
            this.SelectedCategory <- categoryOptions |> Seq.find (fun option -> option.Name = customCategory)
            refreshVisible()
            this.SelectedService <- entry
            this.IsAddingCustom <- false
            this.CustomName <- ""
            this.CustomDisplayName <- ""
            this.CustomUrl <- ""
            this.ErrorMessage <- ""
            this.StatusMessage <- "カスタムサービスを追加しました"

    /// <summary>選択中サービスのURLをOS既定のブラウザーで開きます。</summary>
    member this.OpenSelectedService() =
        if not (isNull selectedService) && isHttpUrl selectedService.Url then
            try
                Process.Start(ProcessStartInfo(selectedService.Url, UseShellExecute = true)) |> ignore
            with ex ->
                this.ErrorMessage <- sprintf "ブラウザを起動できませんでした: %s" ex.Message

    /// <summary>レポート出力モーダルを開きます。</summary>
    member this.OpenExportModal() =
        stopQrSession()
        this.ExportMessage <- ""
        this.IsQrSectionVisible <- false
        this.IsQrActive <- false
        this.IsQrInvalidated <- false
        this.QrImage <- null
        this.IsExportModalOpen <- true

    /// <summary>QR 転送サーバーを停止してレポート出力モーダルを閉じます。</summary>
    member this.CloseExportModal() =
        stopQrSession()
        this.IsQrActive <- false
        this.QrImage <- null
        this.IsExportModalOpen <- false

    /// <summary>Markdown または HTML 形式でレポートファイルを保存します。</summary>
    member this.SaveReportAsync(formatKind: string) =
        task {
            if not (isNull saveFileHandler) then
                let timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss")
                let isHtml = formatKind = "html"
                let defaultName = sprintf "EasyServiceReport_%s.%s" timestamp (if isHtml then "html" else "md")
                let ext = if isHtml then "html" else "md"
                let filterName = localize (if isHtml then "HTML ファイル" else "Markdown ファイル")
                try
                    let! targetPath = saveFileHandler.Invoke(defaultName, ext, filterName)
                    if not (String.IsNullOrWhiteSpace targetPath) then
                        let content =
                            if isHtml then
                                ReportExporter.buildHtmlReport languageCode allServices securityFindings this.SecurityScoreText this.SecurityGradeLabel rawDiffMessages hasPreviousScan
                            else
                                ReportExporter.buildMarkdownReport languageCode allServices securityFindings this.SecurityScoreText this.SecurityGradeLabel rawDiffMessages hasPreviousScan
                        do! File.WriteAllTextAsync(targetPath, content, UTF8Encoding(false))
                        this.ExportMessageColor <- "#1D6B4B"
                        this.ExportMessage <- Localization.format languageCode "保存しました: {0}" [| box targetPath |]
                with ex ->
                    this.ExportMessageColor <- "#B42318"
                    this.ExportMessage <- Localization.format languageCode "保存に失敗しました: {0}" [| box ex.Message |]
        }

    /// <summary>3分間有効・1対1接続・ダウンロード後自動無効化の QR コード転送サーバーを開始します。</summary>
    member this.StartQrTransfer() =
        stopQrSession()
        this.ExportMessage <- ""
        this.IsQrSectionVisible <- true
        this.IsQrInvalidated <- false
        isQrClientConnected <- false

        let timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss")
        let fileName = sprintf "EasyServiceReport_%s.html" timestamp
        let html = ReportExporter.buildHtmlReport languageCode allServices securityFindings this.SecurityScoreText this.SecurityGradeLabel rawDiffMessages hasPreviousScan
        let bytes = Encoding.UTF8.GetBytes(html)
        let server = new QrReportTransferService(bytes, fileName, "text/html; charset=utf-8", languageCode)

        try
            server.Start(5070)
            activeQrServer <- Some server
            selectedIpIndex <- 0
            updateQrDisplay()

            this.QrStatusBg <- "#F3FAF8"
            this.QrStatusFg <- "#00695C"
            this.QrStatusText <- Localization.format languageCode "接続待機中... (残り {0})" [| box "03:00" |]
            this.IsQrActive <- true

            let timerCts = new CancellationTokenSource()
            qrCountdownCts <- Some timerCts
            let startUtc = DateTime.UtcNow
            let timeout = TimeSpan.FromMinutes(3.0)

            server.ClientConnected.Add(fun () ->
                isQrClientConnected <- true
                Dispatcher.UIThread.Post(fun () ->
                    if this.IsQrActive then
                        let rem = timeout - (DateTime.UtcNow - startUtc)
                        let safeRem = if rem < TimeSpan.Zero then TimeSpan.Zero else rem
                        let timeStr = sprintf "%02d:%02d" (int safeRem.TotalMinutes) safeRem.Seconds
                        this.QrStatusText <- Localization.format languageCode "📱 スマホが接続しました（1:1セッション確立・残り {0}）" [| box timeStr |]))

            server.DownloadCompleted.Add(fun () ->
                Dispatcher.UIThread.Post(fun () ->
                    stopQrSession()
                    this.IsQrActive <- false
                    this.IsQrInvalidated <- true
                    this.QrImage <- null
                    this.QrStatusBg <- "#E8F5E9"
                    this.QrStatusFg <- "#1B5E20"
                    this.QrStatusText <- localize "✅ ダウンロード完了！QRコードを無効化しました"))

            server.Expired.Add(fun () ->
                Dispatcher.UIThread.Post(fun () ->
                    stopQrSession()
                    this.IsQrActive <- false
                    this.IsQrInvalidated <- true
                    this.QrImage <- null
                    this.QrStatusBg <- "#FFEBEE"
                    this.QrStatusFg <- "#B42318"
                    this.QrStatusText <- localize "⏱️ 制限時間（3分）を超過したためQRコードを無効化しました"))

            // 1秒ごとの残り時間カウントダウン更新
            Task.Run(fun () ->
                task {
                    use timer = new PeriodicTimer(TimeSpan.FromSeconds(1.0))
                    try
                        while! timer.WaitForNextTickAsync(timerCts.Token) do
                            let rem = timeout - (DateTime.UtcNow - startUtc)
                            let safeRem = if rem < TimeSpan.Zero then TimeSpan.Zero else rem
                            let timeStr = sprintf "%02d:%02d" (int safeRem.TotalMinutes) safeRem.Seconds
                            Dispatcher.UIThread.Post(fun () ->
                                if this.IsQrActive then
                                    let template =
                                        if isQrClientConnected then "📱 スマホが接続しました（1:1セッション確立・残り {0}）"
                                        else "接続待機中... (残り {0})"
                                    this.QrStatusText <- Localization.format languageCode template [| box timeStr |])
                    with :? OperationCanceledException -> ()
                } :> Task) |> ignore
        with ex ->
            (server :> IDisposable).Dispose()
            this.IsQrSectionVisible <- false
            this.IsQrActive <- false
            this.ExportMessageColor <- "#B42318"
            this.ExportMessage <- ex.Message

    /// <summary>複数のローカル IP アダプタ候補がある場合に QR コードの IP アドレスを切り替えます。</summary>
    member this.SwitchQrIp() =
        match activeQrServer with
        | Some srv when srv.IpCandidates.Length > 1 ->
            selectedIpIndex <- (selectedIpIndex + 1) % srv.IpCandidates.Length
            updateQrDisplay()
        | _ -> ()

    /// <summary>ウィンドウ終了時などにバックグラウンドの QR 転送リソースを解放します。</summary>
    member _.Cleanup() =
        stopQrSession()
