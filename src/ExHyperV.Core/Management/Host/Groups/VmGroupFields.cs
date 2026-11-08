using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.Services;

/// <summary>虚拟机直接所属分组的查询成员。</summary>
public static class VmGroupFields
{
    /// <summary>分组身份：虚拟机直接所属分组的稳定标识。</summary>
    /// <fielddoc>
    /// <meaning>虚拟机直接所属分组的唯一标识。</meaning>
    /// <values kind="format">GUID 文本由 32 个十六进制字符和 4 个连字符组成，按 8-4-4-4-12 分组。</values>
    /// <source>VmGroupService.ReadAsync 的直接成员关系，读取或派生 Id。</source>
    /// <references><see cref="VmGroupService"/>。</references>
    /// <modification kind="supported" input="moves、targetId" submission="成员移动">分组归属变化不会改变分组自身的身份；空目标表示不属于任何分组。<see cref="VmGroupService.MoveAsync"/></modification>
    /// </fielddoc>
    public static readonly VmQueryField<Guid> Id = new("Group.Id", "分组身份", "所属分组", VmQueryCollection.Groups, "Groups", "Id");

    /// <summary>分组名称：虚拟机直接所属分组的显示名称。</summary>
    /// <fielddoc>
    /// <meaning>虚拟机直接所属分组的显示名称。</meaning>
    /// <source>VmGroupService.ReadAsync 的直接成员关系，读取或派生 Name。</source>
    /// <references><see cref="VmGroupService"/>。</references>
    /// <modification kind="operation" submission="操作流程">分组名称由该组全部成员共用；重命名后所有成员看到相同的新名称。<see cref="VmGroupService.RenameAsync"/><see cref="VmGroupService.MoveAsync"/></modification>
    /// </fielddoc>
    public static readonly VmQueryField<string> Name = new("Group.Name", "分组名称", "所属分组", VmQueryCollection.Groups, "Groups", "Name");

    internal static readonly VmQueryField[] Items = [Id, Name];
}
