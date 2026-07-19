IF DB_ID(N'FileServerMonitor') IS NULL
BEGIN
    CREATE DATABASE FileServerMonitor;
END;
GO

USE FileServerMonitor;
GO

IF OBJECT_ID(N'dbo.FileAuditEvents', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.FileAuditEvents
    (
        Id UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_FileAuditEvents PRIMARY KEY,
        TimestampUtc DATETIME2(3) NOT NULL,
        ServerName NVARCHAR(128) NOT NULL,
        ShareName NVARCHAR(256) NOT NULL,
        FullPath NVARCHAR(2048) NOT NULL,
        PreviousPath NVARCHAR(2048) NULL,
        ObjectType NVARCHAR(32) NOT NULL,
        ActionName NVARCHAR(64) NOT NULL,
        UserName NVARCHAR(256) NOT NULL,
        Sid NVARCHAR(256) NULL,
        SourceHost NVARCHAR(256) NULL,
        SourceIp NVARCHAR(64) NULL,
        ProcessName NVARCHAR(256) NULL,
        FileSizeBytes BIGINT NULL,
        Extension NVARCHAR(64) NULL,
        ResultName NVARCHAR(64) NOT NULL,
        Severity NVARCHAR(32) NOT NULL,
        SourceName NVARCHAR(128) NOT NULL,
        AgentId NVARCHAR(128) NULL,
        SourceEventId CHAR(64) NULL,
        CursorType NVARCHAR(32) NULL,
        SourceRecordId BIGINT NULL,
        SourceUsn BIGINT NULL,
        SourceVolume NVARCHAR(32) NULL,
        FileReferenceId NVARCHAR(128) NULL,
        IngestedUtc DATETIME2(3) NOT NULL CONSTRAINT DF_FileAuditEvents_IngestedUtc DEFAULT SYSUTCDATETIME()
    );
END;
GO

IF COL_LENGTH(N'dbo.FileAuditEvents', N'AgentId') IS NULL ALTER TABLE dbo.FileAuditEvents ADD AgentId NVARCHAR(128) NULL;
IF COL_LENGTH(N'dbo.FileAuditEvents', N'SourceEventId') IS NULL ALTER TABLE dbo.FileAuditEvents ADD SourceEventId CHAR(64) NULL;
IF COL_LENGTH(N'dbo.FileAuditEvents', N'CursorType') IS NULL ALTER TABLE dbo.FileAuditEvents ADD CursorType NVARCHAR(32) NULL;
IF COL_LENGTH(N'dbo.FileAuditEvents', N'SourceRecordId') IS NULL ALTER TABLE dbo.FileAuditEvents ADD SourceRecordId BIGINT NULL;
IF COL_LENGTH(N'dbo.FileAuditEvents', N'SourceUsn') IS NULL ALTER TABLE dbo.FileAuditEvents ADD SourceUsn BIGINT NULL;
IF COL_LENGTH(N'dbo.FileAuditEvents', N'SourceVolume') IS NULL ALTER TABLE dbo.FileAuditEvents ADD SourceVolume NVARCHAR(32) NULL;
IF COL_LENGTH(N'dbo.FileAuditEvents', N'FileReferenceId') IS NULL ALTER TABLE dbo.FileAuditEvents ADD FileReferenceId NVARCHAR(128) NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_FileAuditEvents_Agent_SourceEvent' AND object_id = OBJECT_ID(N'dbo.FileAuditEvents'))
BEGIN
    CREATE UNIQUE INDEX UX_FileAuditEvents_Agent_SourceEvent
        ON dbo.FileAuditEvents (AgentId, SourceEventId)
        WHERE AgentId IS NOT NULL AND SourceEventId IS NOT NULL;
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_FileAuditEvents_TimestampUtc' AND object_id = OBJECT_ID(N'dbo.FileAuditEvents'))
BEGIN
    CREATE INDEX IX_FileAuditEvents_TimestampUtc ON dbo.FileAuditEvents (TimestampUtc DESC);
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_FileAuditEvents_Server_Action_Time' AND object_id = OBJECT_ID(N'dbo.FileAuditEvents'))
BEGIN
    CREATE INDEX IX_FileAuditEvents_Server_Action_Time ON dbo.FileAuditEvents (ServerName, ActionName, TimestampUtc DESC);
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_FileAuditEvents_User_Time' AND object_id = OBJECT_ID(N'dbo.FileAuditEvents'))
BEGIN
    CREATE INDEX IX_FileAuditEvents_User_Time ON dbo.FileAuditEvents (UserName, TimestampUtc DESC);
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_FileAuditEvents_Share_Time' AND object_id = OBJECT_ID(N'dbo.FileAuditEvents'))
BEGIN
    CREATE INDEX IX_FileAuditEvents_Share_Time ON dbo.FileAuditEvents (ShareName, TimestampUtc DESC);
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_FileAuditEvents_Path_Time' AND object_id = OBJECT_ID(N'dbo.FileAuditEvents'))
BEGIN
    CREATE INDEX IX_FileAuditEvents_Path_Time ON dbo.FileAuditEvents (FullPath, TimestampUtc DESC);
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_FileAuditEvents_Timeline_Recent' AND object_id = OBJECT_ID(N'dbo.FileAuditEvents'))
BEGIN
    CREATE INDEX IX_FileAuditEvents_Timeline_Recent
    ON dbo.FileAuditEvents (TimestampUtc DESC)
    INCLUDE
    (
        ServerName,
        ShareName,
        FullPath,
        PreviousPath,
        ObjectType,
        ActionName,
        UserName,
        Sid,
        SourceHost,
        SourceIp,
        ProcessName,
        FileSizeBytes,
        Extension,
        ResultName,
        Severity,
        SourceName
    );
END;
GO

IF OBJECT_ID(N'dbo.FileAuditTimelineEvents', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.FileAuditTimelineEvents
    (
        Id UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_FileAuditTimelineEvents PRIMARY KEY,
        TimestampUtc DATETIME2(3) NOT NULL,
        ServerName NVARCHAR(128) NOT NULL,
        ShareName NVARCHAR(256) NOT NULL,
        FullPath NVARCHAR(2048) NOT NULL,
        PreviousPath NVARCHAR(2048) NULL,
        ObjectType NVARCHAR(32) NOT NULL,
        ActionName NVARCHAR(64) NOT NULL,
        UserName NVARCHAR(256) NOT NULL,
        Sid NVARCHAR(256) NULL,
        SourceHost NVARCHAR(256) NULL,
        SourceIp NVARCHAR(64) NULL,
        ProcessName NVARCHAR(256) NULL,
        FileSizeBytes BIGINT NULL,
        Extension NVARCHAR(64) NULL,
        ResultName NVARCHAR(64) NOT NULL,
        Severity NVARCHAR(32) NOT NULL,
        SourceName NVARCHAR(128) NOT NULL,
        DisplayAction NVARCHAR(128) NOT NULL,
        DisplayTarget NVARCHAR(512) NOT NULL,
        CorrelationVersion NVARCHAR(64) NOT NULL,
        CorrelatedUtc DATETIME2(3) NOT NULL CONSTRAINT DF_FileAuditTimelineEvents_CorrelatedUtc DEFAULT SYSUTCDATETIME()
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_FileAuditTimelineEvents_TimestampUtc' AND object_id = OBJECT_ID(N'dbo.FileAuditTimelineEvents'))
BEGIN
    CREATE INDEX IX_FileAuditTimelineEvents_TimestampUtc ON dbo.FileAuditTimelineEvents (TimestampUtc DESC);
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_FileAuditTimelineEvents_User_Action_Time' AND object_id = OBJECT_ID(N'dbo.FileAuditTimelineEvents'))
BEGIN
    CREATE INDEX IX_FileAuditTimelineEvents_User_Action_Time ON dbo.FileAuditTimelineEvents (UserName, ActionName, TimestampUtc DESC);
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_FileAuditTimelineEvents_Server_Share_Time' AND object_id = OBJECT_ID(N'dbo.FileAuditTimelineEvents'))
BEGIN
    CREATE INDEX IX_FileAuditTimelineEvents_Server_Share_Time ON dbo.FileAuditTimelineEvents (ServerName, ShareName, TimestampUtc DESC);
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_FileAuditTimelineEvents_Path_Time' AND object_id = OBJECT_ID(N'dbo.FileAuditTimelineEvents'))
BEGIN
    CREATE INDEX IX_FileAuditTimelineEvents_Path_Time ON dbo.FileAuditTimelineEvents (FullPath, TimestampUtc DESC);
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_FileAuditTimelineEvents_Action_Time' AND object_id = OBJECT_ID(N'dbo.FileAuditTimelineEvents'))
BEGIN
    CREATE INDEX IX_FileAuditTimelineEvents_Action_Time
        ON dbo.FileAuditTimelineEvents (ActionName, TimestampUtc DESC)
        INCLUDE (ServerName, ShareName, UserName, DisplayAction, DisplayTarget);
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_FileAuditTimelineEvents_DisplayAction_Time' AND object_id = OBJECT_ID(N'dbo.FileAuditTimelineEvents'))
BEGIN
    CREATE INDEX IX_FileAuditTimelineEvents_DisplayAction_Time
        ON dbo.FileAuditTimelineEvents (DisplayAction, TimestampUtc DESC)
        INCLUDE (ServerName, ShareName, UserName, DisplayTarget);
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_FileAuditTimelineEvents_User_Time' AND object_id = OBJECT_ID(N'dbo.FileAuditTimelineEvents'))
BEGIN
    CREATE INDEX IX_FileAuditTimelineEvents_User_Time
        ON dbo.FileAuditTimelineEvents (UserName, TimestampUtc DESC)
        INCLUDE (ServerName, ShareName, ActionName, DisplayAction, DisplayTarget);
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_FileAuditTimelineEvents_Extension_Time' AND object_id = OBJECT_ID(N'dbo.FileAuditTimelineEvents'))
BEGIN
    CREATE INDEX IX_FileAuditTimelineEvents_Extension_Time
        ON dbo.FileAuditTimelineEvents (Extension, TimestampUtc DESC)
        INCLUDE (ServerName, ShareName, UserName, ActionName, DisplayAction, DisplayTarget);
END;
GO

IF OBJECT_ID(N'dbo.TimelineMaterializationJobs', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.TimelineMaterializationJobs
    (
        Id UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_TimelineMaterializationJobs PRIMARY KEY,
        FromUtc DATETIME2(3) NOT NULL,
        ToUtc DATETIME2(3) NOT NULL,
        StatusName NVARCHAR(16) NOT NULL,
        CreatedUtc DATETIME2(3) NOT NULL,
        AvailableUtc DATETIME2(3) NOT NULL,
        LeaseId UNIQUEIDENTIFIER NULL,
        LeaseExpiresUtc DATETIME2(3) NULL,
        AttemptCount INT NOT NULL,
        LastError NVARCHAR(1024) NULL,
        LastErrorUtc DATETIME2(3) NULL
    );
END;
GO

IF COL_LENGTH(N'dbo.TimelineMaterializationJobs', N'LastErrorUtc') IS NULL
BEGIN
    ALTER TABLE dbo.TimelineMaterializationJobs
    ADD LastErrorUtc DATETIME2(3) NULL;
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_TimelineMaterializationJobs_Claim' AND object_id = OBJECT_ID(N'dbo.TimelineMaterializationJobs'))
BEGIN
    CREATE INDEX IX_TimelineMaterializationJobs_Claim
        ON dbo.TimelineMaterializationJobs (StatusName, AvailableUtc, FromUtc)
        INCLUDE (ToUtc, LeaseExpiresUtc);
END;
GO

IF OBJECT_ID(N'dbo.AgentHeartbeats', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.AgentHeartbeats
    (
        AgentId NVARCHAR(128) NOT NULL CONSTRAINT PK_AgentHeartbeats PRIMARY KEY,
        ServerName NVARCHAR(128) NOT NULL,
        StatusName NVARCHAR(64) NOT NULL,
        LastHeartbeatUtc DATETIME2(3) NOT NULL,
        VersionName NVARCHAR(64) NULL,
        LastRecordId BIGINT NOT NULL CONSTRAINT DF_AgentHeartbeats_LastRecordId DEFAULT 0,
        LastUsnByVolumeJson NVARCHAR(MAX) NULL,
        PendingQueueEvents INT NOT NULL CONSTRAINT DF_AgentHeartbeats_PendingQueueEvents DEFAULT 0,
        LastSuccessfulSendUtc DATETIME2(3) NULL,
        LastCollectedEventUtc DATETIME2(3) NULL,
        LastCycleStartedUtc DATETIME2(3) NULL,
        LastCycleFinishedUtc DATETIME2(3) NULL,
        LastCycleDurationMs BIGINT NULL,
        LastCycleSecurityEventsRead INT NULL,
        LastCycleUsnEventsRead INT NULL,
        LastCycleCorrelatedEvents INT NULL,
        LastCycleSentEvents INT NULL,
        LastCycleQueuedEvents INT NULL,
        LastCycleError NVARCHAR(1024) NULL,
        Message NVARCHAR(1024) NULL
    );
END;
GO

IF COL_LENGTH(N'dbo.AgentHeartbeats', N'LastRecordId') IS NULL
BEGIN
    ALTER TABLE dbo.AgentHeartbeats
    ADD LastRecordId BIGINT NOT NULL CONSTRAINT DF_AgentHeartbeats_LastRecordId DEFAULT 0;
END;
GO

IF COL_LENGTH(N'dbo.AgentHeartbeats', N'LastUsnByVolumeJson') IS NULL
BEGIN
    ALTER TABLE dbo.AgentHeartbeats
    ADD LastUsnByVolumeJson NVARCHAR(MAX) NULL;
END;
GO

IF COL_LENGTH(N'dbo.AgentHeartbeats', N'PendingQueueEvents') IS NULL
BEGIN
    ALTER TABLE dbo.AgentHeartbeats
    ADD PendingQueueEvents INT NOT NULL CONSTRAINT DF_AgentHeartbeats_PendingQueueEvents DEFAULT 0;
END;
GO

IF COL_LENGTH(N'dbo.AgentHeartbeats', N'LastSuccessfulSendUtc') IS NULL
BEGIN
    ALTER TABLE dbo.AgentHeartbeats
    ADD LastSuccessfulSendUtc DATETIME2(3) NULL;
END;
GO

IF COL_LENGTH(N'dbo.AgentHeartbeats', N'LastCollectedEventUtc') IS NULL
BEGIN
    ALTER TABLE dbo.AgentHeartbeats
    ADD LastCollectedEventUtc DATETIME2(3) NULL;
END;
GO

IF COL_LENGTH(N'dbo.AgentHeartbeats', N'LastCycleStartedUtc') IS NULL
BEGIN
    ALTER TABLE dbo.AgentHeartbeats
    ADD LastCycleStartedUtc DATETIME2(3) NULL;
END;
GO

IF COL_LENGTH(N'dbo.AgentHeartbeats', N'LastCycleFinishedUtc') IS NULL
BEGIN
    ALTER TABLE dbo.AgentHeartbeats
    ADD LastCycleFinishedUtc DATETIME2(3) NULL;
END;
GO

IF COL_LENGTH(N'dbo.AgentHeartbeats', N'LastCycleDurationMs') IS NULL
BEGIN
    ALTER TABLE dbo.AgentHeartbeats
    ADD LastCycleDurationMs BIGINT NULL;
END;
GO

IF COL_LENGTH(N'dbo.AgentHeartbeats', N'LastCycleSecurityEventsRead') IS NULL
BEGIN
    ALTER TABLE dbo.AgentHeartbeats
    ADD LastCycleSecurityEventsRead INT NULL;
END;
GO

IF COL_LENGTH(N'dbo.AgentHeartbeats', N'LastCycleUsnEventsRead') IS NULL
BEGIN
    ALTER TABLE dbo.AgentHeartbeats
    ADD LastCycleUsnEventsRead INT NULL;
END;
GO

IF COL_LENGTH(N'dbo.AgentHeartbeats', N'LastCycleCorrelatedEvents') IS NULL
BEGIN
    ALTER TABLE dbo.AgentHeartbeats
    ADD LastCycleCorrelatedEvents INT NULL;
END;
GO

IF COL_LENGTH(N'dbo.AgentHeartbeats', N'LastCycleSentEvents') IS NULL
BEGIN
    ALTER TABLE dbo.AgentHeartbeats
    ADD LastCycleSentEvents INT NULL;
END;
GO

IF COL_LENGTH(N'dbo.AgentHeartbeats', N'LastCycleQueuedEvents') IS NULL
BEGIN
    ALTER TABLE dbo.AgentHeartbeats
    ADD LastCycleQueuedEvents INT NULL;
END;
GO

IF COL_LENGTH(N'dbo.AgentHeartbeats', N'LastCycleError') IS NULL
BEGIN
    ALTER TABLE dbo.AgentHeartbeats
    ADD LastCycleError NVARCHAR(1024) NULL;
END;
GO

IF OBJECT_ID(N'dbo.FileServerAlerts', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.FileServerAlerts
    (
        Id UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_FileServerAlerts PRIMARY KEY,
        RuleName NVARCHAR(128) NOT NULL,
        Severity NVARCHAR(32) NOT NULL,
        StatusName NVARCHAR(32) NOT NULL,
        Title NVARCHAR(256) NOT NULL,
        Description NVARCHAR(1024) NOT NULL,
        ServerName NVARCHAR(128) NOT NULL,
        UserName NVARCHAR(256) NOT NULL,
        EventCount INT NOT NULL,
        FirstEventUtc DATETIME2(3) NOT NULL,
        LastEventUtc DATETIME2(3) NOT NULL,
        CreatedUtc DATETIME2(3) NOT NULL,
        AcknowledgedUtc DATETIME2(3) NULL,
        SamplePathsJson NVARCHAR(MAX) NULL,
        DedupKey NVARCHAR(512) NOT NULL
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_FileServerAlerts_Status_Severity_Created' AND object_id = OBJECT_ID(N'dbo.FileServerAlerts'))
BEGIN
    CREATE INDEX IX_FileServerAlerts_Status_Severity_Created ON dbo.FileServerAlerts (StatusName, Severity, CreatedUtc DESC);
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_FileServerAlerts_DedupKey_Created' AND object_id = OBJECT_ID(N'dbo.FileServerAlerts'))
BEGIN
    CREATE INDEX IX_FileServerAlerts_DedupKey_Created ON dbo.FileServerAlerts (DedupKey, CreatedUtc DESC);
END;
GO

IF OBJECT_ID(N'dbo.AlertRules', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.AlertRules
    (
        RuleName NVARCHAR(128) NOT NULL CONSTRAINT PK_AlertRules PRIMARY KEY,
        Title NVARCHAR(256) NOT NULL,
        Description NVARCHAR(1024) NOT NULL,
        IsEnabled BIT NOT NULL,
        Severity NVARCHAR(32) NOT NULL,
        ThresholdValue INT NULL,
        SecondaryThresholdValue INT NULL,
        SecondarySeverity NVARCHAR(32) NULL,
        ServerFilter NVARCHAR(128) NULL,
        ShareFilter NVARCHAR(256) NULL,
        PathFilter NVARCHAR(2048) NULL,
        ActiveFromHour INT NULL,
        ActiveToHour INT NULL,
        ActiveDays NVARCHAR(128) NULL,
        ExcludedUsers NVARCHAR(1024) NULL,
        ExcludedHosts NVARCHAR(1024) NULL,
        ExcludedProcesses NVARCHAR(1024) NULL,
        TimeZoneId NVARCHAR(128) NULL,
        UpdatedUtc DATETIME2(3) NOT NULL
    );
END;
GO

IF COL_LENGTH(N'dbo.AlertRules', N'ServerFilter') IS NULL
BEGIN
    ALTER TABLE dbo.AlertRules
    ADD ServerFilter NVARCHAR(128) NULL;
END;
GO

IF COL_LENGTH(N'dbo.AlertRules', N'ShareFilter') IS NULL
BEGIN
    ALTER TABLE dbo.AlertRules
    ADD ShareFilter NVARCHAR(256) NULL;
END;
GO

IF COL_LENGTH(N'dbo.AlertRules', N'PathFilter') IS NULL
BEGIN
    ALTER TABLE dbo.AlertRules
    ADD PathFilter NVARCHAR(2048) NULL;
END;
GO

IF COL_LENGTH(N'dbo.AlertRules', N'ActiveFromHour') IS NULL
BEGIN
    ALTER TABLE dbo.AlertRules
    ADD ActiveFromHour INT NULL;
END;
GO

IF COL_LENGTH(N'dbo.AlertRules', N'ActiveToHour') IS NULL
BEGIN
    ALTER TABLE dbo.AlertRules
    ADD ActiveToHour INT NULL;
END;
GO

IF COL_LENGTH(N'dbo.AlertRules', N'ActiveDays') IS NULL
BEGIN
    ALTER TABLE dbo.AlertRules
    ADD ActiveDays NVARCHAR(128) NULL;
END;
GO

IF COL_LENGTH(N'dbo.AlertRules', N'ExcludedUsers') IS NULL
BEGIN
    ALTER TABLE dbo.AlertRules
    ADD ExcludedUsers NVARCHAR(1024) NULL;
END;
GO

IF COL_LENGTH(N'dbo.AlertRules', N'ExcludedHosts') IS NULL
BEGIN
    ALTER TABLE dbo.AlertRules
    ADD ExcludedHosts NVARCHAR(1024) NULL;
END;
GO

IF COL_LENGTH(N'dbo.AlertRules', N'ExcludedProcesses') IS NULL
BEGIN
    ALTER TABLE dbo.AlertRules
    ADD ExcludedProcesses NVARCHAR(1024) NULL;
END;
GO

IF COL_LENGTH(N'dbo.AlertRules', N'TimeZoneId') IS NULL
BEGIN
    ALTER TABLE dbo.AlertRules
    ADD TimeZoneId NVARCHAR(128) NULL;
END;
GO

IF OBJECT_ID(N'dbo.MonitoredPaths', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.MonitoredPaths
    (
        Id UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_MonitoredPaths PRIMARY KEY,
        ServerName NVARCHAR(128) NOT NULL,
        ShareName NVARCHAR(256) NOT NULL,
        RootPath NVARCHAR(2048) NOT NULL,
        StatusName NVARCHAR(32) NOT NULL,
        PriorityName NVARCHAR(32) NOT NULL,
        OwnerName NVARCHAR(256) NULL,
        Notes NVARCHAR(1024) NULL,
        CreatedUtc DATETIME2(3) NOT NULL,
        UpdatedUtc DATETIME2(3) NOT NULL
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_MonitoredPaths_Server_Share_Path' AND object_id = OBJECT_ID(N'dbo.MonitoredPaths'))
BEGIN
    CREATE UNIQUE INDEX IX_MonitoredPaths_Server_Share_Path ON dbo.MonitoredPaths (ServerName, ShareName, RootPath);
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_MonitoredPaths_Status_Priority' AND object_id = OBJECT_ID(N'dbo.MonitoredPaths'))
BEGIN
    CREATE INDEX IX_MonitoredPaths_Status_Priority ON dbo.MonitoredPaths (StatusName, PriorityName);
END;
GO

IF OBJECT_ID(N'dbo.AdminAuditLog', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.AdminAuditLog
    (
        Id UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_AdminAuditLog PRIMARY KEY,
        TimestampUtc DATETIME2(3) NOT NULL,
        ActionName NVARCHAR(128) NOT NULL,
        EntityType NVARCHAR(128) NOT NULL,
        EntityId NVARCHAR(128) NOT NULL,
        ActorName NVARCHAR(256) NOT NULL,
        SourceIp NVARCHAR(64) NULL,
        DetailsJson NVARCHAR(MAX) NULL
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_AdminAuditLog_TimestampUtc' AND object_id = OBJECT_ID(N'dbo.AdminAuditLog'))
BEGIN
    CREATE INDEX IX_AdminAuditLog_TimestampUtc ON dbo.AdminAuditLog (TimestampUtc DESC);
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_AdminAuditLog_Action_Entity_Time' AND object_id = OBJECT_ID(N'dbo.AdminAuditLog'))
BEGIN
    CREATE INDEX IX_AdminAuditLog_Action_Entity_Time ON dbo.AdminAuditLog (ActionName, EntityType, TimestampUtc DESC);
END;
GO

IF OBJECT_ID(N'dbo.LdapAuthSettings', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.LdapAuthSettings
    (
        Id INT NOT NULL CONSTRAINT PK_LdapAuthSettings PRIMARY KEY,
        Enabled BIT NOT NULL,
        HostName NVARCHAR(256) NOT NULL,
        PortNumber INT NOT NULL,
        SecurityMode NVARCHAR(16) NOT NULL,
        TimeoutSeconds INT NOT NULL,
        ValidateTlsCertificate BIT NOT NULL,
        BaseDn NVARCHAR(1024) NOT NULL,
        BindFormat NVARCHAR(64) NOT NULL,
        DomainSuffix NVARCHAR(256) NOT NULL,
        NetbiosDomain NVARCHAR(128) NOT NULL,
        AdminGroupDn NVARCHAR(1024) NOT NULL,
        OperatorGroupDn NVARCHAR(1024) NOT NULL,
        ReaderGroupDn NVARCHAR(1024) NOT NULL,
        UpdatedUtc DATETIME2(3) NOT NULL
    );
END;
GO

IF OBJECT_ID(N'dbo.RetentionSettings', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.RetentionSettings
    (
        Id INT NOT NULL CONSTRAINT PK_RetentionSettings PRIMARY KEY,
        Enabled BIT NOT NULL,
        EventsDays INT NOT NULL,
        TimelineDays INT NOT NULL,
        AlertsDays INT NOT NULL,
        IntervalHours INT NOT NULL,
        PurgeBatchSize INT NOT NULL,
        MaxRowsPerRun INT NOT NULL CONSTRAINT DF_RetentionSettings_MaxRowsPerRun DEFAULT 500000,
        UpdatedUtc DATETIME2(3) NOT NULL
    );
END;
GO

IF COL_LENGTH(N'dbo.RetentionSettings', N'MaxRowsPerRun') IS NULL
BEGIN
    ALTER TABLE dbo.RetentionSettings
    ADD MaxRowsPerRun INT NOT NULL CONSTRAINT DF_RetentionSettings_MaxRowsPerRun_Upgrade DEFAULT 500000;
END;
GO

IF OBJECT_ID(N'dbo.RetentionRuns', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.RetentionRuns
    (
        RunId UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_RetentionRuns PRIMARY KEY,
        TriggerName NVARCHAR(32) NOT NULL,
        StatusName NVARCHAR(32) NOT NULL,
        StartedUtc DATETIME2(3) NOT NULL,
        CompletedUtc DATETIME2(3) NULL,
        EventsCutoffUtc DATETIME2(3) NOT NULL,
        TimelineCutoffUtc DATETIME2(3) NOT NULL,
        AlertsCutoffUtc DATETIME2(3) NOT NULL,
        DeletedEvents INT NOT NULL,
        DeletedTimelineEvents INT NOT NULL,
        DeletedAlerts INT NOT NULL,
        DurationMs BIGINT NOT NULL,
        ErrorMessage NVARCHAR(2048) NULL
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_RetentionRuns_StartedUtc' AND object_id = OBJECT_ID(N'dbo.RetentionRuns'))
BEGIN
    CREATE INDEX IX_RetentionRuns_StartedUtc ON dbo.RetentionRuns (StartedUtc DESC);
END;
GO

UPDATE dbo.RetentionRuns
SET StatusName = N'failed',
    CompletedUtc = COALESCE(CompletedUtc, SYSUTCDATETIME()),
    ErrorMessage = COALESCE(ErrorMessage, N'Execucao interrompida antes da conclusao.')
WHERE StatusName = N'running'
  AND StartedUtc < DATEADD(HOUR, -6, SYSUTCDATETIME());
GO

IF OBJECT_ID(N'dbo.RetentionArchives', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.RetentionArchives
    (
        ArchiveId UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_RetentionArchives PRIMARY KEY,
        RunId UNIQUEIDENTIFIER NOT NULL,
        DatasetName NVARCHAR(32) NOT NULL,
        RelativePath NVARCHAR(1024) NOT NULL,
        CutoffUtc DATETIME2(3) NOT NULL,
        RecordCount INT NOT NULL,
        FileSizeBytes BIGINT NOT NULL,
        Sha256 CHAR(64) NOT NULL,
        CreatedUtc DATETIME2(3) NOT NULL
    );
END;
GO

IF COL_LENGTH(N'dbo.RetentionArchives', N'BackupRelativePath') IS NULL
    ALTER TABLE dbo.RetentionArchives ADD BackupRelativePath NVARCHAR(1024) NULL;
IF COL_LENGTH(N'dbo.RetentionArchives', N'BackupSha256') IS NULL
    ALTER TABLE dbo.RetentionArchives ADD BackupSha256 CHAR(64) NULL;
IF COL_LENGTH(N'dbo.RetentionArchives', N'BackedUpUtc') IS NULL
    ALTER TABLE dbo.RetentionArchives ADD BackedUpUtc DATETIME2(3) NULL;
IF COL_LENGTH(N'dbo.RetentionArchives', N'PrimaryDeletedUtc') IS NULL
    ALTER TABLE dbo.RetentionArchives ADD PrimaryDeletedUtc DATETIME2(3) NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_RetentionArchives_RunId' AND object_id = OBJECT_ID(N'dbo.RetentionArchives'))
BEGIN
    CREATE INDEX IX_RetentionArchives_RunId ON dbo.RetentionArchives (RunId, DatasetName);
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_RetentionArchives_CreatedUtc' AND object_id = OBJECT_ID(N'dbo.RetentionArchives'))
BEGIN
    CREATE INDEX IX_RetentionArchives_CreatedUtc ON dbo.RetentionArchives (CreatedUtc DESC);
END;
GO

IF OBJECT_ID(N'dbo.RetentionRestoreRuns', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.RetentionRestoreRuns
    (
        RestoreId UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_RetentionRestoreRuns PRIMARY KEY,
        ArchiveId UNIQUEIDENTIFIER NOT NULL,
        StatusName NVARCHAR(32) NOT NULL,
        ActorName NVARCHAR(256) NOT NULL,
        StartedUtc DATETIME2(3) NOT NULL,
        CompletedUtc DATETIME2(3) NULL,
        RecordsRead INT NOT NULL,
        RecordsRestored INT NOT NULL,
        DuplicatesSkipped INT NOT NULL,
        DurationMs BIGINT NOT NULL,
        HashVerified BIT NOT NULL,
        ErrorMessage NVARCHAR(2048) NULL
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_RetentionRestoreRuns_Archive_Started' AND object_id = OBJECT_ID(N'dbo.RetentionRestoreRuns'))
BEGIN
    CREATE INDEX IX_RetentionRestoreRuns_Archive_Started
        ON dbo.RetentionRestoreRuns (ArchiveId, StartedUtc DESC);
END;
GO
