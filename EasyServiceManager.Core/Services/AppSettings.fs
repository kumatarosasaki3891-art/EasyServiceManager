namespace EasyServiceManager.Services

open System
open System.IO
open System.Text.Json

/// <summary>永続化されるユーザー設定データ。</summary>
[<AllowNullLiteral>]
type UserSettings() =
    member val Language = "ja-JP" with get, set

/// <summary>ユーザー設定ファイルの読み書きを行うモジュール。</summary>
module AppSettings =
    /// <summary>OS標準の設定保存先ファイルパスを返します。</summary>
    let private settingsPath () =
        let directory =
            if OperatingSystem.IsWindows() then
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EasyServiceManager")
            else
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "EasyServiceManager")
        Path.Combine(directory, "settings.json")

    /// <summary>保存されている言語設定を読み込みます。</summary>
    let loadLanguage () =
        let path = settingsPath ()
        if not (File.Exists(path)) then "ja-JP"
        else
            try
                let settings = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(path))
                if isNull settings then "ja-JP" else Localization.normalizeCode settings.Language
            with
            | :? JsonException
            | :? IOException
            | :? UnauthorizedAccessException -> "ja-JP"

    /// <summary>選択された言語設定をJSONファイルへ保存します。</summary>
    let saveLanguage languageCode =
        try
            let path = settingsPath ()
            Directory.CreateDirectory(Path.GetDirectoryName(path)) |> ignore
            let settings = UserSettings()
            settings.Language <- Localization.normalizeCode languageCode
            File.WriteAllText(path, JsonSerializer.Serialize(settings, JsonSerializerOptions(WriteIndented = true)))
            Ok()
        with ex ->
            Error ex.Message
