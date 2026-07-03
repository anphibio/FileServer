using FileServerMonitor.Core;

var tests = new (string Name, Action Test)[]
{
    ("normaliza campos obrigatorios e defaults", NormalizesRequiredFieldsAndDefaults),
    ("dispara alerta de exclusao em massa", RaisesMassDeleteAlert),
    ("dispara alerta de alteracao de permissao", RaisesPermissionChangeAlert),
    ("dispara alerta de ransomware por extensao suspeita", RaisesRansomwareAlertBySuspiciousExtension),
    ("correlaciona USN com Security Log por caminho", CorrelatesUsnWithSecurityLogByPath),
    ("preserva acesso quando Security Log confirma leitura", PreservesAccessWhenSecurityLogConfirmsRead),
    ("prefere melhor correspondencia por caminho", PrefersBestPathMatch),
    ("nao correlaciona fora da janela", DoesNotCorrelateOutsideWindow),
    ("consolida rename do USN e suprime ruido do security log", CollapsesUsnRenameAndSuppressesSecurityNoise),
    ("consolida rename do USN mesmo com eventos intercalados", CollapsesUsnRenameWithInterleavedEvents),
    ("infere rename a partir de ruído do USN com o mesmo file id", InfersRenameFromUsnNoiseWithSameFileId),
    ("trata nome provisorio de bitmap como criacao final", TreatsBitmapProvisionalRenameAsFinalCreation),
    ("trata nome provisorio do Office como criacao final", TreatsOfficeProvisionalRenameAsFinalCreation),
    ("trata nome provisorio do PowerPoint como criacao final", TreatsPowerPointProvisionalRenameAsFinalCreation),
    ("colapsa cadeia provisoria de bitmap em uma criacao final", CollapsesBitmapProvisionalChainIntoSingleFinalCreation),
    ("trata alteracao de nome provisorio como criacao quando security log confirma criacao", TreatsProvisionalChangeAsCreationWhenSecurityShowsCreation),
    ("trata nome provisorio mantido como criacao", TreatsKeptProvisionalNameAsCreation),
    ("classifica rename entre pastas como movimentacao", ClassifiesCrossFolderRenameAsMove),
    ("nao correlaciona arquivos diferentes por proximidade", DoesNotCorrelateDifferentFilesByTiming),
    ("nao cruza bitmaps provisorios repetidos", DoesNotCrossCorrelateRepeatedBitmapProvisionals),
    ("nao cruza planilhas provisorias repetidas", DoesNotCrossCorrelateRepeatedExcelProvisionals),
    ("timeline colapsa acessos repetidos ao mesmo arquivo", TimelineCollapsesRepeatedFileAccess),
    ("timeline preserva acessos distintos em pastas", TimelineKeepsDistinctFolderAccess),
    ("timeline completa exclusao de descendentes conhecidos", TimelineSynthesizesKnownDescendantDeletes),
    ("timeline remove ecos de exclusao em rename", TimelineSuppressesDeleteEchoAroundRename),
    ("timeline preserva alteracao real antes de exclusao", TimelineKeepsRealModificationBeforeDelete),
    ("timeline preserva alteracao real antes de acesso", TimelineKeepsRealModificationBeforeAccess),
    ("timeline trata append de texto do security log como alteracao", TimelineTreatsSecurityTextAppendCreateAsModification),
    ("timeline colapsa append e modified do security log em uma alteracao", TimelineCollapsesSecurityTextAppendAndModifyDuplicate),
    ("timeline trata append de texto com modified vizinho como alteracao", TimelineTreatsSecurityTextAppendWithNearbyModifyAsModification),
    ("timeline colapsa rename duplicado depois de resolver usuario", TimelineCollapsesRenameDuplicateAfterUserResolution),
    ("timeline preserva rename entre nomes padrao do Windows", TimelineKeepsRenameBetweenWindowsDefaultNames),
    ("agente classifica saude operacional ok atencao e critico", AgentClassifiesOperationalHealth),
    ("inventario normaliza item de arquivo e pasta", InventoryNormalizesFileAndFolderItems),
    ("inventario calcula resumo gerencial", InventoryBuildsGovernanceSummary)
};

var failures = new List<string>();

foreach (var (name, test) in tests)
{
    try
    {
        test();
        Console.WriteLine($"OK {name}");
    }
    catch (Exception ex)
    {
        failures.Add($"{name}: {ex.Message}");
        Console.Error.WriteLine($"FAIL {name}: {ex}");
    }
}

if (failures.Count > 0)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine($"{failures.Count} teste(s) falharam.");
    Environment.Exit(1);
}

Console.WriteLine();
Console.WriteLine($"{tests.Length} teste(s) passaram.");

static void NormalizesRequiredFieldsAndDefaults()
{
    var input = new FileAuditEventInput(
        TimestampUtc: null,
        Server: " FS01 ",
        Share: " Financeiro ",
        Path: "\\\\FS01\\Financeiro\\Relatorio.XLSX",
        PreviousPath: " ",
        ObjectType: " FILE ",
        Action: " MODIFIED ",
        User: "EMPRESA\\maria.silva",
        Sid: null,
        SourceHost: null,
        SourceIp: null,
        ProcessName: "EXCEL.EXE",
        FileSizeBytes: 1024,
        Extension: null,
        Result: null,
        Severity: null,
        Source: null);

    var normalized = FileAuditEventNormalizer.Normalize(input);

    Assert(normalized.Server == "FS01", "Servidor deveria ser aparado.");
    Assert(normalized.Share == "Financeiro", "Compartilhamento deveria ser aparado.");
    Assert(normalized.ObjectType == "file", "Tipo deveria ficar em minusculo.");
    Assert(normalized.Action == "modified", "Acao deveria ficar em minusculo.");
    Assert(normalized.Extension == ".xlsx", "Extensao deveria ser derivada do caminho.");
    Assert(normalized.Result == "success", "Resultado default deveria ser success.");
    Assert(normalized.Severity == "info", "Severidade default deveria ser info.");
    Assert(normalized.Source == "manual-ingest", "Origem default deveria ser manual-ingest.");
}

static void InventoryNormalizesFileAndFolderItems()
{
    var scannedAt = DateTimeOffset.Parse("2026-07-02T03:00:00Z");
    var snapshotId = Guid.NewGuid();

    var file = FileInventoryNormalizer.Normalize(new FileInventoryItemInput(
        SnapshotId: snapshotId,
        ScannedAtUtc: scannedAt,
        Server: " FileServer ",
        Share: " Corporativo ",
        RootPath: @"C:/Corporativo",
        Path: @"C:/Corporativo/RH/Relatorio.XLSX",
        RelativePath: null,
        Name: null,
        ItemType: "file",
        SizeBytes: 2048,
        CreatedUtc: null,
        ModifiedUtc: null,
        AccessedUtc: null,
        Error: null));

    var folder = FileInventoryNormalizer.Normalize(new FileInventoryItemInput(
        SnapshotId: snapshotId,
        ScannedAtUtc: scannedAt,
        Server: "FileServer",
        Share: "Corporativo",
        RootPath: @"C:\Corporativo",
        Path: @"C:\Corporativo\RH",
        RelativePath: null,
        Name: null,
        ItemType: "directory",
        SizeBytes: 999,
        CreatedUtc: null,
        ModifiedUtc: null,
        AccessedUtc: null,
        Error: null));

    Assert(file.Server == "FileServer", "Servidor do inventario deveria ser aparado.");
    Assert(file.Path == @"C:\Corporativo\RH\Relatorio.XLSX", "Caminho deveria usar separador Windows.");
    Assert(file.RelativePath == @"RH\Relatorio.XLSX", "Caminho relativo deveria ser derivado do root.");
    Assert(file.Name == "Relatorio.XLSX", "Nome deveria ser derivado do caminho.");
    Assert(file.Extension == ".xlsx", "Extensao deveria ser normalizada em minusculo.");
    Assert(file.SizeBytes == 2048, "Tamanho de arquivo deveria ser preservado.");
    Assert(file.Depth == 2, "Profundidade deveria contar partes do caminho relativo.");
    Assert(folder.ItemType == "folder", "Directory deveria virar folder.");
    Assert(folder.SizeBytes == 0, "Pasta nao deveria carregar tamanho proprio.");
    Assert(folder.Extension is null, "Pasta nao deveria ter extensao.");
}

static void InventoryBuildsGovernanceSummary()
{
    var snapshotId = Guid.NewGuid();
    var now = DateTimeOffset.Parse("2026-07-02T03:00:00Z");
    var snapshot = new FileInventorySnapshot(
        Id: snapshotId,
        Server: "FileServer",
        Share: "Corporativo",
        RootPath: @"C:\Corporativo",
        StartedUtc: now.AddMinutes(-5),
        FinishedUtc: now,
        Status: "completed",
        FileCount: 3,
        FolderCount: 2,
        TotalBytes: 6144,
        ErrorCount: 0,
        Error: null);
    var items = new[]
    {
        BuildInventoryItem(snapshotId, now, @"C:\Corporativo\RH", "folder", 0, modifiedUtc: now.AddDays(-10)),
        BuildInventoryItem(snapshotId, now, @"C:\Corporativo\DTI", "folder", 0, modifiedUtc: now.AddDays(-10)),
        BuildInventoryItem(snapshotId, now, @"C:\Corporativo\RH\a.xlsx", "file", 4096, modifiedUtc: now.AddDays(-10)),
        BuildInventoryItem(snapshotId, now, @"C:\Corporativo\RH\b.txt", "file", 1024, modifiedUtc: now.AddDays(-45)),
        BuildInventoryItem(snapshotId, now, @"C:\Corporativo\DTI\c.txt", "file", 1024, modifiedUtc: now.AddDays(-400))
    };

    var summary = FileInventoryAnalyzer.BuildSummary(snapshot, items, top: 5, nowUtc: now);

    Assert(summary.Status == "completed", "Resumo deveria preservar status do snapshot.");
    Assert(summary.FileCount == 3, "Resumo deveria contar arquivos.");
    Assert(summary.FolderCount == 2, "Resumo deveria contar pastas.");
    Assert(summary.TotalBytes == 6144, "Resumo deveria somar bytes.");
    Assert(summary.TopFolders.First().Path == @"C:\Corporativo\RH", "Pasta RH deveria liderar por tamanho.");
    Assert(summary.TopFolders.First().TotalBytes == 5120, "Pasta RH deveria somar arquivos filhos diretos.");
    Assert(summary.TopExtensions.First().Extension == ".xlsx", "Extensao xlsx deveria liderar por tamanho.");
    Assert(summary.AgeBuckets.Single(item => item.Label == "31-90 dias").FileCount == 1, "Um arquivo deveria estar no bucket 31-90 dias.");
    Assert(summary.AgeBuckets.Single(item => item.Label == "+365 dias").FileCount == 1, "Um arquivo deveria estar no bucket acima de 365 dias.");
}

static void AgentClassifiesOperationalHealth()
{
    var now = DateTimeOffset.Parse("2026-07-02T12:00:00Z");

    var ok = AgentOperationalHealth.Evaluate(new AgentOperationalHealthInput(
        Status: "running",
        LastHeartbeatUtc: now.AddMinutes(-1),
        LastSuccessfulSendUtc: now.AddMinutes(-2),
        PendingQueueEvents: 0,
        LastCycle: new AgentCycleMetrics(
            StartedUtc: now.AddMinutes(-1),
            FinishedUtc: now,
            DurationMs: 900,
            SecurityEventsRead: 2,
            UsnEventsRead: 3,
            CorrelatedEvents: 4,
            SentEvents: 4,
            QueuedEvents: 0,
            Error: null),
        NowUtc: now,
        StaleAfterMinutes: 10,
        BacklogWarningThreshold: 1000,
        SendLagWarningMinutes: 15));

    Assert(ok.Level == "ok", "Agente recente e sem fila deveria ser ok.");
    Assert(ok.HasError == false, "Agente ok nao deveria ter erro.");

    var idleOk = AgentOperationalHealth.Evaluate(new AgentOperationalHealthInput(
        Status: "running",
        LastHeartbeatUtc: now.AddMinutes(-1),
        LastSuccessfulSendUtc: now.AddHours(-2),
        PendingQueueEvents: 0,
        LastCycle: new AgentCycleMetrics(
            StartedUtc: now.AddSeconds(-20),
            FinishedUtc: now.AddSeconds(-1),
            DurationMs: 19000,
            SecurityEventsRead: 0,
            UsnEventsRead: 0,
            CorrelatedEvents: 0,
            SentEvents: 0,
            QueuedEvents: 0,
            Error: null),
        NowUtc: now,
        StaleAfterMinutes: 10,
        BacklogWarningThreshold: 1000,
        SendLagWarningMinutes: 15));

    Assert(idleOk.Level == "ok", "Ciclo recente sem eventos novos nao deveria virar atraso de envio.");

    var attention = AgentOperationalHealth.Evaluate(new AgentOperationalHealthInput(
        Status: "running",
        LastHeartbeatUtc: now.AddMinutes(-1),
        LastSuccessfulSendUtc: now.AddMinutes(-20),
        PendingQueueEvents: 5,
        LastCycle: new AgentCycleMetrics(
            StartedUtc: now.AddMinutes(-1),
            FinishedUtc: now,
            DurationMs: 1200,
            SecurityEventsRead: 5,
            UsnEventsRead: 5,
            CorrelatedEvents: 7,
            SentEvents: 0,
            QueuedEvents: 7,
            Error: "API indisponivel"),
        NowUtc: now,
        StaleAfterMinutes: 10,
        BacklogWarningThreshold: 1000,
        SendLagWarningMinutes: 15));

    Assert(attention.Level == "attention", "Fila pequena ou erro recente deveria exigir atencao.");
    Assert(attention.HasError, "Erro do ciclo deveria ser sinalizado.");
    Assert(attention.LastSuccessfulSendAgeSeconds == 1200, "Atraso do ultimo envio deveria ser calculado.");

    var critical = AgentOperationalHealth.Evaluate(new AgentOperationalHealthInput(
        Status: "running",
        LastHeartbeatUtc: now.AddMinutes(-30),
        LastSuccessfulSendUtc: now.AddMinutes(-30),
        PendingQueueEvents: 0,
        LastCycle: null,
        NowUtc: now,
        StaleAfterMinutes: 10,
        BacklogWarningThreshold: 1000,
        SendLagWarningMinutes: 15));

    Assert(critical.Level == "critical", "Heartbeat atrasado deveria ser critico.");
    Assert(critical.LastHeartbeatAgeSeconds == 1800, "Atraso do heartbeat deveria ser calculado.");
}

static void RaisesMassDeleteAlert()
{
    var engine = new AlertRuleEngine(new AlertOptions(MassDeleteThreshold: 3));
    var events = BuildEvents(action: "deleted", count: 3);
    var alerts = engine.Evaluate(events);

    Assert(alerts.Any(alert => alert.Rule == "mass-delete" && alert.Severity == "critical"), "Alerta mass-delete nao foi gerado.");
}

static void RaisesPermissionChangeAlert()
{
    var engine = new AlertRuleEngine(new AlertOptions());
    var events = BuildEvents(action: "permission_changed", count: 1);
    var alerts = engine.Evaluate(events);

    Assert(alerts.Any(alert => alert.Rule == "permission-change" && alert.Severity == "high"), "Alerta permission-change nao foi gerado.");
}

static void RaisesRansomwareAlertBySuspiciousExtension()
{
    var engine = new AlertRuleEngine(new AlertOptions(SuspiciousExtensionThreshold: 2, RansomwareActivityThreshold: 999));
    var events = new[]
    {
        BuildEvent("modified", "\\\\FS01\\Dados\\a.lock", ".lock"),
        BuildEvent("modified", "\\\\FS01\\Dados\\b.locked", ".locked")
    };

    var alerts = engine.Evaluate(events);

    Assert(alerts.Any(alert => alert.Rule == "possible-ransomware" && alert.Severity == "critical"), "Alerta possible-ransomware nao foi gerado.");
}

static void CorrelatesUsnWithSecurityLogByPath()
{
    var timestamp = DateTimeOffset.UtcNow;
    var correlator = new EventCorrelator(TimeSpan.FromSeconds(10));
    var events = new[]
    {
        BuildCollectedEvent("security", timestamp, "\\\\FS01\\Dados\\relatorio.xlsx", "EMPRESA\\maria.silva", "security-log", "EXCEL.EXE"),
        BuildCollectedEvent("usn", timestamp.AddSeconds(2), "\\\\FS01\\Dados\\relatorio.xlsx", "UNKNOWN", "usn-journal", "fsutil.exe")
    };

    var correlated = correlator.Correlate(events).ToArray();
    var usn = correlated.Single(item => item.CursorType == "usn");

    Assert(usn.User == "EMPRESA\\maria.silva", "Usuario do USN deveria ser enriquecido pelo Security Log.");
    Assert(usn.ProcessName == "EXCEL.EXE", "Processo deveria ser enriquecido pelo Security Log.");
    Assert(usn.Source == "usn-journal+security-log", "Fonte deveria indicar correlacao.");
}

static void PreservesAccessWhenSecurityLogConfirmsRead()
{
    var timestamp = DateTimeOffset.UtcNow;
    var correlator = new EventCorrelator(TimeSpan.FromSeconds(10));
    var events = new[]
    {
        BuildCollectedEvent("security", timestamp, "\\\\FS01\\Dados\\Novo Documento.txt", "EMPRESA\\maria.silva", "security-log", "notepad.exe", action: "accessed", recordId: 10),
        BuildCollectedEvent("usn", timestamp.AddMilliseconds(100), "\\\\FS01\\Dados\\Novo Documento.txt", "UNKNOWN", "usn-journal", "fsutil.exe", action: "changed", usn: 100, fileReferenceId: "access-1")
    };

    var correlated = correlator.Correlate(events).Single(item => item.CursorType == "usn");

    Assert(correlated.Action == "accessed", "Leitura confirmada pelo Security Log deveria aparecer como acesso, nao alteracao.");
    Assert(correlated.User == "EMPRESA\\maria.silva", "Acesso deveria herdar usuario do Security Log.");
}

static void PrefersBestPathMatch()
{
    var timestamp = DateTimeOffset.UtcNow;
    var correlator = new EventCorrelator(TimeSpan.FromSeconds(10));
    var events = new[]
    {
        BuildCollectedEvent("security", timestamp, "\\\\FS01\\Dados\\outro\\relatorio.xlsx", "EMPRESA\\usuario.errado", "security-log", "WINWORD.EXE"),
        BuildCollectedEvent("security", timestamp.AddSeconds(1), "\\\\FS01\\Dados\\relatorio.xlsx", "EMPRESA\\usuario.correto", "security-log", "EXCEL.EXE"),
        BuildCollectedEvent("usn", timestamp.AddSeconds(2), "\\\\FS01\\Dados\\relatorio.xlsx", "UNKNOWN", "usn-journal", "fsutil.exe")
    };

    var usn = correlator.Correlate(events).Single(item => item.CursorType == "usn");

    Assert(usn.User == "EMPRESA\\usuario.correto", "Correlacao deveria preferir caminho exato.");
}

static void DoesNotCorrelateOutsideWindow()
{
    var timestamp = DateTimeOffset.UtcNow;
    var correlator = new EventCorrelator(TimeSpan.FromSeconds(5));
    var events = new[]
    {
        BuildCollectedEvent("security", timestamp, "\\\\FS01\\Dados\\relatorio.xlsx", "EMPRESA\\maria.silva", "security-log", "EXCEL.EXE"),
        BuildCollectedEvent("usn", timestamp.AddSeconds(30), "\\\\FS01\\Dados\\relatorio.xlsx", "UNKNOWN", "usn-journal", "fsutil.exe")
    };

    var usn = correlator.Correlate(events).Single(item => item.CursorType == "usn");

    Assert(usn.User == "UNKNOWN", "Evento fora da janela nao deveria ser correlacionado.");
    Assert(usn.Source == "usn-journal", "Fonte nao deveria mudar fora da janela.");
}

static void CollapsesUsnRenameAndSuppressesSecurityNoise()
{
    var timestamp = DateTimeOffset.UtcNow;
    var correlator = new EventCorrelator(TimeSpan.FromSeconds(5));
    var events = new[]
    {
        BuildCollectedEvent("usn", timestamp, "\\\\FS01\\Dados\\antes.txt", "UNKNOWN", "usn-journal", "fsutil.exe", action: "renamed_old", usn: 100, fileReferenceId: "abc"),
        BuildCollectedEvent("usn", timestamp.AddMilliseconds(50), "\\\\FS01\\Dados\\depois.txt", "UNKNOWN", "usn-journal", "fsutil.exe", action: "renamed_new", usn: 101, fileReferenceId: "abc"),
        BuildCollectedEvent("security", timestamp.AddMilliseconds(100), "\\\\FS01\\Dados\\antes.txt", "EMPRESA\\maria.silva", "security-log", "explorer.exe", action: "deleted", recordId: 10),
        BuildCollectedEvent("security", timestamp.AddMilliseconds(150), "\\\\FS01\\Dados\\depois.txt", "EMPRESA\\maria.silva", "security-log", "explorer.exe", action: "created_or_appended", recordId: 11)
    };

    var correlated = correlator.Correlate(events).ToArray();
    var renamed = correlated.Single(item => item.CursorType == "usn");

    Assert(renamed.Action == "renamed", "USN deveria consolidar rename.");
    Assert(renamed.Path == "\\\\FS01\\Dados\\depois.txt", "Caminho final deveria refletir o novo nome.");
    Assert(renamed.PreviousPath == "\\\\FS01\\Dados\\antes.txt", "Caminho anterior deveria ser preservado.");
    Assert(renamed.Source == "usn-journal+security-log", "Rename deveria ser enriquecido pelo Security Log.");
    Assert(correlated.All(item => item.RecordId is not 10 and not 11), "Eventos ruidosos do Security Log deveriam ser suprimidos.");
}

static void CollapsesUsnRenameWithInterleavedEvents()
{
    var timestamp = DateTimeOffset.UtcNow;
    var correlator = new EventCorrelator(TimeSpan.FromSeconds(5));
    var events = new[]
    {
        BuildCollectedEvent("usn", timestamp, "\\\\FS01\\Dados\\antes.txt", "UNKNOWN", "usn-journal", "fsutil.exe", action: "renamed_old", usn: 100, fileReferenceId: "abc"),
        BuildCollectedEvent("usn", timestamp.AddMilliseconds(25), "\\\\FS01\\Dados\\arquivo.tmp", "UNKNOWN", "usn-journal", "fsutil.exe", action: "changed", usn: 101, fileReferenceId: "tmp"),
        BuildCollectedEvent("usn", timestamp.AddMilliseconds(50), "\\\\FS01\\Dados\\depois.txt", "UNKNOWN", "usn-journal", "fsutil.exe", action: "renamed_new", usn: 102, fileReferenceId: "abc")
    };

    var correlated = correlator.Correlate(events).ToArray();
    var renamed = correlated.Single(item => item.Action == "renamed");

    Assert(renamed.Path == "\\\\FS01\\Dados\\depois.txt", "Rename intercalado deveria preservar o novo nome.");
    Assert(renamed.PreviousPath == "\\\\FS01\\Dados\\antes.txt", "Rename intercalado deveria preservar o nome anterior.");
    Assert(correlated.Count(item => item.Action == "renamed") == 1, "Rename intercalado deveria resultar em um unico evento consolidado.");
}

static void InfersRenameFromUsnNoiseWithSameFileId()
{
    var timestamp = DateTimeOffset.UtcNow;
    var correlator = new EventCorrelator(TimeSpan.FromSeconds(5));
    var events = new[]
    {
        BuildCollectedEvent("usn", timestamp, "\\\\FS01\\Dados\\antes.txt", "UNKNOWN", "usn-journal", "fsutil.exe", action: "changed", usn: 100, fileReferenceId: "abc"),
        BuildCollectedEvent("usn", timestamp.AddMilliseconds(100), "\\\\FS01\\Dados\\depois.txt", "UNKNOWN", "usn-journal", "fsutil.exe", action: "changed", usn: 101, fileReferenceId: "abc"),
        BuildCollectedEvent("security", timestamp.AddMilliseconds(150), "\\\\FS01\\Dados\\antes.txt", "EMPRESA\\maria.silva", "security-log", "explorer.exe", action: "deleted", recordId: 10),
        BuildCollectedEvent("security", timestamp.AddMilliseconds(180), "\\\\FS01\\Dados", "EMPRESA\\maria.silva", "security-log", "explorer.exe", action: "created_or_appended", recordId: 11)
    };

    var correlated = correlator.Correlate(events).ToArray();
    var renamed = correlated.Single(item => item.Action == "renamed");

    Assert(renamed.Path == "\\\\FS01\\Dados\\depois.txt", "Rename inferido deveria apontar para o novo caminho.");
    Assert(renamed.PreviousPath == "\\\\FS01\\Dados\\antes.txt", "Rename inferido deveria manter o caminho anterior.");
    Assert(renamed.Source == "usn-journal+security-log", "Rename inferido deveria ser enriquecido pelo Security Log.");
    Assert(correlated.All(item => item.Action != "changed"), "Ruido bruto do USN deveria ser removido depois da inferencia do rename.");
    Assert(correlated.All(item => item.RecordId is not 10 and not 11), "Ruido do Security Log deveria ser suprimido para rename inferido.");
}

static void TreatsBitmapProvisionalRenameAsFinalCreation()
{
    var timestamp = DateTimeOffset.UtcNow;
    var correlator = new EventCorrelator(TimeSpan.FromSeconds(10));
    var events = new[]
    {
        BuildCollectedEvent("security", timestamp, "\\\\FS01\\Dados\\New Bitmap Image.bmp", "EMPRESA\\maria.silva", "security-log", "explorer.exe", action: "created_or_appended", recordId: 10),
        BuildCollectedEvent("usn", timestamp.AddMilliseconds(100), "\\\\FS01\\Dados\\New Bitmap Image.bmp", "UNKNOWN", "usn-journal", "fsutil.exe", action: "renamed_old", usn: 100, fileReferenceId: "bitmap-1"),
        BuildCollectedEvent("usn", timestamp.AddMilliseconds(200), "\\\\FS01\\Dados\\Teste Bitmap.bmp", "UNKNOWN", "usn-journal", "fsutil.exe", action: "renamed_new", usn: 101, fileReferenceId: "bitmap-1")
    };

    var correlated = correlator.Correlate(events).ToArray();
    var created = correlated.Single(item => item.CursorType == "usn");

    Assert(created.Action == "created", "Rename de nome provisorio deveria virar criacao final.");
    Assert(created.Path == "\\\\FS01\\Dados\\Teste Bitmap.bmp", "Criacao deveria apontar para o nome final.");
    Assert(created.PreviousPath is null, "Criacao final nao deveria expor caminho anterior provisorio.");
    Assert(created.User == "EMPRESA\\maria.silva", "Criacao final deveria herdar usuario do Security Log.");
    Assert(correlated.All(item => item.RecordId is not 10), "Criacao provisoria do Security Log deveria ser suprimida.");
}

static void TreatsOfficeProvisionalRenameAsFinalCreation()
{
    var timestamp = DateTimeOffset.UtcNow;
    var correlator = new EventCorrelator(TimeSpan.FromSeconds(10));
    var events = new[]
    {
        BuildCollectedEvent("security", timestamp, "\\\\FS01\\Dados\\Novo(a) Planilha do Microsoft Excel.xlsx", "EMPRESA\\maria.silva", "security-log", "explorer.exe", action: "created_or_appended", recordId: 10),
        BuildCollectedEvent("usn", timestamp.AddMilliseconds(100), "\\\\FS01\\Dados\\Novo(a) Planilha do Microsoft Excel.xlsx", "UNKNOWN", "usn-journal", "fsutil.exe", action: "renamed_old", usn: 100, fileReferenceId: "excel-1"),
        BuildCollectedEvent("usn", timestamp.AddMilliseconds(200), "\\\\FS01\\Dados\\Relatorio Final.xlsx", "UNKNOWN", "usn-journal", "fsutil.exe", action: "renamed_new", usn: 101, fileReferenceId: "excel-1")
    };

    var correlated = correlator.Correlate(events).ToArray();
    var created = correlated.Single(item => item.CursorType == "usn");

    Assert(created.Action == "created", "Rename de nome provisorio Office deveria virar criacao final.");
    Assert(created.Path == "\\\\FS01\\Dados\\Relatorio Final.xlsx", "Criacao Office deveria apontar para o nome final.");
    Assert(created.PreviousPath is null, "Criacao Office nao deveria expor caminho anterior provisorio.");
    Assert(created.User == "EMPRESA\\maria.silva", "Criacao Office deveria herdar usuario do Security Log.");
}

static void TreatsPowerPointProvisionalRenameAsFinalCreation()
{
    var timestamp = DateTimeOffset.UtcNow;
    var correlator = new EventCorrelator(TimeSpan.FromSeconds(10));
    var events = new[]
    {
        BuildCollectedEvent("security", timestamp, "\\\\FS01\\Dados\\Novo(a) Apresenta\u00E7\u00E3o do Microsoft PowerPoint.pptx", "EMPRESA\\maria.silva", "security-log", "POWERPNT.EXE", action: "created_or_appended", recordId: 10),
        BuildCollectedEvent("usn", timestamp.AddMilliseconds(100), "\\\\FS01\\Dados\\Novo(a) Apresenta\u00E7\u00E3o do Microsoft PowerPoint.pptx", "UNKNOWN", "usn-journal", "fsutil.exe", action: "renamed_old", usn: 100, fileReferenceId: "pptx-1"),
        BuildCollectedEvent("usn", timestamp.AddMilliseconds(200), "\\\\FS01\\Dados\\Apresentacao Final.pptx", "UNKNOWN", "usn-journal", "fsutil.exe", action: "renamed_new", usn: 101, fileReferenceId: "pptx-1")
    };

    var correlated = correlator.Correlate(events).ToArray();
    var created = correlated.Single(item => item.CursorType == "usn");

    Assert(created.Action == "created", "Rename de nome provisorio PowerPoint deveria virar criacao final.");
    Assert(created.Path == "\\\\FS01\\Dados\\Apresentacao Final.pptx", "Criacao PowerPoint deveria apontar para o nome final.");
    Assert(created.PreviousPath is null, "Criacao PowerPoint nao deveria expor caminho anterior provisorio.");
    Assert(created.User == "EMPRESA\\maria.silva", "Criacao PowerPoint deveria herdar usuario do Security Log.");
}

static void CollapsesBitmapProvisionalChainIntoSingleFinalCreation()
{
    var timestamp = DateTimeOffset.UtcNow;
    var correlator = new EventCorrelator(TimeSpan.FromSeconds(10));
    var events = new[]
    {
        BuildCollectedEvent("security", timestamp, "\\\\FS01\\Dados\\New Bitmap Image.bmp", "EMPRESA\\maria.silva", "security-log", "explorer.exe", action: "created_or_appended", recordId: 10),
        BuildCollectedEvent("usn", timestamp.AddMilliseconds(50), "\\\\FS01\\Dados\\New Bitmap Image.bmp", "UNKNOWN", "usn-journal", "fsutil.exe", action: "renamed_old", usn: 100, fileReferenceId: "bmp-chain-1"),
        BuildCollectedEvent("usn", timestamp.AddMilliseconds(100), "\\\\FS01\\Dados\\Nova Imagem de Bitmap.bmp", "UNKNOWN", "usn-journal", "fsutil.exe", action: "renamed_new", usn: 101, fileReferenceId: "bmp-chain-1"),
        BuildCollectedEvent("usn", timestamp.AddMilliseconds(150), "\\\\FS01\\Dados\\Nova Imagem de Bitmap.bmp", "UNKNOWN", "usn-journal", "fsutil.exe", action: "renamed_old", usn: 102, fileReferenceId: "bmp-chain-1"),
        BuildCollectedEvent("usn", timestamp.AddMilliseconds(200), "\\\\FS01\\Dados\\Teste Bitmap Final.bmp", "UNKNOWN", "usn-journal", "fsutil.exe", action: "renamed_new", usn: 103, fileReferenceId: "bmp-chain-1")
    };

    var correlated = correlator.Correlate(events).Where(item => item.CursorType == "usn").ToArray();

    Assert(correlated.Length == 1, "Cadeia provisoria deveria virar um unico evento final.");
    Assert(correlated[0].Action == "created", "Cadeia provisoria deveria virar criacao final.");
    Assert(correlated[0].Path == "\\\\FS01\\Dados\\Teste Bitmap Final.bmp", "Criacao final deveria apontar para o ultimo nome.");
    Assert(correlated[0].PreviousPath is null, "Criacao final nao deveria expor etapas provisorias.");
    Assert(correlated[0].User == "EMPRESA\\maria.silva", "Criacao final deveria manter o usuario enriquecido.");
}

static void TreatsProvisionalChangeAsCreationWhenSecurityShowsCreation()
{
    var timestamp = DateTimeOffset.UtcNow;
    var correlator = new EventCorrelator(TimeSpan.FromSeconds(10));
    var events = new[]
    {
        BuildCollectedEvent("security", timestamp, "\\\\FS01\\Dados\\Novo Documento de Texto.txt", "EMPRESA\\maria.silva", "security-log", "explorer.exe", action: "created_or_appended", recordId: 10),
        BuildCollectedEvent("usn", timestamp.AddMilliseconds(100), "\\\\FS01\\Dados\\Novo Documento de Texto.txt", "UNKNOWN", "usn-journal", "fsutil.exe", action: "changed", usn: 100, fileReferenceId: "text-1")
    };

    var correlated = correlator.Correlate(events).ToArray();
    var created = correlated.Single(item => item.CursorType == "usn");

    Assert(created.Action == "created", "Alteracao de nome provisorio recem-criado deveria virar criacao.");
    Assert(created.Path == "\\\\FS01\\Dados\\Novo Documento de Texto.txt", "Criacao deveria preservar o nome quando ele ainda e o atual.");
    Assert(created.User == "EMPRESA\\maria.silva", "Criacao deveria herdar usuario do Security Log.");
    Assert(correlated.All(item => item.RecordId is not 10), "Criacao ruidosa do Security Log deveria ser suprimida.");
}

static void TreatsKeptProvisionalNameAsCreation()
{
    var timestamp = DateTimeOffset.UtcNow;
    var correlator = new EventCorrelator(TimeSpan.FromSeconds(10));
    var events = new[]
    {
        BuildCollectedEvent("usn", timestamp, "\\\\FS01\\Dados\\Novo Documento de Texto.txt", "UNKNOWN", "usn-journal", "fsutil.exe", action: "changed", usn: 100, fileReferenceId: "text-1"),
        BuildCollectedEvent("usn", timestamp.AddSeconds(2), "\\\\FS01\\Dados\\Nova Imagem de Bitmap.bmp", "UNKNOWN", "usn-journal", "fsutil.exe", action: "modified", usn: 101, fileReferenceId: "bitmap-1")
    };

    var correlated = correlator.Correlate(events).ToArray();

    Assert(correlated.Length == 2, "Nomes provisorios mantidos deveriam continuar aparecendo.");
    Assert(correlated.All(item => item.Action == "created"), "Nomes provisorios mantidos deveriam aparecer como criacao, nao alteracao.");
}

static void ClassifiesCrossFolderRenameAsMove()
{
    var timestamp = DateTimeOffset.UtcNow;
    var correlator = new EventCorrelator(TimeSpan.FromSeconds(10));
    var events = new[]
    {
        BuildCollectedEvent("usn", timestamp, "\\\\FS01\\Dados\\teste-live-02.txt", "UNKNOWN", "usn-journal", "fsutil.exe", action: "renamed_old", usn: 100, fileReferenceId: "move-1", previousPath: null),
        BuildCollectedEvent("usn", timestamp.AddMilliseconds(100), "\\\\FS01\\Dados\\RH\\teste-live-02.txt", "UNKNOWN", "usn-journal", "fsutil.exe", action: "renamed_new", usn: 101, fileReferenceId: "move-1"),
        BuildCollectedEvent("security", timestamp.AddMilliseconds(150), "\\\\FS01\\Dados\\teste-live-02.txt", "EMPRESA\\maria.silva", "security-log", "explorer.exe", action: "deleted", recordId: 20)
    };

    var correlated = correlator.Correlate(events).ToArray();
    var moved = correlated.Single(item => item.CursorType == "usn");

    Assert(moved.Action == "moved", "Rename entre pastas deveria ser classificado como movimentacao.");
    Assert(moved.Path == "\\\\FS01\\Dados\\RH\\teste-live-02.txt", "Movimentacao deveria apontar para o destino.");
    Assert(moved.PreviousPath == "\\\\FS01\\Dados\\teste-live-02.txt", "Movimentacao deveria preservar origem.");
    Assert(moved.User == "EMPRESA\\maria.silva", "Movimentacao deveria herdar usuario do Security Log.");
    Assert(correlated.All(item => item.RecordId is not 20), "Delete aparente usado na movimentacao deveria ser suprimido.");
}

static void DoesNotCorrelateDifferentFilesByTiming()
{
    var timestamp = DateTimeOffset.UtcNow;
    var correlator = new EventCorrelator(TimeSpan.FromSeconds(10));
    var events = new[]
    {
        BuildCollectedEvent("usn", timestamp, "\\\\FS01\\Dados\\teste-live-02.txt", "UNKNOWN", "usn-journal", "fsutil.exe", action: "changed", usn: 100, fileReferenceId: "txt-1"),
        BuildCollectedEvent("usn", timestamp.AddMilliseconds(100), "\\\\FS01\\Dados\\Nova Imagem de Bitmap.bmp", "UNKNOWN", "usn-journal", "fsutil.exe", action: "changed", usn: 101, fileReferenceId: "bmp-1"),
        BuildCollectedEvent("security", timestamp.AddMilliseconds(150), "\\\\FS01\\Dados\\teste-live-02.txt", "EMPRESA\\maria.silva", "security-log", "explorer.exe", action: "deleted", recordId: 20)
    };

    var correlated = correlator.Correlate(events).ToArray();

    Assert(correlated.All(item => item.Action != "renamed" && item.Action != "moved"), "Arquivos diferentes nao deveriam virar transicao.");
    Assert(correlated.Any(item => item.Path == "\\\\FS01\\Dados\\Nova Imagem de Bitmap.bmp"), "Evento do bitmap deveria permanecer independente.");
}

static void DoesNotCrossCorrelateRepeatedBitmapProvisionals()
{
    var timestamp = DateTimeOffset.UtcNow;
    var correlator = new EventCorrelator(TimeSpan.FromSeconds(10));
    var events = new[]
    {
        BuildCollectedEvent("security", timestamp, "\\\\FS01\\Dados\\New Bitmap Image.bmp", "EMPRESA\\maria.silva", "security-log", "explorer.exe", action: "created_or_appended", recordId: 10),
        BuildCollectedEvent("usn", timestamp.AddMilliseconds(50), "\\\\FS01\\Dados\\New Bitmap Image.bmp", "UNKNOWN", "usn-journal", "fsutil.exe", action: "renamed_old", usn: 100, fileReferenceId: "bmp-1"),
        BuildCollectedEvent("usn", timestamp.AddMilliseconds(80), "\\\\FS01\\Dados\\teste-bmp-01.bmp", "UNKNOWN", "usn-journal", "fsutil.exe", action: "renamed_new", usn: 101, fileReferenceId: "bmp-1"),
        BuildCollectedEvent("security", timestamp.AddMilliseconds(120), "\\\\FS01\\Dados\\New Bitmap Image (2).bmp", "EMPRESA\\maria.silva", "security-log", "explorer.exe", action: "created_or_appended", recordId: 11),
        BuildCollectedEvent("usn", timestamp.AddMilliseconds(160), "\\\\FS01\\Dados\\New Bitmap Image (2).bmp", "UNKNOWN", "usn-journal", "fsutil.exe", action: "renamed_old", usn: 102, fileReferenceId: "bmp-2"),
        BuildCollectedEvent("usn", timestamp.AddMilliseconds(200), "\\\\FS01\\Dados\\teste-bmp-02.bmp", "UNKNOWN", "usn-journal", "fsutil.exe", action: "renamed_new", usn: 103, fileReferenceId: "bmp-2")
    };

    var correlated = correlator.Correlate(events).ToArray();
    var createdEvents = correlated.Where(item => item.CursorType == "usn").OrderBy(item => item.Path).ToArray();

    Assert(createdEvents.Length == 2, "Bitmaps provisorios repetidos deveriam resultar em duas criacoes finais.");
    Assert(createdEvents.All(item => item.Action == "created"), "Bitmaps provisorios repetidos deveriam virar criacao final.");
    Assert(createdEvents.Any(item => item.Path == "\\\\FS01\\Dados\\teste-bmp-01.bmp"), "Primeiro bitmap deveria permanecer independente.");
    Assert(createdEvents.Any(item => item.Path == "\\\\FS01\\Dados\\teste-bmp-02.bmp"), "Segundo bitmap deveria permanecer independente.");
    Assert(createdEvents.All(item => item.PreviousPath is null), "Criacoes finais nao deveriam expor nomes provisorios.");
}

static void DoesNotCrossCorrelateRepeatedExcelProvisionals()
{
    var timestamp = DateTimeOffset.UtcNow;
    var correlator = new EventCorrelator(TimeSpan.FromSeconds(10));
    var events = new[]
    {
        BuildCollectedEvent("security", timestamp, "\\\\FS01\\Dados\\Novo(a) Planilha do Microsoft Excel.xlsx", "EMPRESA\\maria.silva", "security-log", "EXCEL.EXE", action: "created_or_appended", recordId: 10),
        BuildCollectedEvent("usn", timestamp.AddMilliseconds(50), "\\\\FS01\\Dados\\Novo(a) Planilha do Microsoft Excel.xlsx", "UNKNOWN", "usn-journal", "fsutil.exe", action: "renamed_old", usn: 100, fileReferenceId: "xlsx-1"),
        BuildCollectedEvent("usn", timestamp.AddMilliseconds(80), "\\\\FS01\\Dados\\teste-xlsx-01.xlsx", "UNKNOWN", "usn-journal", "fsutil.exe", action: "renamed_new", usn: 101, fileReferenceId: "xlsx-1"),
        BuildCollectedEvent("security", timestamp.AddMilliseconds(120), "\\\\FS01\\Dados\\Novo(a) Planilha do Microsoft Excel (2).xlsx", "EMPRESA\\maria.silva", "security-log", "EXCEL.EXE", action: "created_or_appended", recordId: 11),
        BuildCollectedEvent("usn", timestamp.AddMilliseconds(160), "\\\\FS01\\Dados\\Novo(a) Planilha do Microsoft Excel (2).xlsx", "UNKNOWN", "usn-journal", "fsutil.exe", action: "renamed_old", usn: 102, fileReferenceId: "xlsx-2"),
        BuildCollectedEvent("usn", timestamp.AddMilliseconds(200), "\\\\FS01\\Dados\\teste-xlsx-02.xlsx", "UNKNOWN", "usn-journal", "fsutil.exe", action: "renamed_new", usn: 103, fileReferenceId: "xlsx-2")
    };

    var correlated = correlator.Correlate(events).ToArray();
    var createdEvents = correlated.Where(item => item.CursorType == "usn").OrderBy(item => item.Path).ToArray();

    Assert(createdEvents.Length == 2, "Planilhas provisorias repetidas deveriam resultar em duas criacoes finais.");
    Assert(createdEvents.All(item => item.Action == "created"), "Planilhas provisorias repetidas deveriam virar criacao final.");
    Assert(createdEvents.Any(item => item.Path == "\\\\FS01\\Dados\\teste-xlsx-01.xlsx"), "Primeira planilha deveria permanecer independente.");
    Assert(createdEvents.Any(item => item.Path == "\\\\FS01\\Dados\\teste-xlsx-02.xlsx"), "Segunda planilha deveria permanecer independente.");
    Assert(createdEvents.All(item => item.PreviousPath is null), "Criacoes finais nao deveriam expor nomes provisorios.");
}

static void TimelineCollapsesRepeatedFileAccess()
{
    var timestamp = DateTimeOffset.UtcNow;
    var projector = new EventTimelineProjector();
    var events = new[]
    {
        BuildTimelineEvent(timestamp, "accessed", @"C:\Corporativo\Novo(a) Documento de Texto - Copia (4).txt", source: "windows-security-log"),
        BuildTimelineEvent(timestamp.AddSeconds(1), "accessed", @"C:\Corporativo\Novo(a) Documento de Texto - Copia (4).txt", source: "windows-security-log"),
        BuildTimelineEvent(timestamp.AddSeconds(2), "accessed", @"C:\Corporativo\Novo(a) Documento de Texto - Copia (4).txt", source: "windows-security-log")
    };

    var display = projector.BuildDisplayEvents(events).ToArray();

    Assert(display.Length == 1, "Acessos repetidos ao mesmo arquivo em poucos segundos deveriam virar um unico evento.");
    Assert(display[0].Action == "accessed", "Evento restante deveria continuar sendo acesso.");
}

static void TimelineKeepsDistinctFolderAccess()
{
    var timestamp = DateTimeOffset.UtcNow;
    var projector = new EventTimelineProjector();
    var events = new[]
    {
        BuildTimelineEvent(timestamp, "accessed", @"C:\Corporativo\Nova pasta", objectType: "folder", source: "windows-security-log"),
        BuildTimelineEvent(timestamp.AddSeconds(1), "accessed", @"C:\Corporativo\Nova pasta\Nova pasta", objectType: "folder", source: "windows-security-log")
    };

    var display = projector.BuildDisplayEvents(events).OrderBy(item => item.Path).ToArray();

    Assert(display.Length == 2, "Acessos em pastas diferentes nao deveriam ser colapsados como ruido.");
    Assert(display.All(item => item.Action == "accessed"), "Eventos de pasta deveriam continuar como acesso.");
}

static void TimelineSynthesizesKnownDescendantDeletes()
{
    var timestamp = DateTimeOffset.UtcNow;
    var projector = new EventTimelineProjector();
    var events = new[]
    {
        BuildTimelineEvent(timestamp, "created", @"C:\Corporativo\Example folder", objectType: "folder", source: "usn-journal+security-log"),
        BuildTimelineEvent(timestamp.AddSeconds(1), "created", @"C:\Corporativo\Example folder\Example txt file.txt", source: "usn-journal+security-log"),
        BuildTimelineEvent(timestamp.AddSeconds(2), "created", @"C:\Corporativo\Example folder\Another example txt file.txt", source: "usn-journal+security-log"),
        BuildTimelineEvent(timestamp.AddSeconds(10), "deleted", @"C:\Corporativo\Example folder", objectType: "folder", source: "windows-security-log")
    };

    var display = projector.BuildDisplayEvents(events).ToArray();

    Assert(display.Count(item => item.Action == "deleted") == 3, "Excluir pasta deveria exibir tambem os descendentes conhecidos sem delete explicito.");
    Assert(display.Any(item => item.Path == @"C:\Corporativo\Example folder\Example txt file.txt" && item.Action == "deleted"), "Delete sintetico do primeiro arquivo deveria aparecer.");
    Assert(display.Any(item => item.Path == @"C:\Corporativo\Example folder\Another example txt file.txt" && item.Action == "deleted"), "Delete sintetico do segundo arquivo deveria aparecer.");
}

static void TimelineSuppressesDeleteEchoAroundRename()
{
    var timestamp = DateTimeOffset.UtcNow;
    var projector = new EventTimelineProjector();
    var events = new[]
    {
        BuildTimelineEvent(timestamp, "deleted", @"C:\Corporativo\codex-client-check-02.md", source: "windows-security-log"),
        BuildTimelineEvent(timestamp.AddMilliseconds(300), "renamed", @"C:\Corporativo\codex-client-check-02 - rename.md", previousPath: @"C:\Corporativo\codex-client-check-02.md", source: "usn-journal+security-log")
    };

    var display = projector.BuildDisplayEvents(events).ToArray();

    Assert(display.Length == 1, "Delete usado como eco de rename nao deveria aparecer na timeline final.");
    Assert(display[0].Action == "renamed", "O rename deveria ser preservado.");
    Assert(display[0].PreviousPath == @"C:\Corporativo\codex-client-check-02.md", "Caminho anterior do rename deveria ser preservado.");
}

static void TimelineKeepsRealModificationBeforeDelete()
{
    var timestamp = DateTimeOffset.Parse("2026-07-02T10:26:55Z");
    var path = @"C:\Corporativo\RH\Novo(a) Documento de Texto - Copia (6).txt";
    var projector = new EventTimelineProjector();
    var events = new[]
    {
        BuildTimelineEvent(timestamp, "modified", path, source: "usn-journal+security-log"),
        BuildTimelineEvent(timestamp.AddSeconds(30), "deleted", path, source: "windows-security-log")
    };

    var display = projector.BuildDisplayEvents(events).OrderBy(item => item.TimestampUtc).ToArray();

    Assert(display.Length == 2, "Alteracao real seguida de exclusao deveria manter os dois eventos.");
    Assert(display[0].Action == "modified", "Primeiro evento deveria continuar sendo alteracao.");
    Assert(display[0].DisplayAction == "Alterado", "Alteracao real nao deveria virar criacao.");
    Assert(display[1].Action == "deleted", "Exclusao posterior deveria ser preservada.");
}

static void TimelineKeepsRealModificationBeforeAccess()
{
    var timestamp = DateTimeOffset.Parse("2026-07-02T10:40:47Z");
    var path = @"C:\Corporativo\RH\Novo(a) Documento de Texto - Copia (3).txt";
    var siblingPath = @"C:\Corporativo\RH\codex-created-nearby.txt";
    var projector = new EventTimelineProjector();
    var events = new[]
    {
        BuildTimelineEvent(timestamp, "modified", path, source: "windows-security-log"),
        BuildTimelineEvent(timestamp.AddSeconds(1), "created", siblingPath, source: "usn-journal+security-log"),
        BuildTimelineEvent(timestamp.AddSeconds(8), "accessed", path, source: "usn-journal+security-log")
    };

    var display = projector.BuildDisplayEvents(events).OrderBy(item => item.TimestampUtc).ToArray();
    var targetEvents = display.Where(item => item.Path == path).ToArray();

    Assert(targetEvents.Length == 1, "Acesso posterior a alteracao deveria ser tratado como eco.");
    Assert(targetEvents[0].Action == "modified", "Alteracao real nao deveria virar criacao quando depois chega acesso.");
    Assert(targetEvents[0].DisplayAction == "Alterado", "Evento final do arquivo deveria permanecer Alterado.");
}

static void TimelineTreatsSecurityTextAppendCreateAsModification()
{
    var timestamp = DateTimeOffset.Parse("2026-07-02T10:50:12Z");
    var path = @"C:\Corporativo\RH\Novo(a) Documento de Texto - Copia (2).txt";
    var projector = new EventTimelineProjector();
    var events = new[]
    {
        BuildTimelineEvent(timestamp, "created", path, source: "windows-security-log"),
        BuildTimelineEvent(timestamp.AddSeconds(6), "accessed", path, source: "usn-journal+security-log")
    };

    var display = projector.BuildDisplayEvents(events).OrderBy(item => item.TimestampUtc).ToArray();

    Assert(display.Length == 1, "Append de texto seguido de acesso deveria aparecer como uma unica alteracao.");
    Assert(display[0].Action == "modified", "Criacao/append do Security Log em arquivo texto existente deveria virar alteracao.");
    Assert(display[0].DisplayAction == "Alterado", "Evento final deveria aparecer como Alterado.");
}

static void TimelineCollapsesSecurityTextAppendAndModifyDuplicate()
{
    var timestamp = DateTimeOffset.Parse("2026-07-03T01:26:01Z");
    var path = @"C:\Corporativo\DTI\Novo(a) Documento de Texto - Copia (0).txt";
    var projector = new EventTimelineProjector();
    var events = new[]
    {
        BuildTimelineEvent(timestamp.AddMilliseconds(440), "created_or_appended", path, source: "windows-security-log"),
        BuildTimelineEvent(timestamp.AddMilliseconds(443), "modified", path, source: "windows-security-log"),
        BuildTimelineEvent(timestamp.AddSeconds(9), "accessed", path, source: "usn-journal+security-log")
    };

    var display = projector.BuildDisplayEvents(events).OrderBy(item => item.TimestampUtc).ToArray();

    Assert(display.Length == 1, "Append e modified do Security Log no mesmo arquivo deveriam virar uma unica alteracao.");
    Assert(display[0].Action == "modified", "Evento restante deveria ser alteracao.");
    Assert(display[0].DisplayAction == "Alterado", "Evento restante deveria aparecer como Alterado.");
}

static void TimelineTreatsSecurityTextAppendWithNearbyModifyAsModification()
{
    var timestamp = DateTimeOffset.Parse("2026-07-03T01:41:18Z");
    var path = @"C:\Corporativo\DTI\Novo(a) Documento de Texto - Copia (3).txt";
    var projector = new EventTimelineProjector();
    var events = new[]
    {
        BuildTimelineEvent(timestamp.AddMilliseconds(943), "created_or_appended", path, source: "windows-security-log"),
        BuildTimelineEvent(timestamp.AddMilliseconds(947), "modified", path, source: "windows-security-log"),
        BuildTimelineEvent(timestamp, "accessed", path, source: "usn-journal+security-log"),
        BuildTimelineEvent(timestamp.AddSeconds(8), "modified", path, source: "usn-journal", user: "UNKNOWN")
    };

    var display = projector.BuildDisplayEvents(events).OrderBy(item => item.TimestampUtc).ToArray();

    var targetEvents = display.Where(item => item.Path == path).ToArray();

    Assert(targetEvents.Length == 1, "Append de texto com modified vizinho deveria aparecer como uma unica alteracao, sem acesso eco.");
    Assert(targetEvents[0].Action == "modified", "Evento final deveria ser alteracao.");
    Assert(targetEvents[0].DisplayAction == "Alterado", "Evento final deveria aparecer como Alterado.");
}

static void TimelineCollapsesRenameDuplicateAfterUserResolution()
{
    var timestamp = DateTimeOffset.UtcNow;
    var projector = new EventTimelineProjector();
    var events = new[]
    {
        BuildTimelineEvent(timestamp, "renamed", @"C:\Corporativo\Nova pasta - 10", previousPath: @"C:\Corporativo\Nova pasta", objectType: "folder", source: "windows-security-log"),
        BuildTimelineEvent(timestamp, "renamed", @"C:\Corporativo\Nova pasta - 10", previousPath: @"C:\Corporativo\Nova pasta", objectType: "folder", source: "usn-journal", user: "UNKNOWN")
    };

    var display = projector.BuildDisplayEvents(events).ToArray();

    Assert(display.Length == 1, "Rename duplicado entre Security Log e USN deveria aparecer uma unica vez depois de resolver usuario.");
    Assert(display[0].Action == "renamed", "Evento preservado deveria continuar sendo rename.");
    Assert(display[0].Source == "usn-journal", "Quando ambos descrevem a mesma transicao, a entrada USN deve ser preferida.");
    Assert(display[0].User == @"FILESERVER\AnphibiO", "Rename USN preservado deveria herdar usuario do Security Log.");
}

static void TimelineKeepsRenameBetweenWindowsDefaultNames()
{
    var timestamp = DateTimeOffset.Parse("2026-07-02T19:38:19Z");
    var previousPath = @"C:\Corporativo\Financeiro\Novo(a) Documento de Texto - Copia (1).txt";
    var nextPath = @"C:\Corporativo\Financeiro\Novo(a) Documento de Texto - Copia (1) - 10.txt";
    var projector = new EventTimelineProjector();
    var events = new[]
    {
        BuildTimelineEvent(timestamp, "renamed", nextPath, previousPath, source: "usn-journal", user: "UNKNOWN"),
        BuildTimelineEvent(timestamp.AddMilliseconds(240), "accessed", nextPath),
        BuildTimelineEvent(timestamp.AddMilliseconds(240), "deleted", previousPath)
    };

    var display = projector.BuildDisplayEvents(events).ToArray();
    var renamed = display.SingleOrDefault(item => item.Action == "renamed");

    if (renamed is null)
    {
        throw new InvalidOperationException("Rename entre nomes padrao do Windows deveria permanecer como renomeado.");
    }

    Assert(renamed.Path == nextPath, "Rename deveria apontar para o novo nome.");
    Assert(renamed.PreviousPath == previousPath, "Rename deveria preservar o nome anterior.");
    Assert(display.All(item => item.Action != "created"), "Rename entre nomes padrao nao deveria virar criacao.");
}

static IReadOnlyCollection<FileAuditEvent> BuildEvents(string action, int count)
{
    return Enumerable.Range(1, count)
        .Select(index => BuildEvent(action, $"\\\\FS01\\Dados\\arquivo-{index}.txt", ".txt"))
        .ToArray();
}

static FileAuditEvent BuildEvent(string action, string path, string extension)
{
    return new FileAuditEvent(
        Id: Guid.NewGuid(),
        TimestampUtc: DateTimeOffset.UtcNow,
        Server: "FS01",
        Share: "Dados",
        Path: path,
        PreviousPath: null,
        ObjectType: "file",
        Action: action,
        User: "EMPRESA\\usuario.teste",
        Sid: null,
        SourceHost: "WKS-001",
        SourceIp: "192.168.1.10",
        ProcessName: "explorer.exe",
        FileSizeBytes: 100,
        Extension: extension,
        Result: "success",
        Severity: "info",
        Source: "test");
}

static FileAuditEvent BuildTimelineEvent(
    DateTimeOffset timestampUtc,
    string action,
    string path,
    string? previousPath = null,
    string objectType = "file",
    string source = "usn-journal+security-log",
    string user = @"FILESERVER\AnphibiO")
{
    return new FileAuditEvent(
        Id: Guid.NewGuid(),
        TimestampUtc: timestampUtc,
        Server: "FileServer",
        Share: "Corporativo",
        Path: path,
        PreviousPath: previousPath,
        ObjectType: objectType,
        Action: action,
        User: user,
        Sid: null,
        SourceHost: null,
        SourceIp: null,
        ProcessName: "explorer.exe",
        FileSizeBytes: null,
        Extension: Path.GetExtension(path),
        Result: "success",
        Severity: "info",
        Source: source);
}

static CollectedFileEvent BuildCollectedEvent(
    string cursorType,
    DateTimeOffset timestampUtc,
    string path,
    string user,
    string source,
    string processName,
    string action = "modified",
    long? recordId = null,
    long? usn = null,
    string? previousPath = null,
    string? fileReferenceId = null)
{
    return new CollectedFileEvent(
        CursorType: cursorType,
        RecordId: cursorType == "security" ? recordId ?? Random.Shared.NextInt64(1, 1000) : null,
        Usn: cursorType == "usn" ? usn ?? Random.Shared.NextInt64(1, 1000) : null,
        Volume: cursorType == "usn" ? "D:" : null,
        TimestampUtc: timestampUtc,
        Server: "FS01",
        Share: "Dados",
        Path: path,
        PreviousPath: previousPath,
        ObjectType: "file",
        Action: action,
        User: user,
        Sid: "S-1-5-21-1",
        SourceHost: "WKS-001",
        SourceIp: "192.168.1.10",
        ProcessName: processName,
        FileSizeBytes: null,
        Extension: ".xlsx",
        FileReferenceId: fileReferenceId,
        Result: "success",
        Severity: "info",
        Source: source);
}

static FileInventoryItem BuildInventoryItem(
    Guid snapshotId,
    DateTimeOffset scannedAtUtc,
    string path,
    string itemType,
    long sizeBytes,
    DateTimeOffset? modifiedUtc = null)
{
    return FileInventoryNormalizer.Normalize(new FileInventoryItemInput(
        SnapshotId: snapshotId,
        ScannedAtUtc: scannedAtUtc,
        Server: "FileServer",
        Share: "Corporativo",
        RootPath: @"C:\Corporativo",
        Path: path,
        RelativePath: null,
        Name: null,
        ItemType: itemType,
        SizeBytes: sizeBytes,
        CreatedUtc: modifiedUtc,
        ModifiedUtc: modifiedUtc,
        AccessedUtc: null,
        Error: null));
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
