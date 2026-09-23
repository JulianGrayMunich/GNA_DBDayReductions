#region Imports
using System.Runtime.InteropServices;
using System.Security;
using System.Windows;
using System.Windows.Controls;
#endregion
namespace GNA_DBDayReductions;
#region Windows Task Account Password
public sealed class SchedulerPasswordWindow : Window
{
    private readonly PasswordBox _password = new() { Margin = new Thickness(uniformLength: 12), Height = 28 };
    public SecureString Password => _password.SecurePassword;
    public void ClearPassword() => _password.Clear();
    public SchedulerPasswordWindow(string account)
    {
        Title = "Windows account for daily reductions"; Width = 490; Height = 245;
        ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        StackPanel panel = new() { Margin = new Thickness(uniformLength: 12) };
        panel.Children.Add(element: new TextBlock { Text = $"Account: {account}\n\nEnter this account's Windows password (not a PIN). Windows Task Scheduler needs it to run while you are signed out.", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(element: _password);
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        Button register = new() { Content = "Start scheduler", Width = 125, Height = 30, IsDefault = true };
        Button cancel = new() { Content = "Cancel", Width = 90, Height = 30, IsCancel = true, Margin = new Thickness(left: 8, top: 0, right: 0, bottom: 0) };
        register.Click += (_, _) => { if (_password.SecurePassword.Length > 0) DialogResult = true; };
        buttons.Children.Add(element: register); buttons.Children.Add(element: cancel); panel.Children.Add(element: buttons); Content = panel;
        Loaded += (_, _) => _password.Focus();
        Closed += (_, _) => { if (DialogResult != true) _password.Clear(); };
    }
}
#endregion

