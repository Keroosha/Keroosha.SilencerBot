// For more information see https://aka.ms/fsharp-console-apps
open System
open Funogram
open Funogram.Telegram
open Funogram.Telegram.Bot
open Funogram.Tools
open Keroosha.SilencerBot
open Keroosha.SilencerBot.Telegram

let config = Env.createConfig "SILENCER_BOT_CONFIG_PATH"
let botConfig = Config.defaultConfig |> Config.withReadTokenFromFile

let ctxFactory = fun () -> Database.createContext <| config.connectionString

Database.migrateApp config.connectionString

let botInbox = createBotInbox <| (botConfig, ctxFactory)
let handleUpdate  (ctx: UpdateContext) = resolveUpdate ctx |> botInbox.Post

Console.CancelKeyPress |> Event.add (fun _ -> Environment.Exit <| 0)

async {
  let! _ = Api.makeRequestAsync botConfig <| Api.deleteWebhookBase()
  return! startBot botConfig handleUpdate None
} |> Async.RunSynchronously

