namespace HistoryAcceptance;

internal sealed record CaseResult(string Id, string Kind, string Status, string Details, string? Exception);

internal sealed class CheckContext(string dataRoot, string timeLogVariant = "Original")
{
    public string DataRoot { get; } = Path.GetFullPath(dataRoot);
    public string TimeLogVariant { get; } = timeLogVariant;
    public List<CaseResult> Results { get; } = [];
    public bool Failed => Results.Any(result => result.Status == "FAIL" ||
        (result.Kind == "Contract" && result.Status == "INCONCLUSIVE" &&
         result.Id.StartsWith("timelog.utc-normalization.", StringComparison.Ordinal)));

    public void Run(string id, Func<string> body, bool characterization = false)
    {
        string kind = characterization ? "Characterization" : "Contract";
        try
        {
            string details = body();
            string status = details.StartsWith("INCONCLUSIVE", StringComparison.Ordinal)
                ? "INCONCLUSIVE" : characterization ? "CONFIRMED" : "PASS";
            Results.Add(new(id, kind, status, details, null));
            Console.WriteLine($"{kind} {id} {status}: {details}");
        }
        catch (Exception error)
        {
            Results.Add(new(id, kind, "FAIL", error.Message, error.ToString()));
            Console.WriteLine($"{kind} {id} FAIL: {error}");
        }
    }
}

internal static class Check
{
    public static void That(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    public static void Equal<T>(T expected, T actual, string message) =>
        That(EqualityComparer<T>.Default.Equals(expected, actual), $"{message} Expected={expected}; actual={actual}.");

    public static TException Throws<TException>(Action action, string message) where TException : Exception
    {
        try { action(); }
        catch (TException error) { return error; }
        throw new InvalidDataException(message + " Expected exception: " + typeof(TException).Name);
    }
}
