using System.Globalization;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using R = ExHyperV.Properties.Resources;

internal static class CheckpointWorkflowChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        var stateOnly = VmCheckpointService.CreateInitialCheckpointNodes();
        check(stateOnly.Select(node => node.Id).SequenceEqual([CheckpointNode.OriginId, CheckpointNode.CurrentId]) &&
            stateOnly[0].NodeType == CheckpointNodeType.Origin && stateOnly[1].NodeType == CheckpointNodeType.Current &&
            stateOnly.All(node => node.IsCurrent) &&
            stateOnly[1].ParentId == CheckpointNode.OriginId && !VmCheckpointService.HasMountedHistoricalDisk(stateOnly),
            "Snapshot state-only initialization preserves logical nodes without requiring an image");

        var target = new CheckpointNode { Id = "target", NodeType = CheckpointNodeType.Snapshot };
        var origin = new CheckpointNode { NodeType = CheckpointNodeType.Origin };
        var current = new CheckpointNode { NodeType = CheckpointNodeType.Current };
        var mounted = new CheckpointNode { Id = "open", NodeType = CheckpointNodeType.Snapshot, IsHistoricalDiskMounted = true };
        check(!VmCheckpointService.CanOperateHistoricalNode(null) &&
            !VmCheckpointService.CanOperateHistoricalNode(origin) &&
            !VmCheckpointService.CanOperateHistoricalNode(current) &&
            VmCheckpointService.CanOperateHistoricalNode(target),
            "Snapshot historical operations keep the original node-type boundary");
        check(VmCheckpointService.CanCreateSnapshot(true, [target]) &&
            !VmCheckpointService.CanCreateSnapshot(false, [target]) &&
            !VmCheckpointService.CanCreateSnapshot(true, [target, mounted]),
            "Snapshot create availability shares snapshot-enabled and open-historical-disk rules");
        check(VmCheckpointService.CanMutateHistoricalNode(target, [target]) &&
            !VmCheckpointService.CanMutateHistoricalNode(origin, [target]) &&
            !VmCheckpointService.CanMutateHistoricalNode(target, [target, mounted]),
            "Deleting or merging any historical node is blocked by another mounted historical disk");

        // Leftward merge rules (Git-style into parent without affecting siblings or active anchor)
        var originRoot = new CheckpointNode { Id = CheckpointNode.OriginId, NodeType = CheckpointNodeType.Origin };
        var a1 = new CheckpointNode { Id = "a1", Name = "A1", NodeType = CheckpointNodeType.Snapshot, ParentId = CheckpointNode.OriginId };
        var a2 = new CheckpointNode { Id = "a2", Name = "A2", NodeType = CheckpointNodeType.Snapshot, ParentId = "a1" };
        var a3 = new CheckpointNode { Id = "a3", Name = "A3", NodeType = CheckpointNodeType.Snapshot, ParentId = "a2" };
        var curOnA3 = new CheckpointNode { Id = CheckpointNode.CurrentId, NodeType = CheckpointNodeType.Current, ParentId = "a3" };
        var linearNodes = new List<CheckpointNode> { originRoot, a1, a2, a3, curOnA3 };
        check(!VmCheckpointService.CanMergeHistoricalNode(a1, linearNodes) &&
            VmCheckpointService.CanMergeHistoricalNode(a2, linearNodes) &&
            VmCheckpointService.CanMergeHistoricalNode(a3, linearNodes),
            "Linear chain forbids merging into Origin and allows merging child snapshots into parent");

        var forkB1 = new CheckpointNode { Id = "b1", Name = "B1", NodeType = CheckpointNodeType.Snapshot, ParentId = CheckpointNode.OriginId };
        var forkB2 = new CheckpointNode { Id = "b2", Name = "B2", NodeType = CheckpointNodeType.Snapshot, ParentId = "b1" };
        var curOnA1 = new CheckpointNode { Id = CheckpointNode.CurrentId, NodeType = CheckpointNodeType.Current, ParentId = "a1" };
        var branchedNodes = new List<CheckpointNode> { originRoot, a1, a2, forkB1, forkB2, curOnA1 };
        check(!VmCheckpointService.CanMergeHistoricalNode(a2, branchedNodes) &&
            !VmCheckpointService.CanMergeHistoricalNode(forkB1, branchedNodes) &&
            VmCheckpointService.CanMergeHistoricalNode(forkB2, branchedNodes),
            "Branching forbids merging when parent carries Current or forked siblings, while independent branch child stays mergeable");

        check(!VmCheckpointService.CanMergeHistoricalNode(a2, [.. linearNodes, mounted]),
            "Mounted historical disk blocks historical node merge");

        var mergeSteps = new List<string>();
        var mergeOk = await VmCheckpointService.MergeWithParentAsync(
            "A2", "A1",
            name => { mergeSteps.Add("rename:" + name); return Task.FromResult(ApiResponse.Ok()); },
            () => { mergeSteps.Add("destroy"); return Task.FromResult(ApiResponse.Ok()); });
        check(mergeOk.Success && mergeSteps.SequenceEqual(["rename:A1", "destroy"]),
            "MergeWithParentAsync renames target to parent name before destroying parent");

        mergeSteps.Clear();
        var mergeRollback = await VmCheckpointService.MergeWithParentAsync(
            "A2", "A1",
            name => { mergeSteps.Add("rename:" + name); return Task.FromResult(ApiResponse.Ok()); },
            () => { mergeSteps.Add("destroy"); return Task.FromResult(ApiResponse.Fail("wmi-failed", kind: ApiErrorKind.OperationFailed)); });
        check(!mergeRollback.Success && mergeRollback.ErrorKind == ApiErrorKind.OperationFailed &&
            mergeSteps.SequenceEqual(["rename:A1", "destroy", "rename:A2"]),
            "Settled parent destroy failure rolls back renamed target name");

        mergeSteps.Clear();
        var mergeUnknown = await VmCheckpointService.MergeWithParentAsync(
            "A2", "A1",
            name => { mergeSteps.Add("rename:" + name); return Task.FromResult(ApiResponse.Ok()); },
            () => { mergeSteps.Add("destroy"); return Task.FromResult(ApiResponse.Fail("unknown", kind: ApiErrorKind.OutcomeUnknown)); });
        check(!mergeUnknown.Success && mergeUnknown.ErrorKind == ApiErrorKind.OutcomeUnknown &&
            mergeSteps.SequenceEqual(["rename:A1", "destroy"]),
            "Uncertain parent destroy outcome preserves OutcomeUnknown without rolling back target name");

        mergeSteps.Clear();
        var sameNameOk = await VmCheckpointService.MergeWithParentAsync(
            "Same", "Same",
            name => { mergeSteps.Add("rename:" + name); return Task.FromResult(ApiResponse.Ok()); },
            () => { mergeSteps.Add("destroy"); return Task.FromResult(ApiResponse.Ok()); });
        check(sameNameOk.Success && mergeSteps.SequenceEqual(["destroy"]),
            "MergeWithParentAsync skips redundant rename when child and parent share the same name");

        var originalCulture = R.Culture;
        try
        {
            foreach (string culture in new[] { "en-US", "zh-CN" })
            {
                R.Culture = CultureInfo.GetCultureInfo(culture);
                void Verify(bool result, string name) => check(result, "Checkpoint workflow: " + name + " (" + culture + ")");
                int mutations = 0, reads = 0;
                Task<(bool Success, string Message)> Mutate()
                {
                    mutations++;
                    return Task.FromResult((true, "mutation-result"));
                }
                var denied = await VmCheckpointService.RunCheckpointMutationAsync(
                    () => { reads++; return Task.FromResult(new List<CheckpointNode> { target, mounted }); }, Mutate);
                Verify(!denied.Success && denied.Message == R.VmCheckpointService_HintDismountHistoricalDiskFirst &&
                    reads == 1 && mutations == 0, "a detected node blocks mutation before its execution");
                var allowed = await VmCheckpointService.RunCheckpointMutationAsync(
                    () => { reads++; return Task.FromResult(new List<CheckpointNode> { target }); }, Mutate);
                Verify(allowed.Success && allowed.Message == "mutation-result" && reads == 2 && mutations == 1,
                    "a clear node list executes once and preserves the operation result");

                var first = new CheckpointNode { Id = "first", IsHistoricalDiskMounted = true };
                var second = new CheckpointNode { Id = "second", IsHistoricalDiskMounted = true };
                var third = new CheckpointNode { Id = "third", IsHistoricalDiskMounted = true };
                var steps = new List<string>();
                var failure = await VmCheckpointService.DismountHistoricalDisksAndApplyOperationAsync(
                    target, [target, first, second, third],
                    node =>
                    {
                        steps.Add("dismount:" + node.Id);
                        if (node == second) return Task.FromResult((false, "dismount-failure"));
                        node.IsHistoricalDiskMounted = false;
                        return Task.FromResult((true, "dismounted"));
                    },
                    () => { steps.Add("apply"); return Task.FromResult((true, "applied")); });
                Verify(!failure.Success && failure.Message == "dismount-failure" &&
                    steps.SequenceEqual(["dismount:first", "dismount:second"]) &&
                    !first.IsHistoricalDiskMounted && second.IsHistoricalDiskMounted && third.IsHistoricalDiskMounted,
                    "failed dismount stops later dismounts and apply while preserving prior dismount state");

                steps.Clear();
                var success = await VmCheckpointService.DismountHistoricalDisksAndApplyOperationAsync(
                    target, [target, first, second, third],
                    node =>
                    {
                        steps.Add("dismount:" + node.Id);
                        node.IsHistoricalDiskMounted = false;
                        return Task.FromResult((true, "dismounted"));
                    },
                    () => { steps.Add("apply"); return Task.FromResult((true, "applied")); });
                Verify(success.Success && success.Message == "applied" &&
                    steps.SequenceEqual(["dismount:second", "dismount:third", "apply"]),
                    "remaining mounted historical disks dismount in node order before applying the snapshot");

                steps.Clear();
                var applyFailure = await VmCheckpointService.DismountHistoricalDisksAndApplyOperationAsync(
                    target, [target, first],
                    node => { steps.Add("unexpected-dismount"); return Task.FromResult((true, "dismounted")); },
                    () => { steps.Add("apply"); return Task.FromResult((false, "apply-failure")); });
                Verify(!applyFailure.Success && applyFailure.Message == "apply-failure" && steps.SequenceEqual(["apply"]),
                    "without open nodes apply runs directly and keeps its own failure");

                steps.Clear();
                var invalid = await VmCheckpointService.DismountHistoricalDisksAndApplyOperationAsync(
                    origin, [mounted],
                    node => { steps.Add("unexpected-dismount"); return Task.FromResult((true, "dismounted")); },
                    () => { steps.Add("unexpected-apply"); return Task.FromResult((true, "applied")); });
                Verify(!invalid.Success && invalid.Message == R.VmCheckpointService_ErrOnlyHistoricalNode && steps.Count == 0,
                    "logical nodes are rejected without closing resources or applying snapshots");
            }
        }
        finally { R.Culture = originalCulture; }
    }
}
