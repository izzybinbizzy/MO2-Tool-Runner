using System.Windows;
using System.Windows.Controls;

namespace ToolRunner.App;

// A queue of setups: tick the ones to run, put them in order; each runs whole (its own check, its own put-back) after the one
// before ends. A stopped run ends the queue; a failed one too, unless "keep going" is ticked.
public static class QueueDialog
{
	public sealed record Picked(List<string> Files, bool KeepGoing);

	sealed class Row
	{
		public required string File { get; init; }
		public required CheckBox Box { get; init; }
	}

	public static Picked? Run(Window a_owner, List<string> a_setups)
	{
		Picked? picked = null;
		var w = new Window {
			Owner = a_owner, Title = "Queue of setups", Width = 560, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner,
			Background = Brushes2.Get("Panel"), Foreground = Brushes2.Get("Text"), ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
		};
		var stack = new StackPanel { Margin = new Thickness(22) };
		stack.Children.Add(new TextBlock { Text = "QUEUE OF SETUPS", Style = (Style)a_owner.FindResource("H1"), Margin = new Thickness(0, 0, 0, 6), FontSize = 18 });
		stack.Children.Add(new TextBlock { Text = "Tick the setups to run and put them in order. Each one runs whole - its check, its tools, your profile put back - before the next starts.",
			Style = (Style)a_owner.FindResource("MutedText"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) });

		var rows = a_setups.Select(f => new Row { File = f, Box = new CheckBox { Content = Path.GetFileNameWithoutExtension(f), Foreground = Brushes2.Get("Text") } }).ToList();
		var list = new ListBox { MinHeight = 120, MaxHeight = 320 };
		// a click on a tick box is the box's own, so its row would stay unselected: the row follows the box's focus
		foreach (var r in rows) {
			r.Box.GotKeyboardFocus += (_, _) => list.SelectedItem = r.Box;
		}
		System.Windows.Automation.AutomationProperties.SetName(list, "Setups in the order they run");
		void Fill(int a_select)
		{
			list.Items.Clear();
			foreach (var r in rows) {
				list.Items.Add(r.Box);
			}
			list.SelectedIndex = a_select;
		}
		Fill(rows.Count > 0 ? 0 : -1);
		stack.Children.Add(list);

		void Move(int a_by)
		{
			var i = list.SelectedIndex;
			var j = i + a_by;
			if (i < 0 || j < 0 || j >= rows.Count) {
				return;
			}
			(rows[i], rows[j]) = (rows[j], rows[i]);
			Fill(j);
			list.Focus();
		}
		var moves = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
		var up = new Button { Content = "Move _up", Style = (Style)a_owner.FindResource("Ghost"), Margin = new Thickness(0, 0, 8, 0) };
		var down = new Button { Content = "Move _down", Style = (Style)a_owner.FindResource("Ghost") };
		up.Click += (_, _) => Move(-1);
		down.Click += (_, _) => Move(1);
		moves.Children.Add(up);
		moves.Children.Add(down);
		stack.Children.Add(moves);

		var keep = new CheckBox { Content = "_Keep going when a setup's run fails (a stopped run always ends the queue)", Foreground = Brushes2.Get("Text"), Margin = new Thickness(0, 14, 0, 0) };
		stack.Children.Add(keep);
		var note = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Foreground = Brushes2.Get("Bad") };
		stack.Children.Add(note);

		var start = new Button { Content = "_Start the queue", Style = (Style)a_owner.FindResource("Primary"), Margin = new Thickness(0, 0, 10, 0), IsDefault = true };
		var cancel = new Button { Content = "Cancel", Style = (Style)a_owner.FindResource("Ghost"), IsCancel = true };
		start.Click += (_, _) => {
			var files = rows.Where(r => r.Box.IsChecked == true).Select(r => r.File).ToList();
			if (files.Count < 2) {
				note.Text = "Tick at least two setups (one setup is a normal Start).";
				return;
			}
			picked = new Picked(files, keep.IsChecked == true);
			w.Close();
		};
		var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 18, 0, 0) };
		bar.Children.Add(start);
		bar.Children.Add(cancel);
		stack.Children.Add(bar);
		w.Content = stack;
		w.ShowDialog();
		return picked;
	}
}
