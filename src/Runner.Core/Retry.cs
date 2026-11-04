namespace ToolRunner;

// "Run again from the failed step": a run that failed, was stopped or was put back after a crash starts again at its first
// step that did not finish (a step the run never reached is Skipped, so only Done counts as finished), and ends where it ended
// before (a run asked to go only "until" a step keeps that end). The place the player was marked at before the clean save goes
// along, so a run again that reaches the save to play still takes the player back there.
public static class Retry
{
	public static string? FromStep(Journal a_j) =>
		a_j.Unfinished || a_j.State == RunState.Finished ? null : a_j.Steps.FirstOrDefault(s => s.State != StepState.Done)?.Key;

	// null when the run has nothing left to run again
	public static RunRequest? Request(Journal a_j, string a_dataDir) =>
		FromStep(a_j) is { } from ? new RunRequest(a_j.SetupFile, a_dataDir, null, from, a_j.Steps[^1].Key, a_j.ReturnMarker) : null;
}
