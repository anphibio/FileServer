USE master;
GO

IF NOT EXISTS (SELECT 1 FROM sys.sql_logins WHERE name = N'fileserver_monitor_app')
BEGIN
    CREATE LOGIN fileserver_monitor_app WITH PASSWORD = N'Troque_esta_senha_antes_de_usar_123!';
END;
GO

USE FileServerMonitor;
GO

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'fileserver_monitor_app')
BEGIN
    CREATE USER fileserver_monitor_app FOR LOGIN fileserver_monitor_app;
END;
GO

ALTER ROLE db_datareader ADD MEMBER fileserver_monitor_app;
ALTER ROLE db_datawriter ADD MEMBER fileserver_monitor_app;
GO
