using Avalonia.Controls;

namespace GuitarPractice.App.Controls;

/// <summary>Rows of exercise name + BPM + note value; DataContext is an <c>ExerciseLinesViewModel</c>.</summary>
public partial class ExerciseLinesEditor : UserControl
{
    public ExerciseLinesEditor() => InitializeComponent();
}
