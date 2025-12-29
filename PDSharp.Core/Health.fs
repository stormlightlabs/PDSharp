namespace PDSharp.Core

open System
open System.IO
open System.Runtime.InteropServices

/// Health monitoring module for guardrails and uptime checks
module Health =

  /// Health status response
  type HealthStatus = {
    /// Version of the PDS
    Version : string
    /// Uptime in seconds
    UptimeSeconds : int64
    /// Server start time in ISO8601
    StartTime : string
    /// Database status
    DatabaseStatus : DatabaseStatus
    /// Disk usage information
    DiskUsage : DiskUsage option
    /// Backup status
    BackupStatus : BackupStatus option
  }

  /// Database connectivity status
  and DatabaseStatus = {
    /// Whether the database is reachable
    IsHealthy : bool
    /// Optional error message
    Message : string option
  }

  /// Disk usage metrics
  and DiskUsage = {
    /// Total disk space in bytes
    TotalBytes : int64
    /// Free disk space in bytes
    FreeBytes : int64
    /// Used disk space in bytes
    UsedBytes : int64
    /// Percentage of disk used
    UsedPercent : float
    /// Whether disk pressure is critical (>90%)
    IsCritical : bool
  }

  /// Backup status tracking
  and BackupStatus = {
    /// Timestamp of last successful backup
    LastBackupTime : DateTimeOffset option
    /// Age of last backup in hours
    BackupAgeHours : float option
    /// Whether backup is too old (>24 hours)
    IsStale : bool
  }

  /// Get disk usage for a given path
  let getDiskUsage (path : string) : DiskUsage option =
    try
      let driveInfo =
        if RuntimeInformation.IsOSPlatform OSPlatform.Windows then
          let driveLetter = Path.GetPathRoot path
          DriveInfo driveLetter
        else
          DriveInfo(if Directory.Exists path then path else "/")

      if driveInfo.IsReady then
        let total = driveInfo.TotalSize
        let free = driveInfo.TotalFreeSpace
        let used = total - free
        let usedPercent = float used / float total * 100.0

        Some {
          TotalBytes = total
          FreeBytes = free
          UsedBytes = used
          UsedPercent = Math.Round(usedPercent, 2)
          IsCritical = usedPercent >= 90.0
        }
      else
        None
    with _ ->
      None



  /// Check if a SQLite database file is accessible
  let checkDatabaseHealth (connectionString : string) : DatabaseStatus =
    try
      let dbPath =
        connectionString.Split ';'
        |> Array.tryFind (fun s -> s.Trim().StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase))
        |> Option.map (fun s -> s.Split('=').[1].Trim())

      match dbPath with
      | Some path when File.Exists path -> { IsHealthy = true; Message = None }
      | Some path -> {
          IsHealthy = false
          Message = Some $"Database file not found: {path}"
        }
      | None -> {
          IsHealthy = false
          Message = Some "Could not parse connection string"
        }
    with ex -> { IsHealthy = false; Message = Some ex.Message }

  /// Calculate backup status from last backup time
  let getBackupStatus (lastBackupTime : DateTimeOffset option) : BackupStatus =
    match lastBackupTime with
    | Some time ->
      let age = DateTimeOffset.UtcNow - time
      let ageHours = age.TotalHours

      {
        LastBackupTime = Some time
        BackupAgeHours = Some(Math.Round(ageHours, 2))
        IsStale = ageHours > 24.0
      }
    | None -> {
        LastBackupTime = None
        BackupAgeHours = None
        IsStale = true
      }

  /// Mutable state for tracking server state
  type HealthState() =
    let mutable startTime = DateTimeOffset.UtcNow
    let mutable lastBackupTime : DateTimeOffset option = None

    member _.StartTime = startTime
    member _.SetStartTime(time : DateTimeOffset) = startTime <- time
    member _.LastBackupTime = lastBackupTime

    member _.RecordBackup() =
      lastBackupTime <- Some DateTimeOffset.UtcNow

    member _.RecordBackup(time : DateTimeOffset) = lastBackupTime <- Some time

    member _.GetUptime() : int64 =
      int64 (DateTimeOffset.UtcNow - startTime).TotalSeconds

  /// Build a complete health status
  let buildHealthStatus
    (version : string)
    (healthState : HealthState)
    (connectionString : string)
    (dataPath : string)
    : HealthStatus =
    {
      Version = version
      UptimeSeconds = healthState.GetUptime()
      StartTime = healthState.StartTime.ToString("o")
      DatabaseStatus = checkDatabaseHealth connectionString
      DiskUsage = getDiskUsage dataPath
      BackupStatus = Some(getBackupStatus healthState.LastBackupTime)
    }
