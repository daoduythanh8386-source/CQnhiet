using System.Windows;
using System.Windows.Controls;
using CqNl.Application;

namespace CqNl.Wpf.Views;

public sealed record UserRow(UserRecord U)
{
    public string Username => U.Username; public string FullName => U.FullName; public string RoleText => Ui.RoleText(U.Role);
    public string StateText => !U.Active ? "Đã khóa" : U.LockedUntil is { } l && l > DateTimeOffset.Now ? $"Tạm khoá đến {l:HH:mm} (nhập sai)" : U.MustChangePassword ? "Chờ đổi mật khẩu" : "Hoạt động";
}

public partial class UsersView : UserControl
{
    public UsersView() { InitializeComponent(); Loaded += (_, _) => Reload(); }

    private void Reload()
    {
        if (!App.Ws.Online) { FormTitle.Text = "OFFLINE — quản lý tài khoản cần kho chung"; IsEnabled = false; return; }
        Ui.Run(() => Grid.ItemsSource = App.Ws.Auth!.Users().Select(u => new UserRow(u)).ToList());
    }

    private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Grid.SelectedItem is not UserRow r) return;
        FormTitle.Text = "Sửa tài khoản " + r.Username;
        UserBox.Text = r.Username; UserBox.IsReadOnly = true; NameBox.Text = r.FullName;
        RoleBox.SelectedIndex = r.U.Role switch { Role.Admin => 0, Role.Qc => 1, Role.Sales => 2, _ => 3 };
        ActiveBox.IsChecked = r.U.Active; PassBox.Clear();
        DeleteBtn.IsEnabled = !string.Equals(r.Username, App.Ws.Session?.User.Username, StringComparison.OrdinalIgnoreCase);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItem is not UserRow r) { Ui.Info("Chọn tài khoản cần xoá trong danh sách"); return; }
        if (!Ui.Ask($"XOÁ HẲN tài khoản {r.Username} ({r.FullName}, {r.RoleText})?\n\nNgười này sẽ không đăng nhập được nữa. Đơn / CQ / nhật ký họ đã làm vẫn giữ nguyên.\nKhông hoàn tác được (muốn dùng lại thì tạo tài khoản mới).")) return;
        if (Ui.Run(() => App.Ws.Auth!.DeleteUser(App.Ws.Session!, r.Username))) { New_Click(sender, e); Reload(); Ui.Info("Đã xoá tài khoản " + r.Username); }
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        Grid.SelectedItem = null; FormTitle.Text = "Thêm tài khoản";
        UserBox.IsReadOnly = false; UserBox.Clear(); NameBox.Clear(); RoleBox.SelectedIndex = 1; ActiveBox.IsChecked = true; PassBox.Clear(); DeleteBtn.IsEnabled = false;
        UserBox.Focus();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var role = Enum.Parse<Role>((string)((ComboBoxItem)RoleBox.SelectedItem).Tag);
        var pass = PassBox.Password.Length == 0 ? null : PassBox.Password;
        if (UserBox.IsReadOnly && pass is not null && !Ui.Ask($"Đặt lại mật khẩu cho {UserBox.Text}? Người dùng sẽ phải đổi mật khẩu khi đăng nhập.")) return;
        if (ActiveBox.IsChecked != true && UserBox.IsReadOnly && !Ui.Ask($"Khóa tài khoản {UserBox.Text}? Phiên đang mở sẽ kết thúc ở lần gia hạn kế tiếp.")) return;
        if (Ui.Run(() => App.Ws.Auth!.SaveUser(App.Ws.Session!, UserBox.Text.Trim(), NameBox.Text.Trim(), role, ActiveBox.IsChecked == true, pass)))
        {
            PassBox.Clear(); Reload(); Ui.Info("Đã lưu tài khoản " + UserBox.Text.Trim());
        }
    }
}
