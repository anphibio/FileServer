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
        IngestedUtc DATETIME2(3) NOT NULL CONSTRAINT DF_FileAuditEvents_IngestedUtc DEFAULT SYSUTCDATETIME()
    );
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
