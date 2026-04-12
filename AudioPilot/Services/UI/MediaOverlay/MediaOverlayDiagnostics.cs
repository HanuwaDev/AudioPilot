namespace AudioPilot.Services.UI.MediaOverlay
{
    internal readonly record struct MediaOverlayDelayAssistResult(
        bool CompletedWithinBudget,
        bool ObservedEvent,
        MediaEventAssistOutcome EventAssistOutcome = default);

    internal readonly record struct MediaOverlayPreferredSourceObservation(
        bool BrowserCandidateBlocked,
        string? BrowserBlockedReason,
        bool BrowserCandidateConverged,
        string? BrowserConvergenceReason,
        string? BrowserSourceAppUserModelId = null,
        string? BrowserTrackFingerprint = null,
        bool BrowserSameSourceConflictObserved = false,
        bool BrowserSameSourceConflictActive = false,
        int BrowserSameSourceDistinctCandidateCount = 0,
        int BrowserSameSourceActiveRivalCount = 0,
        int BrowserSameSourceReinforcedRivalCount = 0,
        int BrowserSameSourceStaleRivalCount = 0,
        bool BrowserConvergedAfterConflict = false,
        bool BrowserConvergedAfterStaleRival = false,
        bool BrowserFarPositionCorrectionWin = false,
        string? BrowserRivalReasonClasses = null,
        string? BrowserPromotionMode = null,
        long? CommandSequence = null,
        double? ElapsedMs = null);

    internal readonly record struct MediaOverlayTrackNavigationDiagnostics(
        string FinalPhase,
        string Outcome,
        string FinalChangeKind,
        bool SawSessionDrop,
        bool UsedSessionDropRecovery,
        bool UsedLateTrackLoadRecovery,
        bool UsedRecoveredAlternateSource,
        string FinalFallbackClassification,
        bool SameSourceConflictObserved = false,
        bool SameSourceConflictActive = false,
        int SameSourceDistinctCandidateCount = 0,
        int SameSourceActiveRivalCount = 0,
        int SameSourceReinforcedRivalCount = 0,
        int SameSourceStaleRivalCount = 0,
        long? CommandSequence = null,
        double? ElapsedMs = null);

    internal readonly record struct MediaOverlayPlayPauseDiagnostics(
        string FinalPath,
        string Outcome,
        bool UsedEventAssist,
        bool UsedChangedBySourceSnapshots,
        bool UsedImmediateCurrentEvidence,
        bool ReusedBaselineMetadata,
        long? CommandSequence = null,
        double? ElapsedMs = null);

    internal readonly record struct MediaOverlayPlayPauseResolutionResult(
        PlayPauseSnapshotResolution Resolution,
        MediaOverlayPlayPauseDiagnostics Diagnostics);
}
