using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using RtTools;

namespace HistoryAcceptance;

internal static class ConstructorListenerCleanupChecks
{
    private static readonly DataStorage.OnException[] Policies = [DataStorage.OnException.Default, DataStorage.OnException.CloseAndThrow, DataStorage.OnException.Throw];

    // Exact 12-condition preview.4 probe matrix: six old failures (four closing-policy
    // cleanup failures and two Throw-only primary-precedence failures), six controls.
    public static void BaselineContracts(CheckContext ctx)
    {
        foreach (bool synchronized in new[] { false, true })
        foreach (var policy in Policies)
        foreach (bool secondary in new[] { true, false })
        {
            string id = "constructor-listener." + Owner(synchronized) + "." + policy + "." + (secondary ? "failed-secondary" : "failed-success");
            ctx.Run(id, () =>
            {
                var observed = Observe(synchronized, policy, secondary ? "Failed" : "None");
                AssertApplicationFailure(observed, policy);
                return "Exact application primary A retained; Failed notification " + (secondary ? "B cannot bypass configured cleanup or mask A" : "succeeds") + "; source health stays unfaulted and close attempts follow Default/CloseAndThrow/Throw policy. Baseline uses distinct A/B IOException objects and observes before manual fallback cleanup.";
            });
        }
    }

    public static void Contracts(CheckContext ctx)
    {
        BaselineContracts(ctx);

        ctx.Run("constructor-listener.error-reporting-notifications", () =>
        {
            foreach (bool synchronized in new[] { false, true })
            foreach (var policy in Policies)
            foreach (string stage in new[] { "Closed", "Throw" })
            {
                if (stage == "Closed" && policy == DataStorage.OnException.Throw) continue;
                AssertApplicationFailure(Observe(synchronized, policy, stage), policy);
            }
            return "Secondary Closed/Throw error-reporting notification exceptions cannot replace A; both owners preserve exact primary identity, source classification and one/zero close policy.";
        });

        ctx.Run("constructor-listener.real-file-close-and-reopen", () =>
        {
            foreach (bool synchronized in new[] { false, true })
            foreach (var policy in new[] { DataStorage.OnException.Default, DataStorage.OnException.CloseAndThrow })
            {
                string directory = Path.Combine(ctx.DataRoot, "constructor-listener");
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".bin");
                byte[] sentinel = EmptyImage();
                File.WriteAllBytes(path, sentinel);
                using var stream = new CloseCountingFileStream(path);
                DataStorage? captured = null;
                var primary = new IOException("file-listener-A");
                var secondary = new IOException("file-listener-B");
                Exception? caught = null;
                int failedNotifications = 0;
                int secondaryThrows = 0;
                bool reopened = false;
                int closes;
                try
                {
                    try
                    {
                        using IDisposable owner = Open(synchronized, stream, policy, (state, data) =>
                        {
                            if (SuccessfulOpened(state)) { captured = (DataStorage)data; throw primary; }
                            if (InitialFailed(state)) { failedNotifications++; secondaryThrows++; throw secondary; }
                        });
                    }
                    catch (Exception error) { caught = error; }
                    Check.Equal(1, failedNotifications, "Real-file Failed notification must be invoked exactly once.");
                    Check.Equal(1, secondaryThrows, "Real-file Failed notification must actually throw B once.");
                    closes = stream.CloseCalls;
                    try { using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); reopened = true; }
                    catch (IOException) { }
                    Check.That(reopened, "Configured constructor cleanup must release the real exclusive file handle before returning failure.");
                    Check.Equal(1, closes, "Real source close attempt count.");
                    Check.That(ReferenceEquals(primary, caught), "Real file path retains exact application A.");
                    Check.That(captured != null && !captured.IsFaulted && captured.StorageException == null, "Application notifications must not be classified as source faults.");
                    Check.That(sentinel.AsSpan().SequenceEqual(File.ReadAllBytes(path)), "Constructor listener rejection preserves exact sentinel file bytes.");
                }
                finally { if (stream.CanRead || stream.CanWrite) stream.Dispose(); }
            }
            return "Both owners and both closing policies close a real FileShare.None source exactly once, permit immediate exclusive reopen and retain exact sentinel bytes after A then B.";
        });

        ctx.Run("constructor-listener.abort-cleanup-source-precedence", () =>
        {
            foreach (bool synchronized in new[] { false, true })
            foreach (var policy in new[] { DataStorage.OnException.Default, DataStorage.OnException.CloseAndThrow })
            {
                var closeError = new IOException("actual-source-close-C");
                var observed = Observe(synchronized, policy, "Failed", closeError: closeError);
                Check.That(ReferenceEquals(closeError, observed.Caught), "Actual Close C has first-source precedence over application A/B.");
                Check.Equal(1, observed.CloseCalls, "A/B abort cleanup attempts source Close only once even when C throws.");
                Check.That(observed.Core != null && observed.Core.IsFaulted && ReferenceEquals(closeError, observed.Core.StorageException), "Only actual source C is latched as StorageException.");
                Check.That(!observed.OpenBeforeFallback, "Controlled failing Close still releases its underlying source.");
            }
            return "Distinct application A/B followed by actual Close C propagates exact C, latches only C and closes once; unified first-source precedence applies to abort cleanup.";
        });

        ctx.Run("constructor-listener.format-primary-failed-secondary", () =>
        {
            foreach (bool synchronized in new[] { false, true })
            foreach (var policy in Policies)
            {
                var observed = Observe(synchronized, policy, "Failed", image: MissingFirstHeader(), failOpened: false);
                Check.That(observed.Primary is DataStorage.DataSourceException && ReferenceEquals(observed.Primary, observed.Caught), "Malformed-format primary remains exact A when Failed notification throws B.");
                AssertClosePolicy(observed, policy);
                Check.Equal(0, observed.Writes, "Format rejection plus notification failure must not write.");
                Check.Equal(0, observed.Resizes, "Format rejection plus notification failure must not resize.");
            }
            return "A real malformed-header primary is retained over Failed notification B; closing/Throw-only policies remain explicit and bytes are unchanged.";
        });

        ctx.Run("constructor-listener.secondary-aborts-recover-clear", () =>
        {
            byte[] image = RecoverableImage();
            foreach (bool synchronized in new[] { false, true })
            foreach (var policy in new[] { DataStorage.OnException.RecoverOrClear, DataStorage.OnException.RecoverOrThrow })
            {
                var observed = Observe(synchronized, policy, "Failed", image: image, failOpened: false);
                Check.That(ReferenceEquals(observed.Primary, observed.Caught), "Secondary notification abort retains primary format exception.");
                AssertClosePolicy(observed, policy);
                Check.Equal(0, observed.Writes, "Failed notification aborts before recovery/clear writes.");
                Check.Equal(0, observed.Resizes, "Failed notification aborts before recovery/clear resize.");
                Check.That(image.AsSpan().SequenceEqual(observed.Bytes), "Recoverable sentinel is unchanged after notification abort.");
            }
            return "Failed notification B aborts RecoverOrClear/RecoverOrThrow immediately, retaining prior abort behavior while preserving A and the configured close policy; no repair or clear is attempted.";
        });

        ctx.Run("constructor-listener.normal-recover-clear-controls", () =>
        {
            foreach (bool synchronized in new[] { false, true })
            foreach (bool recovery in new[] { false, true })
            {
                using var stream = new ProbeStream(recovery ? RecoverableImage() : MissingFirstHeader());
                using IDisposable owner = Open(synchronized, stream, recovery ? DataStorage.OnException.RecoverOrThrow : DataStorage.OnException.Clear, (_, _) => { });
                DataStorage core = owner is DataStorage direct ? direct : ((SynchronizedDataStorage)owner).Storage;
                Check.Equal(recovery ? 1 : 0, core.Count, "Successful notifications retain the selected recovery/clear result.");
                Check.That(!core.IsFaulted && core.StorageException == null, "Successful policy handling remains unfaulted.");
                Check.That(stream.Writes > 0 || stream.Resizes > 0, "Selected recovery/clear control really modifies the malformed fixture.");
            }
            return "When notifications succeed, both owners retain existing explicit Clear and successful RecoverOrThrow behavior; this adds no general recovery guarantee.";
        });

        ctx.Run("constructor-listener.same-primary-stack", () =>
        {
            foreach (bool synchronized in new[] { false, true })
            foreach (var policy in Policies)
            {
                var observed = Observe(synchronized, policy, "SamePrimary");
                AssertApplicationFailure(observed, policy);
                Check.That(observed.Caught!.StackTrace?.Contains(nameof(ThrowPrimary), StringComparison.Ordinal) == true,
                    "Primary EDI must be captured before a listener rethrows the same A object and overwrites its stack.");
            }
            return "Rethrowing the same A object from Failed preserves the original primary throw frame because EDI is captured before secondary notification.";
        });

        ctx.Run("constructor-listener.failed-listener-caught-source-fault", () =>
        {
            foreach (bool synchronized in new[] { false, true })
            {
                var source = new IOException("actual-source-flush-C");
                var observed = Observe(synchronized, DataStorage.OnException.Default, "FailedCatchSource", sourceError: source);
                Check.That(ReferenceEquals(source, observed.Caught), "A source failure caught by Failed notification retains first-source precedence over later B.");
                Check.That(observed.Core != null && observed.Core.IsFaulted && ReferenceEquals(source, observed.Core.StorageException), "Actual source fault remains latched.");
                Check.Equal(1, observed.CloseCalls, "Faulted constructor still closes its owned source once.");
            }
            return "Failed notification catches an actual source Flush C then throws B; C remains the caller-visible first source failure and cleanup occurs once.";
        });

        ctx.Run("constructor-listener.normal-format-close-source-control", () =>
        {
            foreach (bool synchronized in new[] { false, true })
            {
                var closing = new IOException("normal-format-close-C");
                var observed = Observe(synchronized, DataStorage.OnException.Default, "None", image: MissingFirstHeader(), failOpened: false, closeError: closing);
                Check.That(ReferenceEquals(closing, observed.Caught), "Existing normal format-policy Close C still propagates unchanged.");
                Check.Equal(1, observed.CloseCalls, "Normal format-policy Close failure is attempted once.");
            }
            return "Without a secondary notification failure, the existing format-policy actual Close C behavior remains unchanged and attempts Close once.";
        });
    }

    private static Observation Observe(bool synchronized, DataStorage.OnException policy, string stage,
        byte[]? image = null, bool failOpened = true, IOException? closeError = null, IOException? sourceError = null)
    {
        using var stream = new ProbeStream(image ?? EmptyImage()) { CloseError = closeError };
        Exception primary = new IOException("application-listener-A");
        var secondary = new IOException("application-listener-B");
        DataStorage? core = null;
        Exception? caught = null;
        IDisposable? owner = null;
        bool firstFailed = true;
        bool secondaryThrown = false;
        int requestedStageInvocations = 0;
        int secondaryThrowCount = 0;
        try
        {
            void Listener(DataStorage.OpenState state, object data)
            {
                if (SuccessfulOpened(state) && failOpened) { core = (DataStorage)data; ThrowPrimary(primary); }
                if (InitialFailed(state) && firstFailed)
                {
                    firstFailed = false;
                    if (!failOpened) primary = (Exception)data;
                    if (stage == "SamePrimary") { requestedStageInvocations++; secondaryThrowCount++; throw primary; }
                    if (stage == "FailedCatchSource")
                    {
                        requestedStageInvocations++;
                        stream.FlushError = sourceError;
                        Check.That(core != null, "Opened callback captured the same core.");
                        Check.That(ReferenceEquals(sourceError, Check.Throws<IOException>(core!.Flush, "Failed listener catches actual source failure.")), "Injected source fault identity.");
                        secondaryThrowCount++;
                        throw secondary;
                    }
                    if (stage == "Failed") { requestedStageInvocations++; secondaryThrowCount++; throw secondary; }
                }
                bool isTarget = stage == "Closed" && ClosedNotification(state) ||
                    stage == "Throw" && (state & DataStorage.OpenState.Throw) == DataStorage.OpenState.Throw;
                if (isTarget)
                {
                    requestedStageInvocations++;
                    if (!secondaryThrown) { secondaryThrown = true; secondaryThrowCount++; throw secondary; }
                }
            }
            try { owner = Open(synchronized, stream, policy, Listener); }
            catch (Exception error) { caught = error; }
            var observed = new Observation(primary, secondary, caught, core, stream.Closes, stream.CanRead && stream.CanWrite,
                stream.Writes, stream.Resizes, stream.ToArray(), stage, requestedStageInvocations, secondaryThrowCount);
            AssertRequestedNotification(observed);
            return observed;
        }
        finally
        {
            stream.CloseError = null;
            stream.FlushError = null;
            owner?.Dispose();
            if (stream.CanRead || stream.CanWrite) stream.Dispose();
        }
    }

    private static void AssertApplicationFailure(Observation observed, DataStorage.OnException policy)
    {
        AssertClosePolicy(observed, policy);
        Check.That(ReferenceEquals(observed.Primary, observed.Caught), "Secondary application B must not replace exact primary A.");
        Check.That(!ReferenceEquals(observed.Primary, observed.Secondary), "A and B are distinct application exceptions.");
        Check.That(observed.Core != null && !observed.Core.IsFaulted && observed.Core.StorageException == null, "Application A/B must not be classified as physical source failures.");
    }
    private static void AssertRequestedNotification(Observation observed)
    {
        int expected = observed.RequestedStage == "None" ? 0 : 1;
        Check.Equal(expected, observed.RequestedStageInvocations, "Requested " + observed.RequestedStage + " notification invocation count (checked outside listener).");
        Check.Equal(expected, observed.SecondaryThrowCount, "Requested " + observed.RequestedStage + " notification must actually throw exactly once (None never throws).");
    }
    private static void AssertClosePolicy(Observation observed, DataStorage.OnException policy)
    {
        bool closing = policy == DataStorage.OnException.Default || (policy & DataStorage.OnException.CloseSource) != 0;
        Check.Equal(closing ? 1 : 0, observed.CloseCalls, "Configured constructor cleanup occurs exactly once; Throw-only remains nonclosing.");
        Check.Equal(!closing, observed.OpenBeforeFallback, "Source open state before manual fallback cleanup follows explicit policy.");
    }
    private static IDisposable Open(bool synchronized, Stream stream, DataStorage.OnException policy, DataStorage.OpenStateDelegate listener) =>
        synchronized ? new SynchronizedDataStorage(stream, onException: policy, listener: listener) : new DataStorage(stream, onException: policy, listener: listener);
    private static bool SuccessfulOpened(DataStorage.OpenState state) => (state & DataStorage.OpenState.Opened) != 0 && (state & DataStorage.OpenState.Failed) == 0;
    // State flags accumulate: a later Closed/Throw event must not impersonate a
    // missing Failed notification, nor Throw impersonate a missing Closed event.
    private static bool InitialFailed(DataStorage.OpenState state) => state == DataStorage.OpenState.Failed || state == (DataStorage.OpenState.Failed | DataStorage.OpenState.Opened);
    private static bool ClosedNotification(DataStorage.OpenState state) => state == DataStorage.OpenState.Closed || state == (DataStorage.OpenState.Closed | DataStorage.OpenState.Opened);
    private static string Owner(bool synchronized) => synchronized ? "synchronized" : "direct";
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowPrimary(Exception primary) => throw primary;

    private static byte[] EmptyImage() { byte[] image = new byte[12]; BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(4), long.MinValue); return image; }
    private static byte[] MissingFirstHeader() { byte[] image = new byte[12]; BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(4), 12); return image; }
    private static byte[] RecoverableImage()
    {
        byte[] image = new byte[62];
        BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(4), 12);
        BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(12), 0); // malformed first Prev
        BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(20), 37);
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(32), 1);
        image[36] = 1;
        BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(37), 12);
        BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(45), long.MinValue);
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(57), 1);
        image[61] = 2;
        return image;
    }
    private sealed record Observation(Exception Primary, Exception Secondary, Exception? Caught, DataStorage? Core,
        int CloseCalls, bool OpenBeforeFallback, int Writes, int Resizes, byte[] Bytes,
        string RequestedStage, int RequestedStageInvocations, int SecondaryThrowCount);

    private sealed class ProbeStream : MemoryStream
    {
        public int Closes { get; private set; }
        public int Writes { get; private set; }
        public int Resizes { get; private set; }
        public IOException? CloseError { get; set; }
        public IOException? FlushError { get; set; }
        public ProbeStream(byte[] image) { base.Write(image, 0, image.Length); base.Position = 0; }
        public override void Write(byte[] buffer, int offset, int count) { this.Writes++; base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { byte[] bytes = buffer.ToArray(); this.Write(bytes, 0, bytes.Length); }
        public override void SetLength(long value) { this.Resizes++; base.SetLength(value); }
        public override void Flush() { if (this.FlushError != null) throw this.FlushError; base.Flush(); }
        protected override void Dispose(bool disposing) { if (disposing) this.Closes++; base.Dispose(disposing); if (disposing && this.CloseError != null) throw this.CloseError; }
    }
    private sealed class CloseCountingFileStream(string path) : FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)
    {
        public int CloseCalls { get; private set; }
        protected override void Dispose(bool disposing) { if (disposing) this.CloseCalls++; base.Dispose(disposing); }
    }
}
