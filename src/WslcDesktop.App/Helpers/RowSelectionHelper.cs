using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace WslcDesktop.Helpers;

/// <summary>
/// 批量列表里自绘勾选框的辅助逻辑。
///
/// 为什么不用绑定直接接上 ListViewItem.IsSelected：
/// <c>ListViewItemPresenter</c> 必须是 ListViewItem 模板的**根元素本身**（旁边有兄弟元素就抛
/// "can only be used as the first child in the template for a ListViewItem"），
/// 所以没法定制行容器模板、也就没法在模板里绑 IsSelected。
/// 退而求其次：关掉 ListView 自带的多选框，把紧凑勾选框放进各页 ItemTemplate，
/// 再由页面按 <c>SelectedItems</c> 手动同步——选择态仍然只有 SelectedItems 一个真相来源。
/// </summary>
internal static class RowSelectionHelper
{
    public const string CheckBoxName = "RowCheckBox";

    /// <summary>勾选框点击 → 落到真实选择集合上。</summary>
    public static void Toggle(ListView list, object? item)
    {
        if (item is null) return;

        if (list.SelectedItems.Contains(item))
        {
            list.SelectedItems.Remove(item);
        }
        else
        {
            list.SelectedItems.Add(item);
        }
    }

    /// <summary>
    /// 按 SelectedItems 刷新勾选框——只处理本次选择变更涉及的行。
    ///
    /// 程序化改动选择（例如「取消选择」按钮）不会让已实现的行重新触发 Loaded，
    /// 必须靠这里补一次，否则会出现"行没选中了、勾还留着"。
    /// 虚拟化未实现的行本来就没有勾选框，跳过即可——它们被实例化时会走
    /// RowCheckBox_Loaded 按真实选择态回填。
    /// </summary>
    public static void Sync(ListView list, SelectionChangedEventArgs e)
    {
        foreach (var item in e.AddedItems.Concat(e.RemovedItems))
        {
            if (list.ContainerFromItem(item) is not DependencyObject container) continue;

            if (FindCheckBox(container) is CheckBox box)
            {
                box.IsChecked = list.SelectedItems.Contains(item);
            }
        }
    }

    private static CheckBox? FindCheckBox(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);

            if (child is CheckBox box && box.Name == CheckBoxName) return box;

            var found = FindCheckBox(child);
            if (found is not null) return found;
        }

        return null;
    }
}
