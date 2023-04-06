// For more information see https://aka.ms/fsharp-console-apps
open System
open Funogram.Telegram
open Funogram.Telegram.Bot
open Funogram.Tools
open Keroosha.SilencerBot

type Config = {
  connectionString: string
  saveDir: string
}

let config = Env.createConfig "SILENCER_BOT_CONFIG_PATH"

let handleUpdate (ctx: UpdateContext) =
  ()

Console.CancelKeyPress |> Event.add (fun _ -> Environment.Exit <| 0)

async {
  let config = Config.defaultConfig |> Config.withReadTokenFromFile
  let! _ = Api.deleteWebhookBase () |> Api.makeRequestAsync config
  return! startBot config handleUpdate None
} |> Async.RunSynchronously

