module Keroosha.SilencerBot.Database

open System
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
  | [<MapValue("Done")>] Done = 4

[<CLIMutable>]
[<Table("Users")>]
type User = {
  [<Column>] Id: Guid
  [<Column>] TgId: int64
  [<Column>] Name: string
}

[<CLIMutable>]
[<Table("UserJobs")>]
type UserJobs = {
  [<Column>] Id: Guid
  [<Column>] UserId: Guid
  [<Column>] State: JobState
  [<Column(DataType = DataType.BinaryJson)>] Context: string
}

type DbContext(connectionString: string, provider: IDataProvider) =
    inherit DataConnection(provider, connectionString)
    member this.Users = this.GetTable<User>();
    member this.UserJobs = this.GetTable<UserJobs>();

let migrateApp (connectionString: string) =
  use serviceProvider =
    ServiceCollection()
      .AddFluentMigratorCore()
      .ConfigureRunner(fun x -> 
        x.AddPostgres()
          .WithMigrationsIn(typeof<DbContext>.Assembly)
          .WithGlobalConnectionString(connectionString) |> ignore
      )
      .BuildServiceProvider(false)
  use scope = serviceProvider.CreateScope()
  serviceProvider.GetRequiredService<IMigrationRunner>().MigrateUp()

let createContext (connectionString: string) =
  new DbContext(connectionString, PostgreSQLTools.GetDataProvider())

[<TimestampedMigration(2023us, 4us, 6us, 20us, 8us)>]
type InitialMigration() =
  inherit AutoReversingMigration()
  override this.Up() =
    this.Create.Table("Users")
      .WithColumn("Id").AsGuid().PrimaryKey()
      .WithColumn("TgId").AsInt64().NotNullable()
      .WithColumn("Name").AsString().NotNullable()
    |> ignore
    this.Create.Table("UserJobs")
      .WithColumn("Id").AsGuid().PrimaryKey()
      .WithColumn("UserId").AsGuid().ForeignKey("Users", "Id")
      .WithColumn("State").AsString().NotNullable().WithDefaultValue("New")
      .WithColumn("Context").AsCustom("JSONB").NotNullable()
    |> ignore
    ()
  