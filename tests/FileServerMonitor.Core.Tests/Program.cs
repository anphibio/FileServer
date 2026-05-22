using FileServerMonitor.Core;

var tests = new (string Name, Action Test)[]
{
    ("normaliza campos obrigatorios e defaults", NormalizesRequiredFieldsAndDefaults),
    ("dispara alerta de exclusao em massa", RaisesMassDeleteAlert),
    ("dispara alerta de alteracao de permissao", RaisesPermissionChangeAlert),
    ("dispara alerta de ransomware por extensao suspeita", RaisesRansomwareAlertBySuspiciousExtension),
    ("correlaciona USN com Security Log por caminho", CorrelatesUsnWithSecurityLogByPath),
    ("prefere melhor correspondencia por caminho", PrefersBestPathMatch),
    ("nao correlaciona fora da janela", DoesNotCorrelateOutsideWindow)
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
        Console.Error.WriteLine($"FAIL {name}: {ex.Message}");
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

static CollectedFileEvent BuildCollectedEvent(
    string cursorType,
    DateTimeOffset timestampUtc,
    string path,
    string user,
    string source,
    string processName)
{
    return new CollectedFileEvent(
        CursorType: cursorType,
        RecordId: cursorType == "security" ? Random.Shared.NextInt64(1, 1000) : null,
        Usn: cursorType == "usn" ? Random.Shared.NextInt64(1, 1000) : null,
        Volume: cursorType == "usn" ? "D:" : null,
        TimestampUtc: timestampUtc,
        Server: "FS01",
        Share: "Dados",
        Path: path,
        PreviousPath: null,
        ObjectType: "file",
        Action: "modified",
        User: user,
        Sid: "S-1-5-21-1",
        SourceHost: "WKS-001",
        SourceIp: "192.168.1.10",
        ProcessName: processName,
        FileSizeBytes: null,
        Extension: ".xlsx",
        Result: "success",
        Severity: "info",
        Source: source);
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
