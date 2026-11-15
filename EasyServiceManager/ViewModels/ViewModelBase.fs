namespace EasyServiceManager.ViewModels

open CommunityToolkit.Mvvm.ComponentModel

/// <summary>すべての ViewModel が継承する基底クラス。</summary>
[<AbstractClass>]
type ViewModelBase() =
    inherit ObservableObject()
