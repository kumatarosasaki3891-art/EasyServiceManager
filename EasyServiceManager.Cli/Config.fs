namespace EasyServiceManager.Cli

open System
open System.IO
open System.Text.Json

/// <summary>カスタムサービスの登録設定。</summary>
[<AllowNullLiteral>]
type CustomServiceConfig() =
    member val Name = "" with get, set
    member val DisplayName = "" with get, set
    member val Category = "Other" with get, set
    member val Ports = "" with get, set
    member val Url = "" with get, set
    member val Note = "" with get, set

/// <summary>CLI設定（~/.config/easy-service-diagnosis/config.json）。</summary>
[<AllowNullLiteral>]
type CliConfig() =
    member val CustomServices: CustomServiceConfig list = [] with get, set
    member val AdditionalRiskyPorts: int list = [] with get, set
    member val Exclusions: string list = [] with get, set

module Config =
    /// <summary>XDG準拠の設定ファイルパスを取得します。</summary>
    let getConfigPath () =
        let baseDir =
            match Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") with
            | value when not (String.IsNullOrWhiteSpace value) -> value
            | _ ->
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".config"
                )
        Path.Combine(baseDir, "easy-service-diagnosis", "config.json")

    /// <summary>設定ファイルを読み込みます（存在しない場合はデフォルト値を返します）。</summary>
    let loadConfig () : CliConfig =
        try
            let path = getConfigPath ()
            if File.Exists(path) then
                let json = File.ReadAllText(path)
                let options = JsonSerializerOptions(PropertyNameCaseInsensitive = true)
                let config = JsonSerializer.Deserialize<CliConfig>(json, options)
                if isNull config then CliConfig() else config
            else
                CliConfig()
        with _ ->
            CliConfig()
