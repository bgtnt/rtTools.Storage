namespace RtTools;

/// <summary>
/// Defines an explicit persisted payload for StreamPersistence&lt;T&gt;.
/// Supply an explicit binary factory to reconstruct objects. Convenience constructors use an instance byte[] constructor.
/// </summary>
public interface IPersistentBinary : IPersistent
{
    /// <summary>Returns the non-null persisted payload. Transient state and PersistId can be omitted.</summary>
    byte[] ToBinary();
}
