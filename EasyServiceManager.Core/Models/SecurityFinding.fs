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

    /// <summary>未翻訳の元タイトル（判定ロジック用）。</summary>
    member _.RawTitle = title
    /// <summary>未翻訳の元詳細（判定ロジック用）。</summary>
    member _.RawDetail = detail
    /// <summary>翻訳済み項目タイトル。</summary>
    member _.Title = Localization.translateText languageCode title
    /// <summary>翻訳済み詳細説明。</summary>
    member this.Detail = Localization.translateText languageCode detail
    /// <summary>パネル要約表示向けの先頭1行抜粋。</summary>
    member this.SummaryDetail =
        let translated = this.Detail
        let idx = translated.IndexOf('\n')
        if idx >= 0 then translated.Substring(0, idx).TrimEnd('\r') else translated
    /// <summary>深刻度レベル。</summary>
    member _.Level = level
    /// <summary>スコア減点値。</summary>
    member _.Deduction = deduction
    /// <summary>減点があるかどうか。</summary>
    member _.HasDeduction = deduction > 0
    /// <summary>翻訳済み減点表示テキスト。</summary>
    member _.DeductionLabel =
        if deduction > 0 then Localization.format languageCode "-{0}点" [| box deduction |] else ""
    /// <summary>減点バッジの背景色。</summary>
    member _.DeductionBadgeBg = if deduction >= 25 then "#FEE2E2" else "#FEF3C7"
    /// <summary>減点バッジの文字色。</summary>
    member _.DeductionBadgeFg = if deduction >= 25 then "#B42318" else "#B54708"
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
                this.OnPropertyChanged("SummaryDetail")
                this.OnPropertyChanged("DeductionLabel")
