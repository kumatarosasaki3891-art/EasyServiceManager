namespace EasyServiceManager.Controls

open Avalonia.Controls
open Avalonia.Markup.Xaml

/// <summary>総合スコア・グレード・警告件数を表示する共用カードコントロール。</summary>
type SecurityScoreCard() as this =
    inherit UserControl()

    do this.InitializeComponent()

    member private this.InitializeComponent() =
        AvaloniaXamlLoader.Load(this)
