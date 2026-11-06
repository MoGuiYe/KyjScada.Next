using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using KyjScada.Domain.Users;
using KyjScada.Infrastructure.Users;

namespace KyjScada.App;

/// <summary>用户列表一行。</summary>
public sealed class UserRow : INotifyPropertyChanged
{
    public int Id { get; set; }
    public string UserName { get; set; } = "";
    public string PermissionText { get; set; } = "";
    public string ActiveText { get; set; } = "";
    public string LastLoginText { get; set; } = "";

    /// <summary>原始权限值(编辑时回填复选)。</summary>
    public PermissionBit Permissions { get; set; }

    public bool IsActive { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Notify()
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
}

/// <summary>
/// 用户管理窗口。**只有具备"用户管理"权限的人能打开**(由调用方校验)。
///
/// 界面不直接拼 SQL,也不碰密码散列:全部走 UserRepository。
/// </summary>
public partial class UserManageWindow : Window
{
    private readonly UserRepository _repo;
    private readonly List<UserRow> _rows = new();
    private readonly Dictionary<PermissionBit, CheckBox> _permBoxes = new();

    public UserManageWindow(UserRepository repo)
    {
        InitializeComponent();

        _repo = repo;

        BuildPermissionBoxes();

        Reload();

        HintText.Text = "提示:密码只保存 PBKDF2 哈希,任何人(包括管理员)都无法查看原密码;"
            + "忘记只能重置。新建用户必须填写密码。";
    }

    private void BuildPermissionBoxes()
    {
        PermPanel.Children.Clear();
        _permBoxes.Clear();

        foreach (var bit in Enum.GetValues<PermissionBit>())
        {
            if (bit == PermissionBit.None || bit == PermissionBit.All) continue;

            var box = new CheckBox
            {
                Content = PermissionNames.Of(bit),
                Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xC6, 0xCD, 0xE0)),
                FontSize = 12,
                Width = 110,
                Margin = new Thickness(0, 2, 0, 2)
            };

            _permBoxes[bit] = box;
            PermPanel.Children.Add(box);
        }
    }

    private void Reload(string? selectName = null)
    {
        _rows.Clear();

        try
        {
            foreach (var u in _repo.GetAll())
            {
                _rows.Add(new UserRow
                {
                    Id = u.Id,
                    UserName = u.UserName,
                    PermissionText = u.PermissionText,
                    ActiveText = u.IsActive ? "是" : "否",
                    LastLoginText = u.LastLoginAt?.ToString("MM-dd HH:mm") ?? "从未",
                    Permissions = u.Permissions,
                    IsActive = u.IsActive
                });
            }
        }
        catch (Exception ex)
        {
            ResultText.Text = "读取失败: " + ex.Message;
        }

        UserGrid.ItemsSource = null;
        UserGrid.ItemsSource = _rows;

        if (selectName is not null)
        {
            var hit = _rows.FirstOrDefault(r =>
                string.Equals(r.UserName, selectName, StringComparison.OrdinalIgnoreCase));

            if (hit is not null)
            {
                UserGrid.SelectedItem = hit;
                UserGrid.ScrollIntoView(hit);
            }
        }
    }

    private void OnUserSelected(object sender, SelectionChangedEventArgs e)
    {
        if (UserGrid.SelectedItem is not UserRow row) return;

        NameBox.Text = row.UserName;
        PassBox.Clear();
        ActiveBox.IsChecked = row.IsActive;

        foreach (var kv in _permBoxes)
        {
            kv.Value.IsChecked = (row.Permissions & kv.Key) == kv.Key;
        }

        ResultText.Text = "已选中:" + row.UserName;
    }

    /// <summary>
    /// 进入"新建"模式:清空选中与表单。
    ///
    /// 为什么必须有这一步:选中某个账号时表单会填上它的权限,
    /// 这时直接填名字点创建会把**别人的权限**复制给新账号 ——
    /// 实测就出现过"只勾了查看,新号却拿到了全部权限"。
    /// </summary>
    private void OnNewUser(object sender, RoutedEventArgs e)
    {
        UserGrid.SelectedItem = null;

        NameBox.Clear();
        PassBox.Clear();
        ActiveBox.IsChecked = true;

        foreach (var kv in _permBoxes) kv.Value.IsChecked = false;

        ResultText.Text = "新建模式:填写用户名与密码、勾选权限,再点「保存 / 创建」";
    }

    private PermissionBit ReadPermissions()
    {
        var result = PermissionBit.None;

        foreach (var kv in _permBoxes)
        {
            if (kv.Value.IsChecked == true) result |= kv.Key;
        }

        return result;
    }

    private void TryCreate()
    {
        var name = NameBox.Text?.Trim() ?? "";
        var password = PassBox.Password ?? "";

        if (name.Length == 0)
        {
            ResultText.Text = "请填写用户名";

            return;
        }

        if (password.Length < 6)
        {
            ResultText.Text = "密码至少 6 位";

            return;
        }

        var perms = ReadPermissions();

        if (perms == PermissionBit.None)
        {
            ResultText.Text = "至少勾选一项权限";

            return;
        }

        try
        {
            var id = _repo.Create(name, password, perms, DateTime.Now, ActiveBox.IsChecked == true);

            if (id <= 0)
            {
                ResultText.Text = "创建失败:用户名已存在";

                return;
            }

            ResultText.Text = "已创建用户 " + name + "(权限:" + PermissionNames.Describe(perms) + ")";
            Reload(name);
        }
        catch (Exception ex)
        {
            ResultText.Text = "创建失败: " + ex.Message;
        }
    }

    private void OnSavePermissions(object sender, RoutedEventArgs e)
    {
        // 没选中任何行 = 新建;选中了 = 改它的权限
        if (UserGrid.SelectedItem is not UserRow row)
        {
            TryCreate();

            return;
        }

        var perms = ReadPermissions();

        if (perms == PermissionBit.None)
        {
            ResultText.Text = "至少保留一项权限(否则该账号什么都做不了)";

            return;
        }

        try
        {
            var ok = _repo.UpdatePermissions(row.UserName, perms, ActiveBox.IsChecked == true);

            ResultText.Text = ok
                ? "已更新 " + row.UserName + " 的权限"
                : "更新失败:账号可能已被删除";

            Reload(row.UserName);
        }
        catch (Exception ex)
        {
            ResultText.Text = "更新失败: " + ex.Message;
        }
    }

    private void OnResetPassword(object sender, RoutedEventArgs e)
    {
        if (UserGrid.SelectedItem is not UserRow row)
        {
            ResultText.Text = "请先在左侧选中一个用户";

            return;
        }

        var password = PassBox.Password ?? "";

        if (password.Length < 6)
        {
            ResultText.Text = "请在密码框输入新密码(至少 6 位)";

            return;
        }

        try
        {
            var ok = _repo.ChangePassword(row.UserName, password);

            ResultText.Text = ok
                ? "已重置 " + row.UserName + " 的密码"
                : "重置失败";

            PassBox.Clear();
        }
        catch (Exception ex)
        {
            ResultText.Text = "重置失败: " + ex.Message;
        }
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (UserGrid.SelectedItem is not UserRow row)
        {
            ResultText.Text = "请先在左侧选中一个用户";

            return;
        }

        // 不允许删掉最后一个账号,否则没人能再登录
        if (_rows.Count <= 1)
        {
            ResultText.Text = "这是最后一个账号,不能删除(否则系统将无法登录)";

            return;
        }

        var confirm = MessageBox.Show(this,
            "确定删除账号「" + row.UserName + "」?此操作不可恢复。",
            "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            _repo.Delete(row.UserName);

            ResultText.Text = "已删除 " + row.UserName;

            Reload();
            NameBox.Clear();
            PassBox.Clear();
        }
        catch (Exception ex)
        {
            ResultText.Text = "删除失败: " + ex.Message;
        }
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        Close();
    }
}