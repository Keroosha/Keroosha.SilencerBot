module Keroosha.SilencerBot.Env
open System
open System.IO
open System.Text.Json
open Serilog

[<RequireQualifiedAccess>]
module Logging =
    let logger =
        let config = LoggerConfiguration()
        config.WriteTo.Console().CreateLogger()

type public BotConfig = {
  tempSavePath: string
  connectionString: string
}

let private readConfig = File.ReadAllText >> JsonSerializer.Deserialize<BotConfig>

let public createConfig (name: string) =
    match Environment.GetEnvironmentVariable(name) with
    | null ->
          Logging.logger.Error("Missing env")
          ApplicationException("Missing config path env") |> raise
    | path ->
          Logging.logger.Information("Read config from env")
          readConfig path