using System.Windows;

namespace CqNl.Wpf;

public partial class ChangePasswordWindow : Window
{
    public ChangePasswordWindow(bool forced)
    {
        InitializeComponent();
        Note.Text = forced ? "Đây là lần đầu đăng nhập bằng mật khẩu mặc định. Bạn phải đổi mật khẩu trước khi làm việc." : "Đổi mật khẩu đăng nhập của bạn.";
        Loaded += (_, _) => OldBox.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var s = App.Ws.Session!;
        if (!Ui.Run(() =>
        {
            App.Ws.RequireOnline("đổi mật khẩu");
            App.Ws.Auth!.ChangePassword(s, OldBox.Password, NewBox.Password, ConfirmBox.Password);
            App.Ws.RefreshSession(s with { MustChangePassword = false, User = App.Ws.Repo!.Get<CqNl.Application.UserRecord>("user", s.User.Username).Value! });
        })) return;
        OldBox.Clear(); NewBox.Clear(); ConfirmBox.Clear();
        Ui.Info("Đã đổi mật khẩu.");
        DialogResult = true;
    }
}
