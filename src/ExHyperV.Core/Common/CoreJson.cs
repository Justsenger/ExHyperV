using System.Text.Json;
using System.Text.Json.Serialization;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;

namespace ExHyperV;

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(AffinityPreference))]
[JsonSerializable(typeof(LegacyAffinityPreference))]
[JsonSerializable(typeof(CpuGroupFingerprint))]
[JsonSerializable(typeof(CpuGroupSnapshot))]
[JsonSerializable(typeof(SetCpuGroupRequest))]
[JsonSerializable(typeof(List<int>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(VmConsoleFeatures))]
[JsonSerializable(typeof(Dictionary<string, object?>), TypeInfoPropertyName = "FieldMap")]
[JsonSerializable(typeof(List<Dictionary<string, object?>>))]
[JsonSerializable(typeof(NetworkPropertyFingerprint[]))]
[JsonSerializable(typeof(object[]))]
[JsonSerializable(typeof(bool[]))]
[JsonSerializable(typeof(char[]))]
[JsonSerializable(typeof(sbyte[]))]
[JsonSerializable(typeof(byte))]
[JsonSerializable(typeof(byte[]))]
[JsonSerializable(typeof(short[]))]
[JsonSerializable(typeof(ushort[]))]
[JsonSerializable(typeof(int[]))]
[JsonSerializable(typeof(uint[]))]
[JsonSerializable(typeof(long[]))]
[JsonSerializable(typeof(ulong[]))]
[JsonSerializable(typeof(float[]))]
[JsonSerializable(typeof(double[]))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(UpgradeSource))]
[JsonSerializable(typeof(UpgradeExtras))]
internal sealed partial class CoreJson : JsonSerializerContext;

// Incoming HCS responses historically accept mixed case.
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(List<HcsCpuGroupDetail>))]
[JsonSerializable(typeof(List<HcsLogicalProcessor>))]
internal sealed partial class ExternalJson : JsonSerializerContext;

// Published journal versions use string enum values while other Core formats use numbers.
[JsonSourceGenerationOptions(UseStringEnumConverter = true, GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(UpgradeJournal))]
internal sealed partial class UpgradeJournalJson : JsonSerializerContext;
