module PDSharp.Tests.Health

open System
open Xunit
open PDSharp.Core.Health

[<Fact>]
let ``getDiskUsage returns disk info for valid path`` () =
  let result = getDiskUsage "."

  match result with
  | Some usage ->
    Assert.True(usage.TotalBytes > 0L)
    Assert.True(usage.FreeBytes >= 0L)
    Assert.True(usage.UsedBytes >= 0L)
    Assert.True(usage.UsedPercent >= 0.0 && usage.UsedPercent <= 100.0)
  | None -> Assert.True(true)

[<Fact>]
let ``getDiskUsage UsedPercent is calculated correctly`` () =
  let result = getDiskUsage "."

  match result with
  | Some usage ->
    let expectedUsed = usage.TotalBytes - usage.FreeBytes
    Assert.Equal(expectedUsed, usage.UsedBytes)
    let expectedPercent = float usage.UsedBytes / float usage.TotalBytes * 100.0
    Assert.True(abs (usage.UsedPercent - expectedPercent) < 0.1)
  | None -> Assert.True(true)

[<Fact>]
let ``getDiskUsage IsCritical is true when usage > 90 percent`` () =
  let result = getDiskUsage "."

  match result with
  | Some usage -> Assert.Equal(usage.UsedPercent >= 90.0, usage.IsCritical)
  | None -> Assert.True(true)

[<Fact>]
let ``checkDatabaseHealth returns healthy for existing file`` () =
  let tempPath = System.IO.Path.GetTempFileName()

  try
    let connStr = $"Data Source={tempPath}"
    let result = checkDatabaseHealth connStr
    Assert.True result.IsHealthy
    Assert.True result.Message.IsNone
  finally
    System.IO.File.Delete tempPath

[<Fact>]
let ``checkDatabaseHealth returns unhealthy for missing file`` () =
  let connStr = "Data Source=/nonexistent/path/to/database.db"
  let result = checkDatabaseHealth connStr
  Assert.False result.IsHealthy
  Assert.True result.Message.IsSome

[<Fact>]
let ``checkDatabaseHealth handles invalid connection string`` () =
  let connStr = "invalid"
  let result = checkDatabaseHealth connStr
  Assert.False result.IsHealthy
  Assert.True result.Message.IsSome

[<Fact>]
let ``getBackupStatus returns stale when no backup`` () =
  let result = getBackupStatus None
  Assert.True result.IsStale
  Assert.True result.LastBackupTime.IsNone
  Assert.True result.BackupAgeHours.IsNone

[<Fact>]
let ``getBackupStatus returns not stale for recent backup`` () =
  let recentTime = DateTimeOffset.UtcNow.AddHours(-1.0)
  let result = getBackupStatus (Some recentTime)
  Assert.False result.IsStale
  Assert.True result.LastBackupTime.IsSome
  Assert.True result.BackupAgeHours.IsSome
  Assert.True(result.BackupAgeHours.Value < 24.0)

[<Fact>]
let ``getBackupStatus returns stale for old backup`` () =
  let oldTime = DateTimeOffset.UtcNow.AddHours(-25.0)
  let result = getBackupStatus (Some oldTime)
  Assert.True result.IsStale
  Assert.True(result.BackupAgeHours.Value > 24.0)

[<Fact>]
let ``HealthState tracks uptime correctly`` () =
  let state = HealthState()
  state.SetStartTime(DateTimeOffset.UtcNow.AddSeconds(-10.0))
  let uptime = state.GetUptime()
  Assert.True(uptime >= 9L && uptime <= 12L)

[<Fact>]
let ``HealthState records backup time`` () =
  let state = HealthState()
  Assert.True state.LastBackupTime.IsNone
  state.RecordBackup()
  Assert.True state.LastBackupTime.IsSome

[<Fact>]
let ``buildHealthStatus constructs complete status`` () =
  let state = HealthState()
  let tempPath = System.IO.Path.GetTempFileName()

  try
    let connStr = $"Data Source={tempPath}"
    let status = buildHealthStatus "1.0.0" state connStr "."

    Assert.Equal("1.0.0", status.Version)
    Assert.True(status.UptimeSeconds >= 0L)
    Assert.True status.DatabaseStatus.IsHealthy
    Assert.True status.BackupStatus.IsSome
  finally
    System.IO.File.Delete tempPath
