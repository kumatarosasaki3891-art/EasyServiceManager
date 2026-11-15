namespace EasyServiceManager

open System
open Avalonia.Controls
open Avalonia.Controls.Templates
open EasyServiceManager.ViewModels

/// <summary>ViewModel の型名から対応する View を解決して生成するロケーター。</summary>
type ViewLocator() =
    interface IDataTemplate with
        
        /// <summary>ViewModel インスタンスに対応する View コントロールを生成します。</summary>
        member this.Build(data) =
            if isNull data then
                null
            else    
                let name = data.GetType().FullName.Replace("ViewModel", "View", StringComparison.Ordinal)
                let typ = Type.GetType(name)
                if isNull typ then
                    upcast TextBlock(Text = sprintf "Not Found: %s" name)
                else
                    downcast Activator.CreateInstance(typ)
                
        /// <summary>対象データが ViewModelBase を継承しているか判定します。</summary>
        member this.Match(data) = data :? ViewModelBase
