module Keroosha.SilencerBot.Telegram

open Funogram.Telegram.Bot
open Funogram.Telegram.Types
open Funogram.Types
open Keroosha.SilencerBot.Database

type VoiceRemoveArgs = {
  fileId: string
  chatId: int64
}

type Features =
  | VoiceRemove of VoiceRemoveArgs
  | Unknown

let isVoiceRemoveAction(update: Update) =
  update.Message.IsSome && update.Message.Value.Audio.IsSome

let resolveUpdate (ctx: UpdateContext) =
  match ctx.Update with
  | x when isVoiceRemoveAction x ->
    VoiceRemove { fileId =  x.Message.Value.Audio.Value.FileId; chatId = x.Message.Value.Chat.Id }
  | _ -> Unknown

let createBotInbox (cfg: BotConfig, db: unit -> DbContext) = MailboxProcessor.Start(fun (inbox) ->
  let rec loop () =
    async {
      match! inbox.Receive() with
        | VoiceRemove x -> ()
        | Unknown ->  ()
      
      return! loop ()
    }
  loop ()
  )