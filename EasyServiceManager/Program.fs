namespace EasyServiceManager

open System
open Avalonia

/// <summary>アプリケーションの起動と Avalonia ビルダー構成を行うモジュール。</summary>
module Program =

    /// <summary>Avalonia アプリケーションのビルダーを構成します。</summary>
    [<CompiledName "BuildAvaloniaApp">] 
    let buildAvaloniaApp () = 
        AppBuilder
            .Configure<App>()
            .WithInterFont()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .LogToTrace(areas = Array.empty)

    /// <summary>アプリケーションのメインエントリーポイント。</summary>
    [<EntryPoint; STAThread>]
    let main argv =
        buildAvaloniaApp().StartWithClassicDesktopLifetime(argv)
