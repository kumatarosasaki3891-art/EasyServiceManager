namespace EasyServiceManager.Models

open EasyServiceManager.Services
open CommunityToolkit.Mvvm.ComponentModel

/// <summary>セキュリティ簡易診断項目の深刻度レベル。</summary>
type FindingLevel =
    | Good
    | Warning
    | Unknown
    | Information

/// <summary>セキュリティ診断の個別結果項目を表すモデル。</summary>
type SecurityFinding(title: string, detail: string, level: FindingLevel, deduction: int) =
    inherit ObservableObject()

    let mutable languageCode = "ja-JP"

    /// <summary>翻訳済み項目タイトル。</summary>
    member _.Title = Localization.translate languageCode title
    /// <summary>翻訳済み詳細説明。</summary>
    member _.Detail = Localization.translateText languageCode detail
    /// <summary>深刻度レベル。</summary>
    member _.Level = level
    /// <summary>スコア減点値。</summary>
    member _.Deduction = deduction
    /// <summary>レベルに応じたアイコン文字。</summary>
    member _.Icon =
        match level with
        | Good -> "✅"
        | Warning -> "⚠"
        | Unknown -> "⚪"
        | Information -> "ℹ"
    /// <summary>レベルに応じた枠線カラーコード。</summary>
    member _.Color =
        match level with
        | Good -> "#22805D"
        | Warning -> "#B54708"
        | Unknown -> "#667788"
        | Information -> "#316B9B"

    /// <summary>表示言語コード。変更時にタイトルと詳細の再通知を行います。</summary>
    member this.LanguageCode
        with get () = languageCode
        and set value =
            if this.SetProperty(&languageCode, value) then
                this.OnPropertyChanged("Title")
                this.OnPropertyChanged("Detail")
