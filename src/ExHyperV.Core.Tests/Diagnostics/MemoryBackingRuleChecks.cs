using ExHyperV.Models;
using ExHyperV.Services;

internal static class MemoryBackingRuleChecks
{
    public static void Run(Action<bool, string> check)
    {
        static VmMemorySettings Conflicting() => new()
        {
            BackingType = 1, BackingPageSize = 2,
            EnableColdHint = true, EnableHotHint = true,
            EnableEpf = true, EnablePrivateCompressionStore = true,
            Startup = 4096, Minimum = 1024, Maximum = 8192, Weight = 50
        };
        static bool FeaturesDisabled(VmMemorySettings s) => s.EnableColdHint == false
            && s.EnableHotHint == false && s.EnableEpf == false && s.EnablePrivateCompressionStore == false;

        var pageEdit = Conflicting();
        VmMemoryService.NormalizeMemoryBackingSettings(pageEdit, nameof(VmMemorySettings.BackingPageSize));
        check(pageEdit.BackingType == 0 && pageEdit.BackingPageSize == 2 && FeaturesDisabled(pageEdit),
            "Memory backing: selecting 1 GB pages selects physical backing and disables incompatible features");

        var backingEdit = Conflicting();
        VmMemoryService.NormalizeMemoryBackingSettings(backingEdit, nameof(VmMemorySettings.BackingType));
        check(backingEdit.BackingType == 1 && backingEdit.BackingPageSize == 1 && backingEdit.EnableColdHint == true
            && backingEdit.EnableHotHint == true && backingEdit.EnableEpf == true && backingEdit.EnablePrivateCompressionStore == true,
            "Memory backing: selecting virtual backing instead preserves it and falls back to 2 MB pages");

        var manual = Conflicting();
        VmMemoryService.NormalizeMemoryBackingSettings(manual, null);
        check(manual.BackingType == 0 && manual.BackingPageSize == 2 && FeaturesDisabled(manual),
            "Memory backing: manual Apply retains the original page-size priority");
        check(manual.Startup == 4096 && manual.Minimum == 1024 && manual.Maximum == 8192 && manual.Weight == 50,
            "Memory backing: normalization leaves independent memory values unchanged");

        var unrelatedEdit = Conflicting();
        VmMemoryService.NormalizeMemoryBackingSettings(unrelatedEdit, nameof(VmMemorySettings.MemoryEncryptionPolicy));
        check(unrelatedEdit.BackingType == 1 && unrelatedEdit.BackingPageSize == 2 && unrelatedEdit.EnableColdHint == true,
            "Memory backing: an unrelated automatic edit does not apply the manual normalization rule");

        var unsupported = new VmMemorySettings { BackingType = 0, BackingPageSize = 2, EnableColdHint = true };
        VmMemoryService.NormalizeMemoryBackingSettings(unsupported, null);
        check(unsupported.EnableColdHint == false && unsupported.EnableHotHint == null && unsupported.EnableEpf == null
            && unsupported.EnablePrivateCompressionStore == null, "Memory backing: unsupported feature values remain null");
        var unknownBacking = new VmMemorySettings { BackingType = null, BackingPageSize = 2, EnableHotHint = true };
        VmMemoryService.NormalizeMemoryBackingSettings(unknownBacking, null);
        check(unknownBacking.BackingType == null && unknownBacking.BackingPageSize == 2 && unknownBacking.EnableHotHint == true,
            "Memory backing: absent backing capability is not invented");

        foreach (string property in new[] { nameof(VmMemorySettings.EnableColdHint), nameof(VmMemorySettings.EnableHotHint),
                     nameof(VmMemorySettings.EnableEpf), nameof(VmMemorySettings.EnablePrivateCompressionStore) })
        {
            var featureEdit = Conflicting();
            featureEdit.BackingType = 0;
            VmMemoryService.NormalizeMemoryBackingSettings(featureEdit, property);
            check(FeaturesDisabled(featureEdit), "Memory backing: physical backing rejects incompatible feature edit " + property);
        }
        VmMemoryService.NormalizeMemoryBackingSettings(pageEdit, nameof(VmMemorySettings.BackingPageSize));
        check(pageEdit.BackingType == 0 && pageEdit.BackingPageSize == 2 && FeaturesDisabled(pageEdit),
            "Memory backing: GUI preflight followed by service normalization is idempotent");
    }
}
