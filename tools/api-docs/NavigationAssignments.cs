internal static partial class ApiDocument
{
    private static string[] VmArea(string operation, string category) => ["管理层面", "虚拟机", operation, category];
    private static string[] HostArea(string operation, string category) => ["管理层面", "宿主", operation, category];
    private static string[][] DeclaredNavigationPaths(TypeEntry type) => [PresentationFor(type).Path];
    private static string[][] MemberNavigationPaths(TypeEntry type, MemberEntry member) => PresentationFor(member).Paths;
}
