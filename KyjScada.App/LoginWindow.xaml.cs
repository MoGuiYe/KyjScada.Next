using System.Windows;
using KyjScada.Domain.Users;
using KyjScada.Infrastructure.Users;

namespace KyjScada.App;

/// <summary>
/// 登录窗口。校验交给 UserRepository(密码用 PBKDF2 校验,失败不区分"用户不存在"与"密码错")。
/// </summary>
public partial class LoginWindow : Window
{
    private readonly UserRepository _repo;

    /// <summary>登录成功的账号;取消为 null。</summary>
    public UserAccount? User { get; private set; }

    public LoginWindow(UserRepository repo)
    {
        InitializeComponent();

        _repo = repo;

        HintText.Text = "首次使用默认账号 admin / admin123,登录后请立即修改密码。";
    }

    private void OnLogin(object sender, RoutedEventArgs e)
    {
        var name = UserBox.Text?.Trim() ?? "";
        var password = PassBox.Password ?? "";

        if (name.Length == 0 || password.Length == 0)
        {
            ErrorText.Text = "请输入用户名和密码";
            return;
        }

        try
        {
            var user = _repo.Authenticate(name, password, DateTime.Now);

            if (user is null)
            {
                ErrorText.Text = "用户名或密码错误(或该账号已停用)";
                PassBox.Clear();
                return;
            }

            User = user;

            DialogResult = true;
        }
        catch (Exception ex)
        {
            ErrorText.Text = "登录失败: " + ex.Message;
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        User = null;
        DialogResult = false;
    }
}