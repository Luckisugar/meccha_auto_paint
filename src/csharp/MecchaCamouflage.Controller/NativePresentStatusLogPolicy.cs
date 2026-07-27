namespace MecchaCamouflage.Controller;

public sealed record NativePresentStatusLogSample
{
    public string Status { get; init; } = "unknown";
    public string Reason { get; init; } = "";
    public string Format { get; init; } = "";
    public string CaptureStatus { get; init; } = "unknown";
    public ulong CaptureAgeMs { get; init; }
    public ulong HudRebinds { get; init; }
    public uint HiderRosterCount { get; init; }
    public uint HunterRosterCount { get; init; }
    public string RosterSource { get; init; } = "unknown";
    public uint RosterCount { get; init; }
    public uint ValidPawns { get; init; }
    public uint CapsuleComponents { get; init; }
    public uint CapsuleTransforms { get; init; }
    public uint CapsuleSizes { get; init; }
    public uint CapsuleProjected { get; init; }
    public ulong SkeletonContracts { get; init; }
    public uint PoseProfileMatches { get; init; }
    public uint PoseComponentSpace { get; init; }
    public uint PoseLocalSpace { get; init; }
    public uint PoseBones { get; init; }
    public uint PoseEdges { get; init; }
    public uint Poses { get; init; }
    public uint Players { get; init; }
    public uint Lines { get; init; }
    public uint Texts { get; init; }
    public ulong Vertices { get; init; }
    public ulong GlyphQuads { get; init; }
    public double ProjectionScaleX { get; init; } = 1.0;
    public double ProjectionScaleY { get; init; } = 1.0;
    public ulong ProjectionCalibrations { get; init; }
    public ulong SnapshotSequence { get; init; }
    public ulong SubmittedFrames { get; init; }
    public ulong CompletedFences { get; init; }
    public ulong RenderedFrames { get; init; }
}

public static class NativePresentStatusLogPolicy
{
    public static string Signature(NativePresentStatusLogSample sample, bool verbose)
    {
        var stable = string.Join('|',
            sample.Status,
            ReasonState(sample),
            sample.Format,
            sample.CaptureStatus,
            sample.HudRebinds,
            Presence((ulong)sample.HiderRosterCount + sample.HunterRosterCount),
            sample.RosterSource,
            Presence(sample.RosterCount),
            Coverage(sample.ValidPawns, sample.RosterCount),
            Coverage(sample.CapsuleComponents, sample.ValidPawns),
            Coverage(sample.CapsuleTransforms, sample.CapsuleComponents),
            Coverage(sample.CapsuleSizes, sample.CapsuleTransforms),
            Presence(sample.SkeletonContracts),
            Coverage(sample.PoseProfileMatches, sample.ValidPawns),
            Coverage((ulong)sample.PoseComponentSpace + sample.PoseLocalSpace, sample.PoseProfileMatches),
            Presence(sample.PoseBones),
            Coverage(sample.Poses, sample.PoseProfileMatches),
            ProjectionState(sample),
            Presence(sample.SnapshotSequence),
            Presence(sample.SubmittedFrames),
            Presence(sample.CompletedFences),
            Presence(sample.RenderedFrames));

        if (!verbose)
            return stable;

        return string.Join('|',
            stable,
            sample.CaptureAgeMs,
            sample.RosterCount,
            sample.ValidPawns,
            sample.CapsuleComponents,
            sample.CapsuleTransforms,
            sample.CapsuleSizes,
            sample.CapsuleProjected,
            sample.SkeletonContracts,
            sample.PoseProfileMatches,
            sample.PoseComponentSpace,
            sample.PoseLocalSpace,
            sample.PoseBones,
            sample.PoseEdges,
            sample.Poses,
            sample.Players,
            sample.Lines,
            sample.Texts,
            sample.Vertices,
            sample.GlyphQuads,
            Math.Round(sample.ProjectionScaleX, 4),
            Math.Round(sample.ProjectionScaleY, 4),
            sample.ProjectionCalibrations,
            sample.SnapshotSequence,
            sample.SubmittedFrames,
            sample.CompletedFences,
            sample.RenderedFrames);
    }

    public static bool ShouldWarn(NativePresentStatusLogSample sample) =>
        string.Equals(sample.Status, "unavailable", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(sample.CaptureStatus, "stalled", StringComparison.OrdinalIgnoreCase);

    private static string Presence(ulong value) => value == 0 ? "none" : "present";

    private static string ReasonState(NativePresentStatusLogSample sample) =>
        string.Equals(sample.Status, "ready", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(sample.CaptureStatus, "active", StringComparison.OrdinalIgnoreCase)
            ? "healthy"
            : sample.Reason;

    private static string Coverage(ulong value, ulong expected)
    {
        if (expected == 0)
            return value == 0 ? "idle" : "unexpected";
        if (value == 0)
            return "none";
        return value < expected ? "partial" : "complete";
    }

    private static string ProjectionState(NativePresentStatusLogSample sample)
    {
        if (sample.ProjectionCalibrations == 0)
            return "uncalibrated";
        return double.IsFinite(sample.ProjectionScaleX) &&
               double.IsFinite(sample.ProjectionScaleY) &&
               sample.ProjectionScaleX > 0 &&
               sample.ProjectionScaleY > 0
            ? "calibrated"
            : "invalid";
    }
}
