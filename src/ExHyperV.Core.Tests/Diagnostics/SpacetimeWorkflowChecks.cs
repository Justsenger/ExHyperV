using System.Globalization;
using ExHyperV.Models;
using ExHyperV.Services;
using R = ExHyperV.Properties.Resources;

internal static class SpacetimeWorkflowChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        var stateOnly = VmSpacetimeService.CreateInitialSpacetimeNodes(null);
        check(stateOnly.Select(node => node.Id).SequenceEqual([SpacetimeNode.GenesisId, SpacetimeNode.CurrentId]) &&
            stateOnly[0].NodeType == SpacetimeNodeType.Genesis && stateOnly[1].NodeType == SpacetimeNodeType.Current &&
            stateOnly.All(node => node.IsCurrent && node.Thumbnail == null) &&
            stateOnly[1].ParentId == SpacetimeNode.GenesisId && !VmSpacetimeService.HasOpenWormhole(stateOnly),
            "Spacetime state-only initialization preserves logical nodes without requiring an image");
        var thumbnail = VmScreenshotImage.FromRgb565(new byte[8], 2, 2);
        var displayNodes = VmSpacetimeService.CreateInitialSpacetimeNodes(thumbnail);
        check(displayNodes.All(node => ReferenceEquals(node.Thumbnail, thumbnail)) &&
            !ReferenceEquals(displayNodes[0], stateOnly[0]),
            "Spacetime display initialization retains the original shared thumbnail on fresh nodes");

        var target = new SpacetimeNode { Id = "target", NodeType = SpacetimeNodeType.Snapshot };
        var origin = new SpacetimeNode { NodeType = SpacetimeNodeType.Genesis };
        var current = new SpacetimeNode { NodeType = SpacetimeNodeType.Current };
        var wormhole = new SpacetimeNode { Id = "open", NodeType = SpacetimeNodeType.Snapshot, IsWormhole = true };
        check(!VmSpacetimeService.CanOperateHistoricalNode(null) &&
            !VmSpacetimeService.CanOperateHistoricalNode(origin) &&
            !VmSpacetimeService.CanOperateHistoricalNode(current) &&
            VmSpacetimeService.CanOperateHistoricalNode(target),
            "Spacetime historical operations keep the original node-type boundary");
        check(VmSpacetimeService.CanCreateMoment(true, [target]) &&
            !VmSpacetimeService.CanCreateMoment(false, [target]) &&
            !VmSpacetimeService.CanCreateMoment(true, [target, wormhole]),
            "Spacetime create availability shares checkpoint-enabled and open-wormhole rules");
        check(VmSpacetimeService.CanMutateHistoricalNode(target, [target]) &&
            !VmSpacetimeService.CanMutateHistoricalNode(origin, [target]) &&
            !VmSpacetimeService.CanMutateHistoricalNode(target, [target, wormhole]),
            "Deleting or converging any historical node is blocked by another open wormhole");

        var originalCulture = R.Culture;
        try
        {
            foreach (string culture in new[] { "en-US", "zh-CN" })
            {
                R.Culture = CultureInfo.GetCultureInfo(culture);
                void Verify(bool result, string name) => check(result, "Spacetime workflow: " + name + " (" + culture + ")");
                int mutations = 0, reads = 0;
                Task<(bool Success, string Message)> Mutate()
                {
                    mutations++;
                    return Task.FromResult((true, "mutation-result"));
                }
                var denied = await VmSpacetimeService.RunCheckpointMutationAsync(
                    () => { reads++; return Task.FromResult(new List<SpacetimeNode> { target, wormhole }); }, Mutate);
                Verify(!denied.Success && denied.Message == R.VmSpacetimeSettings_HintCloseWormholeFirst &&
                    reads == 1 && mutations == 0, "a detected node blocks mutation before its execution");
                var allowed = await VmSpacetimeService.RunCheckpointMutationAsync(
                    () => { reads++; return Task.FromResult(new List<SpacetimeNode> { target }); }, Mutate);
                Verify(allowed.Success && allowed.Message == "mutation-result" && reads == 2 && mutations == 1,
                    "a clear node list executes once and preserves the operation result");

                var first = new SpacetimeNode { Id = "first", IsWormhole = true };
                var second = new SpacetimeNode { Id = "second", IsWormhole = true };
                var third = new SpacetimeNode { Id = "third", IsWormhole = true };
                var steps = new List<string>();
                var failure = await VmSpacetimeService.CloseWormholesAndTeleportOperationAsync(
                    target, [target, first, second, third],
                    node =>
                    {
                        steps.Add("close:" + node.Id);
                        if (node == second) return Task.FromResult((false, "close-failure"));
                        node.IsWormhole = false;
                        return Task.FromResult((true, "closed"));
                    },
                    () => { steps.Add("teleport"); return Task.FromResult((true, "applied")); });
                Verify(!failure.Success && failure.Message == "close-failure" &&
                    steps.SequenceEqual(["close:first", "close:second"]) &&
                    !first.IsWormhole && second.IsWormhole && third.IsWormhole,
                    "failed close stops later closes and teleport while preserving prior close state");

                steps.Clear();
                var success = await VmSpacetimeService.CloseWormholesAndTeleportOperationAsync(
                    target, [target, first, second, third],
                    node =>
                    {
                        steps.Add("close:" + node.Id);
                        node.IsWormhole = false;
                        return Task.FromResult((true, "closed"));
                    },
                    () => { steps.Add("teleport"); return Task.FromResult((true, "applied")); });
                Verify(success.Success && success.Message == "applied" &&
                    steps.SequenceEqual(["close:second", "close:third", "teleport"]),
                    "remaining wormholes close in node order before applying the snapshot");

                steps.Clear();
                var applyFailure = await VmSpacetimeService.CloseWormholesAndTeleportOperationAsync(
                    target, [target, first],
                    node => { steps.Add("unexpected-close"); return Task.FromResult((true, "closed")); },
                    () => { steps.Add("teleport"); return Task.FromResult((false, "apply-failure")); });
                Verify(!applyFailure.Success && applyFailure.Message == "apply-failure" && steps.SequenceEqual(["teleport"]),
                    "without open nodes teleport runs directly and keeps its own failure");

                steps.Clear();
                var invalid = await VmSpacetimeService.CloseWormholesAndTeleportOperationAsync(
                    origin, [wormhole],
                    node => { steps.Add("unexpected-close"); return Task.FromResult((true, "closed")); },
                    () => { steps.Add("unexpected-teleport"); return Task.FromResult((true, "applied")); });
                Verify(!invalid.Success && invalid.Message == R.VmSpacetimeService_ErrOnlyHistoricalNode && steps.Count == 0,
                    "logical nodes are rejected without closing resources or applying snapshots");
            }
        }
        finally { R.Culture = originalCulture; }
    }
}
