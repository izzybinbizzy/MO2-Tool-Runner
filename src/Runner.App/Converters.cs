using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace ToolRunner.App;

// shown when the value has something in it (text, a count above 0, an object)
public sealed class NotEmptyToVisibility : IValueConverter
{
	public object Convert(object? a_v, Type a_t, object? a_p, CultureInfo a_c) => a_v switch {
		null => Visibility.Collapsed,
		string s => s.Length > 0 ? Visibility.Visible : Visibility.Collapsed,
		int i => i > 0 ? Visibility.Visible : Visibility.Collapsed,
		bool b => b ? Visibility.Visible : Visibility.Collapsed,
		_ => Visibility.Visible,
	};

	public object ConvertBack(object? a_v, Type a_t, object? a_p, CultureInfo a_c) => throw new NotSupportedException();
}

// shown when the count is 0 (an empty list's hint)
public sealed class ZeroToVisibility : IValueConverter
{
	public object Convert(object? a_v, Type a_t, object? a_p, CultureInfo a_c) => a_v is 0 ? Visibility.Visible : Visibility.Collapsed;

	public object ConvertBack(object? a_v, Type a_t, object? a_p, CultureInfo a_c) => throw new NotSupportedException();
}

public sealed class InvertBool : IValueConverter
{
	public object Convert(object? a_v, Type a_t, object? a_p, CultureInfo a_c) => a_v is not true;
	public object ConvertBack(object? a_v, Type a_t, object? a_p, CultureInfo a_c) => a_v is not true;
}

static class Brushes2
{
	public static Brush Get(string a_key) => (Brush)Application.Current.Resources[a_key];
}

// "good" / "bad" / "warn" -> its colour
public sealed class KindToBrush : IValueConverter
{
	public object Convert(object? a_v, Type a_t, object? a_p, CultureInfo a_c) => (a_v as string) switch {
		"good" => Brushes2.Get("Good"),
		"bad" => Brushes2.Get("Bad"),
		"warn" => Brushes2.Get("Warn"),
		_ => Brushes2.Get("Text"),
	};

	public object ConvertBack(object? a_v, Type a_t, object? a_p, CultureInfo a_c) => throw new NotSupportedException();
}

// a step's state, or a yes / no (with the parameter's colour for yes), -> its colour
public sealed class StateToBrush : IValueConverter
{
	public object Convert(object? a_v, Type a_t, object? a_p, CultureInfo a_c) => a_v switch {
		bool b => b ? Brushes2.Get(a_p as string == "bad" ? "Bad" : "Warn") : Brushes2.Get("Muted"),
		StepState.Running => Brushes2.Get("GoldLight"),
		StepState.Done => Brushes2.Get("Good"),
		StepState.Failed => Brushes2.Get("Bad"),
		StepState.Stopped => Brushes2.Get("Warn"),
		_ => Brushes2.Get("Muted"),
	};

	public object ConvertBack(object? a_v, Type a_t, object? a_p, CultureInfo a_c) => throw new NotSupportedException();
}
