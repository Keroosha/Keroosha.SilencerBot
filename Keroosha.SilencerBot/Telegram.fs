module Keroosha.SilencerBot.Telegram

open System
open System.IO
open System.Text.Json
open Funogram.Telegram
open Funogram.Telegram.Bot
open Funogram.Telegram.Types
open Keroosha.SilencerBot.Env
open LinqToDB
open Keroosha.SilencerBot.Database
open Microsoft.FSharp.Control
module TgClient = Funogram.Tools.Api

type VoiceRemoveArgs = {
  fileId: string
  filename: string
  chatId: int64
  fileSize: uint64
}

type StartArgs = {
  id: int64
  chatId: int64
  name: String
}

type Features =
  | VoiceRemove of VoiceRemoveArgs
  | Start of StartArgs
  | Skip

let runDate = DateTime.UtcNow

let greetingText = "Привет, отправь мне вавку и я попробую убрать из нее голос"
let jobCreatedText = "Запустил задачу, обрабатываю"
let unknownUserText = "Прости, но мы с тобой не знакомы, отправь мне в ЛС /start"
let fileTooLargeText = "Файл слишком большой, я умею работать с файлами до 40мб"

let isVoiceRemoveAction(update: Update) =
  update.Message.IsSome &&
  update.Message.Value.Audio.IsSome &&
  update.Message.Value.From.IsSome &&
  update.Message.Value.Audio.Value.FileName.IsSome

let isStartCommand (update: Update) =
  update.Message.IsSome &&
  update.Message.Value.Text.IsSome &&
  update.Message.Value.From.IsSome &&
  update.Message.Value.Text.Value.StartsWith "/start"

let resolveUpdate (ctx: UpdateContext) =
  match ctx.Update with
  | x when x.Message.IsSome && x.Message.Value.Date < runDate -> Skip
  | x when isVoiceRemoveAction x ->
    VoiceRemove {
      fileId = x.Message.Value.Audio.Value.FileId
      chatId = x.Message.Value.From.Value.Id
      filename = x.Message.Value.Audio.Value.FileName.Value
      fileSize = x.Message.Value.Audio.Value.FileSize.Value |> uint64
    }
  | x when isStartCommand x ->
    Start {
      id = x.Message.Value.From.Value.Id
      chatId = x.Message.Value.Chat.Id
      name = x.Message.Value.From.Value.FirstName
    }
  | _ -> Skip

let createBotInbox (cfg: BotConfig, botCfg: Funogram.Types.BotConfig, dbFactory: unit -> DbContext) = MailboxProcessor.Start(fun (inbox) ->
  let rec loop () =
    async {
      try 
        match! inbox.Receive() with
          | VoiceRemove x ->
            let db = dbFactory()
            use! trx = db.BeginTransactionAsync() |> Async.AwaitTask
            let! user = db.Users.FirstOrDefaultAsync(fun u -> u.TgId = x.chatId) |> Async.AwaitTask
            match box user with
            | null ->
              do! TgClient.makeRequestAsync botCfg <| Api.sendMessage x.chatId unknownUserText |> Async.Ignore
              ()
            | _ ->
              match Math.Floor(double(x.fileSize) / double(Math.Pow(1024, 2))) < double(40) with
              | true -> 
                let jobContext: JsonJobContext = {
                  stderr = ""
                  stdout = ""
                  chatId = x.chatId 
                  fileId = x.fileId
                  savePath = Path.Combine(cfg.tempSavePath, x.filename) 
                }
                let job: UserJob = { Id = Guid.NewGuid()
                                     State = JobState.New
                                     UserId = user.Id
                                     Context = JsonSerializer.Serialize(jobContext)
                                     WorkerId = Nullable()
                                     }
                do! db.InsertAsync(job) |> Async.AwaitTask |> Async.Ignore
                do! trx.CommitAsync() |> Async.AwaitTask
                do! TgClient.makeRequestAsync botCfg <| Api.sendMessage x.chatId jobCreatedText |> Async.Ignore
              | false ->
                do! TgClient.makeRequestAsync botCfg <| Api.sendMessage x.chatId fileTooLargeText |> Async.Ignore
              ()
          | Start x ->
            let db = dbFactory()
            use! trx = Async.AwaitTask <| db.BeginTransactionAsync() 
            match! db.Users.AnyAsync(fun u -> u.TgId = x.id) |> Async.AwaitTask with
            | true -> ()
            | false ->
              let user: User = { Id = Guid.NewGuid(); Name = x.name; TgId = x.id; ChatId = x.chatId }
              do! db.InsertAsync(user) |> Async.AwaitTask |> Async.Ignore
              do! trx.CommitAsync() |> Async.AwaitTask
              do! TgClient.makeRequestAsync botCfg <| Api.sendMessage x.chatId greetingText |> Async.Ignore
          | Skip -> ()
      with
        | ex -> Logging.logger.Error $"\n{ex.Message}\n{ex.StackTrace}"
      return! loop ()
    }
  loop ()
  )