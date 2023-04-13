module Keroosha.SilencerBot.Database

open System
open System.Transactions
open FluentMigrator
open FluentMigrator.Runner;
open LinqToDB
open LinqToDB.Data
open LinqToDB.DataProvider
open LinqToDB.DataProvider.PostgreSQL
open LinqToDB.Mapping
open Microsoft.Extensions.DependencyInjection

type JobState =
  | [<MapValue("Failed")>] Failed = -1
  | [<MapValue("New")>] New = 0
  | [<MapValue("Downloading")>] Downloading = 1
  | [<MapValue("Executing")>] Executing = 2
  | [<MapValue("UploadingResults")>] UploadingResults = 3
  | [<MapValue("CleanUp")>] CleanUp = 4
  | [<MapValue("Done")>] Done = 5
  
type JsonJobContext = {
  fileId: String
  chatId: int64
  savePath: String
  stdout: String
  stderr: String
}


[<CLIMutable>]
[<Table("Users")>]
[<NoComparison; NoEquality>]
type User = {
[<PrimaryKey>] Id: Guid
[<Column>] TgId : int64
[<Column>] ChatId : int64
[<Column>] Name : string 
}


[<CLIMutable>]
[<Table("UserJobs")>]
[<NoComparison; NoEquality>]
type UserJob = {
  [<PrimaryKey>] Id: Guid
  [<Column>] UserId: Guid
  [<Column>] State: JobState
  [<Column>] WorkerId: Guid Nullable
  [<Column(DataType = DataType.BinaryJson)>] Context: String
}


type DbContext(connectionString: String, provider: IDataProvider) =
    inherit DataConnection(provider, connectionString)
    member this.Users = this.GetTable<User>();
    member this.UserJobs = this.GetTable<UserJob>();

let migrateApp (connectionString: String) =
  use serviceProvider =
    ServiceCollection()
      .AddFluentMigratorCore()
      .AddLogging(fun x -> x.AddFluentMigratorConsole() |> ignore)
      .ConfigureRunner(fun x -> 
        x.AddPostgres()
          .WithMigrationsIn(typeof<DbContext>.Assembly)
          .WithGlobalConnectionString(connectionString) |> ignore
      )
      .BuildServiceProvider(false)
  use scope = serviceProvider.CreateScope()
  scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp()

let createContext (connectionString: string) =
  new DbContext(connectionString, PostgreSQLTools.GetDataProvider())

[<Migration(1L, "")>]
type InitialMigration() =
  inherit AutoReversingMigration()
  override this.Up() =
    this.Create.Table("Users")
      .WithColumn("Id").AsGuid().PrimaryKey()
      .WithColumn("TgId").AsInt64().NotNullable()
      .WithColumn("ChatId").AsInt64().NotNullable()
      .WithColumn("Name").AsString().NotNullable()
    |> ignore
    this.Create.Table("UserJobs")
      .WithColumn("Id").AsGuid().PrimaryKey()
      .WithColumn("UserId").AsGuid().ForeignKey("Users", "Id")
      .WithColumn("State").AsString().NotNullable().WithDefaultValue("New")
      .WithColumn("WorkerId").AsGuid().Nullable()
      .WithColumn("Context").AsCustom("JSONB").NotNullable()
    |> ignore
    ()
  