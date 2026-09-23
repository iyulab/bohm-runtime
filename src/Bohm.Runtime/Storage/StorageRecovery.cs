namespace Bohm.Runtime.Storage;

/// <summary>Something <see cref="AppStorage.OpenAsync"/> had to repair or work around while loading.</summary>
public enum StorageRecoveryKind
{
    /// <summary>
    /// The journal ended in an incomplete line — a write that was interrupted before it was
    /// acknowledged. The partial line was discarded; nothing acknowledged was lost.
    /// </summary>
    TruncatedJournalTail,

    /// <summary>
    /// A journal line other than the last could not be read. Replay stopped there and the journal
    /// was set aside unchanged, so the unread operations can still be inspected by hand.
    /// </summary>
    CorruptJournal,

    /// <summary>
    /// Journal sequence numbers were not contiguous. Operations that should exist are missing,
    /// so data may have been lost.
    /// </summary>
    SequenceGap,

    /// <summary>A journaled operation repeated or went back in sequence; it was ignored.</summary>
    SequenceRegression,

    /// <summary>
    /// The current snapshot was missing or unreadable, so the newest readable previous snapshot
    /// was used instead. Any unreadable snapshot was set aside unchanged.
    /// </summary>
    UsedPreviousSnapshot,
}

/// <summary>One repair made while loading storage, with enough detail to explain it to a person.</summary>
/// <param name="Kind">What happened.</param>
/// <param name="Detail">A human-readable description, without stored keys or values.</param>
public sealed record StorageRecoveryEvent(StorageRecoveryKind Kind, string Detail);
