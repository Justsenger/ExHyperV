using ExHyperV.Vmcx;

internal static class NativeLifetimeChecks
{
    public static void Run(Action<bool, string> check)
    {
        // Runs the real initialization ownership flow, without loading a native DLL
        // or creating any VM configuration, USB pipe, or process-wide factory.
        foreach (Failure failure in Enum.GetValues<Failure>().Where(value => value != Failure.None))
        {
            var native = new FactoryCalls { FailAt = failure };
            bool failed = RejectsInitialization(native);
            check(failed && native.FailureObserved && native.OutstandingReferences == 0 && native.InvalidReleaseCount == 0,
                $"VMCX initialization releases all acquired resources after {failure}");
        }

        foreach (bool sameInterfacePointer in new[] { false, true })
        {
            var native = new FactoryCalls { SameInterfacePointer = sameInterfacePointer };
            var factory = VmcxStore.InitializeFactory(native);
            check(factory.Module == FactoryCalls.Module && factory.Statics == native.Statics &&
                factory.Create == FactoryCalls.Create && native.OnlySharedReferencesRemain &&
                native.FreeModuleCalls == 0 && native.InvalidReleaseCount == 0,
                $"VMCX success retains only the shared module and statics reference (same COM pointer: {sameInterfacePointer})");
        }

        var aliasedFailure = new FactoryCalls { SameInterfacePointer = true, FailAt = Failure.CreateEntryMissing };
        check(RejectsInitialization(aliasedFailure) && aliasedFailure.FailureObserved && aliasedFailure.OutstandingReferences == 0 &&
            aliasedFailure.InterfaceReleaseCalls == 2 && aliasedFailure.InvalidReleaseCount == 0,
            "VMCX failure releases both COM references when QueryInterface returns the factory pointer");

        var retry = new FactoryCalls { FailAt = Failure.QueryStaticsFailed };
        bool firstFailed = RejectsInitialization(retry) && retry.FailureObserved;
        int referencesAfterFailure = retry.OutstandingReferences;
        retry.FailAt = Failure.None;
        var retriedFactory = VmcxStore.InitializeFactory(retry);
        check(firstFailed && referencesAfterFailure == 0 && retry.LoadCalls == 2 && retry.FreeModuleCalls == 1 &&
            retriedFactory.Create == FactoryCalls.Create && retry.OnlySharedReferencesRemain && retry.InvalidReleaseCount == 0,
            "VMCX can retry initialization after failure without retaining the failed module or interfaces");
    }

    private static bool RejectsInitialization(FactoryCalls native)
    {
        try { VmcxStore.InitializeFactory(native); return false; }
        catch (Exception error) when (error is VmcxException or InvalidOperationException or EntryPointNotFoundException)
        { return true; }
    }

    private enum Failure
    {
        None,
        LoadReturnedNull,
        LoadThrew,
        ClassNameFailed,
        FactoryExportMissing,
        FactoryFailed,
        FactoryReturnedNull,
        QueryStaticsFailed,
        StaticsReturnedNull,
        CreateEntryMissing
    }

    private sealed class FactoryCalls : VmcxStore.IFactoryNative
    {
        internal static readonly nint Module = 11, ClassName = 12, Factory = 13, Create = 15;
        private const int FailedHresult = unchecked((int)0x80004005);
        private readonly Dictionary<nint, int> references = new();
        internal Failure FailAt { get; set; }
        internal bool SameInterfacePointer { get; init; }
        internal nint Statics => SameInterfacePointer ? Factory : 14;
        internal int LoadCalls { get; private set; }
        internal int FreeModuleCalls { get; private set; }
        internal int InterfaceReleaseCalls { get; private set; }
        internal int InvalidReleaseCount { get; private set; }
        internal bool FailureObserved { get; private set; }
        internal int OutstandingReferences => references.Values.Sum();
        internal bool OnlySharedReferencesRemain => Count(Module) == 1 && Count(Statics) == 1 &&
            Count(ClassName) == 0 && (SameInterfacePointer || Count(Factory) == 0) && OutstandingReferences == 2;

        private int Count(nint pointer) => references.GetValueOrDefault(pointer);
        private bool Hit(Failure stage)
        {
            if (FailAt != stage) return false;
            FailureObserved = true;
            return true;
        }
        private nint Acquire(nint pointer)
        {
            references[pointer] = Count(pointer) + 1;
            return pointer;
        }
        private void Drop(nint pointer)
        {
            if (Count(pointer) <= 0) { InvalidReleaseCount++; return; }
            references[pointer] = Count(pointer) - 1;
        }

        public nint LoadModule()
        {
            LoadCalls++;
            if (Hit(Failure.LoadThrew)) throw new InvalidOperationException("Expected load failure.");
            return Hit(Failure.LoadReturnedNull) ? 0 : Acquire(Module);
        }
        public int CreateClassName(out nint name)
        {
            name = Acquire(ClassName);
            return Hit(Failure.ClassNameFailed) ? FailedHresult : 0;
        }
        public int GetFactory(nint module, nint name, out nint factory)
        {
            if (module != Module || name != ClassName) throw new InvalidOperationException("Wrong factory arguments.");
            factory = 0;
            if (Hit(Failure.FactoryExportMissing)) throw new EntryPointNotFoundException("Expected missing export.");
            if (!Hit(Failure.FactoryReturnedNull)) factory = Acquire(Factory);
            return Hit(Failure.FactoryFailed) ? FailedHresult : 0;
        }
        public int QueryStatics(nint factory, out nint statics)
        {
            if (factory != Factory) throw new InvalidOperationException("Wrong factory interface.");
            statics = Hit(Failure.StaticsReturnedNull) ? 0 : Acquire(Statics);
            return Hit(Failure.QueryStaticsFailed) ? FailedHresult : 0;
        }
        public nint GetCreateEntry(nint statics)
        {
            if (statics != Statics) throw new InvalidOperationException("Wrong statics interface.");
            return Hit(Failure.CreateEntryMissing) ? 0 : Create;
        }
        public void Release(nint value)
        {
            InterfaceReleaseCalls++;
            if (value != Factory && value != Statics) InvalidReleaseCount++;
            Drop(value);
        }
        public void DeleteString(nint name)
        {
            if (name != ClassName) InvalidReleaseCount++;
            Drop(name);
        }
        public void FreeModule(nint module)
        {
            // Native entry points must remain loaded until their interfaces and
            // initialization temporaries have been released.
            if (module != Module || OutstandingReferences != Count(Module)) InvalidReleaseCount++;
            FreeModuleCalls++;
            Drop(module);
        }
    }
}
