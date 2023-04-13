module Keroosha.SilencerBot.Env
open System
open System.Diagnostics
open System.IO
open System.Text
open System.Text.Json
open McMaster.Extensions.CommandLineUtils
open Serilog

[<RequireQualifiedAccess>]
module Logging =
    let logger =
        let config = LoggerConfiguration()
        config.WriteTo.Console().CreateLogger()

type public BotConfig = {
  tempSavePath: string
  connectionString: string
  processingWorkerId: Guid
  processorWorkingPath: String
  useGPU: bool
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

// http://fssnip.net/sw/1
// Modified to be non-blocking (async) as fck!
let runProc filename args startDir =
    async {
        let timer = Stopwatch.StartNew()
        let procStartInfo = 
            ProcessStartInfo(
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                FileName = filename,
                Arguments = ArgumentEscaper.EscapeAndConcatenate args
            )
            
        match startDir with | Some d -> procStartInfo.WorkingDirectory <- d | _ -> ()

        let outputs = System.Collections.Generic.List<string>()
        let errors = System.Collections.Generic.List<string>()
        let outputHandler f (_sender:obj) (args:DataReceivedEventArgs) = f args.Data
        let p = new Process(StartInfo = procStartInfo)
        p.OutputDataReceived.AddHandler(DataReceivedEventHandler (outputHandler outputs.Add))
        p.ErrorDataReceived.AddHandler(DataReceivedEventHandler (outputHandler errors.Add))
        let started = 
            try
                p.Start()
            with | ex ->
                ex.Data.Add("filename", filename)
                reraise()
        if not started then failwithf "Failed to start process %s" filename
        Logging.logger.Information $"Started {p.ProcessName} with pid {p.Id}"
        p.BeginOutputReadLine()
        p.BeginErrorReadLine()
        do! p.WaitForExitAsync() |> Async.AwaitTask
        timer.Stop()
        Logging.logger.Information $"Finished {filename} after {timer.ElapsedMilliseconds} milliseconds"
        let joinA (x: String array) = String.Join ("\n", x)
        let cleanOut l = l
                         |> Seq.filter (fun o -> String.IsNullOrEmpty o |> not)
                         |> Seq.toArray
                         |> joinA
        
        return (cleanOut outputs, cleanOut errors)
    }