namespace ToolRunner;

// The program's name, and the name it had before: a save, a cache stamp, a data folder or a Windows task made under the old
// name is still its own.
public static class Brand
{
	public const string Name = "Patcher Unification Project";

	public static readonly string[] OldNames = ["MO2 Tool Runner"];

	public static IEnumerable<string> AllNames => OldNames.Prepend(Name);

	// "<name> - ..." under the new name or an old one
	public static bool Made(string a_text) => AllNames.Any(n => a_text.StartsWith(n + " - ", StringComparison.OrdinalIgnoreCase));
}
