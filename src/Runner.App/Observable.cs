using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace ToolRunner.App;

// the two pieces of MVVM the window needs, without a package
public abstract class Observable : INotifyPropertyChanged
{
	public event PropertyChangedEventHandler? PropertyChanged;

	protected void OnPropertyChanged([CallerMemberName] string? a_name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(a_name));

	protected bool Set<T>(ref T a_field, T a_value, [CallerMemberName] string? a_name = null)
	{
		if (EqualityComparer<T>.Default.Equals(a_field, a_value)) {
			return false;
		}
		a_field = a_value;
		OnPropertyChanged(a_name);
		return true;
	}
}

public sealed class Cmd(Action a_run) : ICommand
{
	public event EventHandler? CanExecuteChanged
	{
		add => CommandManager.RequerySuggested += value;
		remove => CommandManager.RequerySuggested -= value;
	}

	public bool CanExecute(object? a_p) => true;

	public void Execute(object? a_p) => a_run();
}
