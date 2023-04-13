module Keroosha.SilencerBot.Processing

open System
open System.IO
open System.Net.Http
open System.Text.Json
open Funogram.Telegram
open Funogram.Telegram.Types
open Keroosha.SilencerBot.Database
open Keroosha.SilencerBot.Env
open LinqToDB
open Microsoft.FSharp.Control

module TgClient = Funogram.Tools.Api

let http = new HttpClient()

let inline private (>>=) a b = (a, b) |> async.Bind
let getContext (x: UserJob) = x.Context |> JsonSerializer.Deserialize<JsonJobContext>
let serializeContext (x: JsonJobContext) = x |> JsonSerializer.Serialize<JsonJobContext>
let downloadUrl token path = $"https://api.telegram.org/file/bot{token}/{path}"
let packAudio name stream =
  {
    InputMediaAudio.Media = InputFile.File(name, stream)
    Thumb = None
    Caption = None
    ParseMode = None
    CaptionEntities = None
    Duration = None
    Performer = None
    Title = Some name
    Type = "Audio"
  } |> InputMedia.Audio

let failJob (x: UserJob, ctx: JsonJobContext) (errMessage: String) =
  { x with
     State = JobState.Failed
     Context = JsonSerializer.Serialize({ ctx with stderr = errMessage })
  }

let downloadFile (url: String, filePath: String) =
  task {
    try 
      use file = File.OpenWrite(filePath)
      use! request = http.GetStreamAsync(url)
      do! file |> request.CopyToAsync
      return Ok ()
    with
    | ex -> return Error ex.Message
  } |> Async.AwaitTask

let private findJob (dbBuilder: unit -> DbContext, config: BotConfig) =
  task {
    use db = dbBuilder()
    use! __ = db.BeginTransactionAsync()
    let! jobInProgress = db.UserJobs.FirstOrDefaultAsync(fun x -> x.WorkerId = config.processingWorkerId)
    match box jobInProgress with
    | null ->
      let! job = db.UserJobs.FirstOrDefaultAsync(fun x -> x.State <> JobState.Failed && x.State <> JobState.Done)
      match box job with
      | null -> return None
      | _ ->
        let jobWithWorkerId = { job with WorkerId = config.processingWorkerId }
        let! __ = db.InsertOrReplaceAsync(jobWithWorkerId)
        return Some jobWithWorkerId
    | _ -> return Some jobInProgress
  } |> Async.AwaitTask

let private updateJobState (dbBuilder: unit -> DbContext) (job: UserJob) =
  task {      
    use db = dbBuilder()
    use! __ = db.BeginTransactionAsync()
    let! __ = db.InsertOrReplaceAsync job 
    return job
  } |> Async.AwaitTask

let processNew (job: UserJob, botConfig: Funogram.Types.BotConfig, config: BotConfig) =
  async {
    Logging.logger.Information $"Accepted {job.Id} job"
    return { job with State = JobState.Downloading }
  }

let processDownload (job: UserJob, botConfig: Funogram.Types.BotConfig, config: BotConfig) =
  async {
    Logging.logger.Information $"Downloading {job.Id} job"
    let ctx = getContext job
    let! res = TgClient.makeRequestAsync botConfig <| Api.getFile ctx.fileId
    match res with
    | Ok x when x.FilePath.IsNone ->
      return (job, ctx) |> failJob <| "file doesnt exist"
    | Ok x ->
      let url = downloadUrl botConfig.Token x.FilePath.Value
      match! downloadFile (url, ctx.savePath) with
      | Ok _ -> return { job with State = JobState.Executing }
      | Error text -> return (job, ctx) |> failJob <| text
    | Error x ->
      return (job, ctx) |> failJob <| x.Description
  }
  
let processExecuting (job: UserJob, botConfig: Funogram.Types.BotConfig, config: BotConfig) =
  async {
    Logging.logger.Information $"Processing {job.Id} job"
    let ctx = getContext job
    // let gpuFlag = if config.useGPU then "--gpu 0" else null
    let args = ["inference.py"; "--input"; ctx.savePath; "--output_dir"; config.tempSavePath]
    let! stdout, stderr = runProc $"/usr/bin/python" args (Some config.processorWorkingPath)
    let ctxWithOutput = { ctx with stdout = stdout; stderr = stderr }
    return { job with
               State = JobState.UploadingResults
               Context = serializeContext ctxWithOutput
            }
  }

let processUploading (job: UserJob, botConfig: Funogram.Types.BotConfig, config: BotConfig) =
  async {
    let ctx = getContext job
    let cleanName = Path.GetFileNameWithoutExtension ctx.savePath
    let withoutVocalsPath = Path.Combine(Path.GetDirectoryName ctx.savePath, $"{cleanName}_Instrumental.wav")
    use f = File.OpenRead withoutVocalsPath
    Logging.logger.Information $"Uploading results for {job.Id} job"
    
    let media = InputFile.File (Path.GetFileName withoutVocalsPath, f) 
    let! res = TgClient.makeRequestAsync botConfig <| Api.sendAudio (ctx.chatId) (media) (0) 
    return { job with State = JobState.Done }
  }
  

let rec processJob (dbBuilder: unit -> DbContext, botConfig: Funogram.Types.BotConfig, config: BotConfig) (job: UserJob) =
  let updateAndContinue x = x |> updateJobState(dbBuilder) >>= processJob(dbBuilder, botConfig, config)
  let args = (job, botConfig, config)
  async {
    match job.State with
    | JobState.New -> do! processNew args >>= updateAndContinue
    | JobState.Downloading -> do! processDownload args >>= updateAndContinue
    | JobState.Executing -> do! processExecuting args >>= updateAndContinue
    | JobState.UploadingResults -> do! processUploading args >>= updateAndContinue
    | JobState.Done -> Logging.logger.Information $"Job {job.Id} done"
    | JobState.Failed -> Logging.logger.Error $"Job {job.Id} failed"
    ()
  }

let rec processingMain (dbBuilder: unit -> DbContext, appConfig: BotConfig, tgConfig: Funogram.Types.BotConfig) =
  async {
    try 
      match! findJob(dbBuilder, appConfig) with
      | Some x -> do! (dbBuilder, tgConfig, appConfig) |> processJob <| x
      | None -> ()
      do! 150 |> Async.Sleep
      do! (dbBuilder, appConfig, tgConfig) |> processingMain
    with
    | ex -> Logging.logger.Error $"{ex.Message}\n{ex.StackTrace}"
  }
