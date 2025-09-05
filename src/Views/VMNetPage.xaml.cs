using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ExHyperV.ViewModels;

namespace ExHyperV.Views.Pages
{
    public partial class VMNetPage : Page
    {
        public VMNetPage()
        {
            InitializeComponent();
            DataContext = new VMNetViewModel();
        }
        // 处理字符输入（过滤非数字、处理句号跳转）
        private void SubnetTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            if (sender is not TextBox currentTextBox) return;

            // --- 处理句号 '.' ---
            if (e.Text == ".")
            {
                // 查找包含所有子网输入框的父容器 StackPanel
                var parentPanel = FindParent<StackPanel>(currentTextBox);
                if (parentPanel != null)
                {
                    // 获取容器中所有的 TextBox
                    var textBoxes = parentPanel.Children.OfType<TextBox>().ToList();
                    int currentIndex = textBoxes.IndexOf(currentTextBox);

                    // 如果不是最后一个，则将焦点移动到下一个
                    if (currentIndex < textBoxes.Count - 1)
                    {
                        textBoxes[currentIndex + 1].Focus();
                    }
                }

                e.Handled = true; // 阻止'.'字符被输入
                return;
            }

            // --- 过滤非数字输入 ---
            if (!e.Text.All(char.IsDigit))
            {
                e.Handled = true; // 阻止非数字字符被输入
            }
        }

        // 处理功能键（删除键回退）
        private void SubnetTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (sender is not TextBox currentTextBox) return;

            // --- 处理删除键 'Backspace' ---
            if (e.Key == Key.Back && string.IsNullOrEmpty(currentTextBox.Text))
            {
                var parentPanel = FindParent<StackPanel>(currentTextBox);
                if (parentPanel != null)
                {
                    var textBoxes = parentPanel.Children.OfType<TextBox>().ToList();
                    int currentIndex = textBoxes.IndexOf(currentTextBox);

                    // 如果不是第一个，则将焦点移动到上一个
                    if (currentIndex > 0)
                    {
                        textBoxes[currentIndex - 1].Focus();
                    }
                }

                e.Handled = true; // 阻止默认的删除键行为（如系统提示音）
            }
        }

        // 一个通用的帮助方法，用于在可视化树中向上查找指定类型的父控件
        private static T FindParent<T>(DependencyObject child) where T : DependencyObject
        {
            DependencyObject parentObject = VisualTreeHelper.GetParent(child);

            if (parentObject == null) return null;

            if (parentObject is T parent)
                return parent;
            else
                return FindParent<T>(parentObject);
        }
    }
}