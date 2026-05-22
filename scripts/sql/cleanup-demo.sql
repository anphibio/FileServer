USE FileServerMonitor;
GO

/*
Cleanup conservador da demo local.

Por padrão, o script apenas mostra o que será removido.
Para executar a limpeza de fato, altere:

    DECLARE @execute BIT = 1;

O heartbeat real do agente NÃO é removido.
*/

DECLARE @execute BIT = 1;

IF OBJECT_ID('tempdb..#DemoUsers') IS NOT NULL DROP TABLE #DemoUsers;
CREATE TABLE #DemoUsers
(
    UserName NVARCHAR(256) NOT NULL PRIMARY KEY
);

INSERT INTO #DemoUsers (UserName)
VALUES
    (N'EMPRESA\usuario.teste'),
    (N'EMPRESA\usuario.suspeito'),
    (N'EMPRESA\admin.arquivos'),
    (N'EMPRESA\maria.silva'),
    (N'EMPRESA\joao.santos');

IF OBJECT_ID('tempdb..#DemoEvents') IS NOT NULL DROP TABLE #DemoEvents;
CREATE TABLE #DemoEvents
(
    Id UNIQUEIDENTIFIER NOT NULL PRIMARY KEY
);

INSERT INTO #DemoEvents (Id)
SELECT Id
FROM dbo.FileAuditEvents
WHERE
    SourceName = N'manual-demo'
    OR SourceHost = N'WKS-DEMO-01'
    OR SourceIp = N'192.168.10.50'
    OR ProcessName = N'unknown.exe'
    OR FullPath LIKE N'%\-Demo-%' ESCAPE N'\'
    OR FullPath LIKE N'%.locked'
    OR EXISTS (SELECT 1 FROM #DemoUsers AS u WHERE u.UserName = dbo.FileAuditEvents.UserName);

IF OBJECT_ID('tempdb..#DemoAlerts') IS NOT NULL DROP TABLE #DemoAlerts;
CREATE TABLE #DemoAlerts
(
    Id UNIQUEIDENTIFIER NOT NULL PRIMARY KEY
);

INSERT INTO #DemoAlerts (Id)
SELECT Id
FROM dbo.FileServerAlerts
WHERE
    ServerName = N'FS01'
    AND (
        UserName IN (SELECT UserName FROM #DemoUsers)
        OR Description LIKE N'%locked%'
        OR Description LIKE N'%usuario.suspeito%'
        OR Description LIKE N'%usuario.teste%'
        OR SamplePathsJson LIKE N'%\-Demo-%' ESCAPE N'\'
        OR SamplePathsJson LIKE N'%.locked%'
    );

IF OBJECT_ID('tempdb..#DemoPaths') IS NOT NULL DROP TABLE #DemoPaths;
CREATE TABLE #DemoPaths
(
    Id UNIQUEIDENTIFIER NOT NULL PRIMARY KEY
);

INSERT INTO #DemoPaths (Id)
SELECT Id
FROM dbo.MonitoredPaths
WHERE RootPath LIKE N'%\-Demo-%' ESCAPE N'\';

IF OBJECT_ID('tempdb..#DemoAdminAudit') IS NOT NULL DROP TABLE #DemoAdminAudit;
CREATE TABLE #DemoAdminAudit
(
    Id UNIQUEIDENTIFIER NOT NULL PRIMARY KEY
);

INSERT INTO #DemoAdminAudit (Id)
SELECT Id
FROM dbo.AdminAuditLog
WHERE
    DetailsJson LIKE N'%\-Demo-%' ESCAPE N'\'
    OR DetailsJson LIKE N'%WKS-DEMO-01%'
    OR DetailsJson LIKE N'%usuario.suspeito%'
    OR DetailsJson LIKE N'%usuario.teste%';

SELECT
    (SELECT COUNT(*) FROM #DemoEvents) AS DemoEvents,
    (SELECT COUNT(*) FROM #DemoAlerts) AS DemoAlerts,
    (SELECT COUNT(*) FROM #DemoPaths) AS DemoPaths,
    (SELECT COUNT(*) FROM #DemoAdminAudit) AS DemoAdminAudit;

IF @execute = 0
BEGIN
    PRINT N'Preview only. Altere @execute para 1 para remover os registros.';

    SELECT TOP (20)
        TimestampUtc,
        ServerName,
        UserName,
        FullPath,
        SourceName,
        SourceHost
    FROM dbo.FileAuditEvents
    WHERE Id IN (SELECT Id FROM #DemoEvents)
    ORDER BY TimestampUtc DESC;

    SELECT TOP (20)
        CreatedUtc,
        RuleName,
        Severity,
        UserName,
        Description
    FROM dbo.FileServerAlerts
    WHERE Id IN (SELECT Id FROM #DemoAlerts)
    ORDER BY CreatedUtc DESC;

    SELECT TOP (20)
        CreatedUtc,
        ServerName,
        ShareName,
        RootPath,
        StatusName,
        PriorityName
    FROM dbo.MonitoredPaths
    WHERE Id IN (SELECT Id FROM #DemoPaths)
    ORDER BY CreatedUtc DESC;

    RETURN;
END;

BEGIN TRY
    BEGIN TRANSACTION;

    DELETE FROM dbo.FileAuditEvents
    WHERE Id IN (SELECT Id FROM #DemoEvents);

    DELETE FROM dbo.FileServerAlerts
    WHERE Id IN (SELECT Id FROM #DemoAlerts);

    DELETE FROM dbo.MonitoredPaths
    WHERE Id IN (SELECT Id FROM #DemoPaths);

    DELETE FROM dbo.AdminAuditLog
    WHERE Id IN (SELECT Id FROM #DemoAdminAudit);

    COMMIT TRANSACTION;

    PRINT N'Cleanup da demo concluído com sucesso.';
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0
    BEGIN
        ROLLBACK TRANSACTION;
    END;

    THROW;
END CATCH;
