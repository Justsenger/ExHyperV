using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using ExHyperV.Models;
using ExHyperV.Tools;
using ExHyperV.Vmcx;

internal static class ModelIsolationChecks
{
    public static void Run(Action<bool, string> check)
    {
        CheckFixedCatalogs(check);
        CheckAllSnapshotFields(new VmMemorySettings(), source => source.Clone(), check);
        CheckAllSnapshotFields(new VmMmioSettings(), source => source.Clone(), check);
        CheckNotifications(new VmMemorySettings(), source => source.Clone(),
            model => model.Startup++, check);
        CheckNotifications(new VmMmioSettings(), source => source.Clone(),
            model => model.LowSizeMiB = (model.LowSizeMiB ?? 0) + 1, check);

        var source = new VmMemorySettings { SgxEnabled = true, SgxSize = null };
        var clone = source.Clone();
        check(clone.SgxEnabled == true && clone.SgxSize == null,
            "memory snapshot preserves exact SGX values without running normalization setters");
        clone.SgxEnabled = false;
        clone.SgxEnabled = true;
        check(clone.SgxSize == 2 && source.SgxSize == null,
            "memory clone retains the original SGX setter behavior independently of its source");

        source.AvailablePageSizes[0].Description = "source first";
        clone = source.Clone();
        clone.AvailablePageSizes[0].Description = "clone first";
        clone.AvailablePageSizes.Add(new PageSizeItem { Description = "clone extra", Value = 7 });
        check(source.AvailablePageSizes.Count == 3 && source.AvailablePageSizes[0].Description == "source first",
            "changing cloned page-size items or collection leaves the source unchanged");
        source.AvailablePageSizes[1].Value = 9;
        source.AvailablePageSizes.Clear();
        check(clone.AvailablePageSizes.Count == 4 && clone.AvailablePageSizes[1].Value == 1,
            "changing source page-size items or collection leaves its clone unchanged");
    }

    private static void CheckFixedCatalogs(Action<bool, string> check)
    {
        string[] osTypes =
        [
            "Windows", "Ubuntu", "ArchLinux", "Debian", "CentOS", "Kali", "Linux", "Android", "ChromeOS", "FydeOS",
            "MacOS", "FreeBSD", "OpenWrt", "FnOS", "iStoreOS", "TrueNAS", "Unraid", "NixOS", "Manjaro", "LinuxMint",
            "Fedora", "Deepin", "openEuler", "Kylin", "openSUSE", "RockyLinux", "AlmaLinux", "RHEL", "Alpine"
        ];
        check(OsImages.SupportedTypes.SequenceEqual(osTypes) &&
            OsImages.Canonical("openeuler") == "openEuler" && OsImages.Canonical("Unlisted") == "Windows" &&
            OsImages.Canonical(null) == "Windows",
            "fixed OS catalog preserves order, canonical casing, and the Windows fallback");

        var list = OsImages.SupportedTypes as IList<string>;
        check(list != null && list.IsReadOnly && RejectsMutation(() => list.Add("Injected")) &&
            RejectsMutation(() => list[0] = "Injected") && RejectsMutation(() => list.Clear()) &&
            OsImages.SupportedTypes.SequenceEqual(osTypes),
            "OS catalog cannot be changed by casting its read-only view to a mutable collection interface");

        string[] functionalTypes =
        [
            "2fc216b0-d2e2-4967-9b6d-b8a5c9ca2778", "d422512d-2bf2-4752-809d-7b82b5fcb1b4",
            "2fcc454e-a36a-4c77-bb5e-a2d75a51f02c", "99dcd00c-fbd6-42d3-9dfd-1b5ad7058f61"
        ];
        check(VmcxSchema.FunctionalDeviceTypes.SetEquals(functionalTypes) &&
            functionalTypes.All(type => VmcxSchema.FunctionalDeviceTypes.Contains(type.ToUpperInvariant())) &&
            !VmcxSchema.FunctionalDeviceTypes.Contains("unlisted"),
            "fixed VMCX catalog preserves functional types and case-insensitive membership");

        var collection = VmcxSchema.FunctionalDeviceTypes as ICollection<string>;
        check((collection == null || (collection.IsReadOnly &&
            RejectsMutation(() => collection.Add("Injected")) &&
            RejectsMutation(() => collection.Remove(functionalTypes[0])) &&
            RejectsMutation(() => collection.Clear()))) &&
            VmcxSchema.FunctionalDeviceTypes.SetEquals(functionalTypes),
            "VMCX catalog cannot be modified through a mutable collection interface");
    }

    private static bool RejectsMutation(Action mutate)
    {
        try { mutate(); return false; }
        catch (NotSupportedException) { return true; }
    }

    private static void CheckAllSnapshotFields<T>(T source, Func<T, T> clone,
        Action<bool, string> check) where T : class
    {
        // Enumerate actual public/private instance storage, so a newly added field
        // cannot silently disappear from the explicit production snapshot copy.
        var fields = typeof(T).GetFields(BindingFlags.Instance | BindingFlags.Public |
            BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
        foreach (bool populated in new[] { true, false })
        {
            for (int index = 0; index < fields.Length; index++)
                fields[index].SetValue(source, SnapshotValue(fields[index].FieldType, index, populated));

            var copy = clone(source);
            bool matches = !ReferenceEquals(source, copy);
            foreach (var field in fields)
            {
                var original = field.GetValue(source);
                var copied = field.GetValue(copy);
                if (original is List<PageSizeItem> items)
                {
                    matches &= copied is List<PageSizeItem> copiedItems && !ReferenceEquals(items, copiedItems) &&
                        items.Count == copiedItems.Count && items.Zip(copiedItems).All(pair =>
                            !ReferenceEquals(pair.First, pair.Second) &&
                            pair.First.Description == pair.Second.Description && pair.First.Value == pair.Second.Value);
                }
                else matches &= Equals(original, copied);
            }
            check(matches, $"{typeof(T).Name} copies every declared snapshot field and isolates nested values ({(populated ? "populated" : "null and false")})");
        }
    }

    private static object? SnapshotValue(Type type, int index, bool populated)
    {
        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying != null && !populated) return null;
        type = underlying ?? type;
        if (type == typeof(bool)) return populated;
        if (type == typeof(byte)) return (byte)(index + 11);
        if (type == typeof(int)) return index + 101;
        if (type == typeof(uint)) return (uint)(index + 201);
        if (type == typeof(long)) return (long)index + 301;
        if (type == typeof(ulong)) return (ulong)index + 401;
        if (type == typeof(double)) return index + 0.75;
        if (type == typeof(string)) return populated ? "snapshot " + index : null;
        if (type == typeof(List<PageSizeItem>)) return new List<PageSizeItem>
        {
            new() { Description = "custom second", Value = 2 },
            new() { Description = "custom first", Value = 0 }
        };
        throw new InvalidOperationException("Add snapshot coverage for field type " + type);
    }

    private static void CheckNotifications<T>(T source, Func<T, T> clone, Action<T> mutate,
        Action<bool, string> check) where T : ObservableObject
    {
        int sourceChanging = 0, sourceChanged = 0, copyChanging = 0, copyChanged = 0;
        source.PropertyChanging += (_, _) => sourceChanging++;
        source.PropertyChanged += (_, _) => sourceChanged++;
        var copy = clone(source);
        copy.PropertyChanging += (_, _) => copyChanging++;
        copy.PropertyChanged += (_, _) => copyChanged++;
        mutate(copy);
        check(sourceChanging == 0 && sourceChanged == 0 && copyChanging == 1 && copyChanged == 1,
            $"{typeof(T).Name} clone changes notify only the clone's subscribers");
        mutate(source);
        check(sourceChanging == 1 && sourceChanged == 1 && copyChanging == 1 && copyChanged == 1,
            $"{typeof(T).Name} source changes retain independent notifications after cloning");
    }
}
