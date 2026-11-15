namespace EasyServiceManager

open Avalonia
open Avalonia.Controls.ApplicationLifetimes
open Avalonia.Data.Core
open Avalonia.Data.Core.Plugins
open Avalonia.Markup.Xaml
open EasyServiceManager.ViewModels
open EasyServiceManager.Views

/// <summary>Avalonia アプリケーションのエントリクラス。</summary>
type App() =
    inherit Application()

    /// <summary>XAML リソースとスタイルを初期化します。</summary>
    override this.Initialize() =
            AvaloniaXamlLoader.Load(this)

    /// <summary>フレームワーク初期化完了時にメインウィンドウを生成して設定します。</summary>
    override this.OnFrameworkInitializationCompleted() =
        match this.ApplicationLifetime with
        | :? IClassicDesktopStyleApplicationLifetime as desktop ->
             desktop.MainWindow <- MainWindow(DataContext = MainViewModel())
        | _ -> ()

        base.OnFrameworkInitializationCompleted()
