namespace EasyServiceManager.Controls

open Avalonia.Controls
open Avalonia.Markup.Xaml

/// <summary>セキュリティ診断所見リストの共用コントロール（要約モード／全文モード切替対応）。</summary>
type SecurityFindingsList() as this =
    inherit UserControl()

    let mutable isCompact = false
    let mutable rootGrid: Grid = null

    do this.InitializeComponent()

    /// <summary>要約表示モード（true: 右パネル用コンパクト、false: モーダル用全文表示）。</summary>
    member _.IsCompact
        with get () = isCompact
        and set (value: bool) =
            isCompact <- value
            if not (isNull rootGrid) then
                rootGrid.Classes.Set("compact", value)

    member private this.InitializeComponent() =
        AvaloniaXamlLoader.Load(this)
        rootGrid <- this.FindControl<Grid>("RootGrid")
        if not (isNull rootGrid) then
            rootGrid.Classes.Set("compact", isCompact)
