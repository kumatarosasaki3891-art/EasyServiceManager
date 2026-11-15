namespace EasyServiceManager.Models

open CommunityToolkit.Mvvm.ComponentModel
open EasyServiceManager.Services

/// <summary>検出されたサービスおよびカスタムサービスの表示モデル。</summary>
[<AllowNullLiteral>]
type ServiceEntry(name: string, displayName: string, category: string, status: string, ports: string, executable: string, url: string, isCustom: bool, isFavorite: bool, isSharedProcess: bool) =
    inherit ObservableObject()

    let mutable favorite = isFavorite
    let mutable languageCode = "ja-JP"
    let mutable isDiff = false
    let mutable diffNote = ""

    /// <summary>サービス識別名。</summary>
    member _.Name = name
    /// <summary>サービスの表示名。</summary>
    member _.DisplayName = Localization.translateText languageCode displayName
    /// <summary>内部カテゴリ名。</summary>
    member _.Category = category
    /// <summary>生の状態文字列。</summary>
    member _.Status = status
    /// <summary>待ち受けポート一覧文字列。</summary>
    member _.Ports = ports
    /// <summary>実行ファイルパス。</summary>
    member _.Executable = executable
    /// <summary>アクセス用URL。</summary>
    member _.Url = url
    /// <summary>ユーザー登録のカスタムサービスかどうか。</summary>
    member _.IsCustom = isCustom
    /// <summary>svchost.exe 等の共有ホストプロセス上で稼働しているかどうか。</summary>
    member _.IsSharedProcess = isSharedProcess
    /// <summary>共有ホストプロセスかつ待ち受けポートを持つかどうか。</summary>
    member _.HasSharedPorts = isSharedProcess && not (System.String.IsNullOrWhiteSpace ports)
    /// <summary>翻訳済みカテゴリ名。</summary>
    member _.CategoryLabel = Localization.translate languageCode category
    /// <summary>翻訳済み状態ラベル。</summary>
    member _.StatusLabel =
        let label =
            if status.StartsWith("起動中", System.StringComparison.Ordinal) then Localization.translate languageCode "起動中"
            elif status.StartsWith("停止中", System.StringComparison.Ordinal) then Localization.translate languageCode "停止中"
            else Localization.translate languageCode "不明"
        if status.Contains("(失敗)", System.StringComparison.Ordinal) then label + " (" + Localization.translate languageCode "失敗" + ")"
        else label
    /// <summary>状態に応じたアイコン絵文字。</summary>
    member _.StatusIcon =
        if status.StartsWith("起動中", System.StringComparison.Ordinal) then "🟢"
        elif status.StartsWith("停止中", System.StringComparison.Ordinal) then "🔴"
        else "⚪"
    /// <summary>詳細ペイン向けのポート表示文字列。</summary>
    member _.PortLabel = if System.String.IsNullOrWhiteSpace ports then "" else "localhost:" + ports
    /// <summary>一覧ペイン向けのポート表示文字列（svchost.exe 共有ポートは折りたたみ表記）。</summary>
    member _.ListPortLabel =
        if System.String.IsNullOrWhiteSpace ports then ""
        elif isSharedProcess then Localization.translate languageCode "システム共有プロセス"
        else "localhost:" + ports
    /// <summary>svchost.exe 共有ポートである旨の注記文字列。</summary>
    member _.SharedProcessNote = Localization.translate languageCode "※ svchost.exe 共有ポート（同一プロセス内の他サービスと共有）"
    /// <summary>実行ファイルの表示文字列。</summary>
    member _.ExecutableLabel = if System.String.IsNullOrWhiteSpace executable then Localization.translate languageCode "取得できません" else executable
    /// <summary>URLの表示文字列。</summary>
    member _.UrlLabel = if System.String.IsNullOrWhiteSpace url then Localization.translate languageCode "未設定" else url
    /// <summary>一覧行のサブタイトル（表示名）を表示するかどうか。</summary>
    member _.ShowSubtitle = isCustom || isDiff

    /// <summary>前回スキャンからの差分が検出されたかどうか。</summary>
    member this.IsDiff
        with get () = isDiff
        and set value =
            if this.SetProperty(&isDiff, value) then
                this.OnPropertyChanged("RowBackground")
                this.OnPropertyChanged("RowBorderBrush")
                this.OnPropertyChanged("AccentBarColor")
                this.OnPropertyChanged("DiffNoteLabel")
                this.OnPropertyChanged("ShowSubtitle")

    /// <summary>差分内容の生文字列。</summary>
    member this.DiffNote
        with get () = diffNote
        and set value =
            if this.SetProperty(&diffNote, value) then
                this.OnPropertyChanged("DiffNoteLabel")

    /// <summary>翻訳済み・アイコン付きの差分注記文字列。</summary>
    member _.DiffNoteLabel =
        if System.String.IsNullOrWhiteSpace diffNote then ""
        else "⚠ " + Localization.translateText languageCode diffNote

    /// <summary>一覧行の背景色（差分ありは薄い黄色）。</summary>
    member _.RowBackground = if isDiff then "#FEFCE8" else "White"
    /// <summary>一覧行の枠線色（差分ありは黄色）。</summary>
    member _.RowBorderBrush = if isDiff then "#FACC15" else "#E1E7EC"
    /// <summary>一覧行左端のアクセントバー色。</summary>
    member _.AccentBarColor = if isDiff then "#EAB308" else "#38A778"

    /// <summary>表示言語コード。変更時に関連プロパティの変更通知を発行します。</summary>
    member this.LanguageCode
        with get () = languageCode
        and set value =
            if this.SetProperty(&languageCode, value) then
                this.OnPropertyChanged("DisplayName")
                this.OnPropertyChanged("CategoryLabel")
                this.OnPropertyChanged("StatusLabel")
                this.OnPropertyChanged("ListPortLabel")
                this.OnPropertyChanged("SharedProcessNote")
                this.OnPropertyChanged("ExecutableLabel")
                this.OnPropertyChanged("UrlLabel")
                this.OnPropertyChanged("DiffNoteLabel")

    /// <summary>お気に入り登録状態。</summary>
    member this.IsFavorite
        with get () = favorite
        and set value = this.SetProperty(&favorite, value) |> ignore
