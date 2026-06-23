using System;
using System.Linq.Expressions;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
// 项目 UseWindowsForms=true 引入 System.Windows，Expression 与 System.Windows.Expression 冲突，用别名锁定。
using Expr = System.Linq.Expressions.Expression;

namespace ExHyperV.Tools
{
    /// <summary>
    /// 把任意控件事件转接到一个 ICommand（替代 Microsoft.Xaml.Behaviors 的 EventTrigger + InvokeCommandAction）。
    /// 用法：behaviors:EventToCommand.EventName="LostFocus"
    ///       behaviors:EventToCommand.Command="{Binding XxxCommand}"
    ///       behaviors:EventToCommand.CommandParameter="{Binding}"
    /// </summary>
    public static class EventToCommand
    {
        public static readonly DependencyProperty EventNameProperty =
            DependencyProperty.RegisterAttached("EventName", typeof(string), typeof(EventToCommand),
                new PropertyMetadata(null, OnEventNameChanged));
        public static string? GetEventName(DependencyObject o) => (string?)o.GetValue(EventNameProperty);
        public static void SetEventName(DependencyObject o, string? v) => o.SetValue(EventNameProperty, v);

        public static readonly DependencyProperty CommandProperty =
            DependencyProperty.RegisterAttached("Command", typeof(ICommand), typeof(EventToCommand));
        public static ICommand? GetCommand(DependencyObject o) => (ICommand?)o.GetValue(CommandProperty);
        public static void SetCommand(DependencyObject o, ICommand? v) => o.SetValue(CommandProperty, v);

        public static readonly DependencyProperty CommandParameterProperty =
            DependencyProperty.RegisterAttached("CommandParameter", typeof(object), typeof(EventToCommand));
        public static object? GetCommandParameter(DependencyObject o) => o.GetValue(CommandParameterProperty);
        public static void SetCommandParameter(DependencyObject o, object? v) => o.SetValue(CommandParameterProperty, v);

        private static void OnEventNameChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not FrameworkElement fe || e.NewValue is not string name || string.IsNullOrEmpty(name)) return;
            EventInfo? evt = fe.GetType().GetEvent(name);
            if (evt?.EventHandlerType == null) return;
            Delegate handler = BuildHandler(evt.EventHandlerType, fe);
            evt.AddEventHandler(fe, handler);
        }

        // 用 Expression 现编一个与事件委托类型完全匹配的处理器，统一转调 Exec(fe)。
        private static Delegate BuildHandler(Type handlerType, FrameworkElement fe)
        {
            MethodInfo invoke = handlerType.GetMethod("Invoke")!;
            ParameterInfo[] ps = invoke.GetParameters();
            ParameterExpression p0 = Expr.Parameter(ps[0].ParameterType, "s");
            ParameterExpression p1 = Expr.Parameter(ps[1].ParameterType, "e");
            MethodCallExpression call = Expr.Call(
                typeof(EventToCommand).GetMethod(nameof(Exec), BindingFlags.Public | BindingFlags.Static)!,
                Expr.Constant(fe));
            return Expr.Lambda(handlerType, call, p0, p1).Compile();
        }

        // 须为 public：编译出的委托运行在动态方法里，访问非公开成员会抛 MethodAccessException。
        public static void Exec(FrameworkElement fe)
        {
            ICommand? cmd = GetCommand(fe);
            object? param = GetCommandParameter(fe);
            if (cmd != null && cmd.CanExecute(param)) cmd.Execute(param);
        }
    }
}
