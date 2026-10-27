using ToolRunner.Steps;

namespace ToolRunner;

// What a run will do, worked out before anything changes (his checklist 270, "all 3."): the tools that run and the mod each one
// makes, the mods switched off for the run and why, the ones a step replaces for good, the steps that start the game. Read off
// the profile as it is - nothing is written.
public sealed class RunPreview
{
	public sealed record StepLine(string Key, string Name, string? Output, bool OutputIsNew);

	public List<StepLine> Steps { get; } = [];
	public List<(string Mod, string Why)> OffForTheRun { get; } = [];
	public List<string> ReplacedForGood { get; } = [];
	public List<string> StartsTheGame { get; } = [];
	public bool ClosesMo2 { get; private init; }

	public static RunPreview Build(Setup a_setup, Mo2Instance a_mo2, IReadOnlyList<Setup.StepEntry> a_todo)
	{
		var control = new Mo2Control(a_mo2, a_setup.Profile, RunLog.Null);
		var profile = new Profile(a_mo2, a_setup.Profile, () => control.Running);
		var st = profile.States();
		var p = new RunPreview { ClosesMo2 = control.Running };
		foreach (var s in a_todo) {
			var ss = new StepSettings(s.Settings);
			p.Steps.Add(new StepLine(s.Key, StepCatalog.Name(s.Key), ss.OutputMod, ss.OutputMod is { } om && !st.ContainsKey(om)));
			if (s.Key is "shaders" or "cleansave" or "playsave" || (s.Key == "grass" && ss.Str("by") == "game")) {
				p.StartsTheGame.Add(StepCatalog.Name(s.Key));
			}
		}
		// the same list the run switches off (Runner.OutputsOff), only when a step past the shader caches runs
		if (a_todo.Any(s => s.Key is not "shaders" and not "playsave")) {
			var off = Runner.OutputsToTurnOff(a_setup, st);
			p.OffForTheRun.AddRange(off);
			foreach (var (m, why) in profile.Dependents(off.Select(o => o.Mod))) {
				p.OffForTheRun.Add((m, why));
			}
		}
		p.ReplacedForGood.AddRange(a_todo.SelectMany(s => new StepSettings(s.Settings).List("replaces"))
			.Where(m => st.TryGetValue(m, out var f) && f == '+').Distinct(StringComparer.OrdinalIgnoreCase));
		return p;
	}

	// in plain words, for the window's box and the command line; a_usual gives a step's usual time when there is one
	public List<string> Lines(Func<string, TimeSpan?> a_usual)
	{
		var l = new List<string> { "The tools that run, in this order:" };
		var total = TimeSpan.Zero;
		var untimed = 0;
		for (var i = 0; i < Steps.Count; ++i) {
			var s = Steps[i];
			var t = a_usual(s.Key);
			total += t ?? TimeSpan.Zero;
			untimed += t == null ? 1 : 0;
			var made = s.Output == null ? "" : s.OutputIsNew ? $" - makes \"{s.Output}\" (a new mod)" : $" - remakes \"{s.Output}\"";
			l.Add($"  {i + 1}. {s.Name}{made}{(t is { } u ? $" (usually {Minutes(u)})" : "")}");
		}
		if (total > TimeSpan.Zero) {
			l.Add($"About {Minutes(total)} in all{(untimed > 0 ? $", plus {untimed} step(s) never timed yet" : "")}.");
		}
		if (OffForTheRun.Count > 0) {
			l.Add("");
			l.Add("Switched OFF while the run goes, back on when it ends (a remade output as soon as its tool is done):");
			l.AddRange(OffForTheRun.Select(o => $"  - {o.Mod} ({o.Why})"));
		}
		if (ReplacedForGood.Count > 0) {
			l.Add("");
			l.Add("Kept off after the run (replaced by what it makes):");
			l.AddRange(ReplacedForGood.Select(m => "  - " + m));
		}
		if (StartsTheGame.Count > 0) {
			l.Add("");
			l.Add("The game starts for: " + string.Join(", ", StartsTheGame) + ".");
		}
		if (ClosesMo2) {
			l.Add("");
			l.Add("Mod Organizer is open: the run closes it, and opens it again for each tool.");
		}
		l.Add("");
		l.Add("Nothing has been changed yet.");
		return l;
	}

	static string Minutes(TimeSpan a_t) => a_t.TotalMinutes < 1 ? "under a minute" : a_t.TotalMinutes < 60 ? $"{a_t.TotalMinutes:0} min" : $"{(int)a_t.TotalHours} h {a_t.Minutes:00} min";
}
