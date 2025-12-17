using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Runtime.CompilerServices;

namespace ExHyperV.ViewModels
{
    public enum SmtMode
    {
        Inherit,
        SingleThread,
        MultiThread
    }

    public partial class VMProcessorViewModel : ObservableObject
    {
        public Action<string> InstantApplyAction { get; set; }

        [ObservableProperty]
        private long _count;

        [ObservableProperty]
        private int _relativeWeight;

        [ObservableProperty]
        private long _reserve;

        [ObservableProperty]
        private long _maximum;

        [ObservableProperty]
        private SmtMode _smtMode;
        partial void OnSmtModeChanged(SmtMode value) => OnInstantPropertyChanged();

        [ObservableProperty]
        private bool _exposeVirtualizationExtensions;
        partial void OnExposeVirtualizationExtensionsChanged(bool value) => OnInstantPropertyChanged();

        [ObservableProperty]
        private bool _enableHostResourceProtection;
        partial void OnEnableHostResourceProtectionChanged(bool value) => OnInstantPropertyChanged();

        [ObservableProperty]
        private bool _compatibilityForMigrationEnabled;
        partial void OnCompatibilityForMigrationEnabledChanged(bool value) => OnInstantPropertyChanged();

        [ObservableProperty]
        private bool _compatibilityForOlderOperatingSystemsEnabled;
        partial void OnCompatibilityForOlderOperatingSystemsEnabledChanged(bool value) => OnInstantPropertyChanged();

        private void OnInstantPropertyChanged([CallerMemberName] string propertyName = null)
        {
            if (propertyName != null)
            {
                var finalPropertyName = propertyName.Replace("Changed", "");
                InstantApplyAction?.Invoke(finalPropertyName);
            }
        }

        public VMProcessorViewModel CreateCopy()
        {
            return (VMProcessorViewModel)this.MemberwiseClone();
        }

        public void Restore(VMProcessorViewModel source)
        {
            if (source == null) return;

            Count = source.Count;
            RelativeWeight = source.RelativeWeight;
            Reserve = source.Reserve;
            Maximum = source.Maximum;

            bool oldSmt = _smtMode != source._smtMode;
            if (oldSmt) { _smtMode = source._smtMode; OnPropertyChanged(nameof(SmtMode)); }

            bool oldExpose = _exposeVirtualizationExtensions != source._exposeVirtualizationExtensions;
            if (oldExpose) { _exposeVirtualizationExtensions = source._exposeVirtualizationExtensions; OnPropertyChanged(nameof(ExposeVirtualizationExtensions)); }

            bool oldEnable = _enableHostResourceProtection != source._enableHostResourceProtection;
            if (oldEnable) { _enableHostResourceProtection = source._enableHostResourceProtection; OnPropertyChanged(nameof(EnableHostResourceProtection)); }

            bool oldCompat = _compatibilityForMigrationEnabled != source._compatibilityForMigrationEnabled;
            if (oldCompat) { _compatibilityForMigrationEnabled = source._compatibilityForMigrationEnabled; OnPropertyChanged(nameof(CompatibilityForMigrationEnabled)); }

            bool oldCompatOs = _compatibilityForOlderOperatingSystemsEnabled != source._compatibilityForOlderOperatingSystemsEnabled;
            if (oldCompatOs) { _compatibilityForOlderOperatingSystemsEnabled = source._compatibilityForOlderOperatingSystemsEnabled; OnPropertyChanged(nameof(CompatibilityForOlderOperatingSystemsEnabled)); }
        }
    }
}