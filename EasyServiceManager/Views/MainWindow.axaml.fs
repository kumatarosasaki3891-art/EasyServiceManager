namespace EasyServiceManager.Views

open System
open System.Threading.Tasks
open Avalonia
open Avalonia.Controls
open Avalonia.Input
open Avalonia.Markup.Xaml
open Avalonia.Platform.Storage
open EasyServiceManager.ViewModels

/// <summary>アプリケーションのメインウィンドウおよびファイル保存ダイアログ連携を担当するコードビハインド。</summary>
type MainWindow () as this = 
    inherit Window ()

    do this.InitializeComponent()

    /// <summary>XAML を読み込み、ファイル保存ハンドラーと終了時のクリーンアップを登録します。</summary>
    member private this.InitializeComponent() =
        AvaloniaXamlLoader.Load(this)
        this.DataContextChanged.Add(fun _ ->
            match this.DataContext with
            | :? MainViewModel as vm ->
                vm.SaveFileHandler <- Func<string, string, string, Task<string>>(fun defaultName ext filterName ->
                    this.PickSaveFileAsync(defaultName, ext, filterName))
            | _ -> ())
        this.KeyDown.Add(fun e ->
            if e.Key = Key.Escape then
                match this.DataContext with
                | :? MainViewModel as vm ->
                    if vm.IsDiagnosticsModalOpen then vm.IsDiagnosticsModalOpen <- false
                    elif vm.IsInfoDialogOpen then vm.IsInfoDialogOpen <- false
                    elif vm.IsExportModalOpen then vm.CloseExportModal()
                | _ -> ())
        this.Closing.Add(fun _ ->
            match this.DataContext with
            | :? MainViewModel as vm -> vm.Cleanup()
            | _ -> ())

    /// <summary>Avalonia の StorageProvider を使用して保存先ファイルパスを選択します。</summary>
    member private this.PickSaveFileAsync(defaultName: string, extension: string, filterName: string) : Task<string> =
        task {
            let topLevel = TopLevel.GetTopLevel(this)
            if isNull topLevel || isNull topLevel.StorageProvider then
                return null
            else
                let options = FilePickerSaveOptions(SuggestedFileName = defaultName, DefaultExtension = extension)
                let fileType = FilePickerFileType(filterName, Patterns = [| sprintf "*.%s" extension |])
                options.FileTypeChoices <- [| fileType |]
                let! file = topLevel.StorageProvider.SaveFilePickerAsync(options)
                if isNull file then return null
                else return file.Path.LocalPath
        }
