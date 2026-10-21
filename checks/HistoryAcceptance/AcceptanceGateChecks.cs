namespace HistoryAcceptance;

internal static class AcceptanceGateChecks
{
    public static void Contracts(CheckContext context)
    {
        context.Run("timelog.gate.required-inconclusive", () =>
        {
            foreach (string season in new[] { "january", "july" })
            {
                var sample = new CheckContext(context.DataRoot);
                sample.Run($"timelog.utc-normalization.{season}.kind-matrix",
                    () => "INCONCLUSIVE: synthetic zero-offset host; this is a gate check, not timezone evidence.");
                Check.Equal("INCONCLUSIVE", sample.Results.Single().Status, "The original visible status must remain intact.");
                Check.That(sample.Failed, "A mandatory UTC regression without nonzero-offset proof cannot be accepted.");
            }
            return "Both required matrix INCONCLUSIVE outcomes reject aggregate acceptance while retaining their visible status; simulated gate inputs, no machine timezone change.";
        });

        context.Run("timelog.gate.characterization-inconclusive", () =>
        {
            var sample = new CheckContext(context.DataRoot);
            sample.Run("timelog.characterization.local-unspecified-end",
                () => "INCONCLUSIVE: synthetic optional characterization.", characterization: true);
            Check.Equal("INCONCLUSIVE", sample.Results.Single().Status, "Characterization status must remain visible.");
            Check.That(!sample.Failed, "An optional legacy characterization may remain inconclusive.");
            return "Legacy optional Characterization INCONCLUSIVE remains allowed and visible.";
        });

        context.Run("timelog.gate.conclusive-results", () =>
        {
            var sample = new CheckContext(context.DataRoot);
            sample.Run("timelog.utc-normalization.january.kind-matrix", () => "Conclusive regression result.");
            Check.That(!sample.Failed, "A conclusive required PASS must be accepted.");
            sample.Results.Add(new("ordinary-failure", "Contract", "FAIL", "Synthetic failure.", null));
            Check.That(sample.Failed, "Ordinary failures must still reject aggregate acceptance.");
            return "Conclusive required PASS is accepted; ordinary FAIL is still rejected.";
        });
    }
}
