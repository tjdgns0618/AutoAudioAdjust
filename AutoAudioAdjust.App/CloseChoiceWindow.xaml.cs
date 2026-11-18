using System.Windows;
using System.Windows.Input;
using AutoAudioAdjust.App.Models;

namespace AutoAudioAdjust.App;

/// <summary>Asks whether the X button should exit the app or hide it to the tray.</summary>
public partial class CloseChoiceWindow : Window
{
    public CloseChoiceWindow()
    {
        InitializeComponent();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
            }
        };
        MouseLeftButtonDown += (_, _) => DragMove();
    }

    /// <summary>The user's choice, or null when the dialog was cancelled.</summary>
    public CloseAction? Choice { get; private set; }

    public bool Remember => RememberCheckBox.IsChecked == true;

    private void TrayButton_Click(object sender, RoutedEventArgs e)
    {
        Choice = CloseAction.Tray;
        Close();
    }

    private void ExitButton_Click(object sender, RoutedEventArgs e)
    {
        Choice = CloseAction.Exit;
        Close();
    }
}
