using System.Collections;

namespace System.Management;

internal enum TextFormat { Mof, CimDtd20, WmiDtd20 }
internal unsafe class ManagementBaseObject : IDisposable
{
    private WbemHandle? instance;
    private bool disposed;
    protected readonly object Sync = new();
    internal ManagementBaseObject(WbemHandle? instance) => this.instance = instance;
    protected void CheckLifetime() => ObjectDisposedException.ThrowIf(disposed, this);
    internal WbemHandle Native
    {
        get
        {
            lock (Sync)
            {
                CheckLifetime();
                return instance ??= Resolve();
            }
        }
    }
    protected virtual WbemHandle Resolve() => throw new InvalidOperationException("No WMI object was specified.");
    protected void Replace(WbemHandle replacement)
    {
        lock (Sync)
        {
            if (disposed) { replacement.Dispose(); throw new ObjectDisposedException(GetType().Name); }
            instance?.Dispose();
            instance = replacement;
        }
    }
    public void Dispose()
    {
        lock (Sync) { disposed = true; instance?.Dispose(); instance = null; }
    }
    internal object? this[string name]
    {
        get => Read(name, true).Value;
        set
        {
            Native.Use(p =>
            {
                int cim = ReadOn(p, name, false).Type;
                var data = WbemVariant.From((CimType)(cim & ~0x2000), (cim & 0x2000) != 0, value);
                try
                {
                    fixed (char* key = name)
                        WbemNative.Check(((delegate* unmanaged[Stdcall]<nint, char*, int, WbemVariant*, int, int>)WbemNative.Slot(p, 5))(p, key, 0, &data, 0));
                }
                finally { data.Clear(); }
                return true;
            });
            PropertyChanged();
        }
    }
    protected virtual void PropertyChanged() { }
    internal ManagementPath ClassPath
    {
        get
        {
            string? server = this["__SERVER"] as string;
            string? ns = this["__NAMESPACE"] as string;
            string name = (string?)this["__CLASS"] ?? "";
            return new ManagementPath(string.IsNullOrEmpty(ns) ? name : $@"\\{server ?? "."}\{ns}:{name}");
        }
    }
    internal PropertyDataCollection Properties => new(this);
    internal ManagementBaseObject Clone() => Native.Use(p =>
    {
        nint copy = 0;
        WbemNative.Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)WbemNative.Slot(p, 12))(p, &copy));
        return new ManagementBaseObject(new WbemHandle(copy));
    });
    internal (int Type, object? Value) Read(string name, bool value) => Native.Use(p => ReadOn(p, name, value));
    private static (int Type, object? Value) ReadOn(nint p, string name, bool read)
    {
        int type = 0;
        WbemVariant data = default;
        try
        {
            fixed (char* key = name)
                WbemNative.Check(((delegate* unmanaged[Stdcall]<nint, char*, int, WbemVariant*, int*, int*, int>)WbemNative.Slot(p, 4))(p, key, 0, read ? &data : null, &type, null));
            return (type, read ? data.Read((CimType)(type & ~0x2000)) : null);
        }
        finally { data.Clear(); }
    }
    internal string[] PropertyNames() => Native.Use(p =>
    {
        nint names = 0;
        WbemNative.Check(((delegate* unmanaged[Stdcall]<nint, char*, int, nint, nint*, int>)WbemNative.Slot(p, 7))(p, null, 64, 0, &names));
        try { return (string[])WbemVariant.ReadArray(names, CimType.String); }
        finally { WbemNative.Check(WbemVariant.SafeArrayDestroy(names)); }
    });

    internal string GetText(TextFormat format) => Native.Use(p =>
    {
        nint text = 0;
        if (format == TextFormat.Mof)
            WbemNative.Check(((delegate* unmanaged[Stdcall]<nint, int, nint*, int>)WbemNative.Slot(p, 13))(p, 0, &text));
        else
        {
            using var writer = WbemNative.Create(new("8d1c559d-84f0-4bb3-a7d5-56a7435a9ba6"), new("bfbf883a-cad7-11d3-a11b-00105a1f515a"));
            using var context = WbemNative.Create(new("674b6698-ee92-11d0-ad71-00c04fd8fdff"), new("44aca674-e8fc-11d0-a07c-00c04fb68820"));
            return context.Use(c =>
            {
                foreach (string option in new[] { "IncludeQualifiers", "IncludeClassOrigin" })
                {
                    var enabled = WbemVariant.From(CimType.Boolean, false, true);
                    fixed (char* key = option)
                        WbemNative.Check(((delegate* unmanaged[Stdcall]<nint, char*, int, WbemVariant*, int>)WbemNative.Slot(c, 8))(c, key, 0, &enabled));
                }
                return writer.Use(w =>
                {
                    nint xml = 0;
                    WbemNative.Check(((delegate* unmanaged[Stdcall]<nint, int, nint, uint, nint, nint*, int>)WbemNative.Slot(w, 3))(w, 0, p, (uint)format, c, &xml));
                    return WbemNative.TakeBstr(xml);
                });
            });
        }
        return WbemNative.TakeBstr(text);
    });

    internal static WbemHandle Spawn(WbemHandle schema) => schema.Use(p =>
    {
        nint obj = 0;
        WbemNative.Check(((delegate* unmanaged[Stdcall]<nint, int, nint*, int>)WbemNative.Slot(p, 15))(p, 0, &obj));
        return new WbemHandle(obj);
    });
    internal int ParameterId(string name) => Native.Use(p =>
    {
        nint q = 0;
        fixed (char* key = name)
            WbemNative.Check(((delegate* unmanaged[Stdcall]<nint, char*, nint*, int>)WbemNative.Slot(p, 11))(p, key, &q));
        using var qualifiers = new WbemHandle(q);
        return qualifiers.Use(set =>
        {
            WbemVariant value = default;
            try
            {
                fixed (char* key = "ID")
                    WbemNative.Check(((delegate* unmanaged[Stdcall]<nint, char*, int, WbemVariant*, int*, int>)WbemNative.Slot(set, 3))(set, key, 0, &value, null));
                return checked((int)value.Data);
            }
            finally { value.Clear(); }
        });
    });
}

internal sealed unsafe class PropertyData(ManagementBaseObject owner, string name)
{
    internal string Name => name;
    internal string Origin => owner.Native.Use(p =>
    {
        nint origin = 0;
        int hr;
        fixed (char* key = name)
            hr = ((delegate* unmanaged[Stdcall]<nint, char*, nint*, int>)WbemNative.Slot(p, 17))(p, key, &origin);
        if (hr == (int)ManagementStatus.InvalidObject) return "";
        WbemNative.Check(hr);
        return WbemNative.TakeBstr(origin);
    });
    internal CimType Type => (CimType)(owner.Read(name, false).Type & ~0x2000);
    internal bool IsArray => (owner.Read(name, false).Type & 0x2000) != 0;
    internal object? Value { get => owner[name]; set => owner[name] = value; }
}
internal sealed class PropertyDataCollection(ManagementBaseObject owner) : IEnumerable<PropertyData>
{
    internal PropertyData this[string name] => new(owner, name);
    public IEnumerator<PropertyData> GetEnumerator() =>
        owner.PropertyNames().Select(name => new PropertyData(owner, name)).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

internal unsafe class ManagementObject : ManagementBaseObject
{
    private ManagementPath? path;
    internal ManagementObject(string path) : this(null, new ManagementPath(path), null) { }
    internal ManagementObject(ManagementScope? scope, ManagementPath path, ObjectGetOptions? options) : base(null)
    {
        Scope = scope ?? new ManagementScope(path.NamespacePath.Length == 0 ? @"\\.\root\cimv2" : $@"\\{path.Server}\{path.NamespacePath}");
        this.path = path;
    }
    internal ManagementObject(ManagementScope scope, WbemHandle owned) : base(owned) => Scope = scope;
    internal ManagementScope Scope { get; }
    internal ManagementPath Path
    {
        get
        {
            lock (Sync)
            {
                CheckLifetime();
                return path ??= new((string?)this["__PATH"] ?? (string?)this["__RELPATH"] ?? "");
            }
        }
    }
    protected override void PropertyChanged()
    {
        // A key may have changed. Let WBEM format the updated identity before
        // the next associative query, refresh or method call.
        if (this is not ManagementClass) { lock (Sync) path = null; }
    }
    protected override WbemHandle Resolve() => Scope.GetObject(path!.RelativePath);
    internal void Get() => Replace(Scope.GetObject(Path.RelativePath));

    internal static (ManagementBaseObject? Input, ManagementBaseObject? Output) GetSignatures(WbemHandle schema, string methodName)
        => schema.Use(p =>
        {
            nint input = 0, output = 0;
            fixed (char* name = methodName)
                WbemNative.Check(((delegate* unmanaged[Stdcall]<nint, char*, int, nint*, nint*, int>)WbemNative.Slot(p, 19))(p, name, 0, &input, &output));
            return (input == 0 ? null : new ManagementBaseObject(new WbemHandle(input)),
                output == 0 ? null : new ManagementBaseObject(new WbemHandle(output)));
        });

    private (ManagementBaseObject? Input, ManagementBaseObject? Output) Method(string methodName)
    {
        if (this is ManagementClass) return GetSignatures(Native, methodName);
        using var schema = Scope.GetObject(Path.ClassName.Length != 0 ? Path.ClassName : ClassPath.ClassName);
        return GetSignatures(schema, methodName);
    }
    internal ManagementBaseObject? GetMethodParameters(string methodName)
    {
        var signatures = Method(methodName);
        using var input = signatures.Input;
        using var output = signatures.Output;
        return input == null ? null : new ManagementBaseObject(Spawn(input.Native));
    }

    internal ManagementBaseObject? InvokeMethod(string methodName, ManagementBaseObject? inParameters, InvokeMethodOptions? options)
    {
        var output = Scope.Invoke(Path.RelativePath, methodName, inParameters?.Native);
        return output == null ? null : new ManagementBaseObject(output);
    }

    internal object? InvokeMethod(string methodName, object?[]? args)
    {
        var signatures = Method(methodName);
        using var inputClass = signatures.Input;
        using var outputClass = signatures.Output;
        using var input = inputClass == null ? null : new ManagementBaseObject(Spawn(inputClass.Native));
        // ID qualifiers are defined on the signature class. Providers are not
        // required to propagate them to input/output instances.
        if (input != null && args is { Length: > 0 })
            foreach (var property in inputClass!.Properties)
            {
                int id = inputClass.ParameterId(property.Name);
                if ((uint)id < (uint)args.Length) input[property.Name] = args[id];
            }
        using var output = InvokeMethod(methodName, input, null);
        if (output == null || outputClass == null) return null;
        object? returned = null;
        foreach (var property in outputClass.Properties)
        {
            if (property.Name.Equals("ReturnValue", StringComparison.OrdinalIgnoreCase)) returned = output[property.Name];
            else if (args is { Length: > 0 })
            {
                int id = outputClass.ParameterId(property.Name);
                if ((uint)id < (uint)args.Length) args[id] = output[property.Name];
            }
        }
        return returned;
    }

    internal ManagementObjectCollection GetRelated(string? relatedClass, string? relationshipClass = null,
        string? relationshipQualifier = null, string? relatedQualifier = null, string? relatedRole = null,
        string? thisRole = null, bool classDefinitionsOnly = false, EnumerationOptions? options = null)
    {
        var filters = new List<string>();
        void Filter(string keyword, string? value) { if (!string.IsNullOrEmpty(value)) filters.Add(keyword + " = " + value); }
        Filter("ResultClass", relatedClass);
        Filter("AssocClass", relationshipClass);
        Filter("RequiredAssocQualifier", relationshipQualifier);
        Filter("RequiredQualifier", relatedQualifier);
        Filter("ResultRole", relatedRole);
        Filter("Role", thisRole);
        if (classDefinitionsOnly) filters.Add("ClassDefsOnly");
        string query = $"ASSOCIATORS OF {{{Path.RelativePath}}}";
        if (filters.Count != 0) query += " WHERE " + string.Join(" ", filters);
        using var finder = new ManagementObjectSearcher(Scope, new ObjectQuery(query), options);
        return finder.Get();
    }
}

internal sealed class ManagementClass(ManagementScope scope, ManagementPath path, ObjectGetOptions? options) : ManagementObject(scope, path, options)
{
    internal ManagementObject CreateInstance() => new(Scope, Spawn(Native));
}
