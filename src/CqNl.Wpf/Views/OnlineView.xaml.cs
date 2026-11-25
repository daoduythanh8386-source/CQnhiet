using System.Windows;
using System.Windows.Controls;

namespace CqNl.Wpf.Views;

public partial class OnlineView : UserControl
{
    public OnlineView() { InitializeComponent(); Loaded += (_, _) => Reload(); }
    private void Reload()
    {
        Note.Text = App.Ws.Online
            ? "Mỗi máy gia hạn phiên 30 giây/lần. Máy tắt đột ngột sẽ tự hết phiên sau khoảng 4 phút."
            : "OFFLINE — không xem được danh sách phiên. Khi kho chung không truy cập được, không thể bảo đảm mỗi tài khoản chỉ một phiên trên toàn mạng.";
        Ui.Run(() => Grid.ItemsSource = App.Ws.Online ? App.Ws.Auth!.OnlineSessions().OrderBy(s => s.User).ToList() : null);
    }
    private void Refresh_Click(object sender, RoutedEventArgs e) => Reload();
}
