using System.ComponentModel;

namespace RtTools;

/// <summary>
/// An object whose persistence location is managed by StreamPersistence.
/// PersistId is store-local metadata, not a stable application identifier.
/// </summary>
public interface IPersistent
{
    /// <summary>Whether PersistId currently identifies a stored record (normally PersistId &gt;= 0).</summary>
    bool IsPersistent { get; }

    /// <summary>
    /// Current record location. Initialize to long.MinValue; updates can relocate it and removal resets it.
    /// An object must be used with its owning store only.
    /// </summary>
    [Browsable(false)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    long PersistId { get; set; }
}
