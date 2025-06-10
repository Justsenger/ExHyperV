using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using System.Windows.Markup;
using ExHyperV.Properties;
using ExHyperV.Services;

namespace ExHyperV.Markup;

/// <summary>
///     Simple markup extension for localized strings that update when language changes
/// </summary>
[MarkupExtensionReturnType(typeof(BindingExpression))]
public class LocalizedExtension : MarkupExtension, INotifyPropertyChanged
{
    private static readonly List<WeakReference> Instances = [];
    private readonly string _key;
    private bool _isRegistered;

    static LocalizedExtension()
    {
        // Subscribe to language changes
        LocalizationService.LanguageChanged += (_, _) =>
        {
            // Clean up dead references and notify alive instances
            for (var i = Instances.Count - 1; i >= 0; i--)
                if (Instances[i].Target is LocalizedExtension instance)
                    instance.OnPropertyChanged(nameof(Value));
                else
                    Instances.RemoveAt(i);
        };
    }

    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="key">Resource key</param>
    public LocalizedExtension(string key)
    {
        _key = key;
    }

    /// <summary>
    ///     Gets the localized value
    /// </summary>
    public string Value
    {
        get
        {
            try
            {
                var value = Resources.ResourceManager.GetString(_key, Resources.Culture);
                return value ?? _key;
            }
            catch
            {
                return _key;
            }
        }
    }

    /// <summary>
    ///     PropertyChanged event for data binding
    /// </summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    ///     Provides the value for the markup extension
    /// </summary>
    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        if (string.IsNullOrEmpty(_key))
            return string.Empty;

        // Store weak reference for language change notifications (only once)
        if (!_isRegistered)
        {
            Instances.Add(new WeakReference(this));
            _isRegistered = true;
        }

        // Create binding to the Value property
        var binding = new Binding(nameof(Value))
        {
            Source = this,
            Mode = BindingMode.OneWay
        };

        return binding.ProvideValue(serviceProvider);
    }

    /// <summary>
    ///     Raises PropertyChanged event
    /// </summary>
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}