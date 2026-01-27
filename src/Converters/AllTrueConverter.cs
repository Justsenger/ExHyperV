public class AllTrueConverter : System.Windows.Data.IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        // 只有当所有绑定值都为 True 时，才返回 True
        return values.OfType<bool>().All(b => b);
    }
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, System.Globalization.CultureInfo culture) => null;
}