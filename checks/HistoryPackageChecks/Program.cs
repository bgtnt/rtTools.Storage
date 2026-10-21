using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using HistoryAcceptance;
using RtTools;
using RtTools.Logger;

if (args.Length != 2)
    throw new ArgumentException("Usage: HistoryPackageChecks <run-data root> <result-json>");

var context = new CheckContext(args[0], "Patched");
var completedSuites = new List<string>();
Directory.CreateDirectory(context.DataRoot);
Assembly engine = typeof(TimeLog<byte[]>).Assembly;
context.Run("package.engine-identity", () =>
{
    Check.Equal("rtTools.Storage", engine.GetName().Name, "Wrong package assembly was loaded.");
    Check.That(ReferenceEquals(engine, typeof(Log<Record<Key.KeyInt64>, Key.KeyInt64>).Assembly) &&
        ReferenceEquals(engine, typeof(DictionaryPersistent<int, string>).Assembly),
        "State and history must come from the same package assembly.");
    return engine.FullName ?? throw new InvalidDataException("Missing package assembly identity.");
});

RunSuite("acceptance-gate", AcceptanceGateChecks.Contracts);
RunSuite("timelog", TimeLogChecks.Contracts);
RunSuite("timelog-utc", TimeLogUtcChecks.Contracts);
RunSuite("timelog-null", TimeLogNullChecks.Contracts);
RunSuite("log", LogChecks.Contracts);
RunSuite("timelog-flush", TimeLogFlushChecks.Contracts);
RunSuite("resource-lifetime", ResourceLifetimeChecks.Contracts);
RunSuite("stream-persistence", StreamPersistenceChecks.Contracts);
RunSuite("buffered-queue", BufferedQueueChecks.Contracts);
RunSuite("dictionary-logger", DictionaryLoggerChecks.Contracts);
RunSuite("timelog-client-errors", TimeLogClientExceptionChecks.Contracts);
RunSuite("collection-lifetime", CollectionLifetimeChecks.Contracts);
RunSuite("storage-bounds", DataStorageBoundsChecks.Contracts);
RunSuite("storage-failures", DataStorageFailureChecks.Contracts);
RunSuite("synchronized-storage-failures", SynchronizedStorageFailureChecks.Contracts);
RunSuite("wrapper-storage-failures", WrapperStorageFailureChecks.Contracts);

string resultPath = Path.GetFullPath(args[1]);
Directory.CreateDirectory(Path.GetDirectoryName(resultPath)!);
var report = new
{
    Status = context.Failed ? "FAIL" : "PASS",
    Mode = "package-history-contracts",
    CompletedSuites = completedSuites,
    RecordedUtc = DateTimeOffset.UtcNow,
    DataRoot = context.DataRoot,
    Runtime = RuntimeInformation.FrameworkDescription,
    OS = RuntimeInformation.OSDescription,
    Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
    LocalTimeZone = TimeZoneInfo.Local.Id,
    Engine = new
    {
        Identity = engine.FullName,
        InformationalVersion = engine.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
        Path = engine.Location,
        Sha256 = Hash(engine.Location)
    },
    Harness = new { Path = Assembly.GetExecutingAssembly().Location, Sha256 = Hash(Assembly.GetExecutingAssembly().Location) },
    Limitations = new[]
    {
        "PackageReference-only consumption; no original repository or engine source project is referenced",
        "UTC regression needs a nonzero local offset; CI uses TZ=Europe/Sofia",
        "Flush probes inject synthetic exceptions into separate empty scratch streams; no OS/disk/crash/durability guarantee"
    },
    Cases = context.Results
};
File.WriteAllText(resultPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Package history checks {report.Status}: {context.Results.Count} groups; report={resultPath}");
return context.Failed ? 1 : 0;

void RunSuite(string name, Action<CheckContext> run)
{
    int before = context.Results.Count;
    run(context);
    if (context.Results.Count == before)
        throw new InvalidDataException("The package check suite did not record any cases: " + name);
    completedSuites.Add(name);
}

static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
